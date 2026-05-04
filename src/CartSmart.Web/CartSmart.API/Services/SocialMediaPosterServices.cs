using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CartSmart.API.Services;

/// <summary>
/// Posts to X (Twitter) using the v2 API with OAuth 1.0a app-user credentials.
/// Required config keys: Twitter:ApiKey, Twitter:ApiSecret, Twitter:AccessToken, Twitter:AccessTokenSecret
/// </summary>
public class TwitterPosterService : ISocialMediaPoster
{
    public string Platform => "twitter";
    public bool IsConfigured { get; }

    private readonly HttpClient _http;
    private readonly ILogger<TwitterPosterService> _logger;
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly string _accessToken;
    private readonly string _accessTokenSecret;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public TwitterPosterService(HttpClient http, ILogger<TwitterPosterService> logger,
        string apiKey, string apiSecret, string accessToken, string accessTokenSecret)
    {
        _http = http;
        _logger = logger;
        _apiKey = apiKey;
        _apiSecret = apiSecret;
        _accessToken = accessToken;
        _accessTokenSecret = accessTokenSecret;
        IsConfigured = !string.IsNullOrWhiteSpace(apiKey)
                    && !string.IsNullOrWhiteSpace(accessToken);
    }

    public async Task<bool> PostAsync(string caption, string? imageUrl, string? linkUrl, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Twitter posting skipped — credentials not configured");
            return false;
        }

        // Append link to caption if provided
        var text = string.IsNullOrWhiteSpace(linkUrl) ? caption : $"{caption}\n{linkUrl}";

        // Truncate to X's 280-char limit
        if (text.Length > 280)
            text = text[..277] + "...";

        var body = new { text };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.twitter.com/2/tweets");
            req.Headers.Authorization = BuildOAuthHeader("POST", "https://api.twitter.com/2/tweets");
            req.Content = JsonContent.Create(body, options: JsonOpts);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Twitter post failed {Status}: {Body}", resp.StatusCode, errBody);
                return false;
            }

            _logger.LogInformation("Successfully posted to Twitter");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception posting to Twitter");
            return false;
        }
    }

    /// <summary>Builds an OAuth 1.0a Authorization header for the given HTTP method and URL.</summary>
    private AuthenticationHeaderValue BuildOAuthHeader(string method, string url)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var oauthParams = new SortedDictionary<string, string>
        {
            ["oauth_consumer_key"]     = _apiKey,
            ["oauth_nonce"]            = nonce,
            ["oauth_signature_method"] = "HMAC-SHA1",
            ["oauth_timestamp"]        = timestamp,
            ["oauth_token"]            = _accessToken,
            ["oauth_version"]          = "1.0"
        };

        // Build parameter string
        var paramStr = string.Join("&", oauthParams.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        // Build signature base string
        var sigBase = $"{method}&{Uri.EscapeDataString(url)}&{Uri.EscapeDataString(paramStr)}";

        // Build signing key
        var signingKey = $"{Uri.EscapeDataString(_apiSecret)}&{Uri.EscapeDataString(_accessTokenSecret)}";

        // Compute HMAC-SHA1
        using var hmac = new System.Security.Cryptography.HMACSHA1(Encoding.ASCII.GetBytes(signingKey));
        var sig = Convert.ToBase64String(hmac.ComputeHash(Encoding.ASCII.GetBytes(sigBase)));

        oauthParams["oauth_signature"] = sig;

        var header = "OAuth " + string.Join(", ", oauthParams.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}=\"{Uri.EscapeDataString(kv.Value)}\""));

        return new AuthenticationHeaderValue("OAuth", header.Substring("OAuth ".Length));
    }
}

/// <summary>
/// Posts to a Facebook Page using the Graph API.
/// Required config keys: Facebook:PageId, Facebook:PageAccessToken
/// </summary>
public class FacebookPosterService : ISocialMediaPoster
{
    public string Platform => "facebook";
    public bool IsConfigured { get; }

    private readonly HttpClient _http;
    private readonly ILogger<FacebookPosterService> _logger;
    private readonly string _pageId;
    private readonly string _accessToken;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public FacebookPosterService(HttpClient http, ILogger<FacebookPosterService> logger,
        string pageId, string accessToken)
    {
        _http = http;
        _logger = logger;
        _pageId = pageId;
        _accessToken = accessToken;
        IsConfigured = !string.IsNullOrWhiteSpace(pageId) && !string.IsNullOrWhiteSpace(accessToken);
    }

