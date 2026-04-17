using CartSmart.API.Models;
using CartSmart.Core.Worker;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CartSmart.Providers;

// ─── Email source provider (Gmail API or generic endpoint) ──────────────────────

/// <summary>
/// Collects deal signals from promotional emails.
///
/// Supports two modes configured via the ingestion_source.config JSONB:
///
/// 1. Gmail API (Google Workspace) — set "mode": "gmail":
///    {
///      "mode": "gmail",
///      "client_id": "...apps.googleusercontent.com",
///      "client_secret": "...",
///      "refresh_token": "...",
///      "label_ids": ["INBOX"],          // optional, default ["INBOX"]
///      "query": "category:promotions",   // optional Gmail search filter
///      "max_results": 50                 // optional, default 50
///    }
///    Obtain refresh_token via OAuth2 consent flow with scope
///    https://www.googleapis.com/auth/gmail.modify
///    (modify is needed to move ingested emails and mark them as read)
///
/// 2. Generic HTTP endpoint — set "mode": "endpoint" (or omit mode):
///    {
///      "endpoint": "https://your-email-proxy/api/emails",
///      "api_key": "..."
///    }
/// </summary>
public class EmailSignalSourceProvider : ISignalSourceProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<EmailSignalSourceProvider> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public EmailSignalSourceProvider(HttpClient http, ILogger<EmailSignalSourceProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public SignalSourceType SourceType => SignalSourceType.Email;

    public async Task<IReadOnlyList<CollectedSignal>> CollectAsync(IngestionSource source, CancellationToken ct)
    {
        var config = JsonSerializer.Deserialize<EmailSourceConfig>(source.Config, JsonOpts);
        if (config is null)
        {
            _logger.LogWarning("Email source {SourceId} has no config", source.Id);
            return [];
        }

        return config.Mode?.ToLowerInvariant() switch
        {
            "gmail" => await CollectGmailAsync(source, config, ct),
            _ => await CollectEndpointAsync(source, config, ct)
        };
    }

    // ── Gmail API mode ──────────────────────────────────────────────────────

    private async Task<IReadOnlyList<CollectedSignal>> CollectGmailAsync(IngestionSource source, EmailSourceConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.ClientId) ||
            string.IsNullOrWhiteSpace(config.ClientSecret) ||
            string.IsNullOrWhiteSpace(config.RefreshToken))
        {
            _logger.LogWarning("Gmail source {SourceId} missing client_id, client_secret, or refresh_token", source.Id);
            return [];
        }

        try
        {
            // Exchange refresh token for access token
            var accessToken = await GetGmailAccessTokenAsync(config, ct);
            if (string.IsNullOrWhiteSpace(accessToken))
                return [];

            // Build Gmail list query
            var labelIds = config.LabelIds ?? ["INBOX"];
            var maxResults = config.MaxResults ?? 50;
            var queryParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(config.Query))
                queryParts.Add(config.Query);
            if (source.LastPolledAt.HasValue)
                queryParts.Add($"after:{source.LastPolledAt.Value:yyyy/MM/dd}");

            var qParam = queryParts.Count > 0 ? $"&q={Uri.EscapeDataString(string.Join(" ", queryParts))}" : "";
            var labelParam = string.Join("", labelIds.Select(l => $"&labelIds={Uri.EscapeDataString(l)}"));
            var listUrl = $"https://gmail.googleapis.com/gmail/v1/users/me/messages?maxResults={maxResults}{labelParam}{qParam}";

            using var listReq = new HttpRequestMessage(HttpMethod.Get, listUrl);
            listReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            using var listResp = await _http.SendAsync(listReq, ct);
            if (!listResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gmail list for source {SourceId} returned {Status}", source.Id, listResp.StatusCode);
                return [];
            }

            var listJson = await listResp.Content.ReadAsStringAsync(ct);
            using var listDoc = JsonDocument.Parse(listJson);

            if (!listDoc.RootElement.TryGetProperty("messages", out var messages))
                return [];

            var signals = new List<CollectedSignal>();
            var collectedMessageIds = new List<string>();

            foreach (var msgRef in messages.EnumerateArray())
            {
                if (ct.IsCancellationRequested) break;

                var msgId = msgRef.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(msgId)) continue;

                // Fetch full message (includes body parts for forwarded emails)
                var msgUrl = $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{msgId}?format=full";
                using var msgReq = new HttpRequestMessage(HttpMethod.Get, msgUrl);
                msgReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                using var msgResp = await _http.SendAsync(msgReq, ct);
                if (!msgResp.IsSuccessStatusCode) continue;

                var msgJson = await msgResp.Content.ReadAsStringAsync(ct);
                using var msgDoc = JsonDocument.Parse(msgJson);
                var root = msgDoc.RootElement;

                var snippet = root.TryGetProperty("snippet", out var snip) ? snip.GetString() : null;
                string? subject = null, from = null;

                if (root.TryGetProperty("payload", out var payload))
                {
                    if (payload.TryGetProperty("headers", out var headers))
                    {
                        foreach (var hdr in headers.EnumerateArray())
                        {
                            var name = hdr.TryGetProperty("name", out var n) ? n.GetString() : null;
                            var value = hdr.TryGetProperty("value", out var v) ? v.GetString() : null;
                            if (string.Equals(name, "Subject", StringComparison.OrdinalIgnoreCase)) subject = value;
                            if (string.Equals(name, "From", StringComparison.OrdinalIgnoreCase)) from = value;
                        }
                    }
                }

                // Extract full body text from MIME parts (handles forwarded & multipart emails)
                var bodyText = ExtractBodyFromPayload(root);

                signals.Add(new CollectedSignal(
                    ExternalId: msgId,
                    Title: subject,
                    Body: bodyText ?? snippet,
                    Url: null,
                    Author: from,
                    RawJson: msgJson
                ));
                collectedMessageIds.Add(msgId);
            }

            // Move collected emails to "Ingested" label and mark as read
            if (collectedMessageIds.Count > 0)
                await MoveToIngestedAsync(accessToken, collectedMessageIds, ct);

            return signals;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to collect Gmail for source {SourceId}", source.Id);
            return [];
        }
    }

    // ── Extract full body text from Gmail MIME payload ───────────────────────

    /// <summary>
    /// Recursively walks the Gmail message payload to extract text/plain (preferred)
    /// or text/html body. Handles multipart/alternative, multipart/mixed, and
    /// forwarded messages where the original content is nested in child parts.
    /// </summary>
    private static string? ExtractBodyFromPayload(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload))
            return null;

        var textParts = new List<string>();
        var htmlParts = new List<string>();
        CollectBodyParts(payload, textParts, htmlParts);

        // Prefer plain text; fall back to HTML stripped of tags
        if (textParts.Count > 0)
            return string.Join("\n\n", textParts);

        if (htmlParts.Count > 0)
        {
            var combined = string.Join("\n\n", htmlParts);
            return StripHtmlTags(combined);
        }

        return null;
    }

    private static void CollectBodyParts(JsonElement part, List<string> textParts, List<string> htmlParts)
    {
        var mimeType = part.TryGetProperty("mimeType", out var mt) ? mt.GetString() : null;

        // If this part has a body with data, decode it
        if (part.TryGetProperty("body", out var body) &&
            body.TryGetProperty("data", out var data))
        {
            var encoded = data.GetString();
            if (!string.IsNullOrWhiteSpace(encoded))
            {
                var decoded = DecodeBase64Url(encoded);
                if (!string.IsNullOrWhiteSpace(decoded))
                {
                    if (string.Equals(mimeType, "text/plain", StringComparison.OrdinalIgnoreCase))
                        textParts.Add(decoded);
                    else if (string.Equals(mimeType, "text/html", StringComparison.OrdinalIgnoreCase))
                        htmlParts.Add(decoded);
                }
            }
        }

        // Recurse into child parts (multipart/alternative, multipart/mixed, forwarded messages)
        if (part.TryGetProperty("parts", out var parts))
        {
            foreach (var child in parts.EnumerateArray())
                CollectBodyParts(child, textParts, htmlParts);
        }
    }

    private static string DecodeBase64Url(string base64Url)
    {
        var base64 = base64Url.Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }
        var bytes = Convert.FromBase64String(base64);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static string StripHtmlTags(string html)
    {
        // Simple tag removal — sufficient for extracting deal text from emails
        return System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ")
            .Replace("&nbsp;", " ")
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&#39;", "'")
            .Replace("&quot;", "\"");
    }

    private async Task<string?> GetGmailAccessTokenAsync(EmailSourceConfig config, CancellationToken ct)
    {
        try
        {
            var tokenBody = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("client_id", config.ClientId!),
                new KeyValuePair<string, string>("client_secret", config.ClientSecret!),
                new KeyValuePair<string, string>("refresh_token", config.RefreshToken!),
                new KeyValuePair<string, string>("grant_type", "refresh_token")
            ]);

            using var tokenResp = await _http.PostAsync("https://oauth2.googleapis.com/token", tokenBody, ct);
            if (!tokenResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gmail token exchange failed: {Status}", tokenResp.StatusCode);
                return null;
            }

            var tokenJson = await tokenResp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(tokenJson);
            return doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gmail OAuth2 token exchange failed");
            return null;
        }
    }

    // ── Post-collection: move to "Ingested" label and mark as read ──────────

    private async Task MoveToIngestedAsync(string accessToken, List<string> messageIds, CancellationToken ct)
    {
        try
        {
            var ingestedLabelId = await GetOrCreateLabelAsync(accessToken, "Ingested", ct);
            if (string.IsNullOrWhiteSpace(ingestedLabelId))
            {
                _logger.LogWarning("Could not find or create 'Ingested' Gmail label — skipping move");
                return;
            }

            // Batch modify: add "Ingested" label, remove "INBOX" and "UNREAD"
            var body = JsonSerializer.Serialize(new
            {
                ids = messageIds,
                addLabelIds = new[] { ingestedLabelId },
                removeLabelIds = new[] { "INBOX", "UNREAD" }
            }, JsonOpts);

            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://gmail.googleapis.com/gmail/v1/users/me/messages/batchModify");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
                _logger.LogInformation("Moved {Count} emails to 'Ingested' and marked as read", messageIds.Count);
            else
                _logger.LogWarning("Gmail batchModify returned {Status}", resp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move emails to 'Ingested' label");
        }
    }

    private async Task<string?> GetOrCreateLabelAsync(string accessToken, string labelName, CancellationToken ct)
    {
        // List existing labels
        using var listReq = new HttpRequestMessage(HttpMethod.Get,
            "https://gmail.googleapis.com/gmail/v1/users/me/labels");
        listReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        using var listResp = await _http.SendAsync(listReq, ct);
        if (!listResp.IsSuccessStatusCode) return null;

        var listJson = await listResp.Content.ReadAsStringAsync(ct);
        using var listDoc = JsonDocument.Parse(listJson);

        if (listDoc.RootElement.TryGetProperty("labels", out var labels))
        {
            foreach (var label in labels.EnumerateArray())
            {
                var name = label.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.Equals(name, labelName, StringComparison.OrdinalIgnoreCase))
                    return label.GetProperty("id").GetString();
            }
        }

        // Label doesn't exist — create it
        var createBody = JsonSerializer.Serialize(new
        {
            name = labelName,
            labelListVisibility = "labelShow",
            messageListVisibility = "show"
        }, JsonOpts);

        using var createReq = new HttpRequestMessage(HttpMethod.Post,
            "https://gmail.googleapis.com/gmail/v1/users/me/labels");
        createReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        createReq.Content = new StringContent(createBody, System.Text.Encoding.UTF8, "application/json");

        using var createResp = await _http.SendAsync(createReq, ct);
        if (!createResp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to create Gmail label '{Label}': {Status}", labelName, createResp.StatusCode);
            return null;
        }

        var createJson = await createResp.Content.ReadAsStringAsync(ct);
        using var createDoc = JsonDocument.Parse(createJson);
        return createDoc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    // ── Generic HTTP endpoint mode ──────────────────────────────────────────

    private async Task<IReadOnlyList<CollectedSignal>> CollectEndpointAsync(IngestionSource source, EmailSourceConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.Endpoint))
        {
            _logger.LogWarning("Email source {SourceId} has no endpoint configured", source.Id);
            return [];
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, config.Endpoint);
            if (!string.IsNullOrWhiteSpace(config.ApiKey))
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {config.ApiKey}");

            if (source.LastPolledAt.HasValue)
            {
                var since = source.LastPolledAt.Value.ToString("o");
                request.RequestUri = new Uri($"{config.Endpoint}?since={Uri.EscapeDataString(since)}");
            }

            using var resp = await _http.SendAsync(request, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Email source {SourceId} returned {Status}", source.Id, resp.StatusCode);
                return [];
            }

            var emails = await resp.Content.ReadFromJsonAsync<List<EmailPayload>>(JsonOpts, ct) ?? [];

            return emails
                .Where(e => !string.IsNullOrWhiteSpace(e.Subject) || !string.IsNullOrWhiteSpace(e.Body))
                .Select(e => new CollectedSignal(
                    ExternalId: e.MessageId ?? $"email-{e.Subject?.GetHashCode():X8}-{e.ReceivedAt:yyyyMMddHHmmss}",
                    Title: e.Subject,
                    Body: e.Body,
                    Url: e.Links?.FirstOrDefault(),
                    Author: e.From,
                    RawJson: JsonSerializer.Serialize(e, JsonOpts)
                ))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to collect emails from source {SourceId}", source.Id);
            return [];
        }
    }

    private sealed class EmailSourceConfig
    {
        // Mode: "gmail" or "endpoint" (default)
        public string? Mode { get; set; }

        // Gmail API fields
        [JsonPropertyName("client_id")]
        public string? ClientId { get; set; }
        [JsonPropertyName("client_secret")]
        public string? ClientSecret { get; set; }
        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }
        [JsonPropertyName("label_ids")]
        public List<string>? LabelIds { get; set; }
        public string? Query { get; set; }
        [JsonPropertyName("max_results")]
        public int? MaxResults { get; set; }

        // Generic endpoint fields
        public string? Endpoint { get; set; }
        [JsonPropertyName("api_key")]
        public string? ApiKey { get; set; }
    }

    private sealed class EmailPayload
    {
        public string? MessageId { get; set; }
        public string? Subject { get; set; }
        public string? Body { get; set; }
        public string? From { get; set; }
        public DateTime? ReceivedAt { get; set; }
        public List<string>? Links { get; set; }
    }
}

