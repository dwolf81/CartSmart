using CartSmart.API.Models;
using CartSmart.API.Models.DTOs;
using CartSmart.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json.Linq;

namespace CartSmart.API.Controllers
{
    /// <summary>
    /// Endpoints consumed by the CartSmart Chrome extension.
    /// </summary>
    [ApiController]
    [Route("api/extension")]
    public class ExtensionController : ControllerBase
    {
        private readonly ISupabaseService _supabase;
        private readonly IMemoryCache _cache;
        private readonly IAuthService _authService;
        private static readonly TimeSpan StoreCacheDuration = TimeSpan.FromMinutes(15);
        private const string StoreCacheKey = "extension_scrape_stores";
        private static readonly TimeSpan PriceReportThrottle = TimeSpan.FromMinutes(15);
        private const string PriceReportThrottlePrefix = "ext_price_throttle:";

        public ExtensionController(ISupabaseService supabase, IMemoryCache cache, IAuthService authService)
        {
            _supabase = supabase;
            _cache = cache;
            _authService = authService;
        }

        /// <summary>
        /// Returns all stores that have scrape_mode_id = 1 (All) or 2 (BrowserOnly)
        /// along with their scrapeConfig (contains the price_selectors the extension needs to run).
        /// </summary>
        [HttpGet("stores")]
        [AllowAnonymous]
        [ResponseCache(Duration = 900, Location = ResponseCacheLocation.Any, NoStore = false)]
        public async Task<ActionResult<IEnumerable<ExtensionStoreConfigDTO>>> GetScrapeEnabledStores()
        {
            if (_cache.TryGetValue(StoreCacheKey, out List<ExtensionStoreConfigDTO>? cached) && cached != null)
            {
                return Ok(cached);
            }

            var client = _supabase.GetServiceRoleClient();
            var resp = await client
                .From<Store>()
                .Select("id, name, url, slug, scrape_config, scrape_mode_id")
                .Filter("approved", Supabase.Postgrest.Constants.Operator.Equals, "true")
                .Filter("scrape_mode_id", Supabase.Postgrest.Constants.Operator.GreaterThan, "0")
                .Order("name", Supabase.Postgrest.Constants.Ordering.Ascending)
                .Get();

            var stores = (resp.Models ?? new List<Store>())
                .Select(s => new ExtensionStoreConfigDTO
                {
                    id = s.Id,
                    name = s.Name,
                    url = s.URL,
                    slug = s.Slug,
                    scrapeConfig = TryParseScrapeConfig(s.ScrapeConfig),
                })
                .ToList();

            _cache.Set(StoreCacheKey, stores, StoreCacheDuration);

            return Ok(stores);
        }

