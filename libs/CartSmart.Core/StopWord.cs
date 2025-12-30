using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("stop_words")]
    public class StopWord : BaseModel
    {
        [PrimaryKey("name")]
        [Column("name")]
        public string Name { get; set; } = string.Empty;

        [Column("active")]
        public bool Active { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("updated_at")]
        public DateTime? UpdatedAt { get; set; }

        [Column("product_type_id")]
        public long? ProductTypeId { get; set; }
    }
}
