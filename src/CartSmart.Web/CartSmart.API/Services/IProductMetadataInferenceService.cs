namespace CartSmart.API.Services;

/// <summary>
/// Uses OpenAI to infer a brand_id and product_type_id from a free-text product
/// name. Used by the "Add Product" candidate pipeline when the extension's
/// scraped brand text either doesn't exist or doesn't map cleanly to a brand
/// record, and to populate product_type_id (never scraped from the page).
/// </summary>
public interface IProductMetadataInferenceService
{
    Task<ProductMetadataInferenceResult> InferAsync(
        string productName,
        string? scrapedBrandText,
        CancellationToken ct);
}

public sealed class ProductMetadataInferenceResult
{
    public int? BrandId { get; init; }
    public int? ProductTypeId { get; init; }
    public decimal Confidence { get; init; }
    public string? Reason { get; init; }
}
