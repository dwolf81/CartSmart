namespace CartSmart.API.Models
{
    /// <summary>
    /// Scrape mode IDs for the store.scrape_mode_id column.
    /// Controls how the backend and browser extension pull prices.
    /// </summary>
    public static class ScrapeMode
    {
        /// <summary>No scraping — prices only via API if api_enabled is true.</summary>
        public const int None = 0;

        /// <summary>Full scraping — both the backend service and the browser extension can scrape.</summary>
        public const int All = 1;

        /// <summary>Browser only — only the browser extension scrapes (headless browsers are blocked).</summary>
        public const int BrowserOnly = 2;

        /// <summary>Returns true when the mode permits the backend worker to scrape (headless).</summary>
        public static bool AllowsServiceScrape(int? modeId) => modeId == All;

        /// <summary>Returns true when the mode permits the browser extension to scrape.</summary>
        public static bool AllowsBrowserScrape(int? modeId) => modeId == All || modeId == BrowserOnly;
    }
}
