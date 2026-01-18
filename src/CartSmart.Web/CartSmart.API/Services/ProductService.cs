using CartSmart.API.Models;
using Supabase.Postgrest.Models;
using CartSmart.API.Models.DTOs;
using Microsoft.Extensions.Caching.Memory;
using AttributeModel = CartSmart.API.Models.Attribute;
using Supabase.Postgrest.Attributes;

namespace CartSmart.API.Services;

public class ProductService : IProductService
{
    private readonly ISupabaseService _supabase;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan BestDealsTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ProductTtl = TimeSpan.FromMinutes(30);

    [Table("product")]
    private class ProductIdBrandRow : BaseModel
    {
        [PrimaryKey("id")]
        public int Id { get; set; }

        [Column("brand_id")]
        public int BrandId { get; set; }

        [Column("product_type_id")]
        public int ProductTypeId { get; set; }

        [Column("deleted")]
        public bool Deleted { get; set; }
    }

    public ProductService(ISupabaseService supabase, IMemoryCache cache)
    {
        _supabase = supabase;
        _cache = cache;
    }

    public async Task<IEnumerable<Product>> GetAllProductsAsync()
    {
        return await _supabase.GetAllAsync<Product>();
    }

    public async Task<ProductDTO?> GetProductByIdAsync(int id)
    {
        var cacheKey = $"product:id:{id}";
        if (_cache.TryGetValue(cacheKey, out ProductDTO cached)) return cached;

        var products = await _supabase.GetAllAsync<Product>();
        var product = products.FirstOrDefault(p => p.Id == id);
        if (product == null) return null;

        var dto = new ProductDTO
        {
            Id = product.Id,
            Name = product.Name,
            Description = product.Description,
            Slug = product.Slug,
            MSRP = product.MSRP,
            LowPrice = product.LowPrice,
            BrandId = product.BrandId,
            UserId = product.UserId,
            DealId = product.DealId
        };
        _cache.Set(cacheKey, dto, ProductTtl);
        if (!string.IsNullOrWhiteSpace(dto.Slug))
            _cache.Set($"product:slug:{dto.Slug}", dto, ProductTtl);
        return dto;
    }

    public async Task<IEnumerable<DealDisplayDTO>> GetBestProductDealsAsync()
    {
        const string cacheKey = "bestDeals";
        if (_cache.TryGetValue(cacheKey, out List<DealDisplayDTO> cached)) return cached;

        var client = _supabase.GetClient();
        var bestDeals = await client.Rpc<List<DealDisplayDTO>>("f_best_deals", new { });
        // Cache even if empty to avoid hammering
        _cache.Set(cacheKey, bestDeals, BestDealsTtl);
        return bestDeals;
    }

    public async Task<IEnumerable<CategoryProductCardDTO>> GetCategoryProductsAsync(string productType)
    {
        return await GetCategoryProductsAsync(productType, null);
    }

    public async Task<IEnumerable<CategoryProductCardDTO>> GetCategoryProductsAsync(string productType, int? brandId = null)
    {
        if (string.IsNullOrWhiteSpace(productType))
            return new List<CategoryProductCardDTO>();

        // Use service role for product catalog reads to avoid RLS surprises.
        var client = _supabase.GetServiceRoleClient();

        var matchedTypeId = await ResolveProductTypeIdAsync(client, productType);
        if (!matchedTypeId.HasValue)
            return new List<CategoryProductCardDTO>();

        var cacheKey = brandId.HasValue
            ? $"categoryProducts:productTypeId:{matchedTypeId.Value}:brandId:{brandId.Value}"
            : $"categoryProducts:productTypeId:{matchedTypeId.Value}";

        if (_cache.TryGetValue(cacheKey, out List<CategoryProductCardDTO> cached))
            return cached;

        var results = await client
            .Rpc<List<CategoryProductCardDTO>>("f_best_deals", new { p_product_type_id = matchedTypeId.Value });

        var rows = results ?? new List<CategoryProductCardDTO>();

        if (brandId.HasValue)
        {
            // Filter by brand using the product table (no SQL changes required).
            var productResp = await client
                .From<ProductIdBrandRow>()
                .Select("id")
                .Filter("product_type_id", Supabase.Postgrest.Constants.Operator.Equals, matchedTypeId.Value.ToString())
                .Filter("brand_id", Supabase.Postgrest.Constants.Operator.Equals, brandId.Value.ToString())
                .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
                .Get();

            var allowed = new HashSet<int>((productResp.Models ?? new List<ProductIdBrandRow>()).Select(p => p.Id));
            rows = rows.Where(r => allowed.Contains((int)r.product_id)).ToList();
        }

        _cache.Set(cacheKey, rows, TimeSpan.FromMinutes(15));
        return rows;
    }

