using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("product_attribute")]
    public class ProductAttribute : BaseModel
    {
        // Supabase.Postgrest PrimaryKeyAttribute only supports a single column.
        // This table is composite-keyed (product_id, attribute_id) in Postgres.
        [PrimaryKey("product_id")]
        [Column("product_id")]
        public int ProductId { get; set; }

        [Column("attribute_id")]
        public int AttributeId { get; set; }

        [Column("is_required")]
        public bool IsRequired { get; set; }
    }
}
