using CartSmart.API.Models;
using CartSmart.Core.Worker;
using Microsoft.Extensions.Logging;
using Supabase.Postgrest;
using static Supabase.Postgrest.Constants;

namespace CartSmart.Core.Worker;

public class SupabaseIngestionRepository : IIngestionRepository
{
    private readonly Supabase.Client _client;
    private readonly ILogger<SupabaseIngestionRepository> _logger;

    public SupabaseIngestionRepository(Supabase.Client client, ILogger<SupabaseIngestionRepository> logger)
    {
        _client = client;
        _logger = logger;
    }

    // ─── IngestionSource ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<IngestionSource>> GetDueSourcesAsync(CancellationToken ct)
    {
        // Sources where: enabled AND (never polled OR last_polled_at + interval <= now)
        var allEnabled = await _client.From<IngestionSource>()
            .Filter("enabled", Operator.Equals, "true")
            .Get(ct);

        var now = DateTime.UtcNow;
        return allEnabled.Models
            .Where(s => !s.LastPolledAt.HasValue ||
                        s.LastPolledAt.Value.AddMinutes(s.PollIntervalMinutes) <= now)
            .ToList();
    }

    public async Task UpdateSourceLastPolledAsync(long sourceId, DateTime polledAt, CancellationToken ct)
    {
        await _client.From<IngestionSource>()
            .Filter("id", Operator.Equals, sourceId.ToString())
            .Set(x => x.LastPolledAt!, polledAt)
            .Update(cancellationToken: ct);
    }

    // ─── RawSignal ──────────────────────────────────────────────────────────────

    public async Task<RawSignal> CreateRawSignalAsync(RawSignal signal, CancellationToken ct)
    {
        var response = await _client.From<RawSignal>().Insert(signal, cancellationToken: ct);
        return response.Models.First();
    }

    public async Task<bool> RawSignalExistsAsync(long sourceId, string externalId, CancellationToken ct)
    {
        var response = await _client.From<RawSignal>()
            .Filter("ingestion_source_id", Operator.Equals, sourceId.ToString())
            .Filter("external_id", Operator.Equals, externalId)
            .Limit(1)
            .Get(ct);

        return response.Models.Count > 0;
    }

    public async Task<IReadOnlyList<RawSignal>> GetPendingSignalsAsync(int batchSize, CancellationToken ct)
    {
        var response = await _client.From<RawSignal>()
            .Filter("status", Operator.Equals, "pending")
            .Order("created_at", Ordering.Ascending)
            .Limit(batchSize)
            .Get(ct);

        return response.Models;
    }

    public async Task UpdateRawSignalAsync(RawSignal signal, CancellationToken ct)
    {
        await _client.From<RawSignal>()
            .Filter("id", Operator.Equals, signal.Id.ToString())
            .Set(x => x.Status, signal.Status)
            .Set(x => x.ErrorMessage!, signal.ErrorMessage)
            .Set(x => x.ProcessedAt!, signal.ProcessedAt)
            .Update(cancellationToken: ct);
    }

    // ─── ExtractedDeal ──────────────────────────────────────────────────────────

    public async Task<ExtractedDeal> CreateExtractedDealAsync(ExtractedDeal deal, CancellationToken ct)
    {
        var response = await _client.From<ExtractedDeal>().Insert(deal, cancellationToken: ct);
        return response.Models.First();
    }

    public async Task UpdateExtractedDealAsync(ExtractedDeal deal, CancellationToken ct)
    {
        await _client.From<ExtractedDeal>()
            .Filter("id", Operator.Equals, deal.Id.ToString())
            .Set(x => x.Status, deal.Status)
            .Set(x => x.DealId!, deal.DealId)
            .Set(x => x.ImportedAt!, deal.ImportedAt)
            .Update(cancellationToken: ct);
    }

    public async Task<IReadOnlyList<ExtractedDeal>> GetAutoImportableDealsAsync(decimal minConfidence, int batchSize, CancellationToken ct)
    {
        var response = await _client.From<ExtractedDeal>()
            .Filter("status", Operator.Equals, "pending_review")
            .Filter("confidence_score", Operator.GreaterThanOrEqual, minConfidence.ToString("F4"))
            .Order("confidence_score", Ordering.Descending)
            .Limit(batchSize)
            .Get(ct);

        // Only auto-import deals that have both product and store matched
        return response.Models
            .Where(d => d.ProductId.HasValue && d.StoreId.HasValue)
            .ToList();
    }

    // ─── Product/Store matching ─────────────────────────────────────────────────

    public async Task<Product?> FindProductByNameFuzzyAsync(string name, string? brand, CancellationToken ct)
    {
        // Use ilike for case-insensitive partial matching on product name
        var query = _client.From<Product>()
            .Filter("deleted", Operator.Equals, "false")
            .Filter("name", Operator.ILike, $"%{name}%");

        var response = await query.Limit(5).Get(ct);

        if (response.Models.Count == 0)
            return null;

        // If we got multiple matches and have a brand, try to narrow down
        if (response.Models.Count > 1 && !string.IsNullOrWhiteSpace(brand))
        {
            // Load brands to compare
            var brands = await _client.From<Brand>()
                .Filter("name", Operator.ILike, $"%{brand}%")
                .Limit(1)
                .Get(ct);

            if (brands.Models.Count > 0)
            {
                var brandId = brands.Models[0].Id;
                var match = response.Models.FirstOrDefault(p => p.BrandId == brandId);
                if (match != null) return match;
            }
        }

        return response.Models.First();
    }

    public async Task<Store?> FindStoreByNameAsync(string name, CancellationToken ct)
    {
        var response = await _client.From<Store>()
            .Filter("name", Operator.ILike, $"%{name}%")
            .Limit(1)
            .Get(ct);

        return response.Models.FirstOrDefault();
    }

    public async Task<IReadOnlyList<Product>> FindProductsByStoreAsync(int storeId, CancellationToken ct)
    {
        // Find products that have deals linked to this store
        // This returns products that have at least one deal at the given store
        var deals = await _client.From<Deal>()
            .Filter("store_id", Operator.Equals, storeId.ToString())
            .Select("id")
            .Limit(500)
            .Get(ct);

        if (deals.Models.Count == 0)
            return [];

        var dealIds = deals.Models.Select(d => d.Id.ToString()).ToList();
        var dealProducts = await _client.From<DealProduct>()
            .Filter("deal_id", Operator.In, dealIds)
            .Select("product_id")
            .Limit(500)
            .Get(ct);

        var productIds = dealProducts.Models
            .Select(dp => dp.ProductId.ToString())
            .Distinct()
            .ToList();

        if (productIds.Count == 0)
            return [];

        var products = await _client.From<Product>()
            .Filter("id", Operator.In, productIds)
            .Filter("deleted", Operator.Equals, "false")
            .Get(ct);

        return products.Models;
    }

    public async Task<IngestionSource?> GetIngestionSourceBySignalIdAsync(long rawSignalId, CancellationToken ct)
    {
        // Look up the raw signal to get its source ID, then load the source
        var signalResp = await _client.From<RawSignal>()
            .Filter("id", Operator.Equals, rawSignalId.ToString())
            .Limit(1)
            .Get(ct);

        var signal = signalResp.Models.FirstOrDefault();
        if (signal is null) return null;

        var sourceResp = await _client.From<IngestionSource>()
            .Filter("id", Operator.Equals, signal.IngestionSourceId.ToString())
            .Limit(1)
            .Get(ct);

        return sourceResp.Models.FirstOrDefault();
    }
}
