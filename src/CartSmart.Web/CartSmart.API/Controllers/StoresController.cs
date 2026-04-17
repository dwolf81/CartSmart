using CartSmart.API.Models;
using CartSmart.API.Models.DTOs;
using CartSmart.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Text.Json.Serialization;
using AngleSharp;
using AngleSharp.Dom;

namespace CartSmart.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class StoresController : ControllerBase
    {
        private readonly ISupabaseService _supabase;
        private readonly IAuthService _authService;
        private readonly IUserService _userService;
        private readonly IStoreDealsService _storeDealsService;

        public StoresController(ISupabaseService supabase, IAuthService authService, IUserService userService, IStoreDealsService storeDealsService)
        {
            _supabase = supabase;
            _authService = authService;
            _userService = userService;
            _storeDealsService = storeDealsService;
        }

        private async Task<User?> GetCurrentUserAsync()
        {
            var userIdStr = _authService.GetCurrentUserId();
            if (string.IsNullOrWhiteSpace(userIdStr) || !int.TryParse(userIdStr, out var userId))
                return null;
            return await _userService.GetUserByIdAsync(userId);
        }

        private async Task<IActionResult?> EnsureAdminAsync()
        {
            var user = await GetCurrentUserAsync();
            if (user == null) return Unauthorized();
            if (!user.Admin) return Forbid();
            return null;
        }

        private static string Slugify(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            var normalized = input.Trim().ToLowerInvariant();
            normalized = Regex.Replace(normalized, @"[^a-z0-9\s-]", "");
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            normalized = normalized.Replace(' ', '-');
            normalized = Regex.Replace(normalized, @"-+", "-").Trim('-');
            return normalized;
        }

        private static string GetContentType(string fileExtension)
        {
            return (fileExtension ?? string.Empty).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "application/octet-stream"
            };
        }

        private static async Task<byte[]> ConvertImageToWebP(byte[] imageBytes)
        {
            using var image = SixLabors.ImageSharp.Image.Load(imageBytes);
            using var output = new MemoryStream();
            await image.SaveAsWebpAsync(output, new WebpEncoder { Quality = 95 });
            return output.ToArray();
        }

        public sealed class ImportImageFromUrlRequest
        {
            [JsonPropertyName("imageUrl")]
            public string? ImageUrl { get; set; }
        }

        private static Uri? TryCreateHttpUri(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;

            if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var abs)
                && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
                return abs;

            var candidate = $"https://{url.Trim()}";
            if (Uri.TryCreate(candidate, UriKind.Absolute, out abs)
                && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
                return abs;

            return null;
        }

        private static (string Ext, string ContentType) GuessImageType(string? contentType, string? url)
        {
            var ct = (contentType ?? string.Empty).ToLowerInvariant();
            if (ct.StartsWith("image/jpeg")) return (".jpg", "image/jpeg");
            if (ct.StartsWith("image/png")) return (".png", "image/png");
            if (ct.StartsWith("image/gif")) return (".gif", "image/gif");
            if (ct.StartsWith("image/webp")) return (".webp", "image/webp");

            try
            {
                if (!string.IsNullOrWhiteSpace(url))
                {
                    var u = TryCreateHttpUri(url);
                    var ext = Path.GetExtension(u?.AbsolutePath ?? url);
                    if (!string.IsNullOrWhiteSpace(ext))
                    {
                        ext = ext.ToLowerInvariant();
                        if (ext is ".jpg" or ".jpeg") return (".jpg", "image/jpeg");
                        if (ext == ".png") return (".png", "image/png");
                        if (ext == ".gif") return (".gif", "image/gif");
                        if (ext == ".webp") return (".webp", "image/webp");
                    }
                }
            }
            catch { }

            return (".bin", "application/octet-stream");
        }

        [HttpGet]
        [AllowAnonymous]
        [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any, NoStore = false)]
        public async Task<ActionResult<IEnumerable<Store>>> GetAll()
        {
            // Use service-role for this read to avoid RLS silently returning empty results.
            // Store list is needed for public navigation.
            var client = _supabase.GetServiceRoleClient();
            var resp = await client
                .From<Store>()
                .Select("id, name, url, slug, image_url")
                .Filter("approved", Supabase.Postgrest.Constants.Operator.Equals, "true")
                .Order("name", Supabase.Postgrest.Constants.Ordering.Ascending)
                .Get();

            return Ok(resp.Models ?? new List<Store>());
        }

        [HttpGet("{storeId:int}/admin/edit")]
        [Authorize]
        public async Task<IActionResult> GetAdminEditData(int storeId)
        {
            var authResult = await EnsureAdminAsync();
            if (authResult != null) return authResult;

            var client = _supabase.GetServiceRoleClient();
            var storeResp = await client
                .From<Store>()
                .Where(s => s.Id == storeId)
                .Limit(1)
                .Get();

            var store = storeResp.Models.FirstOrDefault();
            if (store == null) return NotFound(new { message = "Store not found" });

            return Ok(new AdminStoreEditResponseDTO
            {
                store = new AdminStoreDTO
                {
                    id = store.Id,
                    name = store.Name,
                    url = store.URL,
                    affiliateCode = store.AffiliateCode,
                    affiliateCodeVar = store.AffiliateCodeVar,
                    brandId = store.BrandId,
                    upfrontCost = store.UpfrontCost,
                    upfrontCostTermId = store.UpfrontCostTermId,
                    apiEnabled = store.ApiEnabled,
                    scrapeModeId = store.ScrapeModeId,
                    scrapeConfig = store.ScrapeConfig,
                    requiredQueryVars = store.RequiredQueryVars,
                    slug = store.Slug,
                    approved = store.Approved,
                    imageUrl = store.ImageUrl,
                    description = store.Description,
                    scrapeHttpEnabled = store.ScrapeHttpEnabled,
                    scrapePlaywrightEnabled = store.ScrapePlaywrightEnabled
                }
            });
        }

        [HttpPost("admin")]
        [Authorize]
        public async Task<IActionResult> CreateAdminStore([FromBody] AdminUpsertStoreRequestDTO request)
        {
            var authResult = await EnsureAdminAsync();
            if (authResult != null) return authResult;

            if (request == null || string.IsNullOrWhiteSpace(request.name))
                return BadRequest(new { message = "name is required" });

            var desiredSlug = Slugify(string.IsNullOrWhiteSpace(request.slug) ? request.name : request.slug);
            if (string.IsNullOrWhiteSpace(desiredSlug))
                return BadRequest(new { message = "Unable to create slug" });

            var client = _supabase.GetServiceRoleClient();

            var slug = desiredSlug;
            for (var attempt = 0; attempt < 25; attempt++)
            {
                var existingResp = await client
                    .From<Store>()
                    .Filter("slug", Supabase.Postgrest.Constants.Operator.Equals, slug)
                    .Limit(1)
                    .Get();

                var existing = existingResp?.Models?.FirstOrDefault();
                if (existing == null) break;

                slug = $"{desiredSlug}-{attempt + 2}";
            }

            var insertRow = new StoreAdminInsertRow
            {
                Name = request.name.Trim(),
                URL = string.IsNullOrWhiteSpace(request.url) ? null : request.url.Trim(),
                AffiliateCode = string.IsNullOrWhiteSpace(request.affiliateCode) ? null : request.affiliateCode.Trim(),
                AffiliateCodeVar = string.IsNullOrWhiteSpace(request.affiliateCodeVar) ? null : request.affiliateCodeVar.Trim(),
                BrandId = request.brandId,
                UpfrontCost = request.upfrontCost,
                UpfrontCostTermId = request.upfrontCostTermId,
                ApiEnabled = request.apiEnabled,
                ScrapeModeId = request.scrapeModeId,
                ScrapeConfig = string.IsNullOrWhiteSpace(request.scrapeConfig) ? null : request.scrapeConfig,
                RequiredQueryVars = string.IsNullOrWhiteSpace(request.requiredQueryVars) ? null : request.requiredQueryVars,
                Slug = slug,
                Approved = request.approved ?? true,
                Description = string.IsNullOrWhiteSpace(request.description) ? null : request.description,
                ImageUrl = null,
                ScrapeHttpEnabled = request.scrapeHttpEnabled ?? true,
                ScrapePlaywrightEnabled = request.scrapePlaywrightEnabled ?? true
            };

            var insertResp = await client.From<StoreAdminInsertRow>().Insert(insertRow);
            var inserted = insertResp?.Models?.FirstOrDefault();
            if (inserted == null)
                return StatusCode(500, new { message = "Failed to create store" });

            return Ok(new AdminCreateStoreResponseDTO
            {
                id = inserted.Id,
                name = inserted.Name,
                url = inserted.URL,
                slug = inserted.Slug,
                approved = inserted.Approved,
                imageUrl = inserted.ImageUrl,
                description = inserted.Description
            });
        }

        [HttpPut("{storeId:int}/admin")]
        [Authorize]
        public async Task<IActionResult> UpdateAdminStore(int storeId, [FromBody] AdminUpsertStoreRequestDTO request)
        {
            var authResult = await EnsureAdminAsync();
            if (authResult != null) return authResult;

            if (request == null)
                return BadRequest(new { message = "body is required" });

            var client = _supabase.GetServiceRoleClient();
            var storeResp = await client
                .From<Store>()
                .Where(s => s.Id == storeId)
                .Limit(1)
                .Get();
            var existing = storeResp.Models.FirstOrDefault();
            if (existing == null) return NotFound(new { message = "Store not found" });

            var nextName = string.IsNullOrWhiteSpace(request.name) ? existing.Name : request.name.Trim();
            if (string.IsNullOrWhiteSpace(nextName))
                return BadRequest(new { message = "name is required" });

            var requestedSlugSource = string.IsNullOrWhiteSpace(request.slug) ? existing.Slug : request.slug;
            var desiredSlug = Slugify(string.IsNullOrWhiteSpace(requestedSlugSource) ? nextName : requestedSlugSource);
            if (string.IsNullOrWhiteSpace(desiredSlug))
                return BadRequest(new { message = "Unable to create slug" });

            // Ensure slug unique if changing
            var slugToUse = desiredSlug;
            if (!string.Equals(existing.Slug ?? string.Empty, desiredSlug, StringComparison.OrdinalIgnoreCase))
            {
                var baseSlug = desiredSlug;
                for (var attempt = 0; attempt < 25; attempt++)
                {
                    var existingResp = await client
                        .From<Store>()
                        .Filter("slug", Supabase.Postgrest.Constants.Operator.Equals, slugToUse)
                        .Limit(1)
                        .Get();

                    var match = existingResp?.Models?.FirstOrDefault();
                    if (match == null || match.Id == storeId) break;

                    slugToUse = $"{baseSlug}-{attempt + 2}";
                }
            }

            var updateRow = new StoreAdminUpdateRow
            {
                Id = storeId,
                Name = nextName,
                URL = string.IsNullOrWhiteSpace(request.url) ? null : request.url.Trim(),
                AffiliateCode = string.IsNullOrWhiteSpace(request.affiliateCode) ? null : request.affiliateCode.Trim(),
                AffiliateCodeVar = string.IsNullOrWhiteSpace(request.affiliateCodeVar) ? null : request.affiliateCodeVar.Trim(),
                BrandId = request.brandId,
                UpfrontCost = request.upfrontCost,
                UpfrontCostTermId = request.upfrontCostTermId,
                ApiEnabled = request.apiEnabled,
                ScrapeModeId = request.scrapeModeId,
                ScrapeConfig = string.IsNullOrWhiteSpace(request.scrapeConfig) ? null : request.scrapeConfig,
                RequiredQueryVars = string.IsNullOrWhiteSpace(request.requiredQueryVars) ? null : request.requiredQueryVars,
                Slug = slugToUse,
                Approved = request.approved ?? existing.Approved,
                Description = string.IsNullOrWhiteSpace(request.description) ? null : request.description,
                ScrapeHttpEnabled = request.scrapeHttpEnabled ?? existing.ScrapeHttpEnabled,
                ScrapePlaywrightEnabled = request.scrapePlaywrightEnabled ?? existing.ScrapePlaywrightEnabled
            };

            await client.From<StoreAdminUpdateRow>().Update(updateRow);

            // reload for a stable response
            var reloadedResp = await client
                .From<Store>()
                .Where(s => s.Id == storeId)
                .Limit(1)
                .Get();
            var persisted = reloadedResp.Models.FirstOrDefault();
            if (persisted == null) return StatusCode(500, new { message = "Failed to reload store after update" });

            return Ok(new AdminCreateStoreResponseDTO
            {
                id = persisted.Id,
                name = persisted.Name,
                url = persisted.URL,
                slug = persisted.Slug,
                approved = persisted.Approved,
                imageUrl = persisted.ImageUrl,
                description = persisted.Description
            });
        }

        [HttpPost("{storeId:int}/admin/image")]
        [Authorize]
        public async Task<IActionResult> UploadStoreImageAdmin(int storeId, IFormFile file)
        {
            var authResult = await EnsureAdminAsync();
            if (authResult != null) return authResult;

            if (file == null || file.Length == 0)
                return BadRequest("No file uploaded");

            var client = _supabase.GetServiceRoleClient();
            var storeResp = await client
                .From<Store>()
                .Where(s => s.Id == storeId)
                .Limit(1)
                .Get();
            var store = storeResp.Models.FirstOrDefault();
            if (store == null) return NotFound(new { message = "Store not found" });

            var fileExt = Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(fileExt)) fileExt = ".bin";

            // Store under stores/{storeId}/
            var name = $"{Guid.NewGuid():N}";
            var basePath = $"stores/{storeId}/{name}";
            var originalPath = $"{basePath}{fileExt}";
            var webpPath = $"{basePath}.webp";

            byte[] fileBytes;
            using (var stream = file.OpenReadStream())
            using (var ms = new MemoryStream())
            {
                await stream.CopyToAsync(ms);
                fileBytes = ms.ToArray();
            }

            // Upload original
            using (var originalStream = new MemoryStream(fileBytes))
            {
                await _supabase.UploadFileWithServiceRoleAsync(
                    "stores",
                    originalPath,
                    originalStream,
                    new Supabase.Storage.FileOptions
                    {
                        CacheControl = "3600",
                        Upsert = true,
                        ContentType = GetContentType(fileExt)
                    }
                );
            }

            // Upload WebP (site-facing)
            var webpBytes = await ConvertImageToWebP(fileBytes);
            using (var webpStream = new MemoryStream(webpBytes))
            {
                await _supabase.UploadFileWithServiceRoleAsync(
                    "stores",
                    webpPath,
                    webpStream,
                    new Supabase.Storage.FileOptions
                    {
                        CacheControl = "3600",
                        Upsert = true,
                        ContentType = "image/webp"
                    }
                );
            }

            var publicUrl = _supabase.GetPublicUrl("stores", webpPath);

            var updateRow = new StoreAdminImageUpdateRow
            {
                Id = store.Id,
                Slug = store.Slug,
                ImageUrl = publicUrl
            };
            await client.From<StoreAdminImageUpdateRow>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, store.Id.ToString())
                .Update(updateRow);

            return Ok(new { imageUrl = publicUrl });
        }

        [HttpPost("{storeId:int}/admin/image-from-url")]
        [Authorize]
        public async Task<IActionResult> ImportStoreImageFromUrlAdmin(int storeId, [FromBody] ImportImageFromUrlRequest request)
        {
            var authResult = await EnsureAdminAsync();
            if (authResult != null) return authResult;

            var rawUrl = request?.ImageUrl;
            var uri = TryCreateHttpUri(rawUrl);
            if (uri == null)
                return BadRequest(new { message = "imageUrl is required" });

            var client = _supabase.GetServiceRoleClient();
            var storeResp = await client
                .From<Store>()
                .Where(s => s.Id == storeId)
                .Limit(1)
                .Get();
            var store = storeResp.Models.FirstOrDefault();
            if (store == null) return NotFound(new { message = "Store not found" });

            byte[] fileBytes;
            string? contentType;

            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; CartSmart/1.0)");
                using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode)
                    return BadRequest(new { message = $"Failed to fetch image (HTTP {(int)resp.StatusCode})" });

                contentType = resp.Content.Headers.ContentType?.MediaType;
                var length = resp.Content.Headers.ContentLength;
                if (length.HasValue && length.Value > 10_000_000)
                    return BadRequest(new { message = "Image too large (max 10MB)." });

                fileBytes = await resp.Content.ReadAsByteArrayAsync();
            }

            if (fileBytes == null || fileBytes.Length == 0)
                return BadRequest(new { message = "Empty image response." });
            if (fileBytes.Length > 10_000_000)
                return BadRequest(new { message = "Image too large (max 10MB)." });

            try
            {
                using var _ = Image.Load(fileBytes);
            }
            catch
            {
                return BadRequest(new { message = "URL did not return a supported image." });
            }

            var (ext, originalContentType) = GuessImageType(contentType, uri.ToString());

            try
            {
                // Store under stores/{storeId}/
                var name = $"{Guid.NewGuid():N}";
                var basePath = $"stores/{storeId}/{name}";
                var originalPath = $"{basePath}{ext}";
                var webpPath = $"{basePath}.webp";

                // Upload original
                using (var originalStream = new MemoryStream(fileBytes))
                {
                    await _supabase.UploadFileWithServiceRoleAsync(
                        "stores",
                        originalPath,
                        originalStream,
                        new Supabase.Storage.FileOptions
                        {
                            CacheControl = "3600",
                            Upsert = true,
                            ContentType = originalContentType
                        }
                    );
                }

                // Upload WebP (site-facing)
                var webpBytes = await ConvertImageToWebP(fileBytes);
                using (var webpStream = new MemoryStream(webpBytes))
                {
                    await _supabase.UploadFileWithServiceRoleAsync(
                        "stores",
                        webpPath,
                        webpStream,
                        new Supabase.Storage.FileOptions
                        {
                            CacheControl = "3600",
                            Upsert = true,
                            ContentType = "image/webp"
                        }
                    );
                }

                var publicUrl = _supabase.GetPublicUrl("stores", webpPath);

                var updateRow = new StoreAdminImageUpdateRow
                {
                    Id = store.Id,
                    Slug = store.Slug,
                    ImageUrl = publicUrl
                };
                await client.From<StoreAdminImageUpdateRow>()
                    .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, store.Id.ToString())
                    .Update(updateRow);

                return Ok(new { imageUrl = publicUrl });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StoreImage] image-from-url failed for store {storeId}: {ex}");
                return StatusCode(500, new { message = $"Image processing failed: {ex.Message}" });
            }
        }

        [HttpGet("{slug}")]
        [AllowAnonymous]
        [ResponseCache(Duration = 900, Location = ResponseCacheLocation.Any, NoStore = false, VaryByQueryKeys = new[] { "_" })]
        public async Task<ActionResult<StorePageResponseDTO>> GetBySlug(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug))
                return BadRequest(new { message = "slug is required" });

            var client = _supabase.GetServiceRoleClient();

            var storeResp = await client
                .From<Store>()
                .Select("id, name, url, slug, approved, image_url, description")
                .Filter("slug", Supabase.Postgrest.Constants.Operator.Equals, slug)
                .Filter("approved", Supabase.Postgrest.Constants.Operator.Equals, "true")
                .Limit(1)
                .Get();

            var store = storeResp.Models.FirstOrDefault();
            if (store == null)
                return NotFound(new { message = "Store not found" });

            var response = new StorePageResponseDTO
            {
                store = new StoreSummaryDTO
                {
                    id = store.Id,
                    name = store.Name,
                    url = store.URL,
                    slug = store.Slug,
                    imageUrl = store.ImageUrl,
                    description = store.Description
                }
            };

            return Ok(response);
        }

        [HttpGet("{slug}/deals")]
        [AllowAnonymous]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<ActionResult<StoreDealsResponseDTO>> GetDealsBySlug(string slug, [FromQuery] long? productTypeId = null)
        {
            if (string.IsNullOrWhiteSpace(slug))
                return BadRequest(new { message = "slug is required" });

            var client = _supabase.GetServiceRoleClient();

            var storeResp = await client
                .From<Store>()
                .Select("id")
                .Filter("slug", Supabase.Postgrest.Constants.Operator.Equals, slug)
                .Filter("approved", Supabase.Postgrest.Constants.Operator.Equals, "true")
                .Limit(1)
                .Get();

            var store = storeResp.Models.FirstOrDefault();
            if (store == null)
                return NotFound(new { message = "Store not found" });

            var storeDeals = await _storeDealsService.GetStoreDealsAsync(store.Id);
            var productDeals = await _storeDealsService.GetStoreProductDealsAsync(store.Id, productTypeId);

            return Ok(new StoreDealsResponseDTO
            {
                storeDeals = storeDeals,
                products = productDeals
            });
        }

        // ────────────────────────────────────────────────────────────
        // POST /api/stores/admin/test-scrape
        // ────────────────────────────────────────────────────────────

        [HttpPost("admin/test-scrape")]
        [Authorize]
        public async Task<ActionResult<TestScrapeResponseDTO>> TestScrape([FromBody] TestScrapeRequestDTO request)
        {
            var adminCheck = await EnsureAdminAsync();
            if (adminCheck != null) return Unauthorized();

            var url = (request?.url ?? string.Empty).Trim();
            var configJson = (request?.scrapeConfig ?? string.Empty).Trim();
            var method = (request?.method ?? "http").Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(url))
                return BadRequest(new TestScrapeResponseDTO { success = false, error = "URL is required." });

            if (method != "http" && method != "playwright")
                return BadRequest(new TestScrapeResponseDTO { success = false, error = "method must be \"http\" or \"playwright\"." });

            // Parse price selectors from the scrape config JSON
            string[] selectors;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(configJson);
                if (!doc.RootElement.TryGetProperty("price_selectors", out var selArr)
                    || selArr.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    return BadRequest(new TestScrapeResponseDTO { success = false, error = "scrapeConfig must contain a \"price_selectors\" array." });
                }

                selectors = selArr.EnumerateArray()
                    .Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct()
                    .ToArray();

                if (selectors.Length == 0)
                    return BadRequest(new TestScrapeResponseDTO { success = false, error = "price_selectors array is empty." });
            }
            catch (System.Text.Json.JsonException)
            {
                return BadRequest(new TestScrapeResponseDTO { success = false, error = "Invalid JSON in scrapeConfig." });
            }

            try
            {
                string html;

                if (method == "playwright")
                {
                    html = await FetchHtmlWithPlaywrightAsync(url);
                }
                else
                {
                    html = await FetchHtmlWithHttpClientAsync(url);
                }

                // Parse with AngleSharp and extract prices
                return Ok(await ParseHtmlAndExtractPrices(html, selectors));
            }
            catch (TaskCanceledException)
            {
                return Ok(new TestScrapeResponseDTO { success = false, error = "Request timed out (15s limit)." });
            }
            catch (HttpRequestException ex)
            {
                return Ok(new TestScrapeResponseDTO { success = false, error = $"HTTP error: {ex.Message}" });
            }
            catch (Microsoft.Playwright.PlaywrightException ex)
            {
                return Ok(new TestScrapeResponseDTO { success = false, error = $"Playwright error: {ex.Message}" });
            }
            catch (Exception ex)
            {
                return Ok(new TestScrapeResponseDTO { success = false, error = $"Unexpected error: {ex.Message}" });
            }
        }

        private static async Task<string> FetchHtmlWithHttpClientAsync(string url)
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(15);
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            return await httpClient.GetStringAsync(url);
        }

        private static async Task<string> FetchHtmlWithPlaywrightAsync(string url)
        {
            using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new Microsoft.Playwright.BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = new[] { "--disable-blink-features=AutomationControlled" }
            });
            await using var context = await browser.NewContextAsync(new Microsoft.Playwright.BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                IgnoreHTTPSErrors = true
            });
            await context.AddInitScriptAsync("Object.defineProperty(navigator, 'webdriver', { get: () => undefined })");
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(20000);

            await page.GotoAsync(url, new Microsoft.Playwright.PageGotoOptions
            {
                WaitUntil = Microsoft.Playwright.WaitUntilState.Load,
                Timeout = 20000
            });

            // Wait for DOM to settle
            try
            {
                await page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.DOMContentLoaded,
                    new Microsoft.Playwright.PageWaitForLoadStateOptions { Timeout = 10000 });
            }
            catch { /* ignore */ }

            // Best-effort wait for price elements
            try
            {
                await page.WaitForSelectorAsync(
                    "[data-testid*='price'], span[class*='price'], div[class*='price']",
                    new Microsoft.Playwright.PageWaitForSelectorOptions { Timeout = 8000 });
            }
            catch { /* ignore */ }

            return await page.ContentAsync();
        }

        private async Task<TestScrapeResponseDTO> ParseHtmlAndExtractPrices(string html, string[] selectors)
        {
            var config = AngleSharp.Configuration.Default;
            var browsingContext = BrowsingContext.New(config);
            var document = await browsingContext.OpenAsync(req => req.Content(html));

            // Check for bot protection
            bool blockedByBot = ScrapeTestLooksBotBlocked(document);
            if (blockedByBot)
            {
                return new TestScrapeResponseDTO
                {
                    success = false,
                    error = "Page appears to be blocked by bot protection (JavaScript challenge page).",
                    blockedByBotProtection = true,
                    htmlLength = html.Length
                };
            }

            // Extract price candidates using the provided selectors
            var candidates = new List<TestScrapePriceCandidateDTO>();
            IElement? regionRoot = null;

            bool RegionContains(IElement el)
            {
                if (regionRoot == null) return false;
                var cur = el;
                while (cur != null)
                {
                    if (cur == regionRoot) return true;
                    cur = cur.ParentElement;
                }
                return false;
            }

            IElement SelectRegionRoot(IElement el)
            {
                var cur = el;
                while (cur.ParentElement != null && cur.ParentElement.TagName != "BODY")
                {
                    var clsId = ((cur.ClassName ?? "") + " " + (cur.Id ?? "")).ToLowerInvariant();
                    if (clsId.Contains("product") || clsId.Contains("price") || clsId.Contains("buy")
                        || clsId.Contains("main") || clsId.Contains("summary") || clsId.Contains("detail"))
                        return cur;
                    cur = cur.ParentElement;
                }
                return el.ParentElement ?? el;
            }

            foreach (var sel in selectors)
            {
                var els = document.QuerySelectorAll(sel);
                foreach (var el in els)
                {
                    if (regionRoot != null && !RegionContains(el))
                        continue;

                    var raw = el.GetAttribute("aria-label") ?? el.GetAttribute("content") ?? el.TextContent;
                    if (string.IsNullOrWhiteSpace(raw)) continue;

                    var promo = ScrapeTestLooksPromotional(raw);
                    var struck = ScrapeTestIsStruckThrough(el);
                    var cleaned = ScrapeTestCleanPriceText(raw);

                    if (ScrapeTestTryParsePrice(cleaned, out var price))
                    {
                        var currency = ScrapeTestDetectCurrency(raw ?? el.TextContent ?? string.Empty);
                        candidates.Add(new TestScrapePriceCandidateDTO
                        {
                            amount = price,
                            currency = currency,
                            struck = struck,
                            promo = promo,
                            selector = sel
                        });

                        if (regionRoot == null)
                            regionRoot = SelectRegionRoot(el);
                    }
                }

                if (regionRoot != null && candidates.Count >= 6) break;
            }

            if (candidates.Count == 0)
            {
                return new TestScrapeResponseDTO
                {
                    success = false,
                    error = "No prices found with the provided selectors.",
                    candidates = candidates,
                    htmlLength = html.Length
                };
            }

            // Select the best price (same logic as GenericHtmlScraper)
            decimal? bestPrice = null;
            string? bestCurrency = null;

            var preferred = candidates
                .Where(c => !c.struck && !c.promo)
                .OrderBy(c => c.amount)
                .FirstOrDefault();

            if (preferred != null && preferred.amount != 0)
            {
                bestPrice = preferred.amount;
                bestCurrency = preferred.currency;
            }
            else
            {
                var alt = candidates.Where(c => !c.struck).OrderBy(c => c.amount).FirstOrDefault();
                if (alt != null && alt.amount != 0)
                {
                    bestPrice = alt.amount;
                    bestCurrency = alt.currency;
                }
                else
                {
                    var any = candidates.OrderBy(c => c.amount).First();
                    bestPrice = any.amount;
                    bestCurrency = any.currency;
                }
            }

            // Stock detection
            var bodyText = document.Body?.TextContent?.ToLowerInvariant() ?? string.Empty;
            bool? inStock = null;
            if (bodyText.Contains("in stock") || bodyText.Contains("available")) inStock = true;
            if (bodyText.Contains("out of stock") || bodyText.Contains("unavailable")) inStock = false;

            return new TestScrapeResponseDTO
            {
                success = true,
                price = bestPrice,
                currency = bestCurrency ?? "USD",
                inStock = inStock,
                candidates = candidates,
                htmlLength = html.Length
            };
        }

        // ────────────────────────────────────────────────────────────
        // POST /api/stores/admin/test-scrape-screenshot
        // ────────────────────────────────────────────────────────────

        [HttpPost("admin/test-scrape-screenshot")]
        [Authorize]
        public async Task<IActionResult> TestScrapeScreenshot([FromBody] TestScrapeScreenshotRequestDTO request)
        {
            var adminCheck = await EnsureAdminAsync();
            if (adminCheck != null) return Unauthorized();

            var url = (request?.url ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(url))
                return BadRequest(new { error = "URL is required." });

            try
            {
                using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
                await using var browser = await playwright.Chromium.LaunchAsync(new Microsoft.Playwright.BrowserTypeLaunchOptions
                {
                    Headless = true,
                    Args = new[] { "--disable-blink-features=AutomationControlled" }
                });
                await using var context = await browser.NewContextAsync(new Microsoft.Playwright.BrowserNewContextOptions
                {
                    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                    IgnoreHTTPSErrors = true,
                    ViewportSize = new Microsoft.Playwright.ViewportSize { Width = 1280, Height = 800 }
                });
                await context.AddInitScriptAsync("Object.defineProperty(navigator, 'webdriver', { get: () => undefined })");
                var page = await context.NewPageAsync();
                page.SetDefaultTimeout(20000);

                await page.GotoAsync(url, new Microsoft.Playwright.PageGotoOptions
                {
                    WaitUntil = Microsoft.Playwright.WaitUntilState.Load,
                    Timeout = 20000
                });

                try { await page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle, new Microsoft.Playwright.PageWaitForLoadStateOptions { Timeout = 8000 }); } catch { }

                var screenshotBytes = await page.ScreenshotAsync(new Microsoft.Playwright.PageScreenshotOptions
                {
                    FullPage = false,
                    Type = Microsoft.Playwright.ScreenshotType.Jpeg,
                    Quality = 70
                });

                var base64 = Convert.ToBase64String(screenshotBytes);
                return Ok(new { success = true, image = $"data:image/jpeg;base64,{base64}" });
            }
            catch (Microsoft.Playwright.PlaywrightException ex)
            {
                return Ok(new { success = false, error = $"Playwright error: {ex.Message}" });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, error = $"Unexpected error: {ex.Message}" });
            }
        }

        // ────────────────────────────────────────────────────────────
        // Scrape Report Endpoints
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// GET /api/stores/admin/scrape-report
        /// Returns per-store scrape success/fail summary grouped by method.
        /// Query params: days (default 7)
        /// </summary>
        [HttpGet("admin/scrape-report")]
        [Authorize]
        public async Task<IActionResult> GetScrapeReport([FromQuery] int days = 7)
        {
            var adminCheck = await EnsureAdminAsync();
            if (adminCheck != null) return Unauthorized();

            var client = _supabase.GetServiceRoleClient();
            var cutoff = DateTime.UtcNow.AddDays(-days);

            // Fetch all scrape logs within the time window
            var logResp = await client
                .From<ScrapeLog>()
                .Filter("created_at", Supabase.Postgrest.Constants.Operator.GreaterThanOrEqual, cutoff.ToString("o"))
                .Order("created_at", Supabase.Postgrest.Constants.Ordering.Descending)
                .Get();

            var logs = logResp.Models ?? new List<ScrapeLog>();

            // Fetch stores that have scraping enabled
            var storeResp = await client
                .From<Store>()
                .Filter("scrape_mode_id", Supabase.Postgrest.Constants.Operator.GreaterThan, "0")
                .Get();

            var stores = storeResp.Models ?? new List<Store>();

            // Group logs by store
            var logsByStore = logs.GroupBy(l => l.StoreId).ToDictionary(g => g.Key, g => g.ToList());

            var summaries = new List<ScrapeReportStoreSummaryDTO>();

            foreach (var store in stores)
            {
                logsByStore.TryGetValue(store.Id, out var storeLogs);
                storeLogs ??= new List<ScrapeLog>();

                var summary = new ScrapeReportStoreSummaryDTO
                {
                    storeId = store.Id,
                    storeName = store.Name ?? "Unknown",
                    storeUrl = store.URL,
                    scrapeModeId = store.ScrapeModeId ?? 0,
                    scrapeHttpEnabled = store.ScrapeHttpEnabled,
                    scrapePlaywrightEnabled = store.ScrapePlaywrightEnabled,
                    http = new ScrapeMethodSummaryDTO
                    {
                        successCount = storeLogs.Count(l => l.Method == "http" && l.Success),
                        failCount = storeLogs.Count(l => l.Method == "http" && !l.Success)
                    },
                    playwright = new ScrapeMethodSummaryDTO
                    {
                        successCount = storeLogs.Count(l => l.Method == "playwright" && l.Success),
                        failCount = storeLogs.Count(l => l.Method == "playwright" && !l.Success)
                    },
                    extension = new ScrapeMethodSummaryDTO
                    {
                        successCount = storeLogs.Count(l => l.Method == "extension" && l.Success),
                        failCount = storeLogs.Count(l => l.Method == "extension" && !l.Success)
                    },
                    lastLogAt = storeLogs.FirstOrDefault()?.CreatedAt
                };

                summaries.Add(summary);
            }

            // Sort: stores with logs first (by most recent), then stores without logs
            summaries = summaries
                .OrderByDescending(s => s.lastLogAt.HasValue)
                .ThenByDescending(s => s.lastLogAt)
                .ToList();

            return Ok(summaries);
        }

        /// <summary>
        /// GET /api/stores/admin/scrape-report/{storeId}
        /// Returns detailed scrape logs for a specific store.
        /// Query params: days (default 7), limit (default 200)
        /// </summary>
        [HttpGet("admin/scrape-report/{storeId:int}")]
        [Authorize]
        public async Task<IActionResult> GetScrapeReportDetail(int storeId, [FromQuery] int days = 7, [FromQuery] int limit = 200)
        {
            var adminCheck = await EnsureAdminAsync();
            if (adminCheck != null) return Unauthorized();

            var client = _supabase.GetServiceRoleClient();
            var cutoff = DateTime.UtcNow.AddDays(-days);

            var logResp = await client
                .From<ScrapeLog>()
                .Filter("store_id", Supabase.Postgrest.Constants.Operator.Equals, storeId.ToString())
                .Filter("created_at", Supabase.Postgrest.Constants.Operator.GreaterThanOrEqual, cutoff.ToString("o"))
                .Order("created_at", Supabase.Postgrest.Constants.Ordering.Descending)
                .Limit(limit)
                .Get();

            var logs = logResp.Models ?? new List<ScrapeLog>();

            return Ok(logs.Select(l => new ScrapeReportDetailDTO
            {
                id = l.Id,
                dealProductId = l.DealProductId,
                url = l.Url,
                method = l.Method,
                success = l.Success,
                price = l.Price,
                currency = l.Currency,
                errorMessage = l.ErrorMessage,
                createdAt = l.CreatedAt
            }).ToList());
        }

        /// <summary>
        /// PATCH /api/stores/admin/{storeId}/scrape-methods
        /// Toggles scrape_http_enabled and/or scrape_playwright_enabled for a store.
        /// </summary>
        [HttpPatch("admin/{storeId:int}/scrape-methods")]
        [Authorize]
        public async Task<IActionResult> UpdateScrapeMethods(int storeId, [FromBody] UpdateScrapeMethodsRequestDTO request)
        {
            var adminCheck = await EnsureAdminAsync();
            if (adminCheck != null) return Unauthorized();

            var client = _supabase.GetServiceRoleClient();
            var storeResp = await client
                .From<Store>()
                .Where(s => s.Id == storeId)
                .Limit(1)
                .Get();
            var store = storeResp.Models.FirstOrDefault();
            if (store == null) return NotFound(new { message = "Store not found" });

            var updateRow = new StoreAdminUpdateRow
            {
                Id = storeId,
                Name = store.Name,
                URL = store.URL,
                AffiliateCode = store.AffiliateCode,
                AffiliateCodeVar = store.AffiliateCodeVar,
                BrandId = store.BrandId,
                UpfrontCost = store.UpfrontCost,
                UpfrontCostTermId = store.UpfrontCostTermId,
                ApiEnabled = store.ApiEnabled,
                ScrapeModeId = store.ScrapeModeId,
                ScrapeConfig = store.ScrapeConfig,
                RequiredQueryVars = store.RequiredQueryVars,
                Slug = store.Slug,
                Approved = store.Approved,
                Description = store.Description,
                ScrapeHttpEnabled = request.scrapeHttpEnabled ?? store.ScrapeHttpEnabled,
                ScrapePlaywrightEnabled = request.scrapePlaywrightEnabled ?? store.ScrapePlaywrightEnabled
            };

            await client.From<StoreAdminUpdateRow>().Update(updateRow);

            return Ok(new
            {
                storeId,
                scrapeHttpEnabled = updateRow.ScrapeHttpEnabled,
                scrapePlaywrightEnabled = updateRow.ScrapePlaywrightEnabled
            });
        }

        #region Test-scrape helpers (mirrors GenericHtmlScraper logic)

        private static bool ScrapeTestLooksBotBlocked(IDocument doc)
        {
            var title = doc.Title?.ToLowerInvariant() ?? string.Empty;
            if (title.Contains("security checkpoint")) return true;

            var bodyText = doc.Body?.TextContent?.ToLowerInvariant() ?? string.Empty;
            if (bodyText.Contains("verifying your browser")) return true;
            if (bodyText.Contains("enable javascript to continue")) return true;
            if (bodyText.Contains("vercel security checkpoint")) return true;

            var html = doc.DocumentElement?.OuterHtml?.ToLowerInvariant() ?? string.Empty;
            if (html.Contains("security-checkpoint")) return true;

            return false;
        }

        private static string ScrapeTestCleanPriceText(string s)
        {
            var trimmed = Regex.Replace(s.Trim(), "\\s+", " ");
            var halfLen = trimmed.Length / 2;
            if (halfLen > 0 && trimmed.Substring(0, halfLen)
                .Equals(trimmed.Substring(halfLen), StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(0, halfLen);
            return trimmed;
        }

        private static bool ScrapeTestLooksPromotional(string s)
        {
            var t = s.ToLowerInvariant();
            return t.Contains("save") || t.Contains("discount") || t.Contains("off");
        }

        private static bool ScrapeTestIsStruckThrough(IElement el)
        {
            var style = el.GetAttribute("style")?.ToLowerInvariant() ?? string.Empty;
            if (style.Contains("line-through")) return true;
            var cls = el.ClassName?.ToLowerInvariant() ?? string.Empty;
            if (!string.IsNullOrEmpty(cls) && (
                cls.Contains("strike") || cls.Contains("strikethrough") ||
                cls.Contains("line-through") || cls.Contains("text-decor_line-through") ||
                cls.Contains("was-price") || cls.Contains("old-price") || cls.Contains("list-price")))
                return true;
            var parent = el.ParentElement;
            if (parent != null)
            {
                var pStyle = parent.GetAttribute("style")?.ToLowerInvariant() ?? string.Empty;
                var pCls = parent.ClassName?.ToLowerInvariant() ?? string.Empty;
                if (pStyle.Contains("line-through")) return true;
                if (!string.IsNullOrEmpty(pCls) && (
                    pCls.Contains("strike") || pCls.Contains("strikethrough") ||
                    pCls.Contains("was-price") || pCls.Contains("old-price") || pCls.Contains("list-price")))
                    return true;
            }
            return false;
        }

        private static bool ScrapeTestTryParsePrice(string? s, out decimal price)
        {
            price = 0m;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var m = Regex.Match(s, "(?<![A-Za-z0-9])([0-9]{1,3}(?:,[0-9]{3})*(?:\\.[0-9]{1,2})?|[0-9]+(?:\\.[0-9]{1,2})?)");
            if (!m.Success) return false;
            var num = m.Groups[1].Value.Replace(",", "");
            return decimal.TryParse(num, out price);
        }

        private static string? ScrapeTestDetectCurrency(string s)
        {
            s = s.ToUpperInvariant();
            if (s.Contains("USD") || s.Contains("US $") || s.Contains("$")) return "USD";
            if (s.Contains("EUR") || s.Contains("€")) return "EUR";
            if (s.Contains("GBP") || s.Contains("£")) return "GBP";
            return null;
        }

        #endregion
    }
}
