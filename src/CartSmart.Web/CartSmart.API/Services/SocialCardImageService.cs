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

public sealed record SocialCardDeal(
    decimal Price,
    decimal? OriginalPrice,
    int? DealTypeId,
    string? DealTypeName,
    string? CouponCode,
    string? StoreName,
    string? StoreImageUrl,
    string? ConditionName,
    bool FreeShipping,
    int? ItemCount,
    string? VariantDetails);

public sealed record SocialCardData(
    string ProductName,
    string? ProductImageUrl,
    IReadOnlyList<SocialCardDeal> Deals,
    string? PriceHistoryNote = null,
    bool IsAllTimeLow = false);

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
        var imageHtml = BuildImageHtml(d.ProductImageUrl);
        var productName = HtmlEncode(d.ProductName);
        var priceHistoryHtml = BuildPriceHistoryHtml(d.PriceHistoryNote, d.IsAllTimeLow);
        var dealRowsHtml = BuildDealRowsHtml(d.Deals);

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
    flex: 0 0 290px;
    position: relative;
    overflow: hidden;
    background: #f8fafc;
    border-bottom: 1px solid #e5e7eb;
    padding: 24px;
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
    padding: 18px 28px 14px;
    gap: 10px;
    min-height: 0;
  }

  .product-name {
    font-size: 28px;
    font-weight: 800;
    color: #0f172a;
    line-height: 1.18;
    display: -webkit-box;
    -webkit-line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
  }

  .price-history-badge {
    align-self: flex-start;
    display: inline-flex;
    align-items: center;
    gap: 6px;
    padding: 5px 12px;
    border-radius: 999px;
    font-size: 14px;
    font-weight: 800;
    letter-spacing: 0.04em;
    text-transform: uppercase;
    background: #fef3c7;
    color: #92400e;
    border: 1px solid #fde68a;
  }

  .price-history-badge.all-time-low {
    background: #fee2e2;
    color: #991b1b;
    border-color: #fecaca;
  }

  .deals-stack {
    display: flex;
    flex-direction: column;
    gap: 10px;
    flex: 1;
    min-height: 0;
  }

  .deal-row {
    display: flex;
    align-items: stretch;
    gap: 16px;
    padding: 12px 16px;
    border: 1px solid #e5e7eb;
    border-radius: 16px;
    background: #ffffff;
    box-shadow: 0 6px 18px rgba(15,23,42,0.06);
  }

  .deal-store {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 6px;
    flex: 0 0 84px;
    width: 84px;
    text-align: center;
  }

  .deal-store-logo {
    width: 56px;
    height: 56px;
    border-radius: 12px;
    border: 1px solid #e5e7eb;
    background: white;
    object-fit: contain;
    padding: 4px;
    flex-shrink: 0;
  }

  .deal-store-logo-fallback {
    display: flex;
    align-items: center;
    justify-content: center;
    background: #ecfdf5;
    color: #15803d;
    font-size: 26px;
    font-weight: 900;
    padding: 0;
  }

  .deal-store-name {
    color: #0f172a;
    font-size: 14px;
    font-weight: 800;
    line-height: 1.15;
    word-break: break-word;
    display: -webkit-box;
    -webkit-line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
    width: 100%;
  }

  .deal-mid {
    flex: 1;
    display: flex;
    flex-direction: column;
    gap: 5px;
    min-width: 0;
    align-self: flex-start;
  }

  .deal-detail {
    display: grid;
    gap: 3px;
  }

  .deal-detail-row {
    font-size: 14px;
    color: #334155;
    line-height: 1.3;
    display: -webkit-box;
    -webkit-line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
  }

  .deal-detail-label {
    color: #64748b;
    font-weight: 700;
    margin-right: 4px;
  }

  .deal-detail-row strong {
    color: #0f172a;
    font-weight: 800;
  }

  .deal-detail-row code {
    font-family: 'Monaco', 'Courier New', monospace;
    font-weight: 700;
    color: #111827;
    background: #f8fafc;
    border: 1px dashed #cbd5e1;
    border-radius: 6px;
    padding: 1px 6px;
    font-size: 13px;
  }

  .deal-pricing {
    display: flex;
    flex-direction: column;
    align-items: flex-end;
    gap: 4px;
    flex: 0 0 auto;
    align-self: flex-start;
  }

  .deal-price {
    font-size: 32px;
    font-weight: 900;
    color: #16a34a;
    letter-spacing: -0.02em;
    line-height: 1;
  }

  .deal-savings {
    font-size: 12px;
    color: #dc2626;
    font-weight: 700;
    line-height: 1;
  }

  .deal-msrp {
    font-size: 12px;
    color: #94a3b8;
    text-decoration: line-through;
    line-height: 1;
  }

  .deal-badge {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    padding: 5px 10px;
    border-radius: 8px;
    border: 1px solid #cbd5e1;
    font-size: 13px;
    font-weight: 700;
    white-space: nowrap;
    background: #ffffff;
    color: #334155;
    align-self: flex-start;
  }

  .deal-badge.coupon {
    border-color: #a7f3d0;
    color: #047857;
    background: #ecfdf5;
  }

  .deal-badge.stacked {
    border-color: #fde68a;
    color: #b45309;
    background: #fffbeb;
  }

  .deal-badge.external {
    border-color: #c7d2fe;
    color: #4338ca;
    background: #eef2ff;
  }

  .discount-badge {
    background: #dcfce7;
    color: #166534;
    padding: 4px 10px;
    border-radius: 999px;
    font-size: 13px;
    font-weight: 700;
    white-space: nowrap;
  }

  .footer {
    display: flex;
    justify-content: center;
    align-items: center;
    text-align: center;
    padding-top: 4px;
    font-size: 15px;
    color: #64748b;
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
    <div class="product-name">{{productName}}</div>
    {{priceHistoryHtml}}

    <div class="deals-stack">
      {{dealRowsHtml}}
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

    private static string BuildDealRowsHtml(IReadOnlyList<SocialCardDeal> deals)
    {
        if (deals is null || deals.Count == 0)
            return string.Empty;

        return string.Join("\n", deals.Select(BuildDealRowHtml));
    }

    private static string BuildDealRowHtml(SocialCardDeal d)
    {
        var dealBadgeHtml = BuildDealBadgeHtml(d.DealTypeId, d.DealTypeName);
        var discountBadgeHtml = BuildDiscountBadgeHtml(d.Price, d.OriginalPrice);
        var price = d.Price.ToString("F2", CultureInfo.InvariantCulture);
        var displayStoreName = string.IsNullOrWhiteSpace(d.StoreName) ? "Store" : d.StoreName.Trim();
        var encodedStoreName = HtmlEncode(displayStoreName);

        var logoHtml = !string.IsNullOrWhiteSpace(d.StoreImageUrl)
            ? $"""<img class="deal-store-logo" src="{HtmlEncode(d.StoreImageUrl)}" alt="{encodedStoreName}" loading="eager">"""
            : $"""<div class="deal-store-logo deal-store-logo-fallback">{HtmlEncode(displayStoreName[..1].ToUpperInvariant())}</div>""";

        var detailRows = new List<string>();

        // Condition (+ optional Qty) — keeps the headline of the row to one tight line.
        var conditionBits = new List<string>();
        if (!string.IsNullOrWhiteSpace(d.ConditionName))
            conditionBits.Add($"<strong>{HtmlEncode(d.ConditionName)}</strong>");
        if (d.ItemCount.HasValue && d.ItemCount.Value > 1)
            conditionBits.Add($"Qty: {d.ItemCount.Value}");
        if (conditionBits.Count > 0)
            detailRows.Add($"""<div class="deal-detail-row"><span class="deal-detail-label">Condition:</span>{string.Join(" • ", conditionBits)}</div>""");

        if (!string.IsNullOrWhiteSpace(d.VariantDetails))
            detailRows.Add($"""<div class="deal-detail-row"><span class="deal-detail-label">Attributes:</span>{HtmlEncode(d.VariantDetails!)}</div>""");

        if (d.FreeShipping)
            detailRows.Add("""<div class="deal-detail-row"><span class="deal-detail-label">Shipping:</span><span style="color:#16a34a;font-weight:700;">Free</span></div>""");

        if (!string.IsNullOrWhiteSpace(d.CouponCode))
            detailRows.Add($"""<div class="deal-detail-row"><span class="deal-detail-label">Coupon:</span><code>{HtmlEncode(d.CouponCode)}</code></div>""");
        else if (d.DealTypeId == 2)
            detailRows.Add("""<div class="deal-detail-row"><span class="deal-detail-label">Coupon:</span>No code required</div>""");

        if (d.DealTypeId == 3)
            detailRows.Add("""<div class="deal-detail-row"><span class="deal-detail-label">How it works:</span>Stack multiple offers for the final price, see details</div>""");
        else if (d.DealTypeId == 4)
            detailRows.Add("""<div class="deal-detail-row"><span class="deal-detail-label">How it works:</span>Activate the offer, then shop the deal, see details</div>""");

        var detailHtml = detailRows.Count > 0
            ? $"""<div class="deal-detail">{string.Join("\n", detailRows)}</div>"""
            : string.Empty;

        string savingsHtml = string.Empty;
        string msrpHtml = string.Empty;
        if (d.OriginalPrice.HasValue && d.OriginalPrice.Value > d.Price)
        {
            var savings = d.OriginalPrice.Value - d.Price;
            savingsHtml = $"""<div class="deal-savings">Save ${savings.ToString("F2", CultureInfo.InvariantCulture)}</div>""";
            msrpHtml = $"""<div class="deal-msrp">MSRP ${d.OriginalPrice.Value.ToString("F2", CultureInfo.InvariantCulture)}</div>""";
        }

        return $$"""
<div class="deal-row">
  <div class="deal-store">
    {{logoHtml}}
    <div class="deal-store-name">{{encodedStoreName}}</div>
  </div>
  <div class="deal-mid">
    {{dealBadgeHtml}}
    {{detailHtml}}
  </div>
  <div class="deal-pricing">
    {{discountBadgeHtml}}
    <div class="deal-price">${{price}}</div>
    {{savingsHtml}}
    {{msrpHtml}}
  </div>
</div>
""";
    }

    private static string BuildPriceHistoryHtml(string? note, bool isAllTimeLow)
    {
        if (string.IsNullOrWhiteSpace(note))
            return string.Empty;
        var cls = isAllTimeLow ? "price-history-badge all-time-low" : "price-history-badge";
        return $"""<div class="{cls}">{HtmlEncode(note)}</div>""";
    }

    private static string BuildDealBadgeHtml(int? dealTypeId, string? dealTypeName)
    {
        var (label, className) = dealTypeId switch
        {
            1 => ("Direct Deal",   "direct"),
            2 => ("Coupon Deal",   "coupon"),
            3 => ("Stacked Deal",  "stacked"),
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
