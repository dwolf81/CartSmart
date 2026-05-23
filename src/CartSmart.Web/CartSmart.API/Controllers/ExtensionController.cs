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
        private readonly IUserService _userService;
        private readonly IUrlSanitizer _urlSanitizer;
        private readonly IProductImageService _productImageService;
        private readonly IProductMetadataInferenceService _metadataInference;
        private readonly ILogger<ExtensionController> _logger;
        private static readonly TimeSpan StoreCacheDuration = TimeSpan.FromMinutes(15);
        private const string StoreCacheKey = "extension_scrape_stores";
        private static readonly TimeSpan PriceReportThrottle = TimeSpan.FromMinutes(15);
        private const string PriceReportThrottlePrefix = "ext_price_throttle:";

        public ExtensionController(
            ISupabaseService supabase,
            IMemoryCache cache,
            IAuthService authService,
            IUserService userService,
            IUrlSanitizer urlSanitizer,
            IProductImageService productImageService,
            IProductMetadataInferenceService metadataInference,
            ILogger<ExtensionController> logger)
        {
            _supabase = supabase;
            _cache = cache;
            _authService = authService;
            _userService = userService;
            _urlSanitizer = urlSanitizer;
            _productImageService = productImageService;
            _metadataInference = metadataInference;
            _logger = logger;
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

            // Match by URL first (these are the rows directly tied to the scraped listing URL).
            var matchedByUrl = allDealProducts
                .Where(dp => UrlsMatch(dp.Url, normUrl))
                .ToList();

            // Also include derived rows linked to the matched originals, even when their URL differs.
            // Walk the link graph transitively because some stacked rows can be chained through
            // intermediate derived rows depending on historical backfills.
            var matchedIds = matchedByUrl.Select(dp => dp.Id).ToHashSet();
            var linkedDerived = new List<DealProduct>();
            var expanded = true;
            while (expanded)
            {
                expanded = false;
                var nextLinked = allDealProducts
                    .Where(dp => dp.OriginalDealProductId.HasValue && matchedIds.Contains(dp.OriginalDealProductId.Value))
                    .Where(dp => !matchedIds.Contains(dp.Id))
                    .ToList();

                if (nextLinked.Count > 0)
                {
                    linkedDerived.AddRange(nextLinked);
                    foreach (var dp in nextLinked)
                        matchedIds.Add(dp.Id);
                    expanded = true;
                }
            }

            var matched = matchedByUrl
                .Concat(linkedDerived)
                .GroupBy(dp => dp.Id)
                .Select(g => g.First())
                .ToList();

            Console.WriteLine($"[Extension] Matched {matched.Count} deal product(s) for URL {normUrl} ({matchedByUrl.Count} URL match + {linkedDerived.Count} linked)");

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
            int sanitySkipped = 0;
            var now = DateTime.UtcNow;

            // Sanity-check bounds. A wrong CSS selector can match a "1 in cart"
            // badge or a shipping line and post $0.99 against a $400 product;
            // once that lands in price_history it pollutes the all-time-low and
            // is painful to undo. Refuse outright when the scraped price is
            // wildly off the currently-stored price.
            const decimal MinPriceRatio = 0.30m; // scraped < 30% of stored ⇒ suspect
            const decimal MaxPriceRatio = 3.00m; // scraped > 3× stored ⇒ suspect (catches non-MSRP products)
            const decimal AbsoluteMinPrice = 1.00m; // never accept sub-$1 — not a real product price

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

            // Seed component lookup with already-loaded parent deals so stacked combo lookup
            // can resolve components that are part of the current matched set.
            foreach (var kv in dealsMap)
            {
                componentDealsMap[kv.Key] = kv.Value;
            }

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
                    .Where(id => !componentDealsMap.ContainsKey(id))
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

                // ── Sanity check: reject prices that are wildly different from
                //    the stored price or below an absolute floor. Wrong CSS
                //    selectors are the usual culprit; refuse before anything
                //    lands in price_history.
                if (scrapedPrice < AbsoluteMinPrice)
                {
                    Console.WriteLine($"[Extension] Sanity skip dp.Id={dp.Id}: scraped ${scrapedPrice} below absolute floor ${AbsoluteMinPrice}");
                    dp.LastCheckedAt = now;
                    dp.NextCheckAt = now.AddHours(24);
                    await client.From<DealProduct>().Update(dp);
                    sanitySkipped++;
                    continue;
                }

                if (dp.Price > 0)
                {
                    var ratio = scrapedPrice / dp.Price;
                    if (ratio < MinPriceRatio || ratio > MaxPriceRatio)
                    {
                        Console.WriteLine($"[Extension] Sanity skip dp.Id={dp.Id}: scraped ${scrapedPrice} is {ratio:P0} of stored ${dp.Price} (out of [{MinPriceRatio:P0}, {MaxPriceRatio:P0}])");
                        dp.LastCheckedAt = now;
                        dp.NextCheckAt = now.AddHours(24);
                        await client.From<DealProduct>().Update(dp);
                        sanitySkipped++;
                        continue;
                    }
                }

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

                // Compute the final price after applying deal discounts.
                // Base price = direct deal price for the URL (= scraped listing price
                // when a linked direct deal product exists), otherwise MSRP.
                dealsMap.TryGetValue(dp.DealId, out var parentDeal);
                var finalPrice = scrapedPrice;

                if (parentDeal != null)
                {
                    var dealType = parentDeal.DealTypeId ?? 1;

                    if (dealType is 2 or 3 or 4) // Coupon, Stacked, or External
                    {
                        // Use scraped price as the direct listing baseline for all derived deal types.
                        // Falling back to MSRP can make derived coupon/external/stacked rows appear
                        // more expensive than the freshly-updated direct row.
                        decimal basePrice = scrapedPrice;

                        if (dealType is 2 or 4)
                        {
                            finalPrice = ApplyPercentOff(basePrice, parentDeal.DiscountPercent);
                        }
                        else // Stacked
                        {
                            finalPrice = ComputeStackedPrice(basePrice, parentDeal.Id, combosByDeal, componentDealsMap);
                        }
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
                        + (sanitySkipped > 0 ? $" Skipped {sanitySkipped} as suspicious (out of sanity bounds)." : "")
                    : sanitySkipped > 0
                        ? $"Skipped {sanitySkipped} deal product(s) where ${report.price:F2} looked like a bad scrape (out of sanity bounds)."
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

        /// <summary>
        /// POST /api/extension/product-candidate
        /// Admin-only "Add Product" submission from the Chrome extension on an
        /// approved retailer page. Captures product + paired deal data and
        /// performs tiered dedup before inserting a product_candidate (and
        /// linked deal_candidate) row for admin review.
        /// </summary>
        [HttpPost("product-candidate")]
        [Authorize]
        public async Task<ActionResult<ExtensionProductCandidateResponseDTO>> SubmitProductCandidate(
            [FromBody] ExtensionProductCandidateDTO body)
        {
            // ── Auth + admin gate ────────────────────────────────────────────
            var userIdStr = _authService.GetCurrentUserId();
            if (string.IsNullOrWhiteSpace(userIdStr) || !int.TryParse(userIdStr, out var submitterId))
                return Unauthorized();
            var submitter = await _userService.GetUserByIdAsync(submitterId);
            if (submitter == null) return Unauthorized();
            if (!submitter.Admin) return Forbid();

            if (body == null || body.storeId <= 0 || string.IsNullOrWhiteSpace(body.url) || string.IsNullOrWhiteSpace(body.name))
                return BadRequest(new ExtensionProductCandidateResponseDTO
                {
                    status = "invalid",
                    message = "storeId, url, and name are required."
                });

            var client = _supabase.GetServiceRoleClient();

            // ── Store gate: must be approved + browser-scrape allowed ────────
            var storeResp = await client
                .From<Store>()
                .Where(s => s.Id == body.storeId)
                .Limit(1)
                .Get();
            var store = storeResp.Models.FirstOrDefault();
            if (store == null || !store.Approved || !ScrapeMode.AllowsBrowserScrape(store.ScrapeModeId))
                return Ok(new ExtensionProductCandidateResponseDTO
                {
                    status = "invalid",
                    message = "Store is not approved for browser submissions."
                });

            // ── Canonicalize URL (no affiliate injection; we want clean dedup keys) ──
            var canonical = _urlSanitizer.CleanForStore(body.url, store, injectAffiliate: false)
                ?? NormaliseUrl(body.url);
            if (string.IsNullOrWhiteSpace(canonical))
                return BadRequest(new ExtensionProductCandidateResponseDTO
                {
                    status = "invalid",
                    message = "URL could not be parsed."
                });

            // ── Tier 1: URL matches a live deal_product for this store ──────
            var liveDealIdsResp = await client
                .From<Deal>()
                .Select("id")
                .Filter("store_id", Supabase.Postgrest.Constants.Operator.Equals, store.Id.ToString())
                .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
                .Get();
            var liveDealIds = (liveDealIdsResp.Models ?? new List<Deal>()).Select(d => d.Id).ToList();

            var liveDealProducts = new List<DealProduct>();
            foreach (var dealId in liveDealIds)
            {
                var dpResp = await client
                    .From<DealProduct>()
                    .Filter("deal_id", Supabase.Postgrest.Constants.Operator.Equals, dealId.ToString())
                    .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
                    .Get();
                liveDealProducts.AddRange(dpResp.Models ?? new List<DealProduct>());
            }

            var liveMatch = liveDealProducts.FirstOrDefault(dp => UrlsMatch(dp.Url, canonical));
            if (liveMatch != null)
            {
                _logger.LogInformation(
                    "[ProductCandidate] duplicate_live_product url={Url} productId={ProductId} submitter={UserId}",
                    canonical, liveMatch.ProductId, submitterId);
                return Ok(new ExtensionProductCandidateResponseDTO
                {
                    status = "duplicate_live_product",
                    productId = liveMatch.ProductId,
                    message = "This product is already tracked at this store."
                });
            }

            // ── Tier 2: same canonical URL already submitted as a candidate ─
            var candidateResp = await client
                .From<ProductCandidate>()
                .Filter("source_url_canonical", Supabase.Postgrest.Constants.Operator.Equals, canonical)
                .Limit(1)
                .Get();
            var existingCandidate = candidateResp.Models.FirstOrDefault();
            if (existingCandidate != null)
            {
                existingCandidate.SubmissionCount += 1;
                existingCandidate.LastSubmittedAt = DateTime.UtcNow;
                existingCandidate.SubmittersJsonb = AppendSubmitter(
                    existingCandidate.SubmittersJsonb,
                    submitterId,
                    body.url ?? canonical);
                await client.From<ProductCandidate>().Update(existingCandidate);

                _logger.LogInformation(
                    "[ProductCandidate] duplicate_candidate id={Id} count={Count} submitter={UserId}",
                    existingCandidate.Id, existingCandidate.SubmissionCount, submitterId);

                return Ok(new ExtensionProductCandidateResponseDTO
                {
                    status = "duplicate_candidate",
                    candidateId = existingCandidate.Id,
                    submissionCount = existingCandidate.SubmissionCount,
                    suggestedMergeProductId = existingCandidate.SuggestedMergeProductId,
                    message = "Already queued for admin review."
                });
            }

            // ── Tier 3: brand + normalized-name fuzzy match against live products ──
            var nameNormalized = NormalizeProductName(body.name);
            int? brandId = await ResolveBrandIdAsync(client, body.brand);
            int? productTypeId = null;
            int? suggestedMergeProductId = null;

            // AI inference: fill in brand_id (when name lookup missed) and
            // product_type_id (never scraped). Best-effort; on failure we just
            // leave the fields null and let the admin set them on approval.
            if (!brandId.HasValue || !productTypeId.HasValue)
            {
                try
                {
                    var inferred = await _metadataInference.InferAsync(
                        body.name!, body.brand, HttpContext.RequestAborted);
                    brandId ??= inferred.BrandId;
                    productTypeId ??= inferred.ProductTypeId;
                    if (inferred.BrandId.HasValue || inferred.ProductTypeId.HasValue)
                    {
                        _logger.LogInformation(
                            "[ProductCandidate] AI inferred brand={Brand} type={Type} conf={Conf} reason={Reason}",
                            inferred.BrandId, inferred.ProductTypeId, inferred.Confidence, inferred.Reason);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ProductCandidate] Metadata inference failed for \"{Name}\"", body.name);
                }
            }

            if (brandId.HasValue && !string.IsNullOrWhiteSpace(nameNormalized))
            {
                var brandProductsResp = await client
                    .From<Product>()
                    .Select("id, name, slug, brand_id, deleted, product_type_id")
                    .Filter("brand_id", Supabase.Postgrest.Constants.Operator.Equals, brandId.Value.ToString())
                    .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
                    .Get();

                var brandProducts = brandProductsResp.Models ?? new List<Product>();
                var bestScore = 0.0;
                int? bestId = null;
                foreach (var p in brandProducts)
                {
                    var pn = NormalizeProductName(p.Name);
                    var score = FuzzyScore(nameNormalized, pn);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestId = p.Id;
                    }
                }

                if (bestScore >= 0.70 && bestId.HasValue)
                    suggestedMergeProductId = bestId;
            }

            // ── Insert fresh product_candidate ───────────────────────────────
            // Seed image_url with the original URL so the admin grid shows
            // something immediately; the async rehost below overwrites it
            // with the WebP-rehosted URL when (and if) that succeeds.
            var insertRow = new ProductCandidateInsertRow
            {
                Source = "extension",
                SourceStoreId = store.Id,
                SourceUrlCanonical = canonical,
                Name = body.name!,
                NameNormalized = nameNormalized,
                BrandText = body.brand,
                BrandId = brandId,
                ProductTypeId = productTypeId,
                MSRP = body.msrp,
                ImageUrlOriginal = body.imageUrl,
                ImageUrl = body.imageUrl,
                Description = body.description,
                SuggestedMergeProductId = suggestedMergeProductId,
                SubmittedByUserId = submitterId,
                SubmittersJsonb = AppendSubmitter("[]", submitterId, body.url ?? canonical)
            };

            var insertResp = await client.From<ProductCandidateInsertRow>().Insert(insertRow);
            var created = await client
                .From<ProductCandidate>()
                .Filter("source_url_canonical", Supabase.Postgrest.Constants.Operator.Equals, canonical)
                .Limit(1)
                .Get();
            var candidate = created.Models.FirstOrDefault();
            if (candidate == null)
            {
                _logger.LogError("[ProductCandidate] Insert succeeded but lookup returned no row for url={Url}", canonical);
                return StatusCode(StatusCodes.Status500InternalServerError, new ExtensionProductCandidateResponseDTO
                {
                    status = "invalid",
                    message = "Candidate was created but could not be loaded."
                });
            }

            // ── Linked deal_candidate (paired submission) ────────────────────
            if (body.dealPrice is > 0)
            {
                var dealCandidate = new DealCandidateInsertRow
                {
                    Source = "extension",
                    StoreId = store.Id,
                    ProductCandidateId = candidate.Id,
                    DealUrlCanonical = canonical,
                    ListingPrice = body.dealPrice,
                    ListingCurrency = string.IsNullOrWhiteSpace(body.currency) ? "USD" : body.currency,
                    ListingMsrp = body.msrp,
                    ConditionCategoryId = body.conditionCategoryId,
                    InStock = body.inStock,
                    RawTitle = body.rawTitle
                };
                try
                {
                    await client.From<DealCandidateInsertRow>().Insert(dealCandidate);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ProductCandidate] Failed to insert linked deal_candidate for candidate {Id}", candidate.Id);
                }
            }

            // ── Image rehost (fire-and-forget into the 'candidates' bucket) ──
            if (!string.IsNullOrWhiteSpace(body.imageUrl))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var basePath = $"candidates/{candidate.Id}/{Guid.NewGuid():N}";
                        var result = await _productImageService.RehostAsync(body.imageUrl!, "products", basePath);
                        if (result.Success && !string.IsNullOrWhiteSpace(result.PublicUrl))
                        {
                            var serviceClient = _supabase.GetServiceRoleClient();
                            candidate.ImageUrl = result.PublicUrl;
                            await serviceClient.From<ProductCandidate>().Update(candidate);
                        }
                        else
                        {
                            _logger.LogInformation("[ProductCandidate] Image rehost skipped for candidate {Id}: {Error}", candidate.Id, result.Error);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[ProductCandidate] Image rehost failed for candidate {Id}", candidate.Id);
                    }
                });
            }

            var status = suggestedMergeProductId.HasValue ? "suggested_merge" : "created";
            _logger.LogInformation(
                "[ProductCandidate] {Status} id={Id} url={Url} brandId={BrandId} mergeSuggest={Merge}",
                status, candidate.Id, canonical, brandId, suggestedMergeProductId);

            return Ok(new ExtensionProductCandidateResponseDTO
            {
                status = status,
                candidateId = candidate.Id,
                suggestedMergeProductId = suggestedMergeProductId,
                submissionCount = 1,
                message = suggestedMergeProductId.HasValue
                    ? "Looks similar to an existing product — admin will confirm."
                    : "Queued for admin review."
            });
        }

        // ─── Helpers ──────────────────────────────────────────────────────

        /// <summary>Lowercase + collapse anything non-alphanumeric to a single space.</summary>
        private static string NormalizeProductName(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            var lower = input.Trim().ToLowerInvariant();
            var stripped = System.Text.RegularExpressions.Regex.Replace(lower, "[^a-z0-9]+", " ");
            return System.Text.RegularExpressions.Regex.Replace(stripped, "\\s+", " ").Trim();
        }

        /// <summary>
        /// Token-set similarity in [0,1]: shared-token ratio with a length-tolerance gate.
        /// 0.85+ ≈ strong match. 0.70+ ≈ admin-confirm merge suggestion.
        /// </summary>
        private static double FuzzyScore(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;
            var aTokens = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length > 1).ToHashSet();
            var bTokens = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length > 1).ToHashSet();
            if (aTokens.Count == 0 || bTokens.Count == 0) return 0;

            // Length gate so "Pro V1" doesn't 70%-match "Pro V1x Left Dash Yellow 2024 Limited Edition"
            var lenA = a.Length;
            var lenB = b.Length;
            var lenGate = Math.Max(3, (int)Math.Round(0.5 * Math.Max(lenA, lenB)));
            if (Math.Abs(lenA - lenB) > lenGate) return 0;

            var shared = aTokens.Intersect(bTokens).Count();
            var ratio = (2.0 * shared) / (aTokens.Count + bTokens.Count);
            return ratio;
        }

        private static async Task<int?> ResolveBrandIdAsync(Supabase.Client client, string? brandText)
        {
            if (string.IsNullOrWhiteSpace(brandText)) return null;
            var trimmed = brandText.Trim();
            var resp = await client
                .From<Brand>()
                .Select("id, name")
                .Filter("name", Supabase.Postgrest.Constants.Operator.ILike, trimmed)
                .Limit(1)
                .Get();
            var brand = resp.Models.FirstOrDefault();
            return brand?.Id;
        }

        /// <summary>
        /// Append {user_id, at, url} to a JSON array string. Tolerates malformed input
        /// by resetting to a single-element array.
        /// </summary>
        private static string AppendSubmitter(string existingJsonArray, int userId, string url)
        {
            JArray arr;
            try
            {
                arr = string.IsNullOrWhiteSpace(existingJsonArray)
                    ? new JArray()
                    : JArray.Parse(existingJsonArray);
            }
            catch
            {
                arr = new JArray();
            }
            arr.Add(new JObject
            {
                ["user_id"] = userId,
                ["at"] = DateTime.UtcNow.ToString("o"),
                ["url"] = url
            });
            return arr.ToString(Newtonsoft.Json.Formatting.None);
        }

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

        /// <summary>Compute the final price for a stacked deal by summing all component discount percentages.</summary>
        private static decimal ComputeStackedPrice(
            decimal listingPrice,
            int stackedDealId,
            Dictionary<int, List<DealCombo>> combosByDeal,
            Dictionary<int, Deal> componentDealsMap)
        {
            if (!combosByDeal.TryGetValue(stackedDealId, out var combos) || combos.Count == 0)
                return listingPrice;

            var totalDiscount = 0;
            foreach (var combo in combos)
            {
                if (!componentDealsMap.TryGetValue(combo.ComboDealId, out var comp))
                    continue;

                // Sum discount percentages for coupon/external components; skip direct components
                if (comp.DealTypeId is 2 or 4 && comp.DiscountPercent.HasValue && comp.DiscountPercent.Value > 0)
                    totalDiscount += comp.DiscountPercent.Value;
            }

            return ApplyPercentOff(listingPrice, totalDiscount > 0 ? totalDiscount : (int?)null);
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
