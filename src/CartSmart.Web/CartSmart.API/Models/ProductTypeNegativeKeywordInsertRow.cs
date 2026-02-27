using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models;

[Table("product_type_negative_keyword")]
public class ProductTypeNegativeKeywordInsertRow : BaseModel
{
    [Column("product_type_id")]
    public long ProductTypeId { get; set; }

    [Column("keyword")]
    public string Keyword { get; set; } = string.Empty;

    [Column("is_active")]
    public bool IsActive { get; set; } = true;
}
