using CartSmart.API.Models;
using CartSmart.Core.Worker;
using CartSmart.Providers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Supabase;
using System.Text.Json;
using System.Text.RegularExpressions;

using Op = Supabase.Postgrest.Constants.Operator;
using Ord = Supabase.Postgrest.Constants.Ordering;

namespace CartSmart.Functions;

public class IngestStoreListingsFunction
{
    private readonly IDealUpdateOrchestrator _orchestrator;
    private readonly IListingPageScraper _listingPageScraper;
    private readonly IOpenAiProductMatcher _productMatcher;
    private readonly IListingSelectorInferrer _selectorInferrer;
    private readonly ILogger<IngestStoreListingsFunction> _logger;
    private readonly IConfiguration _config;
    private readonly Client _supabase;

    // Hard cap on AI matcher calls per run, to keep cost bounded
    private const int MaxAiCallsPerRun = 200;
    // Fuzzy thresholds — must match the extension submit endpoint's logic
    private const double FuzzyAutoMatchScore = 0.85;
    private const double FuzzyAiFloorScore = 0.50;
    private const decimal AiConfidenceThreshold = 0.85m;
    // Per-endpoint discovery cadence. Most product catalogs change on a
    // multi-day timescale and re-hitting category pages every 5 minutes wastes
    // scraper budget. We bypass this when a new active product of the
    // endpoint's product_type_id was created since last_crawled_at, so newly
    // added products get checked on the next timer tick instead of waiting.
    private const int DiscoveryIntervalHours = 72;

    public IngestStoreListingsFunction(
        IDealUpdateOrchestrator orchestrator,
        IListingPageScraper listingPageScraper,
        IOpenAiProductMatcher productMatcher,
        IListingSelectorInferrer selectorInferrer,
        ILogger<IngestStoreListingsFunction> logger,
        IConfiguration config,
        Client supabase)
    {
        _orchestrator = orchestrator;
        _listingPageScraper = listingPageScraper;
        _productMatcher = productMatcher;
        _selectorInferrer = selectorInferrer;
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
        }

