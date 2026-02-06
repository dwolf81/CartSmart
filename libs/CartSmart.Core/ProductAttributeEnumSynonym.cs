using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("product_attribute_enum_synonym")]
    public class ProductAttributeEnumSynonym : BaseModel
    {
        [PrimaryKey("id")]
        public long Id { get; set; }

        [Column("product_id")]
        public long ProductId { get; set; }

        [Column("attribute_id")]
        public int AttributeId { get; set; }

        [Column("enum_value_id")]
        public int EnumValueId { get; set; }

        [Column("synonym")]
        public string Synonym { get; set; } = string.Empty;

        [Column("normalized_synonym")]
        public string NormalizedSynonym { get; set; } = string.Empty;

        [Column("is_active")]
        public bool IsActive { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
    }
}
