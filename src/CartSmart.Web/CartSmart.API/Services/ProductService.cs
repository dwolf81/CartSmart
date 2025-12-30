using CartSmart.API.Models;
using Supabase.Postgrest.Models;
using CartSmart.API.Models.DTOs;
using Microsoft.Extensions.Caching.Memory;
using AttributeModel = CartSmart.API.Models.Attribute;

namespace CartSmart.API.Services;

public class ProductService : IProductService
{
    private readonly ISupabaseService _supabase;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan BestDealsTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ProductTtl = TimeSpan.FromMinutes(30);

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
        var client = _supabase.GetClient();
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