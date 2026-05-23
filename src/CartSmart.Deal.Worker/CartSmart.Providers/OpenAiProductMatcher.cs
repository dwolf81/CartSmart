using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CartSmart.Providers;

/// <summary>
/// Matches a single retailer-listing title to one of a small set of candidate
/// products. Used as a fallback by the discovery crawler when deterministic
/// fuzzy matching scores in the uncertain band (e.g. [0.5, 0.85]). The match
/// + confidence is written to deal_candidate.ai_confidence — never auto-promoted.
/// </summary>
public interface IOpenAiProductMatcher
{
    Task<ProductMatchResult?> MatchAsync(
        string listingTitle,
        string? brandHint,
        IReadOnlyList<ProductMatchCandidate> candidates,
        CancellationToken ct);
}

public sealed record ProductMatchCandidate(int Id, string Name, string? Brand);

public sealed record ProductMatchResult(int? ProductId, decimal Confidence, string? Reason);

public class OpenAiProductMatcher : IOpenAiProductMatcher
{
    private readonly HttpClient _http;
    private readonly ILogger<OpenAiProductMatcher> _logger;
    private readonly string _apiKey;
    private readonly string _model;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OpenAiProductMatcher(
        HttpClient http,
        ILogger<OpenAiProductMatcher> logger,
        string? apiKey = null,
        string? model = null)
    {
        _http = http;
        _logger = logger;
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
        _model = model ?? Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini";
    }

    public async Task<ProductMatchResult?> MatchAsync(
        string listingTitle,
        string? brandHint,
        IReadOnlyList<ProductMatchCandidate> candidates,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogWarning("OPENAI_API_KEY not configured — skipping AI product match");
            return null;
        }
        if (candidates == null || candidates.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(listingTitle)) return null;

        var systemPrompt = """
            You match retailer listing titles to one of a fixed list of canonical golf products.
            Respond with a JSON object ONLY:
              { "product_id": <id from the list> | null, "confidence": <0..1>, "reason": "<short>" }
            Return product_id = null when none of the candidates is clearly the same product.
            Confidence above 0.85 means a high-confidence match; below 0.5 means likely wrong.
            Do not invent product ids that are not in the list.
            """;

        var candidateLines = string.Join("\n", candidates.Select(c =>
            $"- id={c.Id} brand=\"{c.Brand}\" name=\"{c.Name}\""));

        var userPrompt =
            $"Listing title: {listingTitle}\n" +
            (string.IsNullOrWhiteSpace(brandHint) ? "" : $"Listing brand hint: {brandHint}\n") +
            $"\nCandidate products:\n{candidateLines}\n";

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
                _logger.LogWarning("OpenAI product match API error {Status}: {Body}", resp.StatusCode, err);
                return null;
            }

            var rawJson = await resp.Content.ReadAsStringAsync(ct);
            var chat = JsonSerializer.Deserialize<ChatCompletionResponse>(rawJson, JsonOpts);
            var reply = chat?.choices?.FirstOrDefault()?.message?.content?.Trim();
            if (string.IsNullOrWhiteSpace(reply)) return null;

            using var doc = JsonDocument.Parse(reply);
            var root = doc.RootElement;
            int? productId = null;
            if (root.TryGetProperty("product_id", out var pidEl))
            {
                if (pidEl.ValueKind == JsonValueKind.Number) productId = pidEl.GetInt32();
                else if (pidEl.ValueKind == JsonValueKind.String && int.TryParse(pidEl.GetString(), out var p)) productId = p;
            }
            decimal confidence = 0m;
            if (root.TryGetProperty("confidence", out var confEl) && confEl.ValueKind == JsonValueKind.Number)
                confidence = Math.Clamp(confEl.GetDecimal(), 0m, 1m);
            string? reason = null;
            if (root.TryGetProperty("reason", out var rEl) && rEl.ValueKind == JsonValueKind.String)
                reason = rEl.GetString();

            // Guard: only return ids actually in the candidate set
            if (productId.HasValue && !candidates.Any(c => c.Id == productId.Value))
            {
                _logger.LogInformation("AI returned product_id {Id} not in candidate set — ignoring", productId);
                productId = null;
            }

            return new ProductMatchResult(productId, confidence, reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAI product match failed for title \"{Title}\"", listingTitle);
            return null;
        }
    }

    private sealed class ChatCompletionResponse
    {
        public List<Choice>? choices { get; set; }
    }
    private sealed class Choice { public Message? message { get; set; } }
    private sealed class Message { public string? content { get; set; } }
}
