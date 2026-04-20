using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("product_store_page")]
    public class ProductStorePage : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("product_id")]
        public int ProductId { get; set; }

        [Column("store_id")]
        public int StoreId { get; set; }

        [Column("url")]
        public string Url { get; set; } = string.Empty;

        [Column("enabled")]
        public bool Enabled { get; set; } = true;

        [Column("last_scraped_at")]
        public DateTime? LastScrapedAt { get; set; }

        [Column("scrape_interval_minutes")]
        public int ScrapeIntervalMinutes { get; set; } = 120;

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Insert-only row (omits server-generated fields).
    /// </summary>
    [Table("product_store_page")]
    public class ProductStorePageInsertRow : BaseModel
    {
        [Column("product_id")]
        public int ProductId { get; set; }

        [Column("store_id")]
        public int StoreId { get; set; }

        [Column("url")]
        public string Url { get; set; } = string.Empty;

        [Column("enabled")]
        public bool Enabled { get; set; } = true;

        [Column("scrape_interval_minutes")]
        public int ScrapeIntervalMinutes { get; set; } = 120;
    }

    /// <summary>
    /// Update row (requires id for filtering).
    /// </summary>
    [Table("product_store_page")]
    public class ProductStorePageUpdateRow : BaseModel
    {
        [PrimaryKey("id")]
        public long Id { get; set; }

        [Column("url")]
        public string Url { get; set; } = string.Empty;

        [Column("enabled")]
        public bool Enabled { get; set; } = true;

        [Column("scrape_interval_minutes")]
        public int ScrapeIntervalMinutes { get; set; } = 120;
    }
}
