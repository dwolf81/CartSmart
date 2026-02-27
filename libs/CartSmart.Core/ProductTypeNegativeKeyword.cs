using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("product_type_negative_keyword")]
    public class ProductTypeNegativeKeyword : BaseModel
    {
        [PrimaryKey("id")]
        public long Id { get; set; }

        [Column("product_type_id")]
        public long ProductTypeId { get; set; }

        [Column("keyword")]
        public string Keyword { get; set; } = string.Empty;

        [Column("normalized_keyword")]
        public string NormalizedKeyword { get; set; } = string.Empty;

        [Column("is_active")]
        public bool IsActive { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
    }
}
