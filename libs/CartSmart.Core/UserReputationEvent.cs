using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("user_reputation_event")]
    public class UserReputationEvent : BaseModel
    {
        [PrimaryKey("id")]
        public long Id { get; set; }

        [Column("user_id")]
        public int UserId { get; set; }

        [Column("deal_product_id")]
        public int DealProductId { get; set; }

        [Column("event_type")]
        public string EventType { get; set; } = string.Empty;

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
    }
}
