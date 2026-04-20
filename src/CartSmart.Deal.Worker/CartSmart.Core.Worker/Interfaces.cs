using CartSmart.API.Models;

namespace CartSmart.Core.Worker;
    public interface IStopWordsProvider
    {
        Task<IReadOnlyList<string>> GetStopWordsAsync(CancellationToken ct);
    }
public interface IDealRepository
{
    Task<IReadOnlyList<Deal>> GetActiveDealsForRefreshAsync(int batchSize, TimeSpan minInterval, CancellationToken ct);
    Task<Deal?> GetDealByIdAsync(int dealId, CancellationToken ct);
    Task UpdateDealsAsync(IEnumerable<Deal> deals, CancellationToken ct);
    Task UpdateDealDiscountOnlyAsync(int dealId, int? discountPercent, CancellationToken ct);
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
    Task<DealProduct?> GetDealProductByStoreItemIdAsync(string storeItemId, CancellationToken ct);
    Task<IReadOnlyList<Product>> GetActiveProductsAsync(CancellationToken ct);
    Task<Product?> GetProductByIdAsync(int productId, CancellationToken ct);
    Task<IReadOnlyList<DealProduct>> GetDealProductsForDealAsync(int dealId, CancellationToken ct);
    Task<IReadOnlyList<Brand>> GetAllBrandsAsync(CancellationToken ct);
}

public interface IStoreClient
{
    StoreType StoreType { get; }
    bool SupportsSoldStatus { get; }
    bool SupportsApi { get; }
    Task<StoreProductData?> GetByUrlAsync(string productUrl, CancellationToken ct);
    Task<IReadOnlyList<NewListing>> SearchNewListingsAsync(long productId, string query, int? preferredConditionCategoryId, CancellationToken ct);
}

// Optional capability interface (implemented only by store clients that can infer product variants from listing metadata)
public interface IVariantResolvingStoreClient
{
    Task<bool> HasActiveVariantsAsync(long productId, CancellationToken ct);
    Task<IReadOnlyList<long>> GetActiveVariantIdsAsync(long productId, CancellationToken ct);
    Task<long?> TryResolveProductVariantIdAsync(long productId, NewListing listing, CancellationToken ct);
}

public interface IHtmlScraper
{
    Task<ScrapeResult?> ScrapeAsync(Uri uri, string[]? overridePriceSelectors, CancellationToken ct);
    Task<ScrapeResult?> ScrapeAsync(Uri uri, string[]? overridePriceSelectors, bool httpEnabled, bool playwrightEnabled, CancellationToken ct)
        => ScrapeAsync(uri, overridePriceSelectors, ct);
}

public interface IListingPageScraper
{
    /// <summary>
    /// Scrape product listings from an HTML page, following pagination up to maxPages.
    /// Auto-detects single-product vs. multi-listing pages.
    /// </summary>
    Task<IReadOnlyList<ScrapedListing>> ScrapeListingsAsync(
        string url,
        ListingScrapeConfig selectors,
        bool httpEnabled,
        bool playwrightEnabled,
        int maxPages = 10,
        int delayBetweenPagesMs = 2000,
        CancellationToken ct = default);
}

public interface IDealUpdateOrchestrator
{
    Task<DealRefreshResult> RefreshDealsAsync(int batchSize, CancellationToken ct);
    Task<int> SweepExpiredDealsAsync(CancellationToken ct);
    Task<int> IngestNewListingsAsync(StoreType storeType, int topPerProduct, IEnumerable<NewListingQuery> queries, CancellationToken ct);
    /// <summary>
    /// Ingest pre-fetched listings (e.g. from HTML scraping) through the standard filtering/deal-creation pipeline.
    /// </summary>
    Task<int> IngestPreFetchedListingsAsync(int storeId, int topPerProduct, IEnumerable<NewListingQuery> queries,
        Dictionary<int, IReadOnlyList<NewListing>> listingsByProductId, CancellationToken ct);
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
    bool? FreeShipping,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Aspects = null,
    string? ShortDescription = null,
    double? ConfidenceScore = null,
    bool HasCoupons = false
);

public sealed record NewListingQuery(
    int ProductId,
    string Query
);

/// <summary>
/// Source-agnostic AI validation for deal content.
/// Validates whether content (listing, email, forum post, etc.) represents
/// a legitimate deal for a given product.
/// </summary>
public interface IAiDealValidator
{
    Task<AiValidationResult> ValidateAsync(AiValidationRequest request, CancellationToken ct);
}

/// <summary>
/// Optional capability: fetch coupon/promotion details for a store listing.
/// </summary>
public interface ICouponResolvingStoreClient
{
    Task<IReadOnlyList<StoreCoupon>> GetItemCouponsAsync(string itemId, CancellationToken ct);
}

public sealed record StoreCoupon(
    string? RedemptionCode,
    string? DiscountType,  // "PERCENTAGE" or "FIXED_AMOUNT"
    decimal? DiscountValue,
    string? Currency
);

public sealed record AiValidationRequest(
    string ProductName,
    string? ProductBrand,
    decimal? ProductMsrp,
    int? ExpectedPackCount,
    string ContentType,     // "ebay_listing", "email", "forum_post", "social_media", "web_page"
    string ContentTitle,
    string? ContentBody,
    decimal? ContentPrice,
    string? ContentUrl,
    IReadOnlyList<string>? KnownAliases = null
);

public sealed record AiValidationResult(
    bool IsLegitimate,
    string Reason
);
