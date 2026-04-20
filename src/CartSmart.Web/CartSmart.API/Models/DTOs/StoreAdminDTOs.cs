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
