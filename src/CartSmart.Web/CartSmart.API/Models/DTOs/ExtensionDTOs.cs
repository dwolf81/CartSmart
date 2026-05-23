namespace CartSmart.API.Models.DTOs
{
    /// <summary>
    /// Store configuration sent to the Chrome extension.
    /// Only includes stores with scrape_mode_id = 1 (All) or 2 (BrowserOnly).
    /// </summary>
    public class ExtensionStoreConfigDTO
    {
        public int id { get; set; }
        public string? name { get; set; }
        public string? url { get; set; }
        public string? slug { get; set; }
        public Newtonsoft.Json.Linq.JToken? scrapeConfig { get; set; }
    }

    /// <summary>
    /// Price report submitted by the Chrome extension.
    /// </summary>
    public class ExtensionPriceReportDTO
    {
        public string? url { get; set; }
        public int storeId { get; set; }
        public decimal? price { get; set; }
        public string? currency { get; set; }
        public bool? inStock { get; set; }
        public int candidateCount { get; set; }
        public string? extractedAt { get; set; }
    }

    /// <summary>
    /// Response returned after processing a price report.
    /// </summary>
    public class ExtensionPriceReportResponseDTO
    {
        public bool accepted { get; set; }
        public bool throttled { get; set; }
        public int matchedDealProducts { get; set; }
        public int updatedDealProducts { get; set; }
        public string? message { get; set; }
    }

    /// <summary>
    /// Report submitted by the Chrome extension when price extraction fails.
    /// </summary>
    public class ExtensionScrapeFailureDTO
    {
        public string? url { get; set; }
        public int storeId { get; set; }
        public string? errorMessage { get; set; }
        public int candidateCount { get; set; }
    }

    /// <summary>
    /// "Add Product" payload submitted by the Chrome extension when an admin
    /// clicks the button on an approved retailer page. Carries both product
    /// and deal data — they're promoted together on admin approval.
    /// </summary>
    public class ExtensionProductCandidateDTO
    {
        public int storeId { get; set; }
        public string? url { get; set; }

        // Product metadata
        public string? name { get; set; }
        public string? brand { get; set; }
        public decimal? msrp { get; set; }
        public string? imageUrl { get; set; }
        public string? description { get; set; }

        // Deal metadata (paired)
        public decimal? dealPrice { get; set; }
        public string? currency { get; set; }
        public int? conditionCategoryId { get; set; }
        public bool? inStock { get; set; }
        public string? rawTitle { get; set; }
    }

    /// <summary>
    /// Response from POST /api/extension/product-candidate.
    /// </summary>
    public class ExtensionProductCandidateResponseDTO
    {
        /// <summary>
        /// One of: created | duplicate_candidate | duplicate_live_product | suggested_merge
        /// </summary>
        public string status { get; set; } = string.Empty;
        public long? candidateId { get; set; }
        public int? productId { get; set; }
        public int? suggestedMergeProductId { get; set; }
        public int submissionCount { get; set; }
        public string? message { get; set; }
    }
}
