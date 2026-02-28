namespace CartSmart.API.Models.DTOs;

public sealed class DealVariantOptionDTO
{
    public long DealProductId { get; set; }
    public long? ProductVariantId { get; set; }
    public string? Url { get; set; }
    public string? VariantDetails { get; set; }
    public int? ConditionId { get; set; }
    public decimal? Price { get; set; }
}
