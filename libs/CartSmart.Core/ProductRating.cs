using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("product_rating")]
    public class ProductRating : BaseModel
    {
        [PrimaryKey("id")]
        public int Id { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("user_id")]
        public int UserId { get; set; }

        [Column("product_id")]
        public int ProductId { get; set; }

        [Column("url")]
        public string? URL { get; set; }

        [Column("title")]
        public string? Title { get; set; }

        [Column("rating")]
        public int? Rating { get; set; }

        [Column("review_site_id")]
        public int? ReviewSiteId { get; set; }

        // Navigation property
        public User User { get; set; }

        public Product Product { get; set; }
        
        public ReviewSite ReviewSite { get; set; }
    }
} 