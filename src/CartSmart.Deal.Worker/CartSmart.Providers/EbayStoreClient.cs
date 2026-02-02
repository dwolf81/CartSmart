using CartSmart.Core.Worker;
using Microsoft.Extensions.Logging;
using Supabase;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace CartSmart.Providers;

public class EbayStoreClient : IStoreClient, IVariantResolvingStoreClient
{
    private readonly HttpClient _http;
    private readonly ILogger<EbayStoreClient> _logger;
    private readonly IEbayAuthService _auth;
    private readonly IStopWordsProvider _stopWordsProvider;
    private readonly Client _supabase;

    private readonly ConcurrentDictionary<long, ProductVariantConfigIndex> _variantConfigCache = new();
    private readonly ConcurrentDictionary<long, long?> _defaultVariantIdCache = new();
    private readonly ConcurrentDictionary<string, string> _listingTextCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>?> _itemAspectsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, IReadOnlyList<string>> _productSearchAliasCache = new();

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
        // If we don't have aspects yet, try to pull them from the item details endpoint.
        if (aspectValueNorms.Count == 0 && !string.IsNullOrWhiteSpace(listing.ItemId))
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
        if (constraints.Count == 0)
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

            // Item IDs in listing URLs are legacy numeric IDs. The Browse API's /item/{itemId}
            // frequently expects the REST "itemId" (often shaped like v1|...|0). Use the
            // get_item_by_legacy_id endpoint for numeric IDs.
            var (item, status) = await GetItemByLegacyIdWithStatusAsync(itemId, ct);
            if (item == null && status == HttpStatusCode.NotFound)
            {
                // As a last resort, try the direct item endpoint too.
                (item, status) = await GetItemWithStatusAsync(itemId, ct);
            }
            if (status == HttpStatusCode.NotFound)
                return new StoreProductData(null, null, false, true, true, DateTime.UtcNow);
            if (item == null) return null;
            var price = item.price?.value;
            var currency = item.price?.currency;
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

