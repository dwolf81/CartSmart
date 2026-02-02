using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using CartSmart.Core.Worker;

namespace CartSmart.Providers;

public class AmazonStoreClient : IStoreClient
{
    private readonly HttpClient _http;
    private readonly ILogger<AmazonStoreClient> _logger;
    private readonly string _apiKey;

    public StoreType StoreType => StoreType.Amazon;
    public bool SupportsSoldStatus => false;
    public bool SupportsApi => true;

    public AmazonStoreClient(HttpClient http, ILogger<AmazonStoreClient> logger, IConfiguration config)
    {
        _http = http;
        _logger = logger;
        _apiKey = config["AMAZON_API_KEY"] ?? throw new InvalidOperationException("Missing AMAZON_API_KEY");
    }

    public async Task<StoreProductData?> GetByUrlAsync(string productUrl, CancellationToken ct)
    {
        // Derive ASIN or product identifier from URL
        var asin = ExtractAsin(productUrl);
        if (asin == null) return null;

        var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.amazon.example/products/{asin}");
        req.Headers.TryAddWithoutValidation("X-Api-Key", _apiKey);

        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new StoreProductData(null, "USD", false, false, true, DateTime.UtcNow); // treat as discontinued
            return null;
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        // Parse JSON (pseudo)
        // dynamic doc = JsonSerializer.Deserialize<JsonElement>(json);
        // decimal price = doc.GetProperty(\"price\").GetDecimal();
        // bool inStock = doc.GetProperty(\"inStock\").GetBoolean();

        // Placeholder values
        return new StoreProductData(99.99m, "USD", true, false, false, DateTime.UtcNow);
    }

    private string? ExtractAsin(string url)
    {
        // rudimentary parse
        var marker = "dp";
        var idx = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var start = idx + marker.Length;
        var end = url.IndexOf('/', start);
        return end > start ? url.Substring(start, end - start) : url[start..].Split('?', '#')[0];
    }

     public async Task<IReadOnlyList<CartSmart.Core.Worker.NewListing>> SearchNewListingsAsync(long productId, string query, int? preferredConditionCategoryId, CancellationToken ct)
    {
        // Amazon API does not support new listings search in this example
        return Array.Empty<CartSmart.Core.Worker.NewListing>();
    }
}