using System.Collections.Concurrent;
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

    private readonly ConcurrentDictionary<int, IReadOnlyList<string>> _productNegativeKeywordsCache = new();
    private readonly ConcurrentDictionary<int, IReadOnlyList<string>> _productTypeNegativeKeywordsCache = new();

    // Status mapping constants provided by user
    public const int DealStatusActive = 2;
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
        // Note: refresh should only run for DIRECT deals (deal_type_id = 1).
        // We need two queries because NULL next_check_at is never <= now in SQL.
        var nowIso = _timeProvider.GetUtcNow().UtcDateTime.ToString("O");

        // 1) Rows where next_check_at <= now (scheduled and due)
        var dueResponse = await _client.From<DealProduct>()
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Filter("deal_status_id", Supabase.Postgrest.Constants.Operator.Equals, "2")
            .Filter("next_check_at", Supabase.Postgrest.Constants.Operator.LessThanOrEqual, nowIso)
            .Limit(batchSize)
            .Get(ct);

        // 2) Rows where next_check_at IS NULL (never scheduled — treat as immediately due)
        var nullResponse = await _client.From<DealProduct>()
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Filter("deal_status_id", Supabase.Postgrest.Constants.Operator.Equals, "2")
            .Filter("next_check_at", Supabase.Postgrest.Constants.Operator.Is, "null")
            .Limit(batchSize)
            .Get(ct);

        var dueModels = dueResponse.Models ?? new List<DealProduct>();
        var nullModels = nullResponse.Models ?? new List<DealProduct>();

        // Merge and deduplicate by Id
        var due = dueModels
            .Concat(nullModels)
            .GroupBy(dp => dp.Id)
            .Select(g => g.First())
            .ToList();

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
            .Filter("deal_type_id", Supabase.Postgrest.Constants.Operator.Equals, "1")
            .Select("id")
            .Get(ct);

        var allowed = (dealsResp.Models ?? new List<Deal>()).Select(d => d.Id).ToHashSet();
        return due.Where(dp => allowed.Contains(dp.DealId)).ToList();
    }

    public async Task<IReadOnlyDictionary<int, Product>> GetProductsByIdsAsync(IEnumerable<int> productIds, CancellationToken ct)
    {
        var ids = (productIds ?? Array.Empty<int>())
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
            return new Dictionary<int, Product>();

        var idObjects = ids.Cast<object>().ToArray();
        var resp = await _client
            .From<Product>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.In, idObjects)
            .Get(ct);

        var models = resp.Models ?? new List<Product>();
        return models
            .Where(p => p != null && p.Id > 0)
            .GroupBy(p => p.Id)
            .ToDictionary(g => g.Key, g => g.First());
    }

    public async Task<Dictionary<int, int>> GetClickCountsByProductAsync(IEnumerable<int> productIds, TimeSpan window, CancellationToken ct)
    {
        var ids = (productIds ?? Array.Empty<int>())
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
            return new Dictionary<int, int>();

        var sinceIso = _timeProvider.GetUtcNow().UtcDateTime.Subtract(window).ToString("O");
        var idObjects = ids.Cast<object>().ToArray();
        var resp = await _client
            .From<DealClick>()
            .Filter("product_id", Supabase.Postgrest.Constants.Operator.In, idObjects)
            .Filter("created_at", Supabase.Postgrest.Constants.Operator.GreaterThanOrEqual, sinceIso)
            .Get(ct);

        var rows = resp.Models ?? new List<DealClick>();
        return rows
            .Where(r => r.ProductId.HasValue)
            .GroupBy(r => r.ProductId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
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
            await _client
                .From<Deal>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, deal.Id.ToString())
                .Update(deal);
        }
    }

    public async Task AppendPriceHistoryAsync(int dealId, decimal newPrice, string? currency, DateTime changedUtc, CancellationToken ct)
    {
        // Append history for all active deal_products on this deal (or first found if multiple)
        var dpResp = await _client.From<DealProduct>()
            .Filter("deal_id", Supabase.Postgrest.Constants.Operator.Equals, dealId.ToString())
            .Filter("deal_status_id", Supabase.Postgrest.Constants.Operator.Equals, DealStatusActive.ToString())
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

    public async Task AppendPriceHistoryForDealProductAsync(int dealProductId, decimal newPrice, string? currency, DateTime changedUtc, CancellationToken ct)
    {
        if (dealProductId <= 0) return;
        var record = new DealProductPriceHistory
        {
            DealProductId = dealProductId,
            Price = newPrice,
            Currency = currency,
            ChangedAt = changedUtc
        };
        await _client.From<DealProductPriceHistory>().Insert(record);
    }

    public async Task<DealProduct?> GetDealProductByIdAsync(int dealProductId, CancellationToken ct)
    {
        if (dealProductId <= 0) return null;
        var resp = await _client
            .From<DealProduct>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, dealProductId.ToString())
            .Limit(1)
            .Get(ct);
        return resp.Models?.FirstOrDefault();
    }

    public async Task<IReadOnlyList<DealProduct>> GetDealProductsForDealAsync(int dealId, CancellationToken ct)
    {
        if (dealId <= 0) return Array.Empty<DealProduct>();
        var resp = await _client
            .From<DealProduct>()
            .Filter("deal_id", Supabase.Postgrest.Constants.Operator.Equals, dealId.ToString())
            .Get(ct);
        return resp.Models ?? new List<DealProduct>();
    }

    public async Task<long?> CreateOrGetPendingManualPriceTaskAsync(DealProduct dealProduct, string reason, CancellationToken ct)
    {
        if (dealProduct == null || dealProduct.Id <= 0) return null;
        var url = (dealProduct.Url ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(url)) return null;

        // Skip if the deal product was recently confirmed by another source (extension, API, admin).
        // This prevents re-creating tasks that were just closed because the scraper still can't access the page.
        if (dealProduct.LastCheckedAt.HasValue)
        {
            var hoursSinceCheck = (_timeProvider.GetUtcNow().UtcDateTime - dealProduct.LastCheckedAt.Value).TotalHours;
            if (hoursSinceCheck < 48 && (dealProduct.ErrorCount ?? 0) == 0)
                return null;
        }

        // De-dupe: if a pending task already exists, reuse it.
        try
        {
            var existing = await _client
                .From<ManualPriceTask>()
                .Filter("deal_product_id", Supabase.Postgrest.Constants.Operator.Equals, dealProduct.Id.ToString())
                .Filter("status", Supabase.Postgrest.Constants.Operator.Equals, "pending")
                .Limit(1)
                .Get(ct);
            var found = existing.Models?.FirstOrDefault();
            if (found != null) return found.Id;
        }
        catch
        {
            // ignore and attempt insert
        }

        try
        {
            var insert = new ManualPriceTaskInsertRow
            {
                DealProductId = dealProduct.Id,
                Url = url,
                Reason = string.IsNullOrWhiteSpace(reason) ? "bot_protection" : reason,
                Status = "pending"
            };

            await _client.From<ManualPriceTaskInsertRow>().Insert(insert);

            // Insert-row model doesn't include the generated ID; read back.
            var readBack = await _client
                .From<ManualPriceTask>()
                .Filter("deal_product_id", Supabase.Postgrest.Constants.Operator.Equals, dealProduct.Id.ToString())
                .Filter("status", Supabase.Postgrest.Constants.Operator.Equals, "pending")
                .Order("created_at", Supabase.Postgrest.Constants.Ordering.Descending)
                .Limit(1)
                .Get(ct);
            return readBack.Models?.FirstOrDefault()?.Id;
        }
        catch
        {
            // If insert fails (race due to unique partial index), attempt read again.
            try
            {
                var existing = await _client
                    .From<ManualPriceTask>()
                    .Filter("deal_product_id", Supabase.Postgrest.Constants.Operator.Equals, dealProduct.Id.ToString())
                    .Filter("status", Supabase.Postgrest.Constants.Operator.Equals, "pending")
                    .Limit(1)
                    .Get(ct);
                return existing.Models?.FirstOrDefault()?.Id;
            }
            catch
            {
                return null;
            }
        }
    }

    public async Task<IReadOnlyList<ManualPriceTask>> GetPendingManualPriceTasksAsync(int limit, CancellationToken ct)
    {
        var l = limit <= 0 ? 50 : Math.Min(limit, 200);
        var resp = await _client
            .From<ManualPriceTask>()
            .Filter("status", Supabase.Postgrest.Constants.Operator.Equals, "pending")
            .Order("created_at", Supabase.Postgrest.Constants.Ordering.Descending)
            .Limit(l)
            .Get(ct);
        return resp.Models ?? new List<ManualPriceTask>();
    }

    public async Task<ManualPriceTask?> GetManualPriceTaskByIdAsync(long taskId, CancellationToken ct)
    {
        if (taskId <= 0) return null;
        var resp = await _client
            .From<ManualPriceTask>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, taskId.ToString())
            .Limit(1)
            .Get(ct);
        return resp.Models?.FirstOrDefault();
    }

    public async Task<bool> ApplyManualPriceTaskSubmissionAsync(
        long taskId,
        decimal? price,
        string? currency,
        bool? inStock,
        bool? sold,
        string? submittedBy,
        string? notes,
        CancellationToken ct)
    {
        var task = await GetManualPriceTaskByIdAsync(taskId, ct);
        if (task == null) return false;
        if (!string.Equals(task.Status, "pending", StringComparison.OrdinalIgnoreCase)) return false;

        var dp = await GetDealProductByIdAsync(task.DealProductId, ct);
        if (dp == null) return false;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var statusChanged = false;

        // Status mapping
        if (sold == true && dp.DealStatusId != DealStatusSold)
        {
            dp.DealStatusId = DealStatusSold;
            statusChanged = true;
        }
        else if (inStock == false && dp.DealStatusId != DealStatusOutOfStock)
        {
            dp.DealStatusId = DealStatusOutOfStock;
            statusChanged = true;
        }
        else if (inStock == true && dp.DealStatusId != DealStatusActive)
        {
            dp.DealStatusId = DealStatusActive;
            statusChanged = true;
        }

        // Price mapping
        if (price.HasValue && price.Value > 0 && dp.Price != price.Value)
        {
            dp.Price = price.Value;
            await AppendPriceHistoryForDealProductAsync(dp.Id, price.Value, currency, now, ct);
        }

        // Clear scrape-noise flags: this is a human-confirmed update.
        dp.ErrorCount = 0;
        dp.StaleAt = null;
        dp.LastCheckedAt = now;
        dp.NextCheckAt = now.AddHours(12);

        await UpdateDealProductAsync(dp, ct);
        if (statusChanged)
        {
            try
            {
                await UpdateProductBestDealAsync(dp.ProductId, ct);
            }
            catch
            {
                // best-effort
            }
        }

        var update = new ManualPriceTaskUpdateRow
        {
            Id = taskId,
            Status = "completed",
            SubmittedAt = now,
            SubmittedPrice = price,
            SubmittedCurrency = currency,
            SubmittedInStock = inStock,
            SubmittedSold = sold,
            SubmittedBy = string.IsNullOrWhiteSpace(submittedBy) ? null : submittedBy,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes
        };
        await _client
            .From<ManualPriceTaskUpdateRow>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, taskId.ToString())
            .Update(update);

        return true;
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

    /// <summary>
    /// Fallback: find a store whose URL domain matches the given deal-product URL.
    /// Used when the id-based lookup returns null (e.g. deserialization failure).
    /// </summary>
    public async Task<Store?> GetStoreByUrlDomainAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        string host;
        try
        {
            var uri = new Uri(url);
            host = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        }
        catch { return null; }
        if (string.IsNullOrEmpty(host)) return null;

        // Fetch all stores (small table, typically < 200 rows)
        var resp = await _client.From<Store>().Get(ct);
        return resp.Models.FirstOrDefault(s =>
        {
            if (string.IsNullOrWhiteSpace(s.URL)) return false;
            try
            {
                var storeUrl = s.URL.Contains("://") ? s.URL : $"https://{s.URL}";
                var storeHost = new Uri(storeUrl).Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
                return storeHost == host;
            }
            catch { return false; }
        });
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

    public async Task<DealProduct?> GetDealProductByStoreItemIdAsync(string storeItemId, CancellationToken ct)
    {
        var resp = await _client.From<DealProduct>()
            .Filter("store_item_id", Supabase.Postgrest.Constants.Operator.Equals, storeItemId)
            .Limit(1)
            .Get(ct);
        return resp.Models.FirstOrDefault();
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

    public async Task<IReadOnlyList<Brand>> GetAllBrandsAsync(CancellationToken ct)
    {
        var resp = await _client.From<Brand>()
            .Select("id, name")
            .Get(ct);
        return resp.Models ?? new List<Brand>();
    }

    public async Task<IReadOnlyList<string>> GetOrFetchProductNegativeKeywordsAsync(int productId, CancellationToken ct)
    {
        if (productId <= 0) return Array.Empty<string>();
        if (_productNegativeKeywordsCache.TryGetValue(productId, out var cached))
            return cached;

        try
        {
            // Some PostgREST clients can be picky about boolean filters; filter in-memory instead.
            var resp = await _client
                .From<ProductNegativeKeyword>()
                .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, productId.ToString())
                .Get(ct);

            var keywords = (resp.Models ?? new List<ProductNegativeKeyword>())
                .Where(k => k.IsActive)
                .Select(k => (k.Keyword ?? string.Empty).Trim())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(100)
                .ToList();

            _productNegativeKeywordsCache[productId] = keywords;
            return keywords;
        }
        catch
        {
            _productNegativeKeywordsCache[productId] = Array.Empty<string>();
            return Array.Empty<string>();
        }
    }

    public async Task<IReadOnlyList<string>> GetOrFetchProductTypeNegativeKeywordsAsync(int productTypeId, CancellationToken ct)
    {
        if (productTypeId <= 0) return Array.Empty<string>();
        if (_productTypeNegativeKeywordsCache.TryGetValue(productTypeId, out var cached))
            return cached;

        try
        {
            var resp = await _client
                .From<ProductTypeNegativeKeyword>()
                .Filter("product_type_id", Supabase.Postgrest.Constants.Operator.Equals, productTypeId.ToString())
                .Get(ct);

            var keywords = (resp.Models ?? new List<ProductTypeNegativeKeyword>())
                .Where(k => k.IsActive)
                .Select(k => (k.Keyword ?? string.Empty).Trim())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(100)
                .ToList();

            _productTypeNegativeKeywordsCache[productTypeId] = keywords;
            return keywords;
        }
        catch
        {
            _productTypeNegativeKeywordsCache[productTypeId] = Array.Empty<string>();
            return Array.Empty<string>();
        }
    }

    private static Uri? TryCreateHttpUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var abs))
            return abs;

        var candidate = $"https://{url.Trim()}";
        if (Uri.TryCreate(candidate, UriKind.Absolute, out abs))
            return abs;

        return null;
    }

    private static string? NormalizeUrlForMatch(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var u = TryCreateHttpUri(url);
        if (u == null) return url.Trim().TrimEnd('/').ToLowerInvariant();

        var path = u.AbsolutePath?.TrimEnd('/');
        var host = u.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        return $"{host}{path}".ToLowerInvariant();
    }

    private static bool PricesEqual(decimal a, decimal b)
    {
        return Math.Abs(a - b) < 0.005m; // ~1/2 cent tolerance
    }

    private static decimal ApplyPercentOff(decimal basePrice, int? percentOff)
    {
        if (!percentOff.HasValue) return basePrice;
        if (percentOff.Value <= 0) return basePrice;
        if (percentOff.Value >= 100) return 0m;

        var factor = 1m - (percentOff.Value / 100m);
        var raw = basePrice * factor;
        return Math.Round(raw, 2, MidpointRounding.AwayFromZero);
    }

    public async Task<int> PropagateDirectPriceChangeToLinkedDealsByUrlAsync(
        DealProduct directDealProduct,
        decimal oldDirectPrice,
        decimal newDirectPrice,
        CancellationToken ct)
    {
        if (directDealProduct == null) return 0;
        if (directDealProduct.ProductId <= 0) return 0;
        if (string.IsNullOrWhiteSpace(directDealProduct.Url)) return 0;
        if (PricesEqual(oldDirectPrice, newDirectPrice)) return 0;

        var targetNorm = NormalizeUrlForMatch(directDealProduct.Url);
        if (string.IsNullOrWhiteSpace(targetNorm)) return 0;

        // Pull all active deal_products for this product and filter locally by normalized URL.
        // (PostgREST doesn't have a stable "normalized url" column we can filter on.)
        var dpResp = await _client
            .From<DealProduct>()
            .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, directDealProduct.ProductId.ToString())
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Filter("deal_status_id", Supabase.Postgrest.Constants.Operator.Equals, DealStatusActive.ToString())
            .Select("id,deal_id,product_id,product_variant_id,price,url,deal_status_id,deleted")
            .Get(ct);

        var allForProduct = dpResp.Models ?? new List<DealProduct>();
        if (allForProduct.Count == 0) return 0;

        bool VariantMatches(DealProduct dp)
        {
            // Conservative: only propagate within the same variant scope.
            if (directDealProduct.ProductVariantId.HasValue)
                return dp.ProductVariantId == directDealProduct.ProductVariantId;
            return dp.ProductVariantId == null;
        }

        var linked = allForProduct
            .Where(dp => dp != null)
            .Where(dp => dp.Id != directDealProduct.Id)
            .Where(dp => VariantMatches(dp))
            .Where(dp => NormalizeUrlForMatch(dp.Url) == targetNorm)
            .ToList();

        if (linked.Count == 0) return 0;

        var linkedDealIds = linked
            .Select(x => x.DealId)
            .Distinct()
            .Cast<object>()
            .ToArray();

        if (linkedDealIds.Length == 0) return 0;

        var dealsResp = await _client
            .From<Deal>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.In, linkedDealIds)
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Select("id,deal_type_id,discount_percent,deleted")
            .Get(ct);

        var deals = dealsResp.Models ?? new List<Deal>();
        if (deals.Count == 0) return 0;
        var dealById = deals.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());

        // Preload combo definitions for stacked deals.
        var stackedDealIds = deals
            .Where(d => d.DealTypeId == 3)
            .Select(d => d.Id)
            .Distinct()
            .ToList();

        Dictionary<int, List<DealCombo>> combosByStacked = new();
        Dictionary<int, Deal> componentDealById = new();
        if (stackedDealIds.Count > 0)
        {
            var stackedObjects = stackedDealIds.Cast<object>().ToArray();
            var comboResp = await _client
                .From<DealCombo>()
                .Filter("deal_id", Supabase.Postgrest.Constants.Operator.In, stackedObjects)
                .Select("deal_id,combo_deal_id,order")
                .Get(ct);

            var combos = comboResp.Models ?? new List<DealCombo>();
            combosByStacked = combos
                .GroupBy(c => c.DealId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var componentIds = combos
                .Select(c => c.ComboDealId)
                .Distinct()
                .ToList();

            if (componentIds.Count > 0)
            {
                var componentObjects = componentIds.Cast<object>().ToArray();
                var componentDealsResp = await _client
                    .From<Deal>()
                    .Filter("id", Supabase.Postgrest.Constants.Operator.In, componentObjects)
                    .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
                    .Select("id,deal_type_id,discount_percent,deleted")
                    .Get(ct);

                var componentDeals = componentDealsResp.Models ?? new List<Deal>();
                componentDealById = componentDeals
                    .GroupBy(d => d.Id)
                    .ToDictionary(g => g.Key, g => g.First());
            }
        }

        decimal ComputeStackedPrice(int stackedDealId)
        {
            // Default: base on the updated direct price.
            var price = newDirectPrice;

            if (!combosByStacked.TryGetValue(stackedDealId, out var combos) || combos.Count == 0)
            {
                // Fallback: treat stacked like a single percent-off deal if it has one.
                if (dealById.TryGetValue(stackedDealId, out var stackedDealFallback))
                    return ApplyPercentOff(price, stackedDealFallback.DiscountPercent);
                return price;
            }

            var ordered = combos
                .OrderBy(c => c.Order ?? int.MaxValue)
                .ThenBy(c => c.ComboDealId)
                .ToList();

            foreach (var c in ordered)
            {
                if (!componentDealById.TryGetValue(c.ComboDealId, out var comp))
                    continue;

                // Apply percent-off style steps (coupon/external). Ignore direct steps (they define the base URL/price).
                if (comp.DealTypeId is 2 or 4)
                    price = ApplyPercentOff(price, comp.DiscountPercent);
            }

            return price;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var updated = 0;

        foreach (var dp in linked)
        {
            if (!dealById.TryGetValue(dp.DealId, out var d))
                continue;

            var dealType = d.DealTypeId;
            if (!dealType.HasValue) continue;
            if (dealType.Value == 1) continue; // direct

            var newPrice = dp.Price;

            if (dealType.Value == 2 || dealType.Value == 4)
            {
                newPrice = ApplyPercentOff(newDirectPrice, d.DiscountPercent);
            }
            else if (dealType.Value == 3)
            {
                newPrice = ComputeStackedPrice(d.Id);
            }
            else
            {
                // Unknown deal type: do not touch.
                continue;
            }

            if (PricesEqual(dp.Price, newPrice))
                continue;

            dp.Price = newPrice;
            // This is a derived update; still record price history for observability.
            await AppendPriceHistoryForDealProductAsync(dp.Id, newPrice, currency: null, changedUtc: now, ct);
            await UpdateDealProductAsync(dp, ct);
            updated++;
        }

        if (updated > 0)
        {
            // Ensure the product best-deal reflects the new derived prices.
            await UpdateProductBestDealAsync(directDealProduct.ProductId, ct);
        }

        return updated;
    }
}