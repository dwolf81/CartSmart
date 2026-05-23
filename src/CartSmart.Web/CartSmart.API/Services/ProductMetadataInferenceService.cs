using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CartSmart.API.Models;
using Microsoft.Extensions.Caching.Memory;

namespace CartSmart.API.Services;

/// <summary>
/// Asks OpenAI to pick a brand_id and product_type_id from the curated lists in
/// the brand/product_type tables. Whole-table candidate sets are tiny enough
/// (dozens of rows) to fit in a single prompt, so we send them in full and
/// constrain the model to ids from those lists.
/// </summary>
public sealed class ProductMetadataInferenceService : IProductMetadataInferenceService
{
    private const string BrandsCacheKey = "infer_brands";
    private const string ProductTypesCacheKey = "infer_product_types";
    private static readonly TimeSpan LookupCacheDuration = TimeSpan.FromMinutes(10);

    private readonly ISupabaseService _supabase;
    private readonly IMemoryCache _cache;
    private readonly HttpClient _http;
    private readonly ILogger<ProductMetadataInferenceService> _logger;
    private readonly string _apiKey;
    private readonly string _model;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ProductMetadataInferenceService(
        ISupabaseService supabase,
        IMemoryCache cache,
        HttpClient http,
        ILogger<ProductMetadataInferenceService> logger)
    {
        _supabase = supabase;
        _cache = cache;
        _http = http;
        _logger = logger;
        _apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
        _model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini";
    }

    public async Task<ProductMetadataInferenceResult> InferAsync(
        string productName,
        string? scrapedBrandText,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(productName) || string.IsNullOrWhiteSpace(_apiKey))
            return new ProductMetadataInferenceResult();

        var (brands, productTypes) = await LoadLookupsAsync(ct);
        if (brands.Count == 0 || productTypes.Count == 0)
            return new ProductMetadataInferenceResult();

        var systemPrompt = """
            You classify a golf product listing title into one of the brands and
            one of the product types provided. Respond with a JSON object ONLY:
              { "brand_id": <id from brands list> | null,
                "product_type_id": <id from product types list> | null,
                "confidence": <0..1>,
                "reason": "<short>" }
            Return null for either field when no candidate is a clear match.
            Do not invent ids that are not in the provided lists.
            """;

        var brandLines = string.Join("\n", brands.Select(b => $"- id={b.Id} name=\"{b.Name}\""));
        var typeLines = string.Join("\n", productTypes.Select(t => $"- id={t.Id} name=\"{t.Name}\""));

        var userPrompt =
            $"Listing title: {productName}\n" +
            (string.IsNullOrWhiteSpace(scrapedBrandText) ? "" : $"Scraped brand hint: {scrapedBrandText}\n") +
            $"\nBrands:\n{brandLines}\n\nProduct types:\n{typeLines}\n";

        var body = new
        {
            model = _model,
            max_completion_tokens = 256,
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
                var err = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("OpenAI inference API error {Status}: {Body}", resp.StatusCode, err);
                return new ProductMetadataInferenceResult();
            }

            var rawJson = await resp.Content.ReadAsStringAsync(ct);
            var chat = JsonSerializer.Deserialize<ChatCompletionResponse>(rawJson, JsonOpts);
            var reply = chat?.choices?.FirstOrDefault()?.message?.content?.Trim();
            if (string.IsNullOrWhiteSpace(reply)) return new ProductMetadataInferenceResult();

            using var doc = JsonDocument.Parse(reply);
            var root = doc.RootElement;

            int? brandId = ReadInt(root, "brand_id");
            int? productTypeId = ReadInt(root, "product_type_id");
            decimal confidence = 0m;
            if (root.TryGetProperty("confidence", out var confEl) && confEl.ValueKind == JsonValueKind.Number)
                confidence = Math.Clamp(confEl.GetDecimal(), 0m, 1m);
            string? reason = null;
            if (root.TryGetProperty("reason", out var rEl) && rEl.ValueKind == JsonValueKind.String)
                reason = rEl.GetString();

            // Guard: only return ids that are in the lookup sets
            if (brandId.HasValue && brands.All(b => b.Id != brandId.Value))
            {
                _logger.LogInformation("AI returned brand_id {Id} not in lookup — ignoring", brandId);
                brandId = null;
            }
            if (productTypeId.HasValue && productTypes.All(t => t.Id != productTypeId.Value))
            {
                _logger.LogInformation("AI returned product_type_id {Id} not in lookup — ignoring", productTypeId);
                productTypeId = null;
            }

            return new ProductMetadataInferenceResult
            {
                BrandId = brandId,
                ProductTypeId = productTypeId,
                Confidence = confidence,
                Reason = reason
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAI metadata inference failed for product name \"{Name}\"", productName);
            return new ProductMetadataInferenceResult();
        }
    }

    private async Task<(List<Brand> Brands, List<ProductType> ProductTypes)> LoadLookupsAsync(CancellationToken ct)
    {
        var client = _supabase.GetServiceRoleClient();

        if (!_cache.TryGetValue(BrandsCacheKey, out List<Brand>? brands) || brands == null)
        {
            var bResp = await client.From<Brand>().Select("id, name").Get();
            brands = (bResp.Models ?? new List<Brand>())
                .Where(b => !string.IsNullOrWhiteSpace(b.Name))
                .ToList();
            _cache.Set(BrandsCacheKey, brands, LookupCacheDuration);
        }

        if (!_cache.TryGetValue(ProductTypesCacheKey, out List<ProductType>? productTypes) || productTypes == null)
        {
            var tResp = await client.From<ProductType>().Select("id, name").Get();
            productTypes = (tResp.Models ?? new List<ProductType>())
                .Where(t => !string.IsNullOrWhiteSpace(t.Name))
                .ToList();
            _cache.Set(ProductTypesCacheKey, productTypes, LookupCacheDuration);
        }

        return (brands, productTypes);
    }

    private static int? ReadInt(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s)) return s;
        return null;
    }

    private sealed class ChatCompletionResponse { public List<Choice>? choices { get; set; } }
    private sealed class Choice { public Message? message { get; set; } }
    private sealed class Message { public string? content { get; set; } }
}