// ─── Reddit source provider ─────────────────────────────────────────────────────

/// <summary>
/// Collects deal signals from Reddit subreddits.
///
/// Config (single or multi-subreddit):
///   { "subreddit": "buildapcsales", "sort": "new", "limit": 50 }
///   { "subreddits": ["buildapcsales", "deals", "frugal"], "sort": "new", "limit": 50 }
///
/// Uses the public Reddit JSON API (no auth required for read-only).
/// When "subreddits" (array) is provided it polls each sub. "subreddit" (string)
/// is kept for backwards compatibility / single-sub convenience.
/// </summary>
public class RedditSignalSourceProvider : ISignalSourceProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<RedditSignalSourceProvider> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public RedditSignalSourceProvider(HttpClient http, ILogger<RedditSignalSourceProvider> logger)
    {
        _http = http;
        _logger = logger;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CartSmart/1.0 (deal-ingestion)");
    }

    public SignalSourceType SourceType => SignalSourceType.Reddit;

    public async Task<IReadOnlyList<CollectedSignal>> CollectAsync(IngestionSource source, CancellationToken ct)
    {
        var config = JsonSerializer.Deserialize<RedditSourceConfig>(source.Config, JsonOpts);
        if (config is null)
        {
            _logger.LogWarning("Reddit source {SourceId} has no config", source.Id);
            return [];
        }

        // Build list of subreddits from either "subreddits" array or single "subreddit" string
        var subs = new List<string>();
        if (config.Subreddits is { Count: > 0 })
            subs.AddRange(config.Subreddits);
        if (!string.IsNullOrWhiteSpace(config.Subreddit))
            subs.Add(config.Subreddit);

        subs = subs
            .Select(s => s.TrimStart('/').TrimStart('r').TrimStart('/'))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (subs.Count == 0)
        {
            _logger.LogWarning("Reddit source {SourceId} has no subreddit(s) configured", source.Id);
            return [];
        }

        var sort = config.Sort ?? "new";
        var limit = config.Limit ?? 50;
        var allSignals = new List<CollectedSignal>();

        foreach (var subreddit in subs)
        {
            if (ct.IsCancellationRequested) break;

            var url = $"https://www.reddit.com/r/{Uri.EscapeDataString(subreddit)}/{sort}.json?limit={limit}&raw_json=1";

            try
            {
                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Reddit source {SourceId} returned {Status} for /r/{Subreddit}", source.Id, resp.StatusCode, subreddit);
                    continue;
                }

                var rawJson = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(rawJson);
                var children = doc.RootElement
                    .GetProperty("data")
                    .GetProperty("children");

                foreach (var child in children.EnumerateArray())
                {
                    var data = child.GetProperty("data");
                    var postId = data.GetProperty("id").GetString() ?? "";
                    var title = data.TryGetProperty("title", out var t) ? t.GetString() : null;
                    var selftext = data.TryGetProperty("selftext", out var s) ? s.GetString() : null;
                    var postUrl = data.TryGetProperty("url", out var u) ? u.GetString() : null;
                    var author = data.TryGetProperty("author", out var a) ? a.GetString() : null;
                    var permalink = data.TryGetProperty("permalink", out var p) ? p.GetString() : null;

                    allSignals.Add(new CollectedSignal(
                        ExternalId: postId,
                        Title: title,
                        Body: selftext,
                        Url: postUrl ?? (permalink != null ? $"https://www.reddit.com{permalink}" : null),
                        Author: author,
                        RawJson: data.GetRawText()
                    ));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to collect from Reddit source {SourceId} (/r/{Subreddit})", source.Id, subreddit);
            }
        }

        return allSignals;
    }

    private sealed class RedditSourceConfig
    {
        // Single subreddit (backwards-compatible)
        public string? Subreddit { get; set; }
        // Multiple subreddits in one source
        public List<string>? Subreddits { get; set; }
        public string? Sort { get; set; }
        public int? Limit { get; set; }
    }
}

