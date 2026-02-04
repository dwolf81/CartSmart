using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models;

[Table("product_negative_keyword")]
public class ProductNegativeKeywordInsertRow : BaseModel
{
    [Column("product_id")]
    public long ProductId { get; set; }

    [Column("keyword")]
    public string Keyword { get; set; } = string.Empty;

    [Column("is_active")]
    public bool IsActive { get; set; } = true;
}
