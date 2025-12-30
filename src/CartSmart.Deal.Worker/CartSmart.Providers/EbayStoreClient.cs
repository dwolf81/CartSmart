using CartSmart.Core.Worker;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace CartSmart.Providers;

public class EbayStoreClient : IStoreClient
{
    private readonly HttpClient _http;
    private readonly ILogger<EbayStoreClient> _logger;
    private readonly IEbayAuthService _auth;
    private readonly IStopWordsProvider _stopWordsProvider;

    public StoreType StoreType => StoreType.Ebay;
    public bool SupportsSoldStatus => true;
    public bool SupportsApi => true;

    public EbayStoreClient(HttpClient http, ILogger<EbayStoreClient> logger, IEbayAuthService auth, IStopWordsProvider stopWordsProvider)
    {
        _http = http;
        _logger = logger;
        _auth = auth;
        _stopWordsProvider = stopWordsProvider ?? throw new ArgumentNullException(nameof(stopWordsProvider));
    }

    public async Task<StoreProductData?> GetByUrlAsync(string productUrl, CancellationToken ct)
    {
        try
        {
            var itemId = ExtractItemId(productUrl);
            if (string.IsNullOrEmpty(itemId))
            {
                // Fallback to simple GET if itemId cannot be parsed
                var resp = await _http.GetAsync(productUrl, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                        return new StoreProductData(null, null, false, true, true, DateTime.UtcNow);
                    return null;
                }
                var html = await resp.Content.ReadAsStringAsync(ct);
                var sold = html.Contains("This listing has ended") || html.Contains("Sold");
                return new StoreProductData(null, null, sold ? false : true, sold, false, DateTime.UtcNow);
            }
            var item = await GetItemAsync(itemId, ct);
            if (item == null) return null;
            var price = item.price?.value;
            var currency = item.price?.currency;
            bool soldFlag = item.seller?.feedbackPercentage == 0 || string.Equals(item.itemGroupType, "SOLD", StringComparison.OrdinalIgnoreCase);
            bool inStock = !(soldFlag || (item.availability?.shipToLocationAvailability?.quantity ?? 0) <= 0);
            return new StoreProductData(price, currency, inStock, soldFlag, false, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ebay fetch failed {Url}", productUrl);
            return null;
        }
    }

    public async Task<IReadOnlyList<CartSmart.Core.Worker.NewListing>> SearchNewListingsAsync(string query, int? preferredConditionCategoryId, CancellationToken ct)
    {
        // Stage A: recall – expand into multiple related queries and union results
        var queryVariants = BuildQueryVariants(query);
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
                freeShipping
            ));
        }

        return filtered;
    }

    private async Task<ItemResponse?> GetItemAsync(string itemId, CancellationToken ct)
    {
        var url = $"https://api.ebay.com/buy/browse/v1/item/{itemId}";
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
    private static IReadOnlyList<string> BuildQueryVariants(string query)
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

        AddVariant(trimmed);

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
        return variants.Take(4).ToList();
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
            if (idx >= 0 && idx + 2 < parts.Length)
            {
                var candidate = parts[idx + 2];
                if (candidate.All(char.IsDigit)) return candidate;
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
}
internal class ItemResponse
{
    public string itemId { get; set; } = string.Empty;
    public Price? price { get; set; }
    public Availability? availability { get; set; }
    public Seller? seller { get; set; }
    public string? itemGroupType { get; set; }
}
internal class Price { public decimal? value { get; set; } public string? currency { get; set; } }
internal class Availability { public ShipAvail? shipToLocationAvailability { get; set; } }
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