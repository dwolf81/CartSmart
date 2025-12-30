using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("deal_issue_type")]
    public class DealIssueType:BaseModel
    {
        [PrimaryKey("id")]
        public int Id { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("name")]
        public string? Name { get; set; }
    }
}
