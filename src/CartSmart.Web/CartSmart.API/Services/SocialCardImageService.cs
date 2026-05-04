using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using System.Globalization;

namespace CartSmart.API.Services;

/// <summary>
/// Generates a social-media-optimised deal card image (PNG, base64-encoded)
/// using a headless Chromium instance.
/// Card dimensions: 900 x 800 (narrower layout to fit Instagram more reliably).
/// </summary>
public interface ISocialCardImageService
{
    /// <summary>
    /// Renders the deal card and returns the raw PNG bytes.
    /// Returns null when Playwright is unavailable or rendering fails.
    /// </summary>
    Task<byte[]?> GenerateAsync(SocialCardData data, CancellationToken ct = default);
}

public sealed record SocialCardData(
    string ProductName,
    string? ProductImageUrl,
    decimal CurrentPrice,
    decimal? OriginalPrice,
    int? DealTypeId,
    string? DealTypeName,
    string? CouponCode,
    string? StoreName,
    string? StoreImageUrl,
    string? ConditionName,
    string? VariantDetails,
    int? ItemCount,
    bool FreeShipping);

public sealed class SocialCardImageService : ISocialCardImageService
{
  private const int CardWidth = 900;
  private const int CardHeight = 800;

    private readonly ILogger<SocialCardImageService> _logger;

    public SocialCardImageService(ILogger<SocialCardImageService> logger)
    {
        _logger = logger;
    }