// ─── Social (X/Twitter) source provider ─────────────────────────────────────────

/// <summary>
/// Collects deal signals from X (Twitter).
///
/// Supports two modes configured via config JSONB:
///
/// 1. Search mode (default) — searches recent tweets matching a query:
///    {
///      "bearer_token": "...",
///      "query": "deal OR coupon OR discount",
///      "max_results": 50
///    }
///    query uses full X search syntax: https://developer.x.com/en/docs/x-api/tweets/search/integrate/build-a-query
///    To limit to specific accounts use "from:" operators, e.g.:
///      "query": "from:woot OR from:mattswider OR from:IGNDeals"
///
/// 2. User timeline mode — pulls recent tweets from specific user IDs:
///    {
///      "bearer_token": "...",
///      "user_ids": ["12345", "67890"],
///      "max_results": 20
///    }
///    Get user IDs from https://tweeterid.com or the X API users/by/username endpoint.
///    Pulls the timeline of each listed user.
/// </summary>
public class SocialSignalSourceProvider : ISignalSourceProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<SocialSignalSourceProvider> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SocialSignalSourceProvider(HttpClient http, ILogger<SocialSignalSourceProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public SignalSourceType SourceType => SignalSourceType.Social;

    public async Task<IReadOnlyList<CollectedSignal>> CollectAsync(IngestionSource source, CancellationToken ct)
    {
        var config = JsonSerializer.Deserialize<SocialSourceConfig>(source.Config, JsonOpts);
        if (config is null || string.IsNullOrWhiteSpace(config.BearerToken))
        {
            _logger.LogWarning("Social source {SourceId} missing bearer_token", source.Id);
            return [];
        }

        // User timeline mode: pull tweets from specific user IDs
        if (config.UserIds is { Count: > 0 })
            return await CollectUserTimelinesAsync(source, config, ct);

        // Search mode: query-based recent search
        if (!string.IsNullOrWhiteSpace(config.Query))
            return await CollectSearchAsync(source, config, ct);

        _logger.LogWarning("Social source {SourceId} has neither query nor user_ids configured", source.Id);
        return [];
    }

    // ── Search mode ─────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<CollectedSignal>> CollectSearchAsync(IngestionSource source, SocialSourceConfig config, CancellationToken ct)
    {
        var maxResults = config.MaxResults ?? 50;
        var url = $"https://api.x.com/2/tweets/search/recent?query={Uri.EscapeDataString(config.Query!)}&max_results={maxResults}&tweet.fields=created_at,author_id,entities";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.BearerToken);

            using var resp = await _http.SendAsync(request, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Social search source {SourceId} returned {Status}", source.Id, resp.StatusCode);
                return [];
            }

            return await ParseTweetsResponseAsync(resp, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search from Social source {SourceId}", source.Id);
            return [];
        }
    }

    // ── User timeline mode ──────────────────────────────────────────────────

    private async Task<IReadOnlyList<CollectedSignal>> CollectUserTimelinesAsync(IngestionSource source, SocialSourceConfig config, CancellationToken ct)
    {
        var maxResults = config.MaxResults ?? 20;
        var allSignals = new List<CollectedSignal>();

        foreach (var userId in config.UserIds!)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(userId)) continue;

            var url = $"https://api.x.com/2/users/{Uri.EscapeDataString(userId)}/tweets?max_results={maxResults}&tweet.fields=created_at,author_id,entities";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.BearerToken);

                using var resp = await _http.SendAsync(request, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Social timeline source {SourceId} returned {Status} for user {UserId}", source.Id, resp.StatusCode, userId);
                    continue;
                }

                var tweets = await ParseTweetsResponseAsync(resp, ct);
                allSignals.AddRange(tweets);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to collect timeline for user {UserId} from Social source {SourceId}", userId, source.Id);
            }
        }

        return allSignals;
    }

    // ── Shared tweet parsing ────────────────────────────────────────────────

    private static async Task<IReadOnlyList<CollectedSignal>> ParseTweetsResponseAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var rawJson = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(rawJson);

        if (!doc.RootElement.TryGetProperty("data", out var dataArray))
            return [];

        var signals = new List<CollectedSignal>();
        foreach (var tweet in dataArray.EnumerateArray())
        {
            var id = tweet.GetProperty("id").GetString() ?? "";
            var text = tweet.TryGetProperty("text", out var txt) ? txt.GetString() : null;
            var authorId = tweet.TryGetProperty("author_id", out var aid) ? aid.GetString() : null;

            string? tweetUrl = null;
            if (tweet.TryGetProperty("entities", out var entities) &&
                entities.TryGetProperty("urls", out var urls))
            {
                var first = urls.EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Undefined &&
                    first.TryGetProperty("expanded_url", out var expandedUrl))
                {
                    tweetUrl = expandedUrl.GetString();
                }
            }

            signals.Add(new CollectedSignal(
                ExternalId: id,
                Title: text?.Length > 100 ? text[..100] : text,
                Body: text,
                Url: tweetUrl ?? $"https://x.com/i/status/{id}",
                Author: authorId,
                RawJson: tweet.GetRawText()
            ));
        }

        return signals;
    }

    private sealed class SocialSourceConfig
    {
        [JsonPropertyName("bearer_token")]
        public string? BearerToken { get; set; }
        // Search mode
        public string? Query { get; set; }
        // User timeline mode — list of X user IDs
        [JsonPropertyName("user_ids")]
        public List<string>? UserIds { get; set; }
        [JsonPropertyName("max_results")]
        public int? MaxResults { get; set; }
    }
}

