using System.Text.Json.Serialization;

namespace CartSmart.API.Models.DTOs;

/// <summary>
/// Message format for Azure Queue Storage: social card generation requests.
/// Enqueued when a social post is created and needs a card image generated.
/// </summary>
public sealed record SocialCardQueueMessage
{
    [JsonPropertyName("social_post_id")]
    public long SocialPostId { get; init; }

    [JsonPropertyName("product_name")]
    public string ProductName { get; init; } = string.Empty;

    [JsonPropertyName("product_image_url")]
    public string? ProductImageUrl { get; init; }

    [JsonPropertyName("current_price")]
    public decimal CurrentPrice { get; init; }

    [JsonPropertyName("original_price")]
    public decimal? OriginalPrice { get; init; }

    [JsonPropertyName("deal_type_id")]
    public int? DealTypeId { get; init; }

    [JsonPropertyName("deal_type_name")]
    public string? DealTypeName { get; init; }

    [JsonPropertyName("coupon_code")]
    public string? CouponCode { get; init; }

    [JsonPropertyName("store_name")]
    public string? StoreName { get; init; }

    [JsonPropertyName("store_image_url")]
    public string? StoreImageUrl { get; init; }

    [JsonPropertyName("condition_name")]
    public string? ConditionName { get; init; }

    [JsonPropertyName("variant_details")]
    public string? VariantDetails { get; init; }

    [JsonPropertyName("item_count")]
    public int? ItemCount { get; init; }

    [JsonPropertyName("free_shipping")]
    public bool FreeShipping { get; init; }

    [JsonPropertyName("retry_count")]
    public int RetryCount { get; init; } = 0;
}