        /// <summary>
        /// Accepts a price report from the Chrome extension.
        /// Requires a valid CartSmart JWT (users log in via the extension with their
        /// existing CartSmart account). This prevents unauthenticated price submissions.
        /// Matches the submitted URL to existing deal_product rows for the given store
        /// and updates prices / records price history where the price has changed.
        /// </summary>
        [HttpPost("price-report")]
        [Authorize]
        public async Task<ActionResult<ExtensionPriceReportResponseDTO>> SubmitPriceReport(
            [FromBody] ExtensionPriceReportDTO report)
        {
            if (report == null || string.IsNullOrWhiteSpace(report.url) || report.price == null || report.price <= 0)
            {
                return BadRequest(new ExtensionPriceReportResponseDTO
                {
                    accepted = false,
                    message = "Invalid report: url and a positive price are required."
                });
            }

            var client = _supabase.GetServiceRoleClient();

            // Normalise the submitted URL for comparison & throttle key
            var normUrl = NormaliseUrl(report.url);
            var throttleKey = PriceReportThrottlePrefix + normUrl;

            // ── 0. Throttle: skip if this URL was reported within the last 15 min ──
            if (_cache.TryGetValue(throttleKey, out DateTime lastReported))
            {
                var remaining = PriceReportThrottle - (DateTime.UtcNow - lastReported);
                return Ok(new ExtensionPriceReportResponseDTO
                {
                    accepted = false,
                    throttled = true,
                    message = $"This URL was already updated {(int)remaining.TotalMinutes + 1} minute(s) ago. Try again later."
                });
            }

            // ── 1. Verify the store has scraping enabled ────────────────────────
            var storeResp = await client
                .From<Store>()
                .Where(s => s.Id == report.storeId)
                .Limit(1)
                .Get();

            var store = storeResp.Models.FirstOrDefault();
            if (store == null || !ScrapeMode.AllowsBrowserScrape(store.ScrapeModeId))
            {
                return Ok(new ExtensionPriceReportResponseDTO
                {
                    accepted = false,
                    message = "Store not found or scraping is not enabled."
                });
            }

            // ── 2. Find deal_product rows whose URL matches ──────────────────

            // Fetch active (non-deleted) deal products for this store via deal
            var dealResp = await client
                .From<Deal>()
                .Select("id")
                .Filter("store_id", Supabase.Postgrest.Constants.Operator.Equals, store.Id.ToString())
                .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
                .Get();

            var dealIds = (dealResp.Models ?? new List<Deal>()).Select(d => d.Id).ToList();

            if (dealIds.Count == 0)
            {
                return Ok(new ExtensionPriceReportResponseDTO
                {
                    accepted = true,
                    matchedDealProducts = 0,
                    updatedDealProducts = 0,
                    message = "No active deals found for this store."
                });
            }

            // Fetch deal products for this store's deals
            var allDealProducts = new List<DealProduct>();
            foreach (var dealId in dealIds)
            {
                var dpResp = await client
                    .From<DealProduct>()
                    .Filter("deal_id", Supabase.Postgrest.Constants.Operator.Equals, dealId.ToString())
                    .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
                    .Get();
                allDealProducts.AddRange(dpResp.Models ?? new List<DealProduct>());
            }

            Console.WriteLine($"[Extension] SubmitPriceReport: storeId={report.storeId}, normUrl={normUrl}, dealIds={dealIds.Count}, dealProducts={allDealProducts.Count}");
            foreach (var dp in allDealProducts)
            {
                Console.WriteLine($"[Extension]   dp.Id={dp.Id}, dp.Url={dp.Url}, norm={NormaliseUrl(dp.Url)}, match={UrlsMatch(dp.Url, normUrl)}");
            }

            // Match by URL
            var matched = allDealProducts
                .Where(dp => UrlsMatch(dp.Url, normUrl))
                .ToList();

            Console.WriteLine($"[Extension] Matched {matched.Count} deal product(s) for URL {normUrl}");

            if (matched.Count == 0)
            {
                return Ok(new ExtensionPriceReportResponseDTO
                {
                    accepted = true,
                    matchedDealProducts = 0,
                    updatedDealProducts = 0,
                    message = "Price received but no matching deal products found for this URL."
                });
            }

            // ── 3. Pre-fetch products for MSRP validation ───────────────────
            var productIds = matched.Select(dp => dp.ProductId).Distinct().ToList();
            var productsMap = new Dictionary<int, Product>();
            foreach (var pid in productIds)
            {
                var prodResp = await client
                    .From<Product>()
                    .Select("id, msrp, count_enabled, default_count")
                    .Where(p => p.Id == pid)
                    .Limit(1)
                    .Get();
                var prod = prodResp.Models.FirstOrDefault();
                if (prod != null) productsMap[prod.Id] = prod;
            }

            // ── 4. Update prices where changed ──────────────────────────────
            //
            // The extension scrapes the listing price from the HTML page. For non-direct
            // deals (coupon, external, stacked), the stored deal_product.price should
            // reflect the FINAL price after applying the deal's discount(s).
            //   - Direct (type 1): stored price = scraped listing price
            //   - Coupon/External (type 2/4): stored price = listing × (1 - discount_percent/100)
            //   - Stacked (type 3): stored price = listing with each combo discount applied
            int updated = 0;
            int msrpSkipped = 0;
            var now = DateTime.UtcNow;

            // Pre-fetch parent deals for all matched deal products
            var matchedDealIds = matched.Select(dp => dp.DealId).Distinct().ToList();
            var dealsMap = new Dictionary<int, Deal>();
            foreach (var did in matchedDealIds)
            {
                var dResp = await client.From<Deal>()
                    .Where(d => d.Id == did)
                    .Limit(1)
                    .Get();
                var d = dResp.Models.FirstOrDefault();
                if (d != null) dealsMap[d.Id] = d;
            }

            // Pre-fetch combo definitions for stacked deals
            var stackedDealIds = dealsMap.Values
                .Where(d => d.DealTypeId == 3)
                .Select(d => d.Id)
                .ToList();
            var combosByDeal = new Dictionary<int, List<DealCombo>>();
            var componentDealsMap = new Dictionary<int, Deal>();

            if (stackedDealIds.Count > 0)
            {
                foreach (var sdid in stackedDealIds)
                {
                    var comboResp = await client.From<DealCombo>()
                        .Filter("deal_id", Supabase.Postgrest.Constants.Operator.Equals, sdid.ToString())
                        .Get();
                    combosByDeal[sdid] = comboResp.Models ?? new List<DealCombo>();
                }

                var componentIds = combosByDeal.Values
                    .SelectMany(c => c)
                    .Select(c => c.ComboDealId)
                    .Distinct()
                    .Where(id => !dealsMap.ContainsKey(id))
                    .ToList();

                foreach (var cid in componentIds)
                {
                    var cResp = await client.From<Deal>()
                        .Where(d => d.Id == cid)
                        .Limit(1)
                        .Get();
                    var cd = cResp.Models.FirstOrDefault();
                    if (cd != null) componentDealsMap[cd.Id] = cd;
                }
            }

            foreach (var dp in matched)
            {
                var scrapedPrice = report.price!.Value;

                // Don't accept scraped prices above MSRP (may be quantity-discount pricing)
                if (productsMap.TryGetValue(dp.ProductId, out var msrpProduct) && msrpProduct.MSRP is > 0)
                {
                    double effectiveMsrp = (double)msrpProduct.MSRP.Value;
                    double effectivePrice = (double)scrapedPrice;

                    if (msrpProduct.CountEnabled && msrpProduct.DefaultCount > 0 && dp.ItemCount > 0)
                    {
                        effectiveMsrp /= msrpProduct.DefaultCount;
                        effectivePrice /= dp.ItemCount;
                    }

                    if (effectivePrice > effectiveMsrp)
                    {
                        Console.WriteLine($"[Extension] Skipping dp.Id={dp.Id}: price ${scrapedPrice} exceeds MSRP ${msrpProduct.MSRP.Value}");
                        dp.LastCheckedAt = now;
                        dp.ErrorCount = 0;
                        dp.NextCheckAt = now.AddHours(24);
                        await client.From<DealProduct>().Update(dp);
                        msrpSkipped++;
                        continue;
                    }
                }

                // Compute the final price after applying deal discounts
                dealsMap.TryGetValue(dp.DealId, out var parentDeal);
                var finalPrice = scrapedPrice;

                if (parentDeal != null)
                {
                    var dealType = parentDeal.DealTypeId ?? 1;

                    if (dealType is 2 or 4) // Coupon or External
                    {
                        finalPrice = ApplyPercentOff(scrapedPrice, parentDeal.DiscountPercent);
                        Console.WriteLine($"[Extension] dp.Id={dp.Id}: applying {parentDeal.DiscountPercent}% off to ${scrapedPrice} → ${finalPrice} (deal_type={dealType})");
                    }
                    else if (dealType == 3) // Stacked
                    {
                        finalPrice = ComputeStackedPrice(scrapedPrice, parentDeal.Id, combosByDeal, componentDealsMap);
                        Console.WriteLine($"[Extension] dp.Id={dp.Id}: stacked price from ${scrapedPrice} → ${finalPrice}");
                    }
                }

                // Only update if the price actually changed
                if (dp.Price == finalPrice)
                {
                    // Still update last_checked_at
                    dp.LastCheckedAt = now;
                    dp.ErrorCount = 0;
                    dp.NextCheckAt = now.AddHours(24);
                    await client.From<DealProduct>().Update(dp);
                    continue;
                }

                // Record price history
                var history = new DealProductPriceHistory
                {
                    DealProductId = dp.Id,
                    Price = finalPrice,
                    Currency = report.currency ?? "USD",
                    ChangedAt = now
                };
                await client.From<DealProductPriceHistory>().Insert(history);

                // Update the deal product price
                dp.Price = finalPrice;
                dp.LastCheckedAt = now;
                dp.ErrorCount = 0;
                dp.NextCheckAt = now.AddHours(24);
                await client.From<DealProduct>().Update(dp);

                // Recalculate deal discount_percent for primary direct deal products
                if (dp.Primary && parentDeal != null && parentDeal.DealTypeId == 1)
                {
                    productsMap.TryGetValue(dp.ProductId, out var product);
                    if (product?.MSRP is > 0)
                    {
                        double effectiveMsrp = (double)product.MSRP.Value;
                        double effectivePrice = (double)scrapedPrice;

                        if (product.CountEnabled && product.DefaultCount > 0 && dp.ItemCount > 0)
                        {
                            effectiveMsrp /= product.DefaultCount;
                            effectivePrice /= dp.ItemCount;
                        }

                        var newDiscount = effectiveMsrp > 0
                            ? (int)Math.Round((1.0 - effectivePrice / effectiveMsrp) * 100.0)
                            : 0;
                        if (newDiscount < 0) newDiscount = 0;
                        if (newDiscount > 100) newDiscount = 100;
                        if (parentDeal.DiscountPercent != newDiscount)
                        {
                            parentDeal.DiscountPercent = newDiscount;
                            var discountRow = new DealDiscountUpdateRow
                            {
                                Id = parentDeal.Id,
                                DiscountPercent = newDiscount
                            };
                            await client.From<DealDiscountUpdateRow>()
                                .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, parentDeal.Id.ToString())
                                .Update(discountRow);
                        }
                    }
                }

                updated++;
            }

