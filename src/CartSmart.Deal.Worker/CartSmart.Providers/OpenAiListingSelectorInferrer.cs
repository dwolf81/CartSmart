using CartSmart.Core.Worker;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CartSmart.Providers;

/// <summary>
/// Uses OpenAI to infer CSS listing selectors from a raw HTML snippet.
/// Called by the discovery crawler when a store has no listing_selectors
/// configured — the result is persisted back to store.scrape_config so it
/// only runs once per store.
/// </summary>
public interface IListingSelectorInferrer
{
    /// <summary>
    /// Analyse <paramref name="html"/> and return a best-effort
    /// <see cref="ListingScrapeConfig"/>, or <c>null</c> if inference fails.
    /// </summary>
    Task<ListingScrapeConfig?> InferSelectorsAsync(string html, string pageUrl, CancellationToken ct);
}

public class OpenAiListingSelectorInferrer : IListingSelectorInferrer
{
    private readonly HttpClient _http;
    private readonly ILogger<OpenAiListingSelectorInferrer> _logger;
    private readonly string _apiKey;
    private readonly string _model;

    // Keep the HTML snippet sent to the model small to control token cost.
    private const int MaxHtmlChars = 24_000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OpenAiListingSelectorInferrer(
        HttpClient http,
        ILogger<OpenAiListingSelectorInferrer> logger,
        string? apiKey = null,
        string? model = null)
    {
        _http = http;
        _logger = logger;
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
        _model = model ?? Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini";
    }

    public async Task<ListingScrapeConfig?> InferSelectorsAsync(string html, string pageUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogWarning("OPENAI_API_KEY not configured — skipping listing selector inference");
            return null;
        }

        var cleanHtml = StripScriptsAndStyles(html);

        // Build a compact, structured summary of repeated elements — gives the AI
        // the exact class names that are in the page rather than letting it guess.
        var elementSummary = BuildRepeatedElementSummary(cleanHtml);

        // Also send a truncated raw snippet so the AI can see nesting context.
        var rawSnippet = cleanHtml.Length > MaxHtmlChars ? cleanHtml[..MaxHtmlChars] : cleanHtml;

        var systemPrompt = """
            You are an HTML analysis assistant. You will be given:
              1. A "Repeated elements" table extracted from the page — these are the ONLY element
                 signatures (tag + class combinations) that appear more than once.
              2. A raw HTML snippet for nesting context.

            Your task: identify the CSS selectors that extract individual product listings.

            CRITICAL rules — violating any of these makes the response useless:
            - ONLY use class names that appear VERBATIM in the "Repeated elements" table.
            - Do NOT invent, shorten, paraphrase, or guess any class name.
            - Copy class names character-for-character from the table (including hyphens, underscores,
              double-hyphens like BEM modifiers, etc.).
            - "container" must be the element that repeats once per product listing (the highest-count
              repeated element that wraps a title + price + link together).
            - title/price/url/condition selectors are relative to the container element.
            - If a field cannot be found, use null.

            Rules for the "title" selector specifically:
            - The title is the PRODUCT NAME — it is unique text that differs for every listing.
            - PREFER heading elements: h2, h3, h4, h5 inside the container.
            - If no heading is present, look for a span or div whose class name includes words like
              "title", "name", "label", or "heading".
            - AVOID selecting an anchor element (<a>) as the title unless no heading or
              labelled span/div is present AND the anchor's text clearly looks like a
              unique product name (not a call-to-action like "View full details", "Shop now",
              "See product", "Buy now", "Add to cart"). Anchor text is frequently a CTA.
            - NEVER select visually-hidden accessibility spans (classes like "visually-hidden",
              "sr-only", "screen-reader-only", "hidden") — they typically contain generic phrases.
            - If the only text you can find is a call-to-action or generic phrase, return null
              for title rather than returning a selector that gives the same text on every listing.

            Respond with a JSON object ONLY (no markdown fences):
            {
              "container": "<tag.exact-class-from-table>",
              "title":     "<selector relative to container, or null>",
              "price":     "<selector relative to container, or null>",
              "url":       "<selector relative to container (anchor tag), or null>",
              "condition": "<selector relative to container, or null>",
              "next_page": "<selector for 'next page' link, or null>"
            }
            """;

