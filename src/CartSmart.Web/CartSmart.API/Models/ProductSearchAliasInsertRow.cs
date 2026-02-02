using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models;

[Table("product_search_alias")]
public class ProductSearchAliasInsertRow : BaseModel
{
    [Column("product_id")]
    public long ProductId { get; set; }

    [Column("alias")]
    public string Alias { get; set; } = string.Empty;

    [Column("is_active")]
    public bool IsActive { get; set; } = true;
}
