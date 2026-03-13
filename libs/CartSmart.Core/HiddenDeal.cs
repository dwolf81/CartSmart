using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("hidden_deal")]
    public class HiddenDeal : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("user_id")]
        public int UserId { get; set; }

        [Column("deal_id")]
        public long DealId { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
    }
}
