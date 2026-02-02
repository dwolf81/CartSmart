using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models;

[Table("manual_price_task")]
public class ManualPriceTaskUpdateRow : BaseModel
{
    [PrimaryKey("id")]
    public long Id { get; set; }

    [Column("status")]
    public string? Status { get; set; }

    [Column("submitted_at")]
    public DateTime? SubmittedAt { get; set; }

    [Column("submitted_price")]
    public decimal? SubmittedPrice { get; set; }

    [Column("submitted_currency")]
    public string? SubmittedCurrency { get; set; }

    [Column("submitted_in_stock")]
    public bool? SubmittedInStock { get; set; }

    [Column("submitted_sold")]
    public bool? SubmittedSold { get; set; }

    [Column("submitted_by")]
    public string? SubmittedBy { get; set; }

    [Column("notes")]
    public string? Notes { get; set; }
}
