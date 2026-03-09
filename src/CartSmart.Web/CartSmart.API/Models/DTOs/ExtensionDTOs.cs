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
        public int matchedDealProducts { get; set; }
        public int updatedDealProducts { get; set; }
        public string? message { get; set; }
    }
}