// ─── Retail site source provider (API + HTML scrape fallback) ────────────────────

/// <summary>
/// Collects deal signals from retail sites via their RSS/API feeds or HTML scraping.
/// Config: { "feed_url": "https://...", "format": "json|rss", "api_key": "..." }
/// </summary>
public class RetailSignalSourceProvider : ISignalSourceProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<RetailSignalSourceProvider> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public RetailSignalSourceProvider(HttpClient http, ILogger<RetailSignalSourceProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public SignalSourceType SourceType => SignalSourceType.Retail;

    public async Task<IReadOnlyList<CollectedSignal>> CollectAsync(IngestionSource source, CancellationToken ct)
    {
        var config = JsonSerializer.Deserialize<RetailSourceConfig>(source.Config, JsonOpts);
        if (config is null || string.IsNullOrWhiteSpace(config.FeedUrl))
        {
            _logger.LogWarning("Retail source {SourceId} has no feed_url configured", source.Id);
            return [];
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, config.FeedUrl);
            if (!string.IsNullOrWhiteSpace(config.ApiKey))
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {config.ApiKey}");

            using var resp = await _http.SendAsync(request, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Retail source {SourceId} returned {Status}", source.Id, resp.StatusCode);
                return [];
            }

            var format = config.Format?.ToLowerInvariant() ?? "json";
            if (format == "rss")
                return await ParseRssFeedAsync(source.Id, resp, ct);

            return await ParseJsonFeedAsync(source.Id, resp, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to collect from Retail source {SourceId}", source.Id);
            return [];
        }
    }

    private async Task<IReadOnlyList<CollectedSignal>> ParseJsonFeedAsync(long sourceId, HttpResponseMessage resp, CancellationToken ct)
    {
        var rawJson = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            var items = JsonSerializer.Deserialize<List<RetailFeedItem>>(rawJson, JsonOpts) ?? [];
            return items
                .Where(i => !string.IsNullOrWhiteSpace(i.Title) || !string.IsNullOrWhiteSpace(i.Url))
                .Select(i => new CollectedSignal(
                    ExternalId: i.Id ?? i.Url ?? Guid.NewGuid().ToString("N"),
                    Title: i.Title,
                    Body: i.Description,
                    Url: i.Url,
                    Author: i.Store,
                    RawJson: JsonSerializer.Serialize(i, JsonOpts)
                ))
                .ToList();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse JSON feed for Retail source {SourceId}", sourceId);
            return [];
        }
    }

    private async Task<IReadOnlyList<CollectedSignal>> ParseRssFeedAsync(long sourceId, HttpResponseMessage resp, CancellationToken ct)
    {
        // Simple RSS/XML parsing using string operations (no XML dependency)
        var content = await resp.Content.ReadAsStringAsync(ct);
        var signals = new List<CollectedSignal>();
        var itemStart = 0;

        while ((itemStart = content.IndexOf("<item>", itemStart, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var itemEnd = content.IndexOf("</item>", itemStart, StringComparison.OrdinalIgnoreCase);
            if (itemEnd < 0) break;

            var itemXml = content[itemStart..(itemEnd + 7)];
            var title = ExtractXmlElement(itemXml, "title");
            var link = ExtractXmlElement(itemXml, "link");
            var description = ExtractXmlElement(itemXml, "description");
            var guid = ExtractXmlElement(itemXml, "guid");

            signals.Add(new CollectedSignal(
                ExternalId: guid ?? link ?? $"rss-{title?.GetHashCode():X8}",
                Title: title,
                Body: description,
                Url: link,
                Author: null,
                RawJson: null
            ));

            itemStart = itemEnd + 7;
        }

        return signals;
    }

    private static string? ExtractXmlElement(string xml, string element)
    {
        var openTag = $"<{element}>";
        var closeTag = $"</{element}>";
        var cdataOpen = $"<{element}><![CDATA[";

        var start = xml.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += openTag.Length;

        // Handle CDATA
        if (xml[start..].StartsWith("<![CDATA[", StringComparison.OrdinalIgnoreCase))
        {
            start += 9; // skip <![CDATA[
            var cdataEnd = xml.IndexOf("]]>", start, StringComparison.OrdinalIgnoreCase);
            return cdataEnd >= 0 ? xml[start..cdataEnd].Trim() : null;
        }

        var end = xml.IndexOf(closeTag, start, StringComparison.OrdinalIgnoreCase);
        return end >= 0 ? xml[start..end].Trim() : null;
    }

    private sealed class RetailSourceConfig
    {
        [JsonPropertyName("feed_url")]
        public string? FeedUrl { get; set; }
        public string? Format { get; set; }
        [JsonPropertyName("api_key")]
        public string? ApiKey { get; set; }
    }

    private sealed class RetailFeedItem
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? Url { get; set; }
        public string? Store { get; set; }
        public decimal? Price { get; set; }
    }
}

