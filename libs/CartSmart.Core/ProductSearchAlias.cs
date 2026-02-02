using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("product_search_alias")]
    public class ProductSearchAlias : BaseModel
    {
        [PrimaryKey("id")]
        public long Id { get; set; }

        [Column("product_id")]
        public long ProductId { get; set; }

        [Column("alias")]
        public string Alias { get; set; } = string.Empty;

        [Column("normalized_alias")]
        public string NormalizedAlias { get; set; } = string.Empty;

        [Column("is_active")]
        public bool IsActive { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
    }
}
