using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models;

[Table("product_negative_keyword")]
public class ProductNegativeKeywordUpdateRow : BaseModel
{
    [PrimaryKey("id")]
    public long Id { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; }
}
