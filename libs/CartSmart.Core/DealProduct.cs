using System.Text.Json.Serialization;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("deal_product")]
    public class DealProduct : BaseModel
    {
        [PrimaryKey("id")]
        [JsonIgnore]
        public int Id { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("product_id")]
        public int ProductId { get; set; }
                
        [Column("product_variant_id")]
        public int ProductVariantId { get; set; }

        [Column("price")]
        public decimal Price { get; set; }

        [Column("url")]
        public string? Url { get; set; }

        [Column("deleted")]
        public bool Deleted { get; set; }

        [Column("deal_id")]
        public int DealId { get; set; }

        [Column("deal_status_id")]
        public int DealStatusId { get; set; }

        [Column("condition_id")]
        public int? ConditionId { get; set; }

        [Column("free_shipping")]
        public bool FreeShipping { get; set; }

        [Column("primary")]
        public bool Primary { get; set; }

        [Column("last_checked_at")]
        public DateTime? LastCheckedAt { get; set; }

        [Column("next_check_at")]
        public DateTime? NextCheckAt { get; set; }

        [Column("error_count")]
        public int? ErrorCount { get; set; }

        [Column("stale_at")]
        public DateTime? StaleAt { get; set; }

        [Column("store_item_id")]
        public string? StoreItemId { get; set; }

    }
}