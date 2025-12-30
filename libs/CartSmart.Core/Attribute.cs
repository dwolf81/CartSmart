using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("attribute")]
    public class Attribute : BaseModel
    {
        [PrimaryKey("id")]
        public int Id { get; set; }

        [Column("attribute_key")]
        public string AttributeKey { get; set; } = string.Empty;

        [Column("data_type")]
        public string DataType { get; set; } = string.Empty;

        [Column("description")]
        public string? Description { get; set; }
    }
}
