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

            // Fetch deal products for those deals
            var dpResp = await client
                .From<DealProduct>()
                .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
                .Get();

            var allDealProducts = dpResp.Models ?? new List<DealProduct>();

            // Match by deal_id being in our store's deals AND URL matching
            var matched = allDealProducts
                .Where(dp => dealIds.Contains(dp.DealId) && UrlsMatch(dp.Url, normUrl))
                .ToList();

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

            // ── 3. Update prices where changed ──────────────────────────────
            int updated = 0;
            var now = DateTime.UtcNow;

            foreach (var dp in matched)
            {
                // Only update if the price actually changed
                if (dp.Price == report.price.Value) 
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
                    Price = report.price.Value,
                    Currency = report.currency ?? "USD",
                    ChangedAt = now
                };
                await client.From<DealProductPriceHistory>().Insert(history);

                // Update the deal product price
                dp.Price = report.price.Value;
                dp.LastCheckedAt = now;
                dp.ErrorCount = 0;
                dp.NextCheckAt = now.AddHours(24);
                await client.From<DealProduct>().Update(dp);

                // Recalculate deal discount_percent for primary direct deal products
                if (dp.Primary)
                {
                    var dealResp2 = await client
                        .From<Deal>()
                        .Where(d => d.Id == dp.DealId)
                        .Limit(1)
                        .Get();
                    var deal = dealResp2.Models.FirstOrDefault();
                    if (deal != null && deal.DealTypeId == 1) // Direct deal
                    {
                        var productResp = await client
                            .From<Product>()
                            .Select("id, msrp, count_enabled, default_count")
                            .Where(p => p.Id == dp.ProductId)
                            .Limit(1)
                            .Get();
                        var product = productResp.Models.FirstOrDefault();
                        if (product?.MSRP is > 0)
                        {
                            double effectiveMsrp = (double)product.MSRP.Value;
                            double effectivePrice = (double)report.price.Value;

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
                            if (deal.DiscountPercent != newDiscount)
                            {
                                deal.DiscountPercent = newDiscount;
                                await client.From<Deal>().Update(deal);
                            }
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
                    : "Price unchanged; timestamps updated."
            });
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
                var builder = new UriBuilder(uri)
                {
                    Scheme = uri.Scheme.ToLowerInvariant(),
                    Host = uri.Host.ToLowerInvariant(),
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
        /// </summary>
        private static bool UrlsMatch(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            return string.Equals(NormaliseUrl(a), NormaliseUrl(b), StringComparison.OrdinalIgnoreCase);
        }
    }
}
