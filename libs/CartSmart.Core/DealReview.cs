using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("deal_review")]
    public class DealReview : BaseModel
    {
        [PrimaryKey("id")]
        public int Id { get; set; }

        [Column("user_id")]
        public int UserId { get; set; }

        [Column("deal_id")]
        public int DealId { get; set; }

        [Column("deal_product_id")]
        public int DealProductId { get; set; }

        [Column("deal_status_id")]
        public int DealStatusId { get; set; }

        [Column("comments")]
        public string? Comments { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("deal_issue_type_id")]
        public int? DealIssueTypeId { get; set; }
    }
}