using CartSmart.API.Models;
using Microsoft.Extensions.Logging;

namespace CartSmart.API.Services;

/// <summary>
/// Implements social card generation orchestration.
/// Manages the flow of generating card images via Playwright, persisting to Supabase,
/// and handling retry logic and error reporting.
/// </summary>
public sealed class SocialCardOrchestrator : ISocialCardOrchestrator
{
    private readonly ISocialCardImageService _cardImageService;
    private readonly ISupabaseService _supabaseService;
    private readonly ILogger<SocialCardOrchestrator> _logger;

    public SocialCardOrchestrator(
        ISocialCardImageService cardImageService,
        ISupabaseService supabaseService,
        ILogger<SocialCardOrchestrator> logger)
    {
        _cardImageService = cardImageService ?? 
            throw new ArgumentNullException(nameof(cardImageService));
        _supabaseService = supabaseService ?? 
            throw new ArgumentNullException(nameof(supabaseService));
        _logger = logger ?? 
            throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SocialCardGenerationResult> ProcessSocialCardAsync(
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
        CancellationToken ct = default)
    {
        try
        {
            if (socialPostId <= 0)
            {
                var error = "Invalid social_post_id provided for card generation";
                _logger.LogWarning("SocialCardOrchestrator: {Error}", error);
                return new SocialCardGenerationResult(socialPostId, false, ErrorMessage: error);
            }

            _logger.LogInformation(
                "SocialCardOrchestrator: generating card for post {PostId}, product '{ProductName}'",
                socialPostId, productName);

            // Build card data from parameters
            var cardData = new SocialCardData(
                ProductName: productName,
                ProductImageUrl: productImageUrl,
                CurrentPrice: currentPrice,
                OriginalPrice: originalPrice,
                DealTypeId: dealTypeId,
                DealTypeName: dealTypeName,
                CouponCode: couponCode,
                StoreName: storeName,
                StoreImageUrl: storeImageUrl,
                ConditionName: conditionName,
                VariantDetails: variantDetails,
                ItemCount: itemCount,
                FreeShipping: freeShipping);

            // Generate card image (PNG bytes)
            var cardBytes = await _cardImageService.GenerateAsync(cardData, ct);
            
            if (cardBytes is not { Length: > 0 })
            {
                var error = "Card image generation returned no data";
                _logger.LogWarning(
                    "SocialCardOrchestrator: {Error} for post {PostId}",
                    error, socialPostId);
                return new SocialCardGenerationResult(socialPostId, false, ErrorMessage: error);
            }

            // Convert PNG bytes to base64 data-URI
            var dataUri = "data:image/png;base64," + Convert.ToBase64String(cardBytes);
            _logger.LogInformation(
                "SocialCardOrchestrator: generated {Bytes} bytes, data-URI length: {UriLength}",
                cardBytes.Length, dataUri.Length);

            // Update social_post record with the image data-URI
            var client = _supabaseService.GetServiceRoleClient();
            var post = await client.From<SocialPost>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, socialPostId.ToString())
                .Single();

            if (post == null)
            {
                var error = $"Social post {socialPostId} not found in database";
                _logger.LogWarning("SocialCardOrchestrator: {Error}", error);
                return new SocialCardGenerationResult(socialPostId, false, ErrorMessage: error);
            }

            post.ImageUrl = dataUri;
            await client.From<SocialPost>().Upsert(post);

            _logger.LogInformation(
                "SocialCardOrchestrator: successfully generated and persisted card image for post {PostId}",
                socialPostId);

            return new SocialCardGenerationResult(
                socialPostId,
                Success: true,
                ImageDataUri: dataUri,
                GeneratedAt: DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "SocialCardOrchestrator: unexpected error processing card for post {PostId}",
                socialPostId);

            return new SocialCardGenerationResult(
                socialPostId,
                Success: false,
                ErrorMessage: $"Exception: {ex.Message}");
        }
    }
}
