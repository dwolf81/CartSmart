using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("deal_candidate")]
    public class DealCandidate : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("last_seen_at")]
        public DateTime LastSeenAt { get; set; }

        // extension | crawler | ai
        [Column("source")]
        public string Source { get; set; } = string.Empty;

        [Column("store_id")]
        public int StoreId { get; set; }

        [Column("product_candidate_id")]
        public long? ProductCandidateId { get; set; }

        [Column("product_id")]
        public int? ProductId { get; set; }

        [Column("deal_url_canonical")]
        public string DealUrlCanonical { get; set; } = string.Empty;

        [Column("listing_price")]
        public decimal? ListingPrice { get; set; }

        [Column("listing_currency")]
        public string? ListingCurrency { get; set; } = "USD";

        [Column("listing_msrp")]
        public decimal? ListingMsrp { get; set; }

        [Column("condition_category_id")]
        public int? ConditionCategoryId { get; set; }

        [Column("in_stock")]
        public bool? InStock { get; set; }

        [Column("raw_title")]
        public string? RawTitle { get; set; }

        [Column("raw_html_snippet")]
        public string? RawHtmlSnippet { get; set; }

        [Column("ai_confidence")]
        public decimal? AiConfidence { get; set; }

        // pending_review | approved | rejected | promoted
        [Column("status")]
        public string Status { get; set; } = "pending_review";

        [Column("promoted_deal_id")]
        public int? PromotedDealId { get; set; }

        [Column("admin_notes")]
        public string? AdminNotes { get; set; }
    }

    /// <summary>
    /// Insert-only row for deal_candidate (omits server-generated fields).
    /// </summary>
    [Table("deal_candidate")]
    public class DealCandidateInsertRow : BaseModel
    {
        [Column("source")]
        public string Source { get; set; } = string.Empty;

        [Column("store_id")]
        public int StoreId { get; set; }

        [Column("product_candidate_id")]
        public long? ProductCandidateId { get; set; }

        [Column("product_id")]
        public int? ProductId { get; set; }

        [Column("deal_url_canonical")]
        public string DealUrlCanonical { get; set; } = string.Empty;

        [Column("listing_price")]
        public decimal? ListingPrice { get; set; }

        [Column("listing_currency")]
        public string? ListingCurrency { get; set; } = "USD";

        [Column("listing_msrp")]
        public decimal? ListingMsrp { get; set; }

        [Column("condition_category_id")]
        public int? ConditionCategoryId { get; set; }

        [Column("in_stock")]
        public bool? InStock { get; set; }

        [Column("raw_title")]
        public string? RawTitle { get; set; }

        [Column("raw_html_snippet")]
        public string? RawHtmlSnippet { get; set; }

        [Column("ai_confidence")]
        public decimal? AiConfidence { get; set; }
    }
}
