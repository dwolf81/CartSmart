using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;


namespace CartSmart.API.Models
{

    [Table("review_site")]
    public class ReviewSite:BaseModel
    {
        [PrimaryKey("id")]
        public int Id { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("name")]
        public string? Name { get; set; }

        [Column("url")]
        public string? URL { get; set; }

    }
} 