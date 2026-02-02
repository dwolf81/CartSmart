using AngleSharp;
using AngleSharp.Dom;
using CartSmart.Core.Worker;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CartSmart.Scraping;

public class GenericHtmlScraper : IHtmlScraper
{
    private readonly ILogger<GenericHtmlScraper> _logger;
    private readonly IBrowsingContext _context;
    private readonly IJsRenderer? _jsRenderer;
    private readonly bool _enableJsFallback;
    private readonly int _jsTimeoutMs;
    private readonly bool _openBrowserOnChallenge;

    private static readonly string[] PriceSelectors = new[]
    {
        "meta[itemprop=price]",
        "meta[property='product:price:amount']",
        "*[itemprop='price']",
        "span[class*='price']",
        "div[class*='price']",
        "span[id*='price']",
        "div[id*='price']",
        // common sale/price classes
        "span.text_sale",
        "span[class*='text_sale']",
        "span[class*='price-lg']",
        "span[class*='price']"
    };
    private static readonly string[] StockKeywords = new[] { "in stock", "available" };
    private static readonly string[] OosKeywords = new[] { "out of stock", "unavailable" };

    public GenericHtmlScraper(
        ILogger<GenericHtmlScraper> logger,
        IJsRenderer? jsRenderer = null,
        bool enableJsFallback = false,
        int jsTimeoutMs = 4000,
        bool openBrowserOnChallenge = false)
    {
        _logger = logger;
        var config = Configuration.Default.WithDefaultLoader();
        _context = BrowsingContext.New(config);
        _jsRenderer = jsRenderer;
        _enableJsFallback = enableJsFallback;
        _jsTimeoutMs = jsTimeoutMs;
        _openBrowserOnChallenge = openBrowserOnChallenge;
    }

