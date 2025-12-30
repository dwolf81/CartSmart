using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("user_login")]
    public class UserLogin:BaseModel
    {
        [PrimaryKey("id")]
        public int Id { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("userId")]
        public int UserId { get; set; }

        [Column("ip_address")]
        public string? IPAddress { get; set; }

        // Navigation property
        public User User { get; set; }
    }
} 