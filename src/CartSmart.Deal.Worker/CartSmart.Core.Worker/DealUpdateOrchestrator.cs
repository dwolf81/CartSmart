using CartSmart.API.Models;
using Microsoft.Extensions.Logging;

namespace CartSmart.Core.Worker;

public class DealUpdateOrchestrator : IDealUpdateOrchestrator
{
    private readonly IDealRepository _repo;
    private readonly IEnumerable<IStoreClient> _storeClients;
    private readonly ILogger<DealUpdateOrchestrator> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _minRefreshInterval;
    private readonly SemaphoreSlim _semaphore;
    private readonly IHtmlScraper _scraper;
    private HashSet<string>? _stopWords;

    private readonly Dictionary<StoreType, IStoreClient> _clientMap;

    public DealUpdateOrchestrator(
        IDealRepository repo,
        IEnumerable<IStoreClient> storeClients,
        ILogger<DealUpdateOrchestrator> logger,
        IHtmlScraper scraper,
        TimeProvider? timeProvider = null,
        int maxParallel = 5,
        TimeSpan? minRefreshInterval = null)
    {
        _repo = repo;
        _storeClients = storeClients;
        _logger = logger;
        _scraper = scraper;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _minRefreshInterval = minRefreshInterval ?? TimeSpan.FromMinutes(5);
        _semaphore = new SemaphoreSlim(maxParallel);
        _clientMap = storeClients.ToDictionary(c => c.StoreType, c => c);
    }
    public async Task<DealRefreshResult> RefreshDealsAsync(int batchSize, CancellationToken ct)
    {
        var repoImpl = _repo as SupabaseDealRepository;
        if (repoImpl == null)
        {
            _logger.LogError("Repository implementation missing for product-centric refresh");
            return new DealRefreshResult(0,0,0,0,1);
        }
        var products = await repoImpl.GetDueDealProductsAsync(batchSize, ct);
        int updated=0, expired=0, sold=0, errors=0;
        var tasks = products.Select(p => ProcessDealProductAsync(p, ct));
        var results = await Task.WhenAll(tasks);
        foreach (var r in results)
        {
            switch (r)
            {
                case DealProcessOutcome.Updated: updated++; break;
                case DealProcessOutcome.Expired: expired++; break;
                case DealProcessOutcome.Sold: sold++; break;
                case DealProcessOutcome.Error: errors++; break;
            }
        }
        return new DealRefreshResult(products.Count, updated, expired, sold, errors);
    }
    // Separate sweep for expired deals: update main deal and all product deals to expired
    public async Task<int> SweepExpiredDealsAsync(CancellationToken ct)
    {
        var repoImpl = _repo as SupabaseDealRepository;
        if (repoImpl == null)
        {
            _logger.LogError("Repository implementation missing for expiry sweep");
            return 0;
        }
        var expiredDeals = await _repo.GetExpiredActiveDealsAsync(ct);
        int count = 0;
        foreach (var d in expiredDeals)
        {
            await repoImpl.ExpireDealAndProductsAsync(d, ct);
            count++;
        }
        _logger.LogInformation("Expired {Count} deals in sweep", count);
        return count;
    }

