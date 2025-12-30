using CartSmart.API.Models;

namespace CartSmart.Core.Worker;
    public interface IStopWordsProvider
    {
        Task<IReadOnlyList<string>> GetStopWordsAsync(CancellationToken ct);
    }
public interface IDealRepository
{
    Task<IReadOnlyList<Deal>> GetActiveDealsForRefreshAsync(int batchSize, TimeSpan minInterval, CancellationToken ct);
    Task UpdateDealsAsync(IEnumerable<Deal> deals, CancellationToken ct);
    Task AppendPriceHistoryAsync(int dealId, decimal newPrice, string? currency, DateTime changedUtc, CancellationToken ct);
    Task<IReadOnlyList<Deal>> GetExpiredActiveDealsAsync(CancellationToken ct);
    Task ExpireDealAndProductsAsync(Deal deal, CancellationToken ct);
    Task UpdateProductBestDealAsync(int productId, CancellationToken ct);
    Task<Product?> FindProductByGTINAsync(string gtin, CancellationToken ct);
    Task<Product?> FindProductByBrandAndMPNAsync(int brandId, string mpn, CancellationToken ct);
    Task<Product> CreateProductAsync(Product product, CancellationToken ct);
    Task<Deal> CreateDealAsync(Deal deal, CancellationToken ct);
    Task<DealProduct> CreateDealProductAsync(DealProduct dealProduct, CancellationToken ct);
    Task<bool> ExistsDealByStoreItemAsync(string storeItemId, CancellationToken ct);
    Task<IReadOnlyList<Product>> GetActiveProductsAsync(CancellationToken ct);
    Task<Product?> GetProductByIdAsync(int productId, CancellationToken ct);
}

public interface IStoreClient
{
    StoreType StoreType { get; }
    bool SupportsSoldStatus { get; }
    bool SupportsApi { get; }
    Task<StoreProductData?> GetByUrlAsync(string productUrl, CancellationToken ct);
    Task<IReadOnlyList<NewListing>> SearchNewListingsAsync(string query, int? preferredConditionCategoryId, CancellationToken ct);
}

public interface IHtmlScraper
{
    Task<ScrapeResult?> ScrapeAsync(Uri uri, string[]? overridePriceSelectors, CancellationToken ct);
}

public interface IDealUpdateOrchestrator
{
    Task<DealRefreshResult> RefreshDealsAsync(int batchSize, CancellationToken ct);
    Task<int> SweepExpiredDealsAsync(CancellationToken ct);
    Task<int> IngestNewListingsAsync(StoreType storeType, int topPerProduct, IEnumerable<NewListingQuery> queries, CancellationToken ct);
}

public sealed record DealRefreshResult(int Total, int Updated, int Expired, int Sold, int Errors);

public sealed record NewListing(
    string ItemId,
    string? Title,
    string? Url,
    decimal? Price,
    string? Currency,
    string? GTIN,
    string? MPN,
    string? Brand,
    int? ConditionCategoryId,
    bool? FreeShipping
);

public sealed record NewListingQuery(
    int ProductId,
    string Query
);
