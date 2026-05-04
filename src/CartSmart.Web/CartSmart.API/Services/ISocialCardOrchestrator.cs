namespace CartSmart.API.Services;

/// <summary>
/// Orchestrates async social card image generation from queue messages.
/// Handles rendering, storage, and persistence of social post card images.
/// </summary>
public interface ISocialCardOrchestrator
{
    /// <summary>
    /// Processes a single social card generation request.
    /// Generates the card image, stores it, and updates the social post record.
    /// Returns the result of the operation including any errors.
    /// </summary>
    Task<SocialCardGenerationResult> ProcessSocialCardAsync(
        long socialPostId,
        string productName,
        string? productImageUrl,
        decimal currentPrice,
        decimal? originalPrice,
        int? dealTypeId,
        string? dealTypeName,
        string? couponCode,
        string? storeName,
        string? storeImageUrl,
        string? conditionName,
        string? variantDetails,
        int? itemCount,
        bool freeShipping,
        CancellationToken ct = default);
}

/// <summary>
/// Result of a social card generation operation.
/// </summary>
public sealed record SocialCardGenerationResult(
    long SocialPostId,
    bool Success,
    string ImageDataUri = "",
    string? ErrorMessage = null,
    DateTime? GeneratedAt = null);
