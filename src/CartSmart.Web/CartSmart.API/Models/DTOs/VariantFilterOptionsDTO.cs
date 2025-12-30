namespace CartSmart.API.Models.DTOs;

public sealed class VariantFilterOptionsDTO
{
    // Attributes (driven by product type) and their enum options.
    public List<VariantFilterAttributeDTO> Attributes { get; set; } = new();

    // Lightweight mapping so the client can filter deals by product_variant_id.
    // (No need to return the full product_variant rows.)
    public List<VariantAttributeValueDTO> VariantAttributeValues { get; set; } = new();
}

public sealed class VariantFilterAttributeDTO
{
    public int AttributeId { get; set; }
    public string AttributeKey { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<VariantFilterEnumOptionDTO> Options { get; set; } = new();
}

public sealed class VariantFilterEnumOptionDTO
{
    public int Id { get; set; }
    public string EnumKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

public sealed class VariantAttributeValueDTO
{
    public long ProductVariantId { get; set; }
    public int AttributeId { get; set; }
    public int? EnumValueId { get; set; }
}