    public async Task<IEnumerable<BrandDTO>> GetCategoryBrandsAsync(string productType)
    {
        if (string.IsNullOrWhiteSpace(productType))
            return new List<BrandDTO>();

        var client = _supabase.GetServiceRoleClient();
        var matchedTypeId = await ResolveProductTypeIdAsync(client, productType);
        if (!matchedTypeId.HasValue)
            return new List<BrandDTO>();

        var cacheKey = $"categoryBrands:productTypeId:{matchedTypeId.Value}";
        if (_cache.TryGetValue(cacheKey, out List<BrandDTO> cached))
            return cached;

        var productsResp = await client
            .From<ProductIdBrandRow>()
            .Select("id, brand_id")
            .Filter("product_type_id", Supabase.Postgrest.Constants.Operator.Equals, matchedTypeId.Value.ToString())
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Get();

        var brandIds = (productsResp.Models ?? new List<ProductIdBrandRow>())
            .Select(p => p.BrandId)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        if (brandIds.Count == 0)
        {
            var empty = new List<BrandDTO>();
            _cache.Set(cacheKey, empty, TimeSpan.FromMinutes(30));
            return empty;
        }

        var brandIdObjects = brandIds.Cast<object>().ToList();
        var brandsResp = await client
            .From<Brand>()
            .Select("id, name")
            .Filter("id", Supabase.Postgrest.Constants.Operator.In, brandIdObjects)
            .Get();

        var brands = (brandsResp.Models ?? new List<Brand>())
            .Where(b => !string.IsNullOrWhiteSpace(b.Name))
            .OrderBy(b => b.Name)
            .Select(b => new BrandDTO { Id = b.Id, Name = b.Name })
            .ToList();

        _cache.Set(cacheKey, brands, TimeSpan.FromMinutes(30));
        return brands;
    }

    private static string NormalizeProductType(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var s = input.Trim();
        s = s.Replace('-', ' ');
        s = string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return s.ToLowerInvariant();
    }

    private static async Task<int?> ResolveProductTypeIdAsync(Supabase.Client client, string productType)
    {
        var normalizedName = NormalizeProductType(productType);
        var needleRaw = productType.Trim().ToLowerInvariant();
        var needleSlug = needleRaw.Replace(' ', '-');
        needleSlug = string.Join('-', needleSlug.Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(normalizedName) && string.IsNullOrWhiteSpace(needleSlug))
            return null;

        var productTypesResp = await client
            .From<ProductType>()
            .Select("id, name, slug")
            .Get();

        var productTypes = productTypesResp.Models ?? new List<ProductType>();
        var matched = productTypes.FirstOrDefault(pt =>
        {
            var ptSlug = (pt.Slug ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(ptSlug))
            {
                if (ptSlug == needleRaw) return true;
                if (ptSlug == needleSlug) return true;
            }
            return NormalizeProductType(pt.Name) == normalizedName;
        });

        return matched?.Id;
    }

     public async Task<ProductDTO?> GetProductBySlugAsync(string? slug)
     {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        var cacheKey = $"product:slug:{slug}";
        if (_cache.TryGetValue(cacheKey, out ProductDTO cached)) return cached;

        var productsTable = await _supabase.QueryTable<Product>();
        var products = await productsTable
            .Select("*, brand!inner(name)")
            .Get();

        var product = products.Models.FirstOrDefault(p => p.Slug == slug);
        if (product == null) return null;

        var dto = new ProductDTO
        {
            Id = product.Id,
            Name = product.Name,
            Description = product.Description,
            Slug = product.Slug,
            MSRP = product.MSRP,
            LowPrice = product.LowPrice,
            BrandId = product.BrandId,
            UserId = product.UserId,
            DealId = product.DealId,
            ImageUrl = product.ImageUrl,
            BrandName = product.Brand.Name,
            Rating = product.Rating
        };
        _cache.Set(cacheKey, dto, ProductTtl);
        _cache.Set($"product:id:{dto.Id}", dto, ProductTtl);
        return dto;
     }

    public async Task<Product> CreateProductAsync(Product product)
    {
        return await _supabase.InsertAsync(product);
    }

    public async Task<Product?> UpdateProductAsync(int id, Product product)
    {
        if (id != product.Id) return null;
        return await _supabase.UpdateAsync(product);
    }

    public async Task<bool> DeleteProductAsync(int id)
    {
        await _supabase.DeleteAsync<Product>(id);
        return true;
    }

