using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
[Table("deal_click")]
public class DealClick:BaseModel
{
    [PrimaryKey("id")]
    public int id { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("deal_id")]
    public int DealId { get; set; }

    [Column("product_id")]
    public int? ProductId { get; set; }

    [Column("user_id")]
    public int UserId { get; set; }

    [Column("external")]
    public bool External { get; set; }
}
} 