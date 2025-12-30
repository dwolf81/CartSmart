using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.Core.Worker;

[Table("deal_price_history")] // assume table name; adjust if different
public class DealPriceHistory : BaseModel
{
    [PrimaryKey("id")]
    public int Id { get; set; }

    [Column("deal_id")] public int DealId { get; set; }
    [Column("price")] public decimal Price { get; set; }
    [Column("currency")] public string? Currency { get; set; }
    [Column("changed_at")] public DateTime ChangedAt { get; set; }
}