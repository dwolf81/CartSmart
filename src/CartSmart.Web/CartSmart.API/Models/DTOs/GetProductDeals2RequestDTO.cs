using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace CartSmart.API.Models.DTOs;

public sealed class GetProductDeals2RequestDTO
{
    [JsonProperty("storeId")]
    [JsonPropertyName("storeId")]
    public long? StoreId { get; set; }

    [JsonProperty("dealTypeId")]
    [JsonPropertyName("dealTypeId")]
    public int? DealTypeId { get; set; }

    [JsonProperty("conditionId")]
    [JsonPropertyName("conditionId")]
    public int? ConditionId { get; set; }

    [JsonProperty("userId")]
    [JsonPropertyName("userId")]
    public int? UserId { get; set; }

    // List of attribute filters. Each attribute is AND'ed together; values inside each attribute are OR'ed.
    [JsonProperty("attributeFilters")]
    [JsonPropertyName("attributeFilters")]
    public List<ProductAttributeFilterDTO>? AttributeFilters { get; set; }
}

public sealed class ProductAttributeFilterDTO
{
    // Use snake_case inside the jsonb payload passed to Postgres.
    [JsonProperty("attribute_id")]
    [JsonPropertyName("attribute_id")]
    public long AttributeId { get; set; }

    [JsonProperty("enum_value_ids")]
    [JsonPropertyName("enum_value_ids")]
    public List<long> EnumValueIds { get; set; } = new();
}