    public async Task<ScrapeResult?> ScrapeAsync(Uri uri, string[]? overridePriceSelectors, CancellationToken ct)
    {
        try
        {
            // If no explicit selectors provided (store profile required), skip scraping
            if (overridePriceSelectors == null || overridePriceSelectors.Length == 0)
            {
                _logger.LogInformation("Skipping scrape for {Url}: no override selectors provided", uri);
                return null;
            }
            var doc = await _context.OpenAsync(uri.ToString(), ct);

            if (LooksLikeBotProtectionPage(doc))
            {
                _logger.LogWarning("Scrape blocked (bot protection) for {Url}", uri);
                TryOpenBrowserForManualReview(uri);
                return new ScrapeResult
                {
                    Html = null,
                    ExtractedPrice = null,
                    Currency = null,
                    InStock = null,
                    Sold = null,
                    BlockedByBotProtection = true,
                    RawSignals = new Dictionary<string, string>
                    {
                        ["blocked"] = "bot_protection"
                    }
                };
            }
            var result = new ScrapeResult();

            // Collect candidates then choose best (avoid struck-through/promotional; prefer lowest current price)
            var candidates = new List<(decimal amount, string? currency, bool struck, bool promo)>();
            IElement? regionRoot = null;
            bool RegionContains(IElement el)
            {
                if (regionRoot == null) return false;
                var cur = el;
                while (cur != null)
                {
                    if (cur == regionRoot) return true;
                    cur = cur.ParentElement;
                }
                return false;
            }
            IElement SelectRegionRoot(IElement el)
            {
                var cur = el;
                while (cur.ParentElement != null && cur.ParentElement.TagName != "BODY")
                {
                    var clsId = (cur.ClassName + " " + cur.Id).ToLowerInvariant();
                    if (clsId.Contains("product") || clsId.Contains("price") || clsId.Contains("buy") || clsId.Contains("main") || clsId.Contains("summary") || clsId.Contains("detail"))
                        return cur;
                    cur = cur.ParentElement;
                }
                return el.ParentElement ?? el; // fallback
            }
            var activeSelectors = overridePriceSelectors;
            foreach (var sel in activeSelectors)
            {
                var els = doc.QuerySelectorAll(sel);
                _logger.LogDebug("Selector '{Selector}' found {Count} elements", sel, els.Length);
                foreach (var el in els)
                {
                    if (regionRoot != null && !RegionContains(el))
                        continue; // ignore elements outside first price region
                    // Prefer explicit aria-label when present
                    var raw = el.GetAttribute("aria-label") ?? el.GetAttribute("content") ?? el.TextContent;
                    _logger.LogDebug("  Element: tag={Tag}, class={Class}, aria-label={AriaLabel}, content={Content}, text={Text}", 
                        el.TagName, el.ClassName, el.GetAttribute("aria-label"), el.GetAttribute("content"), el.TextContent?.Substring(0, Math.Min(30, el.TextContent?.Length ?? 0)));
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var promo = LooksPromotional(raw);
                    var struck = IsStruckThrough(el);
                    var cleaned = CleanPriceText(raw);
                    if (TryParsePrice(cleaned, out var p))
                    {
                        var curr = DetectCurrency(raw ?? el.TextContent ?? string.Empty);
                        candidates.Add((p, curr, struck, promo));
                        if (regionRoot == null)
                        {
                            regionRoot = SelectRegionRoot(el);
                            _logger.LogInformation("Price region locked: tag={Tag}, id={Id}, class={Class}", regionRoot.TagName, regionRoot.Id, regionRoot.ClassName);
                        }
                        _logger.LogDebug("Price candidate: {Amount} {Currency}, struck={Struck}, promo={Promo}, raw={Raw}", p, curr, struck, promo, raw?.Substring(0, Math.Min(50, raw.Length)));
                    }
                }
                if (regionRoot != null)
                {
                    // After locking region, we continue current selector loop but skip remaining selectors once a reasonable number of candidates collected
                    if (candidates.Count >= 6) break; // heuristic limit
                }
            }

            // JS-rendered fallback: re-run selectors only (no heuristic expansion)
            if (candidates.Count == 0 && _enableJsFallback && _jsRenderer != null)
            {
                _logger.LogInformation("Running JS-rendered fallback (profile selectors only) for {Url}", uri);
                var effectiveTimeout = _jsTimeoutMs < 8000 ? 15000 : _jsTimeoutMs;
                if (effectiveTimeout != _jsTimeoutMs)
                    _logger.LogInformation("Using elevated JS timeout: {TimeoutMs}ms (configured {ConfiguredMs}ms)", effectiveTimeout, _jsTimeoutMs);
                var rendered = await _jsRenderer.RenderAsync(uri, effectiveTimeout, ct);
                if (!string.IsNullOrEmpty(rendered))
                {
                    var doc2 = await _context.OpenAsync(req => req.Content(rendered));

                    if (LooksLikeBotProtectionPage(doc2))
                    {
                        _logger.LogWarning("JS-rendered scrape still blocked (bot protection) for {Url}", uri);
                        TryOpenBrowserForManualReview(uri);
                        return new ScrapeResult
                        {
                            Html = null,
                            ExtractedPrice = null,
                            Currency = null,
                            InStock = null,
                            Sold = null,
                            BlockedByBotProtection = true,
                            RawSignals = new Dictionary<string, string>
                            {
                                ["blocked"] = "bot_protection"
                            }
                        };
                    }

                    foreach (var sel in activeSelectors)
                    {
                        var els = doc2.QuerySelectorAll(sel);
                        foreach (var el in els)
                        {
                            if (regionRoot != null && !RegionContains(el)) continue;
                            var raw = el.GetAttribute("aria-label") ?? el.GetAttribute("content") ?? el.TextContent;
                            if (string.IsNullOrWhiteSpace(raw)) continue;
                            var promo = LooksPromotional(raw);
                            var struck = IsStruckThrough(el);
                            var cleaned = CleanPriceText(raw);
                            if (TryParsePrice(cleaned, out var p))
                            {
                                var curr = DetectCurrency(raw ?? el.TextContent ?? string.Empty);
                                candidates.Add((p, curr, struck, promo));
                                if (regionRoot == null)
                                {
                                    regionRoot = SelectRegionRoot(el);
                                    _logger.LogInformation("(JS) Price region locked: tag={Tag}, id={Id}, class={Class}", regionRoot.TagName, regionRoot.Id, regionRoot.ClassName);
                                }
                                _logger.LogDebug("(JS) Price candidate: {Amount} {Currency}, struck={Struck}, promo={Promo}", p, curr, struck, promo);
                            }
                        }
                        if (regionRoot != null && candidates.Count >= 6) break;
                    }
                }
            }

            decimal? price = null;
            string? currency = null;
            _logger.LogInformation("Total price candidates found: {Count}", candidates.Count);
            if (candidates.Count > 0)
            {
                // Prefer non-struck, non-promo candidates; if multiple, choose the lowest amount (current sale price)
                var preferred = candidates
                    .Where(c => !c.struck && !c.promo)
                    .DefaultIfEmpty()
                    .OrderBy(c => c.amount)
                    .FirstOrDefault();

                if (preferred.amount != 0)
                {
                    price = preferred.amount;
                    currency = preferred.currency;
                    _logger.LogDebug("Selected preferred price: {Price} {Currency}", price, currency);
                }
                else
                {
                    // Fallback: any non-struck candidate
                    var alt = candidates.Where(c => !c.struck).OrderBy(c => c.amount).FirstOrDefault();
                    if (alt.amount != 0)
                    {
                        price = alt.amount;
                        currency = alt.currency;
                    }
                    else
                    {
                        // Last resort: take the lowest among all candidates
                        var any = candidates.OrderBy(c => c.amount).First();
                        price = any.amount;
                        currency = any.currency;
                    }
                }
            }

            var text = doc.Body?.TextContent?.ToLowerInvariant() ?? string.Empty;
            bool? inStock = null;
            if (StockKeywords.Any(k => text.Contains(k))) inStock = true;
            if (OosKeywords.Any(k => text.Contains(k))) inStock = false;

            return new ScrapeResult
            {
                Html = null,
                ExtractedPrice = price,
                Currency = currency ?? "USD",
                InStock = inStock,
                Sold = text.Contains("sold") ? true : null,
                RawSignals = new Dictionary<string, string>
                {
                    ["priceFound"] = price?.ToString() ?? "",
                    ["length"] = text.Length.ToString()
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scrape failed {Url}", uri);
            return null;
        }
    }

    private static bool LooksLikeBotProtectionPage(IDocument doc)
    {
        // Common for Vercel Security Checkpoint and similar JS-based challenge pages.
        // We avoid trying to scrape prices from these because the “real” DOM never loads.
        var title = doc.Title?.ToLowerInvariant() ?? string.Empty;
        if (title.Contains("security checkpoint")) return true;

        var headerText = doc.QuerySelector("#header-text")?.TextContent?.ToLowerInvariant() ?? string.Empty;
        if (headerText.Contains("verifying your browser")) return true;

        var bodyText = doc.Body?.TextContent?.ToLowerInvariant() ?? string.Empty;
        if (bodyText.Contains("verifying your browser")) return true;
        if (bodyText.Contains("enable javascript to continue")) return true;
        if (bodyText.Contains("vercel security checkpoint")) return true;

        // Last-resort heuristic: lots of sites include these markers in the challenge HTML.
        var html = doc.DocumentElement?.OuterHtml?.ToLowerInvariant() ?? string.Empty;
        if (html.Contains("vercel security checkpoint")) return true;
        if (html.Contains("verifying your browser")) return true;
        if (html.Contains("security-checkpoint")) return true;

        return false;
    }

    private void TryOpenBrowserForManualReview(Uri uri)
    {
        if (!_openBrowserOnChallenge) return;

        // Only attempt on Windows in an interactive session.
        // Cloud workers/containers typically cannot show a GUI window.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _logger.LogInformation("OpenBrowserOnChallenge enabled, but OS is not Windows; skipping browser launch for {Url}", uri);
            return;
        }

        if (!Environment.UserInteractive)
        {
            _logger.LogInformation("OpenBrowserOnChallenge enabled, but process is not interactive; skipping browser launch for {Url}", uri);
            return;
        }

        // Best-effort guard for Azure/containers even on Windows.
        var websiteInstanceId = Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID");
        var runningInContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        if (!string.IsNullOrEmpty(websiteInstanceId) || string.Equals(runningInContainer, "true", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("OpenBrowserOnChallenge enabled, but environment looks non-desktop; skipping browser launch for {Url}", uri);
            return;
        }

        try
        {
            _logger.LogInformation("Opening URL in default browser for manual review: {Url}", uri);
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.ToString(),
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open browser for manual review: {Url}", uri);
        }
    }

    private static string CleanPriceText(string s)
    {
        // Collapse duplicate sequences like "US $949.00US $949.00" by splitting on currency markers and keeping first meaningful token.
        var trimmed = s.Trim();
        // Normalize spaces
        trimmed = System.Text.RegularExpressions.Regex.Replace(trimmed, "\\s+", " ");
        // Remove repeated identical substrings
        var halfLen = trimmed.Length / 2;
        if (halfLen > 0 && trimmed.Substring(0, halfLen).Equals(trimmed.Substring(halfLen), StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Substring(0, halfLen);
        }
        return trimmed;
    }

    private static bool LooksPromotional(string s)
    {
        // Ignore strings like "save $100", "save 20%", "discount", "off"
        var t = s.ToLowerInvariant();
        if (t.Contains("save") || t.Contains("discount") || t.Contains("off"))
            return true;
        return false;
    }

    private static bool IsStruckThrough(IElement el)
    {
        // Heuristics: inline style text-decoration: line-through; classes indicating old/was/strike
        var style = el.GetAttribute("style")?.ToLowerInvariant() ?? string.Empty;
        if (style.Contains("line-through")) return true;
        var cls = el.ClassName?.ToLowerInvariant() ?? string.Empty;
        if (!string.IsNullOrEmpty(cls) && (
            cls.Contains("strike") || cls.Contains("strikethrough") || 
            cls.Contains("line-through") || cls.Contains("text-decor_line-through") ||
            cls.Contains("was-price") || cls.Contains("old-price") || cls.Contains("list-price")
        )) return true;
        // Also check parent containers which often wrap the old price
        var parent = el.ParentElement;
        if (parent != null)
        {
            var pStyle = parent.GetAttribute("style")?.ToLowerInvariant() ?? string.Empty;
            var pCls = parent.ClassName?.ToLowerInvariant() ?? string.Empty;
            if (pStyle.Contains("line-through")) return true;
            if (!string.IsNullOrEmpty(pCls) && (
                pCls.Contains("strike") || pCls.Contains("strikethrough") || pCls.Contains("was-price") || pCls.Contains("old-price") || pCls.Contains("list-price")
            )) return true;
        }
        return false;
    }

    private bool TryParsePrice(string? s, out decimal price)
    {
        price = 0m;
        if (string.IsNullOrWhiteSpace(s)) return false;
        // Find first monetary number (supports comma thousand separators and dot decimals)
        var m = System.Text.RegularExpressions.Regex.Match(s, "(?<![A-Za-z0-9])([0-9]{1,3}(?:,[0-9]{3})*(?:\\.[0-9]{2})|[0-9]+(?:\\.[0-9]{1,2})?)");
        if (!m.Success) return false;
        var num = m.Groups[1].Value;
        // Remove thousand separators
        num = num.Replace(",", "");
        return decimal.TryParse(num, out price);
    }

    private static string? DetectCurrency(string s)
    {
        s = s.ToUpperInvariant();
        if (s.Contains("USD") || s.Contains("US $") || s.Contains("$")) return "USD";
        if (s.Contains("EUR") || s.Contains("€")) return "EUR";
        if (s.Contains("GBP") || s.Contains("£")) return "GBP";
        return null;
    }
}