using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models;

[Table("deal_product_price_history")]
public class DealProductPriceHistory : BaseModel
{
    [PrimaryKey("id")] public int Id { get; set; }
    [Column("deal_product_id")] public int DealProductId { get; set; }
    [Column("price")] public decimal Price { get; set; }
    [Column("currency")] public string? Currency { get; set; }
    [Column("changed_at")] public DateTime ChangedAt { get; set; }
}