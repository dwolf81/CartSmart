using CartSmart.API.Models;
using CartSmart.Core.Worker;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CartSmart.Providers;

/// <summary>
/// Uses OpenAI to extract structured deal data from raw ingestion signals.
/// Given a raw signal (title, body, URL), the AI returns a structured
/// JSON object with product name, price, store, coupon code, deal type, etc.
/// </summary>
public class OpenAiDealExtractor : IAiDealExtractor
{
    private readonly HttpClient _http;
    private readonly ILogger<OpenAiDealExtractor> _logger;
    private readonly string _apiKey;
    private readonly string _model;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OpenAiDealExtractor(HttpClient http, ILogger<OpenAiDealExtractor> logger, string? apiKey = null, string? model = null)
    {
        _http = http;
        _logger = logger;
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
        _model = model ?? Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini";
    }

    public async Task<DealExtractionResult?> ExtractAsync(RawSignal signal, CancellationToken ct)
    {
        var results = await ExtractMultipleAsync(signal, ct);
        return results.Count > 0 ? results[0] : null;
    }

    public async Task<IReadOnlyList<DealExtractionResult>> ExtractMultipleAsync(RawSignal signal, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogWarning("OPENAI_API_KEY not configured — cannot extract deal data");
            return [];
        }

        var systemPrompt = BuildMultiDealSystemPrompt();
        var userPrompt = BuildUserPrompt(signal);

        var body = new
        {
            model = _model,
            max_completion_tokens = 4096,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
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
                var errorBody = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("OpenAI extraction API error {Status}: {Body}", resp.StatusCode, errorBody);
                return [];
            }

            var rawJson = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogDebug("OpenAI multi-deal extraction raw response: {Body}", rawJson);

            var chatResponse = JsonSerializer.Deserialize<ChatCompletionResponse>(rawJson, JsonOpts);
            var reply = chatResponse?.choices?.FirstOrDefault()?.message?.content?.Trim()
                     ?? chatResponse?.output?.FirstOrDefault(o => o.type == "message")?.content?.FirstOrDefault()?.text?.Trim()
                     ?? string.Empty;

            if (string.IsNullOrWhiteSpace(reply))
            {
                _logger.LogWarning("OpenAI extraction returned empty reply for signal {SignalId}", signal.Id);
                return [];
            }

