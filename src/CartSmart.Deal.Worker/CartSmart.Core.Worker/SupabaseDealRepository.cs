using System.Text;
using CartSmart.API.Models;
using Supabase;
using Supabase.Postgrest.Models;
using Supabase.Postgrest.Responses;


namespace CartSmart.Core.Worker;

public class SupabaseDealRepository : IDealRepository, IStopWordsProvider
{
    private readonly Client _client;
    private readonly TimeProvider _timeProvider;

    // Status mapping constants provided by user
    public const int DealStatusExpired = 6;
    public const int DealStatusSold = 7;
    public const int DealStatusOutOfStock = 8;

    public SupabaseDealRepository(Client client, TimeProvider? timeProvider = null)
    {
        _client = client;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // Stop words table model
    private class StopWord : Supabase.Postgrest.Models.BaseModel
    {
        [Supabase.Postgrest.Attributes.Table("stop_words")]
        public class Entity : Supabase.Postgrest.Models.BaseModel
        {
            [Supabase.Postgrest.Attributes.PrimaryKey("name")] public string name { get; set; } = string.Empty;
            [Supabase.Postgrest.Attributes.Column("active")] public bool active { get; set; } = true;
        }
    }

    public async Task<IReadOnlyList<string>> GetStopWordsAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _client.From<StopWord.Entity>()
                .Filter("active", Supabase.Postgrest.Constants.Operator.Equals, "true")
                .Get(ct);
            if (resp?.Models == null || resp.Models.Count == 0) return Array.Empty<string>();
            return resp.Models.Select(r => r.name).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public async Task<IReadOnlyList<Deal>> GetActiveDealsForRefreshAsync(int batchSize, TimeSpan minInterval, CancellationToken ct)
    {
        // Deprecated in favor of product-centric selection; keep for compatibility.
        var response = await _client.From<Deal>()
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Filter("deal_status_id", Supabase.Postgrest.Constants.Operator.Equals, "2")
            .Filter("deal_type_id", Supabase.Postgrest.Constants.Operator.Equals, "1")
            .Limit(batchSize)
            .Get(ct);
        return response.Models;
    }

    public async Task<Deal?> GetDealByIdAsync(int dealId, CancellationToken ct)
    {
        if (dealId <= 0) return null;
        var resp = await _client.From<Deal>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, dealId.ToString())
            .Limit(1)
            .Get(ct);
        return resp.Models?.FirstOrDefault();
    }

    public async Task<IReadOnlyList<DealProduct>> GetDueDealProductsAsync(int batchSize, CancellationToken ct)
    {
        // Select active, non-deleted products that are due now.
        var nowIso = _timeProvider.GetUtcNow().UtcDateTime.ToString("O");
        var response = await _client.From<DealProduct>()
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Filter("deal_status_id", Supabase.Postgrest.Constants.Operator.Equals, "2")
            .Filter("next_check_at", Supabase.Postgrest.Constants.Operator.LessThanOrEqual, nowIso)
            .Limit(batchSize)
            .Get(ct);

        var due = response.Models ?? new List<DealProduct>();
        if (due.Count == 0) return due;

        // Also ensure the parent deal itself is not deleted.
        var dealIds = due
            .Select(dp => dp.DealId)
            .Distinct()
            .ToList();

        if (dealIds.Count == 0) return due;

        var dealIdObjects = dealIds.Cast<object>().ToArray();
        var dealsResp = await _client.From<Deal>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.In, dealIdObjects)
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Select("id")
            .Get(ct);

