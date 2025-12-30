using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("user_privacy_preference")]
    public class UserPrivacyPreference : BaseModel
    {
        [PrimaryKey("id")]
        public int Id { get; set; }

        [Column("user_id")]
        public int UserId { get; set; }

        [Column("performance")]
        public bool Performance { get; set; }

        [Column("analytics")]
        public bool Analytics { get; set; }

        [Column("advertising")]
        public bool Advertising { get; set; }

        [Column("sale_share_opt_out")]
        public bool SaleShareOptOut { get; set; }

        [Column("updated_at")]
        public DateTime UpdatedAt { get; set; }
    }
}