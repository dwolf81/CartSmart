using CartSmart.API.Models;
using CartSmart.Core.Worker;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Supabase;
using System.Text.Json;

using Op = Supabase.Postgrest.Constants.Operator;
using Ord = Supabase.Postgrest.Constants.Ordering;

namespace CartSmart.Functions;

public class IngestStoreListingsFunction
{
    private readonly IDealUpdateOrchestrator _orchestrator;
    private readonly IListingPageScraper _listingPageScraper;
    private readonly ILogger<IngestStoreListingsFunction> _logger;
    private readonly IConfiguration _config;
    private readonly Client _supabase;

    public IngestStoreListingsFunction(
        IDealUpdateOrchestrator orchestrator,
        IListingPageScraper listingPageScraper,
        ILogger<IngestStoreListingsFunction> logger,
        IConfiguration config,
        Client supabase)
    {
        _orchestrator = orchestrator;
        _listingPageScraper = listingPageScraper;
        _logger = logger;
        _config = config;
        _supabase = supabase;
    }

    /// <summary>
    /// Timer-triggered function that scrapes HTML listing pages for configured products/stores
    /// and feeds the results through the deal ingestion pipeline.
    /// Runs every 120 minutes by default.
    /// </summary>
    [Function("IngestStoreListings")]
    public async Task Run([TimerTrigger("0 */120 * * * *", UseMonitor = true)] TimerInfo timerInfo, CancellationToken ct)
    {
        _logger.LogInformation("IngestStoreListings triggered at {Time}", DateTime.UtcNow);

        // Fetch all enabled product_store_page records that are due
        var duePages = await GetDueStorePageAsync(ct);
        if (duePages.Count == 0)
        {
            _logger.LogInformation("No product store pages are due for scraping");
            return;
        }

        _logger.LogInformation("Found {Count} product store page(s) due for scraping", duePages.Count);

        // Group by store_id so we process all pages for a store together
        var byStore = duePages.GroupBy(p => p.StoreId);
        var topPerProduct = int.TryParse(
            _config["Values:TopPerProduct"] ?? _config["TopPerProduct"], out var t) ? t : 5;

        foreach (var storeGroup in byStore)
        {
            var storeId = storeGroup.Key;

            // Fetch store for scrape settings
            var storeResp = await _supabase.From<Store>()
                .Filter("id", Op.Equals, storeId.ToString())
                .Limit(1)
                .Get(ct);
            var store = storeResp.Models.FirstOrDefault();
            if (store == null)
            {
                _logger.LogWarning("Store {StoreId} not found, skipping", storeId);
                continue;
            }

            var httpEnabled = store.ScrapeHttpEnabled;
            var playwrightEnabled = store.ScrapePlaywrightEnabled;

            // Parse listing selectors from store's scrape_config
            var selectors = ParseListingSelectors(store.ScrapeConfig);
            if (selectors == null || string.IsNullOrWhiteSpace(selectors.Container))
            {
                _logger.LogWarning("Store {StoreId} ({StoreName}) has no listing_selectors configured, skipping",
                    storeId, store.Name);
                continue;
            }

            var listingsByProductId = new Dictionary<int, IReadOnlyList<NewListing>>();
            var queries = new List<NewListingQuery>();

            foreach (var page in storeGroup)
            {
                try
                {
                    _logger.LogInformation("Scraping store page: product={ProductId}, store={StoreId}, url={Url}",
                        page.ProductId, page.StoreId, page.Url);

                    var scrapedListings = await _listingPageScraper.ScrapeListingsAsync(
                        page.Url,
                        selectors,
                        httpEnabled,
                        playwrightEnabled,
                        maxPages: 10,
                        delayBetweenPagesMs: 2000,
                        ct: ct);

                    // Convert ScrapedListing → NewListing
                    var newListings = scrapedListings
                        .Where(sl => sl.Price.HasValue && sl.Price.Value > 0)
                        .Select(sl => new NewListing(
                            ItemId: sl.ItemId,
                            Title: sl.Title,
                            Url: sl.Url,
                            Price: sl.Price,
                            Currency: sl.Currency,
                            GTIN: null,
                            MPN: null,
                            Brand: null,
                            ConditionCategoryId: sl.ConditionCategoryId,
                            FreeShipping: null))
                        .ToList();

                    if (newListings.Count > 0)
                    {
                        // Merge if multiple pages for the same product
                        if (listingsByProductId.TryGetValue(page.ProductId, out var existing))
                        {
                            var merged = existing.Concat(newListings).ToList();
                            listingsByProductId[page.ProductId] = merged;
                        }
                        else
                        {
                            listingsByProductId[page.ProductId] = newListings;
                        }
                    }

                    _logger.LogInformation("Scraped {Count} listing(s) from {Url} for product {ProductId}",
                        newListings.Count, page.Url, page.ProductId);

                    // Log scrape results
                    var method = playwrightEnabled ? "playwright" : "http";
                    var logInsert = new ScrapeLogInsert
                    {
                        StoreId = storeId,
                        Url = page.Url,
                        Method = "listing_scrape",
                        Success = newListings.Count > 0,
                        Price = newListings.FirstOrDefault()?.Price,
                        Currency = newListings.FirstOrDefault()?.Currency,
                        ErrorMessage = newListings.Count == 0 ? "no_listings_found" : null
                    };
                    await _supabase.From<ScrapeLogInsert>().Insert(logInsert, cancellationToken: ct);

                    // Update last_scraped_at
                    await _supabase.From<ProductStorePage>()
                        .Filter("id", Op.Equals, page.Id.ToString())
                        .Set(x => x.LastScrapedAt!, DateTime.UtcNow)
                        .Update(cancellationToken: ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to scrape store page: product={ProductId}, url={Url}",
                        page.ProductId, page.Url);

                    // Log failure
                    try
                    {
                        var logInsert = new ScrapeLogInsert
                        {
                            StoreId = storeId,
                            Url = page.Url,
                            Method = "listing_scrape",
                            Success = false,
                            ErrorMessage = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message
                        };
                        await _supabase.From<ScrapeLogInsert>().Insert(logInsert, cancellationToken: ct);
                    }
                    catch { /* don't let logging failure mask original error */ }
                }
            }

            if (listingsByProductId.Count == 0) continue;

            // Build query list for all products that returned listings
            var productIds = listingsByProductId.Keys.ToList();
            var productResp = await _supabase.From<Product>()
                .Filter("id", Op.In, productIds.Select(id => id.ToString()).ToList())
                .Get(ct);

            // Build product name lookup for title fallback
            var productNameMap = productResp.Models
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .ToDictionary(p => p.Id, p => p.Name!);

            // Fill in missing titles with the product name
            foreach (var (productId, listings) in listingsByProductId.ToList())
            {
                if (productNameMap.TryGetValue(productId, out var productName))
                {
                    var patched = listings.Select(l =>
                        string.IsNullOrWhiteSpace(l.Title)
                            ? l with { Title = productName }
                            : l).ToList();
                    listingsByProductId[productId] = patched;
                }
            }

            foreach (var product in productResp.Models)
            {
                if (!string.IsNullOrWhiteSpace(product.Name))
                    queries.Add(new NewListingQuery(product.Id, product.Name));
            }

            // Feed pre-fetched listings into the orchestrator pipeline
            try
            {
                var created = await _orchestrator.IngestPreFetchedListingsAsync(
                    storeId, topPerProduct, queries, listingsByProductId, ct);
                _logger.LogInformation("Store {StoreId}: created/updated {Count} deal(s) from scraped listings",
                    storeId, created);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ingest pre-fetched listings for store {StoreId}", storeId);
            }
        }

        _logger.LogInformation("IngestStoreListings completed at {Time}", DateTime.UtcNow);
    }