            // Auto-close any pending manual price tasks for matched deal_products
            // (even if price unchanged, the extension verified the listing is live)
            foreach (var dp in matched)
            {
                try
                {
                    var pendingTasksResp = await client
                        .From<ManualPriceTask>()
                        .Filter("deal_product_id", Supabase.Postgrest.Constants.Operator.Equals, dp.Id.ToString())
                        .Filter("status", Supabase.Postgrest.Constants.Operator.Equals, "pending")
                        .Get();
                    var pendingTasks = pendingTasksResp.Models ?? new List<ManualPriceTask>();
                    foreach (var pt in pendingTasks)
                    {
                        var taskUpdate = new ManualPriceTaskUpdateRow
                        {
                            Id = pt.Id,
                            Status = "completed",
                            SubmittedAt = now,
                            SubmittedPrice = report.price.Value,
                            SubmittedInStock = true,
                            Notes = "Auto-completed: price confirmed via browser extension"
                        };
                        await client
                            .From<ManualPriceTaskUpdateRow>()
                            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, pt.Id.ToString())
                            .Update(taskUpdate);
                    }
                }
                catch { /* best-effort */ }
            }

            // Mark this URL as recently updated so other users don't re-submit
            _cache.Set(throttleKey, DateTime.UtcNow, PriceReportThrottle);