    // Ingest new listings for a specific store; selects top N lowest prices per product
    public async Task<int> IngestNewListingsAsync(StoreType storeType, int topPerProduct, IEnumerable<NewListingQuery> queries, CancellationToken ct)
    {
        await EnsureStopWordsAsync(ct);
        var repoImpl = _repo as SupabaseDealRepository;
        if (repoImpl == null) return 0;
        if (!_clientMap.TryGetValue(storeType, out var client) || client == null || !client.SupportsApi)
        {
            _logger.LogWarning("Store client unavailable or API unsupported: {Store}", storeType);
            return 0;
        }

        int created = 0;
        foreach (var q in queries)
        {
            // Load product context for matching (MSRP, Brand)
            var product = await repoImpl.GetProductByIdAsync(q.ProductId, ct);
            var msrp = product?.MSRP;
            var brandId = product?.BrandId;
            var productName = product?.Name?.ToLowerInvariant() ?? string.Empty;
            var productTokens = NormalizeIdentityTokens(product?.Name ?? string.Empty);

            var listings = await client.SearchNewListingsAsync(q.Query, product?.PreferredConditionCategoryId, ct);
            // Apply matching hierarchy and price sanity
            var candidates = new List<NewListing>();
            foreach (var l in listings)
            {
                // Respect product's preferred condition category if configured
                if (product?.PreferredConditionCategoryId.HasValue == true)
                {
                    if (l.ConditionCategoryId != product.PreferredConditionCategoryId.Value)
                        continue;
                }
                // 1) GTIN authoritative
                if (!string.IsNullOrWhiteSpace(l.GTIN))
                {
                    candidates.Add(l);
                    continue;
                }
                // 2) Brand + MPN
                if (!string.IsNullOrWhiteSpace(l.Brand) && !string.IsNullOrWhiteSpace(l.MPN) && brandId != null)
                {
                    var inferred = await InferBrandIdAsync(l.Brand!, ct);
                    if (inferred != null && inferred == brandId)
                    {
                        candidates.Add(l);
                        continue;
                    }
                }
                // 3) Title + attributes + price sanity
                var title = (l.Title ?? string.Empty).ToLowerInvariant();
                var titleTokens = NormalizeIdentityTokens(l.Title ?? string.Empty);
                var coverage = Coverage(productTokens, titleTokens);
                bool titleMatch = coverage >= 0.6;
                bool priceOk = false;
                if (msrp.HasValue && l.Price.HasValue)
                {
                    // Accept listings priced within 30%..150% of MSRP to avoid low-cost accessories and overpriced bundles
                    var p = l.Price!.Value;
                    priceOk = p >= (decimal)msrp.Value * 0.3m && p <= (decimal)msrp.Value * 1.5m;
                }
                if (titleMatch && priceOk)
                {
                    candidates.Add(l);
                }
            }

            // From candidates, pick lowest priced listings.
            // For eBay: select top N per resolved product variant.
            // If the product has variants and we can't confidently resolve a variant from the listing, skip it.

            var variantClient = client as IVariantResolvingStoreClient;
            var hasVariants = variantClient != null && await variantClient.HasActiveVariantsAsync(q.ProductId, ct);

            var resolved = new List<(NewListing Listing, long? VariantId)>();
            foreach (var l in candidates.Where(x => x.Price.HasValue))
            {
                long? variantId = null;
                if (variantClient != null)
                    variantId = await variantClient.TryResolveProductVariantIdAsync(q.ProductId, l, ct);

                if (hasVariants && !variantId.HasValue)
                    continue;

                resolved.Add((l, variantId));
            }

            List<(NewListing Listing, long? VariantId)> selected;
            if (hasVariants)
            {
                selected = resolved
                    .Where(x => x.VariantId.HasValue)
                    .GroupBy(x => x.VariantId!.Value)
                    .SelectMany(g => g.OrderBy(x => x.Listing.Price!.Value).Take(topPerProduct))
                    .ToList();
            }
            else
            {
                selected = resolved
                    .OrderBy(x => x.Listing.Price!.Value)
                    .Take(topPerProduct)
                    .ToList();
            }

            foreach (var (listing, variantId) in selected)
            {
                if (listing.ItemId != null && await repoImpl.ExistsDealByStoreItemAsync(listing.ItemId, ct))
                    continue;
                var deal = new Deal
                {
                    CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                    DealStatusId = 2,
                    DealTypeId = 1,                    
                    AdditionalDetails = listing.Title,
                    StoreId = 4,
                    DiscountPercent = ComputeDiscountPercent(msrp, listing.Price),
                    UserId = 1 // TODO: system user
                };
                deal = await repoImpl.CreateDealAsync(deal, ct);
                var dp = new DealProduct
                {
                    DealId = deal.Id,
                    ProductId = q.ProductId,
                    ProductVariantId = variantId,
                    Price = listing.Price ?? 0,
                    DealStatusId = 2,
                    Url = listing.Url,
                    ConditionId = listing.ConditionCategoryId,
                    StoreItemId = listing.ItemId,
                    FreeShipping = listing.FreeShipping ?? false,
                    CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                    NextCheckAt = _timeProvider.GetUtcNow().UtcDateTime.AddHours(6)
                };
                await repoImpl.CreateDealProductAsync(dp, ct);
                await _repo.UpdateProductBestDealAsync(q.ProductId, ct);
                created++;
            }
        }
        _logger.LogInformation("Ingested {Count} eBay deals", created);
        return created;
    }

