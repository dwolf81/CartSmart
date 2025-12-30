using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("product_type")]
    public class ProductType:BaseModel
    {
        [PrimaryKey("id")]
        public int Id { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("name")]
        public string? Name { get; set; }

    }
}
