using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models;

[Table("product_search_alias")]
public class ProductSearchAliasUpdateRow : BaseModel
{
    [PrimaryKey("id")]
    public long Id { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; }
}