    private async Task<int?> InferBrandIdAsync(string brandName, CancellationToken ct)
    {
        // Placeholder: assumes brand names map by slug/name; implement lookup if Brand model present
        return null;
    }

    private async Task<DealProcessOutcome> ProcessDealProductAsync(DealProduct dealProduct, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            var repoImpl = _repo as SupabaseDealRepository;
            if (repoImpl == null) return DealProcessOutcome.Error;

            // Load the parent deal reliably (do not depend on a limited refresh batch).
            var deal = await _repo.GetDealByIdAsync(dealProduct.DealId, ct);
            var url = dealProduct.Url ?? deal?.ExternalOfferUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                _logger.LogWarning("Missing URL for deal_product {DealProductId} (deal_id={DealId}). deal_product.url and deal.external_offer_url are null/empty.", dealProduct.Id, dealProduct.DealId);
                return DealProcessOutcome.Error;
            }

            
            var storeType = InferStoreType(url);
            _clientMap.TryGetValue(storeType, out var client);
            if (client == null)
            {
                _logger.LogWarning("No client for store type {StoreType}. Using scraper fallback.", storeType);
            }
            
            // store api_enabled
            Store? store = null;
            if (deal?.StoreId != null)
                store = await repoImpl.GetStoreByIdAsync(deal.StoreId, ct);

            // Expire deal if its expiration_date is past now
            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            if (deal != null && deal.ExpirationDate.HasValue && deal.ExpirationDate.Value < nowUtc && dealProduct.DealStatusId != SupabaseDealRepository.DealStatusExpired)
            {
                dealProduct.DealStatusId = SupabaseDealRepository.DealStatusExpired;
                await repoImpl.UpdateDealProductAsync(dealProduct, ct);
                if (deal.DealStatusId != SupabaseDealRepository.DealStatusExpired)
                {
                    deal.DealStatusId = SupabaseDealRepository.DealStatusExpired;
                    await _repo.UpdateDealsAsync(new[] { deal }, ct);
                }
                try
                {
                    await _repo.UpdateProductBestDealAsync(dealProduct.ProductId, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Best deal RPC failed (expired) for product {ProductId}", dealProduct.ProductId);
                }
                return DealProcessOutcome.Expired;
            }

            // Only price-check admin-posted deals
            bool isAdminDeal = false;
            if (deal != null)
            {
                var user = await repoImpl.GetUserByIdAsync(deal.UserId, ct);
                isAdminDeal = user?.Admin == true;
            }

            StoreProductData? data = null;
            if (!isAdminDeal)
            {
                // Skip price fetch; schedule a distant next check
                await repoImpl.SetNextCheckAsync(dealProduct, nowUtc.AddHours(48), ct);
                return DealProcessOutcome.Updated;
            }

            // Parse store scrape configuration (selectors) if present
            string[]? overrideSelectors = null;
            if (store?.ScrapeConfig != null)
            {
                try
                {
                    using var docJson = System.Text.Json.JsonDocument.Parse(store.ScrapeConfig);
                    if (docJson.RootElement.TryGetProperty("price_selectors", out var selArr) && selArr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        overrideSelectors = selArr.EnumerateArray()
                            .Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String)
                            .Select(e => e.GetString()!)
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .Distinct()
                            .ToArray();
                    }
                }
                catch { /* ignore malformed JSON */ }
            }