            return ParseMultiDealResponse(reply, signal.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAI multi-deal extraction failed for signal {SignalId}", signal.Id);
            return [];
        }
    }

    private static string BuildMultiDealSystemPrompt()
    {
        return """
            You are a deal extraction assistant. Given raw content from various sources (emails, Reddit posts,
            social media, retail feeds, forums), extract ALL deals found in the content.

            A single source may contain multiple deals. Determine whether each deal is:
            - "store_wide": applies to ALL products at the store (e.g. "20% off everything", "sitewide sale")
            - "product_specific": applies to specific products only

            For product-specific deals, list every individual product mentioned with its own price/discount.

            Respond with a JSON object containing a "deals" array:
            {
              "deals": [
                {
                  "title": "short deal title",
                  "price": 123.45 or null,
                  "currency": "USD",
                  "coupon_code": "CODE123" or null,
                  "url": "https://..." or null,
                  "discount_percent": 25 or null,
                  "deal_type": "direct" | "coupon" | "stacked" | "external",
                  "expiration_date": "2026-04-15T00:00:00Z" or null,
                  "store_name": "Store Name",
                  "is_store_wide": true or false,
                  "product_name": "Primary product name" or null,
                  "product_brand": "Brand" or null,
                  "products": [
                    {
                      "product_name": "Specific Product Name",
                      "product_brand": "Brand" or null,
                      "price": 99.99 or null,
                      "discount_percent": 20 or null,
                      "coupon_code": null,
                      "url": null
                    }
                  ],
                  "confidence": 0.85,
                  "reasoning": "brief explanation"
                }
              ]
            }

            Rules:
            - Return ALL deals found, even if from the same store. One email can have many deals.
            - "is_store_wide" = true when the deal applies to the entire store (sitewide sale, store-wide coupon).
              For store-wide deals, "products" can be empty and "product_name" can be null.
            - "is_store_wide" = false for product-specific deals. Include each product in the "products" array.
            - "confidence" is 0.0 to 1.0 indicating how confident you are this is a real deal.
            - Set confidence low (<0.3) if the content is vague, spam, or not clearly a deal.
            - "deal_type" should be "coupon" if there's a coupon code, "direct" for price deals,
              "stacked" if multiple discounts apply, "external" for deals on another site.
            - Extract the most specific product name possible (include model numbers, sizes, colors).
            - If price is not explicitly stated, set to null.
            - If the content contains NO deals, return {"deals": []} (empty array).

            Respond ONLY with the JSON object. No other text.
            """;
    }

    private static string BuildUserPrompt(RawSignal signal)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(signal.Title))
            parts.Add($"Title: {signal.Title}");
        if (!string.IsNullOrWhiteSpace(signal.Body))
        {
            // Truncate very long bodies to stay within token limits
            var body = signal.Body.Length > 3000 ? signal.Body[..3000] + "..." : signal.Body;
            parts.Add($"Body: {body}");
        }
        if (!string.IsNullOrWhiteSpace(signal.Url))
            parts.Add($"URL: {signal.Url}");
        if (!string.IsNullOrWhiteSpace(signal.Author))
            parts.Add($"Author: {signal.Author}");

        parts.Add("\nExtract the deal information from this content.");

        return string.Join("\n", parts);
    }

    private IReadOnlyList<DealExtractionResult> ParseMultiDealResponse(string json, long signalId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Handle both { "deals": [...] } and single-object format (backwards compat)
            if (root.TryGetProperty("deals", out var dealsArray) && dealsArray.ValueKind == JsonValueKind.Array)
            {
                var results = new List<DealExtractionResult>();
                foreach (var dealEl in dealsArray.EnumerateArray())
                {
                    var result = ParseSingleDealElement(dealEl, signalId);
                    if (result is not null)
                        results.Add(result);
                }
                return results;
            }

            // Fallback: single deal object (backwards compat with old prompt format)
            var single = ParseSingleDealElement(root, signalId);
            return single is not null ? [single] : [];
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse AI multi-deal JSON for signal {SignalId}: {Json}", signalId, json);
            return [];
        }
    }

    private DealExtractionResult? ParseSingleDealElement(JsonElement root, long signalId)
    {
        try
        {
            var title = TryGetString(root, "title") ?? "Unknown Deal";
            var price = TryGetDecimal(root, "price");
            var currency = TryGetString(root, "currency") ?? "USD";
            var couponCode = TryGetString(root, "coupon_code");
            var url = TryGetString(root, "url");
            var discountPercent = TryGetInt(root, "discount_percent");
            var dealTypeStr = TryGetString(root, "deal_type") ?? "direct";
            var expirationStr = TryGetString(root, "expiration_date");
            var storeName = TryGetString(root, "store_name");
            var productName = TryGetString(root, "product_name");
            var productBrand = TryGetString(root, "product_brand");
            var confidence = TryGetDecimal(root, "confidence") ?? 0m;
            var reasoning = TryGetString(root, "reasoning");
            var isStoreWide = TryGetBool(root, "is_store_wide") ?? false;

            var dealTypeId = dealTypeStr?.ToLowerInvariant() switch
            {
                "coupon" => 2,
                "stacked" => 3,
                "external" => 4,
                _ => 1
            };

            DateTime? expirationDate = null;
            if (!string.IsNullOrWhiteSpace(expirationStr) && DateTime.TryParse(expirationStr, out var parsed))
                expirationDate = parsed.ToUniversalTime();

            // Parse products array for product-specific deals
            List<ProductDealInfo>? products = null;
            if (root.TryGetProperty("products", out var productsArray) && productsArray.ValueKind == JsonValueKind.Array)
            {
                products = new List<ProductDealInfo>();
                foreach (var pEl in productsArray.EnumerateArray())
                {
                    var pName = TryGetString(pEl, "product_name");
                    if (string.IsNullOrWhiteSpace(pName)) continue;

                    products.Add(new ProductDealInfo(
                        ProductName: pName,
                        ProductBrand: TryGetString(pEl, "product_brand"),
                        Price: TryGetDecimal(pEl, "price"),
                        DiscountPercent: TryGetInt(pEl, "discount_percent"),
                        CouponCode: TryGetString(pEl, "coupon_code"),
                        Url: TryGetString(pEl, "url")
                    ));
                }
                if (products.Count == 0) products = null;
            }

            return new DealExtractionResult(
                Title: title,
                Price: price,
                Currency: currency,
                CouponCode: couponCode,
                Url: url,
                DiscountPercent: discountPercent,
                DealTypeId: dealTypeId,
                ExpirationDate: expirationDate,
                StoreName: storeName,
                ProductName: productName,
                ProductBrand: productBrand,
                ConfidenceScore: Math.Clamp(confidence, 0m, 1m),
                Reasoning: reasoning,
                IsStoreWide: isStoreWide,
                Products: products
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse deal element for signal {SignalId}", signalId);
            return null;
        }
    }

    private static string? TryGetString(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString();
        return null;
    }

    private static decimal? TryGetDecimal(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number)
            return val.GetDecimal();
        return null;
    }

    private static int? TryGetInt(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number)
            return val.GetInt32();
        return null;
    }

    private static bool? TryGetBool(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.True) return true;
            if (val.ValueKind == JsonValueKind.False) return false;
        }
        return null;
    }

    // --- OpenAI response models (shared format with OpenAiDealValidator) ---
    private sealed class ChatCompletionResponse
    {
        public List<Choice>? choices { get; set; }
        public List<OutputItem>? output { get; set; }
    }
    private sealed class Choice { public Message? message { get; set; } }
    private sealed class Message { public string? content { get; set; } }
    private sealed class OutputItem
    {
        public string? type { get; set; }
        public List<ContentPart>? content { get; set; }
    }
    private sealed class ContentPart
    {
        public string? type { get; set; }
        public string? text { get; set; }
    }
}
