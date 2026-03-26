using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    /// <summary>
    /// Insert-only model for ingest_log.
    /// Omits created_at (DEFAULT now()) so the database default applies.
    /// </summary>
    [Table("ingest_log")]
    public class IngestLog : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("product_id")]
        public int ProductId { get; set; }

        [Column("store_item_id")]
        public string? StoreItemId { get; set; }

        [Column("title")]
        public string? Title { get; set; }

        [Column("short_description")]
        public string? ShortDescription { get; set; }

        [Column("price")]
        public decimal? Price { get; set; }

        [Column("outcome")]
        public string Outcome { get; set; } = string.Empty; // "added", "updated", "ignored"

        [Column("deal_product_id")]
        public int? DealProductId { get; set; }

        [Column("ignore_reason")]
        public string? IgnoreReason { get; set; }
    }
}
