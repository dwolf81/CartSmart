using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("product_variant")]
    public class ProductVariant : BaseModel
    {
        [PrimaryKey("id")]
        public long Id { get; set; }

        [Column("product_id")]
        public long ProductId { get; set; }

        [Column("variant_name")]
        public string? VariantName { get; set; }

        [Column("unit_count")]
        public int? UnitCount { get; set; }

        [Column("unit_type")]
        public string? UnitType { get; set; }

        [Column("display_name")]
        public string DisplayName { get; set; } = string.Empty;

        [Column("normalized_title")]
        public string NormalizedTitle { get; set; } = string.Empty;

        [Column("is_default")]
        public bool IsDefault { get; set; }

        [Column("is_active")]
        public bool IsActive { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("updated_at")]
        public DateTime UpdatedAt { get; set; }
    }
}
