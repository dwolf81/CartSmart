using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("attribute_enum_value")]
    public class AttributeEnumValue : BaseModel
    {
        [PrimaryKey("id")]
        public int Id { get; set; }

        [Column("attribute_id")]
        public int AttributeId { get; set; }

        [Column("enum_key")]
        public string EnumKey { get; set; } = string.Empty;

        [Column("display_name")]
        public string DisplayName { get; set; } = string.Empty;

        [Column("sort_order")]
        public int SortOrder { get; set; }

        [Column("is_active")]
        public bool IsActive { get; set; }
    }
}
