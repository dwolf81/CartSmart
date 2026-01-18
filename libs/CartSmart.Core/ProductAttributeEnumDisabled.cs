using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("product_attribute_enum_disabled")]
    public class ProductAttributeEnumDisabled : BaseModel
    {
        // Supabase.Postgrest PrimaryKeyAttribute only supports a single column.
        // This table is composite-keyed (product_id, enum_value_id) in Postgres.
        [PrimaryKey("product_id")]
        [Column("product_id")]
        public int ProductId { get; set; }

        [Column("attribute_id")]
        public int AttributeId { get; set; }

        [Column("enum_value_id")]
        public int EnumValueId { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
    }
}
