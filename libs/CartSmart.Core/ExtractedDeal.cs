using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("extracted_deal")]
    public class ExtractedDeal : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Column("raw_signal_id")]
        public long RawSignalId { get; set; }

        [Column("product_id")]
        public long? ProductId { get; set; }

        [Column("store_id")]
        public int? StoreId { get; set; }

        [Column("deal_id")]
        public long? DealId { get; set; }

        [Column("title")]
        public string Title { get; set; } = string.Empty;

        [Column("price")]
        public decimal? Price { get; set; }

        [Column("currency")]
        public string? Currency { get; set; } = "USD";

        [Column("coupon_code")]
        public string? CouponCode { get; set; }

        [Column("url")]
        public string? Url { get; set; }

        [Column("discount_percent")]
        public int? DiscountPercent { get; set; }

        [Column("deal_type_id")]
        public int? DealTypeId { get; set; } = 1;

        [Column("expiration_date")]
        public DateTime? ExpirationDate { get; set; }

        [Column("confidence_score")]
        public decimal ConfidenceScore { get; set; }

        [Column("ai_reasoning")]
        public string? AiReasoning { get; set; }

        [Column("store_wide")]
        public bool StoreWide { get; set; }

        [Column("status")]
        public string Status { get; set; } = "pending_review";

        [Column("reviewed_by")]
        public long? ReviewedBy { get; set; }

        [Column("reviewed_at")]
        public DateTime? ReviewedAt { get; set; }

        [Column("imported_at")]
        public DateTime? ImportedAt { get; set; }
    }
}
