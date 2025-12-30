using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("deal_combo")]
    public class DealCombo : BaseModel
    {
        [PrimaryKey("id")]
        public int id { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("deal_id")]
        public int DealId { get; set; }

        [Column("combo_deal_id")]
        public int ComboDealId { get; set; }
    
        [Column("order")]
        public int? Order { get; set; }
}
} 