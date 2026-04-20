using AngleSharp;
using AngleSharp.Dom;
using CartSmart.Core.Worker;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CartSmart.Scraping;

public class ListingPageScraper : IListingPageScraper
{
    private readonly ILogger<ListingPageScraper> _logger;
    private readonly IJsRenderer? _jsRenderer;

    private static readonly Regex PriceRegex = new(
        @"(?<![A-Za-z0-9])([0-9]{1,3}(?:,[0-9]{3})*(?:\.[0-9]{1,2})?|[0-9]+(?:\.[0-9]{1,2})?)",
        RegexOptions.Compiled);

    private static readonly Regex ConditionNewRegex = new(
        @"\b(brand\s*new|new|sealed|factory\s*sealed|unopened|bnib|nib)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ConditionRefurbRegex = new(
        @"\b(refurbished|refurb|renewed|recertified|certified\s*refurbished|open\s*box)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ConditionUsedRegex = new(
        @"\b(used|pre[\-\s]?owned|pre[\-\s]?loved|secondhand|second[\-\s]?hand|mint|near\s*mint|above\s*average|below\s*average|average|excellent|very\s*good|good|fair|poor|acceptable)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ListingPageScraper(
        ILogger<ListingPageScraper> logger,
        IJsRenderer? jsRenderer = null)
    {
        _logger = logger;
        _jsRenderer = jsRenderer;
    }

    public async Task<IReadOnlyList<ScrapedListing>> ScrapeListingsAsync(
        string url,
        ListingScrapeConfig selectors,
        bool httpEnabled,
        bool playwrightEnabled,
        int maxPages = 10,
        int delayBetweenPagesMs = 2000,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(selectors.Container))
        {
            _logger.LogWarning("No listing container selector configured for {Url}", url);
            return Array.Empty<ScrapedListing>();
        }

        var allListings = new List<ScrapedListing>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentUrl = url;

        for (int page = 0; page < maxPages; page++)
        {
            ct.ThrowIfCancellationRequested();

            if (page > 0 && delayBetweenPagesMs > 0)
                await Task.Delay(delayBetweenPagesMs, ct);

            _logger.LogInformation("Scraping listing page {Page} of {Url}", page + 1, currentUrl);

            var html = await FetchHtmlAsync(currentUrl, httpEnabled, playwrightEnabled, ct);
            if (string.IsNullOrWhiteSpace(html))
            {
                _logger.LogWarning("Empty HTML returned for {Url}", currentUrl);
                break;
            }

            var config = Configuration.Default;
            var browsingContext = BrowsingContext.New(config);
            var document = await browsingContext.OpenAsync(req => req.Content(html), ct);

            if (LooksLikeBotProtection(document))
            {
                _logger.LogWarning("Bot protection detected on {Url}", currentUrl);
                break;
            }

            var pageListings = ExtractListings(document, selectors, currentUrl, seenIds);

            // Auto-detect: if no listing containers found, try single-product extraction
            if (pageListings.Count == 0 && page == 0)
            {
                _logger.LogInformation("No listing containers found; attempting single-product extraction for {Url}", currentUrl);
                var single = TryExtractSingleProduct(document, selectors, currentUrl);
                if (single != null)
                {
                    allListings.Add(single);
                    _logger.LogInformation("Extracted single product listing from {Url}", currentUrl);
                }
                break; // Single product page — no pagination
            }

            allListings.AddRange(pageListings);
            _logger.LogInformation("Extracted {Count} listings from page {Page} of {Url}",
                pageListings.Count, page + 1, currentUrl);

            // Check for next page
            if (string.IsNullOrWhiteSpace(selectors.NextPage))
                break;

            var nextPageUrl = FindNextPageUrl(document, selectors.NextPage, currentUrl);
            if (string.IsNullOrWhiteSpace(nextPageUrl))
                break;

            currentUrl = nextPageUrl;
        }

