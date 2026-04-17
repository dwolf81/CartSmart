using CartSmart.API.Models;

namespace CartSmart.Core.Worker;

// ─── Source types ───────────────────────────────────────────────────────────────

public enum SignalSourceType
{
    Email,
    Reddit,
    Social,
    Retail,
    Forum
}

// ─── Raw signal collected from a source ─────────────────────────────────────────

public sealed record CollectedSignal(
    string ExternalId,
    string? Title,
    string? Body,
    string? Url,
    string? Author,
    string? RawJson
);

// ─── AI extraction result ───────────────────────────────────────────────────────

public sealed record DealExtractionResult(
    string Title,
    decimal? Price,
    string? Currency,
    string? CouponCode,
    string? Url,
    int? DiscountPercent,
    int DealTypeId,               // 1=Direct, 2=Coupon, 3=Stacked, 4=External
    DateTime? ExpirationDate,
    string? StoreName,            // AI-inferred store name (for matching)
    string? ProductName,          // AI-inferred product name (for matching)
    string? ProductBrand,         // AI-inferred brand
    decimal ConfidenceScore,      // 0.0–1.0
    string? Reasoning,
    bool IsStoreWide = false,     // true = applies to all products for the store
    bool IsActionable = true,     // false = vague/unverifiable marketing language
    IReadOnlyList<ProductDealInfo>? Products = null  // product-specific deals within this signal
);

/// <summary>Individual product deal extracted from a multi-deal signal (e.g. a promo email listing several products on sale).</summary>
public sealed record ProductDealInfo(
    string ProductName,
    string? ProductBrand,
    decimal? Price,
    int? DiscountPercent,
    string? CouponCode,
    string? Url
);

// ─── Ingestion pipeline result ──────────────────────────────────────────────────

public sealed record IngestionRunResult(
    int SignalsCollected,
    int SignalsExtracted,
    int DealsAutoImported,
    int DealsQueuedForReview,
    int DuplicatesSkipped,
    int Errors
);

// ─── Signal source provider (one per source type) ───────────────────────────────

public interface ISignalSourceProvider
{
    SignalSourceType SourceType { get; }
    Task<IReadOnlyList<CollectedSignal>> CollectAsync(IngestionSource source, CancellationToken ct);
}

// ─── AI deal extractor ──────────────────────────────────────────────────────────

public interface IAiDealExtractor
{
    /// <summary>Extract a single deal (legacy, still used for simple signals).</summary>
    Task<DealExtractionResult?> ExtractAsync(RawSignal signal, CancellationToken ct);

    /// <summary>
    /// Extract one or more deals from a single signal. Emails and social posts often
    /// contain multiple deals (store-wide + product-specific). Returns all of them.
    /// </summary>
    Task<IReadOnlyList<DealExtractionResult>> ExtractMultipleAsync(RawSignal signal, CancellationToken ct);
}

// ─── Ingestion repository ───────────────────────────────────────────────────────

public interface IIngestionRepository
{
    Task<IReadOnlyList<IngestionSource>> GetDueSourcesAsync(CancellationToken ct);
    Task UpdateSourceLastPolledAsync(long sourceId, DateTime polledAt, CancellationToken ct);

    Task<RawSignal> CreateRawSignalAsync(RawSignal signal, CancellationToken ct);
    Task<bool> RawSignalExistsAsync(long sourceId, string externalId, CancellationToken ct);
    Task<IReadOnlyList<RawSignal>> GetPendingSignalsAsync(int batchSize, CancellationToken ct);
    Task UpdateRawSignalAsync(RawSignal signal, CancellationToken ct);

    Task<ExtractedDeal> CreateExtractedDealAsync(ExtractedDeal deal, CancellationToken ct);
    Task UpdateExtractedDealAsync(ExtractedDeal deal, CancellationToken ct);
    Task<IReadOnlyList<ExtractedDeal>> GetAutoImportableDealsAsync(decimal minConfidence, int batchSize, CancellationToken ct);

    // Product/store matching helpers
    Task<Product?> FindProductByNameFuzzyAsync(string name, string? brand, CancellationToken ct);
    Task<Store?> FindStoreByNameAsync(string name, CancellationToken ct);
    Task<IReadOnlyList<Product>> FindProductsByStoreAsync(int storeId, CancellationToken ct);
    Task<IngestionSource?> GetIngestionSourceBySignalIdAsync(long rawSignalId, CancellationToken ct);
}

// ─── Ingestion pipeline orchestrator ────────────────────────────────────────────

public interface IIngestionPipelineOrchestrator
{
    /// <summary>Collect raw signals from all due sources.</summary>
    Task<IngestionRunResult> CollectSignalsAsync(CancellationToken ct);

    /// <summary>Process pending signals: AI extract → match → score → queue/import.</summary>
    Task<IngestionRunResult> ProcessSignalsAsync(int batchSize, CancellationToken ct);
}