    public async Task<bool> PostAsync(string caption, string? imageUrl, string? linkUrl, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Facebook posting skipped — credentials not configured");
            return false;
        }

        try
        {
            // Facebook: post with link embeds the image automatically from Open Graph tags
            var message = string.IsNullOrWhiteSpace(linkUrl) ? caption : $"{caption}\n{linkUrl}";

            var postUrl = $"https://graph.facebook.com/v19.0/{_pageId}/feed"
                        + $"?access_token={Uri.EscapeDataString(_accessToken)}";

            var body = new Dictionary<string, string> { ["message"] = message };
            if (!string.IsNullOrWhiteSpace(linkUrl))
                body["link"] = linkUrl;

            using var resp = await _http.PostAsync(postUrl,
                new FormUrlEncodedContent(body), ct);

            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Facebook post failed {Status}: {Body}", resp.StatusCode, errBody);
                return false;
            }

            _logger.LogInformation("Successfully posted to Facebook");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception posting to Facebook");
            return false;
        }
    }
}

/// <summary>
/// Posts to Instagram Business via the Instagram Graph API.
/// Required config keys: Instagram:UserId, Instagram:AccessToken
/// Flow: create media container → publish container.
/// </summary>
public class InstagramPosterService : ISocialMediaPoster
{
    public string Platform => "instagram";
    public bool IsConfigured { get; }

    private readonly HttpClient _http;
    private readonly ILogger<InstagramPosterService> _logger;
    private readonly string _igUserId;
    private readonly string _accessToken;

    public InstagramPosterService(HttpClient http, ILogger<InstagramPosterService> logger,
        string igUserId, string accessToken)
    {
        _http = http;
        _logger = logger;
        _igUserId = igUserId;
        _accessToken = accessToken;
        IsConfigured = !string.IsNullOrWhiteSpace(igUserId) && !string.IsNullOrWhiteSpace(accessToken);
    }

    public async Task<bool> PostAsync(string caption, string? imageUrl, string? linkUrl, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Instagram posting skipped — credentials not configured");
            return false;
        }

        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            _logger.LogWarning("Instagram posting skipped — no image URL provided");
            return false;
        }

        try
        {
            // Step 1: Create media container
            var createUrl = $"https://graph.facebook.com/v19.0/{_igUserId}/media"
                          + $"?access_token={Uri.EscapeDataString(_accessToken)}";

            var postCaption = caption;
            if (!string.IsNullOrWhiteSpace(linkUrl))
                postCaption += $"\n\n🔗 Link in bio";

            var createBody = new Dictionary<string, string>
            {
                ["image_url"] = imageUrl,
                ["caption"]   = postCaption
            };

            using var createResp = await _http.PostAsync(createUrl,
                new FormUrlEncodedContent(createBody), ct);

            if (!createResp.IsSuccessStatusCode)
            {
                var errBody = await createResp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Instagram create container failed {Status}: {Body}",
                    createResp.StatusCode, errBody);
                return false;
            }

            var createJson = await createResp.Content.ReadAsStringAsync(ct);
            using var doc = System.Text.Json.JsonDocument.Parse(createJson);
            if (!doc.RootElement.TryGetProperty("id", out var idEl))
            {
                _logger.LogWarning("Instagram create container returned no id: {Body}", createJson);
                return false;
            }

            var containerId = idEl.GetString();

            // Step 2: Publish the container
            var publishUrl = $"https://graph.facebook.com/v19.0/{_igUserId}/media_publish"
                           + $"?access_token={Uri.EscapeDataString(_accessToken)}";

            var publishBody = new Dictionary<string, string>
            {
                ["creation_id"] = containerId!
            };

            using var publishResp = await _http.PostAsync(publishUrl,
                new FormUrlEncodedContent(publishBody), ct);

            if (!publishResp.IsSuccessStatusCode)
            {
                var errBody = await publishResp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Instagram publish failed {Status}: {Body}",
                    publishResp.StatusCode, errBody);
                return false;
            }

            _logger.LogInformation("Successfully posted to Instagram");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception posting to Instagram");
            return false;
        }
    }
}
