using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("user_token")]
    public class UserToken : BaseModel
    {
        [PrimaryKey("id")]
        public int Id { get; set; }

        [Column("user_id")]
        public int UserId { get; set; }

        // "email-confirm" | "pwd-reset"
        [Column("type")]
        public string Type { get; set; } = default!;

        [Column("token")]
        public string Token { get; set; } = default!;

        [Column("expires_utc")]
        public DateTime ExpiresUtc { get; set; }

        [Column("used")]
        public bool Used { get; set; }

        [Column("created_utc")]
        public DateTime CreatedUtc { get; set; }
    }
}