        var allowed = (dealsResp.Models ?? new List<Deal>()).Select(d => d.Id).ToHashSet();
        return due.Where(dp => allowed.Contains(dp.DealId)).ToList();
    }

    public async Task<IReadOnlyList<Deal>> GetExpiredActiveDealsAsync(CancellationToken ct)
    {
        var nowIso = _timeProvider.GetUtcNow().UtcDateTime.ToString("O");
        var response = await _client.From<Deal>()
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Filter("deal_status_id", Supabase.Postgrest.Constants.Operator.Equals, "2")
            .Filter("expiration_date", Supabase.Postgrest.Constants.Operator.LessThan, nowIso)
            .Get(ct);
        return response.Models;
    }

    public async Task ExpireDealAndProductsAsync(Deal deal, CancellationToken ct)
    {
        // Update deal status
        deal.DealStatusId = DealStatusExpired;
        await _client.From<Deal>().Upsert(deal);

        // Update all associated product deals
        var dpResp = await _client.From<DealProduct>()
            .Filter("deal_id", Supabase.Postgrest.Constants.Operator.Equals, deal.Id.ToString())
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Get(ct);
        var productIds = new HashSet<int>();
        foreach (var dp in dpResp.Models)
        {
            dp.DealStatusId = DealStatusExpired;
            await _client.From<DealProduct>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, dp.Id.ToString())
                .Update(dp);
            if (dp.ProductId > 0)
                productIds.Add(dp.ProductId);
        }
        // Recalculate best deal per affected product
        foreach (var pid in productIds)
        {
            await UpdateProductBestDealAsync(pid, ct);
        }
    }

    public async Task UpdateDealsAsync(IEnumerable<Deal> deals, CancellationToken ct)
    {
        foreach (var deal in deals)
        {
            await _client.From<Deal>().Upsert(deal);
        }
    }

    public async Task AppendPriceHistoryAsync(int dealId, decimal newPrice, string? currency, DateTime changedUtc, CancellationToken ct)
    {
        // Append history for all active deal_products on this deal (or first found if multiple)
        var dpResp = await _client.From<DealProduct>()
            .Filter("deal_id", Supabase.Postgrest.Constants.Operator.Equals, dealId.ToString())
            .Filter("deal_status_id", Supabase.Postgrest.Constants.Operator.Equals, "2")
            .Limit(1)
            .Get(ct);
        var dealProduct = dpResp.Models.FirstOrDefault();
        if (dealProduct == null) return;

        var record = new DealProductPriceHistory
        {
            DealProductId = dealProduct.Id,
            Price = newPrice,
            Currency = currency,
            ChangedAt = changedUtc
        };
        await _client.From<DealProductPriceHistory>().Insert(record);
    }

    public async Task<DealProduct?> GetPrimaryDealProductAsync(int dealId, CancellationToken ct)
    {
        var dpResp = await _client.From<DealProduct>()
            .Filter("deal_id", Supabase.Postgrest.Constants.Operator.Equals, dealId.ToString())
            .Filter("deal_status_id", Supabase.Postgrest.Constants.Operator.Equals, "2")
            .Limit(1)
            .Get(ct);
        return dpResp.Models.FirstOrDefault();
    }

    public async Task UpdateDealProductAsync(DealProduct dealProduct, CancellationToken ct)
    {
        await _client
            .From<DealProduct>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, dealProduct.Id.ToString())
            .Update(dealProduct);
    }

    public async Task SetNextCheckAsync(DealProduct dealProduct, DateTime nextCheckAt, CancellationToken ct)
    {
        if (dealProduct == null) return;
        dealProduct.NextCheckAt = nextCheckAt;
        await _client
            .From<DealProduct>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, dealProduct.Id.ToString())
            .Update(dealProduct);
    }

    public async Task IncrementErrorCountAsync(DealProduct dealProduct, CancellationToken ct)
    {
        if (dealProduct == null) return;
        dealProduct.ErrorCount = (dealProduct.ErrorCount ?? 0) + 1;
        await _client
            .From<DealProduct>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, dealProduct.Id.ToString())
            .Update(dealProduct);
    }

    public async Task MarkStaleAsync(DealProduct dealProduct, CancellationToken ct)
    {
                if (dealProduct == null) return;
        dealProduct.StaleAt = _timeProvider.GetUtcNow().UtcDateTime;
        await _client
            .From<DealProduct>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, dealProduct.Id.ToString())
            .Update(dealProduct);
    }

    public async Task<Store?> GetStoreByIdAsync(int? storeId, CancellationToken ct)
    {
        if (storeId == null) return null;
        var resp = await _client.From<Store>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, storeId.Value.ToString())
            .Limit(1)
            .Get(ct);
        return resp.Models.FirstOrDefault();
    }

    public async Task<User?> GetUserByIdAsync(int userId, CancellationToken ct)
    {
        var resp = await _client.From<User>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, userId.ToString())
            .Limit(1)
            .Get(ct);
        return resp.Models.FirstOrDefault();
    }

    public async Task<int> GetRecentClicksAsync(long deal_id,long product_id, TimeSpan window, CancellationToken ct)
    {
        var sinceIso = _timeProvider.GetUtcNow().UtcDateTime.Subtract(window).ToString("O");
        var resp = await _client.From<DealClick>()
            .Filter("deal_id", Supabase.Postgrest.Constants.Operator.Equals, deal_id.ToString())
            .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, product_id.ToString())
            .Filter("created_at", Supabase.Postgrest.Constants.Operator.GreaterThanOrEqual, sinceIso)
            .Get(ct);
        return resp.Models.Count;
    }

    public async Task UpdateProductBestDealAsync(int productId, CancellationToken ct)
    {
        // Call stored function f_update_product_best_deal(product_id => productId)
        var args = new Dictionary<string, object>
        {
            { "product_id", productId }
        };
        try
        {
            await _client.Rpc("f_update_product_best_deal", args);
        }
        catch
        {
            // Silent failure; optionally add logging if desired
        }
    }

    public async Task<Product?> FindProductByGTINAsync(string gtin, CancellationToken ct)
    {
        var resp = await _client.From<ProductVariantGTIN>()
            .Filter("gtin", Supabase.Postgrest.Constants.Operator.Equals, gtin)
            .Limit(1)
            .Get(ct);
        var pg = resp.Models.FirstOrDefault();
        if (pg == null) return null;
        var prodResp = await _client.From<Product>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, pg.ProductVariantId.ToString())
            .Limit(1)
            .Get(ct);
        return prodResp.Models.FirstOrDefault();
    }

    public async Task<Product?> FindProductByBrandAndMPNAsync(int brandId, string mpn, CancellationToken ct)
    {
        var resp = await _client.From<ProductVariantMPN>()
            .Filter("mpn", Supabase.Postgrest.Constants.Operator.Equals, mpn)
            .Limit(10)
            .Get(ct);
        var ids = resp.Models.Select(x => x.ProductVariantId).ToHashSet();
        if (ids.Count == 0) return null;
        var prodResp = await _client.From<Product>()
            .Filter("brand_id", Supabase.Postgrest.Constants.Operator.Equals, brandId.ToString())
            .Filter("id", Supabase.Postgrest.Constants.Operator.In, string.Join(',', ids))
            .Limit(1)
            .Get(ct);
        return prodResp.Models.FirstOrDefault();
    }

    public async Task<Product> CreateProductAsync(Product product, CancellationToken ct)
    {
        var insert = await _client.From<Product>().Insert(product);
        return insert.Models.First();
    }

    public async Task<Deal> CreateDealAsync(Deal deal, CancellationToken ct)
    {
        var insert = await _client.From<Deal>().Insert(deal);
        return insert.Models.First();
    }

    public async Task<bool> ExistsDealByStoreItemAsync(string storeItemId, CancellationToken ct)
    {
        var resp = await _client.From<DealProduct>()
            .Filter("store_item_id", Supabase.Postgrest.Constants.Operator.Equals, storeItemId)
            .Limit(1)
            .Get(ct);
        return resp.Models.Any();
    }

    public async Task<DealProduct> CreateDealProductAsync(DealProduct dealProduct, CancellationToken ct)
    {
        var insert = await _client.From<DealProduct>().Insert(dealProduct);
        return insert.Models.First();
    }

    public async Task<IReadOnlyList<Product>> GetActiveProductsAsync(CancellationToken ct)
    {
        // Select products directly where deleted == false
        var prodResp = await _client.From<Product>()
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Get(ct);
        return prodResp.Models;
    }

    public async Task<Product?> GetProductByIdAsync(int productId, CancellationToken ct)
    {
        var resp = await _client.From<Product>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, productId.ToString())
            .Limit(1)
            .Get(ct);
        return resp.Models.FirstOrDefault();
    }
}