using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("raw_signal")]
    public class RawSignal : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Column("ingestion_source_id")]
        public long IngestionSourceId { get; set; }

        [Column("external_id")]
        public string? ExternalId { get; set; }

        [Column("title")]
        public string? Title { get; set; }

        [Column("body")]
        public string? Body { get; set; }

        [Column("url")]
        public string? Url { get; set; }

        [Column("author")]
        public string? Author { get; set; }

        [Column("raw_json")]
        public string? RawJson { get; set; }

        [Column("status")]
        public string Status { get; set; } = "pending";

        [Column("error_message")]
        public string? ErrorMessage { get; set; }

        [Column("processed_at")]
        public DateTime? ProcessedAt { get; set; }
    }
}
