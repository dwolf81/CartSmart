using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models;

[Table("manual_price_task")]
public class ManualPriceTaskInsertRow : BaseModel
{
    [Column("deal_product_id")]
    public int DealProductId { get; set; }

    [Column("url")]
    public string Url { get; set; } = string.Empty;

    [Column("reason")]
    public string Reason { get; set; } = "bot_protection";

    [Column("status")]
    public string Status { get; set; } = "pending";
}