            // ── Log to scrape_log (extension method) ──
            try
            {
                var dpId = matched.FirstOrDefault()?.Id;
                var log = new ScrapeLogInsert
                {
                    StoreId = report.storeId,
                    DealProductId = dpId,
                    Url = report.url,
                    Method = "extension",
                    Success = true,
                    Price = report.price,
                    Currency = report.currency
                };
                await client.From<ScrapeLogInsert>().Insert(log);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScrapeLog] Insert failed: {ex.Message}");
                /* best-effort logging */
            }

            return Ok(new ExtensionPriceReportResponseDTO
            {
                accepted = true,
                matchedDealProducts = matched.Count,
                updatedDealProducts = updated,
                message = updated > 0
                    ? $"Updated {updated} deal product(s) with new price ${report.price:F2}."
                        + (msrpSkipped > 0 ? $" Skipped {msrpSkipped} where price exceeds MSRP." : "")
                    : msrpSkipped > 0
                        ? $"Skipped {msrpSkipped} deal product(s) where price ${report.price:F2} exceeds MSRP."
                        : "Price unchanged; timestamps updated."
            });
        }

        /// <summary>
        /// POST /api/extension/scrape-failure
        /// Logs when the extension visited a tracked product page but failed to extract a price.
        /// Only logs if the URL matches a tracked deal_product — ignores non-product pages.
        /// </summary>
        [HttpPost("scrape-failure")]
        [Authorize]
        public async Task<IActionResult> ReportScrapeFailure(
            [FromBody] ExtensionScrapeFailureDTO report)
        {
            if (report == null || string.IsNullOrWhiteSpace(report.url))
            {
                return BadRequest(new { message = "url is required." });
            }

            var client = _supabase.GetServiceRoleClient();
            var normUrl = NormaliseUrl(report.url);

            // ── Check if this URL matches any tracked deal_product ──
            var dealResp = await client
                .From<Deal>()
                .Select("id")
                .Filter("store_id", Supabase.Postgrest.Constants.Operator.Equals, report.storeId.ToString())
                .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
                .Get();

            var dealIds = (dealResp.Models ?? new List<Deal>()).Select(d => d.Id).ToList();
            if (dealIds.Count == 0)
            {
                return Ok(new { accepted = false, message = "No tracked deals for this store." });
            }

            var allDealProducts = new List<DealProduct>();
            foreach (var dealId in dealIds)
            {
                var dpResp = await client
                    .From<DealProduct>()
                    .Filter("deal_id", Supabase.Postgrest.Constants.Operator.Equals, dealId.ToString())
                    .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
                    .Get();
                allDealProducts.AddRange(dpResp.Models ?? new List<DealProduct>());
            }

            var matched = allDealProducts.Where(dp => UrlsMatch(dp.Url, normUrl)).ToList();
            if (matched.Count == 0)
            {
                // Not a tracked product page — don't log
                return Ok(new { accepted = false, message = "URL does not match any tracked deal product." });
            }

            // Throttle: don't log the same URL more than once per 15 minutes
            var throttleKey = "scrape_fail:" + normUrl;
            if (_cache.TryGetValue(throttleKey, out _))
            {
                return Ok(new { accepted = false, throttled = true, message = "Already logged recently." });
            }

            try
            {
                var dpId = matched.FirstOrDefault()?.Id;
                var log = new ScrapeLogInsert
                {
                    StoreId = report.storeId,
                    DealProductId = dpId,
                    Url = report.url,
                    Method = "extension",
                    Success = false,
                    Price = null,
                    Currency = null,
                    ErrorMessage = report.errorMessage?.Length > 500
                        ? report.errorMessage[..500]
                        : report.errorMessage
                };
                await client.From<ScrapeLogInsert>().Insert(log);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScrapeLog] Failure insert failed: {ex.Message}");
            }

            _cache.Set(throttleKey, true, TimeSpan.FromMinutes(15));

            return Ok(new { accepted = true, message = "Failure logged." });
        }

        // ─── Helpers ──────────────────────────────────────────────────────

        private static JToken? TryParseScrapeConfig(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                return JToken.Parse(raw);
            }
            catch
            {
                return null;
            }
        }

        // ── Discount helpers ────────────────────────────────────────────────

        /// <summary>Apply a percent-off discount to a base price.</summary>
        private static decimal ApplyPercentOff(decimal basePrice, int? percentOff)
        {
            if (!percentOff.HasValue || percentOff.Value <= 0) return basePrice;
            if (percentOff.Value >= 100) return 0m;
            return Math.Round(basePrice * (1m - percentOff.Value / 100m), 2, MidpointRounding.AwayFromZero);
        }

        /// <summary>Compute the final price for a stacked deal by applying each combo component's discount in order.</summary>
        private static decimal ComputeStackedPrice(
            decimal listingPrice,
            int stackedDealId,
            Dictionary<int, List<DealCombo>> combosByDeal,
            Dictionary<int, Deal> componentDealsMap)
        {
            if (!combosByDeal.TryGetValue(stackedDealId, out var combos) || combos.Count == 0)
                return listingPrice;

            var price = listingPrice;
            var ordered = combos
                .OrderBy(c => c.Order ?? int.MaxValue)
                .ThenBy(c => c.ComboDealId)
                .ToList();

            foreach (var combo in ordered)
            {
                if (!componentDealsMap.TryGetValue(combo.ComboDealId, out var comp))
                    continue;

                // Apply percent-off for coupon/external components; skip direct components (they define the base)
                if (comp.DealTypeId is 2 or 4)
                    price = ApplyPercentOff(price, comp.DiscountPercent);
            }

            return price;
        }

        /// <summary>
        /// Normalise a URL for comparison: lowercase host, strip trailing slash,
        /// remove tracking params (utm_*, fbclid, gclid, ref, tag).
        /// </summary>
        private static string NormaliseUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;
            try
            {
                var uri = new Uri(url);
                var host = uri.Host.ToLowerInvariant();
                if (host.StartsWith("www."))
                    host = host[4..];

                var builder = new UriBuilder(uri)
                {
                    Scheme = uri.Scheme.ToLowerInvariant(),
                    Host = host,
                    Fragment = string.Empty
                };

                // Filter out known tracking query params
                if (!string.IsNullOrWhiteSpace(uri.Query))
                {
                    var qs = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    var keysToRemove = qs.AllKeys
                        .Where(k => k != null && (
                            k.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) ||
                            k.Equals("fbclid", StringComparison.OrdinalIgnoreCase) ||
                            k.Equals("gclid", StringComparison.OrdinalIgnoreCase) ||
                            k.Equals("ref", StringComparison.OrdinalIgnoreCase) ||
                            k.Equals("tag", StringComparison.OrdinalIgnoreCase)
                        )).ToList();

                    foreach (var key in keysToRemove)
                    {
                        qs.Remove(key);
                    }
                    builder.Query = qs.ToString();
                }

                var result = builder.Uri.ToString().TrimEnd('/');
                return result;
            }
            catch
            {
                return url.ToLowerInvariant().TrimEnd('/');
            }
        }

        /// <summary>
        /// Compare two URLs after normalisation.
        /// First tries exact normalised match, then falls back to path-only match
        /// (ignoring query strings) to handle URLs with variant parameters.
        /// </summary>
        private static bool UrlsMatch(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            var normA = NormaliseUrl(a);
            var normB = NormaliseUrl(b);
            if (string.Equals(normA, normB, StringComparison.OrdinalIgnoreCase)) return true;

            // Fallback: compare scheme + host + path only (strip query strings)
            try
            {
                var uriA = new Uri(normA);
                var uriB = new Uri(normB);
                return string.Equals(uriA.Host, uriB.Host, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(uriA.AbsolutePath.TrimEnd('/'), uriB.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
