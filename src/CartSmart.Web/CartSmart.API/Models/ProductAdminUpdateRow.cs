using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models;

[Table("product")]
public class ProductAdminUpdateRow : BaseModel
{
    [PrimaryKey("id")]
    public int Id { get; set; }

    [Column("slug")]
    public string? Slug { get; set; }

    [Column("name")]
    public string? Name { get; set; }

    [Column("msrp")]
    public float? MSRP { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("brand_id")]
    public int? BrandId { get; set; }

    [Column("enable_service")]
    public bool? EnableService { get; set; }
}
