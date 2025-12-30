using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("product_type_attribute")]
    public class ProductTypeAttribute : BaseModel
    {
        // Supabase.Postgrest PrimaryKeyAttribute only supports a single column.
        // This table is composite-keyed (product_type_id, attribute_id) in Postgres.
        [PrimaryKey("product_type_id")]
        [Column("product_type_id")]
        public int ProductTypeId { get; set; }

        [Column("attribute_id")]
        public int AttributeId { get; set; }

        [Column("is_required")]
        public bool IsRequired { get; set; }
    }
}
