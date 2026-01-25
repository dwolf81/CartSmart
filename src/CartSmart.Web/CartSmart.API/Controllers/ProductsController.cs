using Microsoft.AspNetCore.Mvc;
using CartSmart.API.Models;
using CartSmart.API.Services;
using CartSmart.API.Models.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
using System.Text.RegularExpressions;
using AttributeModel = CartSmart.API.Models.Attribute;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;

namespace CartSmart.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ProductsController : ControllerBase
    {
        private readonly IProductService _productService;
        private readonly ISupabaseService _supabase;
        private readonly IAuthService _authService;
        private readonly IUserService _userService;
        private readonly IMemoryCache _cache;

        public ProductsController(
            IProductService productService,
            ISupabaseService supabase,
            IAuthService authService,
            IUserService userService,
            IMemoryCache cache)
        {
            _productService = productService;
            _supabase = supabase;
            _authService = authService;
            _userService = userService;
            _cache = cache;
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

        private void InvalidateProductCaches(int productId, string? slug)
        {
            _cache.Remove($"product:id:{productId}");
            if (!string.IsNullOrWhiteSpace(slug))
                _cache.Remove($"product:slug:{slug}");
        }

        private void InvalidateCategoryProductsCache(int productTypeId)
        {
            if (productTypeId <= 0) return;
            _cache.Remove($"categoryProducts:productTypeId:{productTypeId}");
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

        [HttpGet("getproduct/{identifier}")]
        [AllowAnonymous]
        [ResponseCache(Duration = 1800, Location = ResponseCacheLocation.Any, NoStore = false)]
        public async Task<ActionResult<ProductDTO>> GetProductBySlug(string identifier)
        {
            ProductDTO? product;    
            if (int.TryParse(identifier, out int id))
            {
                product = await _productService.GetProductByIdAsync(id);
            }
            else
            {
                product = await _productService.GetProductBySlugAsync(identifier);
            }

            if (product == null)
            {
                return NotFound(); // Explicitly return 404 when product is null
            }
            return Ok(product);
        }

        [HttpGet("getbestproductdeals")]
        [AllowAnonymous]
        [ResponseCache(Duration = 900, Location = ResponseCacheLocation.Any, NoStore = false)]
        public async Task<ActionResult<IEnumerable<DealDisplayDTO>>> GetBestProductDeals()
        {
            return Ok(await _productService.GetBestProductDealsAsync());
        }

        // Returns all products for a given product type (category), with optional best-deal enrichment.
        [HttpGet("byproducttype")]
        [AllowAnonymous]
        //[ResponseCache(Duration = 900, Location = ResponseCacheLocation.Any, NoStore = false, VaryByQueryKeys = new[] { "productType" })]
        public async Task<ActionResult<IEnumerable<CategoryProductCardDTO>>> GetProductsByProductType([FromQuery] string productType, [FromQuery] int? brandId = null)
        {
            if (string.IsNullOrWhiteSpace(productType))
                return BadRequest(new { message = "productType is required" });

            var results = await _productService.GetCategoryProductsAsync(productType, brandId);
            return Ok(results);
        }

        // Returns the available brands for a given product type (category).
        [HttpGet("brands/byproducttype")]
        [AllowAnonymous]
        [ResponseCache(Duration = 900, Location = ResponseCacheLocation.Any, NoStore = false, VaryByQueryKeys = new[] { "productType" })]
        public async Task<ActionResult<IEnumerable<BrandDTO>>> GetBrandsByProductType([FromQuery] string productType)
        {
            if (string.IsNullOrWhiteSpace(productType))
                return BadRequest(new { message = "productType is required" });

            var results = await _productService.GetCategoryBrandsAsync(productType);
            return Ok(results);
        }

        [HttpPost("admin")]
        [Authorize]
        public async Task<ActionResult<AdminCreateProductResponseDTO>> CreateAdminProduct([FromBody] AdminCreateProductRequestDTO request)
        {
            var user = await GetCurrentUserAsync();
            if (user == null) return Unauthorized();
            if (!user.Admin) return Forbid();

            if (request == null || string.IsNullOrWhiteSpace(request.Name))
                return BadRequest(new { message = "name is required" });
            if (request.ProductTypeId <= 0)
                return BadRequest(new { message = "productTypeId is required" });

            var desiredSlug = Slugify(request.Name);
            if (string.IsNullOrWhiteSpace(desiredSlug))
                return BadRequest(new { message = "Unable to create slug from name" });

            var client = _supabase.GetServiceRoleClient();

            var slug = desiredSlug;
            for (var attempt = 0; attempt < 25; attempt++)
            {
                var existingResp = await client
                    .From<Product>()
                    .Filter("slug", Supabase.Postgrest.Constants.Operator.Equals, slug)
                    .Limit(1)
                    .Get();

                var existing = existingResp?.Models?.FirstOrDefault();
                if (existing == null) break;

                slug = $"{desiredSlug}-{attempt + 2}";
            }

            var insertRow = new ProductAdminInsertRow
            {
                Slug = slug,
                Name = request.Name.Trim(),
                MSRP = request.Msrp,
                Description = request.Description,
                ProductTypeId = request.ProductTypeId,
                UserId = user.Id,
                BrandId = request.BrandId,
                Deleted = false
            };

            var insertResp = await client.From<ProductAdminInsertRow>().Insert(insertRow);
            var inserted = insertResp?.Models?.FirstOrDefault();
            if (inserted == null)
                return StatusCode(500, new { message = "Failed to create product" });

            // Ensure every product starts with at least one placeholder variant.
            // This variant intentionally has no attributes; it represents the "no variant" case.
            var now = DateTime.UtcNow;
            var placeholderVariant = new ProductVariant
            {
                ProductId = inserted.Id,
                VariantName = null,
                UnitCount = null,
                UnitType = null,
                DisplayName = "Default",
                NormalizedTitle = NormalizeTitle("Default"),
                IsDefault = true,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };

            try
            {
                await client.From<ProductVariant>().Insert(placeholderVariant);
            }
            catch
            {
                // Best-effort cleanup: don't leave a half-created product without a default variant.
                try
                {
                    inserted.Deleted = true;
                    await client.From<ProductAdminInsertRow>().Update(inserted);
                }
                catch { }

                return StatusCode(500, new { message = "Failed to create default product variant" });
            }

            InvalidateProductCaches(inserted.Id, inserted.Slug);
            InvalidateCategoryProductsCache(request.ProductTypeId);

            return Ok(new AdminCreateProductResponseDTO
            {
                Id = inserted.Id,
                Name = inserted.Name,
                Msrp = inserted.MSRP,
                Description = inserted.Description,
                Slug = inserted.Slug
            });
        }

        [HttpGet("{productId}/ratings")]
        [AllowAnonymous]
        public async Task<ActionResult<IEnumerable<object>>> GetProductRatings(int productId)
        {
            var ratings = await _productService.GetProductRatingsAsync(productId);
            return Ok(ratings);
        }

        [HttpGet("{productId}/variant-filters")]
        [AllowAnonymous]
        public async Task<ActionResult<VariantFilterOptionsDTO>> GetVariantFilters(int productId)
        {
            var dto = await _productService.GetVariantFilterOptionsAsync(productId);
            return Ok(dto);
        }

        // ----- ADMIN EDIT ENDPOINTS -----
        [HttpGet("{productId}/admin/edit")]
        [Authorize]
        public async Task<IActionResult> GetAdminEditData(int productId)
        {
            var authResult = await EnsureAdminAsync();
            if (authResult != null) return authResult;

            var client = _supabase.GetServiceRoleClient();
            var productResp = await client
                .From<Product>()
                .Where(p => p.Id == productId && p.Deleted == false)
                .Limit(1)
                .Get();
            var product = productResp.Models.FirstOrDefault();
            if (product == null) return NotFound(new { message = "Product not found" });

            // Load product attributes for this product (product_attribute)
            var paTable = await _supabase.QueryTable<ProductAttribute>();
            var paResp = await paTable
                .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, productId)
                .Get();
            var productAttributes = paResp.Models ?? new List<ProductAttribute>();
            var attributeIds = productAttributes
                .Select(x => x.AttributeId)
                .Distinct()
                .ToList();

            var attributes = new List<AttributeModel>();
            var enumValues = new List<AttributeEnumValue>();
            var disabledEnumValueIds = new HashSet<int>();
            if (attributeIds.Count > 0)
            {
                var attributeIdObjects = attributeIds.Cast<object>().ToArray();
                var attributesResp = await client
                    .From<AttributeModel>()
                    .Filter("id", Supabase.Postgrest.Constants.Operator.In, attributeIdObjects)
                    .Get();
                attributes = attributesResp.Models ?? new List<AttributeModel>();

                var enumResp = await client
                    .From<AttributeEnumValue>()
                    .Filter("attribute_id", Supabase.Postgrest.Constants.Operator.In, attributeIdObjects)
                    .Get();
                enumValues = enumResp.Models ?? new List<AttributeEnumValue>();

                // Per-product disabled enums
                var disabledTable = await _supabase.QueryTable<ProductAttributeEnumDisabled>();
                var disabledResp = await disabledTable
                    .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, productId)
                    .Filter("attribute_id", Supabase.Postgrest.Constants.Operator.In, attributeIdObjects)
                    .Get();
                var disabledRows = disabledResp.Models ?? new List<ProductAttributeEnumDisabled>();
                disabledEnumValueIds = disabledRows
                    .Select(x => x.EnumValueId)
                    .ToHashSet();
            }

            // Provide attribute catalog to allow adding missing attributes
            var allAttrResp = await client
                .From<AttributeModel>()
                .Get();
            var allAttributes = allAttrResp.Models ?? new List<AttributeModel>();

            var dto = new AdminProductEditResponseDTO
            {
                Product = new AdminProductDTO
                {
                    Id = product.Id,
                    Name = product.Name,
                    Msrp = product.MSRP,
                    Description = product.Description,
                    Slug = product.Slug,
                    ImageUrl = product.ImageUrl,
                    BrandId = product.BrandId
                },
                Attributes = attributes
                    .OrderBy(a => a.AttributeKey)
                    .Select(a => new AdminProductAttributeDTO
                    {
                        AttributeId = a.Id,
                        AttributeKey = a.AttributeKey,
                        DataType = a.DataType,
                        Description = a.Description,
                        IsRequired = productAttributes.FirstOrDefault(pa => pa.AttributeId == a.Id)?.IsRequired ?? false,
                        Options = enumValues
                            .Where(ev => ev.AttributeId == a.Id)
                            .OrderBy(ev => ev.SortOrder)
                            .ThenBy(ev => ev.DisplayName)
                            .Select(ev => new AdminAttributeEnumValueDTO
                            {
                                Id = ev.Id,
                                EnumKey = ev.EnumKey,
                                DisplayName = ev.DisplayName,
                                SortOrder = ev.SortOrder,
                                IsActive = ev.IsActive,
                                IsEnabled = !disabledEnumValueIds.Contains(ev.Id)
                            })
                            .ToList()
                    })
                    .ToList(),
                AvailableAttributes = allAttributes
                    .Where(a => !attributeIds.Contains(a.Id))
                    .OrderBy(a => a.AttributeKey)
                    .Select(a => new AdminAttributeCatalogItemDTO
                    {
                        AttributeId = a.Id,
                        AttributeKey = a.AttributeKey,
                        DataType = a.DataType,
                        Description = a.Description
                    })
                    .ToList()
            };

            return Ok(dto);
        }

        [HttpPost("{productId}/admin/image")]
        [Authorize]
        public async Task<IActionResult> UploadProductImageAdmin(int productId, IFormFile file)
        {
            var authResult = await EnsureAdminAsync();
            if (authResult != null) return authResult;

            if (file == null || file.Length == 0)
                return BadRequest("No file uploaded");

            var client = _supabase.GetServiceRoleClient();
            var productResp = await client
                .From<Product>()
                .Where(p => p.Id == productId && p.Deleted == false)
                .Limit(1)
                .Get();
            var product = productResp.Models.FirstOrDefault();
            if (product == null) return NotFound(new { message = "Product not found" });

            var fileExt = Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(fileExt)) fileExt = ".bin";

            // Generate a new GUID name, store under products/{productId}/
            var name = $"{Guid.NewGuid():N}";
            var basePath = $"{productId}/{name}";
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
                    "products",
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
                    "products",
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

            var publicUrl = _supabase.GetPublicUrl("products", webpPath);

            // Avoid PostgREST clearing slug by always including it.
            var updateRow = new ProductAdminImageUpdateRow
            {
                Id = product.Id,
                Slug = product.Slug,
                ImageUrl = publicUrl
            };
            await client.From<ProductAdminImageUpdateRow>().Update(updateRow);

            InvalidateProductCaches(productId, product.Slug);

            return Ok(new { imageUrl = publicUrl });
        }

        [HttpPost("{productId}/admin/product-attributes")]
        [Authorize]
        public async Task<IActionResult> AddProductAttributeAdmin(int productId, [FromBody] AdminUpsertProductAttributeRequestDTO req)
        {
            var authResult = await EnsureAdminAsync();
            if (authResult != null) return authResult;
            if (req.AttributeId <= 0) return BadRequest(new { message = "attributeId is required" });

            var client = _supabase.GetServiceRoleClient();
            var productResp = await client
                .From<Product>()
                .Where(p => p.Id == productId && p.Deleted == false)
                .Limit(1)
                .Get();
            var product = productResp.Models.FirstOrDefault();
            if (product == null) return NotFound(new { message = "Product not found" });

            // Ensure attribute exists
            var attrResp = await client
                .From<AttributeModel>()
                .Where(a => a.Id == req.AttributeId)
                .Limit(1)
                .Get();
            var attr = attrResp.Models.FirstOrDefault();
            if (attr == null) return NotFound(new { message = "Attribute not found" });

            // Upsert: table is composite keyed; easiest path is delete then insert if exists.
            var paTable = await _supabase.QueryTable<ProductAttribute>();
            var existingResp = await paTable
                .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, productId)
                .Filter("attribute_id", Supabase.Postgrest.Constants.Operator.Equals, req.AttributeId)
                .Get();
            var existing = existingResp.Models?.FirstOrDefault();
            if (existing != null)
            {
                existing.IsRequired = req.IsRequired;
                await client.From<ProductAttribute>().Update(existing);
            }
            else
            {
                var row = new ProductAttribute
                {
                    ProductId = productId,
                    AttributeId = req.AttributeId,
                    IsRequired = req.IsRequired
                };
                await client.From<ProductAttribute>().Insert(row);
            }

            InvalidateProductCaches(productId, product.Slug);
            return Ok(new { success = true });
        }

        [HttpPost("{productId}/admin/attributes")]
        [Authorize]
        public async Task<IActionResult> CreateAttributeAndAttachToProductAdmin(int productId, [FromBody] AdminCreateAttributeRequestDTO req)
        {
            var authResult = await EnsureAdminAsync();
            if (authResult != null) return authResult;

            var rawName = req.Name?.Trim();
            var rawKey = req.AttributeKey?.Trim();
            var source = !string.IsNullOrWhiteSpace(rawName) ? rawName : rawKey;
            if (string.IsNullOrWhiteSpace(source))
                return BadRequest(new { message = "name is required" });

            // Normalize to a stable key format (derived from name/key)
            var attributeKey = source
                .Replace(' ', '_')
                .Replace('-', '_')
                .ToLowerInvariant();

            var dataType = string.IsNullOrWhiteSpace(req.DataType) ? "enum" : req.DataType!.Trim().ToLowerInvariant();

            var client = _supabase.GetServiceRoleClient();

            var productResp = await client
                .From<Product>()
                .Where(p => p.Id == productId && p.Deleted == false)
                .Limit(1)
                .Get();
            var product = productResp.Models.FirstOrDefault();
            if (product == null) return NotFound(new { message = "Product not found" });

            // Find existing attribute by key
            var existingResp = await client
                .From<AttributeModel>()
                .Filter("attribute_key", Supabase.Postgrest.Constants.Operator.Equals, attributeKey)
                .Limit(1)
                .Get();

            var attr = existingResp.Models.FirstOrDefault();
            var created = false;

            if (attr == null)
            {
                var insertResp = await client.From<AttributeModel>().Insert(new AttributeModel
                {
                    AttributeKey = attributeKey,
                    DataType = dataType,
                    Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description!.Trim()
                });
                attr = insertResp.Models.FirstOrDefault();
                if (attr == null) return StatusCode(500, new { message = "Failed to create attribute" });
                created = true;
            }

            // Ensure mapping exists in product_attribute
            var paTable = await _supabase.QueryTable<ProductAttribute>();
            var mapResp = await paTable
                .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, productId)
                .Filter("attribute_id", Supabase.Postgrest.Constants.Operator.Equals, attr.Id)
                .Get();

            var existingMap = mapResp.Models?.FirstOrDefault();
            if (existingMap != null)
            {
                existingMap.IsRequired = req.IsRequired;
                await client.From<ProductAttribute>().Update(existingMap);
            }
            else
            {
                await client.From<ProductAttribute>().Insert(new ProductAttribute
                {
                    ProductId = productId,
                    AttributeId = attr.Id,
                    IsRequired = req.IsRequired
                });
            }

            InvalidateProductCaches(productId, product.Slug);

            return Ok(new
            {
                created,
                attribute = new AdminAttributeCatalogItemDTO
                {
                    AttributeId = attr.Id,
                    AttributeKey = attr.AttributeKey,
                    DataType = attr.DataType,
                    Description = attr.Description
                }
            });
        }

        [HttpPut("{productId}/admin/product-attributes/{attributeId:int}")]
        [Authorize]
        public async Task<IActionResult> UpdateProductAttributeAdmin(int productId, int attributeId, [FromBody] AdminUpsertProductAttributeRequestDTO req)
        {
            var authResult = await EnsureAdminAsync();
            if (authResult != null) return authResult;
            if (attributeId <= 0) return BadRequest(new { message = "attributeId is required" });

            var client = _supabase.GetServiceRoleClient();
            var productResp = await client
                .From<Product>()
                .Where(p => p.Id == productId && p.Deleted == false)
                .Limit(1)
                .Get();
            var product = productResp.Models.FirstOrDefault();
            if (product == null) return NotFound(new { message = "Product not found" });

            var paTable = await _supabase.QueryTable<ProductAttribute>();
            var existingResp = await paTable
                .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, productId)
                .Filter("attribute_id", Supabase.Postgrest.Constants.Operator.Equals, attributeId)
                .Get();
            var existing = existingResp.Models?.FirstOrDefault();
            if (existing == null) return NotFound(new { message = "Product attribute not found" });

            existing.IsRequired = req.IsRequired;
            await client.From<ProductAttribute>().Update(existing);

            InvalidateProductCaches(productId, product.Slug);
            return Ok(new { success = true });
        }

        [HttpDelete("{productId}/admin/product-attributes/{attributeId:int}")]
        [Authorize]
        public async Task<IActionResult> RemoveProductAttributeAdmin(int productId, int attributeId)
        {
            var authResult = await EnsureAdminAsync();
            if (authResult != null) return authResult;
            if (attributeId <= 0) return BadRequest(new { message = "attributeId is required" });

            var client = _supabase.GetServiceRoleClient();
            var productResp = await client
                .From<Product>()
                .Where(p => p.Id == productId && p.Deleted == false)
                .Limit(1)
                .Get();
            var product = productResp.Models.FirstOrDefault();
            if (product == null) return NotFound(new { message = "Product not found" });

            // Composite key delete: use query delete.
            await client
                .From<ProductAttribute>()
                .Where(pa => pa.ProductId == productId && pa.AttributeId == attributeId)
                .Delete();

            InvalidateProductCaches(productId, product.Slug);
            return Ok(new { success = true });
        }

        [HttpPost("{productId}/admin/attributes/{attributeId:int}/enum-values")]
        [Authorize]
        public async Task<IActionResult> CreateAttributeEnumValueAdmin(int productId, int attributeId, [FromBody] AdminUpsertAttributeEnumValueRequestDTO req)
        {
            var authResult = await EnsureAdminAsync();
            if (authResult != null) return authResult;

            if (attributeId <= 0) return BadRequest(new { message = "attributeId is required" });
            if (string.IsNullOrWhiteSpace(req.EnumKey)) return BadRequest(new { message = "enumKey is required" });
            if (string.IsNullOrWhiteSpace(req.DisplayName)) return BadRequest(new { message = "displayName is required" });

            var client = _supabase.GetServiceRoleClient();
            var productResp = await client
                .From<Product>()
                .Where(p => p.Id == productId && p.Deleted == false)
                .Limit(1)
                .Get();
            var product = productResp.Models.FirstOrDefault();
            if (product == null) return NotFound(new { message = "Product not found" });

            // Ensure attribute exists
            var attrResp = await client
                .From<AttributeModel>()
                .Where(a => a.Id == attributeId)
                .Limit(1)
                .Get();
            var attr = attrResp.Models.FirstOrDefault();
            if (attr == null) return NotFound(new { message = "Attribute not found" });

            var row = new AttributeEnumValue
            {
                AttributeId = attributeId,
                EnumKey = req.EnumKey.Trim(),
                DisplayName = req.DisplayName.Trim(),
                SortOrder = req.SortOrder ?? 0,
                IsActive = req.IsActive ?? true
            };

            var insertResp = await client.From<AttributeEnumValue>().Insert(row);
            var inserted = insertResp.Models.FirstOrDefault();
            if (inserted == null) return StatusCode(500, new { message = "Failed to create enum value" });

            InvalidateProductCaches(productId, product.Slug);
            return Ok(new AdminAttributeEnumValueDTO
            {
                Id = inserted.Id,
                EnumKey = inserted.EnumKey,
                DisplayName = inserted.DisplayName,
                SortOrder = inserted.SortOrder,
                IsActive = inserted.IsActive
            });
        }

        [HttpPut("{productId}/admin/attributes/{attributeId:int}/enum-values/{enumValueId:int}")]
        [Authorize]
        public async Task<IActionResult> UpdateAttributeEnumValueAdmin(int productId, int attributeId, int enumValueId, [FromBody] AdminUpsertAttributeEnumValueRequestDTO req)
        {
            var authResult = await EnsureAdminAsync();
            if (authResult != null) return authResult;
            if (attributeId <= 0 || enumValueId <= 0) return BadRequest(new { message = "attributeId and enumValueId are required" });

            var client = _supabase.GetServiceRoleClient();
            var productResp = await client
                .From<Product>()
                .Where(p => p.Id == productId && p.Deleted == false)
                .Limit(1)
                .Get();
            var product = productResp.Models.FirstOrDefault();
            if (product == null) return NotFound(new { message = "Product not found" });

            var existingResp = await client
                .From<AttributeEnumValue>()
                .Where(ev => ev.Id == enumValueId && ev.AttributeId == attributeId)
                .Limit(1)
                .Get();
            var existing = existingResp.Models.FirstOrDefault();
            if (existing == null) return NotFound(new { message = "Enum value not found" });

            if (req.DisplayName != null) existing.DisplayName = req.DisplayName.Trim();
            if (req.SortOrder.HasValue) existing.SortOrder = req.SortOrder.Value;
            if (req.IsActive.HasValue) existing.IsActive = req.IsActive.Value;
            // Do not change EnumKey on update.

            await client.From<AttributeEnumValue>().Update(existing);

            InvalidateProductCaches(productId, product.Slug);
            return Ok(new AdminAttributeEnumValueDTO
            {
                Id = existing.Id,
                EnumKey = existing.EnumKey,
                DisplayName = existing.DisplayName,
                SortOrder = existing.SortOrder,
                IsActive = existing.IsActive
            });
        }

        public class AdminSetProductAttributeEnumEnabledRequestDTO
        {
            [System.Text.Json.Serialization.JsonPropertyName("isEnabled")]
            public bool IsEnabled { get; set; }
        }

        [HttpPut("{productId}/admin/product-attributes/{attributeId:int}/enum-values/{enumValueId:int}/enabled")]
        [Authorize]
        public async Task<IActionResult> SetProductAttributeEnumEnabledAdmin(
            int productId,
            int attributeId,
            int enumValueId,
            [FromBody] AdminSetProductAttributeEnumEnabledRequestDTO req)
        {
            var authResult = await EnsureAdminAsync();
            if (authResult != null) return authResult;
            if (productId <= 0 || attributeId <= 0 || enumValueId <= 0)
                return BadRequest(new { message = "Invalid ids" });

            var client = _supabase.GetServiceRoleClient();
            var productResp = await client
                .From<Product>()
                .Where(p => p.Id == productId && p.Deleted == false)
                .Limit(1)
                .Get();
            var product = productResp.Models.FirstOrDefault();
            if (product == null) return NotFound(new { message = "Product not found" });

            // Ensure the attribute is attached to the product.
            var paTable = await _supabase.QueryTable<ProductAttribute>();
            var paResp = await paTable
                .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, productId)
                .Filter("attribute_id", Supabase.Postgrest.Constants.Operator.Equals, attributeId)
                .Limit(1)
                .Get();
            var productAttr = paResp.Models?.FirstOrDefault();
            if (productAttr == null) return NotFound(new { message = "Product attribute not found" });

            // Ensure enum exists and belongs to the attribute.
            var enumResp = await client
                .From<AttributeEnumValue>()
                .Where(ev => ev.Id == enumValueId && ev.AttributeId == attributeId)
                .Limit(1)
                .Get();
            var evRow = enumResp.Models.FirstOrDefault();
            if (evRow == null) return NotFound(new { message = "Enum value not found" });

            if (req.IsEnabled)
            {
                // Enabled is the default state: remove any disabled row.
                await client
                    .From<ProductAttributeEnumDisabled>()
                    .Where(r => r.ProductId == productId && r.EnumValueId == enumValueId)
                    .Delete();
            }
            else
            {
                // Insert disabled row if not exists.
                var disabledTable = await _supabase.QueryTable<ProductAttributeEnumDisabled>();
                var existingDisabledResp = await disabledTable
                    .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, productId)
                    .Filter("enum_value_id", Supabase.Postgrest.Constants.Operator.Equals, enumValueId)
                    .Limit(1)
                    .Get();
                var existingDisabled = existingDisabledResp.Models?.FirstOrDefault();
                if (existingDisabled == null)
                {
                    await client.From<ProductAttributeEnumDisabled>().Insert(new ProductAttributeEnumDisabled
                    {
                        ProductId = productId,
                        AttributeId = attributeId,
                        EnumValueId = enumValueId,
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }

            InvalidateProductCaches(productId, product.Slug);
            return Ok(new { success = true, isEnabled = req.IsEnabled });
        }

        [HttpPut("{productId}/admin")]
        [Authorize]
        public async Task<IActionResult> UpdateProductAdmin(int productId, [FromBody] AdminUpdateProductRequestDTO req)
        {
            var authResult = await EnsureAdminAsync();
            if (authResult != null) return authResult;

            if (req.Name == null && req.Msrp == null && req.Description == null && req.BrandId == null)
                return BadRequest(new { message = "No fields provided" });

            var client = _supabase.GetServiceRoleClient();
            var existingResp = await client
                .From<Product>()
                .Where(p => p.Id == productId && p.Deleted == false)
                .Limit(1)
                .Get();
            var existing = existingResp.Models.FirstOrDefault();
            if (existing == null) return NotFound(new { message = "Product not found" });

            // Important: Product includes navigation properties (Brand/User/Deal).
            // Sending those in an update causes PostgREST to look for columns that don't exist.
            // Use a columns-only model for admin updates.
            // Preserve slug on update (slug is not editable here). If we send null, PostgREST will clear it.
            var updateRow = new ProductAdminUpdateRow { Id = existing.Id, Slug = existing.Slug };
            if (req.Name != null) updateRow.Name = req.Name.Trim();
            if (req.Msrp != null) updateRow.MSRP = req.Msrp;
            if (req.Description != null) updateRow.Description = req.Description;
            if (req.BrandId != null) updateRow.BrandId = req.BrandId;

            await client.From<ProductAdminUpdateRow>().Update(updateRow);

            var expectedName = req.Name != null ? req.Name.Trim() : existing.Name;
            var expectedMsrp = req.Msrp ?? existing.MSRP;
            var expectedDescription = req.Description ?? existing.Description;
            var expectedBrandId = req.BrandId ?? existing.BrandId;

            // Read-after-write so the API response reflects what actually persisted.
            var verifyResp = await client
                .From<ProductAdminUpdateRow>()
                .Where(p => p.Id == productId)
                .Limit(1)
                .Get();
            var persisted = verifyResp.Models.FirstOrDefault();
            if (persisted == null)
                return StatusCode(500, new { message = "Update succeeded but could not re-load product" });

            // If the write didn't actually persist (e.g., missing service role key / RLS), fail loudly.
            // Otherwise the UI can look updated until a refresh shows the old data.
            var didPersist =
                string.Equals(persisted.Name, expectedName, StringComparison.Ordinal) &&
                persisted.MSRP == expectedMsrp &&
                string.Equals(persisted.Description, expectedDescription, StringComparison.Ordinal) &&
                persisted.BrandId == expectedBrandId;

            if (!didPersist)
            {
                return StatusCode(500, new
                {
                    message = "Product update did not persist. Check Supabase service role configuration / RLS."
                });
            }

            InvalidateProductCaches(productId, persisted.Slug ?? existing.Slug);

            return Ok(new
            {
                id = persisted.Id,
                name = persisted.Name,
                msrp = persisted.MSRP,
                description = persisted.Description
            });
        }

        [HttpPost("{productId}/admin/variants")]
        [Authorize]
        public async Task<IActionResult> CreateVariantAdmin(int productId, [FromBody] AdminUpsertVariantRequestDTO req)
        {
            var authResult = await EnsureAdminAsync();
            if (authResult != null) return authResult;

            var client = _supabase.GetServiceRoleClient();
            var productResp = await client
                .From<Product>()
                .Where(p => p.Id == productId && p.Deleted == false)
                .Limit(1)
                .Get();
            var product = productResp.Models.FirstOrDefault();
            if (product == null) return NotFound(new { message = "Product not found" });

            var variant = new ProductVariant
            {
                ProductId = productId,
                VariantName = req.VariantName?.Trim(),
                UnitCount = req.UnitCount,
                UnitType = string.IsNullOrWhiteSpace(req.UnitType) ? null : req.UnitType.Trim(),
                DisplayName = (req.DisplayName ?? "").Trim(),
                NormalizedTitle = (req.NormalizedTitle ?? "").Trim(),
                IsDefault = req.IsDefault ?? false,
                IsActive = req.IsActive ?? true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            if (string.IsNullOrWhiteSpace(variant.DisplayName))
                variant.DisplayName = BuildVariantDisplayName(variant);
            if (string.IsNullOrWhiteSpace(variant.NormalizedTitle))
                variant.NormalizedTitle = NormalizeTitle(variant.DisplayName);

            if (variant.IsDefault)
                await ClearDefaultVariantAsync(client, productId);

            var insertResp = await client.From<ProductVariant>().Insert(variant);
            var inserted = insertResp.Models.FirstOrDefault();
            if (inserted == null) return StatusCode(500, new { message = "Failed to create variant" });

            InvalidateProductCaches(productId, product.Slug);

            return Ok(new AdminProductVariantDTO
            {
                Id = inserted.Id,
                ProductId = inserted.ProductId,
                VariantName = inserted.VariantName,
                UnitCount = inserted.UnitCount,
                UnitType = inserted.UnitType,
                DisplayName = inserted.DisplayName,
                NormalizedTitle = inserted.NormalizedTitle,
                IsDefault = inserted.IsDefault,
                IsActive = inserted.IsActive
            });
        }

        [HttpPut("{productId}/admin/variants/{variantId:long}")]
        [Authorize]
        public async Task<IActionResult> UpdateVariantAdmin(int productId, long variantId, [FromBody] AdminUpsertVariantRequestDTO req)
        {
            var authResult = await EnsureAdminAsync();
            if (authResult != null) return authResult;

            var client = _supabase.GetServiceRoleClient();
            var productResp = await client
                .From<Product>()
                .Where(p => p.Id == productId && p.Deleted == false)
                .Limit(1)
                .Get();
            var product = productResp.Models.FirstOrDefault();
            if (product == null) return NotFound(new { message = "Product not found" });

            var existingResp = await client
                .From<ProductVariant>()
                .Where(v => v.Id == variantId && v.ProductId == productId)
                .Limit(1)
                .Get();
            var existing = existingResp.Models.FirstOrDefault();
            if (existing == null) return NotFound(new { message = "Variant not found" });

            existing.VariantName = req.VariantName?.Trim();
            existing.UnitCount = req.UnitCount;
            existing.UnitType = string.IsNullOrWhiteSpace(req.UnitType) ? null : req.UnitType.Trim();

            if (req.DisplayName != null) existing.DisplayName = req.DisplayName.Trim();
            if (req.NormalizedTitle != null) existing.NormalizedTitle = req.NormalizedTitle.Trim();
            if (req.IsActive.HasValue) existing.IsActive = req.IsActive.Value;
            if (req.IsDefault.HasValue) existing.IsDefault = req.IsDefault.Value;
            existing.UpdatedAt = DateTime.UtcNow;

            if (string.IsNullOrWhiteSpace(existing.DisplayName))
                existing.DisplayName = BuildVariantDisplayName(existing);
            if (string.IsNullOrWhiteSpace(existing.NormalizedTitle))
                existing.NormalizedTitle = NormalizeTitle(existing.DisplayName);

            if (existing.IsDefault)
                await ClearDefaultVariantAsync(client, productId, exceptVariantId: existing.Id);

            await client.From<ProductVariant>().Update(existing);

            InvalidateProductCaches(productId, product.Slug);

            return Ok(new AdminProductVariantDTO
            {
                Id = existing.Id,
                ProductId = existing.ProductId,
                VariantName = existing.VariantName,
                UnitCount = existing.UnitCount,
                UnitType = existing.UnitType,
                DisplayName = existing.DisplayName,
                NormalizedTitle = existing.NormalizedTitle,
                IsDefault = existing.IsDefault,
                IsActive = existing.IsActive
            });
        }

        [HttpPost("{productId}/admin/variants/{variantId:long}/set-default")]
        [Authorize]
        public async Task<IActionResult> SetDefaultVariantAdmin(int productId, long variantId)
        {
            var authResult = await EnsureAdminAsync();
            if (authResult != null) return authResult;

            var client = _supabase.GetServiceRoleClient();
            var productResp = await client
                .From<Product>()
                .Where(p => p.Id == productId && p.Deleted == false)
                .Limit(1)
                .Get();
            var product = productResp.Models.FirstOrDefault();
            if (product == null) return NotFound(new { message = "Product not found" });

            var existingResp = await client
                .From<ProductVariant>()
                .Where(v => v.Id == variantId && v.ProductId == productId)
                .Limit(1)
                .Get();
            var existing = existingResp.Models.FirstOrDefault();
            if (existing == null) return NotFound(new { message = "Variant not found" });

            await ClearDefaultVariantAsync(client, productId, exceptVariantId: existing.Id);
            existing.IsDefault = true;
            existing.UpdatedAt = DateTime.UtcNow;
            await client.From<ProductVariant>().Update(existing);

            InvalidateProductCaches(productId, product.Slug);

            return Ok(new { success = true });
        }

        private static string NormalizeTitle(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var lower = value.Trim().ToLowerInvariant();
            var chars = lower
                .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
                .ToArray();
            var cleaned = new string(chars);
            while (cleaned.Contains("  ")) cleaned = cleaned.Replace("  ", " ");
            return cleaned.Trim();
        }

        private static string BuildVariantDisplayName(ProductVariant v)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(v.VariantName))
                parts.Add(v.VariantName.Trim());
            if (v.UnitCount.HasValue && v.UnitCount.Value > 0)
            {
                if (!string.IsNullOrWhiteSpace(v.UnitType))
                    parts.Add($"{v.UnitCount.Value} {v.UnitType.Trim()}");
                else
                    parts.Add(v.UnitCount.Value.ToString());
            }
            return parts.Count > 0 ? string.Join(" ", parts) : "Variant";
        }

        private static async Task ClearDefaultVariantAsync(Supabase.Client client, int productId, long? exceptVariantId = null)
        {
            var variantsResp = await client
                .From<ProductVariant>()
                .Where(v => v.ProductId == productId)
                .Get();

            var toClear = variantsResp.Models
                .Where(v => v.IsDefault && (!exceptVariantId.HasValue || v.Id != exceptVariantId.Value))
                .ToList();

            foreach (var v in toClear)
            {
                v.IsDefault = false;
                v.UpdatedAt = DateTime.UtcNow;
                await client.From<ProductVariant>().Update(v);
            }
        }

    }
}