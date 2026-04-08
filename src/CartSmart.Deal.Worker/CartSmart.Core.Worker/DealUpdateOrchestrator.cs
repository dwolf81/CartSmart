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
    private readonly IAiDealValidator? _aiValidator;

    // Word-form numbers for pack-count parsing in listing titles
    private static readonly Dictionary<string, int> WordToNumber = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
        ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
        ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14,
        ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18,
        ["nineteen"] = 19, ["twenty"] = 20,
    };

    public DealUpdateOrchestrator(
        IDealRepository repo,
        IEnumerable<IStoreClient> storeClients,
        ILogger<DealUpdateOrchestrator> logger,
        IHtmlScraper scraper,
        RefreshSchedulingOptions? schedulingOptions = null,
        TimeProvider? timeProvider = null,
        int maxParallel = 5,
        TimeSpan? minRefreshInterval = null,
        IAiDealValidator? aiValidator = null)
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
        _aiValidator = aiValidator;
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

            // Ingest log: track outcome for every listing returned by the API
            var ingestLogEntries = new Dictionary<string, (NewListing Listing, string Outcome, int? DealProductId, string? IgnoreReason)>(StringComparer.OrdinalIgnoreCase);
            var aiDecisions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void LogListing(NewListing l, string outcome, int? dpId = null, string? reason = null)
            {
                var key = l.ItemId ?? Guid.NewGuid().ToString();
                ingestLogEntries[key] = (l, outcome, dpId, reason);
            }

            // Apply matching hierarchy and price sanity
            var candidates = new List<NewListing>();
            foreach (var l in listings)
            {
                if (normalizedNegativeKeywords.Count > 0 && MatchesAnyNegativeKeyword(l.Title, l.ShortDescription, normalizedNegativeKeywords))
                {
                    LogListing(l, "ignored", reason: "negative_keyword");
                    continue;
                }

                // Respect product's preferred condition category for all stores as a safety net.
                if (product?.PreferredConditionCategoryId.HasValue == true)
                {
                    if (l.ConditionCategoryId != product.PreferredConditionCategoryId.Value)
                    {
                        LogListing(l, "ignored", reason: "condition_mismatch");
                        continue;
                    }
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
                    // Accept listings priced within floor..150% of MSRP to avoid low-cost accessories and overpriced bundles.
                    // Floor defaults to 40% of MSRP but respects api_min_price when set on the product.
                    var p = l.Price!.Value;
                    var floor = product?.ApiMinPrice.HasValue == true
                        ? (decimal)product.ApiMinPrice.Value
                        : (decimal)msrp.Value * 0.4m;
                    priceOk = p >= floor && p <= (decimal)msrp.Value * 1.5m;
                }
                if (titleMatch && priceOk)
                {
                    candidates.Add(l);
                }
                else
                {
                    LogListing(l, "ignored", reason: !titleMatch ? "title_mismatch" : "price_out_of_range");
                }
            }

            // ── AI validation for low-confidence title-only matches ──
            if (_aiValidator != null && candidates.Count > 0)
            {
                var aiNoiseThreshold = double.TryParse(
                    Environment.GetEnvironmentVariable("AI_VALIDATION_NOISE_THRESHOLD"), out var nt) ? nt : 0.5;

                // Resolve brand name for AI prompt (reverse lookup from brand cache)
                string? brandName = null;
                if (brandId.HasValue)
                {
                    await EnsureBrandCacheAsync(ct);
                    brandName = _brandNameToIdCache?
                        .FirstOrDefault(kv => kv.Value == brandId.Value).Key;
                }

                // Fetch search aliases so the AI knows which editions/variants are valid
                var searchAliases = await repoImpl.GetProductSearchAliasesAsync(q.ProductId, ct);

                var aiValidated = new List<NewListing>(candidates.Count);
                foreach (var c in candidates)
                {
                    // Structural matches (GTIN or Brand+MPN) are high confidence — skip AI
                    bool isStructural = !string.IsNullOrWhiteSpace(c.GTIN)
                        || (!string.IsNullOrWhiteSpace(c.Brand) && !string.IsNullOrWhiteSpace(c.MPN) && brandId.HasValue);

                    if (isStructural)
                    {
                        aiValidated.Add(c);
                        continue;
                    }

                    // Already tracked as a deal_product — no need to re-validate with AI
                    if (!string.IsNullOrWhiteSpace(c.ItemId)
                        && await repoImpl.GetDealProductByStoreItemIdAsync(c.ItemId, ct) != null)
                    {
                        aiValidated.Add(c);
                        continue;
                    }

                    // Compute noise ratio: fraction of listing tokens NOT matching product tokens
                    var cTitleTokens = NormalizeIdentityTokens(c.Title ?? string.Empty).ToList();
                    var productTokenSet = productTokens.ToHashSet();
                    var matchedCount = cTitleTokens.Count(t => productTokenSet.Contains(t));
                    var noiseRatio = cTitleTokens.Count > 0
                        ? 1.0 - ((double)matchedCount / cTitleTokens.Count)
                        : 0.0;

                    if (noiseRatio <= aiNoiseThreshold)
                    {
                        aiValidated.Add(c);
                        continue;
                    }

                    // High noise — check ingest_log for a previous AI decision
                    if (!string.IsNullOrWhiteSpace(c.ItemId))
                    {
                        if (await repoImpl.HasAiRejectedEntryAsync(c.ItemId, ct))
                        {
                            LogListing(c, "ignored", reason: "ai_previously_rejected");
                            continue;
                        }
                        if (await repoImpl.HasAiApprovedEntryAsync(c.ItemId, ct))
                        {
                            aiValidated.Add(c);
                            continue;
                        }
                    }

                    // Call AI validation
                    _logger.LogDebug("AI validating listing {ItemId} (noise={Noise:P0}): {Title}",
                        c.ItemId, noiseRatio, c.Title);

                    var aiResult = await _aiValidator.ValidateAsync(new AiValidationRequest(
                        ProductName: product?.Name ?? productName,
                        ProductBrand: brandName,
                        ProductMsrp: msrp.HasValue ? (decimal)msrp.Value : null,
                        ExpectedPackCount: product?.CountEnabled == true ? product.DefaultCount : null,
                        ContentType: "ebay_listing",
                        ContentTitle: c.Title ?? string.Empty,
                        ContentBody: c.ShortDescription,
                        ContentPrice: c.Price,
                        ContentUrl: c.Url,
                        KnownAliases: searchAliases
                    ), ct);

                    if (aiResult.IsLegitimate)
                    {
                        _logger.LogInformation("AI approved listing {ItemId}: {Reason}", c.ItemId, aiResult.Reason);
                        if (!string.IsNullOrWhiteSpace(c.ItemId))
                            aiDecisions[c.ItemId] = $"ai_approved: {aiResult.Reason}";
                        aiValidated.Add(c);
                    }
                    else
                    {
                        _logger.LogInformation("AI rejected listing {ItemId}: {Reason}", c.ItemId, aiResult.Reason);
                        if (!string.IsNullOrWhiteSpace(c.ItemId))
                            aiDecisions[c.ItemId] = $"ai_rejected: {aiResult.Reason}";
                        LogListing(c, "ignored", reason: $"ai_rejected: {aiResult.Reason}");
                    }
                }
                candidates = aiValidated;
            }

            // From candidates, pick lowest priced listings.
            // For eBay: resolve variants lazily in ascending-price order and stop early once we have N per variant.
            // If the product has variants and we can't confidently resolve a variant from the listing, skip it.

            var variantClient = client as IVariantResolvingStoreClient;
            var hasVariants = variantClient != null && await variantClient.HasActiveVariantsAsync(q.ProductId, ct);

            List<(NewListing Listing, long? VariantId)> selected;
            var cappedItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (storeType == StoreType.Ebay)
            {
                var targetConditions = product?.PreferredConditionCategoryId is int preferred && (preferred == 1 || preferred == 2 || preferred == 3)
                    ? new[] { preferred }
                    : new[] { 1, 2, 3 }; // New, Used, Refurbished

                // EbayStoreClient already returns a price-sorted list capped at 200 item summaries.
                // Still defensively sort and cap here.
                var pricedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var priced = new List<NewListing>();
                foreach (var c in candidates.OrderBy(x => x.Price ?? decimal.MaxValue))
                {
                    if (!c.Price.HasValue)
                    {
                        LogListing(c, "ignored", reason: "no_price");
                        continue;
                    }
                    if (pricedSet.Count >= 200)
                    {
                        LogListing(c, "ignored", reason: "over_cap");
                        continue;
                    }
                    pricedSet.Add(c.ItemId ?? "");
                    priced.Add(c);
                }

                // For count-enabled products, only import listings whose detected item count
                // matches the product's default_count. If no count can be parsed from the
                // title, assume it matches (keep the listing).
                if (product?.CountEnabled == true && product.DefaultCount > 0)
                {
                    var expectedCount = product.DefaultCount;
                    var filtered = new List<NewListing>();
                    foreach (var l in priced)
                    {
                        var parsed = ParsePackCount(l.Title ?? string.Empty)
                            ?? ParsePackCount(l.ShortDescription ?? string.Empty);
                        if (parsed.HasValue && parsed.Value != expectedCount)
                        {
                            LogListing(l, "ignored", reason: $"pack_count_mismatch (detected={parsed.Value}, expected={expectedCount})");
                            continue;
                        }
                        filtered.Add(l);
                    }
                    priced = filtered;
                }

                // Pre-fetch tracked store_item_ids so we can bypass caps for existing deal_products
                var trackedItemIds = await repoImpl.GetTrackedStoreItemIdsForProductAsync(q.ProductId, ct);

                if (!hasVariants)
                {
                    var conditionCounts = targetConditions.ToDictionary(c => c, _ => 0);
                    selected = new List<(NewListing Listing, long? VariantId)>();
                    foreach (var l in priced)
                    {
                        if (!l.ConditionCategoryId.HasValue || !targetConditions.Contains(l.ConditionCategoryId.Value))
                        {
                            LogListing(l, "ignored", reason: $"condition_not_in_target (condition={l.ConditionCategoryId})");
                            continue;
                        }
                        var cond = l.ConditionCategoryId.Value;
                        if (conditionCounts[cond] >= topPerProduct)
                        {
                            // Cap reached — still include if already tracked so it gets
                            // refreshed (marked Capped instead of Sold).
                            if (l.ItemId != null && trackedItemIds.Contains(l.ItemId))
                            {
                                selected.Add((l, (long?)null));
                                cappedItemIds.Add(l.ItemId);
                            }
                            else
                            {
                                LogListing(l, "ignored", reason: $"per_condition_cap_reached (condition={cond})");
                            }
                            continue;
                        }
                        selected.Add((l, (long?)null));
                        conditionCounts[cond]++;
                    }
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
                        foreach (var l in priced)
                        {
                            var vid = variantClient != null
                                ? await variantClient.TryResolveProductVariantIdAsync(q.ProductId, l, ct)
                                : null;
                            if (!vid.HasValue)
                            {
                                LogListing(l, "ignored", reason: "variant_unresolved");
                                continue;
                            }
                            resolvedFallback.Add((l, vid));
                        }

                        // Apply condition + cap filtering on resolved
                        var fallbackCounts = new Dictionary<(long, int), int>();
                        selected = new List<(NewListing Listing, long? VariantId)>();
                        foreach (var (l, vid) in resolvedFallback)
                        {
                            if (!l.ConditionCategoryId.HasValue || !targetConditions.Contains(l.ConditionCategoryId.Value))
                            {
                                LogListing(l, "ignored", reason: $"condition_not_in_target (condition={l.ConditionCategoryId})");
                                continue;
                            }
                            var key = (vid!.Value, l.ConditionCategoryId.Value);
                            fallbackCounts.TryGetValue(key, out var cnt);
                            if (cnt >= topPerProduct)
                            {
                                if (l.ItemId != null && trackedItemIds.Contains(l.ItemId))
                                {
                                    selected.Add((l, vid));
                                    cappedItemIds.Add(l.ItemId);
                                }
                                else
                                {
                                    LogListing(l, "ignored", reason: $"per_variant_condition_cap_reached (variant={vid}, condition={l.ConditionCategoryId})");
                                }
                                continue;
                            }
                            selected.Add((l, vid));
                            fallbackCounts[key] = cnt + 1;
                        }
                    }
                    else
                    {
                        var counts = variantIds
                            .SelectMany(vid => targetConditions.Select(cond => (vid, cond)))
                            .ToDictionary(k => k, _ => 0);
                        var picked = new List<(NewListing Listing, long? VariantId)>();
                        var allFull = false;

                        foreach (var l in priced)
                        {
                            if (allFull)
                            {
                                if (l.ItemId != null && trackedItemIds.Contains(l.ItemId))
                                {
                                    // Resolve variant for the tracked item before adding
                                    var capVid = await variantClient!.TryResolveProductVariantIdAsync(q.ProductId, l, ct);
                                    picked.Add((l, capVid));
                                    cappedItemIds.Add(l.ItemId);
                                }
                                else
                                {
                                    LogListing(l, "ignored", reason: "all_variant_slots_filled");
                                }
                                continue;
                            }

                            var vid = await variantClient!.TryResolveProductVariantIdAsync(q.ProductId, l, ct);
                            if (!vid.HasValue)
                            {
                                LogListing(l, "ignored", reason: "variant_unresolved");
                                continue;
                            }
                            if (!l.ConditionCategoryId.HasValue)
                            {
                                LogListing(l, "ignored", reason: "no_condition");
                                continue;
                            }
                            var condition = l.ConditionCategoryId.Value;
                            if (!targetConditions.Contains(condition))
                            {
                                LogListing(l, "ignored", reason: $"condition_not_in_target (condition={condition})");
                                continue;
                            }

                            var key = (vid.Value, condition);
                            if (!counts.TryGetValue(key, out var c))
                            {
                                LogListing(l, "ignored", reason: $"variant_not_in_active_set (variant={vid})");
                                continue;
                            }
                            if (c >= topPerProduct)
                            {
                                if (l.ItemId != null && trackedItemIds.Contains(l.ItemId))
                                {
                                    picked.Add((l, vid));
                                    cappedItemIds.Add(l.ItemId);
                                }
                                else
                                {
                                    LogListing(l, "ignored", reason: $"per_variant_condition_cap_reached (variant={vid}, condition={condition})");
                                }
                                continue;
                            }

                            picked.Add((l, vid));
                            counts[key] = c + 1;

                            if (counts.Values.All(v => v >= topPerProduct))
                                allFull = true;
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
                    {
                        LogListing(l, "ignored", reason: "variant_unresolved");
                        continue;
                    }

                    resolved.Add((l, variantId));
                }

                if (hasVariants)
                {
                    var grouped = resolved
                        .Where(x => x.VariantId.HasValue)
                        .GroupBy(x => (VariantId: x.VariantId!.Value, ConditionId: x.Listing.ConditionCategoryId ?? 0))
                        .ToList();
                    selected = new List<(NewListing Listing, long? VariantId)>();
                    foreach (var g in grouped)
                    {
                        var taken = 0;
                        foreach (var x in g.OrderBy(x => x.Listing.Price!.Value))
                        {
                            if (taken < topPerProduct)
                            {
                                selected.Add(x);
                                taken++;
                            }
                            else
                            {
                                LogListing(x.Listing, "ignored", reason: $"per_variant_condition_cap_reached (variant={x.VariantId}, condition={g.Key.ConditionId})");
                            }
                        }
                    }
                }
                else
                {
                    var ordered = resolved.OrderBy(x => x.Listing.Price!.Value).ToList();
                    selected = ordered.Take(topPerProduct).ToList();
                    foreach (var x in ordered.Skip(topPerProduct))
                        LogListing(x.Listing, "ignored", reason: "per_product_cap_reached");
                }
            }

            // Capture before the selected loop so any listing updated during the loop
            // gets last_checked_at >= this timestamp, and stale ones remain below it.
            var ingestStartedAt = _timeProvider.GetUtcNow().UtcDateTime;

            foreach (var (listing, variantId) in selected)
            {
                if (listing.ItemId != null)
                {
                    var existing = await repoImpl.GetDealProductByStoreItemIdAsync(listing.ItemId, ct);
                    if (existing != null)
                    {
                        var isCapped = cappedItemIds.Contains(listing.ItemId);
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
                                        await _repo.UpdateDealDiscountOnlyAsync(existingDealForDiscount.Id, newDiscount, ct);
                                    }
                                }
                            }
                        }

                        if (isCapped)
                        {
                            // Listing is still on eBay but exceeds the cap — mark as Capped.
                            if (existing.DealStatusId != SupabaseDealRepository.DealStatusCapped)
                            {
                                existing.DealStatusId = SupabaseDealRepository.DealStatusCapped;
                                var existingDeal = await _repo.GetDealByIdAsync(existing.DealId, ct);
                                if (existingDeal != null && !existingDeal.Deleted
                                    && existingDeal.DealStatusId == SupabaseDealRepository.DealStatusActive)
                                {
                                    existingDeal.DealStatusId = SupabaseDealRepository.DealStatusCapped;
                                    await _repo.UpdateDealsAsync(new[] { existingDeal }, ct);
                                }
                                _logger.LogInformation(
                                    "Capped deal_product {Id} (deal_id={DealId}) — listing still live but exceeds ingest cap.",
                                    existing.Id, existing.DealId);
                            }
                        }
                        else
                        {
                            // Reactivate if the listing appeared again during ingest but was
                            // previously marked Expired/Sold/OutOfStock/Capped (skip if deleted).
                            if (!existing.Deleted
                                && (existing.DealStatusId == SupabaseDealRepository.DealStatusExpired
                                    || existing.DealStatusId == SupabaseDealRepository.DealStatusSold
                                    || existing.DealStatusId == SupabaseDealRepository.DealStatusOutOfStock
                                    || existing.DealStatusId == SupabaseDealRepository.DealStatusCapped))
                            {
                                existing.DealStatusId = SupabaseDealRepository.DealStatusActive;
                                var existingDeal = await _repo.GetDealByIdAsync(existing.DealId, ct);
                                if (existingDeal != null && !existingDeal.Deleted
                                    && (existingDeal.DealStatusId == SupabaseDealRepository.DealStatusExpired
                                        || existingDeal.DealStatusId == SupabaseDealRepository.DealStatusSold
                                        || existingDeal.DealStatusId == SupabaseDealRepository.DealStatusOutOfStock
                                        || existingDeal.DealStatusId == SupabaseDealRepository.DealStatusCapped))
                                {
                                    existingDeal.DealStatusId = SupabaseDealRepository.DealStatusActive;
                                    await _repo.UpdateDealsAsync(new[] { existingDeal }, ct);
                                }
                                _logger.LogInformation(
                                    "Reactivated deal_product {Id} (deal_id={DealId}) — listing available again during ingest.",
                                    existing.Id, existing.DealId);
                            }
                        }
                        existing.LastCheckedAt = refreshNow;
                        existing.NextCheckAt = refreshNow.AddHours(6);
                        await repoImpl.UpdateDealProductAsync(existing, ct);
                        LogListing(listing, isCapped ? "capped" : "updated", dpId: existing.Id);
                        _logger.LogDebug("Refreshed existing deal_product {Id} (store_item_id={ItemId}, capped={Capped})", existing.Id, listing.ItemId, isCapped);
                        continue;
                    }
                }
                var itemCount = product?.CountEnabled == true
                    ? (ParsePackCount(listing.Title ?? string.Empty)
                        ?? ParsePackCount(listing.ShortDescription ?? string.Empty)
                        ?? product.DefaultCount)
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
                    ShortDescription = listing.ShortDescription,
                    Primary = true
                };
                dp = await repoImpl.CreateDealProductAsync(dp, ct);
                LogListing(listing, "added", dpId: dp.Id);
                created++;
            }



            // Flush ingest log entries for this product
            if (ingestLogEntries.Count > 0)
            {
                var logRows = ingestLogEntries.Values.Select(e =>
                {
                    var reason = e.IgnoreReason;
                    // Merge AI decision into the final reason so it's always visible
                    if (e.Listing.ItemId != null && aiDecisions.TryGetValue(e.Listing.ItemId, out var aiDecision)
                        && (reason == null || !reason.StartsWith("ai_", StringComparison.Ordinal)))
                    {
                        reason = reason != null ? $"{aiDecision} | {reason}" : aiDecision;
                    }
                    return new IngestLog
                    {
                        ProductId = q.ProductId,
                        StoreItemId = e.Listing.ItemId,
                        Title = e.Listing.Title?.Length > 500 ? e.Listing.Title[..500] : e.Listing.Title,
                        ShortDescription = e.Listing.ShortDescription?.Length > 1000 ? e.Listing.ShortDescription[..1000] : e.Listing.ShortDescription,
                        Price = e.Listing.Price,
                        Outcome = e.Outcome,
                        DealProductId = e.DealProductId,
                        IgnoreReason = reason
                    };
                }).ToList();
                await repoImpl.InsertIngestLogBatchAsync(logRows, ct);
            }

            // ── eBay staleness: mark deal_products as sold if no longer in search results ──
            // Uses last_checked_at: the selected loop above updates it for found listings,
            // so any active deal_product with last_checked_at < ingestStartedAt wasn't in this run.
            if (storeType == StoreType.Ebay)
            {
                var markedSold = await repoImpl.MarkStaleDealProductsSoldAsync(q.ProductId, 4, ingestStartedAt, ct);
                if (markedSold > 0)
                    _logger.LogInformation(
                        "Marked {Count} stale eBay deal_product(s) as sold for product {ProductId}.",
                        markedSold, q.ProductId);
            }

        }
        _logger.LogInformation("Ingested {Count} eBay deals", created);

        // Purge old ingest log entries (keep last 7 days)
        var retentionDays = int.TryParse(Environment.GetEnvironmentVariable("INGEST_LOG_RETENTION_DAYS"), out var rd) && rd > 0 ? rd : 7;
        await repoImpl.PurgeOldIngestLogsAsync(retentionDays, ct);

        return created;
    }

    private async Task EnsureBrandCacheAsync(CancellationToken ct)
    {
        if (_brandNameToIdCache != null) return;
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
            _logger.LogWarning(ex, "Failed to load brands for EnsureBrandCacheAsync");
            _brandNameToIdCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task<int?> InferBrandIdAsync(string brandName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(brandName)) return null;
        await EnsureBrandCacheAsync(ct);
        var normalized = brandName.Trim();
        if (_brandNameToIdCache!.TryGetValue(normalized, out var id))
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

            // Fallback: if store couldn't be loaded by id (e.g. deserialization failure),
            // resolve from the deal product URL so we still have scrape config.
            if (store == null && !string.IsNullOrWhiteSpace(url))
            {
                _logger.LogWarning(
                    "Store lookup by id returned null for deal_product {DealProductId} (deal.store_id={StoreId}). Attempting URL-based fallback.",
                    dealProduct.Id, deal?.StoreId);
                store = await repoImpl.GetStoreByUrlDomainAsync(url, ct);
                if (store != null)
                    _logger.LogInformation(
                        "URL-based store fallback resolved store {StoreId} (scrape_mode_id={ScrapeModeId}) for deal_product {DealProductId}.",
                        store.Id, store.ScrapeModeId, dealProduct.Id);
            }

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
                    var httpOn = store?.ScrapeHttpEnabled ?? true;
                    var pwOn = store?.ScrapePlaywrightEnabled ?? true;
                    var scrapeOutcome = await FallbackScrapeAsync(url, overrideSelectors, httpOn, pwOn, ct);

                    // Log to scrape_log
                    if (store != null)
                    {
                        if (scrapeOutcome.SucceededMethod != null)
                            await repoImpl.InsertScrapeLogAsync(store.Id, dealProduct.Id, url, scrapeOutcome.SucceededMethod, true, scrapeOutcome.Data?.Price, scrapeOutcome.Data?.Currency, null, ct);
                        else
                        {
                            if (httpOn) await repoImpl.InsertScrapeLogAsync(store.Id, dealProduct.Id, url, "http", false, null, null, scrapeOutcome.BlockedByBotProtection ? "bot_protection" : "no_price_found", ct);
                            if (pwOn) await repoImpl.InsertScrapeLogAsync(store.Id, dealProduct.Id, url, "playwright", false, null, null, scrapeOutcome.BlockedByBotProtection ? "bot_protection" : "no_price_found", ct);
                        }
                    }

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
                var httpOn = store?.ScrapeHttpEnabled ?? true;
                var pwOn = store?.ScrapePlaywrightEnabled ?? true;
                var scrapeOutcome = await FallbackScrapeAsync(url, overrideSelectors, httpOn, pwOn, ct);

                // Log to scrape_log
                if (store != null)
                {
                    if (scrapeOutcome.SucceededMethod != null)
                        await repoImpl.InsertScrapeLogAsync(store.Id, dealProduct.Id, url, scrapeOutcome.SucceededMethod, true, scrapeOutcome.Data?.Price, scrapeOutcome.Data?.Currency, null, ct);
                    else
                    {
                        if (httpOn) await repoImpl.InsertScrapeLogAsync(store.Id, dealProduct.Id, url, "http", false, null, null, scrapeOutcome.BlockedByBotProtection ? "bot_protection" : "no_price_found", ct);
                        if (pwOn) await repoImpl.InsertScrapeLogAsync(store.Id, dealProduct.Id, url, "playwright", false, null, null, scrapeOutcome.BlockedByBotProtection ? "bot_protection" : "no_price_found", ct);
                    }
                }

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

            // TODO: Re-enable status updates from scrape data once stock detection is more reliable.
            // For now, scraping only updates price — status changes are manual-only.
            // if (data.Sold == true && dealProduct.DealStatusId != SupabaseDealRepository.DealStatusSold)
            // {
            //     dealProduct.DealStatusId = SupabaseDealRepository.DealStatusSold; statusChanged = true;
            // }
            // else if (data.InStock == false && dealProduct.DealStatusId != SupabaseDealRepository.DealStatusOutOfStock)
            // {
            //     dealProduct.DealStatusId = SupabaseDealRepository.DealStatusOutOfStock; statusChanged = true;
            // }
            // else if (data.Discontinued == true && dealProduct.DealStatusId != SupabaseDealRepository.DealStatusExpired)
            // {
            //     dealProduct.DealStatusId = SupabaseDealRepository.DealStatusExpired; statusChanged = true; return DealProcessOutcome.Expired;
            // }

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
                            await _repo.UpdateDealDiscountOnlyAsync(deal.Id, newDiscount, ct);
                        }
                    }
                }
            }
            dealProduct.LastCheckedAt = _timeProvider.GetUtcNow().UtcDateTime;
            await repoImpl.UpdateDealProductAsync(dealProduct, ct);

            // Auto-complete any pending manual price tasks since we got a successful refresh
            await repoImpl.CompletePendingManualPriceTasksAsync(
                dealProduct.Id,
                data.Price,
                data.Currency,
                "worker scrape",
                ct);

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

    private sealed record ScrapeOutcome(StoreProductData? Data, bool BlockedByBotProtection, string? SucceededMethod = null);

    private async Task<ScrapeOutcome> FallbackScrapeAsync(string url, string[]? overrideSelectors, bool httpEnabled, bool playwrightEnabled, CancellationToken ct)
    {
        try
        {
            var uri = new Uri(url);
            var scrape = await _scraper.ScrapeAsync(uri, overrideSelectors, httpEnabled, playwrightEnabled, ct);
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
                false,
                scrape.SucceededMethod);
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

    private static bool MatchesAnyNegativeKeyword(string? title, string? shortDescription, IReadOnlyList<string> normalizedNegativeKeywords)
    {
        if (normalizedNegativeKeywords == null || normalizedNegativeKeywords.Count == 0) return false;
        foreach (var nk in normalizedNegativeKeywords)
        {
            if (string.IsNullOrWhiteSpace(nk)) continue;
            var pattern = @"\b" + Regex.Escape(nk) + @"\b";
            if (!string.IsNullOrWhiteSpace(title) && Regex.IsMatch(title.Trim().ToLowerInvariant(), pattern))
                return true;
            if (!string.IsNullOrWhiteSpace(shortDescription) && Regex.IsMatch(shortDescription.Trim().ToLowerInvariant(), pattern))
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
            // 1) Numeric + qualifier: "12 pack", "12-pack", "12pk", "12 ct", "12 count"
            var m = Regex.Match(lower, @"(\d+)\s*[-]?\s*(pack|pk|ct|count|pc|pcs|piece|pieces)\b");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > 0)
                return n;

            // 2) "pack/box/set/case of N"
            m = Regex.Match(lower, @"\b(pack|box|set|case)\s+of\s+(\d+)\b");
            if (m.Success && int.TryParse(m.Groups[2].Value, out n) && n > 0)
                return n;

            // 3) "Nx" or "xN" patterns (e.g. "12x", "x12") — only when N > 1
            m = Regex.Match(lower, @"\b(\d+)\s*x\b");
            if (m.Success && int.TryParse(m.Groups[1].Value, out n) && n > 1)
                return n;
            m = Regex.Match(lower, @"\bx\s*(\d+)\b");
            if (m.Success && int.TryParse(m.Groups[1].Value, out n) && n > 1)
                return n;

            // 4) Special phrases: "half dozen" → 6, "N dozen" / "<word> dozen" → N*12, bare "dozen" → 12
            if (Regex.IsMatch(lower, @"\bhalf\s+dozen\b"))
                return 6;
            var wordPattern = string.Join("|", WordToNumber.Keys);
            var dozenMatch = Regex.Match(lower, @"\b(\d+|" + wordPattern + @")\s+dozen\b");
            if (dozenMatch.Success)
            {
                var prefix = dozenMatch.Groups[1].Value;
                if (int.TryParse(prefix, out var dnum) && dnum > 0)
                    return dnum * 12;
                if (WordToNumber.TryGetValue(prefix, out var wnum))
                    return wnum * 12;
            }
            if (Regex.IsMatch(lower, @"\bdozen\b"))
                return 12;

            // 5) Word number + qualifier: "twelve pack", "twelve-pack", "twelve ct"
            m = Regex.Match(lower, @"\b(" + wordPattern + @")\s*[-]?\s*(pack|pk|ct|count|pc|pcs|piece|pieces)\b");
            if (m.Success && WordToNumber.TryGetValue(m.Groups[1].Value, out var wn) && wn > 0)
                return wn;

            // 6) "pack/box/set/case of <word>"
            m = Regex.Match(lower, @"\b(pack|box|set|case)\s+of\s+(" + wordPattern + @")\b");
            if (m.Success && WordToNumber.TryGetValue(m.Groups[2].Value, out wn) && wn > 0)
                return wn;
        }
        catch { }
        return null;
    }
}