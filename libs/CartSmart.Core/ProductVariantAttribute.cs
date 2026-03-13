using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("product_variant_attribute")]
    public class ProductVariantAttribute : BaseModel
    {
        // Composite PK: (product_variant_id, attribute_id, enum_value_id) in Postgres.
        // Supabase.Postgrest PrimaryKeyAttribute only supports a single column.
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
