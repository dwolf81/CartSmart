using Supabase.Postgrest.Models;
using Supabase.Postgrest.Attributes;

namespace CartSmart.API.Models
{
    [Table("notifications")]
    public class Notification : BaseModel
    {
        [PrimaryKey("id")]
        public long Id { get; set; }

        [Column("user_id")]
        public long UserId { get; set; }

        [Column("type_id")]
        public int TypeId { get; set; }

        [Column("message")]
        public string Message { get; set; } = "";

        [Column("link_url")]
        public string? LinkUrl { get; set; }

        [Column("is_read")]
        public bool IsRead { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
    }
}