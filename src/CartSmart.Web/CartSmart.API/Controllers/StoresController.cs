using CartSmart.API.Models;
using CartSmart.API.Models.DTOs;
using CartSmart.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using System.Text.RegularExpressions;

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
            await image.SaveAsWebpAsync(output, new WebpEncoder { Quality = 85 });
            return output.ToArray();
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
                    scrapeEnabled = store.ScrapeEnabled,
                    scrapeConfig = store.ScrapeConfig,
                    requiredQueryVars = store.RequiredQueryVars,
                    slug = store.Slug,
                    approved = store.Approved,
                    imageUrl = store.ImageUrl,
                    description = store.Description
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
                ScrapeEnabled = request.scrapeEnabled,
                ScrapeConfig = string.IsNullOrWhiteSpace(request.scrapeConfig) ? null : request.scrapeConfig,
                RequiredQueryVars = string.IsNullOrWhiteSpace(request.requiredQueryVars) ? null : request.requiredQueryVars,
                Slug = slug,
                Approved = request.approved ?? true,
                Description = string.IsNullOrWhiteSpace(request.description) ? null : request.description,
                ImageUrl = null
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
                ScrapeEnabled = request.scrapeEnabled,
                ScrapeConfig = string.IsNullOrWhiteSpace(request.scrapeConfig) ? null : request.scrapeConfig,
                RequiredQueryVars = string.IsNullOrWhiteSpace(request.requiredQueryVars) ? null : request.requiredQueryVars,
                Slug = slugToUse,
                Approved = request.approved ?? existing.Approved,
                Description = string.IsNullOrWhiteSpace(request.description) ? null : request.description
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
            await client.From<StoreAdminImageUpdateRow>().Update(updateRow);

            return Ok(new { imageUrl = publicUrl });
        }

        [HttpGet("{slug}")]
        [AllowAnonymous]
        [ResponseCache(Duration = 900, Location = ResponseCacheLocation.Any, NoStore = false, VaryByQueryKeys = new[] { "productTypeId", "_" })]
        public async Task<ActionResult<StorePageResponseDTO>> GetBySlug(string slug, [FromQuery] long? productTypeId = null)
        {
            if (string.IsNullOrWhiteSpace(slug))
                return BadRequest(new { message = "slug is required" });

            var client = _supabase.GetServiceRoleClient();

            var storeResp = await client
                .From<Store>()
                .Select("id, name, url, slug, approved, image_url")
                .Filter("slug", Supabase.Postgrest.Constants.Operator.Equals, slug)
                .Filter("approved", Supabase.Postgrest.Constants.Operator.Equals, "true")
                .Limit(1)
                .Get();

            var store = storeResp.Models.FirstOrDefault();
            if (store == null)
                return NotFound(new { message = "Store not found" });

            var storeDeals = await _storeDealsService.GetStoreDealsAsync(store.Id); 

            var productDeals = await _storeDealsService.GetStoreProductDealsAsync(store.Id, productTypeId);

            var response = new StorePageResponseDTO
            {
                store = new StoreSummaryDTO
                {
                    id = store.Id,
                    name = store.Name,
                    url = store.URL,
                    slug = store.Slug,
                    imageUrl = store.ImageUrl
                },
                storeDeals = storeDeals,
                products = productDeals
            };

            return Ok(response);
        }
    }
}
