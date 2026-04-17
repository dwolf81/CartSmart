using CartSmart.API.Models;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace CartSmart.Core.Worker;

/// <summary>
/// Coordinates the deal ingestion pipeline:
/// 1. Collect: Poll due sources → store raw signals
/// 2. Process: AI extract → match products/stores → score → queue for review or auto-import
/// </summary>
public class IngestionPipelineOrchestrator : IIngestionPipelineOrchestrator
{
    private readonly IIngestionRepository _repo;
    private readonly IDealRepository _dealRepo;
    private readonly IEnumerable<ISignalSourceProvider> _sourceProviders;
    private readonly IAiDealExtractor _extractor;
    private readonly ILogger<IngestionPipelineOrchestrator> _logger;
    private readonly decimal _autoImportMinConfidence;

    public IngestionPipelineOrchestrator(
        IIngestionRepository repo,
        IDealRepository dealRepo,
        IEnumerable<ISignalSourceProvider> sourceProviders,
        IAiDealExtractor extractor,
        ILogger<IngestionPipelineOrchestrator> logger,
        decimal autoImportMinConfidence = 0.90m)
    {
        _repo = repo;
        _dealRepo = dealRepo;
        _sourceProviders = sourceProviders;
        _extractor = extractor;
        _logger = logger;
        _autoImportMinConfidence = autoImportMinConfidence;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Phase 1: Collect raw signals from all due sources
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<IngestionRunResult> CollectSignalsAsync(CancellationToken ct)
    {
        var dueSources = await _repo.GetDueSourcesAsync(ct);
        _logger.LogInformation("Ingestion collect: {Count} sources due for polling", dueSources.Count);

        int collected = 0, duplicates = 0, errors = 0;

        foreach (var source in dueSources)
        {
            if (ct.IsCancellationRequested) break;

            var provider = _sourceProviders.FirstOrDefault(p =>
                p.SourceType.ToString().Equals(source.SourceType, StringComparison.OrdinalIgnoreCase));

            if (provider is null)
            {
                _logger.LogWarning("No provider registered for source type '{Type}' (source {SourceId})", source.SourceType, source.Id);
                continue;
            }

            try
            {
                var signals = await provider.CollectAsync(source, ct);
                _logger.LogInformation("Source {SourceId} ({Name}): collected {Count} signals", source.Id, source.Name, signals.Count);

                foreach (var signal in signals)
                {
                    if (ct.IsCancellationRequested) break;

                    // Dedup by external ID
                    if (!string.IsNullOrWhiteSpace(signal.ExternalId) &&
                        await _repo.RawSignalExistsAsync(source.Id, signal.ExternalId, ct))
                    {
                        duplicates++;
                        continue;
                    }

                    await _repo.CreateRawSignalAsync(new RawSignal
                    {
                        IngestionSourceId = source.Id,
                        ExternalId = signal.ExternalId,
                        Title = signal.Title,
                        Body = signal.Body,
                        Url = signal.Url,
                        Author = signal.Author,
                        RawJson = signal.RawJson,
                        Status = "pending"
                    }, ct);

                    collected++;
                }

                await _repo.UpdateSourceLastPolledAsync(source.Id, DateTime.UtcNow, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error collecting from source {SourceId} ({Name})", source.Id, source.Name);
                errors++;
            }
        }

        _logger.LogInformation("Ingestion collect complete: collected={Collected}, duplicates={Dupes}, errors={Errors}",
            collected, duplicates, errors);

        return new IngestionRunResult(collected, 0, 0, 0, duplicates, errors);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Phase 2: Process pending signals → AI extract → match → score → import
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<IngestionRunResult> ProcessSignalsAsync(int batchSize, CancellationToken ct)
    {
        var pending = await _repo.GetPendingSignalsAsync(batchSize, ct);
        _logger.LogInformation("Ingestion process: {Count} pending signals to process", pending.Count);

        int extracted = 0, autoImported = 0, queued = 0, errors = 0;

        foreach (var signal in pending)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                // Mark as processing
                signal.Status = "processing";
                await _repo.UpdateRawSignalAsync(signal, ct);

                // AI extraction — multiple deals per signal
                var extractions = await _extractor.ExtractMultipleAsync(signal, ct);
                if (extractions.Count == 0)
                {
                    signal.Status = "failed";
                    signal.ErrorMessage = "AI extraction returned no deals";
                    signal.ProcessedAt = DateTime.UtcNow;
                    await _repo.UpdateRawSignalAsync(signal, ct);
                    errors++;
                    continue;
                }

                // Look up the ingestion source for sender verification
                var ingestionSource = await _repo.GetIngestionSourceBySignalIdAsync(signal.Id, ct);

                foreach (var extraction in extractions)
                {
                    // Match store first (needed for sender verification)
                    Store? matchedStore = null;
                    if (!string.IsNullOrWhiteSpace(extraction.StoreName))
                        matchedStore = await _repo.FindStoreByNameAsync(extraction.StoreName, ct);

                    // If the ingestion source is linked to a specific store, use that
                    if (matchedStore is null && ingestionSource?.StoreId is > 0)
                        matchedStore = await _repo.FindStoreByNameAsync("", ct); // fallback — we'll look up by ID below

                    // Sender verification: for email/social sources, verify the sender (Author)
                    // matches the store. E.g., email from "@callawaygolf.com" should match "Callaway Golf" store.
                    if (ingestionSource is not null && !string.IsNullOrWhiteSpace(signal.Author))
                    {
                        var sourceType = ingestionSource.SourceType?.ToLowerInvariant();
                        if (sourceType is "email" or "social")
                        {
                            if (matchedStore is not null && !VerifySenderMatchesStore(signal.Author, matchedStore))
                            {
                                _logger.LogWarning(
                                    "Sender verification failed: signal {SignalId} author '{Author}' does not match store '{Store}' (URL: {StoreUrl}). Skipping deal.",
                                    signal.Id, signal.Author, matchedStore.Name, matchedStore.URL);

                                // Still create the extracted deal but with reduced confidence and note
                                var skippedDeal = await _repo.CreateExtractedDealAsync(new ExtractedDeal
                                {
                                    RawSignalId = signal.Id,
                                    StoreId = matchedStore.Id,
                                    Title = extraction.Title,
                                    Price = extraction.Price,
                                    Currency = extraction.Currency,
                                    CouponCode = extraction.CouponCode,
                                    Url = extraction.Url,
                                    DiscountPercent = extraction.DiscountPercent,
                                    DealTypeId = extraction.DealTypeId,
                                    ExpirationDate = extraction.ExpirationDate,
                                    ConfidenceScore = Math.Min(extraction.ConfidenceScore, 0.3m),
                                    AiReasoning = $"[SENDER MISMATCH] Author '{signal.Author}' does not match store '{matchedStore.Name}'. {extraction.Reasoning}",
                                    StoreWide = extraction.IsStoreWide,
                                    Status = "pending_review"
                                }, ct);

                                queued++;
                                continue;
                            }
                        }
                    }

                    // Auto-reject email signals that advertise products we don't carry (unless store-wide)
                    bool isEmailSource = ingestionSource?.SourceType?.Equals("email", StringComparison.OrdinalIgnoreCase) == true;

                    // Handle product-specific deals with multiple products
                    if (!extraction.IsStoreWide && extraction.Products is { Count: > 0 })
                    {
                        foreach (var productInfo in extraction.Products)
                        {
                            var matchedProduct = await _repo.FindProductByNameFuzzyAsync(
                                productInfo.ProductName, productInfo.ProductBrand, ct);

                            if (isEmailSource && matchedProduct is null)
                            {
                                _logger.LogInformation(
                                    "Auto-rejecting email deal for unmatched product '{Product}' from signal {SignalId}",
                                    productInfo.ProductName, signal.Id);
                                await _repo.CreateExtractedDealAsync(new ExtractedDeal
                                {
                                    RawSignalId = signal.Id,
                                    StoreId = matchedStore?.Id,
                                    Title = $"{extraction.Title} — {productInfo.ProductName}",
                                    Price = productInfo.Price ?? extraction.Price,
                                    Currency = extraction.Currency,
                                    CouponCode = productInfo.CouponCode ?? extraction.CouponCode,
                                    Url = productInfo.Url ?? extraction.Url,
                                    DiscountPercent = productInfo.DiscountPercent ?? extraction.DiscountPercent,
                                    DealTypeId = extraction.DealTypeId,
                                    ExpirationDate = extraction.ExpirationDate,
                                    ConfidenceScore = 0m,
                                    AiReasoning = $"[AUTO-REJECT] Product '{productInfo.ProductName}' not found in catalog. {extraction.Reasoning}",
                                    StoreWide = false,
                                    Status = "auto_rejected"
                                }, ct);
                                extracted++;
                                continue;
                            }

                            int? discountPct = productInfo.DiscountPercent ?? extraction.DiscountPercent;
                            decimal? price = productInfo.Price ?? extraction.Price;

                            if (discountPct is null && price.HasValue && matchedProduct?.MSRP is > 0)
                            {
                                discountPct = (int)Math.Round((1m - price.Value / (decimal)matchedProduct.MSRP.Value) * 100m);
                                if (discountPct < 0) discountPct = null;
                            }

                            var extractedDeal = await _repo.CreateExtractedDealAsync(new ExtractedDeal
                            {
                                RawSignalId = signal.Id,
                                ProductId = matchedProduct?.Id,
                                StoreId = matchedStore?.Id,
                                Title = $"{extraction.Title} — {productInfo.ProductName}",
                                Price = price,
                                Currency = extraction.Currency,
                                CouponCode = productInfo.CouponCode ?? extraction.CouponCode,
                                Url = productInfo.Url ?? extraction.Url,
                                DiscountPercent = discountPct,
                                DealTypeId = extraction.DealTypeId,
                                ExpirationDate = extraction.ExpirationDate,
                                ConfidenceScore = extraction.ConfidenceScore,
                                AiReasoning = extraction.Reasoning,
                                StoreWide = false,
                                Status = "pending_review"
                            }, ct);

                            extracted++;

                            if (extraction.ConfidenceScore >= _autoImportMinConfidence &&
                                matchedProduct is not null && matchedStore is not null && price.HasValue)
                            {
                                if (await TryAutoImportAsync(extractedDeal, matchedProduct, matchedStore, ct))
                                {
                                    autoImported++;
                                    continue;
                                }
                            }
                            queued++;
                        }
                    }
                    else
                    {
                        // Single deal or store-wide deal — original flow
                        Product? matchedProduct = null;
                        if (!extraction.IsStoreWide && !string.IsNullOrWhiteSpace(extraction.ProductName))
                            matchedProduct = await _repo.FindProductByNameFuzzyAsync(extraction.ProductName, extraction.ProductBrand, ct);

                        if (isEmailSource && !extraction.IsStoreWide && matchedProduct is null)
                        {
                            _logger.LogInformation(
                                "Auto-rejecting email deal '{Title}' — product not in catalog (signal {SignalId})",
                                extraction.Title, signal.Id);
                            await _repo.CreateExtractedDealAsync(new ExtractedDeal
                            {
                                RawSignalId = signal.Id,
                                StoreId = matchedStore?.Id,
                                Title = extraction.Title,
                                Price = extraction.Price,
                                Currency = extraction.Currency,
                                CouponCode = extraction.CouponCode,
                                Url = extraction.Url,
                                DiscountPercent = extraction.DiscountPercent,
                                DealTypeId = extraction.DealTypeId,
                                ExpirationDate = extraction.ExpirationDate,
                                ConfidenceScore = 0m,
                                AiReasoning = $"[AUTO-REJECT] Product not found in catalog. {extraction.Reasoning}",
                                StoreWide = false,
                                Status = "auto_rejected"
                            }, ct);
                            extracted++;
                            continue;
                        }

                        int? discountPercent = extraction.DiscountPercent;
                        if (discountPercent is null && extraction.Price.HasValue && matchedProduct?.MSRP is > 0)
                        {
                            discountPercent = (int)Math.Round((1m - extraction.Price.Value / (decimal)matchedProduct.MSRP.Value) * 100m);
                            if (discountPercent < 0) discountPercent = null;
                        }

                        var extractedDeal = await _repo.CreateExtractedDealAsync(new ExtractedDeal
                        {
                            RawSignalId = signal.Id,
                            ProductId = matchedProduct?.Id,
                            StoreId = matchedStore?.Id,
                            Title = extraction.Title,
                            Price = extraction.Price,
                            Currency = extraction.Currency,
                            CouponCode = extraction.CouponCode,
                            Url = extraction.Url,
                            DiscountPercent = discountPercent,
                            DealTypeId = extraction.DealTypeId,
                            ExpirationDate = extraction.ExpirationDate,
                            ConfidenceScore = extraction.ConfidenceScore,
                            AiReasoning = extraction.Reasoning,
                            StoreWide = extraction.IsStoreWide,
                            Status = "pending_review"
                        }, ct);

                        extracted++;

                        // Auto-import: for store-wide, only need store match; for product-specific, need both
                        bool canAutoImport = extraction.ConfidenceScore >= _autoImportMinConfidence &&
                            matchedStore is not null && extraction.Price.HasValue;
                        if (!extraction.IsStoreWide)
                            canAutoImport = canAutoImport && matchedProduct is not null;

                        if (canAutoImport)
                        {
                            if (await TryAutoImportAsync(extractedDeal, matchedProduct, matchedStore!, ct))
                            {
                                autoImported++;
                                continue;
                            }
                        }
                        queued++;
                    }
                }

                // Mark signal as successfully extracted
                signal.Status = "extracted";
                signal.ProcessedAt = DateTime.UtcNow;
                await _repo.UpdateRawSignalAsync(signal, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing signal {SignalId}", signal.Id);
                signal.Status = "failed";
                signal.ErrorMessage = ex.Message;
                signal.ProcessedAt = DateTime.UtcNow;
                try { await _repo.UpdateRawSignalAsync(signal, ct); } catch { /* best effort */ }
                errors++;
            }
        }

        _logger.LogInformation(
            "Ingestion process complete: extracted={Extracted}, auto_imported={Auto}, queued={Queued}, errors={Errors}",
            extracted, autoImported, queued, errors);

        return new IngestionRunResult(0, extracted, autoImported, queued, 0, errors);
    }

    // ─── Sender verification ────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that the signal author (email sender or social account) plausibly matches the store.
    /// For emails: extracts the domain and compares to the store URL domain.
    /// For social: compares the author name / handle against the store name.
    /// </summary>
    private static bool VerifySenderMatchesStore(string author, Store store)
    {
        if (string.IsNullOrWhiteSpace(author)) return true; // no author to verify

        // Extract domain from email address (e.g., "deals@callawaygolf.com" → "callawaygolf.com")
        var emailDomain = ExtractEmailDomain(author);

        // Extract domain from store URL (e.g., "https://www.callawaygolf.com" → "callawaygolf.com")
        var storeUrlDomain = ExtractDomainFromUrl(store.URL);

        if (!string.IsNullOrWhiteSpace(emailDomain) && !string.IsNullOrWhiteSpace(storeUrlDomain))
        {
            // Compare root domains (strip www and subdomains)
            var emailRoot = GetRootDomain(emailDomain);
            var storeRoot = GetRootDomain(storeUrlDomain);
            if (string.Equals(emailRoot, storeRoot, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Fallback: check if store name appears in the author field (for social media handles, display names)
        if (!string.IsNullOrWhiteSpace(store.Name))
        {
            var normalizedAuthor = Regex.Replace(author, @"[^a-zA-Z0-9]", "").ToLowerInvariant();
            var normalizedStore = Regex.Replace(store.Name, @"[^a-zA-Z0-9]", "").ToLowerInvariant();

            if (normalizedAuthor.Contains(normalizedStore) || normalizedStore.Contains(normalizedAuthor))
                return true;
        }

        return false;
    }

    /// <summary>Extracts the domain from an email address like "Name &lt;deals@store.com&gt;" → "store.com".</summary>
    private static string? ExtractEmailDomain(string author)
    {
        // Handle "Display Name <email@domain.com>" format
        var match = Regex.Match(author, @"[\w.+-]+@([\w.-]+\.\w+)");
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>Extracts the domain from a URL.</summary>
    private static string? ExtractDomainFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Host.ToLowerInvariant();
        return null;
    }

    /// <summary>Strips subdomains to get the root domain: "email.callawaygolf.com" → "callawaygolf.com".</summary>
    private static string GetRootDomain(string domain)
    {
        var parts = domain.Split('.');
        return parts.Length >= 2
            ? $"{parts[^2]}.{parts[^1]}"
            : domain;
    }

    // ─── Auto-import ────────────────────────────────────────────────────────────

    private async Task<bool> TryAutoImportAsync(ExtractedDeal ed, Product? product, Store store, CancellationToken ct)
    {
        try
        {
            // Create deal
            var deal = await _dealRepo.CreateDealAsync(new Deal
            {
                DealStatusId = 2, // Active
                UserId = 1,       // System user
                StoreId = store.Id,
                DealTypeId = ed.DealTypeId ?? 1,
                CouponCode = ed.CouponCode,
                DiscountPercent = ed.DiscountPercent,
                ExternalOfferUrl = ed.Url,
                ExpirationDate = ed.ExpirationDate,
                StoreWide = ed.StoreWide
            }, ct);

            // Create deal product (only for product-specific deals)
            if (product is not null)
            {
                await _dealRepo.CreateDealProductAsync(new DealProduct
                {
                    DealId = deal.Id,
                    ProductId = (int)product.Id,
                    Price = ed.Price ?? 0m,
                    Url = ed.Url ?? string.Empty,
                    DealStatusId = 2, // Active
                    Primary = true,
                    FreeShipping = false,
                    ShortDescription = ed.Title
                }, ct);

                // Update best deal pointer
                await _dealRepo.UpdateProductBestDealAsync((int)product.Id, ct);
            }

            // Mark extracted deal as imported
            ed.Status = "auto_imported";
            ed.DealId = deal.Id;
            ed.ImportedAt = DateTime.UtcNow;
            await _repo.UpdateExtractedDealAsync(ed, ct);

            _logger.LogInformation(
                "Auto-imported deal {DealId} for product {ProductId} from signal {SignalId} (confidence={Confidence:F2}){StoreWide}",
                deal.Id, product?.Id, ed.RawSignalId, ed.ConfidenceScore, ed.StoreWide ? " [STORE-WIDE]" : "");

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to auto-import extracted deal {ExtractedDealId}", ed.Id);
            return false;
        }
    }
}
