using Supabase.Postgrest.Models;
using Supabase.Postgrest.Attributes;

namespace CartSmart.API.Models
{
    [Table("notification_types")]
    public class NotificationType : BaseModel
    {
        [PrimaryKey("id")]
        public int Id { get; set; }

        [Column("code")]
        public string Code { get; set; } = "";

        [Column("description")]
        public string Description { get; set; } = "";
    }
}