    public async Task<byte[]?> GenerateAsync(SocialCardData data, CancellationToken ct)
    {
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" }
            });

            var page = await browser.NewPageAsync(new BrowserNewPageOptions
            {
                ViewportSize = new ViewportSize { Width = CardWidth, Height = CardHeight }
            });

            var html = BuildCardHtml(data);
            await page.SetContentAsync(html, new PageSetContentOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout   = 15_000
            });

            // Give CSS animations / image decode a moment to settle
            await page.WaitForTimeoutAsync(400);

            var bytes = await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type      = ScreenshotType.Png,
                FullPage  = false,
                Clip      = new Clip { X = 0, Y = 0, Width = CardWidth, Height = CardHeight }
            });

            return bytes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SocialCardImageService: failed to render card for '{Product}'", data.ProductName);
            return null;
        }
    }

    // ── HTML Card Template ────────────────────────────────────────────────

    private static string BuildCardHtml(SocialCardData d)
    {
        var dealBadgeHtml = BuildDealBadgeHtml(d.DealTypeId, d.DealTypeName);
        var discountBadgeHtml = BuildDiscountBadgeHtml(d.CurrentPrice, d.OriginalPrice);
        var storeHtml = BuildStoreHtml(d.StoreName, d.StoreImageUrl);
        var detailsHtml = BuildDetailsHtml(d);
        var savingsHtml = BuildSavingsHtml(d.CurrentPrice, d.OriginalPrice);
        var imageHtml = BuildImageHtml(d.ProductImageUrl);
        var productName = HtmlEncode(d.ProductName);
        var currentPrice = d.CurrentPrice.ToString("F2", CultureInfo.InvariantCulture);
        var couponCodeHtml = !string.IsNullOrWhiteSpace(d.CouponCode)
            ? $"<div class=\"detail-row\"><span class=\"detail-label\">Coupon Code:</span><code>{HtmlEncode(d.CouponCode)}</code></div>"
            : string.Empty;

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width={{CardWidth}},height={{CardHeight}}">
<style>
  * { margin: 0; padding: 0; box-sizing: border-box; }
  
  body {
    width: {{CardWidth}}px;
    height: {{CardHeight}}px;
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
    background: linear-gradient(135deg, #eef7ef 0%, #f8fafc 48%, #eef2ff 100%);
    overflow: hidden;
    color: #111827;
  }

  .card {
    width: calc(100% - 72px);
    height: calc(100% - 96px);
    margin: 48px 36px;
    display: flex;
    flex-direction: column;
    background: white;
    border: 1px solid #e5e7eb;
    border-radius: 28px;
    box-shadow: 0 24px 70px rgba(15,23,42,0.16);
    overflow: hidden;
  }

  .image-section {
    flex: 0 0 320px;
    position: relative;
    overflow: hidden;
    background: #f8fafc;
    border-bottom: 1px solid #e5e7eb;
    padding: 30px;
    display: flex;
    align-items: center;
    justify-content: center;
  }

  .image-section img {
    width: 100%;
    height: 100%;
    object-fit: contain;
    object-position: center;
    display: block;
  }

  .image-placeholder {
    width: 100%;
    height: 100%;
    display: flex;
    align-items: center;
    justify-content: center;
    background: linear-gradient(135deg, #e5e7eb 0%, #d1d5db 100%);
  }

  .image-placeholder svg {
    width: 120px;
    height: 120px;
    opacity: 0.4;
  }

  .content-section {
    flex: 1;
    display: flex;
    flex-direction: column;
    padding: 18px 32px 16px;
    justify-content: flex-start;
    gap: 10px;
  }

  .store-header {
    display: flex;
    align-items: center;
    gap: 14px;
    padding-bottom: 10px;
    border-bottom: 1px solid #edf2f7;
  }

  .store-logo {
    width: 48px;
    height: 48px;
    border-radius: 10px;
    border: 1px solid #e5e7eb;
    background: white;
    object-fit: contain;
  }

  .store-logo-fallback {
    width: 48px;
    height: 48px;
    border-radius: 10px;
    background: #ecfdf5;
    color: #15803d;
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 24px;
    font-weight: 900;
  }

  .store-kicker {
    color: #64748b;
    font-size: 15px;
    font-weight: 600;
  }

  .store-name {
    color: #0f172a;
    font-size: 25px;
    font-weight: 800;
    line-height: 1.1;
  }

  .deal-card {
    border: 1px solid #e5e7eb;
    border-radius: 18px;
    padding: 16px 18px;
    box-shadow: 0 10px 30px rgba(15,23,42,0.08);
  }

  .deal-top-row {
    display: flex;
    justify-content: space-between;
    align-items: flex-start;
    gap: 18px;
    margin-bottom: 8px;
  }

  .deal-badge {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    padding: 8px 12px;
    border-radius: 8px;
    border: 1px solid #cbd5e1;
    font-size: 17px;
    font-weight: 600;
    white-space: nowrap;
    background: #ffffff;
    color: #334155;
    box-shadow: 0 1px 5px rgba(15,23,42,0.06);
  }

  .deal-badge.coupon {
    border-color: #a7f3d0;
    color: #047857;
  }

  .deal-badge.stacked {
    border-color: #fde68a;
    color: #b45309;
  }

  .deal-badge.external {
    border-color: #c7d2fe;
    color: #4338ca;
  }

  .price-discount-row {
    display: flex;
    align-items: flex-end;
    gap: 10px;
    text-align: right;
  }

  .price {
    font-size: 44px;
    font-weight: 900;
    color: #16a34a;
    letter-spacing: -0.02em;
    line-height: 1;
  }

  .discount-badge {
    background: #dcfce7;
    color: #166534;
    padding: 7px 12px;
    border-radius: 999px;
    font-size: 16px;
    font-weight: 700;
    white-space: nowrap;
  }

  .product-name {
    font-size: 31px;
    font-weight: 800;
    color: #0f172a;
    line-height: 1.18;
    margin-bottom: 10px;
    display: -webkit-box;
    -webkit-line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
  }

  .details {
    display: grid;
    gap: 8px;
  }

  .detail-row {
    font-size: 17px;
    color: #334155;
    line-height: 1.28;
  }

  .detail-row.attributes {
    display: -webkit-box;
    -webkit-line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
  }

  .detail-label {
    color: #64748b;
    font-weight: 700;
  }

  code {
    font-family: 'Monaco', 'Courier New', monospace;
    font-weight: 700;
    color: #111827;
    background: #f8fafc;
    border: 1px dashed #cbd5e1;
    border-radius: 7px;
    padding: 4px 9px;
  }

  .footer {
    display: flex;
    justify-content: center;
    align-items: center;
    text-align: center;
    padding-top: 0;
    margin-top: 4px;
    font-size: 18px;
    color: #64748b;
  }

  .branding {
    color: #16a34a;
    font-weight: 900;
    letter-spacing: -0.02em;
  }

  .site {
    color: #16a34a;
    font-weight: 800;
  }
</style>
</head>
<body>
<div class="card">

  <!-- Product Image -->
  <div class="image-section">
    {{imageHtml}}
  </div>

  <!-- Content -->
  <div class="content-section">
    {{storeHtml}}

    <div class="deal-card">
      <div class="deal-top-row">
        <div>{{dealBadgeHtml}}</div>
        <div class="price-discount-row">
          {{discountBadgeHtml}}
          <div class="price">${{currentPrice}}</div>
        </div>
      </div>

      <div class="product-name">{{productName}}</div>

      <div class="details">
        {{detailsHtml}}
        {{savingsHtml}}
        {{couponCodeHtml}}
      </div>
    </div>

    <div class="footer">
      <span>Found a lower price? Prove it - we reward the best deals - <span class="site">CartSmart.com</span></span>
    </div>
  </div>

</div>
</body>
</html>
""";
    }

    private static string BuildImageHtml(string? imageUrl)
    {
        if (!string.IsNullOrWhiteSpace(imageUrl))
            return $"""<img src="{HtmlEncode(imageUrl)}" alt="product" loading="eager">""";

        // Placeholder shopping bag SVG when no image
        return """
<div class="image-placeholder">
  <svg viewBox="0 0 64 64" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M20 16h24v4H20z" stroke="#9ca3af" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>
    <path d="M22 20v20c0 2 1 3 3 3h14c2 0 3-1 3-3V20" stroke="#9ca3af" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>
    <path d="M28 28v8M36 28v8" stroke="#9ca3af" stroke-width="2" stroke-linecap="round"/>
  </svg>
</div>
""";
    }

    private static string BuildStoreHtml(string? storeName, string? storeImageUrl)
    {
        var safeStoreName = string.IsNullOrWhiteSpace(storeName) ? "Featured Store" : HtmlEncode(storeName.Trim());
        var logoHtml = !string.IsNullOrWhiteSpace(storeImageUrl)
            ? $"""<img class="store-logo" src="{HtmlEncode(storeImageUrl)}" alt="{safeStoreName}" loading="eager">"""
            : $"""<div class="store-logo-fallback">{HtmlEncode(safeStoreName[..1].ToUpperInvariant())}</div>""";

        return $$"""
<div class="store-header">
  {{logoHtml}}
  <div>
  <div class="store-kicker">Store</div>
  <div class="store-name">{{safeStoreName}}</div>
  </div>
</div>
""";
    }

    private static string BuildDetailsHtml(SocialCardData d)
    {
        var rows = new List<string>();

        var summaryParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(d.ConditionName))
            summaryParts.Add(HtmlEncode(d.ConditionName));
        if (d.ItemCount.HasValue && d.ItemCount.Value > 1)
            summaryParts.Add($"Qty: {d.ItemCount.Value}");

        if (summaryParts.Count > 0)
            rows.Add($"""<div class="detail-row"><span class="detail-label">Details:</span> {string.Join(" • ", summaryParts)}</div>""");

        if (!string.IsNullOrWhiteSpace(d.VariantDetails))
            rows.Add($"""<div class="detail-row attributes"><span class="detail-label">Attributes:</span> {HtmlEncode(d.VariantDetails)}</div>""");

        if (d.FreeShipping)
            rows.Add("""<div class="detail-row"><span class="detail-label">Shipping:</span> <span style="color:#16a34a;font-weight:700;">Free</span></div>""");

        if (d.DealTypeId == 2 && string.IsNullOrWhiteSpace(d.CouponCode))
            rows.Add("""<div class="detail-row"><span class="detail-label">Coupon:</span> No code required</div>""");

        if (d.DealTypeId == 3)
          rows.Add("""<div class="detail-row"><span class="detail-label">How it works:</span> Stack multiple offers for the final price, see details</div>""");

        if (d.DealTypeId == 4)
          rows.Add("""<div class="detail-row"><span class="detail-label">How it works:</span> Activate the offer, then shop the deal, see details</div>""");

        return rows.Count == 0
            ? """<div class="detail-row"><span class="detail-label">Deal:</span> Verified shopping offer</div>"""
            : string.Join("\n", rows);
    }

  private static string BuildSavingsHtml(decimal currentPrice, decimal? originalPrice)
  {
    if (!originalPrice.HasValue || originalPrice.Value <= currentPrice)
      return string.Empty;

    var savings = originalPrice.Value - currentPrice;
    return $"""<div class="detail-row"><span class="detail-label">You save:</span> <span style="color:#dc2626;font-weight:700;">${savings:F2}</span> <span style="color:#64748b;text-decoration:line-through;">MSRP ${originalPrice.Value:F2}</span></div>""";
  }

    private static string BuildDealBadgeHtml(int? dealTypeId, string? dealTypeName)
    {
        var (label, className) = (dealTypeId) switch
        {
      1 => ("Direct Deal", "direct"),
            2 => ("Coupon Deal", "coupon"),
            3 => ("Stacked Deal", "stacked"),
            4 => ("External Deal", "external"),
      _ => (string.IsNullOrWhiteSpace(dealTypeName) ? "Deal" : $"{dealTypeName} Deal", "direct")
        };

        return $"""<span class="deal-badge {className}">{HtmlEncode(label)}</span>""";
    }

    private static string BuildDiscountBadgeHtml(decimal currentPrice, decimal? originalPrice)
    {
        if (!originalPrice.HasValue || originalPrice.Value <= currentPrice)
            return string.Empty;

        var percent = (int)Math.Round((1m - currentPrice / originalPrice.Value) * 100);
        return $"""<span class="discount-badge">{percent}% Off</span>""";
    }

    private static string HtmlEncode(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value
            .Replace("&",  "&amp;")
            .Replace("<",  "&lt;")
            .Replace(">",  "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'",  "&#39;");
    }
}
