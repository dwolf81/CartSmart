using CartSmart.API.Models;

namespace CartSmart.API.Services;

public interface ISocialPostService
{
    /// <summary>Returns a paged list of posts, optionally filtered by status.</summary>
    Task<IReadOnlyList<SocialPostDto>> GetPostsAsync(string? status, int page, int limit, CancellationToken ct = default);

    /// <summary>Returns a single post with all caption variations.</summary>
    Task<SocialPostDto?> GetPostAsync(long id, CancellationToken ct = default);

    /// <summary>Approves a post, marking the given caption as selected.</summary>
    Task<bool> ApproveAsync(long id, long? captionId, string? adminNotes, CancellationToken ct = default);

    /// <summary>Rejects a post.</summary>
    Task<bool> RejectAsync(long id, string? adminNotes, CancellationToken ct = default);

    /// <summary>Updates the text of a caption variation.</summary>
    Task<bool> UpdateCaptionAsync(long postId, long captionId, string newText, CancellationToken ct = default);

    /// <summary>Deletes a social post and all caption rows (cascade).</summary>
    Task<bool> DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Posts an approved post immediately to all configured platforms,
    /// then marks it as posted.
    /// </summary>
    Task<PostNowResult> PostNowAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Selects the top deals for today and generates caption variations.
    /// Called by the daily Azure Function and by the manual trigger endpoint.
    /// </summary>
    Task<int> GenerateDailyPostsAsync(SocialPostGenerationOptions? options = null, CancellationToken ct = default);

    /// <summary>Generates one "Best Deals of the Week" digest post.</summary>
    Task<bool> GenerateWeeklyDigestAsync(CancellationToken ct = default);

    /// <summary>
    /// Renders a deal card image for the given post. Returns the PNG bytes, or null on failure.
    /// Also persists the base64 data-URI to <c>social_post.image_url</c>.
    /// </summary>
    Task<byte[]?> GenerateCardImageAsync(long postId, CancellationToken ct = default);
}

public record SocialPostDto(
    long Id,
    int DealId,
    int ProductId,
    string ProductName,
    string? ProductImage,
    string? CartSmartDealUrl,
    decimal CurrentPrice,
    decimal? OriginalPrice,
    string? DealUrl,
    SocialDealDetailsDto? DealDetails,
    string Status,
    DateTime? ScheduledDate,
    DateTime? PostedAt,
    bool IsWeekly,
    string? AdminNotes,
    DateTime CreatedAt,
    string? CardImageUrl,
    IReadOnlyList<SocialPostCaptionDto> Captions);

public record SocialDealDetailsDto(
    int? DealTypeId,
    string? DealTypeName,
    string? CouponCode,
    string? StoreName,
    string? StoreImageUrl,
    string? ConditionName,
    string? VariantDetails,
    int? ItemCount,
    bool FreeShipping,
    string? CartSmartDealUrl,
    string? AdditionalDetails,
    string? ExternalOfferUrl,
    string? ExternalStoreName,
    string? ExternalStoreUrl,
    IReadOnlyList<SocialDealStepDto> Steps);

public record SocialDealStepDto(
    int StepNumber,
    int DealId,
    int? DealTypeId,
    string? DealTypeName,
    string? CouponCode,
    string? AdditionalDetails,
    string? DealUrl,
    string? ExternalOfferUrl,
    string? ExternalStoreName,
    string? ExternalStoreUrl);

public record SocialPostCaptionDto(
    long Id,
    string CaptionText,
    string Platform,
    bool Selected);

public record PostNowResult(
    bool OverallSuccess,
    IReadOnlyList<PlatformResult> Platforms);

public record PlatformResult(string Platform, bool Success, bool Skipped);

public record SocialPostGenerationOptions(
    int? Count = null,
    int? MaxPerProductPerDay = null,
    IReadOnlyList<int>? DealIds = null,
    IReadOnlyList<int>? ProductIds = null,
    IReadOnlyList<int>? PriorityDealIds = null,
    IReadOnlyList<int>? PriorityProductIds = null,
    IReadOnlyList<int>? ExcludedDealIds = null,
    IReadOnlyList<int>? ExcludedProductIds = null
);