        _logger.LogInformation("Total listings scraped: {Count} from {Url}", allListings.Count, url);
        return allListings;
    }

    private async Task<string?> FetchHtmlAsync(string url, bool httpEnabled, bool playwrightEnabled, CancellationToken ct)
    {
        // Try HTTP first (fast, lightweight)
        if (httpEnabled)
        {
            try
            {
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                httpClient.DefaultRequestHeaders.Accept.ParseAdd(
                    "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                var html = await httpClient.GetStringAsync(url, ct);
                if (!string.IsNullOrWhiteSpace(html))
                    return html;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HTTP fetch failed for {Url}; falling back to Playwright", url);
            }
        }

        // Playwright fallback for JS-rendered pages
        if (playwrightEnabled && _jsRenderer != null)
        {
            try
            {
                var html = await _jsRenderer.RenderAsync(new Uri(url), 15000, ct);
                return html;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Playwright fetch failed for {Url}", url);
            }
        }

        return null;
    }

    private List<ScrapedListing> ExtractListings(
        IDocument document,
        ListingScrapeConfig selectors,
        string pageUrl,
        HashSet<string> seenIds)
    {
        var results = new List<ScrapedListing>();
        var containers = document.QuerySelectorAll(selectors.Container!);

        foreach (var container in containers)
        {
            try
            {
                var listing = ExtractListingFromContainer(container, selectors, pageUrl);
                if (listing == null) continue;
                if (seenIds.Contains(listing.ItemId)) continue;

                seenIds.Add(listing.ItemId);
                results.Add(listing);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to extract listing from container");
            }
        }

        return results;
    }

    private ScrapedListing? ExtractListingFromContainer(
        IElement container,
        ListingScrapeConfig selectors,
        string pageUrl)
    {
        // Extract URL
        string? listingUrl = null;
        if (!string.IsNullOrWhiteSpace(selectors.Url))
        {
            var urlEl = container.QuerySelector(selectors.Url);
            var href = urlEl?.GetAttribute("href");
            if (!string.IsNullOrWhiteSpace(href))
                listingUrl = ResolveUrl(href, pageUrl);
        }

        // Extract price
        decimal? price = null;
        string? currency = null;
        if (!string.IsNullOrWhiteSpace(selectors.Price))
        {
            var priceEl = container.QuerySelector(selectors.Price);
            if (priceEl != null)
            {
                var priceText = priceEl.GetAttribute("content")
                    ?? priceEl.GetAttribute("aria-label")
                    ?? priceEl.TextContent;
                (price, currency) = ParsePrice(priceText);
            }
        }

        // Must have a price to be useful
        if (!price.HasValue || price.Value <= 0)
            return null;

        // Extract title
        string? title = null;
        if (!string.IsNullOrWhiteSpace(selectors.Title))
        {
            var titleEl = container.QuerySelector(selectors.Title);
            title = titleEl?.GetAttribute("title") ?? titleEl?.TextContent?.Trim();
        }

        // Extract condition
        int? conditionCategoryId = null;
        if (!string.IsNullOrWhiteSpace(selectors.Condition))
        {
            var condEl = container.QuerySelector(selectors.Condition);
            var condText = condEl?.TextContent?.Trim();
            if (!string.IsNullOrWhiteSpace(condText))
                conditionCategoryId = InferConditionCategory(condText);
        }
        // Fall back to inferring condition from title
        conditionCategoryId ??= InferConditionCategory(title ?? string.Empty);

        // Generate stable item ID from listing URL (or fallback to title+price hash)
        var itemId = DeriveItemId(listingUrl, title, price.Value);

        return new ScrapedListing(
            ItemId: itemId,
            Title: title,
            Url: listingUrl,
            Price: price,
            Currency: currency ?? "USD",
            ConditionCategoryId: conditionCategoryId
        );
    }

    /// <summary>
    /// Handles single-product pages where the product info is displayed directly
    /// (no listing containers). Uses the same selectors but applied to the whole page.
    /// </summary>
    private ScrapedListing? TryExtractSingleProduct(
        IDocument document,
        ListingScrapeConfig selectors,
        string pageUrl)
    {
        // Try to find price anywhere on the page
        decimal? price = null;
        string? currency = null;
        if (!string.IsNullOrWhiteSpace(selectors.Price))
        {
            var priceEl = document.QuerySelector(selectors.Price);
            if (priceEl != null)
            {
                var priceText = priceEl.GetAttribute("content")
                    ?? priceEl.GetAttribute("aria-label")
                    ?? priceEl.TextContent;
                (price, currency) = ParsePrice(priceText);
            }
        }

        if (!price.HasValue || price.Value <= 0)
            return null;

        string? title = null;
        if (!string.IsNullOrWhiteSpace(selectors.Title))
        {
            var titleEl = document.QuerySelector(selectors.Title);
            title = titleEl?.GetAttribute("title") ?? titleEl?.TextContent?.Trim();
        }
        // Fallback to page title
        title ??= document.Title;

        int? conditionCategoryId = null;
        if (!string.IsNullOrWhiteSpace(selectors.Condition))
        {
            var condEl = document.QuerySelector(selectors.Condition);
            var condText = condEl?.TextContent?.Trim();
            if (!string.IsNullOrWhiteSpace(condText))
                conditionCategoryId = InferConditionCategory(condText);
        }
        conditionCategoryId ??= InferConditionCategory(title ?? string.Empty);

        var itemId = DeriveItemId(pageUrl, title, price.Value);

        return new ScrapedListing(
            ItemId: itemId,
            Title: title,
            Url: pageUrl,
            Price: price,
            Currency: currency ?? "USD",
            ConditionCategoryId: conditionCategoryId
        );
    }

    private string? FindNextPageUrl(IDocument document, string nextPageSelector, string currentUrl)
    {
        // Try each selector (comma-separated fallbacks)
        var selectorParts = nextPageSelector.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var sel in selectorParts)
        {
            var el = document.QuerySelector(sel);
            if (el == null) continue;

            var href = el.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;

            return ResolveUrl(href, currentUrl);
        }
        return null;
    }

    internal static int? InferConditionCategory(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Check refurbished first (more specific than "new")
        if (ConditionRefurbRegex.IsMatch(text)) return 3;
        if (ConditionUsedRegex.IsMatch(text)) return 2;
        if (ConditionNewRegex.IsMatch(text)) return 1;

        return null;
    }

    private static (decimal? price, string? currency) ParsePrice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);

        var currency = DetectCurrency(text);
        var cleaned = text.Trim().Replace("\u00a0", " "); // non-breaking space

        var match = PriceRegex.Match(cleaned);
        if (!match.Success) return (null, currency);

        var num = match.Groups[1].Value.Replace(",", "");
        if (decimal.TryParse(num, out var price) && price > 0)
            return (price, currency);

        return (null, currency);
    }

    private static string? DetectCurrency(string text)
    {
        var upper = text.ToUpperInvariant();
        if (upper.Contains("USD") || upper.Contains("US $") || upper.Contains("$")) return "USD";
        if (upper.Contains("EUR") || upper.Contains("€")) return "EUR";
        if (upper.Contains("GBP") || upper.Contains("£")) return "GBP";
        return null;
    }

    /// <summary>
    /// Generates a stable, deterministic item ID from the listing URL.
    /// Falls back to hashing title+price if no URL available.
    /// </summary>
    private static string DeriveItemId(string? url, string? title, decimal price)
    {
        var source = !string.IsNullOrWhiteSpace(url) ? url : $"{title}|{price:F2}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string ResolveUrl(string href, string pageUrl)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var abs)
            && (abs.Scheme == "http" || abs.Scheme == "https"))
            return abs.ToString();

        if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri))
        {
            if (Uri.TryCreate(baseUri, href, out var resolved))
                return resolved.ToString();
        }

        return href;
    }

    private static bool LooksLikeBotProtection(IDocument doc)
    {
        var title = doc.Title?.ToLowerInvariant() ?? string.Empty;
        if (title.Contains("security checkpoint")) return true;

        var bodyText = doc.Body?.TextContent?.ToLowerInvariant() ?? string.Empty;
        if (bodyText.Contains("verifying your browser")) return true;
        if (bodyText.Contains("enable javascript to continue")) return true;
        if (bodyText.Contains("vercel security checkpoint")) return true;

        return false;
    }
}
