using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("product_variant_attribute")]
    public class ProductVariantAttribute : BaseModel
    {
        // Supabase.Postgrest PrimaryKeyAttribute only supports a single column.
        // This table is composite-keyed (product_variant_id, attribute_id) in Postgres.
        [PrimaryKey("product_variant_id")]
        [Column("product_variant_id")]
        public long ProductVariantId { get; set; }

        [Column("attribute_id")]
        public int AttributeId { get; set; }

        [Column("enum_value_id")]
        public int? EnumValueId { get; set; }

        [Column("value_num")]
        public decimal? ValueNum { get; set; }

        [Column("value_text")]
        public string? ValueText { get; set; }

        [Column("value_bool")]
        public bool? ValueBool { get; set; }
    }
}
