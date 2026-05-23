using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("store_scan_endpoint")]
    public class StoreScanEndpoint : BaseModel
    {
        [PrimaryKey("id", false)]
        public int Id { get; set; }

        [Column("store_id")]
        public int StoreId { get; set; }

        [Column("url")]
        public string Url { get; set; } = string.Empty;

        [Column("label")]
        public string? Label { get; set; }

        [Column("product_type_id")]
        public int? ProductTypeId { get; set; }

        [Column("is_active")]
        public bool IsActive { get; set; } = true;

        [Column("last_crawled_at")]
        public DateTime? LastCrawledAt { get; set; }

        [Column("last_result_count")]
        public int? LastResultCount { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
    }

    [Table("store_scan_endpoint")]
    public class StoreScanEndpointInsertRow : BaseModel
    {
        [Column("store_id")]
        public int StoreId { get; set; }

        [Column("url")]
        public string Url { get; set; } = string.Empty;

        [Column("label")]
        public string? Label { get; set; }

        [Column("product_type_id")]
        public int? ProductTypeId { get; set; }

        [Column("is_active")]
        public bool IsActive { get; set; } = true;
    }

    [Table("store_scan_endpoint")]
    public class StoreScanEndpointUpdateRow : BaseModel
    {
        [PrimaryKey("id")]
        public int Id { get; set; }

        [Column("url")]
        public string Url { get; set; } = string.Empty;

        [Column("label")]
        public string? Label { get; set; }

        [Column("product_type_id")]
        public int? ProductTypeId { get; set; }

        [Column("is_active")]
        public bool IsActive { get; set; } = true;
    }
}
