using CartSmart.Core.Worker;
using Microsoft.Extensions.Logging;
using Supabase;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;

namespace CartSmart.Providers;

public class EbayStoreClient : IStoreClient, IVariantResolvingStoreClient
{
    private readonly HttpClient _http;
    private readonly ILogger<EbayStoreClient> _logger;
    private readonly IEbayAuthService _auth;
    private readonly IStopWordsProvider _stopWordsProvider;
    private readonly Client _supabase;

    private static readonly SemaphoreSlim _ebayPacingGate = new(1, 1);
    private static long _nextAllowedTickMs;

    private readonly ConcurrentDictionary<long, ProductVariantConfigIndex> _variantConfigCache = new();
    private readonly ConcurrentDictionary<long, long?> _defaultVariantIdCache = new();
    private readonly ConcurrentDictionary<string, string> _listingTextCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>?> _itemAspectsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, IReadOnlyList<string>> _productSearchAliasCache = new();
    private readonly ConcurrentDictionary<long, decimal?> _productMsrpCache = new();
    private readonly ConcurrentDictionary<long, decimal?> _productApiMinPriceCache = new();
    private readonly ConcurrentDictionary<long, IReadOnlyList<long>> _activeVariantIdsCache = new();

    // --- Failure cache & circuit breaker ---
    // Tracks item IDs that recently failed so we don't re-query them.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _itemFailureCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan _failureCacheTtl = TimeSpan.FromHours(
        double.TryParse(Environment.GetEnvironmentVariable("EBAY_ITEM_FAILURE_CACHE_HOURS"), NumberStyles.Any, CultureInfo.InvariantCulture, out var fch) && fch > 0 ? fch : 1);

    // Simple circuit breaker: after N consecutive API errors, stop calling for a cooldown period.
    private static int _consecutiveApiErrors;
    private static DateTimeOffset _circuitOpenUntil = DateTimeOffset.MinValue;
    private static readonly int _circuitBreakerThreshold = int.TryParse(Environment.GetEnvironmentVariable("EBAY_CIRCUIT_BREAKER_THRESHOLD"), out var cbt) && cbt > 0 ? cbt : 5;
    private static readonly TimeSpan _circuitBreakerCooldown = TimeSpan.FromMinutes(
        double.TryParse(Environment.GetEnvironmentVariable("EBAY_CIRCUIT_BREAKER_COOLDOWN_MINUTES"), NumberStyles.Any, CultureInfo.InvariantCulture, out var cbm) && cbm > 0 ? cbm : 10);

    public StoreType StoreType => StoreType.Ebay;
    public bool SupportsSoldStatus => true;
    public bool SupportsApi => true;

    public EbayStoreClient(HttpClient http, ILogger<EbayStoreClient> logger, IEbayAuthService auth, IStopWordsProvider stopWordsProvider, Client supabase)
    {
        _http = http;
        _logger = logger;
        _auth = auth;
        _stopWordsProvider = stopWordsProvider ?? throw new ArgumentNullException(nameof(stopWordsProvider));
        _supabase = supabase ?? throw new ArgumentNullException(nameof(supabase));
    }

    /// <summary>
    /// Attempts to resolve a <c>product_variant_id</c> for a listing using a product's known variant attribute enum values.
    /// This is intentionally conservative: if we can't identify a single variant unambiguously, returns null.
    /// </summary>
    public async Task<long?> TryResolveProductVariantIdAsync(long productId, NewListing listing, CancellationToken ct)
    {
        if (productId <= 0) return null;
        if (listing == null) return null;

        var config = await GetOrBuildProductVariantConfigIndexAsync(productId, ct);

        // If this product has no variant options configured, always use the default variant.
        if (config.EnumValueTokensByAttribute.Count == 0)
            return await GetDefaultVariantIdAsync(productId, ct);

        // Build a normalized search surface from title + item specifics (aspects) + listing page text (HTML)
        // Note: listing page fetch is lazy (only when needed), since it can be relatively expensive.
        var titleNorm = NormalizeComparable(listing.Title);

        var aspectValueNorms = new List<string>();
        if (listing.Aspects != null)
        {
            foreach (var kv in listing.Aspects)
            {
                if (kv.Value == null) continue;
                foreach (var v in kv.Value)
                {
                    var n = NormalizeComparable(v);
                    if (!string.IsNullOrWhiteSpace(n)) aspectValueNorms.Add(n);
                }
            }
        }

        // Search results don't always include item specifics (localizedAspects).
        // Pulling item details is expensive (one API call per listing), so keep this opt-in.
        var allowItemAspectFetch = bool.TryParse(Environment.GetEnvironmentVariable("EBAY_VARIANT_RESOLVE_FETCH_ITEM_ASPECTS"), out var fetchItemAspects)
            ? fetchItemAspects
            : false;
        if (allowItemAspectFetch && aspectValueNorms.Count == 0 && !string.IsNullOrWhiteSpace(listing.ItemId))
        {
            var fetchedAspects = await GetOrFetchItemAspectsAsync(listing.ItemId!, ct);
            if (fetchedAspects != null)
            {
                foreach (var kv in fetchedAspects)
                {
                    if (kv.Value == null) continue;
                    foreach (var v in kv.Value)
                    {
                        var n = NormalizeComparable(v);
                        if (!string.IsNullOrWhiteSpace(n)) aspectValueNorms.Add(n);
                    }
                }
            }
        }

        string? pageTextNorm = null;
        async Task<string> GetPageTextNormAsync()
        {
            if (pageTextNorm != null) return pageTextNorm;
            pageTextNorm = await GetOrFetchListingPageTextNormAsync(listing.Url, ct);
            return pageTextNorm;
        }

        Dictionary<int, int> DetectConstraints(string title, List<string> aspectValues, string? pageText)
        {
            var constraintsLocal = new Dictionary<int, int>();
            foreach (var (attributeId, enumValueIdToTokens) in config.EnumValueTokensByAttribute)
            {
                var bestScore = 0;
                var bestIds = new List<int>();

                foreach (var (enumValueId, tokens) in enumValueIdToTokens)
                {
                    var score = ScoreEnumCandidate(tokens, title, aspectValues, pageText);
                    if (score <= 0) continue;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIds.Clear();
                        bestIds.Add(enumValueId);
                    }
                    else if (score == bestScore)
                    {
                        bestIds.Add(enumValueId);
                    }
                }

                if (bestIds.Count == 1)
                {
                    constraintsLocal[attributeId] = bestIds[0];
                    continue;
                }

                if (bestIds.Count > 1)
                {
                    // Ambiguous for this attribute.
                    // Prefer to return no match rather than creating incorrect variants.
                    return new Dictionary<int, int>();
                }
            }

            return constraintsLocal;
        }

        // First pass: title + aspects.
        var constraints = DetectConstraints(titleNorm, aspectValueNorms, pageText: null);
        var allowPageTextFallback = bool.TryParse(Environment.GetEnvironmentVariable("EBAY_VARIANT_RESOLVE_USE_PAGE_TEXT"), out var usePageText)
            ? usePageText
            : false;
        if (constraints.Count == 0 && allowPageTextFallback)
        {
            // Second pass: include listing page text (item specifics + description HTML).
            var page = await GetPageTextNormAsync();
            constraints = DetectConstraints(titleNorm, aspectValueNorms, page);
        }

        if (constraints.Count == 0)
            return null;

        // Require all required attributes to be resolved.
        if (config.RequiredAttributeIds.Count > 0 && !config.RequiredAttributeIds.All(aid => constraints.ContainsKey(aid)))
            return null;

        // If the variant doesn't exist yet, create it.
        return await ResolveOrCreateVariantIdAsync(productId, constraints, config, ct);
    }

    private static int ScoreEnumCandidate(IReadOnlyList<string> tokens, string titleNorm, List<string> aspectValueNorms, string? pageTextNorm)
    {
        if (tokens == null || tokens.Count == 0) return 0;

        bool Matches(string hay)
        {
            if (string.IsNullOrWhiteSpace(hay)) return false;
            foreach (var token in tokens)
            {
                if (string.IsNullOrWhiteSpace(token)) continue;
                if (ContainsToken(hay, token)) return true;
            }
            return false;
        }

        var score = 0;
        if (Matches(titleNorm)) score += 2;
        if (aspectValueNorms != null && aspectValueNorms.Any(Matches)) score += 3;
        if (!string.IsNullOrWhiteSpace(pageTextNorm) && Matches(pageTextNorm)) score += 1;
        return score;
    }

    private async Task<string> GetOrFetchListingPageTextNormAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;

        if (_listingTextCache.TryGetValue(url, out var cached))
            return cached;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (CartSmart) AppleWebKit/537.36 (KHTML, like Gecko)");

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return string.Empty;

            var html = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(html))
                return string.Empty;

            // Drop script/style blocks, then strip tags.
            var cleaned = Regex.Replace(html, "<script[\\s\\S]*?</script>", " ", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "<style[\\s\\S]*?</style>", " ", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "<noscript[\\s\\S]*?</noscript>", " ", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "<[^>]+>", " ");
            cleaned = WebUtility.HtmlDecode(cleaned);

            // Limit size to keep memory bounded.
            if (cleaned.Length > 50_000)
                cleaned = cleaned.Substring(0, 50_000);

            var norm = NormalizeComparable(cleaned);
            _listingTextCache[url] = norm;
            return norm;
        }
        catch
        {
            return string.Empty;
        }
    }

    public async Task<bool> HasActiveVariantsAsync(long productId, CancellationToken ct)
    {
        if (productId <= 0) return false;
        var config = await GetOrBuildProductVariantConfigIndexAsync(productId, ct);
        return config.EnumValueTokensByAttribute.Count > 0;
    }

    public async Task<IReadOnlyList<long>> GetActiveVariantIdsAsync(long productId, CancellationToken ct)
    {
        if (productId <= 0) return Array.Empty<long>();
        if (_activeVariantIdsCache.TryGetValue(productId, out var cached))
            return cached;

        try
        {
            if (productId > int.MaxValue)
            {
                _activeVariantIdsCache[productId] = Array.Empty<long>();
                return Array.Empty<long>();
            }

            var resp = await _supabase
                .From<CartSmart.API.Models.ProductVariant>()
                .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, productId.ToString())
                .Filter("is_active", Supabase.Postgrest.Constants.Operator.Equals, "true")
                .Select("id")
                .Get(ct);

            var ids = (resp.Models ?? new List<CartSmart.API.Models.ProductVariant>())
                .Select(v => v.Id)
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            _activeVariantIdsCache[productId] = ids;
            return ids;
        }
        catch
        {
            _activeVariantIdsCache[productId] = Array.Empty<long>();
            return Array.Empty<long>();
        }
    }

    public async Task<StoreProductData?> GetByUrlAsync(string productUrl, CancellationToken ct)
    {
        try
        {
            // For refresh checks (sold / in-stock / price), prefer the API over HTML scraping.
            // If we can't get an itemId, attempt a lightweight GET to resolve redirects and re-extract.
            var itemId = ExtractItemId(productUrl);
            if (string.IsNullOrWhiteSpace(itemId))
            {
                try
                {
                    using var resp = await _http.GetAsync(productUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                    var finalUrl = resp.RequestMessage?.RequestUri?.ToString();
                    if (!string.IsNullOrWhiteSpace(finalUrl))
                        itemId = ExtractItemId(finalUrl);
                }
                catch
                {
                    // ignore
                }
            }

            if (string.IsNullOrWhiteSpace(itemId))
                return null;

            // Extract variation ID from the URL's ?var= query parameter.
            // Multi-variation eBay listings require this for the legacy ID endpoint.
            var variationId = ExtractVariationId(productUrl);

            // Circuit breaker: if eBay API is consistently failing, skip entirely.
            if (IsCircuitOpen())
            {
                _logger.LogWarning("eBay circuit breaker is open – skipping API call for itemId={ItemId}", itemId);
                return null;
            }

            // Per-item failure cache: skip items that failed recently.
            if (_itemFailureCache.TryGetValue(itemId, out var failedAt) && DateTimeOffset.UtcNow < failedAt + _failureCacheTtl)
            {
                _logger.LogDebug("Skipping eBay item {ItemId} – recently failed at {FailedAt}", itemId, failedAt);
                return null;
            }

            // Item IDs in listing URLs are legacy numeric IDs. The Browse API's /item/{itemId}
            // frequently expects the REST "itemId" (often shaped like v1|...|0). Use the
            // get_item_by_legacy_id endpoint for numeric IDs.
            var (item, status) = await GetItemByLegacyIdWithStatusAsync(itemId, variationId, ct);

            // 409 (Conflict / error 11004) = "item not available for purchase".
            // This is a DEFINITIVE signal that the listing has ended — do NOT fall back
            // to the direct endpoint, which can return stale IN_STOCK data.
            if (item == null && status == HttpStatusCode.Conflict)
            {
                _logger.LogInformation(
                    "Legacy endpoint returned 409 (item not available) for itemId={ItemId} – treating as ended.",
                    itemId);
                return new StoreProductData(null, null, false, true, true, DateTime.UtcNow);
            }

            if (item == null && (status == HttpStatusCode.NotFound || status == HttpStatusCode.BadRequest))
            {
                // Fall back to the direct /item/{itemId} endpoint on:
                //   404 – the legacy endpoint doesn't know the ID but the direct one might.
                //   400 – typically a multi-variation listing missing legacy_variation_id
                //          (error 11006). The direct endpoint returns the parent item.
                // Do NOT fall back on throttle/server errors to avoid doubling requests.
                _logger.LogInformation(
                    "Legacy endpoint returned {Status} for itemId={ItemId} – falling back to direct /item/ endpoint.",
                    status, itemId);
                (item, status) = await GetItemWithStatusAsync(itemId, ct);
            }

            // If both calls failed with a non-success status, cache the failure.
            // Exclude 404 (handled as Sold/Discontinued below) and 400 (item-specific, not transient).
            if (item == null
                && status != HttpStatusCode.NotFound
                && status != HttpStatusCode.BadRequest)
            {
                _itemFailureCache[itemId] = DateTimeOffset.UtcNow;
            }

            // 404 from both endpoints = item truly doesn't exist.
            if (item == null && status == HttpStatusCode.NotFound)
                return new StoreProductData(null, null, false, true, true, DateTime.UtcNow);
            if (item == null) return null;
            var price = item.price?.value;
            var currency = item.price?.currency;

            // Diagnostic logging: log key availability fields so we can diagnose
            // items that aren't transitioning to the correct status.
            var estStatus = item.estimatedAvailabilities?.FirstOrDefault()?.estimatedAvailabilityStatus;
            _logger.LogInformation(
                "eBay item response for itemId={ItemId}: price={Price}, itemEndDate={ItemEndDate}, " +
                "estimatedAvailabilityStatus={EstStatus}, itemState={ItemState}, " +
                "estimatedAvailCount={EstCount}, buyingOptions={BuyingOptions}, " +
                "eligibleForInlineCheckout={EligibleCheckout}, " +
                "availabilityStatus={AvailStatus}, nested.availabilityStatus={NestedAvailStatus}",
                item.itemId,
                price,
                item.itemEndDate,
                estStatus,
                item.itemState,
                item.estimatedAvailabilities?.Count ?? 0,
                item.buyingOptions != null ? string.Join(",", item.buyingOptions) : "(null)",
                item.eligibleForInlineCheckout,
                item.availabilityStatus,
                item.availability?.availabilityStatus);

            // Determine sold / in-stock based on eBay's availability fields.
            // NOTE: itemGroupType and seller feedback are not sold-status signals.
            var (inStock, soldFlag) = ComputeAvailability(item);
            return new StoreProductData(price, currency, inStock, soldFlag, false, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ebay fetch failed {Url}", productUrl);
            return null;
        }
    }

    private static (bool? InStock, bool Sold) ComputeAvailability(ItemResponse item)
    {
        if (item == null) return (null, false);

        // Sold should be reserved for listings that are actually ended/unavailable.
        // For active listings that are temporarily OOS, we prefer Sold=false and InStock=false.
        var sold = false;
        var state = item.itemState;
        if (!string.IsNullOrWhiteSpace(state))
        {
            // Common shapes seen across APIs.
            if (string.Equals(state, "ENDED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "INACTIVE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "EXPIRED", StringComparison.OrdinalIgnoreCase))
            {
                sold = true;
            }
        }

        if (!sold && item.itemEndDate.HasValue && item.itemEndDate.Value <= DateTimeOffset.UtcNow)
            sold = true;

        bool? inStock = null;

        // 1) Check estimatedAvailabilities (the actual field returned by the getItem / getItemByLegacyId endpoints).
        var estAvail = item.estimatedAvailabilities?.FirstOrDefault();
        if (estAvail != null)
        {
            var estStatus = estAvail.estimatedAvailabilityStatus;
            if (!string.IsNullOrWhiteSpace(estStatus))
            {
                var s = estStatus.Trim().ToUpperInvariant();
                if (s.Contains("OUT_OF_STOCK")) inStock = false;
                else if (s.Contains("IN_STOCK") || s.Contains("LIMITED")) inStock = true;
            }

            // Fall back to estimated quantity if status string was absent or unrecognised.
            if (!inStock.HasValue && estAvail.estimatedAvailableQuantity.HasValue)
                inStock = estAvail.estimatedAvailableQuantity.Value > 0;
        }

        // 2) Legacy fallback: top-level availabilityStatus / nested availability object
        //    (some older or alternative endpoints may still use these shapes).
        if (!inStock.HasValue)
        {
            var status = item.availabilityStatus ?? item.availability?.availabilityStatus;
            if (!string.IsNullOrWhiteSpace(status))
            {
                var s = status.Trim().ToUpperInvariant();
                if (s.Contains("IN_STOCK")) inStock = true;
                else if (s.Contains("OUT_OF_STOCK") || s.Contains("SOLD_OUT") || s.Contains("SOLDOUT")) inStock = false;
                else if (s.Contains("LIMITED")) inStock = true;
            }
        }

        // 3) Fall back to nested quantity if eBay provides it.
        if (!inStock.HasValue)
        {
            var qty = item.availability?.shipToLocationAvailability?.quantity;
            if (qty.HasValue) inStock = qty.Value > 0;
        }

        // 4) Safety net: if the API returned a valid response but we still have no
        //    availability signal at all, default to out of stock.
        //    Active in-stock eBay listings always return estimatedAvailabilities.
        //    The complete absence of all availability signals typically means the
        //    listing has ended, is unavailable, or is in a degraded state.
        if (!inStock.HasValue)
            inStock = false;

        // If we believe the listing is ended, it cannot be in stock.
        if (sold) inStock = false;

        return (inStock, sold);
    }

    private async Task<(ItemResponse? Item, HttpStatusCode Status)> GetItemWithStatusAsync(string itemId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return (null, HttpStatusCode.BadRequest);

        // The /item/{itemId} endpoint expects a REST-style ID (e.g. "v1|123456789|0"),
        // NOT a raw numeric legacy ID. If the caller passes a numeric ID (extracted from
        // an eBay listing URL), convert it to the expected format.
        var restItemId = itemId.All(char.IsDigit) ? $"v1|{itemId}|0" : itemId;

        // Use 0 retries for item lookups to avoid ballooning API requests.
        using var resp = await SendEbayAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"https://api.ebay.com/buy/browse/v1/item/{Uri.EscapeDataString(restItemId)}"),
            operation: "getItem",
            maxRetriesOverride: 0,
            ct);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("eBay item fetch failed: {Status} (itemId={ItemId})", resp.StatusCode, itemId);
            return (null, resp.StatusCode);
        }

        var item = await resp.Content.ReadFromJsonAsync<ItemResponse>(cancellationToken: ct);
        return (item, resp.StatusCode);
    }

    private async Task<(ItemResponse? Item, HttpStatusCode Status)> GetItemByLegacyIdWithStatusAsync(string legacyItemId, string? legacyVariationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(legacyItemId)) return (null, HttpStatusCode.BadRequest);

        // Build the query string. For multi-variation listings, eBay requires
        // legacy_variation_id — without it the endpoint returns an error.
        var url = $"https://api.ebay.com/buy/browse/v1/item/get_item_by_legacy_id?legacy_item_id={Uri.EscapeDataString(legacyItemId)}";
        if (!string.IsNullOrWhiteSpace(legacyVariationId))
            url += $"&legacy_variation_id={Uri.EscapeDataString(legacyVariationId)}";

        // Use 0 retries for item lookups to avoid ballooning API requests.
        using var resp = await SendEbayAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            operation: "getItemByLegacyId",
            maxRetriesOverride: 0,
            ct);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("eBay legacy item fetch failed: {Status} (legacyItemId={LegacyItemId})", resp.StatusCode, legacyItemId);
            return (null, resp.StatusCode);
        }

        var item = await resp.Content.ReadFromJsonAsync<ItemResponse>(cancellationToken: ct);
        return (item, resp.StatusCode);
    }

    public async Task<IReadOnlyList<CartSmart.Core.Worker.NewListing>> SearchNewListingsAsync(long productId, string query, int? preferredConditionCategoryId, CancellationToken ct)
    {
        // If MSRP is configured for the product, compute server-side price bounds
        // to filter out low-cost accessories and overpriced bundles at the API level.
        var productMsrp = await GetOrFetchProductMsrpAsync(productId, ct);

        // Check for a per-product api_min_price override (populated after the MSRP fetch).
        _productApiMinPriceCache.TryGetValue(productId, out var apiMinPriceOverride);

        decimal? apiMinPrice = null;
        decimal? apiMaxPrice = null;
        if (productMsrp.HasValue && productMsrp.Value > 0)
        {
            // Use the override if set, otherwise default to 40% of MSRP.
            apiMinPrice = apiMinPriceOverride ?? Math.Round(productMsrp.Value * 0.4m, 2);
            apiMaxPrice = productMsrp.Value;
        }
        else if (apiMinPriceOverride.HasValue)
        {
            // Override is set but no MSRP — still apply the minimum.
            apiMinPrice = apiMinPriceOverride.Value;
        }

        var sortOverride = Environment.GetEnvironmentVariable("EBAY_SEARCH_SORT") ?? "price";

        // Pull a single page of candidates to cap request volume.
        // We sort by lowest price so downstream selection can stop early.
        var items = await ExecuteSearchAsync(
            query,
            preferredConditionCategoryId,
            limitOverride: 200,
            sortOverride: sortOverride,
            minPrice: apiMinPrice,
            maxPrice: apiMaxPrice,
            ct);

        // Also search product aliases (alternate titles) – one API call per alias.
        var maxAliases = int.TryParse(Environment.GetEnvironmentVariable("EBAY_QUERY_ALIAS_LIMIT"), out var aliasLimit) && aliasLimit >= 0 && aliasLimit <= 20 ? aliasLimit : 5;
        if (productId > 0 && maxAliases > 0)
        {
            var aliases = await GetOrFetchProductSearchAliasesAsync(productId, ct);
            foreach (var alias in aliases.Take(maxAliases))
            {
                if (string.Equals(alias, query, StringComparison.OrdinalIgnoreCase))
                    continue; // skip duplicate of the primary query

                var aliasItems = await ExecuteSearchAsync(
                    alias,
                    preferredConditionCategoryId,
                    limitOverride: 200,
                    sortOverride: sortOverride,
                    minPrice: apiMinPrice,
                    maxPrice: apiMaxPrice,
                    ct);
                if (aliasItems.Count > 0)
                    items = items.Concat(aliasItems).ToList();
            }
        }

        if (items.Count == 0)
            return Array.Empty<CartSmart.Core.Worker.NewListing>();

        // Collect raw candidates keyed by itemId (dedup across primary + alias searches)
        var rawById = new Dictionary<string, ItemSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (!rawById.ContainsKey(item.itemId))
                rawById[item.itemId] = item;
        }

        // Stage B: verification – aggressively filter out wrong/low-quality matches

        // Accessory / non-core keywords (headcovers, chargers, mounts, etc.)
        var accessories = new[]
        {
            "case", "cover", "headcover", "head cover", "charger", "screen protector", "protector",
            "cable", "battery", "mount", "stand", "skin", "dock", "adapter", "shaft", "grip",
            "sleeve", "tip", "tool", "wrench", "weight", "weights", "screw", "screws"
        };

        var filtered = new List<CartSmart.Core.Worker.NewListing>();

        // Stop words for token normalization
        HashSet<string> stopWords = new(new[] { "the", "and", "with", "for", "of", "by", "to", "from", "new", "brand", "inch", "inches" }, StringComparer.OrdinalIgnoreCase);
        try
        {
            var words = await _stopWordsProvider.GetStopWordsAsync(ct);
            if (words != null && words.Count > 0)
                stopWords = new HashSet<string>(words, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // ignore and keep defaults
        }

        // Seller quality thresholds (can be overridden via environment variables)
        var minPct = decimal.TryParse(Environment.GetEnvironmentVariable("EBAY_MIN_FEEDBACK_PERCENT"), out var p) ? p : 95m;
        var minScore = int.TryParse(Environment.GetEnvironmentVariable("EBAY_MIN_FEEDBACK_SCORE"), out var sscore) ? sscore : 3;
        var requireTopRated = bool.TryParse(Environment.GetEnvironmentVariable("EBAY_REQUIRE_TOP_RATED"), out var rtr) ? rtr : false;

        // Pre-normalize query tokens for coverage checks
        var queryTokens = NormalizeTokens(query, stopWords);
        var queryPack = ParsePackInfo(query);

        foreach (var s in rawById.Values)
        {
            var title = s.title ?? string.Empty;
            var titleLower = title.ToLowerInvariant();
            var hasGtin = s.gtin != null && s.gtin.FirstOrDefault() != null;
            var hasBrandMpn = !string.IsNullOrWhiteSpace(s.brand) && !string.IsNullOrWhiteSpace(s.mpn);

            // Price filter: only keep listings strictly below MSRP (when MSRP is set).
            if (productMsrp.HasValue)
            {
                var currency = s.price?.currency;
                if (!string.IsNullOrWhiteSpace(currency) && !string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase))
                    continue;

                var listingPrice = s.price?.value;
                if (!listingPrice.HasValue)
                    continue;
                if (listingPrice.Value >= productMsrp.Value)
                    continue;
            }

            // Exclude obvious accessories/parts
            //if (accessories.Any(k => titleLower.Contains(k)))
            //    continue;

            // Pack / lot normalization: reject obvious pack-size mismatches
            var titlePack = ParsePackInfo(title);
            if (IsPackMismatch(queryPack, titlePack, titleLower))
                continue;

            // Token-based coverage for semantic title match
            var titleTokens = NormalizeTokens(title, stopWords);
            var coverage = Coverage(queryTokens, titleTokens);
            var strongTitleMatch = coverage >= 0.6;

            // Attempt to detect free shipping from summary if available
            bool? freeShipping = null;
            if (s.shippingOptions != null)
            {
                freeShipping = s.shippingOptions.Any(o => string.Equals(o.shippingCostType, "FREE", StringComparison.OrdinalIgnoreCase));
            }

            // Seller quality filter: established, highly rated
            var feedbackPct = s.seller?.feedbackPercentage ?? 0;
            var feedbackScore = s.seller?.feedbackScore ?? 0;
            var isTopRated = s.seller?.topRatedSeller == true;
            bool sellerOk = feedbackPct >= minPct && feedbackScore >= minScore && (!requireTopRated || isTopRated);
            if (!sellerOk)
                continue;

            // Simple scoring: prioritize structural matches, then title similarity, then penalize ambiguity
            double score = 0;
            if (hasGtin) score += 50;
            else if (hasBrandMpn) score += 40;

            score += Math.Min(40, coverage * 40.0); // up to +40 from title coverage

            if (queryPack.Quantity.HasValue && titlePack.Quantity.HasValue && !IsPackMismatch(queryPack, titlePack, titleLower))
                score += 10; // reward pack agreement

            if (titleLower.Contains("lot") || titleLower.Contains("assorted") || titleLower.Contains("variety") || titleLower.Contains("bulk"))
                score -= 15; // penalize ambiguous multi-item lots

            // If we have no strong structural signals, require a reasonable score
            if (!hasGtin && !hasBrandMpn && !strongTitleMatch)
                continue;

            if (!hasGtin && !hasBrandMpn && score < 30)
                continue;

            filtered.Add(new CartSmart.Core.Worker.NewListing(
                s.itemId,
                s.title,
                s.itemWebUrl,
                s.price?.value,
                s.price?.currency,
                s.gtin?.FirstOrDefault(),
                s.mpn,
                s.brand,
                MapConditionToCategory(s.conditionId),
                freeShipping,
                BuildAspects(s.localizedAspects)
            ));
        }

        // Optional: Search results frequently omit localizedAspects.
        // Enriching here can explode API call volume (N+1 item detail calls).
        // Variant resolution already fetches aspects lazily per candidate (and caches results), so keep this off by default.
        var enrichDuringSearch = bool.TryParse(Environment.GetEnvironmentVariable("EBAY_ENRICH_ASPECTS_DURING_SEARCH"), out var enrich) && enrich;
        if (enrichDuringSearch && filtered.Any(l => l.Aspects == null) && filtered.Count > 0)
        {
            var enriched = new List<CartSmart.Core.Worker.NewListing>(filtered.Count);
            foreach (var l in filtered)
            {
                if (l.Aspects != null)
                {
                    enriched.Add(l);
                    continue;
                }

                var fetched = await GetOrFetchItemAspectsAsync(l.ItemId, ct);
                enriched.Add(fetched != null ? l with { Aspects = fetched } : l);
            }
            return enriched;
        }

        // Keep deterministic low-price ordering for the orchestrator.
        return filtered
            .OrderBy(l => l.Price ?? decimal.MaxValue)
            .ToList();
    }

    private async Task<decimal?> GetOrFetchProductMsrpAsync(long productId, CancellationToken ct)
    {
        if (productId <= 0) return null;
        if (_productMsrpCache.TryGetValue(productId, out var cached))
            return cached;

        try
        {
            if (productId > int.MaxValue)
            {
                _productMsrpCache[productId] = null;
                _productApiMinPriceCache[productId] = null;
                return null;
            }

            var resp = await _supabase
                .From<CartSmart.API.Models.Product>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, productId.ToString())
                .Select("id, msrp, api_min_price")
                .Limit(1)
                .Get(ct);

            var product = resp.Models.FirstOrDefault();
            decimal? msrp = null;
            if (product?.MSRP is > 0)
                msrp = Convert.ToDecimal(product.MSRP.Value);

            decimal? apiMinOverride = null;
            if (product?.ApiMinPrice is > 0)
                apiMinOverride = Convert.ToDecimal(product.ApiMinPrice.Value);

            _productMsrpCache[productId] = msrp;
            _productApiMinPriceCache[productId] = apiMinOverride;
            return msrp;
        }
        catch
        {
            _productMsrpCache[productId] = null;
            _productApiMinPriceCache[productId] = null;
            return null;
        }
    }

    private async Task<IReadOnlyList<string>> BuildQueryVariantsForProductAsync(long productId, string query, CancellationToken ct)
    {
        var maxTotal = int.TryParse(Environment.GetEnvironmentVariable("EBAY_QUERY_VARIANT_LIMIT"), out var m) && m > 0 && m <= 20 ? m : 6;
        var maxAliases = int.TryParse(Environment.GetEnvironmentVariable("EBAY_QUERY_ALIAS_LIMIT"), out var a) && a >= 0 && a <= 20 ? a : 5;

        var variants = new List<string>();
        void AddVariant(string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return;
            if (!variants.Any(v => v.Equals(q, StringComparison.OrdinalIgnoreCase)))
                variants.Add(q);
        }

        foreach (var v in BuildQueryVariants(query, maxTotal))
            AddVariant(v);

        if (productId > 0 && maxAliases > 0)
        {
            var aliases = await GetOrFetchProductSearchAliasesAsync(productId, ct);
            foreach (var alias in aliases.Take(maxAliases))
            {
                foreach (var v in BuildQueryVariants(alias, maxTotal))
                    AddVariant(v);
            }
        }

        return variants.Take(maxTotal).ToList();
    }

    private async Task<IReadOnlyList<string>> GetOrFetchProductSearchAliasesAsync(long productId, CancellationToken ct)
    {
        if (productId <= 0) return Array.Empty<string>();
        if (_productSearchAliasCache.TryGetValue(productId, out var cached))
            return cached;

        try
        {
            var resp = await _supabase
                .From<CartSmart.API.Models.ProductSearchAlias>()
                .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, productId.ToString())
                .Filter("is_active", Supabase.Postgrest.Constants.Operator.Equals, "true")
                .Select("alias")
                .Get(ct);

            var aliases = (resp.Models ?? new List<CartSmart.API.Models.ProductSearchAlias>())
                .Select(x => (x.Alias ?? string.Empty).Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(50)
                .ToList();

            _productSearchAliasCache[productId] = aliases;
            return aliases;
        }
        catch
        {
            _productSearchAliasCache[productId] = Array.Empty<string>();
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>>? BuildAspects(List<LocalizedAspect>? aspects)
    {
        if (aspects == null || aspects.Count == 0) return null;

        var dict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in aspects)
        {
            var k = (a?.name ?? string.Empty).Trim();
            var v = (a?.value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(k) || string.IsNullOrWhiteSpace(v)) continue;
            if (!dict.TryGetValue(k, out var list))
            {
                list = new List<string>();
                dict[k] = list;
            }
            if (!list.Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase)))
                list.Add(v);
        }

        if (dict.Count == 0) return null;
        return dict.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<ProductVariantConfigIndex> GetOrBuildProductVariantConfigIndexAsync(long productId, CancellationToken ct)
    {
        if (_variantConfigCache.TryGetValue(productId, out var cached))
            return cached;

        if (productId > int.MaxValue)
        {
            var empty = new ProductVariantConfigIndex(
                new Dictionary<int, Dictionary<int, List<string>>>(),
                new HashSet<int>(),
                new Dictionary<int, string>());
            _variantConfigCache[productId] = empty;
            return empty;
        }

        // 1) Determine which attributes apply for this product.
        var paResp = await _supabase
            .From<CartSmart.API.Models.ProductAttribute>()
            .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, productId.ToString())
            .Select("product_id, attribute_id, is_required")
            .Get(ct);
        var productAttributes = paResp.Models ?? new List<CartSmart.API.Models.ProductAttribute>();
        var attributeIds = productAttributes
            .Select(x => x.AttributeId)
            .Distinct()
            .ToList();

        if (attributeIds.Count == 0)
        {
            var empty = new ProductVariantConfigIndex(
                new Dictionary<int, Dictionary<int, List<string>>>(),
                new HashSet<int>(),
                new Dictionary<int, string>());
            _variantConfigCache[productId] = empty;
            return empty;
        }

        // 2) Load attribute definitions so we can limit to enum attributes.
        var attributeIdObjects = attributeIds.Cast<object>().ToArray();
        var attrResp = await _supabase
            .From<global::CartSmart.API.Models.Attribute>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.In, attributeIdObjects)
            .Select("id, data_type")
            .Get(ct);
        var attrs = attrResp.Models ?? new List<global::CartSmart.API.Models.Attribute>();

        var enumAttributeIds = attrs
            .Where(a => string.Equals(a.DataType, "enum", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Id)
            .Distinct()
            .ToList();

        if (enumAttributeIds.Count == 0)
        {
            var empty = new ProductVariantConfigIndex(
                new Dictionary<int, Dictionary<int, List<string>>>(),
                new HashSet<int>(),
                new Dictionary<int, string>());
            _variantConfigCache[productId] = empty;
            return empty;
        }

        var enumAttributeIdObjects = enumAttributeIds.Cast<object>().ToArray();

        // 3) Load enabled enum options for these attributes.
        var enumResp = await _supabase
            .From<CartSmart.API.Models.AttributeEnumValue>()
            .Filter("attribute_id", Supabase.Postgrest.Constants.Operator.In, enumAttributeIdObjects)
            .Filter("is_active", Supabase.Postgrest.Constants.Operator.Equals, "true")
            .Select("id, attribute_id, display_name, enum_key")
            .Get(ct);
        var enums = enumResp.Models ?? new List<CartSmart.API.Models.AttributeEnumValue>();

        // 3b) Remove enums disabled for this product.
        var disabledResp = await _supabase
            .From<CartSmart.API.Models.ProductAttributeEnumDisabled>()
            .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, productId.ToString())
            .Filter("attribute_id", Supabase.Postgrest.Constants.Operator.In, enumAttributeIdObjects)
            .Select("product_id, attribute_id, enum_value_id")
            .Get(ct);
        var disabled = disabledResp.Models ?? new List<CartSmart.API.Models.ProductAttributeEnumDisabled>();
        var disabledEnumIds = disabled.Select(x => x.EnumValueId).ToHashSet();
        if (disabledEnumIds.Count > 0)
        {
            enums = enums.Where(ev => !disabledEnumIds.Contains(ev.Id)).ToList();
        }

        // 3c) Load per-product synonyms for these enum attributes.
        // These are additional tokens used during enum value matching in listing text.
        var synonymsByEnumValueId = new Dictionary<int, List<string>>();
        try
        {
            var synResp = await _supabase
                .From<CartSmart.API.Models.ProductAttributeEnumSynonym>()
                .Filter("attribute_id", Supabase.Postgrest.Constants.Operator.In, enumAttributeIdObjects)
                .Select("enum_value_id, synonym, is_active")
                .Get(ct);

            var synRows = (synResp.Models ?? new List<CartSmart.API.Models.ProductAttributeEnumSynonym>())
                .Where(s => s.IsActive)
                .ToList();

            if (synRows.Count > 0)
            {
                synonymsByEnumValueId = synRows
                    .GroupBy(s => s.EnumValueId)
                    .ToDictionary(
                        g => g.Key,
                        g => g
                            .Select(s => (s.Synonym ?? string.Empty).Trim())
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(25)
                            .ToList());
            }
        }
        catch
        {
            // Keep non-fatal; tokens will fall back to enum display_name/enum_key only.
            synonymsByEnumValueId = new Dictionary<int, List<string>>();
        }

        // attribute_id -> enum_value_id -> tokens
        var tokensByAttribute = new Dictionary<int, Dictionary<int, List<string>>>();
        var displayByEnumId = new Dictionary<int, string>();
        foreach (var ev in enums)
        {
            displayByEnumId[ev.Id] = ev.DisplayName ?? string.Empty;

            var tokenSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dn = NormalizeComparable(ev.DisplayName);
            var ek = NormalizeComparable(ev.EnumKey);
            if (!string.IsNullOrWhiteSpace(dn)) tokenSet.Add(dn);
            if (!string.IsNullOrWhiteSpace(ek)) tokenSet.Add(ek);

            // Common normalization helpers: allow matching "256 gb" vs "256gb"
            if (!string.IsNullOrWhiteSpace(ev.DisplayName))
            {
                var compact = NormalizeComparable(ev.DisplayName).Replace("-", string.Empty);
                if (!string.IsNullOrWhiteSpace(compact)) tokenSet.Add(compact);
            }

            if (!tokensByAttribute.TryGetValue(ev.AttributeId, out var perEnum))
            {
                perEnum = new Dictionary<int, List<string>>();
                tokensByAttribute[ev.AttributeId] = perEnum;
            }

            if (synonymsByEnumValueId.TryGetValue(ev.Id, out var syns) && syns != null)
            {
                foreach (var syn in syns)
                {
                    var norm = NormalizeComparable(syn);
                    if (!string.IsNullOrWhiteSpace(norm)) tokenSet.Add(norm);

                    // Compact helper: allow matching "1-dozen" vs "1dozen" in normalized listing surface.
                    var compact = norm.Replace("-", string.Empty);
                    if (!string.IsNullOrWhiteSpace(compact)) tokenSet.Add(compact);
                }
            }

            perEnum[ev.Id] = tokenSet.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        }

        var required = productAttributes
            .Where(pa => pa.IsRequired && enumAttributeIds.Contains(pa.AttributeId))
            .Select(pa => pa.AttributeId)
            .Distinct()
            .ToHashSet();

        var built = new ProductVariantConfigIndex(tokensByAttribute, required, displayByEnumId);
        _variantConfigCache[productId] = built;
        return built;
    }

    private async Task<long?> GetDefaultVariantIdAsync(long productId, CancellationToken ct)
    {
        if (_defaultVariantIdCache.TryGetValue(productId, out var cached))
            return cached;

        var resp = await _supabase
            .From<CartSmart.API.Models.ProductVariant>()
            .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, productId.ToString())
            .Filter("is_default", Supabase.Postgrest.Constants.Operator.Equals, "true")
            .Filter("is_active", Supabase.Postgrest.Constants.Operator.Equals, "true")
            .Select("id")
            .Limit(1)
            .Get(ct);
        var id = resp.Models?.FirstOrDefault()?.Id;
        _defaultVariantIdCache[productId] = id;
        return id;
    }

    private async Task<long?> ResolveOrCreateVariantIdAsync(long productId, Dictionary<int, int> attributeToEnumValueId, ProductVariantConfigIndex config, CancellationToken ct)
    {
        // Load existing active variants.
        var variantsResp = await _supabase
            .From<CartSmart.API.Models.ProductVariant>()
            .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, productId.ToString())
            .Filter("is_active", Supabase.Postgrest.Constants.Operator.Equals, "true")
            .Select("id")
            .Get(ct);

        var variants = variantsResp.Models ?? new List<CartSmart.API.Models.ProductVariant>();
        var variantIds = variants.Select(v => v.Id).Distinct().ToList();

        if (variantIds.Count > 0)
        {
            var pvaResp = await _supabase
                .From<CartSmart.API.Models.ProductVariantAttribute>()
                .Filter("product_variant_id", Supabase.Postgrest.Constants.Operator.In, variantIds.ToList())
                .Select("product_variant_id, attribute_id, enum_value_id")
                .Get(ct);
            var pvas = pvaResp.Models ?? new List<CartSmart.API.Models.ProductVariantAttribute>();

            // variant_id -> (attribute_id -> enum_value_id)
            var perVariant = new Dictionary<long, Dictionary<int, int>>();
            foreach (var row in pvas)
            {
                if (!row.EnumValueId.HasValue) continue;
                if (!perVariant.TryGetValue(row.ProductVariantId, out var map))
                {
                    map = new Dictionary<int, int>();
                    perVariant[row.ProductVariantId] = map;
                }
                map[row.AttributeId] = row.EnumValueId.Value;
            }

            foreach (var variantId in variantIds)
            {
                var map = perVariant.TryGetValue(variantId, out var m) ? m : new Dictionary<int, int>();
                if (map.Count != attributeToEnumValueId.Count) continue;

                var match = true;
                foreach (var kvp in attributeToEnumValueId)
                {
                    if (!map.TryGetValue(kvp.Key, out var evId) || evId != kvp.Value)
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                    return variantId;
            }
        }

        // Create a new variant for this enum combination.
        var now = DateTime.UtcNow;
        var displayName = BuildVariantDisplayName(attributeToEnumValueId, config);
        var normalizedTitle = NormalizeTitleForDb(displayName);

        var newVariant = new CartSmart.API.Models.ProductVariant
        {
            ProductId = productId,
            VariantName = null,
            UnitCount = null,
            UnitType = null,
            DisplayName = displayName,
            NormalizedTitle = normalizedTitle,
            IsDefault = false,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        var insertedResp = await _supabase.From<CartSmart.API.Models.ProductVariant>().Insert(newVariant);
        var inserted = insertedResp.Models?.FirstOrDefault();
        if (inserted == null)
            return null;

        var createdVariantId = inserted.Id;
        foreach (var kvp in attributeToEnumValueId)
        {
            await _supabase.From<CartSmart.API.Models.ProductVariantAttribute>().Insert(new CartSmart.API.Models.ProductVariantAttribute
            {
                ProductVariantId = createdVariantId,
                AttributeId = kvp.Key,
                EnumValueId = kvp.Value,
                ValueNum = null,
                ValueText = null,
                ValueBool = null
            });
        }

        // Invalidate caches for this product so future calls see the new variant.
        _variantConfigCache.TryRemove(productId, out _);
        _defaultVariantIdCache.TryRemove(productId, out _);

        return createdVariantId;
    }

    private static string NormalizeComparable(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var lower = s.Trim().ToLowerInvariant();
        var sb = new StringBuilder(lower.Length);
        foreach (var ch in lower)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch == '.') sb.Append('.'); // preserve decimals like 10.5
            else if (char.IsWhiteSpace(ch) || ch == '-' || ch == '_') sb.Append('-');
            // else drop
        }
        var outStr = sb.ToString();
        outStr = Regex.Replace(outStr, "-+", "-");
        return outStr.Trim('-');
    }

    private static bool ContainsToken(string haystackNormalized, string needleTokenNormalized)
    {
        if (string.IsNullOrWhiteSpace(haystackNormalized) || string.IsNullOrWhiteSpace(needleTokenNormalized))
            return false;
        // Normalize contains check with hyphen boundaries
        if (haystackNormalized.Equals(needleTokenNormalized, StringComparison.OrdinalIgnoreCase)) return true;
        return haystackNormalized.Contains(needleTokenNormalized, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTitleForDb(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var lower = value.Trim().ToLowerInvariant();
        var chars = lower
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray();
        var cleaned = new string(chars);
        while (cleaned.Contains("  ")) cleaned = cleaned.Replace("  ", " ");
        return cleaned.Trim();
    }

    private static string BuildVariantDisplayName(Dictionary<int, int> attributeToEnumValueId, ProductVariantConfigIndex config)
    {
        if (attributeToEnumValueId == null || attributeToEnumValueId.Count == 0)
            return "Variant";

        var parts = attributeToEnumValueId
            .OrderBy(k => k.Key)
            .Select(kvp => config.EnumValueDisplayNameById.TryGetValue(kvp.Value, out var dn) && !string.IsNullOrWhiteSpace(dn)
                ? dn.Trim()
                : kvp.Value.ToString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        return parts.Count > 0 ? string.Join(" / ", parts) : "Variant";
    }

    private sealed record ProductVariantConfigIndex(
        Dictionary<int, Dictionary<int, List<string>>> EnumValueTokensByAttribute,
        HashSet<int> RequiredAttributeIds,
        Dictionary<int, string> EnumValueDisplayNameById);

    private static bool IsRetryableStatus(HttpStatusCode status)
    {
        return status == HttpStatusCode.TooManyRequests
            || status == HttpStatusCode.RequestTimeout
            || status == HttpStatusCode.BadGateway
            || status == HttpStatusCode.ServiceUnavailable
            || status == HttpStatusCode.GatewayTimeout;
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage resp, int attempt)
    {
        if (resp.Headers.RetryAfter?.Delta != null)
            return resp.Headers.RetryAfter.Delta.Value;

        if (resp.Headers.RetryAfter?.Date != null)
        {
            var delta = resp.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
            if (delta > TimeSpan.Zero)
                return delta;
        }

        var baseMs = int.TryParse(Environment.GetEnvironmentVariable("EBAY_API_RETRY_BASE_DELAY_MS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) && b > 0
            ? b
            : 500;
        var maxMs = int.TryParse(Environment.GetEnvironmentVariable("EBAY_API_RETRY_MAX_DELAY_MS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && m > 0
            ? m
            : 10_000;

        var pow = Math.Min(6, Math.Max(0, attempt));
        var raw = baseMs * (int)Math.Pow(2, pow);
        var jitter = Random.Shared.Next(0, Math.Max(1, baseMs));
        var ms = Math.Min(maxMs, raw + jitter);
        return TimeSpan.FromMilliseconds(ms);
    }

    private static async Task PaceEbayAsync(CancellationToken ct)
    {
        var minDelayMs = int.TryParse(Environment.GetEnvironmentVariable("EBAY_API_MIN_DELAY_MS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) && d >= 0
            ? d
            : 0;
        if (minDelayMs <= 0)
            return;

        await _ebayPacingGate.WaitAsync(ct);
        try
        {
            var now = Environment.TickCount64;
            var next = Interlocked.Read(ref _nextAllowedTickMs);
            var waitMs = next - now;
            if (waitMs > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct);

            Interlocked.Exchange(ref _nextAllowedTickMs, Environment.TickCount64 + minDelayMs);
        }
        finally
        {
            _ebayPacingGate.Release();
        }
    }

    private Task<HttpResponseMessage> SendEbayAsync(Func<HttpRequestMessage> buildRequest, string operation, CancellationToken ct)
        => SendEbayAsync(buildRequest, operation, maxRetriesOverride: null, ct);

    private async Task<HttpResponseMessage> SendEbayAsync(Func<HttpRequestMessage> buildRequest, string operation, int? maxRetriesOverride, CancellationToken ct)
    {
        var maxRetries = maxRetriesOverride
            ?? (int.TryParse(Environment.GetEnvironmentVariable("EBAY_API_MAX_RETRIES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) && r >= 0
                ? r
                : 3);

        for (var attempt = 0; ; attempt++)
        {
            using var req = buildRequest();
            if (!req.Headers.Contains("X-EBAY-C-MARKETPLACE-ID"))
                req.Headers.Add("X-EBAY-C-MARKETPLACE-ID", "EBAY_US");

            var token = await _auth.GetAccessTokenAsync(ct);
            if (!string.IsNullOrEmpty(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            await PaceEbayAsync(ct);

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, ct);
            }
            catch (HttpRequestException ex) when (attempt < maxRetries)
            {
                var delay = TimeSpan.FromMilliseconds(500);
                _logger.LogWarning(ex, "eBay request failed (op={Operation}, attempt={Attempt}/{Max}). Retrying after {Delay}.", operation, attempt + 1, maxRetries + 1, delay);
                await Task.Delay(delay, ct);
                continue;
            }

            if (resp.IsSuccessStatusCode)
            {
                // Reset circuit breaker on success.
                Interlocked.Exchange(ref _consecutiveApiErrors, 0);
                return resp;
            }

            if (IsRetryableStatus(resp.StatusCode) && attempt < maxRetries)
            {
                var delay = GetRetryDelay(resp, attempt);
                _logger.LogWarning("eBay throttled/transient error (op={Operation}, status={Status}, attempt={Attempt}/{Max}). Retrying after {Delay}.", operation, resp.StatusCode, attempt + 1, maxRetries + 1, delay);
                resp.Dispose();
                await Task.Delay(delay, ct);
                continue;
            }

            // Track consecutive failures for circuit breaker.
            // Only count server-side errors (5xx) and throttling (429) — these indicate
            // an API-wide issue. Client errors like 400 (BadRequest), 404 (NotFound),
            // and 409 (Conflict) are per-item issues and should NOT trip the breaker.
            var sc = (int)resp.StatusCode;
            if (sc >= 500 || resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var errors = Interlocked.Increment(ref _consecutiveApiErrors);
                if (errors >= _circuitBreakerThreshold)
                {
                    _circuitOpenUntil = DateTimeOffset.UtcNow + _circuitBreakerCooldown;
                    _logger.LogError(
                        "eBay circuit breaker OPEN after {Errors} consecutive errors. Cooling down until {Until}.",
                        errors, _circuitOpenUntil);
                }
            }
            else if (sc < 500)
            {
                // A client error (4xx) means the API itself is healthy — reset the breaker.
                Interlocked.Exchange(ref _consecutiveApiErrors, 0);
            }

            return resp;
        }
    }

    private static bool IsCircuitOpen()
    {
        if (Interlocked.CompareExchange(ref _consecutiveApiErrors, 0, 0) < _circuitBreakerThreshold)
            return false;
        if (DateTimeOffset.UtcNow >= _circuitOpenUntil)
        {
            // Cooldown elapsed – allow a probe request and reset.
            Interlocked.Exchange(ref _consecutiveApiErrors, 0);
            return false;
        }
        return true;
    }

    private async Task<ItemResponse?> GetItemAsync(string itemId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return null;

        using var resp = await SendEbayAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"https://api.ebay.com/buy/browse/v1/item/{Uri.EscapeDataString(itemId)}"),
            operation: "getItem",
            ct);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("eBay item fetch failed: {Status}", resp.StatusCode);
            return null;
        }

        return await resp.Content.ReadFromJsonAsync<ItemResponse>(cancellationToken: ct);
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?> GetOrFetchItemAspectsAsync(string itemId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return null;
        if (_itemAspectsCache.TryGetValue(itemId, out var cached))
            return cached;

        try
        {
            var item = await GetItemAsync(itemId, ct);
            var aspects = BuildAspects(item?.localizedAspects);
            _itemAspectsCache[itemId] = aspects;
            return aspects;
        }
        catch
        {
            _itemAspectsCache[itemId] = null;
            return null;
        }
    }

    // Execute a single eBay search call for a specific query variant
    private async Task<IReadOnlyList<ItemSummary>> ExecuteSearchAsync(string query, int? preferredConditionCategoryId, int? limitOverride, string? sortOverride, decimal? minPrice, decimal? maxPrice, CancellationToken ct)
    {
        // Base filter for Buy It Now (FIXED_PRICE) and acceptable conditions
        string BuildConditionFilter(int? category)
        {
            if (!category.HasValue)
                return "conditionIds:{1000|1500|2000|2500|3000|4000|5000|6000}"; // broad: new/open-box/refurb/used family
            var ids = category.Value switch
            {
                1 => new[] { 1000 }, // New
                3 => new[] { 2000, 2500 }, // Refurbished
                2 => new[] { 3000, 4000, 5000, 6000 }, // Used family
                _ => Array.Empty<int>()
            };
            return ids.Length > 0 ? $"conditionIds:{{{string.Join('|', ids)}}}" : string.Empty;
        }

        var condPart = BuildConditionFilter(preferredConditionCategoryId);
        var filter = string.IsNullOrEmpty(condPart) ? "buyingOptions:{FIXED_PRICE}" : $"buyingOptions:{{FIXED_PRICE}},{condPart}";

        // Server-side price filter: reduce junk results (accessories, overpriced bundles)
        // eBay Browse API format: price:[min..max],priceCurrency:USD
        if (minPrice.HasValue || maxPrice.HasValue)
        {
            var minPart = minPrice.HasValue ? minPrice.Value.ToString("F2", CultureInfo.InvariantCulture) : string.Empty;
            var maxPart = maxPrice.HasValue ? maxPrice.Value.ToString("F2", CultureInfo.InvariantCulture) : string.Empty;
            filter += $",price:[{minPart}..{maxPart}],priceCurrency:USD";
        }

        // Optional category locking via environment variable (comma-separated category IDs)
        var categoryIdsRaw = Environment.GetEnvironmentVariable("EBAY_CATEGORY_IDS");
        var configuredLimit = int.TryParse(Environment.GetEnvironmentVariable("EBAY_SEARCH_LIMIT"), out var parsedLimit) && parsedLimit > 0 && parsedLimit <= 200
            ? parsedLimit
            : 100;
        var limit = limitOverride.HasValue && limitOverride.Value > 0
            ? Math.Min(200, limitOverride.Value)
            : configuredLimit;

        var sb = new StringBuilder();
        sb.Append("q=");
        sb.Append(Uri.EscapeDataString(query));
        sb.Append("&filter=");
        sb.Append(Uri.EscapeDataString(filter));
        sb.Append("&limit=");
        sb.Append(limit.ToString());

        // Optional lookback: keep only listings created within the last N minutes.
        int? lookbackMinutes = int.TryParse(Environment.GetEnvironmentVariable("EBAY_SEARCH_LOOKBACK_MINUTES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lm) && lm > 0
            ? lm
            : (int?)null;

        var sort = (sortOverride ?? Environment.GetEnvironmentVariable("EBAY_SEARCH_SORT"))?.Trim();
        if (string.IsNullOrWhiteSpace(sort))
            sort = "price";

        // Only add sort if a value is provided.
        // Common values: "price", "newlyListed" (eBay Browse API)
        if (!string.IsNullOrWhiteSpace(sort))
        {
            sb.Append("&sort=");
            sb.Append(Uri.EscapeDataString(sort));
        }
        if (!string.IsNullOrWhiteSpace(categoryIdsRaw))
        {
            sb.Append("&category_ids=");
            sb.Append(Uri.EscapeDataString(categoryIdsRaw));
        }

        var url = $"https://api.ebay.com/buy/browse/v1/item_summary/search?{sb}";
        using var resp = await SendEbayAsync(() => new HttpRequestMessage(HttpMethod.Get, url), operation: "search", ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("eBay search failed: {Status}", resp.StatusCode);
            return Array.Empty<ItemSummary>();
        }
        var json = await resp.Content.ReadFromJsonAsync<ItemSummaryResponse>(cancellationToken: ct);

        var items = json?.itemSummaries ?? new List<ItemSummary>();
        if (lookbackMinutes.HasValue)
        {
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-lookbackMinutes.Value);
            items = items
                .Where(i => i?.itemCreationDate != null && i.itemCreationDate.Value >= cutoff)
                .ToList();
        }
        return items;
    }

    // Back-compat wrapper for older call sites in this file
    private Task<IReadOnlyList<ItemSummary>> ExecuteSearchAsync(string query, int? preferredConditionCategoryId, CancellationToken ct)
        => ExecuteSearchAsync(query, preferredConditionCategoryId, limitOverride: null, sortOverride: null, minPrice: null, maxPrice: null, ct);

    // Build 2-4 query variants from a canonical product query
    private static IReadOnlyList<string> BuildQueryVariants(string query, int maxVariants)
    {
        var trimmed = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return Array.Empty<string>();

        var variants = new List<string>();
        void AddVariant(string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return;
            if (!variants.Any(v => v.Equals(q, StringComparison.OrdinalIgnoreCase)))
                variants.Add(q);
        }

        static string NormalizeSpaces(string s) => Regex.Replace((s ?? string.Empty).Trim(), "\\s+", " ");

        // Common formatting/spelling variants (e.g., Mevo+ vs Mevo Plus)
        string? PlusToWord(string s)
        {
            if (string.IsNullOrWhiteSpace(s) || !s.Contains('+')) return null;
            var r = Regex.Replace(s, "\\s*\\+\\s*", " plus ", RegexOptions.IgnoreCase);
            return NormalizeSpaces(r);
        }
        string? WordToPlus(string s)
        {
            if (string.IsNullOrWhiteSpace(s) || !Regex.IsMatch(s, "\\bplus\\b", RegexOptions.IgnoreCase)) return null;
            var r = Regex.Replace(s, "\\bplus\\b", "+", RegexOptions.IgnoreCase);
            r = Regex.Replace(r, "\\s*\\+\\s*", "+", RegexOptions.IgnoreCase);
            return NormalizeSpaces(r);
        }

        AddVariant(trimmed);

        var plusWord = PlusToWord(trimmed);
        if (!string.IsNullOrWhiteSpace(plusWord)) AddVariant(plusWord);
        var plusSym = WordToPlus(trimmed);
        if (!string.IsNullOrWhiteSpace(plusSym)) AddVariant(plusSym);

        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length >= 2)
        {
            var brand = tokens[0];
            var rest = string.Join(' ', tokens.Skip(1));
            if (!string.IsNullOrWhiteSpace(rest))
            {
                // Brand + quoted model phrase
                AddVariant($"{brand} \"{rest}\"");
            }
        }
 /*
        var lower = trimmed.ToLowerInvariant();
        // Simple pack-size variants for things like "dozen" vs "12"
       
        if (lower.Contains("dozen") || lower.Contains(" 12 ") || lower.EndsWith(" 12") || lower.StartsWith("12 "))
        {
            if (!lower.Contains("dozen"))
                AddVariant(trimmed + " dozen");
            if (!lower.Contains(" 12"))
                AddVariant(trimmed + " 12");
        }
        */

        // If the query looks like a UPC/GTIN, also try digits-only search
        var digitsOnly = new string(trimmed.Where(char.IsDigit).ToArray());
        if (digitsOnly.Length >= 10 && digitsOnly.Length <= 14)
        {
            AddVariant(digitsOnly);
        }

        // Hard cap to keep 2-4 variants
        var cap = maxVariants > 0 ? maxVariants : 4;
        return variants.Take(cap).ToList();
    }

    private static (int? Quantity, bool IsLot, bool IsAssorted) ParsePackInfo(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, false, false);

        var lower = text.ToLowerInvariant();
        bool isLot = lower.Contains("lot ") || lower.Contains(" lot");
        bool isAssorted = lower.Contains("assorted") || lower.Contains("variety") || lower.Contains("mix");

        int? qty = null;
        try
        {
            var m = Regex.Match(lower, @"(\d+)\s*(pack|pk|ct|count|pc|pcs)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
            {
                qty = n;
            }
            else if (lower.Contains("dozen"))
            {
                qty = 12;
            }
        }
        catch
        {
            // ignore parsing issues
        }

        return (qty, isLot, isAssorted);
    }

    private static bool IsPackMismatch((int? Quantity, bool IsLot, bool IsAssorted) queryPack,
                                       (int? Quantity, bool IsLot, bool IsAssorted) titlePack,
                                       string titleLower)
    {
        // If query specifies a pack size and the title clearly refers to a bulk/lot/assorted listing, treat as mismatch
        if (queryPack.Quantity.HasValue && (titlePack.IsLot || titlePack.IsAssorted ||
                                            titleLower.Contains("lot") || titleLower.Contains("assorted") || titleLower.Contains("variety")))
        {
            if (!titlePack.Quantity.HasValue)
                return true;
        }

        if (queryPack.Quantity.HasValue && titlePack.Quantity.HasValue)
        {
            var q = queryPack.Quantity.Value;
            var t = titlePack.Quantity.Value;
            if (q > 0 && t > 0)
            {
                var ratio = t >= q ? (double)t / q : (double)q / t;
                if (ratio > 2.5) // e.g., 1-pack vs 3+ pack, or dozen vs 36, etc.
                    return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> NormalizeTokens(string text, HashSet<string> stopWords)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var s = text.ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }
        var raw = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        static string Norm(string t) => t switch { "ps5" => "playstation5", "tv" => "television", _ => t };
        return raw.Select(Norm).Where(t => !string.IsNullOrWhiteSpace(t) && !stopWords.Contains(t)).Distinct();
    }

    private static double Coverage(IEnumerable<string> productTokens, IEnumerable<string> listingTokens)
    {
        var setProduct = productTokens.ToHashSet();
        var setListing = listingTokens.ToHashSet();
        if (setProduct.Count == 0 || setListing.Count == 0) return 0.0;
        var inter = setProduct.Intersect(setListing).Count();
        return (double)inter / (double)setProduct.Count;
    }

    private static string? ExtractItemId(string url)
    {
        try
        {
            var u = new Uri(url);
            var parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var idx = Array.IndexOf(parts, "itm");
            if (idx >= 0)
            {
                // Common shapes:
                // - /itm/1234567890
                // - /itm/title-slug/1234567890
                if (idx + 1 < parts.Length)
                {
                    var candidate = parts[idx + 1];
                    if (candidate.All(char.IsDigit)) return candidate;
                }
                if (idx + 2 < parts.Length)
                {
                    var candidate = parts[idx + 2];
                    if (candidate.All(char.IsDigit)) return candidate;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Extracts the variation ID from an eBay listing URL's "var" query parameter.
    /// Multi-variation listings require this to resolve via the legacy ID endpoint.
    /// Example: https://www.ebay.com/itm/123456789?var=456789 → "456789"
    /// </summary>
    private static string? ExtractVariationId(string url)
    {
        try
        {
            var u = new Uri(url);
            var query = u.Query;
            if (string.IsNullOrWhiteSpace(query)) return null;

            // Parse query string manually (avoid System.Web dependency)
            var pairs = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                var eqIdx = pair.IndexOf('=');
                if (eqIdx < 0) continue;
                var key = Uri.UnescapeDataString(pair.Substring(0, eqIdx));
                if (string.Equals(key, "var", StringComparison.OrdinalIgnoreCase))
                {
                    var val = Uri.UnescapeDataString(pair.Substring(eqIdx + 1)).Trim();
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }
            }
        }
        catch { }
        return null;
    }

    private static int? MapConditionToCategory(int? conditionId)
    {
        // Internal condition table: 1=New, 2=Used, 3=Refurbished
        // eBay condition IDs: https://developer.ebay.com/api-docs/buy/browse/resources/item_summary/methods/search
        // Common mappings:
        // 1000=New -> 1
        // 1500=New (Other)/Open Box -> 2 (Used-like)
        // 2000=Manufacturer Refurbished -> 3
        // 2500=Seller Refurbished -> 3
        // 3000=Used -> 2
        // 4000=Very Good/Like New -> 2
        // 5000=Good -> 2, 6000=Acceptable -> 2
        if (conditionId == null) return null;
        return conditionId switch
        {
            1000 => 1,
            2000 => 3,
            2500 => 3,
            3000 => 2,
            4000 => 2,
            5000 => 2,
            6000 => 2,
            1500 => 2,
            _ => 2 // default to Used
        };
    }
}

// obsolete local listing class removed; using CartSmart.Core.NewListing instead

internal class ItemSummaryResponse { public List<ItemSummary>? itemSummaries { get; set; } }
internal class ItemSummary
{
    public string itemId { get; set; } = string.Empty;
    public string? title { get; set; }
    public string? itemWebUrl { get; set; }
    public DateTimeOffset? itemCreationDate { get; set; }
    public Price? price { get; set; }
    public List<string>? gtin { get; set; }
    public string? mpn { get; set; }
    public string? brand { get; set; }
    public int? conditionId { get; set; }
    public List<ShippingOption>? shippingOptions { get; set; }
    public SellerSummary? seller { get; set; }
    public List<LocalizedAspect>? localizedAspects { get; set; }
}

internal class LocalizedAspect
{
    public string? name { get; set; }
    public string? value { get; set; }
}
internal class ItemResponse
{
    public string itemId { get; set; } = string.Empty;
    public Price? price { get; set; }
    public Availability? availability { get; set; }
    public string? availabilityStatus { get; set; }
    public List<EstimatedAvailability>? estimatedAvailabilities { get; set; }
    public Seller? seller { get; set; }
    public string? itemGroupType { get; set; }
    public string? itemState { get; set; }
    public DateTimeOffset? itemEndDate { get; set; }
    public List<LocalizedAspect>? localizedAspects { get; set; }
    public List<string>? buyingOptions { get; set; }
    public bool? eligibleForInlineCheckout { get; set; }
}
internal class Price { public decimal? value { get; set; } public string? currency { get; set; } }
internal class Availability { public ShipAvail? shipToLocationAvailability { get; set; } public string? availabilityStatus { get; set; } }
internal class ShipAvail { public int? quantity { get; set; } }
internal class EstimatedAvailability
{
    public string? estimatedAvailabilityStatus { get; set; }
    public int? estimatedAvailableQuantity { get; set; }
    public int? estimatedSoldQuantity { get; set; }
    public int? availabilityThreshold { get; set; }
    public string? availabilityThresholdType { get; set; }
}
internal class Seller { public decimal? feedbackPercentage { get; set; } }
internal class ShippingOption { public string? shippingCostType { get; set; } }

internal class SellerSummary
{
    public string? username { get; set; }
    public decimal? feedbackPercentage { get; set; }
    public int? feedbackScore { get; set; }
    public bool? topRatedSeller { get; set; }
    public string? sellerAccountType { get; set; }
}