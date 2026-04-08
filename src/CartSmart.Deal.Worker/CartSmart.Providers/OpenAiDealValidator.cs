using CartSmart.Core.Worker;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CartSmart.Providers;

/// <summary>
/// Validates deal content against a target product using the OpenAI chat API.
/// Designed to be source-agnostic: works with eBay listings today and can be
/// extended for emails, forums, social media posts, and web pages.
/// </summary>
public class OpenAiDealValidator : IAiDealValidator
{
    private readonly HttpClient _http;
    private readonly ILogger<OpenAiDealValidator> _logger;
    private readonly string _apiKey;
    private readonly string _model;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OpenAiDealValidator(HttpClient http, ILogger<OpenAiDealValidator> logger, string? apiKey = null, string? model = null)
    {
        _http = http;
        _logger = logger;
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
        _model = model ?? Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini";
    }

    public async Task<AiValidationResult> ValidateAsync(AiValidationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogWarning("OPENAI_API_KEY not configured — skipping AI validation, allowing listing through");
            return new AiValidationResult(true, "ai_not_configured");
        }

        var systemPrompt = BuildSystemPrompt();
        var userPrompt = BuildUserPrompt(request);

        var body = new
        {
            model = _model,
            max_completion_tokens = 2048,
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
                _logger.LogWarning("OpenAI API error {Status}: {Body}", resp.StatusCode, errorBody);
                // On API failure, allow the listing through so we don't block deals due to AI outage
                return new AiValidationResult(true, "ai_api_error");
            }

            var rawJson = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogDebug("OpenAI raw response: {Body}", rawJson);

            var json = JsonSerializer.Deserialize<ChatCompletionResponse>(rawJson, JsonOpts);
            var reply = json?.choices?.FirstOrDefault()?.message?.content?.Trim()
                     ?? json?.output?.FirstOrDefault(o => o.type == "message")?.content?.FirstOrDefault()?.text?.Trim()
                     ?? string.Empty;

            if (string.IsNullOrEmpty(reply))
                _logger.LogWarning("OpenAI returned empty reply. Raw: {Body}", rawJson);

            return ParseResponse(reply);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenAI validation failed for {ContentType} — allowing through", request.ContentType);
            return new AiValidationResult(true, "ai_exception");
        }
    }

    private static string BuildSystemPrompt()
    {
        return """
            You are a deal validation assistant. Your job is to determine whether a piece of content 
            (e.g. an online listing, email, forum post, or social media post) represents a legitimate 
            deal for a specific target product. Listings may be new, used, refurbished, or open-box — all conditions are acceptable.

            Reject the content if ANY of the following are true:
            - It is selling a single individual unit extracted from a multi-pack (e.g. 1 golf ball from a box of 12, 1 battery from a 4-pack). Do NOT reject sets or kits with a different piece count — products like golf club sets, knife sets, and tool kits commonly come in varying set sizes (e.g. a 6-piece iron set vs an 8-piece iron set are both valid).
            - It is a custom, personalized, or logo-branded version (e.g. "logo golf ball", "custom engraved")
            - It is clearly a different product that happens to mention the target product name
            - It is selling ONLY accessories, parts, or add-ons WITHOUT the main product (e.g. just a travel case, just a charger, just a mount)
            - The title contains excessive unrelated keywords (keyword stuffing for search visibility)
            - It is a collectible, commemorative, or special edition not representative of normal pricing — UNLESS the edition, variant, or sub-model name is mentioned in the product name or in any of the known aliases (e.g. if an alias contains "Circle B", then a "Circle B" edition is valid and should be approved)

            Approve the content if:
            - It is the target product in any condition (new, used, refurbished, open-box) at a reasonable price
            - Minor cosmetic descriptors (color, year model) are acceptable
            - Condition ratings, wear descriptions, or grading (e.g. "8/10 condition", "good condition", "like new") are perfectly fine
            - The main product is included even if bundled with accessories (e.g. a launch monitor bundled with a travel case is fine as long as the main device is included)
            - It is a set or kit of the target product, even if the piece count differs from the expected count (e.g. a 6-piece golf iron set when 8-piece is typical)

            Respond with EXACTLY one line in this format:
            APPROVED: <brief reason>
            or
            REJECTED: <brief reason>

            Do not include any other text.
            """;
    }

    private static string BuildUserPrompt(AiValidationRequest request)
    {
        var parts = new List<string>
        {
            $"Target product: {request.ProductName}"
        };

        if (!string.IsNullOrWhiteSpace(request.ProductBrand))
            parts.Add($"Brand: {request.ProductBrand}");
        if (request.ProductMsrp.HasValue)
            parts.Add($"MSRP: ${request.ProductMsrp.Value:F2}");
        if (request.ExpectedPackCount.HasValue && request.ExpectedPackCount.Value > 1)
            parts.Add($"Typical pack/set size (approximate): {request.ExpectedPackCount.Value} — but other set sizes are acceptable");
        if (request.KnownAliases is { Count: > 0 })
            parts.Add($"Known aliases / editions: {string.Join(", ", request.KnownAliases)}");

        parts.Add(string.Empty);
        parts.Add($"Content type: {request.ContentType}");
        parts.Add($"Title: {request.ContentTitle}");

        if (!string.IsNullOrWhiteSpace(request.ContentBody))
            parts.Add($"Description: {request.ContentBody}");
        if (request.ContentPrice.HasValue)
            parts.Add($"Price: ${request.ContentPrice.Value:F2}");

        parts.Add(string.Empty);
        parts.Add("Is this a legitimate, standard deal for the target product?");

        return string.Join("\n", parts);
    }

    private static AiValidationResult ParseResponse(string reply)
    {
        if (reply.StartsWith("APPROVED", StringComparison.OrdinalIgnoreCase))
        {
            var reason = reply.Length > 10 ? reply[10..].Trim() : "approved";
            return new AiValidationResult(true, reason);
        }
        if (reply.StartsWith("REJECTED", StringComparison.OrdinalIgnoreCase))
        {
            var reason = reply.Length > 10 ? reply[10..].Trim() : "rejected";
            return new AiValidationResult(false, reason);
        }

        // Couldn't parse — default to allowing the listing
        return new AiValidationResult(true, $"ai_unparseable: {reply}");
    }

    // --- OpenAI response models ---
    // Chat completions format (gpt-4o and earlier)
    private sealed class ChatCompletionResponse
    {
        public List<Choice>? choices { get; set; }
        // Responses API format (gpt-5-mini, newer models)
        public List<OutputItem>? output { get; set; }
    }
    private sealed class Choice
    {
        public Message? message { get; set; }
    }
    private sealed class Message
    {
        public string? content { get; set; }
    }
    // Responses API models
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
