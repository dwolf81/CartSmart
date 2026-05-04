using System.Text.Json.Serialization;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models;

[Table("social_post")]
public class SocialPost : BaseModel
{
    [PrimaryKey("id")]
    [JsonIgnore]
    public long Id { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("deal_id")]
    public int DealId { get; set; }

    [Column("product_id")]
    public int ProductId { get; set; }

    [Column("product_name")]
    public string? ProductName { get; set; }

    [Column("product_image")]
    public string? ProductImage { get; set; }

    [Column("current_price")]
    public decimal CurrentPrice { get; set; }

    [Column("original_price")]
    public decimal? OriginalPrice { get; set; }

    [Column("deal_url")]
    public string? DealUrl { get; set; }

    /// <summary>
    /// Status values: pending_approval | approved | posted | rejected
    /// </summary>
    [Column("status")]
    public string Status { get; set; } = "pending_approval";

    [Column("scheduled_date")]
    public DateTime? ScheduledDate { get; set; }

    [Column("posted_at")]
    public DateTime? PostedAt { get; set; }

    [Column("is_weekly")]
    public bool IsWeekly { get; set; }

    [Column("image_url")]
    public string? ImageUrl { get; set; }

    [Column("admin_notes")]
    public string? AdminNotes { get; set; }
}