        // Prefer explicit availabilityStatus if present.
        var status = item.availabilityStatus ?? item.availability?.availabilityStatus;
        bool? inStock = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToUpperInvariant();
            if (s.Contains("IN_STOCK")) inStock = true;
            else if (s.Contains("OUT_OF_STOCK") || s.Contains("SOLD_OUT") || s.Contains("SOLDOUT")) inStock = false;
            else if (s.Contains("LIMITED")) inStock = true;
        }

        // Fall back to quantity if eBay provides it. If quantity is absent, keep as unknown.
        if (!inStock.HasValue)
        {
            var qty = item.availability?.shipToLocationAvailability?.quantity;
            if (qty.HasValue) inStock = qty.Value > 0;
        }

        // If we believe the listing is ended, it cannot be in stock.
        if (sold) inStock = false;

        return (inStock, sold);
    }

    private async Task<(ItemResponse? Item, HttpStatusCode Status)> GetItemWithStatusAsync(string itemId, CancellationToken ct)
    {
        var url = $"https://api.ebay.com/buy/browse/v1/item/{Uri.EscapeDataString(itemId)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-EBAY-C-MARKETPLACE-ID", "EBAY_US");
        var token = await _auth.GetAccessTokenAsync(ct);
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("eBay item fetch failed: {Status} (itemId={ItemId})", resp.StatusCode, itemId);
            return (null, resp.StatusCode);
        }
        var item = await resp.Content.ReadFromJsonAsync<ItemResponse>(cancellationToken: ct);
        return (item, resp.StatusCode);
    }

    private async Task<(ItemResponse? Item, HttpStatusCode Status)> GetItemByLegacyIdWithStatusAsync(string legacyItemId, CancellationToken ct)
    {
        var url = $"https://api.ebay.com/buy/browse/v1/item/get_item_by_legacy_id?legacy_item_id={Uri.EscapeDataString(legacyItemId)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-EBAY-C-MARKETPLACE-ID", "EBAY_US");
        var token = await _auth.GetAccessTokenAsync(ct);
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, ct);
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
        // Stage A: recall – expand into multiple related queries and union results
        var queryVariants = await BuildQueryVariantsForProductAsync(productId, query, ct);
        if (queryVariants.Count == 0)
            return Array.Empty<CartSmart.Core.Worker.NewListing>();

        // Collect raw candidates keyed by itemId (dedup across queries)
        var rawById = new Dictionary<string, ItemSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in queryVariants)
        {
            var items = await ExecuteSearchAsync(q, preferredConditionCategoryId, ct);
            foreach (var item in items)
            {
                if (!rawById.ContainsKey(item.itemId))
                    rawById[item.itemId] = item;
            }
        }

        if (rawById.Count == 0)
            return Array.Empty<CartSmart.Core.Worker.NewListing>();

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
        var minPct = decimal.TryParse(Environment.GetEnvironmentVariable("EBAY_MIN_FEEDBACK_PERCENT"), out var p) ? p : 98m;
        var minScore = int.TryParse(Environment.GetEnvironmentVariable("EBAY_MIN_FEEDBACK_SCORE"), out var sscore) ? sscore : 500;
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

            // Exclude obvious accessories/parts
            if (accessories.Any(k => titleLower.Contains(k)))
                continue;

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

        // Search results frequently omit localizedAspects. Enrich missing aspects via the item details endpoint
        // so downstream variant resolution has item-specific signals.
        if (filtered.Any(l => l.Aspects == null) && filtered.Count > 0)
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

        return filtered;
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

    private async Task<ItemResponse?> GetItemAsync(string itemId, CancellationToken ct)
    {
        var url = $"https://api.ebay.com/buy/browse/v1/item/{Uri.EscapeDataString(itemId)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-EBAY-C-MARKETPLACE-ID", "EBAY_US");
        var token = await _auth.GetAccessTokenAsync(ct);
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var resp = await _http.SendAsync(req, ct);
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
    private async Task<IReadOnlyList<ItemSummary>> ExecuteSearchAsync(string query, int? preferredConditionCategoryId, CancellationToken ct)
    {
        // Base filter for Buy It Now (FIXED_PRICE) and acceptable conditions
        string BuildConditionFilter(int? category)
        {
            if (!category.HasValue)
                return "conditionIds:{1000|1500|2000|4000}"; // default broad
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
        var filter = string.IsNullOrEmpty(condPart) ? "buyingOptions:{FIXED_PRICE}" : $"buyingOptions:{{FIXED_PRICE}}|{condPart}";

        // Optional category locking via environment variable (comma-separated category IDs)
        var categoryIdsRaw = Environment.GetEnvironmentVariable("EBAY_CATEGORY_IDS");
        var limit = int.TryParse(Environment.GetEnvironmentVariable("EBAY_SEARCH_LIMIT"), out var parsedLimit) && parsedLimit > 0 && parsedLimit <= 200
            ? parsedLimit
            : 100;

        var sb = new StringBuilder();
        sb.Append("q=");
        sb.Append(Uri.EscapeDataString(query));
        sb.Append("&filter=");
        sb.Append(Uri.EscapeDataString(filter));
        sb.Append("&limit=");
        sb.Append(limit.ToString());
        if (!string.IsNullOrWhiteSpace(categoryIdsRaw))
        {
            sb.Append("&category_ids=");
            sb.Append(Uri.EscapeDataString(categoryIdsRaw));
        }

        var url = $"https://api.ebay.com/buy/browse/v1/item_summary/search?{sb}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-EBAY-C-MARKETPLACE-ID", "EBAY_US");
        var token = await _auth.GetAccessTokenAsync(ct);
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("eBay search failed: {Status}", resp.StatusCode);
            return Array.Empty<ItemSummary>();
        }
        var json = await resp.Content.ReadFromJsonAsync<ItemSummaryResponse>(cancellationToken: ct);
        return json?.itemSummaries ?? new List<ItemSummary>();
    }

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
    public Seller? seller { get; set; }
    public string? itemGroupType { get; set; }
    public string? itemState { get; set; }
    public DateTimeOffset? itemEndDate { get; set; }
    public List<LocalizedAspect>? localizedAspects { get; set; }
}
internal class Price { public decimal? value { get; set; } public string? currency { get; set; } }
internal class Availability { public ShipAvail? shipToLocationAvailability { get; set; } public string? availabilityStatus { get; set; } }
internal class ShipAvail { public int? quantity { get; set; } }
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