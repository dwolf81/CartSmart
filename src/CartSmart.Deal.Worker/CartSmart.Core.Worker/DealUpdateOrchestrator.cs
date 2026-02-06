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
    private readonly RefreshSchedulingOptions _scheduling;
    private HashSet<string>? _stopWords;

    private readonly Dictionary<StoreType, IStoreClient> _clientMap;

    public DealUpdateOrchestrator(
        IDealRepository repo,
        IEnumerable<IStoreClient> storeClients,
        ILogger<DealUpdateOrchestrator> logger,
        IHtmlScraper scraper,
        RefreshSchedulingOptions? schedulingOptions = null,
        TimeProvider? timeProvider = null,
        int maxParallel = 5,
        TimeSpan? minRefreshInterval = null)
    {
        _repo = repo;
        _storeClients = storeClients;
        _logger = logger;
        _scraper = scraper;
        _scheduling = schedulingOptions ?? new RefreshSchedulingOptions();
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

        // Priority scheduling: fetch a larger due candidate pool, score, then process only the top N.
        // This keeps "fresh where it counts" while staying within the batchSize budget.
        var multiplier = _scheduling.CandidatePoolMultiplier <= 0 ? 10 : _scheduling.CandidatePoolMultiplier;
        var maxPool = _scheduling.CandidatePoolMax <= 0 ? 500 : _scheduling.CandidatePoolMax;
        var candidateLimit = Math.Clamp(batchSize * multiplier, batchSize, maxPool);
        var dueCandidates = await repoImpl.GetDueDealProductsAsync(candidateLimit, ct);
        if (dueCandidates.Count == 0)
            return new DealRefreshResult(0, 0, 0, 0, 0);

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var productIds = dueCandidates
            .Select(dp => dp.ProductId)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        var productMap = await repoImpl.GetProductsByIdsAsync(productIds, ct);
        var clicks7dByProduct = await repoImpl.GetClickCountsByProductAsync(productIds, TimeSpan.FromDays(7), ct);
        var clicks5mByProduct = await repoImpl.GetClickCountsByProductAsync(productIds, TimeSpan.FromMinutes(5), ct);

        var maxClicks7d = clicks7dByProduct.Count > 0 ? clicks7dByProduct.Values.Max() : 0;

        double Score(DealProduct dp)
        {
            var score = 0.0;

            var storeType = InferStoreType(dp.Url ?? string.Empty);
            var volatileMultiplier = storeType == StoreType.Ebay ? _scheduling.VolatileStalenessMultiplier : 1.0;

            var clicks7d = clicks7dByProduct.TryGetValue(dp.ProductId, out var c7) ? c7 : 0;
            var clicks5m = clicks5mByProduct.TryGetValue(dp.ProductId, out var c5) ? c5 : 0;

            // User-facing weight
            if (clicks5m > 0)
                score += _scheduling.RecentClicks5mBoost; // "on product page now" proxy

            if (productMap.TryGetValue(dp.ProductId, out var product) && product != null && product.DealId == dp.DealId)
                score += _scheduling.BestDealBoost; // best deal currently shown for the product

            // Store primary (often what we show first for a deal)
            if (dp.Primary)
                score += _scheduling.PrimaryBoost;

            // Popularity proxy (clicks instead of page views)
            if (maxClicks7d > 0)
                score += (double)clicks7d / maxClicks7d * _scheduling.Clicks7dNormalizedMaxBoost;
            if (clicks7d >= _scheduling.Clicks7dThreshold)
                score += _scheduling.Clicks7dThresholdBoost;

            // Staleness (minutes since last check)
            var minutesSinceLastCheck = dp.LastCheckedAt.HasValue ? (nowUtc - dp.LastCheckedAt.Value).TotalMinutes : 10_000;
            score += minutesSinceLastCheck * _scheduling.StalenessMinutesFactor * volatileMultiplier;

            // Risk/extractor signals
            var errorCount = dp.ErrorCount ?? 0;
            if (errorCount > 0 && errorCount <= _scheduling.ErrorCountSmallMax)
                score += _scheduling.ErrorCountSmallBoost;
            if (errorCount >= _scheduling.ErrorCountPenaltyMin)
                score += _scheduling.ErrorCountPenalty; // deprioritize very noisy/broken scrapes

            // Business value proxy
            if (dp.Price >= _scheduling.HighPriceThreshold)
                score += _scheduling.HighPriceBoost;

            return score;
        }

        // If a product has service disabled, skip refreshing its deal products.
        // Note: Expire sweep should still run regardless; this only affects refresh.
        var eligibleCandidates = dueCandidates
            .Where(dp => dp.ProductId <= 0 || (productMap.TryGetValue(dp.ProductId, out var prod) && prod != null && !prod.Deleted && prod.EnableService))
            .ToList();

        if (eligibleCandidates.Count == 0)
            return new DealRefreshResult(0, 0, 0, 0, 0);

        var products = eligibleCandidates
            .Select(dp => new { DealProduct = dp, Score = Score(dp) })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.DealProduct.NextCheckAt ?? DateTime.MinValue)
            .Take(batchSize)
            .Select(x => x.DealProduct)
            .ToList();

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

            // Product-scoped negative keywords (listing exclusion)
            var negativeKeywords = await repoImpl.GetOrFetchProductNegativeKeywordsAsync(q.ProductId, ct);
            var normalizedNegativeKeywords = negativeKeywords
                .Select(NormalizeForContains)
                .Where(k => !string.IsNullOrWhiteSpace(k) && k.Length >= 3)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            IReadOnlyList<NewListing> listings;
            if (storeType == StoreType.Ebay)
            {
                // For eBay ingestion, if the product specifies a preferred condition, only search that.
                // Otherwise, search both New and Used so variant resolution can find the best deal per condition.
                // 1 = New, 2 = Used (internal condition categories)
                var conditionIds = product?.PreferredConditionCategoryId.HasValue == true
                    ? new[] { product.PreferredConditionCategoryId.Value }
                    : new[] { 1, 2 };

                var combined = new Dictionary<string, NewListing>(StringComparer.OrdinalIgnoreCase);
                foreach (var cat in conditionIds)
                {
                    var part = await client.SearchNewListingsAsync(q.ProductId, q.Query, cat, ct);
                    foreach (var l in part)
                    {
                        var key = !string.IsNullOrWhiteSpace(l.ItemId)
                            ? l.ItemId!
                            : (l.Url ?? string.Empty);

                        if (string.IsNullOrWhiteSpace(key))
                            continue;

                        if (!combined.ContainsKey(key))
                            combined[key] = l;
                    }
                }
                listings = combined.Values.ToList();
            }
            else
            {
                listings = await client.SearchNewListingsAsync(q.ProductId, q.Query, product?.PreferredConditionCategoryId, ct);
            }
            // Apply matching hierarchy and price sanity
            var candidates = new List<NewListing>();
            foreach (var l in listings)
            {
                if (normalizedNegativeKeywords.Count > 0 && TitleMatchesAnyNegativeKeyword(l.Title, normalizedNegativeKeywords))
                    continue;

                // Respect product's preferred condition category if configured.
                // For eBay, we also restrict the search when configured, but keep this guard as a safety net.
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
                    .GroupBy(x => (VariantId: x.VariantId!.Value, ConditionId: x.Listing.ConditionCategoryId ?? 0))
                    .SelectMany(g => g.OrderBy(x => x.Listing.Price!.Value).Take(topPerProduct))
                    .ToList();
            }
            else
            {
                if (storeType == StoreType.Ebay)
                {
                    // For eBay, keep top N for New and Used separately.
                    selected = resolved
                        .GroupBy(x => x.Listing.ConditionCategoryId ?? 0)
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
                    var scrapeOutcome = await FallbackScrapeAsync(url, overrideSelectors, ct);
                    if (scrapeOutcome.BlockedByBotProtection)
                    {
                        var taskId = await repoImpl.CreateOrGetPendingManualPriceTaskAsync(dealProduct, "bot_protection", ct);
                        _logger.LogWarning(
                            "Scrape blocked for deal_product {DealProductId}. Created/reused manual_price_task {TaskId}.",
                            dealProduct.Id,
                            taskId);
                        await repoImpl.SetNextCheckAsync(dealProduct, nowUtc.AddHours(24), ct);
                        return DealProcessOutcome.Updated;
                    }

                    data = scrapeOutcome.Data;
                }
            }
            else if (store?.ScrapeEnabled == true && overrideSelectors != null && overrideSelectors.Length > 0)
            {
                var scrapeOutcome = await FallbackScrapeAsync(url, overrideSelectors, ct);
                if (scrapeOutcome.BlockedByBotProtection)
                {
                    var taskId = await repoImpl.CreateOrGetPendingManualPriceTaskAsync(dealProduct, "bot_protection", ct);
                    _logger.LogWarning(
                        "Scrape blocked for deal_product {DealProductId}. Created/reused manual_price_task {TaskId}.",
                        dealProduct.Id,
                        taskId);
                    await repoImpl.SetNextCheckAsync(dealProduct, nowUtc.AddHours(24), ct);
                    return DealProcessOutcome.Updated;
                }

                data = scrapeOutcome.Data;
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
            var clicks5m = await repoImpl.GetRecentClicksAsync(dealProduct.DealId, dealProduct.ProductId, TimeSpan.FromMinutes(5), ct);
            var product = await repoImpl.GetProductByIdAsync(dealProduct.ProductId, ct);
            var isBestDealForProduct = product?.DealId == dealProduct.DealId;
            var next = ComputeNextCheckTiered(dealProduct, storeType, clicks7d, clicks5m, isBestDealForProduct, statusChanged, priceChanged);
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

    private enum RefreshTier { A, B, C, D }

    private sealed record ScrapeOutcome(StoreProductData? Data, bool BlockedByBotProtection);

    private async Task<ScrapeOutcome> FallbackScrapeAsync(string url, string[]? overrideSelectors, CancellationToken ct)
    {
        try
        {
            var uri = new Uri(url);
            var scrape = await _scraper.ScrapeAsync(uri, overrideSelectors, ct);
            if (scrape == null) return new ScrapeOutcome(null, false);

            if (scrape.BlockedByBotProtection)
                return new ScrapeOutcome(null, true);

            return new ScrapeOutcome(
                new StoreProductData(
                    Price: scrape.ExtractedPrice,
                    Currency: scrape.Currency,
                    InStock: scrape.InStock,
                    Sold: scrape.Sold,
                    Discontinued: false,
                    RetrievedUtc: _timeProvider.GetUtcNow().UtcDateTime
                ),
                false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallback scrape failed for {Url}", url);
            return new ScrapeOutcome(null, false);
        }
    }

    private DateTime ComputeNextCheckTiered(
        DealProduct dealProduct,
        StoreType storeType,
        int clicks7d,
        int clicks5m,
        bool isBestDealForProduct,
        bool statusChanged,
        bool priceChanged)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        DateTime AddJitterMinutes(int minMinutes, int maxMinutes)
        {
            var min = Math.Max(1, minMinutes);
            var max = Math.Max(min, maxMinutes);
            var jitter = Random.Shared.Next(min, max + 1);
            return now.AddMinutes(jitter);
        }

        DateTime AddJitterHours(int minHours, int maxHours)
        {
            var min = Math.Max(1, minHours);
            var max = Math.Max(min, maxHours);
            var jitter = Random.Shared.Next(min, max + 1);
            return now.AddHours(jitter);
        }

        RefreshTier tier;
        if (clicks5m > 0 || isBestDealForProduct)
            tier = RefreshTier.A;
        else if (dealProduct.Primary)
            tier = RefreshTier.B;
        else if ((dealProduct.ErrorCount ?? 0) >= 10 || dealProduct.StaleAt.HasValue)
            tier = RefreshTier.D;
        else
            tier = RefreshTier.C;

        // Volatile sources get shorter intervals.
        var volatileSource = storeType == StoreType.Ebay;

        // Risk signals: recent status/price changes should be re-checked quickly.
        var riskBump = statusChanged || priceChanged;

        return tier switch
        {
            RefreshTier.A => riskBump
                ? now.AddMinutes(_scheduling.TierA_RiskMinutes)
                : volatileSource
                    ? AddJitterMinutes(_scheduling.TierA_VolatileMinMinutes, _scheduling.TierA_VolatileMaxMinutes)
                    : AddJitterMinutes(_scheduling.TierA_MinMinutes, _scheduling.TierA_MaxMinutes),
            RefreshTier.B => riskBump
                ? now.AddMinutes(_scheduling.TierB_RiskMinutes)
                : volatileSource
                    ? AddJitterMinutes(_scheduling.TierB_VolatileMinMinutes, _scheduling.TierB_VolatileMaxMinutes)
                    : AddJitterMinutes(_scheduling.TierB_MinMinutes, _scheduling.TierB_MaxMinutes),
            RefreshTier.C => volatileSource
                ? AddJitterHours(_scheduling.TierC_VolatileMinHours, _scheduling.TierC_VolatileMaxHours)
                : AddJitterHours(_scheduling.TierC_MinHours, _scheduling.TierC_MaxHours),
            RefreshTier.D => now.AddDays(_scheduling.TierD_Days),
            _ => now.AddHours(24)
        };
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

    private static bool TitleMatchesAnyNegativeKeyword(string? title, IReadOnlyList<string> normalizedNegativeKeywords)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        if (normalizedNegativeKeywords == null || normalizedNegativeKeywords.Count == 0) return false;
        var normTitle = NormalizeForContains(title);
        if (string.IsNullOrWhiteSpace(normTitle)) return false;
        foreach (var nk in normalizedNegativeKeywords)
        {
            if (string.IsNullOrWhiteSpace(nk)) continue;
            if (normTitle.Contains(nk, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string NormalizeForContains(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var lower = s.Trim().ToLowerInvariant();
        var sb = new System.Text.StringBuilder(lower.Length);
        foreach (var ch in lower)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }
}