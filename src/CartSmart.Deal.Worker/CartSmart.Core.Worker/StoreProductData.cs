namespace CartSmart.Core.Worker;

public sealed record StoreProductData(
    decimal? Price,
    string? Currency,
    bool? InStock,
    bool? Sold,
    bool? Discontinued,
    DateTime RetrievedUtc
);