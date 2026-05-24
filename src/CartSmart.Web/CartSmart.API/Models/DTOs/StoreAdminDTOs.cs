namespace CartSmart.API.Models.DTOs
{
    public class AdminStoreDTO
    {
        public int id { get; set; }
        public string? name { get; set; }
        public string? url { get; set; }
        public string? affiliateCode { get; set; }
        public string? affiliateCodeVar { get; set; }
        public string? affiliateUrlTemplate { get; set; }
        public int? brandId { get; set; }
        public float? upfrontCost { get; set; }
        public int? upfrontCostTermId { get; set; }
        public bool? apiEnabled { get; set; }
        public int? scrapeModeId { get; set; }
        public string? scrapeConfig { get; set; }
        public string? requiredQueryVars { get; set; }
        public string? slug { get; set; }
        public bool approved { get; set; }
        public string? imageUrl { get; set; }
        public string? description { get; set; }
        public bool scrapeHttpEnabled { get; set; }
        public bool scrapePlaywrightEnabled { get; set; }
    }

    public class AdminStoreEditResponseDTO
    {
        public AdminStoreDTO? store { get; set; }
    }

    public class AdminUpsertStoreRequestDTO
    {
        public string? name { get; set; }
        public string? url { get; set; }
        public string? affiliateCode { get; set; }
        public string? affiliateCodeVar { get; set; }
        public string? affiliateUrlTemplate { get; set; }
        public int? brandId { get; set; }
        public float? upfrontCost { get; set; }
        public int? upfrontCostTermId { get; set; }
        public bool? apiEnabled { get; set; }
        public int? scrapeModeId { get; set; }
        public string? scrapeConfig { get; set; }
        public string? requiredQueryVars { get; set; }
        public string? slug { get; set; }
        public bool? approved { get; set; }
        public string? description { get; set; }
        public bool? scrapeHttpEnabled { get; set; }
        public bool? scrapePlaywrightEnabled { get; set; }
    }

    public class AdminCreateStoreResponseDTO
    {
        public int id { get; set; }
        public string? name { get; set; }
        public string? url { get; set; }
        public string? slug { get; set; }
        public bool approved { get; set; }
        public string? imageUrl { get; set; }
        public string? description { get; set; }
    }

    public class TestScrapeRequestDTO
    {
        public string url { get; set; } = string.Empty;
        public string scrapeConfig { get; set; } = string.Empty;
        /// <summary>"http" (default) or "playwright"</summary>
        public string method { get; set; } = "http";
        /// <summary>"price" (default) — exercise price_selectors. "listing" — exercise listing_selectors.</summary>
        public string mode { get; set; } = "price";
    }

    public class TestScrapeListingSampleDTO
    {
        public string? title { get; set; }
        public string? url { get; set; }
        public decimal? price { get; set; }
        public string? currency { get; set; }
        public string? conditionText { get; set; }
        public int? conditionCategoryId { get; set; }
    }

    public class TestScrapeResponseDTO
    {
        public bool success { get; set; }
        public string? error { get; set; }
        public decimal? price { get; set; }
        public string? currency { get; set; }
        public bool? inStock { get; set; }
        public List<TestScrapePriceCandidateDTO> candidates { get; set; } = new();
        public bool blockedByBotProtection { get; set; }
        /// <summary>Length of fetched HTML in characters (diagnostic info).</summary>
        public int? htmlLength { get; set; }
        /// <summary>
        /// Fetched HTML, truncated to a transport-safe size. Returned so admins
        /// can diagnose selector mismatches by seeing exactly what the server
        /// got back (e.g. JS-rendered SPA shell, bot-block challenge, geo-block
        /// page) instead of guessing whether their selectors are wrong.
        /// </summary>
        public string? html { get; set; }
        /// <summary>True when the html field has been truncated below htmlLength.</summary>
        public bool htmlTruncated { get; set; }
        /// <summary>Populated only when mode=listing: count of container matches found.</summary>
        public int? containerCount { get; set; }
        /// <summary>Populated only when mode=listing: a preview of the first N parsed listings.</summary>
        public List<TestScrapeListingSampleDTO>? listings { get; set; }
    }

    public class TestScrapePriceCandidateDTO
    {
        public decimal amount { get; set; }
        public string? currency { get; set; }
        public bool struck { get; set; }
        public bool promo { get; set; }
        public string? selector { get; set; }
    }

    public class TestScrapeScreenshotRequestDTO
    {
        public string url { get; set; } = string.Empty;
    }

    public class AutoGenerateScrapeConfigRequestDTO
    {
        public string url { get; set; } = string.Empty;
        /// <summary>"http" (default) or "playwright"</summary>
        public string method { get; set; } = "http";
        /// <summary>
        /// "price" (default) — analyze a product page and return only price_selectors.
        /// "listing" — analyze a listing/category page and return only listing_selectors.
        /// The caller is responsible for merging the returned subset into the existing
        /// scrape_config so the two halves don't overwrite each other.
        /// </summary>
        public string mode { get; set; } = "price";
    }

    public class AutoGenerateScrapeConfigResponseDTO
    {
        public bool success { get; set; }
        public string? error { get; set; }
        public string? scrapeConfig { get; set; }
    }

    // ── Scrape Report DTOs ───────────────────────────────────────────────

    public class ScrapeReportStoreSummaryDTO
    {
        public int storeId { get; set; }
        public string storeName { get; set; } = string.Empty;
        public string? storeUrl { get; set; }
        public int scrapeModeId { get; set; }
        public bool scrapeHttpEnabled { get; set; }
        public bool scrapePlaywrightEnabled { get; set; }
        public ScrapeMethodSummaryDTO http { get; set; } = new();
        public ScrapeMethodSummaryDTO playwright { get; set; } = new();
        public ScrapeMethodSummaryDTO extension { get; set; } = new();
        public ScrapeMethodSummaryDTO discovery { get; set; } = new();
        public DateTime? lastLogAt { get; set; }
    }

    public class ScrapeMethodSummaryDTO
    {
        public int successCount { get; set; }
        public int failCount { get; set; }
        public int totalCount => successCount + failCount;
    }

    public class ScrapeReportDetailDTO
    {
        public long id { get; set; }
        public long? dealProductId { get; set; }
        public string url { get; set; } = string.Empty;
        public string method { get; set; } = string.Empty;
        public bool success { get; set; }
        public decimal? price { get; set; }
        public string? currency { get; set; }
        public string? errorMessage { get; set; }
        public DateTime createdAt { get; set; }
    }

    public class UpdateScrapeMethodsRequestDTO
    {
        public bool? scrapeHttpEnabled { get; set; }
        public bool? scrapePlaywrightEnabled { get; set; }
    }
}
