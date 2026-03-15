using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    /// <summary>
    /// Insert-only model for scrape_log.
    /// Omits id (GENERATED ALWAYS) and created_at (DEFAULT now())
    /// so the database defaults apply.
    /// </summary>
    [Table("scrape_log")]
    public class ScrapeLogInsert : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("store_id")]
        public int StoreId { get; set; }

        [Column("deal_product_id")]
        public long? DealProductId { get; set; }

        [Column("url")]
        public string Url { get; set; } = string.Empty;

        [Column("method")]
        public string Method { get; set; } = string.Empty;

        [Column("success")]
        public bool Success { get; set; }

        [Column("price")]
        public decimal? Price { get; set; }

        [Column("currency")]
        public string? Currency { get; set; }

        [Column("error_message")]
        public string? ErrorMessage { get; set; }
    }
}
