using Newtonsoft.Json;
using System.Text.Json.Serialization;

namespace CartSmart.API.Models.DTOs
{
    public class AdminProductEditResponseDTO
    {
        [JsonProperty("product")]
        [JsonPropertyName("product")]
        public AdminProductDTO Product { get; set; } = new();

        // Product-specific attributes are driven by the product_attribute table.
        [JsonProperty("attributes")]
        [JsonPropertyName("attributes")]
        public List<AdminProductAttributeDTO> Attributes { get; set; } = new();

        // Catalog used to add new attributes to the product.
        [JsonProperty("availableAttributes")]
        [JsonPropertyName("availableAttributes")]
        public List<AdminAttributeCatalogItemDTO> AvailableAttributes { get; set; } = new();
    }

    public class AdminProductDTO
    {
        [JsonProperty("id")]
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonProperty("msrp")]
        [JsonPropertyName("msrp")]
        public float? Msrp { get; set; }

        [JsonProperty("description")]
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonProperty("slug")]
        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonProperty("imageUrl")]
        [JsonPropertyName("imageUrl")]
        public string? ImageUrl { get; set; }

        [JsonProperty("brandId")]
        [JsonPropertyName("brandId")]
        public int? BrandId { get; set; }

        [JsonProperty("searchAliases")]
        [JsonPropertyName("searchAliases")]
        public List<string> SearchAliases { get; set; } = new();

        [JsonProperty("negativeKeywords")]
        [JsonPropertyName("negativeKeywords")]
        public List<string> NegativeKeywords { get; set; } = new();
    }

    public class AdminUpdateProductRequestDTO
    {
        [JsonProperty("name")]
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonProperty("msrp")]
        [JsonPropertyName("msrp")]
        public float? Msrp { get; set; }

        [JsonProperty("description")]
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonProperty("brandId")]
        [JsonPropertyName("brandId")]
        public int? BrandId { get; set; }

        // Optional. If provided (even empty), replaces the current alias set.
        [JsonProperty("searchAliases")]
        [JsonPropertyName("searchAliases")]
        public List<string>? SearchAliases { get; set; }

        // Optional. If provided (even empty), replaces the current negative keyword set.
        [JsonProperty("negativeKeywords")]
        [JsonPropertyName("negativeKeywords")]
        public List<string>? NegativeKeywords { get; set; }
    }

    public class AdminCreateProductRequestDTO
    {
        [JsonProperty("name")]
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonProperty("msrp")]
        [JsonPropertyName("msrp")]
        public float? Msrp { get; set; }

        [JsonProperty("description")]
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonProperty("productTypeId")]
        [JsonPropertyName("productTypeId")]
        public int ProductTypeId { get; set; }

        [JsonProperty("brandId")]
        [JsonPropertyName("brandId")]
        public int? BrandId { get; set; }

        [JsonProperty("searchAliases")]
        [JsonPropertyName("searchAliases")]
        public List<string>? SearchAliases { get; set; }

        [JsonProperty("negativeKeywords")]
        [JsonPropertyName("negativeKeywords")]
        public List<string>? NegativeKeywords { get; set; }
    }

    public class AdminCreateProductResponseDTO
    {
        [JsonProperty("id")]
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonProperty("msrp")]
        [JsonPropertyName("msrp")]
        public float? Msrp { get; set; }

        [JsonProperty("description")]
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonProperty("slug")]
        [JsonPropertyName("slug")]
        public string? Slug { get; set; }
    }

    public class AdminAttributeCatalogItemDTO
    {
        [JsonProperty("attributeId")]
        [JsonPropertyName("attributeId")]
        public int AttributeId { get; set; }

        [JsonProperty("attributeKey")]
        [JsonPropertyName("attributeKey")]
        public string AttributeKey { get; set; } = string.Empty;

        [JsonProperty("dataType")]
        [JsonPropertyName("dataType")]
        public string DataType { get; set; } = string.Empty;

        [JsonProperty("description")]
        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }

    public class AdminProductAttributeDTO
    {
        [JsonProperty("attributeId")]
        [JsonPropertyName("attributeId")]
        public int AttributeId { get; set; }

        [JsonProperty("attributeKey")]
        [JsonPropertyName("attributeKey")]
        public string AttributeKey { get; set; } = string.Empty;

        [JsonProperty("dataType")]
        [JsonPropertyName("dataType")]
        public string DataType { get; set; } = string.Empty;

        [JsonProperty("description")]
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonProperty("isRequired")]
        [JsonPropertyName("isRequired")]
        public bool IsRequired { get; set; }

        [JsonProperty("options")]
        [JsonPropertyName("options")]
        public List<AdminAttributeEnumValueDTO> Options { get; set; } = new();
    }

    public class AdminAttributeEnumValueDTO
    {
        [JsonProperty("id")]
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonProperty("enumKey")]
        [JsonPropertyName("enumKey")]
        public string EnumKey { get; set; } = string.Empty;

        [JsonProperty("displayName")]
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonProperty("sortOrder")]
        [JsonPropertyName("sortOrder")]
        public int SortOrder { get; set; }

        [JsonProperty("isActive")]
        [JsonPropertyName("isActive")]
        public bool IsActive { get; set; }

        // Product-scoped availability: if false, the enum is hidden/disabled for this product.
        // Defaults to true when not explicitly disabled.
        [JsonProperty("isEnabled")]
        [JsonPropertyName("isEnabled")]
        public bool IsEnabled { get; set; } = true;
    }

    public class AdminUpsertProductAttributeRequestDTO
    {
        [JsonProperty("attributeId")]
        [JsonPropertyName("attributeId")]
        public int AttributeId { get; set; }

        [JsonProperty("isRequired")]
        [JsonPropertyName("isRequired")]
        public bool IsRequired { get; set; }
    }

    public class AdminUpsertAttributeEnumValueRequestDTO
    {
        // Required on create. Ignored on update.
        [JsonProperty("enumKey")]
        [JsonPropertyName("enumKey")]
        public string? EnumKey { get; set; }

        [JsonProperty("displayName")]
        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonProperty("sortOrder")]
        [JsonPropertyName("sortOrder")]
        public int? SortOrder { get; set; }

        [JsonProperty("isActive")]
        [JsonPropertyName("isActive")]
        public bool? IsActive { get; set; }
    }

    public class AdminCreateAttributeRequestDTO
    {
        // Preferred input: user-friendly name/label. Server generates attribute_key.
        [JsonProperty("name")]
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        // Backward compatibility: allow clients to send attributeKey directly.
        [JsonProperty("attributeKey")]
        [JsonPropertyName("attributeKey")]
        public string? AttributeKey { get; set; }

        [JsonProperty("dataType")]
        [JsonPropertyName("dataType")]
        public string? DataType { get; set; }

        [JsonProperty("description")]
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        // If true, the attribute will be required for this product.
        [JsonProperty("isRequired")]
        [JsonPropertyName("isRequired")]
        public bool IsRequired { get; set; }
    }

    public class AdminProductVariantDTO
    {
        [JsonProperty("id")]
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonProperty("productId")]
        [JsonPropertyName("productId")]
        public long ProductId { get; set; }

        [JsonProperty("variantName")]
        [JsonPropertyName("variantName")]
        public string? VariantName { get; set; }

        [JsonProperty("unitCount")]
        [JsonPropertyName("unitCount")]
        public int? UnitCount { get; set; }

        [JsonProperty("unitType")]
        [JsonPropertyName("unitType")]
        public string? UnitType { get; set; }

        [JsonProperty("displayName")]
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonProperty("normalizedTitle")]
        [JsonPropertyName("normalizedTitle")]
        public string NormalizedTitle { get; set; } = string.Empty;

        [JsonProperty("isDefault")]
        [JsonPropertyName("isDefault")]
        public bool IsDefault { get; set; }

        [JsonProperty("isActive")]
        [JsonPropertyName("isActive")]
        public bool IsActive { get; set; }
    }

    public class AdminUpsertVariantRequestDTO
    {
        [JsonProperty("variantName")]
        [JsonPropertyName("variantName")]
        public string? VariantName { get; set; }

        [JsonProperty("unitCount")]
        [JsonPropertyName("unitCount")]
        public int? UnitCount { get; set; }

        [JsonProperty("unitType")]
        [JsonPropertyName("unitType")]
        public string? UnitType { get; set; }

        [JsonProperty("displayName")]
        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonProperty("normalizedTitle")]
        [JsonPropertyName("normalizedTitle")]
        public string? NormalizedTitle { get; set; }

        [JsonProperty("isDefault")]
        [JsonPropertyName("isDefault")]
        public bool? IsDefault { get; set; }

        [JsonProperty("isActive")]
        [JsonPropertyName("isActive")]
        public bool? IsActive { get; set; }
    }
}
