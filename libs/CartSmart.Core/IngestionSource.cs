using Newtonsoft.Json;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("ingestion_source")]
    public class IngestionSource : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Column("name")]
        public string Name { get; set; } = string.Empty;

        [Column("source_type")]
        public string SourceType { get; set; } = string.Empty;

        [Column("config")]
        [JsonConverter(typeof(JsonStringOrObjectConverter))]
        public string Config { get; set; } = "{}";

        [Column("enabled")]
        public bool Enabled { get; set; } = true;

        [Column("store_id")]
        public int? StoreId { get; set; }

        [Column("poll_interval_minutes")]
        public int PollIntervalMinutes { get; set; } = 30;

        [Column("last_polled_at")]
        public DateTime? LastPolledAt { get; set; }
    }
}