        var userContent =
            $"Page URL: {pageUrl}\n\n" +
            $"Repeated elements (ONLY use class names from this table):\n{elementSummary}\n\n" +
            $"HTML snippet (for nesting context):\n{rawSnippet}";

        var body = new
        {
            model = _model,
            max_completion_tokens = 512,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = userContent }
            }
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            req.Content = JsonContent.Create(body, options: JsonOpts);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("OpenAI selector inference API error {Status}: {Body}", resp.StatusCode, err);
                return null;
            }

            var rawJson = await resp.Content.ReadAsStringAsync(ct);
            var chat = JsonSerializer.Deserialize<ChatCompletionResponse>(rawJson, JsonOpts);
            var reply = chat?.choices?.FirstOrDefault()?.message?.content?.Trim();
            if (string.IsNullOrWhiteSpace(reply)) return null;

            var config = JsonSerializer.Deserialize<ListingScrapeConfig>(reply, JsonOpts);
            if (config == null || string.IsNullOrWhiteSpace(config.Container))
            {
                _logger.LogWarning("AI selector inference returned no container selector for {Url}", pageUrl);
                return null;
            }

            // ── Validate: every class token in the container selector must
            //    appear verbatim as a class= value in the raw HTML.
            if (!ContainerSelectorIsPresent(config.Container, cleanHtml))
            {
                _logger.LogWarning(
                    "AI selector inference returned container '{Container}' which was not found in the HTML for {Url} — discarding",
                    config.Container, pageUrl);
                return null;
            }

            // ── Validate: title selector must not resolve to the same text on
            //    every listing (catches "View full details", "Add to cart", etc.).
            if (!string.IsNullOrWhiteSpace(config.Title))
            {
                var titleWarning = DetectUniformTitleText(config.Title, cleanHtml);
                if (titleWarning != null)
                {
                    _logger.LogWarning(
                        "AI selector inference: title selector '{Title}' appears to return uniform text '{Text}' for {Url} — clearing title selector",
                        config.Title, titleWarning, pageUrl);
                    config.Title = null;
                }
            }

            _logger.LogInformation(
                "AI inferred listing selectors for {Url}: container={Container} title={Title} price={Price} url={UrlSel}",
                pageUrl, config.Container, config.Title, config.Price, config.Url);

            return config;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI listing selector inference failed for {Url}", pageUrl);
            return null;
        }
    }

    /// <summary>
    /// Builds a compact table of element signatures (tag + class combinations) that
    /// appear more than once in the HTML, sorted by descending frequency.
    /// Gives the AI exact class names to pick from instead of letting it guess.
    /// Headings (h2-h5) are always included even if they only appear once, because
    /// they are strong candidates for the title selector.
    /// </summary>
    private static string BuildRepeatedElementSummary(string html)
    {
        // Match opening tags that have a class attribute
        var tagRegex = new System.Text.RegularExpressions.Regex(
            @"<(div|li|article|section|ul|ol|span|a|figure|tr|td|h2|h3|h4|h5)\b[^>]*\bclass=""([^""]+)""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in tagRegex.Matches(html))
        {
            var tag = m.Groups[1].Value.ToLowerInvariant();
            var rawClasses = m.Groups[2].Value.Trim();
            var classTokens = rawClasses.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var fullKey = $"{tag}.{string.Join(".", classTokens)}";
            counts.TryGetValue(fullKey, out var c);
            counts[fullKey] = c + 1;
        }

        // Headings are always surfaced (even if count=1) — they're strong title candidates.
        var headingTags = new HashSet<string> { "h2", "h3", "h4", "h5" };
        var repeated = counts
            .Where(kv => kv.Value > 1 || headingTags.Contains(kv.Key.Split('.')[0]))
            .OrderByDescending(kv => kv.Value)
            .Take(50)
            .Select(kv => $"  count={kv.Value,4}  selector={kv.Key}");

        var summary = string.Join("\n", repeated);
        return string.IsNullOrWhiteSpace(summary)
            ? "(no repeated elements found)"
            : summary;
    }

    /// <summary>
    /// Extracts the inner text of the first ~10 elements that loosely match the
    /// CSS selector's leaf tag/class in the HTML. Returns the repeated text value
    /// if 80 %+ of samples are identical (indicating a generic label like
    /// "View full details"), or null when the texts look varied (real titles).
    /// </summary>
    private static string? DetectUniformTitleText(string titleSelector, string html)
    {
        // Pull the last simple segment from the selector, e.g. "h2.product-title" or just "h2"
        var lastSegment = titleSelector.Split(' ', '>').Last().Trim();

        // Extract tag name (if present)
        var tagMatch = System.Text.RegularExpressions.Regex.Match(lastSegment, @"^([a-zA-Z][a-zA-Z0-9]*)");
        var tag = tagMatch.Success ? tagMatch.Groups[1].Value : @"[a-zA-Z][a-zA-Z0-9]*";

        // Extract first class token (if present)
        var classMatch = System.Text.RegularExpressions.Regex.Match(lastSegment, @"\.([\w-]+)");
        string pattern;
        if (classMatch.Success)
        {
            var cls = System.Text.RegularExpressions.Regex.Escape(classMatch.Groups[1].Value);
            pattern = $@"<{tag}\b[^>]*\bclass=""[^""]*{cls}[^""]*""[^>]*>([\s\S]*?)</{tag}>";
        }
        else
        {
            pattern = $@"<{tag}\b[^>]*>([\s\S]*?)</{tag}>";
        }

        var matches = System.Text.RegularExpressions.Regex.Matches(
            html, pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (matches.Count < 3) return null; // not enough samples to judge

        // Strip inner tags and collapse whitespace to get plain text
        var texts = matches
            .Take(10)
            .Select(m => System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, @"<[^>]+>", " "))
            .Select(t => System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim())
            .Where(t => t.Length > 0)
            .ToList();

        if (texts.Count < 3) return null;

        // Check known generic call-to-action phrases regardless of frequency
        var genericPhrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "view full details", "view details", "see product", "see full details",
            "add to cart", "add to bag", "buy now", "shop now", "learn more",
            "read more", "click here", "find out more"
        };
        if (genericPhrases.Contains(texts[0]))
            return texts[0];

        // If 80 %+ of sampled texts are identical it's a static label, not a product name
        var dominant = texts.GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(g => g.Count())
                            .First();
        if (dominant.Count() >= (int)Math.Ceiling(texts.Count * 0.8))
            return dominant.Key;

        return null;
    }

    /// <summary>
    /// Returns true when every class token extracted from the CSS selector
    /// exists as a class value somewhere in the raw HTML. This catches
    /// AI-hallucinated class names before they are persisted.
    /// </summary>
    private static bool ContainerSelectorIsPresent(string selector, string html)
    {
        // Extract class tokens from selector, e.g. ".foo.bar--baz" → ["foo", "bar--baz"]
        var classTokens = System.Text.RegularExpressions.Regex
            .Matches(selector, @"\.([\w-]+)")
            .Select(m => m.Groups[1].Value)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        if (classTokens.Count == 0)
            return true; // tag-only or id selector — can't easily validate, allow it

        foreach (var token in classTokens)
        {
            // Check that the class token appears literally inside a class="..." attribute
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                    html,
                    @"class=""[^""]*\b" + System.Text.RegularExpressions.Regex.Escape(token) + @"\b[^""]*""",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Strip &lt;script&gt; and &lt;style&gt; blocks to reduce noise before sending to AI.</summary>
    private static string StripScriptsAndStyles(string html)
    {
        // Simple regex strip — good enough for token reduction; not a security boundary.
        var noScript = System.Text.RegularExpressions.Regex.Replace(
            html, @"<script[\s\S]*?</script>", string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var noStyle = System.Text.RegularExpressions.Regex.Replace(
            noScript, @"<style[\s\S]*?</style>", string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return noStyle;
    }

    // ── minimal response-shape types ────────────────────────────────────────

    private sealed class ChatCompletionResponse
    {
        public List<ChatChoice>? choices { get; set; }
    }

    private sealed class ChatChoice
    {
        public ChatMessage? message { get; set; }
    }

    private sealed class ChatMessage
    {
        public string? content { get; set; }
    }
}
