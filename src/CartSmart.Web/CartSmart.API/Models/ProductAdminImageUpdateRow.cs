using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models;

[Table("product")]
public class ProductAdminImageUpdateRow : BaseModel
{
    [PrimaryKey("id")]
    public int Id { get; set; }

    // Preserve slug on update to avoid it being cleared.
    [Column("slug")]
    public string? Slug { get; set; }

    [Column("image_url")]
    public string? ImageUrl { get; set; }
}