// ─── Forum / deal community source provider ─────────────────────────────────────

/// <summary>
/// Collects deal signals from forums and deal communities via API or HTML.
/// Config: { "api_url": "https://...", "api_key": "...", "page_size": 50 }
/// </summary>
public class ForumSignalSourceProvider : ISignalSourceProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<ForumSignalSourceProvider> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ForumSignalSourceProvider(HttpClient http, ILogger<ForumSignalSourceProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public SignalSourceType SourceType => SignalSourceType.Forum;

    public async Task<IReadOnlyList<CollectedSignal>> CollectAsync(IngestionSource source, CancellationToken ct)
    {
        var config = JsonSerializer.Deserialize<ForumSourceConfig>(source.Config, JsonOpts);
        if (config is null || string.IsNullOrWhiteSpace(config.ApiUrl))
        {
            _logger.LogWarning("Forum source {SourceId} has no api_url configured", source.Id);
            return [];
        }

        try
        {
            var pageSize = config.PageSize ?? 50;
            var url = $"{config.ApiUrl}?page_size={pageSize}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(config.ApiKey))
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {config.ApiKey}");

            using var resp = await _http.SendAsync(request, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Forum source {SourceId} returned {Status}", source.Id, resp.StatusCode);
                return [];
            }

            var rawJson = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(rawJson);

            // Support both root-array and { "posts": [...] } formats
            JsonElement postsArray;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                postsArray = doc.RootElement;
            }
            else if (doc.RootElement.TryGetProperty("posts", out var p) ||
                     doc.RootElement.TryGetProperty("threads", out p) ||
                     doc.RootElement.TryGetProperty("topics", out p) ||
                     doc.RootElement.TryGetProperty("data", out p))
            {
                postsArray = p;
            }
            else
            {
                _logger.LogWarning("Forum source {SourceId}: unable to find posts array in response", source.Id);
                return [];
            }

            var signals = new List<CollectedSignal>();
            foreach (var post in postsArray.EnumerateArray())
            {
                var id = TryGetString(post, "id") ?? TryGetString(post, "thread_id") ?? TryGetString(post, "topic_id");
                var title = TryGetString(post, "title") ?? TryGetString(post, "subject");
                var body = TryGetString(post, "body") ?? TryGetString(post, "content") ?? TryGetString(post, "message");
                var postUrl = TryGetString(post, "url") ?? TryGetString(post, "link");
                var author = TryGetString(post, "author") ?? TryGetString(post, "username") ?? TryGetString(post, "user");

                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
                    continue;

                signals.Add(new CollectedSignal(
                    ExternalId: id ?? $"forum-{title?.GetHashCode():X8}-{body?.GetHashCode():X8}",
                    Title: title,
                    Body: body,
                    Url: postUrl,
                    Author: author,
                    RawJson: post.GetRawText()
                ));
            }

            return signals;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to collect from Forum source {SourceId}", source.Id);
            return [];
        }
    }

    private static string? TryGetString(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString();
        return null;
    }

    private sealed class ForumSourceConfig
    {
        [JsonPropertyName("api_url")]
        public string? ApiUrl { get; set; }
        [JsonPropertyName("api_key")]
        public string? ApiKey { get; set; }
        [JsonPropertyName("page_size")]
        public int? PageSize { get; set; }
    }
}