        if (duePages.Count > 0)
        {
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
                // Hoisted out of the try so the catch can reuse it to log under
                // the right transport bucket. Playwright wins when both are
                // enabled because the scraper falls back HTTP→Playwright.
                var method = playwrightEnabled ? "playwright" : "http";

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

                    var logInsert = new ScrapeLogInsert
                    {
                        StoreId = storeId,
                        Url = page.Url,
                        Method = method,
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

                    try
                    {
                        var logInsert = new ScrapeLogInsert
                        {
                            StoreId = storeId,
                            Url = page.Url,
                            Method = method,
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
        } // end if (duePages.Count > 0)

        // ── Store-level discovery pass ───────────────────────────────────────
        // Hits admin-curated store_scan_endpoint URLs and emits deal_candidate
        // rows. Bounded traffic: only URLs explicitly added by an admin are
        // crawled; per-store query is scoped by endpoint.product_type_id so
        // the fuzzy/AI candidate set stays small.
        try
        {
            await RunDiscoveryPassAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discovery pass failed");
        }

        _logger.LogInformation("IngestStoreListings completed at {Time}", DateTime.UtcNow);
    }

    private async Task RunDiscoveryPassAsync(CancellationToken ct)
    {
        var endpointsResp = await _supabase.From<StoreScanEndpoint>()
            .Filter("is_active", Op.Equals, "true")
            .Get(ct);
        var endpoints = endpointsResp.Models ?? new List<StoreScanEndpoint>();
        if (endpoints.Count == 0)
        {
            _logger.LogInformation("Discovery pass: no active scan endpoints");
            return;
        }

        int aiCallsThisRun = 0;
        var endpointsByStore = endpoints.GroupBy(e => e.StoreId);

        foreach (var storeGroup in endpointsByStore)
        {
            if (ct.IsCancellationRequested) break;
            var storeId = storeGroup.Key;

            var storeResp = await _supabase.From<Store>()
                .Filter("id", Op.Equals, storeId.ToString())
                .Limit(1)
                .Get(ct);
            var store = storeResp.Models.FirstOrDefault();
            if (store == null || !store.Approved)
            {
                _logger.LogInformation("Discovery pass: store {StoreId} not approved or missing, skipping", storeId);
                continue;
            }
            if (store.ScrapeModeId is null or 0)
            {
                _logger.LogInformation("Discovery pass: store {StoreId} has scrape_mode_id=0, skipping", storeId);
                continue;
            }

            var selectors = ParseListingSelectors(store.ScrapeConfig);
            if (selectors == null || string.IsNullOrWhiteSpace(selectors.Container))
            {
                _logger.LogInformation(
                    "Discovery pass: store {StoreId} has no listing_selectors — attempting AI inference from first endpoint",
                    storeId);

                // Use the first active endpoint URL as sample input for the AI
                var sampleEndpoint = storeGroup.FirstOrDefault();
                if (sampleEndpoint != null)
                {
                    selectors = await TryInferAndPersistSelectorsAsync(store, sampleEndpoint.Url, ct);
                }

                if (selectors == null || string.IsNullOrWhiteSpace(selectors.Container))
                {
                    _logger.LogWarning(
                        "Discovery pass: store {StoreId} has no listing_selectors and AI inference failed, skipping",
                        storeId);
                    continue;
                }
            }

            // Products this store already has active deals for — used to skip
            // emitting duplicate candidates per the user's "skip on existing
            // active deal at this store" requirement.
            var existingDealProductIds = await GetProductIdsWithActiveDealAtStoreAsync(storeId, ct);
            var existingDealUrls = await GetActiveDealProductUrlsForStoreAsync(storeId, ct);
            var existingCandidateUrls = await GetPendingCandidateUrlsForStoreAsync(storeId, ct);

            foreach (var endpoint in storeGroup)
            {
                if (ct.IsCancellationRequested) break;

                // ── 72-hour throttle ───────────────────────────────────────
                // Skip the endpoint if we crawled it within the cadence window
                // AND no new active product of this endpoint's product_type has
                // been added since the last crawl. A freshly added product
                // bypasses the throttle so the next timer tick goes hunting
                // for it instead of waiting up to three days.
                if (endpoint.LastCrawledAt.HasValue)
                {
                    var hoursSinceLastCrawl = (DateTime.UtcNow - endpoint.LastCrawledAt.Value).TotalHours;
                    if (hoursSinceLastCrawl < DiscoveryIntervalHours)
                    {
                        var hasNewMatchingProduct = await AnyNewMatchingProductSinceAsync(
                            endpoint.ProductTypeId, endpoint.LastCrawledAt.Value, ct);
                        if (!hasNewMatchingProduct)
                        {
                            _logger.LogInformation(
                                "Discovery pass: skipping endpoint {Id} ({Url}) — last crawled {Hours:F1}h ago (<{Interval}h) and no new product_type={Type} products since",
                                endpoint.Id, endpoint.Url, hoursSinceLastCrawl, DiscoveryIntervalHours, endpoint.ProductTypeId);
                            continue;
                        }
                        _logger.LogInformation(
                            "Discovery pass: endpoint {Id} ({Url}) is within the {Interval}h throttle window but a new product_type={Type} product was added — running early",
                            endpoint.Id, endpoint.Url, DiscoveryIntervalHours, endpoint.ProductTypeId);
                    }
                }

                // ── Missing-products gate ─────────────────────────────────
                // Compute the products this endpoint could realistically
                // discover BEFORE we pay for a scrape. If every active product
                // of this type already has a deal at this store, the scrape
                // can't produce any new candidates — skip outright and bump
                // last_crawled_at so the 72h throttle takes over next tick.
                var candidateProducts = await GetCandidateProductsAsync(endpoint.ProductTypeId, ct);
                var missingProducts = candidateProducts
                    .Where(p => !existingDealProductIds.Contains(p.Id))
                    .ToList();

                if (missingProducts.Count == 0)
                {
                    var reason = candidateProducts.Count == 0
                        ? "no_candidate_products"
                        : "all_matching_products_already_attached";

                    if (candidateProducts.Count == 0)
                    {
                        // Catalog-level gap — surface it in the scrape report so
                        // an admin notices nothing exists for this type.
                        _logger.LogInformation("Discovery pass: no candidate products for endpoint {Url}", endpoint.Url);
                        try
                        {
                            await _supabase.From<ScrapeLogInsert>().Insert(new ScrapeLogInsert
                            {
                                StoreId = storeId,
                                Url = endpoint.Url,
                                Method = "discovery",
                                Success = false,
                                ErrorMessage = reason
                            }, cancellationToken: ct);
                        }
                        catch { /* best-effort */ }
                    }
                    else
                    {
                        // Coverage is full — quiet skip (no scrape_log noise),
                        // but still bump last_crawled_at so we don't recompute
                        // this gate on every 5-minute tick.
                        _logger.LogInformation(
                            "Discovery pass: skipping endpoint {Id} ({Url}) — all {Count} active product_type={Type} product(s) already have an active deal at this store",
                            endpoint.Id, endpoint.Url, candidateProducts.Count, endpoint.ProductTypeId);
                    }

                    try
                    {
                        await _supabase.From<StoreScanEndpoint>()
                            .Filter("id", Op.Equals, endpoint.Id.ToString())
                            .Set(x => x.LastCrawledAt!, DateTime.UtcNow)
                            .Update(cancellationToken: ct);
                    }
                    catch { /* best-effort */ }
                    continue;
                }

                _logger.LogInformation(
                    "Discovery pass: scanning store={StoreId} endpoint={Url} (productType={Type}, {Missing} missing of {Total} matching products)",
                    storeId, endpoint.Url, endpoint.ProductTypeId, missingProducts.Count, candidateProducts.Count);

                List<ScrapedListing> listings;
                string? scrapeError = null;
                try
                {
                    var scraped = await _listingPageScraper.ScrapeListingsAsync(
                        endpoint.Url,
                        selectors,
                        store.ScrapeHttpEnabled,
                        store.ScrapePlaywrightEnabled,
                        maxPages: 3,                  // small cap on per-endpoint pagination
                        delayBetweenPagesMs: 2000,
                        ct: ct);
                    listings = scraped.ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Discovery pass: scrape failed for endpoint {Url}", endpoint.Url);
                    listings = new List<ScrapedListing>();
                    scrapeError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                }

                // Update endpoint observability fields
                try
                {
                    await _supabase.From<StoreScanEndpoint>()
                        .Filter("id", Op.Equals, endpoint.Id.ToString())
                        .Set(x => x.LastCrawledAt!, DateTime.UtcNow)
                        .Set(x => x.LastResultCount!, listings.Count)
                        .Update(cancellationToken: ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Discovery pass: failed to update endpoint {Id} observability", endpoint.Id);
                }

                if (listings.Count == 0)
                {
                    // Log the empty/failed scrape so it surfaces in the admin
                    // scrape report. Without this, broken endpoints are
                    // invisible — they just silently produce zero candidates.
                    try
                    {
                        await _supabase.From<ScrapeLogInsert>().Insert(new ScrapeLogInsert
                        {
                            StoreId = storeId,
                            Url = endpoint.Url,
                            Method = "discovery",
                            Success = false,
                            ErrorMessage = scrapeError ?? "no_listings_found"
                        }, cancellationToken: ct);
                    }
                    catch { /* best-effort */ }
                    continue;
                }

                // Candidate-product sets (candidateProducts / missingProducts)
                // were computed at the top of the loop — see the missing-products
                // gate. The per-listing matching below uses missingProducts so we
                // never bother fuzzy/AI-matching against products that already
                // have a deal at this store.

                // Track per-endpoint counters so we can write one summary row
                // at the bottom (vs. one row per listing — which would be too
                // noisy for the report and confusing in the per-store detail
                // drawer).
                int emittedCandidates = 0;
                int skippedDuplicates = 0;
                int unmatchedListings = 0;

                foreach (var listing in listings)
                {
                    if (string.IsNullOrWhiteSpace(listing.Url) || string.IsNullOrWhiteSpace(listing.Title))
                        continue;
                    if (!listing.Price.HasValue || listing.Price.Value <= 0)
                        continue;

                    var canonical = NormalizeUrl(listing.Url);
                    if (string.IsNullOrWhiteSpace(canonical)) continue;

                    if (existingDealUrls.Contains(canonical))
                        continue; // already a live deal_product at this store

                    if (existingCandidateUrls.Contains(canonical))
                    {
                        // bump last_seen_at and skip
                        try
                        {
                            await _supabase.From<DealCandidate>()
                                .Filter("deal_url_canonical", Op.Equals, canonical)
                                .Filter("status", Op.Equals, "pending_review")
                                .Set(x => x.LastSeenAt, DateTime.UtcNow)
                                .Update(cancellationToken: ct);
                        }
                        catch { /* best-effort */ }
                        skippedDuplicates++;
                        continue;
                    }

                    // ── Match the listing to a known product ─────────────
                    // Match against missingProducts only so we don't waste an
                    // AI call (or a fuzzy false-positive) on a product that's
                    // already attached to this store.
                    var (matchedId, matchScore) = BestFuzzyMatch(listing.Title, missingProducts);
                    int? finalProductId = null;
                    decimal? aiConfidence = null;
                    string source = "crawler";

                    if (matchScore >= FuzzyAutoMatchScore)
                    {
                        finalProductId = matchedId;
                    }
                    else if (matchScore >= FuzzyAiFloorScore && aiCallsThisRun < MaxAiCallsPerRun)
                    {
                        aiCallsThisRun++;
                        var aiInput = missingProducts
                            .Select(p => new ProductMatchCandidate(p.Id, p.Name ?? string.Empty, null))
                            .Take(25)
                            .ToList();
                        var aiResult = await _productMatcher.MatchAsync(listing.Title, brandHint: null, aiInput, ct);
                        if (aiResult?.ProductId.HasValue == true && aiResult.Confidence >= AiConfidenceThreshold)
                        {
                            finalProductId = aiResult.ProductId.Value;
                            aiConfidence = aiResult.Confidence;
                            source = "ai";
                        }
                    }

                    if (!finalProductId.HasValue)
                    {
                        unmatchedListings++;
                        continue;
                    }

                    // ── Skip-on-existing-deal check ──────────────────────
                    if (existingDealProductIds.Contains(finalProductId.Value))
                    {
                        skippedDuplicates++;
                        continue;
                    }

                    var insert = new DealCandidateInsertRow
                    {
                        Source = source,
                        StoreId = storeId,
                        ProductId = finalProductId,
                        DealUrlCanonical = canonical,
                        ListingPrice = listing.Price,
                        ListingCurrency = listing.Currency ?? "USD",
                        ConditionCategoryId = listing.ConditionCategoryId,
                        RawTitle = listing.Title,
                        AiConfidence = aiConfidence
                    };
                    try
                    {
                        await _supabase.From<DealCandidateInsertRow>().Insert(insert, cancellationToken: ct);
                        existingCandidateUrls.Add(canonical);
                        emittedCandidates++;
                        _logger.LogInformation(
                            "Discovery pass: created deal_candidate productId={ProductId} url={Url} source={Source} conf={Conf}",
                            finalProductId, canonical, source, aiConfidence);
                    }
                    catch (Exception ex)
                    {
                        // Unique index on (deal_url_canonical) WHERE status='pending_review'
                        // makes this idempotent — log and continue.
                        _logger.LogWarning(ex, "Discovery pass: insert failed for url={Url}", canonical);
                    }
                }

                // ── Per-endpoint summary row ─────────────────────────────
                //
                // Success = "the scrape produced usable listings". We consider
                // it a success when listings were extracted, even if every
                // listing was a duplicate of an existing live deal — that's
                // an expected outcome, not a config problem. Failure means
                // either the scrape errored (handled above) or nothing was
                // emitted/skipped/matched, which usually points at a stale
                // selector or a drift in the page structure.
                try
                {
                    var scrapeWorked = emittedCandidates + skippedDuplicates > 0;
                    string? err = scrapeWorked
                        ? null
                        : unmatchedListings > 0
                            ? $"all_unmatched ({unmatchedListings} listing(s) had no product match)"
                            : "no_listings_emitted";

                    await _supabase.From<ScrapeLogInsert>().Insert(new ScrapeLogInsert
                    {
                        StoreId = storeId,
                        Url = endpoint.Url,
                        Method = "discovery",
                        Success = scrapeWorked,
                        ErrorMessage = err
                    }, cancellationToken: ct);
                }
                catch { /* best-effort */ }
            }
        }

        if (aiCallsThisRun > 0)
            _logger.LogInformation("Discovery pass: {Calls} AI matcher call(s) this run", aiCallsThisRun);
    }

    // ── Discovery-pass helpers ───────────────────────────────────────────

    /// <summary>
    /// Fetches the page at <paramref name="sampleUrl"/>, sends the HTML to the AI to
    /// infer CSS listing selectors, and — on success — persists the result back into
    /// the store's <c>scrape_config</c> so inference only runs once.
    /// </summary>
    private async Task<ListingScrapeConfig?> TryInferAndPersistSelectorsAsync(
        Store store, string sampleUrl, CancellationToken ct)
    {
        // Fetch raw HTML using the store's configured scrape method
        string? html = null;
        try
        {
            using var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            html = await httpClient.GetStringAsync(sampleUrl, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discovery pass: failed to fetch sample page {Url} for selector inference", sampleUrl);
        }

        if (string.IsNullOrWhiteSpace(html)) return null;

        var inferred = await _selectorInferrer.InferSelectorsAsync(html, sampleUrl, ct);
        if (inferred == null || string.IsNullOrWhiteSpace(inferred.Container)) return null;

        // Persist selectors back to the store's scrape_config so we don't call AI again
        try
        {
            var existingConfig = string.IsNullOrWhiteSpace(store.ScrapeConfig)
                ? new System.Text.Json.Nodes.JsonObject()
                : System.Text.Json.Nodes.JsonNode.Parse(store.ScrapeConfig)?.AsObject()
                  ?? new System.Text.Json.Nodes.JsonObject();

            existingConfig["listing_selectors"] = System.Text.Json.Nodes.JsonNode.Parse(
                JsonSerializer.Serialize(inferred));

            var updatedJson = existingConfig.ToJsonString();

            await _supabase.From<Store>()
                .Filter("id", Op.Equals, store.Id.ToString())
                .Set(x => x.ScrapeConfig!, updatedJson)
                .Update(cancellationToken: ct);

            _logger.LogInformation(
                "Discovery pass: AI-inferred listing_selectors saved for store {StoreId} (container={Container})",
                store.Id, inferred.Container);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discovery pass: failed to persist inferred selectors for store {StoreId}", store.Id);
            // Non-fatal — still use the inferred selectors for this run
        }

        return inferred;
    }

    private async Task<HashSet<int>> GetProductIdsWithActiveDealAtStoreAsync(int storeId, CancellationToken ct)
    {
        var dealResp = await _supabase.From<Deal>()
            .Select("id")
            .Filter("store_id", Op.Equals, storeId.ToString())
            .Filter("deleted", Op.Equals, "false")
            .Filter("deal_status_id", Op.Equals, "2")
            .Get(ct);
        var dealIds = (dealResp.Models ?? new List<Deal>()).Select(d => d.Id).ToList();
        if (dealIds.Count == 0) return new HashSet<int>();

        var result = new HashSet<int>();
        // Postgrest `in` filter accepts a comma-separated list — batch in chunks
        foreach (var chunk in dealIds.Chunk(50))
        {
            var dpResp = await _supabase.From<DealProduct>()
                .Select("product_id, deal_id, deleted")
                .Filter("deal_id", Op.In, chunk.Select(id => id.ToString()).ToList())
                .Filter("deleted", Op.Equals, "false")
                .Get(ct);
            foreach (var dp in dpResp.Models ?? new List<DealProduct>())
                result.Add(dp.ProductId);
        }
        return result;
    }

    private async Task<HashSet<string>> GetActiveDealProductUrlsForStoreAsync(int storeId, CancellationToken ct)
    {
        var dealResp = await _supabase.From<Deal>()
            .Select("id")
            .Filter("store_id", Op.Equals, storeId.ToString())
            .Filter("deleted", Op.Equals, "false")
            .Get(ct);
        var dealIds = (dealResp.Models ?? new List<Deal>()).Select(d => d.Id).ToList();
        if (dealIds.Count == 0) return new HashSet<string>();

        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in dealIds.Chunk(50))
        {
            var dpResp = await _supabase.From<DealProduct>()
                .Select("url, deleted")
                .Filter("deal_id", Op.In, chunk.Select(id => id.ToString()).ToList())
                .Filter("deleted", Op.Equals, "false")
                .Get(ct);
            foreach (var dp in dpResp.Models ?? new List<DealProduct>())
            {
                var n = NormalizeUrl(dp.Url);
                if (!string.IsNullOrWhiteSpace(n)) urls.Add(n);
            }
        }
        return urls;
    }

    private async Task<HashSet<string>> GetPendingCandidateUrlsForStoreAsync(int storeId, CancellationToken ct)
    {
        var resp = await _supabase.From<DealCandidate>()
            .Select("deal_url_canonical, status, store_id")
            .Filter("store_id", Op.Equals, storeId.ToString())
            .Filter("status", Op.Equals, "pending_review")
            .Get(ct);
        return new HashSet<string>(
            (resp.Models ?? new List<DealCandidate>())
                .Select(dc => dc.DealUrlCanonical ?? string.Empty)
                .Where(u => !string.IsNullOrWhiteSpace(u)),
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task<List<Product>> GetCandidateProductsAsync(int? productTypeId, CancellationToken ct)
    {
        var query = _supabase.From<Product>()
            .Select("id, name, brand_id, product_type_id, enable_service, deleted")
            .Filter("deleted", Op.Equals, "false")
            .Filter("enable_service", Op.Equals, "true");
        if (productTypeId.HasValue)
            query = query.Filter("product_type_id", Op.Equals, productTypeId.Value.ToString());

        var resp = await query.Get(ct);
        return (resp.Models ?? new List<Product>()).ToList();
    }

    /// <summary>
    /// True when at least one active product matching <paramref name="productTypeId"/>
    /// (or any type, when null) was created strictly after <paramref name="since"/>.
    /// Used to bypass the per-endpoint 72-hour throttle when a fresh product
    /// gets added — we want the next timer tick to start looking for it
    /// instead of waiting up to three more days.
    /// </summary>
    private async Task<bool> AnyNewMatchingProductSinceAsync(int? productTypeId, DateTime since, CancellationToken ct)
    {
        var query = _supabase.From<Product>()
            .Select("id, created_at")
            .Filter("deleted", Op.Equals, "false")
            .Filter("enable_service", Op.Equals, "true")
            .Filter("created_at", Op.GreaterThan, since.ToString("o"))
            .Limit(1);
        if (productTypeId.HasValue)
            query = query.Filter("product_type_id", Op.Equals, productTypeId.Value.ToString());

        var resp = await query.Get(ct);
        return (resp.Models?.Count ?? 0) > 0;
    }

    private static (int? productId, double score) BestFuzzyMatch(string title, IReadOnlyList<Product> candidates)
    {
        var titleNorm = NormalizeName(title);
        if (string.IsNullOrWhiteSpace(titleNorm)) return (null, 0);

        int? bestId = null;
        double bestScore = 0;
        foreach (var p in candidates)
        {
            var pn = NormalizeName(p.Name);
            var s = TokenSetScore(titleNorm, pn);
            if (s > bestScore)
            {
                bestScore = s;
                bestId = p.Id;
            }
        }
        return (bestId, bestScore);
    }

    private static string NormalizeName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var lower = s.Trim().ToLowerInvariant();
        var alphanum = Regex.Replace(lower, "[^a-z0-9]+", " ");
        return Regex.Replace(alphanum, "\\s+", " ").Trim();
    }

    private static double TokenSetScore(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;
        var aTokens = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length > 1).ToHashSet();
        var bTokens = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length > 1).ToHashSet();
        if (aTokens.Count == 0 || bTokens.Count == 0) return 0;

        var lenGate = Math.Max(3, (int)Math.Round(0.5 * Math.Max(a.Length, b.Length)));
        if (Math.Abs(a.Length - b.Length) > lenGate) return 0;

        var shared = aTokens.Intersect(bTokens).Count();
        return (2.0 * shared) / (aTokens.Count + bTokens.Count);
    }

    /// <summary>
    /// Lowercase host (strip www.), strip fragment + known tracking params,
    /// trim trailing slash. Matches the API's NormaliseUrl shape for dedup keys.
    /// </summary>
    private static string NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        try
        {
            var uri = new Uri(url);
            var host = uri.Host.ToLowerInvariant();
            if (host.StartsWith("www.")) host = host[4..];

            var builder = new UriBuilder(uri)
            {
                Scheme = uri.Scheme.ToLowerInvariant(),
                Host = host,
                Fragment = string.Empty
            };

            if (!string.IsNullOrWhiteSpace(uri.Query))
            {
                var qs = System.Web.HttpUtility.ParseQueryString(uri.Query);
                var keysToRemove = qs.AllKeys
                    .Where(k => k != null && (
                        k.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) ||
                        k.Equals("fbclid", StringComparison.OrdinalIgnoreCase) ||
                        k.Equals("gclid", StringComparison.OrdinalIgnoreCase) ||
                        k.Equals("ref", StringComparison.OrdinalIgnoreCase) ||
                        k.Equals("tag", StringComparison.OrdinalIgnoreCase)
                    ))
                    .ToList();
                foreach (var key in keysToRemove) qs.Remove(key);
                builder.Query = qs.ToString();
            }

            return builder.Uri.ToString().TrimEnd('/');
        }
        catch
        {
            return url.Trim().ToLowerInvariant().TrimEnd('/');
        }
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