    public async Task<IEnumerable<object>> GetProductRatingsAsync(int productId)
    {
        // Join ProductRating with ReviewSite for the given product
        var ratings = await _supabase.GetAllAsync<ProductRating>();
        var reviewSites = await _supabase.GetAllAsync<ReviewSite>();
        var filtered = ratings.Where(r => r.ProductId == productId).Select(r => new {
            source = reviewSites.FirstOrDefault(s => s.Id == r.ReviewSiteId)?.Name ?? r.Title ?? "Unknown",
            url = r.URL,
            rating = r.Rating
        });
        return filtered;
    }

    public async Task<VariantFilterOptionsDTO> GetVariantFilterOptionsAsync(int productId)
    {
        // 1) Determine which attributes apply for this specific product
        var paTable = await _supabase.QueryTable<ProductAttribute>();
        var paResp = await paTable
            .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, productId)
            .Get();

        var attributeIds = (paResp.Models ?? new List<ProductAttribute>())
            .Select(x => x.AttributeId)
            .Distinct()
            .ToList();

        if (attributeIds.Count == 0)
            return new VariantFilterOptionsDTO();

        // 2) Load attribute definitions and enum values
    // Use service-role to avoid RLS returning empty results for public navigation.
    var client = _supabase.GetServiceRoleClient();
        var attributeIdObjects = attributeIds.Cast<object>().ToArray();

        var attributesResp = await client
            .From<AttributeModel>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.In, attributeIdObjects)
            .Get();

        var enumResp = await client
            .From<AttributeEnumValue>()
            .Filter("attribute_id", Supabase.Postgrest.Constants.Operator.In, attributeIdObjects)
            .Filter("is_active", Supabase.Postgrest.Constants.Operator.Equals, "true")
            .Get();

        var attributes = attributesResp.Models ?? new List<AttributeModel>();
        var enumValues = enumResp.Models ?? new List<AttributeEnumValue>();

        // 2b) Remove enums disabled for this product (defaults to enabled when no row exists)
        var disabledTable = await _supabase.QueryTable<ProductAttributeEnumDisabled>();
        var disabledResp = await disabledTable
            .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, productId)
            .Filter("attribute_id", Supabase.Postgrest.Constants.Operator.In, attributeIdObjects)
            .Get();
        var disabledEnumIds = (disabledResp.Models ?? new List<ProductAttributeEnumDisabled>())
            .Select(x => x.EnumValueId)
            .ToHashSet();

        if (disabledEnumIds.Count > 0)
        {
            enumValues = enumValues
                .Where(ev => !disabledEnumIds.Contains(ev.Id))
                .ToList();
        }

        // 3) Build lightweight variant->enum mapping for variants belonging to this product
        // (We fetch IDs only; we do NOT return product_variant rows.)
        var variantsTable = await _supabase.QueryTable<ProductVariant>();
        var variantsResp = await variantsTable
            .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, productId)
            .Filter("is_active", Supabase.Postgrest.Constants.Operator.Equals, "true")
            .Select("id")
            .Get();

        var variantIds = (variantsResp.Models ?? new List<ProductVariant>())
            .Select(v => v.Id)
            .Distinct()
            .ToList();

        var dto = new VariantFilterOptionsDTO();

        dto.Attributes = attributes
            .OrderBy(a => a.AttributeKey)
            .Select(a => new VariantFilterAttributeDTO
            {
                AttributeId = a.Id,
                AttributeKey = a.AttributeKey,
                Label = a.AttributeKey,
                DataType = a.DataType,
                Description = a.Description,
                Options = enumValues
                    .Where(ev => ev.AttributeId == a.Id)
                    .OrderBy(ev => ev.SortOrder)
                    .ThenBy(ev => ev.DisplayName)
                    .Select(ev => new VariantFilterEnumOptionDTO
                    {
                        Id = ev.Id,
                        EnumKey = ev.EnumKey,
                        DisplayName = ev.DisplayName,
                        SortOrder = ev.SortOrder
                    })
                    .ToList()
            })
            .Where(a => a.Options.Count > 0)
            .ToList();

        if (variantIds.Count == 0 || dto.Attributes.Count == 0)
            return dto;

        var variantIdObjects = variantIds.Cast<object>().ToArray();
        var pvaResp = await client
            .From<ProductVariantAttribute>()
            .Filter("product_variant_id", Supabase.Postgrest.Constants.Operator.In, variantIdObjects)
            .Filter("attribute_id", Supabase.Postgrest.Constants.Operator.In, attributeIdObjects)
            .Get();

        dto.VariantAttributeValues = (pvaResp.Models ?? new List<ProductVariantAttribute>())
            .Select(x => new VariantAttributeValueDTO
            {
                ProductVariantId = x.ProductVariantId,
                AttributeId = x.AttributeId,
                EnumValueId = x.EnumValueId
            })
            .ToList();

        return dto;
    }
}