            // eBay sold/in-stock status should come from the API (HTML scraping is unreliable).
            var forceApi = storeType == StoreType.Ebay && client != null && client.SupportsApi;
            var useApi = forceApi || (store?.ApiEnabled == true && client != null);

            if (useApi)
            {
                data = await client!.GetByUrlAsync(url, ct);
                if (!forceApi && data == null && store?.ScrapeEnabled == true && overrideSelectors != null && overrideSelectors.Length > 0)
                {
                    // fallback to scraping (only if enabled)
                    data = await FallbackScrapeAsync(url, overrideSelectors, ct);
                }
            }
            else if (store?.ScrapeEnabled == true && overrideSelectors != null && overrideSelectors.Length > 0)
            {
                data = await FallbackScrapeAsync(url, overrideSelectors, ct);
            }
            else
            {
                // Scraping disabled; schedule next far out and return
                await repoImpl.SetNextCheckAsync(dealProduct, nowUtc.AddHours(48), ct);
                return DealProcessOutcome.Updated;
            }
            if (data == null)
            {
                await repoImpl.IncrementErrorCountAsync(dealProduct, ct);
                await repoImpl.SetNextCheckAsync(dealProduct, _timeProvider.GetUtcNow().UtcDateTime.AddHours(12), ct);
                if ((dealProduct.ErrorCount ?? 0) + 1 > 10)
                    await repoImpl.MarkStaleAsync(dealProduct, ct);
                return DealProcessOutcome.Error;
            }

            bool statusChanged = false;
            bool priceChanged = false;

            // Determine new status based on data flags.
            if (data.Sold == true && dealProduct.DealStatusId != SupabaseDealRepository.DealStatusSold)
            {
                dealProduct.DealStatusId = SupabaseDealRepository.DealStatusSold; statusChanged = true;
            }
            else if (data.InStock == false && dealProduct.DealStatusId != SupabaseDealRepository.DealStatusOutOfStock)
            {
                dealProduct.DealStatusId = SupabaseDealRepository.DealStatusOutOfStock; statusChanged = true;
            }
            else if (data.Discontinued == true && dealProduct.DealStatusId != SupabaseDealRepository.DealStatusExpired)
            {
                dealProduct.DealStatusId = SupabaseDealRepository.DealStatusExpired; statusChanged = true; return DealProcessOutcome.Expired;
            }

            if (data.Price.HasValue && data.Price.Value > 0 && dealProduct.Price != data.Price.Value)
            {
                var oldPrice = dealProduct.Price;
                dealProduct.Price = data.Price.Value;
                priceChanged = true;
                await _repo.AppendPriceHistoryAsync(dealProduct.DealId, data.Price.Value, data.Currency, _timeProvider.GetUtcNow().UtcDateTime, ct);
            }
            dealProduct.LastCheckedAt = _timeProvider.GetUtcNow().UtcDateTime;
            await repoImpl.UpdateDealProductAsync(dealProduct, ct);
            if (deal != null)
                await _repo.UpdateDealsAsync(new[] { deal }, ct);

