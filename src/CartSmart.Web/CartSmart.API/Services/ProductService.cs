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
    private static readonly TimeSpan PriceHistoryTtl = TimeSpan.FromMinutes(10);

    [Table("product")]
    private class ProductIdBrandRow : BaseModel
    {
        [PrimaryKey("id")]
        public int Id { get; set; }

        [Column("brand_id")]
        public int? BrandId { get; set; }

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
            .Where(id => id.HasValue && id.Value > 0)
            .Select(id => id!.Value)
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
            Rating = product.Rating,
            CountEnabled = product.CountEnabled,
            DefaultCount = product.DefaultCount
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

    public async Task<ProductPriceHistoryDTO> GetProductPriceHistoryAsync(
        int productId,
        int? storeId = null,
        int? dealTypeId = null,
        int? conditionId = null,
        List<ProductAttributeFilterDTO>? attributeFilters = null)
    {
        var normalizedAttributeFilters = (attributeFilters ?? new List<ProductAttributeFilterDTO>())
            .Where(f => f != null && f.AttributeId > 0 && f.EnumValueIds != null && f.EnumValueIds.Count > 0)
            .Select(f => new ProductAttributeFilterDTO
            {
                AttributeId = f.AttributeId,
                EnumValueIds = f.EnumValueIds.Where(v => v > 0).Distinct().OrderBy(v => v).ToList()
            })
            .Where(f => f.EnumValueIds.Count > 0)
            .OrderBy(f => f.AttributeId)
            .ToList();

        var attributeFilterKey = normalizedAttributeFilters.Count == 0
            ? "none"
            : string.Join(";", normalizedAttributeFilters.Select(f => $"{f.AttributeId}:{string.Join(",", f.EnumValueIds)}"));

        var cacheKey = $"product:price-history:v3:{productId}:store:{storeId?.ToString() ?? "all"}:dealType:{dealTypeId?.ToString() ?? "all"}:condition:{conditionId?.ToString() ?? "all"}:attrs:{attributeFilterKey}";
        if (_cache.TryGetValue(cacheKey, out ProductPriceHistoryDTO cached)) return cached;

        var historyStatusIds = new HashSet<int> { 2, 6, 7, 8 };
        const int currentStatusId = 2;

        var client = _supabase.GetServiceRoleClient();
        var dealProductsQuery = client
            .From<DealProduct>()
            .Select("id, product_id, price, deleted, condition_id, deal_status_id, deal_id, product_variant_id, item_count")
            .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, productId.ToString());

        if (conditionId.HasValue)
            dealProductsQuery = dealProductsQuery.Filter("condition_id", Supabase.Postgrest.Constants.Operator.Equals, conditionId.Value.ToString());

        var dealProductsResp = await dealProductsQuery.Get();

        var dealProducts = dealProductsResp.Models ?? new List<DealProduct>();

        if (normalizedAttributeFilters.Count > 0)
        {
            var attributeIdObjects = normalizedAttributeFilters
                .Select(f => (object)f.AttributeId)
                .Distinct()
                .ToList();

            var variantAttrResp = await client
                .From<ProductVariantAttribute>()
                .Select("product_variant_id,attribute_id,enum_value_id")
                .Filter("attribute_id", Supabase.Postgrest.Constants.Operator.In, attributeIdObjects)
                .Get();

            var variantRows = variantAttrResp.Models ?? new List<ProductVariantAttribute>();
            var rowsByVariant = variantRows
                .Where(row => row.EnumValueId.HasValue && row.EnumValueId.Value > 0)
                .GroupBy(row => row.ProductVariantId)
                .ToDictionary(group => group.Key, group => group.ToList());

            var matchingVariantIds = rowsByVariant
                .Where(kvp =>
                {
                    var rows = kvp.Value;
                    return normalizedAttributeFilters.All(filter =>
                        rows.Any(row => row.AttributeId == filter.AttributeId && row.EnumValueId.HasValue && filter.EnumValueIds.Contains(row.EnumValueId.Value)));
                })
                .Select(kvp => kvp.Key)
                .ToHashSet();

            dealProducts = dealProducts
                .Where(dp => dp.ProductVariantId.HasValue && matchingVariantIds.Contains(dp.ProductVariantId.Value))
                .ToList();
        }

        if (storeId.HasValue || dealTypeId.HasValue)
        {
            var dealIds = dealProducts
                .Select(dp => dp.DealId)
                .Where(id => id > 0)
                .Distinct()
                .Cast<object>()
                .ToList();

            if (dealIds.Count == 0)
            {
                var empty = new ProductPriceHistoryDTO();
                _cache.Set(cacheKey, empty, PriceHistoryTtl);
                return empty;
            }

            var dealsResp = await client
                .From<Deal>()
                .Select("id, store_id, deal_type_id")
                .Filter("id", Supabase.Postgrest.Constants.Operator.In, dealIds)
                .Get();

            var matchingDealIds = (dealsResp.Models ?? new List<Deal>())
                .Where(d => (!storeId.HasValue || d.StoreId == storeId.Value)
                    && (!dealTypeId.HasValue || d.DealTypeId == dealTypeId.Value))
                .Select(d => d.Id)
                .ToHashSet();

            dealProducts = dealProducts
                .Where(dp => matchingDealIds.Contains(dp.DealId))
                .ToList();
        }

        // History series includes approved + review/hold-like statuses.
        dealProducts = dealProducts
            .Where(dp => historyStatusIds.Contains(dp.DealStatusId))
            .ToList();

        // Include all deal_products (even inactive) for mapping history buckets,
        // but only use active, non-deleted ones for today's current price.
        var bucketByDealProductId = dealProducts
            .Select(dp => new { DealProductId = dp.Id, Bucket = MapPriceHistoryBucket(dp.ConditionId) })
            .Where(x => !string.IsNullOrWhiteSpace(x.Bucket))
            .ToDictionary(x => x.DealProductId, x => x.Bucket!);

        // Per-item count for each deal_product — used to normalize prices to per-item
        var itemCountById = dealProducts
            .ToDictionary(dp => dp.Id, dp => Math.Max(dp.ItemCount, 1));

        if (bucketByDealProductId.Count == 0)
        {
            var empty = new ProductPriceHistoryDTO();
            _cache.Set(cacheKey, empty, PriceHistoryTtl);
            return empty;
        }

        var historyResp = await client
            .From<DealProductPriceHistory>()
            .Select("deal_product_id, price, changed_at")
            .Filter("deal_product_id", Supabase.Postgrest.Constants.Operator.In, bucketByDealProductId.Keys.Cast<object>().ToList())
            .Order("changed_at", Supabase.Postgrest.Constants.Ordering.Ascending)
            .Get();

        var histories = historyResp.Models ?? new List<DealProductPriceHistory>();
        var dailyPriceByBucket = new Dictionary<string, SortedDictionary<DateTime, decimal>>(StringComparer.OrdinalIgnoreCase);

        // Carry forward last known per-item price for each deal_product so daily minima are
        // computed across all tracked rows, not only those that changed on that day.
        var latestByBucketDealProduct = new Dictionary<string, Dictionary<int, decimal>>(StringComparer.OrdinalIgnoreCase);

        var normalizedEvents = histories
            .Where(h => h.Price > 0 && bucketByDealProductId.ContainsKey(h.DealProductId))
            .Select(h =>
            {
                var itemCount = itemCountById.TryGetValue(h.DealProductId, out var ic) ? Math.Max(ic, 1) : 1;
                return new
                {
                    DealProductId = h.DealProductId,
                    Bucket = bucketByDealProductId[h.DealProductId],
                    ChangedAt = DateTime.SpecifyKind(h.ChangedAt, DateTimeKind.Utc),
                    NormalizedPrice = h.Price / itemCount
                };
            })
            .OrderBy(h => h.ChangedAt)
            .ToList();

        foreach (var dateGroup in normalizedEvents.GroupBy(e => e.ChangedAt.Date))
        {
            foreach (var ev in dateGroup)
            {
                if (!latestByBucketDealProduct.TryGetValue(ev.Bucket, out var latestForBucket))
                {
                    latestForBucket = new Dictionary<int, decimal>();
                    latestByBucketDealProduct[ev.Bucket] = latestForBucket;
                }

                latestForBucket[ev.DealProductId] = ev.NormalizedPrice;
            }

            foreach (var bucketEntry in latestByBucketDealProduct)
            {
                var bucket = bucketEntry.Key;
                var latestForBucket = bucketEntry.Value;
                if (latestForBucket.Count == 0) continue;

                if (!dailyPriceByBucket.TryGetValue(bucket, out var bucketPoints))
                {
                    bucketPoints = new SortedDictionary<DateTime, decimal>();
                    dailyPriceByBucket[bucket] = bucketPoints;
                }

                bucketPoints[dateGroup.Key] = latestForBucket.Values.Min();
            }
        }

        var today = DateTime.UtcNow.Date;
        foreach (var currentGroup in dealProducts
            .Where(dp => !dp.Deleted && dp.DealStatusId == currentStatusId && dp.Price > 0)
            .Select(dp => new { DealProduct = dp, Bucket = MapPriceHistoryBucket(dp.ConditionId) })
            .Where(x => !string.IsNullOrWhiteSpace(x.Bucket))
            .GroupBy(x => x.Bucket!, StringComparer.OrdinalIgnoreCase))
        {
            var currentLow = currentGroup.Min(x => x.DealProduct.Price / Math.Max(x.DealProduct.ItemCount, 1));
            if (!dailyPriceByBucket.TryGetValue(currentGroup.Key, out var bucketPoints))
            {
                bucketPoints = new SortedDictionary<DateTime, decimal>();
                dailyPriceByBucket[currentGroup.Key] = bucketPoints;
            }

            // Today's point should reflect the live active deals aggregate, not whichever history row happened to exist today.
            bucketPoints[today] = currentLow;
        }

        var dto = new ProductPriceHistoryDTO();
        foreach (var bucket in new[] { "new", "used" })
        {
            if (!dailyPriceByBucket.TryGetValue(bucket, out var bucketPoints) || bucketPoints.Count == 0)
                continue;

            var trimmedPoints = bucketPoints
                .TakeLast(120)
                .Select(point => new ProductPriceHistoryPointDTO
                {
                    Date = point.Key,
                    Price = decimal.Round(point.Value, 2)
                })
                .ToList();

            dto.Series.Add(new ProductPriceHistorySeriesDTO
            {
                Key = bucket,
                Label = bucket.Equals("new", StringComparison.OrdinalIgnoreCase) ? "New" : "Used",
                CurrentPrice = trimmedPoints.Last().Price,
                LowestPrice = trimmedPoints.Min(point => point.Price),
                Points = trimmedPoints
            });
        }

        if (dto.Series.Count > 0)
        {
            dto.StartDate = dto.Series.SelectMany(series => series.Points).Min(point => point.Date);
            dto.EndDate = dto.Series.SelectMany(series => series.Points).Max(point => point.Date);
        }

        _cache.Set(cacheKey, dto, PriceHistoryTtl);
        return dto;
    }

    private static string? MapPriceHistoryBucket(int? conditionId)
    {
        return conditionId switch
        {
            1 => "new",
            2 => "used",
            3 => "used",
            _ => null
        };
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