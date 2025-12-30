using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;
namespace CartSmart.API.Models
{
    [Table("user_points")]
    public class UserPoints:BaseModel
    {
        [PrimaryKey("id")]
        public int Id { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("month_no")]
        public int MonthNo { get; set; }

        [Column("year")]
        public int Year { get; set; }

        [Column("points")]
        public int Points { get; set; }

        [Column("user_id")]
        public int UserId { get; set; }

        [Column("amount")]
        public float Amount { get; set; }     

        [Column("paid")]
        public bool Paid { get; set; }

    }
} 