            if (statusChanged)
            {
                try
                {
                    await _repo.UpdateProductBestDealAsync(dealProduct.ProductId, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Best deal RPC failed for product {ProductId}", dealProduct.ProductId);
                }
            }

            // schedule next
            var clicks7d = await repoImpl.GetRecentClicksAsync(dealProduct.DealId, dealProduct.ProductId, TimeSpan.FromDays(7), ct);
            var next = ComputeNextCheck(clicks7d, statusChanged);
            await repoImpl.SetNextCheckAsync(dealProduct, next, ct);

            if (dealProduct.DealStatusId == SupabaseDealRepository.DealStatusExpired) return DealProcessOutcome.Expired;
            if (dealProduct.DealStatusId == SupabaseDealRepository.DealStatusSold) return DealProcessOutcome.Sold;
            if (dealProduct.DealStatusId == SupabaseDealRepository.DealStatusOutOfStock && !priceChanged) return DealProcessOutcome.Updated; // treat OOS as updated unless changed earlier
            return DealProcessOutcome.Updated;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing deal_product {DealProductId}", dealProduct.Id);
            return DealProcessOutcome.Error;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private StoreType InferStoreType(string url)
    {
        var u = url.ToLowerInvariant();
        if (u.Contains("ebay.")) return StoreType.Ebay;
        if (u.Contains("amazon.")) return StoreType.Amazon;
        if (u.Contains("bestbuy.")) return StoreType.BestBuy;
        if (u.Contains("walmart.")) return StoreType.Walmart;
        return StoreType.Generic;
    }

    private enum DealProcessOutcome { Updated, Expired, Sold, Error }

    private async Task<StoreProductData?> FallbackScrapeAsync(string url, string[]? overrideSelectors, CancellationToken ct)
    {
        try
        {
            var uri = new Uri(url);
            var scrape = await _scraper.ScrapeAsync(uri, overrideSelectors, ct);
            if (scrape == null) return null;
            return new StoreProductData(
                Price: scrape.ExtractedPrice,
                Currency: scrape.Currency,
                InStock: scrape.InStock,
                Sold: scrape.Sold,
                Discontinued: false,
                RetrievedUtc: _timeProvider.GetUtcNow().UtcDateTime
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallback scrape failed for {Url}", url);
            return null;
        }
    }

    private DateTime ComputeNextCheck(int clicks7d, bool statusChanged)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (statusChanged) return now.AddHours(3);
        if (clicks7d >= 100) return now.AddHours(3);
        if (clicks7d >= 20) return now.AddHours(6);
        if (clicks7d >= 5) return now.AddHours(24);
        return now.AddHours(48);
    }

    private int? ComputeDiscountPercent(float? msrp, decimal? price)
    {
        if (!msrp.HasValue || !price.HasValue || msrp.Value <= 0) return null;
        try
        {
            var pct = (int)Math.Round((1.0 - ((double)price.Value / (double)msrp.Value)) * 100.0);
            return pct;
        }
        catch
        {
            return null;
        }
    }

    // --- Stop words loading ---
    private async Task EnsureStopWordsAsync(CancellationToken ct)
    {
        if (_stopWords != null && _stopWords.Count > 0) return;
        try
        {
            IReadOnlyList<string>? words = null;
            if (_repo is IStopWordsProvider swp)
            {
                words = await swp.GetStopWordsAsync(ct);
            }
            var defaults = new[] { "the","and","with","for","of","by","to","from","new","brand","inch","inches" };
            var source = (words != null && words.Count > 0) ? words : defaults;
            _stopWords = new HashSet<string>(source, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _stopWords = new HashSet<string>(new[] { "the","and","with","for","of","by","to","from","new","brand","inch","inches" }, StringComparer.OrdinalIgnoreCase);
        }
    }

    // --- Matching helpers ---
    private IEnumerable<string> NormalizeIdentityTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var s = text.ToLowerInvariant();
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }
        var raw = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return raw
            .Select(NormalizeToken)
            .Where(t => !string.IsNullOrWhiteSpace(t) && !(_stopWords?.Contains(t) ?? false))
            .Distinct();
    }

    private static string NormalizeToken(string t)
    {
        // unify common synonyms
        return t switch
        {
            "ps5" => "playstation5",
            "tv" => "television",
            _ => t
        };
    }

    private static double Jaccard(IEnumerable<string> a, IEnumerable<string> b)
    {
        var setA = a.ToHashSet();
        var setB = b.ToHashSet();
        if (setA.Count == 0 || setB.Count == 0) return 0.0;
        var inter = setA.Intersect(setB).Count();
        var union = setA.Union(setB).Count();
        return union == 0 ? 0.0 : (double)inter / union;
    }

    private static double Coverage(IEnumerable<string> productTokens, IEnumerable<string> listingTokens)
    {
        var setProduct = productTokens.ToHashSet();
        var setListing = listingTokens.ToHashSet();
        if (setProduct.Count == 0 || setListing.Count == 0) return 0.0;
        var inter = setProduct.Intersect(setListing).Count();
        return (double)inter / (double)setProduct.Count;
    }
}