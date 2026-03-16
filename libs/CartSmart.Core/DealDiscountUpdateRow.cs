using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    /// <summary>
    /// Minimal update model for deal discount_percent changes.
    /// Only includes the discount_percent column to avoid triggering
    /// column-level PostgreSQL triggers on deal_status_id or deleted,
    /// which would cause the storewide/stacked upsert functions to fire
    /// and create duplicate deal_product rows.
    /// </summary>
    [Table("deal")]
    public class DealDiscountUpdateRow : BaseModel
    {
        [PrimaryKey("id")]
        public int Id { get; set; }

        [Column("discount_percent")]
        public int? DiscountPercent { get; set; }
    }
}
