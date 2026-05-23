using Newtonsoft.Json;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Models
{
    [Table("product_candidate")]
    public class ProductCandidate : BaseModel
    {
        [PrimaryKey("id", false)]
        public long Id { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }

        [Column("last_submitted_at")]
        public DateTime LastSubmittedAt { get; set; }

        // extension | crawler | ai
        [Column("source")]
        public string Source { get; set; } = "extension";

        [Column("source_store_id")]
        public int SourceStoreId { get; set; }

        [Column("source_url_canonical")]
        public string SourceUrlCanonical { get; set; } = string.Empty;

        [Column("name")]
        public string Name { get; set; } = string.Empty;

        [Column("name_normalized")]
        public string NameNormalized { get; set; } = string.Empty;

        [Column("brand_text")]
        public string? BrandText { get; set; }

        [Column("brand_id")]
        public int? BrandId { get; set; }

        [Column("product_type_id")]
        public int? ProductTypeId { get; set; }

        [Column("msrp")]
        public decimal? MSRP { get; set; }

        [Column("slug_suggested")]
        public string? SlugSuggested { get; set; }

        [Column("image_url_original")]
        public string? ImageUrlOriginal { get; set; }

        [Column("image_url")]
        public string? ImageUrl { get; set; }

        [Column("description")]
        public string? Description { get; set; }

        // pending_review | approved | rejected | duplicate | merged
        [Column("status")]
        public string Status { get; set; } = "pending_review";

        [Column("suggested_merge_product_id")]
        public int? SuggestedMergeProductId { get; set; }

        [Column("merged_into_product_id")]
        public int? MergedIntoProductId { get; set; }

        [Column("admin_notes")]
        public string? AdminNotes { get; set; }

        [Column("submitted_by_user_id")]
        public int? SubmittedByUserId { get; set; }

        [Column("submission_count")]
        public int SubmissionCount { get; set; } = 1;

        // Array of submitter objects: [{ user_id, at, url }, ...]
        [Column("submitters_jsonb")]
        [JsonConverter(typeof(JsonStringOrObjectConverter))]
        public string SubmittersJsonb { get; set; } = "[]";
    }

    /// <summary>
    /// Insert-only row for product_candidate (omits server-generated fields).
    /// </summary>
    [Table("product_candidate")]
    public class ProductCandidateInsertRow : BaseModel
    {
        [Column("source")]
        public string Source { get; set; } = "extension";

        [Column("source_store_id")]
        public int SourceStoreId { get; set; }

        [Column("source_url_canonical")]
        public string SourceUrlCanonical { get; set; } = string.Empty;

        [Column("name")]
        public string Name { get; set; } = string.Empty;

        [Column("name_normalized")]
        public string NameNormalized { get; set; } = string.Empty;

        [Column("brand_text")]
        public string? BrandText { get; set; }

        [Column("brand_id")]
        public int? BrandId { get; set; }

        [Column("product_type_id")]
        public int? ProductTypeId { get; set; }

        [Column("msrp")]
        public decimal? MSRP { get; set; }

        [Column("slug_suggested")]
        public string? SlugSuggested { get; set; }

        [Column("image_url_original")]
        public string? ImageUrlOriginal { get; set; }

        [Column("image_url")]
        public string? ImageUrl { get; set; }

        [Column("description")]
        public string? Description { get; set; }

        [Column("suggested_merge_product_id")]
        public int? SuggestedMergeProductId { get; set; }

        [Column("submitted_by_user_id")]
        public int? SubmittedByUserId { get; set; }

        [Column("submitters_jsonb")]
        [JsonConverter(typeof(JsonStringOrObjectConverter))]
        public string SubmittersJsonb { get; set; } = "[]";
    }
}
