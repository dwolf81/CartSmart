using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("site_income")]
    public class SiteIncome:BaseModel
    {
        [PrimaryKey("id")]
        public int Id { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("month_no")]
        public int MonthNo { get; set; }

        [Column("year")]
        public int Year { get; set; }

        [Column("amount")]
        public float Amount { get; set; }      

        [Column("total_points")]
        public int TotalPoints { get; set; }
    }
} 