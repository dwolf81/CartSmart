using CartSmart.API.Models;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

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
    // Cache for brand name → brand ID lookups (populated lazily during ingest)
    private Dictionary<string, int>? _brandNameToIdCache;

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

            // Product + product-type scoped negative keywords (listing exclusion)
            var productNegativeKeywords = await repoImpl.GetOrFetchProductNegativeKeywordsAsync(q.ProductId, ct);
            var productTypeNegativeKeywords = await repoImpl.GetOrFetchProductTypeNegativeKeywordsAsync(product?.ProductTypeId ?? 0, ct);
            var normalizedNegativeKeywords = productNegativeKeywords
                .Concat(productTypeNegativeKeywords)
                .Select(k => (k ?? string.Empty).Trim().ToLowerInvariant())
                .Where(k => k.Length >= 1)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // For eBay ingest, preferredConditionForSearch narrows to a single condition when configured;
            // otherwise search stays broad and downstream selection takes top N per condition.
            var preferredConditionForSearch = product?.PreferredConditionCategoryId;
            var listings = await client.SearchNewListingsAsync(q.ProductId, q.Query, preferredConditionForSearch, ct);
            // Apply matching hierarchy and price sanity
            var candidates = new List<NewListing>();
            foreach (var l in listings)
            {
                if (normalizedNegativeKeywords.Count > 0 && TitleMatchesAnyNegativeKeyword(l.Title, normalizedNegativeKeywords))
                    continue;

                // Respect product's preferred condition category for all stores as a safety net.
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
                    // Accept listings priced within 40%..150% of MSRP to avoid low-cost accessories and overpriced bundles
                    var p = l.Price!.Value;
                    priceOk = p >= (decimal)msrp.Value * 0.4m && p <= (decimal)msrp.Value * 1.5m;
                }
                if (titleMatch && priceOk)
                {
                    candidates.Add(l);
                }
            }

            // From candidates, pick lowest priced listings.
            // For eBay: resolve variants lazily in ascending-price order and stop early once we have N per variant.
            // If the product has variants and we can't confidently resolve a variant from the listing, skip it.

            var variantClient = client as IVariantResolvingStoreClient;
            var hasVariants = variantClient != null && await variantClient.HasActiveVariantsAsync(q.ProductId, ct);

            List<(NewListing Listing, long? VariantId)> selected;
            if (storeType == StoreType.Ebay)
            {
                // Safety cap: limit variant-resolution calls per product to avoid excessive eBay item requests.
                var maxVariantResolveAttempts = int.TryParse(Environment.GetEnvironmentVariable("EBAY_VARIANT_RESOLVE_MAX_ATTEMPTS"), out var parsedMaxVariantResolveAttempts)
                    ? Math.Clamp(parsedMaxVariantResolveAttempts, 0, 500)
                    : 40;
                var targetConditions = product?.PreferredConditionCategoryId is int preferred && (preferred == 1 || preferred == 2 || preferred == 3)
                    ? new[] { preferred }
                    : new[] { 1, 2, 3 }; // New, Used, Refurbished

                // EbayStoreClient already returns a price-sorted list capped at 200 item summaries.
                // Still defensively sort and cap here.
                var priced = candidates
                    .Where(x => x.Price.HasValue)
                    .OrderBy(x => x.Price!.Value)
                    .Take(200)
                    .ToList();

                if (!hasVariants)
                {
                    selected = priced
                        .Where(l => l.ConditionCategoryId.HasValue && targetConditions.Contains(l.ConditionCategoryId.Value))
                        .GroupBy(l => l.ConditionCategoryId!.Value)
                        .SelectMany(g => g.Take(topPerProduct))
                        .Select(l => (Listing: l, VariantId: (long?)null))
                        .ToList();
                }
                else
                {
                    var variantIds = variantClient != null
                        ? await variantClient.GetActiveVariantIdsAsync(q.ProductId, ct)
                        : Array.Empty<long>();

                    // If we can't enumerate variants, fall back to the old behavior (but still cap to 200).
                    if (variantIds.Count == 0)
                    {
                        var resolvedFallback = new List<(NewListing Listing, long? VariantId)>();
                        var variantResolveAttempts = 0;
                        foreach (var l in priced)
                        {
                            if (variantResolveAttempts >= maxVariantResolveAttempts)
                            {
                                _logger.LogInformation("Reached variant resolve cap for product {ProductId}. Attempts={Attempts}", q.ProductId, variantResolveAttempts);
                                break;
                            }

                            variantResolveAttempts++;
                            var vid = variantClient != null
                                ? await variantClient.TryResolveProductVariantIdAsync(q.ProductId, l, ct)
                                : null;
                            if (!vid.HasValue) continue;
                            resolvedFallback.Add((l, vid));
                        }

                        selected = resolvedFallback
                            .Where(x => x.Listing.ConditionCategoryId.HasValue && targetConditions.Contains(x.Listing.ConditionCategoryId.Value))
                            .GroupBy(x => (x.VariantId!.Value, x.Listing.ConditionCategoryId!.Value))
                            .SelectMany(g => g.Take(topPerProduct))
                            .ToList();
                    }
                    else
                    {
                        var counts = variantIds
                            .SelectMany(vid => targetConditions.Select(cond => (vid, cond)))
                            .ToDictionary(k => k, _ => 0);
                        var picked = new List<(NewListing Listing, long? VariantId)>();
                        var variantResolveAttempts = 0;

                        foreach (var l in priced)
                        {
                            if (picked.Count >= variantIds.Count * targetConditions.Length * topPerProduct)
                                break;

                            if (variantResolveAttempts >= maxVariantResolveAttempts)
                            {
                                _logger.LogInformation("Reached variant resolve cap for product {ProductId}. Attempts={Attempts}", q.ProductId, variantResolveAttempts);
                                break;
                            }

                            variantResolveAttempts++;

                            var vid = await variantClient!.TryResolveProductVariantIdAsync(q.ProductId, l, ct);
                            if (!vid.HasValue) continue;
                            if (!l.ConditionCategoryId.HasValue) continue;
                            var condition = l.ConditionCategoryId.Value;
                            if (!targetConditions.Contains(condition)) continue;

                            var key = (vid.Value, condition);
                            if (!counts.TryGetValue(key, out var c))
                                continue; // ignore variants outside the configured active set
                            if (c >= topPerProduct)
                                continue;

                            picked.Add((l, vid));
                            counts[key] = c + 1;

                            if (counts.Values.All(v => v >= topPerProduct))
                                break;
                        }

                        selected = picked;
                    }
                }
            }
            else
            {
                // Non-eBay: keep existing behavior.
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
                    selected = resolved
                        .OrderBy(x => x.Listing.Price!.Value)
                        .Take(topPerProduct)
                        .ToList();
                }
            }

            foreach (var (listing, variantId) in selected)
            {
                if (listing.ItemId != null)
                {
                    var existing = await repoImpl.GetDealProductByStoreItemIdAsync(listing.ItemId, ct);
                    if (existing != null)
                    {
                        // Listing already tracked — refresh price and scheduling timestamps
                        // so the refresh pipeline doesn't re-check it shortly after ingest.
                        var refreshNow = _timeProvider.GetUtcNow().UtcDateTime;
                        if (listing.Price.HasValue && existing.Price != listing.Price.Value)
                        {
                            await repoImpl.AppendPriceHistoryForDealProductAsync(existing.Id, listing.Price.Value, listing.Currency, refreshNow, ct);
                            existing.Price = listing.Price.Value;

                            // Update discount on the parent deal when price changes for a primary deal_product
                            if (existing.Primary)
                            {
                                var existingDealForDiscount = await _repo.GetDealByIdAsync(existing.DealId, ct);
                                if (existingDealForDiscount != null)
                                {
                                    var newDiscount = ComputeDiscountPercent(msrp, listing.Price.Value,
                                        product?.CountEnabled == true, product?.DefaultCount ?? 1, existing.ItemCount);
                                    if (existingDealForDiscount.DiscountPercent != newDiscount)
                                    {
                                        existingDealForDiscount.DiscountPercent = newDiscount;
                                        await _repo.UpdateDealsAsync(new[] { existingDealForDiscount }, ct);
                                    }
                                }
                            }
                        }
                        // Reactivate if the listing appeared again during ingest but was
                        // previously marked Expired/Sold/OutOfStock (skip if deleted).
                        if (!existing.Deleted
                            && (existing.DealStatusId == SupabaseDealRepository.DealStatusExpired
                                || existing.DealStatusId == SupabaseDealRepository.DealStatusSold
                                || existing.DealStatusId == SupabaseDealRepository.DealStatusOutOfStock))
                        {
                            existing.DealStatusId = SupabaseDealRepository.DealStatusActive;
                            var existingDeal = await _repo.GetDealByIdAsync(existing.DealId, ct);
                            if (existingDeal != null && !existingDeal.Deleted
                                && (existingDeal.DealStatusId == SupabaseDealRepository.DealStatusExpired
                                    || existingDeal.DealStatusId == SupabaseDealRepository.DealStatusSold
                                    || existingDeal.DealStatusId == SupabaseDealRepository.DealStatusOutOfStock))
                            {
                                existingDeal.DealStatusId = SupabaseDealRepository.DealStatusActive;
                                await _repo.UpdateDealsAsync(new[] { existingDeal }, ct);
                            }
                            _logger.LogInformation(
                                "Reactivated deal_product {Id} (deal_id={DealId}) — listing available again during ingest.",
                                existing.Id, existing.DealId);
                        }
                        existing.LastCheckedAt = refreshNow;
                        existing.NextCheckAt = refreshNow.AddHours(6);
                        await repoImpl.UpdateDealProductAsync(existing, ct);
                        _logger.LogDebug("Refreshed existing deal_product {Id} (store_item_id={ItemId})", existing.Id, listing.ItemId);
                        continue;
                    }
                }
                var itemCount = product?.CountEnabled == true
                    ? (ParsePackCount(listing.Title ?? string.Empty) ?? product.DefaultCount)
                    : 1;
                var deal = new Deal
                {
                    CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                    DealStatusId = 2,
                    DealTypeId = 1,                    
                    AdditionalDetails = listing.Title,
                    StoreId = 4,
                    DiscountPercent = ComputeDiscountPercent(msrp, listing.Price,
                        product?.CountEnabled == true, product?.DefaultCount ?? 1, itemCount),
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
                    NextCheckAt = _timeProvider.GetUtcNow().UtcDateTime.AddHours(6),
                    ItemCount = itemCount,
                    Primary = true
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
        if (string.IsNullOrWhiteSpace(brandName)) return null;

        // Lazily load and cache all brands on first use
        if (_brandNameToIdCache == null)
        {
            try
            {
                var brands = await _repo.GetAllBrandsAsync(ct);
                _brandNameToIdCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var b in brands)
                {
                    if (!string.IsNullOrWhiteSpace(b.Name))
                        _brandNameToIdCache.TryAdd(b.Name.Trim(), b.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load brands for InferBrandIdAsync");
                _brandNameToIdCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }
        }

        var normalized = brandName.Trim();
        if (_brandNameToIdCache.TryGetValue(normalized, out var id))
            return id;

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
                if (!forceApi && data == null && ScrapeMode.AllowsServiceScrape(store?.ScrapeModeId) && overrideSelectors != null && overrideSelectors.Length > 0)
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
            else if (ScrapeMode.AllowsServiceScrape(store?.ScrapeModeId) && overrideSelectors != null && overrideSelectors.Length > 0)
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
                // No automated refresh path (no API and scraping disabled/unconfigured).
                // This refresh pipeline only runs on ACTIVE + DIRECT deals that are due, so flag for manual verification.
                if (dealProduct.DealStatusId == SupabaseDealRepository.DealStatusActive)
                {
                    var reason = ScrapeMode.AllowsServiceScrape(store?.ScrapeModeId)
                        ? (overrideSelectors == null || overrideSelectors.Length == 0 ? "scrape_selectors_missing" : "no_auto_refresh")
                        : ScrapeMode.AllowsBrowserScrape(store?.ScrapeModeId) ? "browser_only" : "scrape_disabled";

                    var taskId = await repoImpl.CreateOrGetPendingManualPriceTaskAsync(
                        string.IsNullOrWhiteSpace(dealProduct.Url) ? new DealProduct { Id = dealProduct.Id, Url = url } : dealProduct,
                        reason,
                        ct);
                    _logger.LogWarning(
                        "No API/scrape refresh path for deal_product {DealProductId}. Created/reused manual_price_task {TaskId} (reason={Reason}).",
                        dealProduct.Id,
                        taskId,
                        reason);
                }

                await repoImpl.SetNextCheckAsync(dealProduct, nowUtc.AddHours(48), ct);
                return DealProcessOutcome.Updated;
            }
            if (data == null)
            {
                await repoImpl.IncrementErrorCountAsync(dealProduct, ct);
                var newErrorCount = (dealProduct.ErrorCount ?? 0) + 1;

                if (newErrorCount > 20)
                {
                    // Too many consecutive failures — mark stale and create a manual price task,
                    // but do NOT auto-expire. Only expiration_date should drive the Expired status.
                    _logger.LogWarning(
                        "deal_product {DealProductId} (deal {DealId}) has {ErrorCount} consecutive errors. Marking stale and creating manual task.",
                        dealProduct.Id, dealProduct.DealId, newErrorCount);

                    await repoImpl.MarkStaleAsync(dealProduct, ct);

                    try
                    {
                        var taskId = await repoImpl.CreateOrGetPendingManualPriceTaskAsync(dealProduct, "consecutive_errors", ct);
                        _logger.LogWarning(
                            "Created/reused manual_price_task {TaskId} for deal_product {DealProductId} due to {ErrorCount} consecutive errors.",
                            taskId, dealProduct.Id, newErrorCount);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to create manual price task for deal_product {DealProductId}", dealProduct.Id);
                    }

                    // Push next check far out so we stop retrying frequently.
                    await repoImpl.SetNextCheckAsync(dealProduct, _timeProvider.GetUtcNow().UtcDateTime.AddHours(48), ct);
                    return DealProcessOutcome.Error;
                }

                if (newErrorCount > 10)
                    await repoImpl.MarkStaleAsync(dealProduct, ct);

                await repoImpl.SetNextCheckAsync(dealProduct, _timeProvider.GetUtcNow().UtcDateTime.AddHours(12), ct);
                return DealProcessOutcome.Error;
            }

            bool statusChanged = false;
            bool priceChanged = false;
            decimal? oldPriceForPropagation = null;

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
                oldPriceForPropagation = oldPrice;
                await _repo.AppendPriceHistoryAsync(dealProduct.DealId, data.Price.Value, data.Currency, _timeProvider.GetUtcNow().UtcDateTime, ct);

                // Update discount on the parent deal when price changes for a primary deal_product
                if (dealProduct.Primary && deal != null)
                {
                    var refreshProduct = await repoImpl.GetProductByIdAsync(dealProduct.ProductId, ct);
                    if (refreshProduct != null)
                    {
                        var newDiscount = ComputeDiscountPercent(refreshProduct.MSRP, data.Price.Value,
                            refreshProduct.CountEnabled, refreshProduct.DefaultCount, dealProduct.ItemCount);
                        if (deal.DiscountPercent != newDiscount)
                        {
                            deal.DiscountPercent = newDiscount;
                            await _repo.UpdateDealsAsync(new[] { deal }, ct);
                        }
                    }
                }
            }
            dealProduct.LastCheckedAt = _timeProvider.GetUtcNow().UtcDateTime;
            await repoImpl.UpdateDealProductAsync(dealProduct, ct);

            //Not needed anymore
            /*if (statusChanged || priceChanged)
            {
                try
                {
                    await _repo.UpdateProductBestDealAsync(dealProduct.ProductId, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Best deal RPC failed for product {ProductId}", dealProduct.ProductId);
                }
            }*/

            // If a DIRECT deal price changes, propagate it to coupon/external/stacked deals that share the same URL.
            // Those deal types derive their effective price from the direct base price.
            if (priceChanged && oldPriceForPropagation.HasValue)
            {
                try
                {
                    var updatedLinked = await repoImpl.PropagateDirectPriceChangeToLinkedDealsByUrlAsync(
                        directDealProduct: dealProduct,
                        oldDirectPrice: oldPriceForPropagation.Value,
                        newDirectPrice: dealProduct.Price,
                        ct);

                    if (updatedLinked > 0)
                        _logger.LogInformation(
                            "Propagated direct price change for deal_product {DealProductId}: updated {Count} linked deal_product rows.",
                            dealProduct.Id,
                            updatedLinked);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to propagate direct price change for deal_product {DealProductId}", dealProduct.Id);
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

    private int? ComputeDiscountPercent(float? msrp, decimal? price, bool countEnabled = false, int defaultCount = 1, int itemCount = 1)
    {
        if (!msrp.HasValue || !price.HasValue || msrp.Value <= 0) return null;
        try
        {
            double effectiveMsrp = (double)msrp.Value;
            double effectivePrice = (double)price.Value;

            if (countEnabled && defaultCount > 0 && itemCount > 0)
            {
                // Compare per-item prices for multi-pack products
                effectiveMsrp /= defaultCount;
                effectivePrice /= itemCount;
            }

            if (effectiveMsrp <= 0) return null;
            var pct = (int)Math.Round((1.0 - (effectivePrice / effectiveMsrp)) * 100.0);
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
        var lowerTitle = title.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lowerTitle)) return false;
        foreach (var nk in normalizedNegativeKeywords)
        {
            if (string.IsNullOrWhiteSpace(nk)) continue;
            if (lowerTitle.Contains(nk, StringComparison.Ordinal))
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

    /// <summary>
    /// Extract the pack/item count from a listing title (e.g. "12 pack", "dozen", "6ct").
    /// Returns the parsed quantity, or null if none found.
    /// </summary>
    private static int? ParsePackCount(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var lower = title.ToLowerInvariant();
        try
        {
            var m = Regex.Match(lower, @"(\d+)\s*(pack|pk|ct|count|pc|pcs)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > 0)
                return n;
            if (lower.Contains("dozen"))
                return 12;
        }
        catch { }
        return null;
    }
}