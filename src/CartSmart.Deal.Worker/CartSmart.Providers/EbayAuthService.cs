using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;

namespace CartSmart.Providers;

public interface IEbayAuthService
{
    Task<string?> GetAccessTokenAsync(CancellationToken ct);
}

public class EbayAuthService : IEbayAuthService
{
    private readonly HttpClient _http;
    private readonly ILogger<EbayAuthService> _logger;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _scope;

    private string? _cachedToken;
    private DateTime _expiresAtUtc;

    public EbayAuthService(HttpClient http, ILogger<EbayAuthService> logger, string clientId, string clientSecret, string scope = "https://api.ebay.com/oauth/api_scope")
    {
        _http = http;
        _logger = logger;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _scope = scope;
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_cachedToken) && DateTime.UtcNow < _expiresAtUtc.AddMinutes(-5))
            return _cachedToken;

        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_clientId}:{_clientSecret}"));
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.ebay.com/identity/v1/oauth2/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "grant_type", "client_credentials" },
            { "scope", _scope }
        });
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("eBay OAuth token request failed: {Status}", resp.StatusCode);
            return null;
        }
        var payload = await resp.Content.ReadAsStringAsync(ct);
        var json = JsonSerializer.Deserialize<EbayTokenResponse>(payload);
        _cachedToken = json?.access_token;
        if (json?.expires_in != null)
            _expiresAtUtc = DateTime.UtcNow.AddSeconds(json.expires_in.Value);
        return _cachedToken;
    }

    private class EbayTokenResponse
    {
        public string? access_token { get; set; }
        public int? expires_in { get; set; }
        public string? token_type { get; set; }
        public string? scope { get; set; }
    }
}