    private async Task<List<ProductStorePage>> GetDueStorePageAsync(CancellationToken ct)
    {
        // Fetch enabled pages where last_scraped_at is null or older than scrape_interval_minutes
        var resp = await _supabase.From<ProductStorePage>()
            .Filter("enabled", Op.Equals, "true")
            .Get(ct);

        _logger.LogInformation("GetDueStorePageAsync: query returned {Count} enabled row(s), response content length={Length}",
            resp.Models.Count, resp.Content?.Length ?? 0);

        var now = DateTime.UtcNow;
        var due = resp.Models
            .Where(p => p.LastScrapedAt == null
                || (now - p.LastScrapedAt.Value).TotalMinutes >= p.ScrapeIntervalMinutes)
            .ToList();

        _logger.LogInformation("GetDueStorePageAsync: {DueCount} page(s) are due for scraping", due.Count);
        return due;
    }

    private static ListingScrapeConfig? ParseListingSelectors(string? scrapeConfig)
    {
        if (string.IsNullOrWhiteSpace(scrapeConfig)) return null;
        try
        {
            using var doc = JsonDocument.Parse(scrapeConfig);
            if (doc.RootElement.TryGetProperty("listing_selectors", out var listingEl))
            {
                return JsonSerializer.Deserialize<ListingScrapeConfig>(listingEl.GetRawText());
            }
        }
        catch { }
        return null;
    }
}
