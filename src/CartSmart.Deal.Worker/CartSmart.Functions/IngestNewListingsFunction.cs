using CartSmart.Core.Worker;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CartSmart.Functions;

public class IngestNewListingsFunction
{
    private readonly IDealUpdateOrchestrator _orchestrator;
    private readonly ILogger<IngestNewListingsFunction> _logger;
    private readonly IConfiguration _config;
    private readonly IDealRepository _repo;

    public IngestNewListingsFunction(IDealUpdateOrchestrator orchestrator, ILogger<IngestNewListingsFunction> logger, IConfiguration config, IDealRepository repo)
    {
        _orchestrator = orchestrator;
        _logger = logger;
        _config = config;
        _repo = repo;
    }

    [Function("IngestNewListings")]
    public async Task Run([TimerTrigger("*/30 * * * * *", UseMonitor = true)] TimerInfo timerInfo, CancellationToken ct)
    {
        var storesRaw = _config["Values:IngestStores"] ?? _config["IngestStores"] ?? "ebay";
        var storeKeys = storesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                  .Select(s => s.ToLowerInvariant())
                                  .ToArray();

        // Build queries from active products rather than static terms
        var products = await _repo.GetActiveProductsAsync(ct);
        var queries = products
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => new NewListingQuery(p.Id, p.Name!))
            .ToList();

        int totalCreated = 0;
        foreach (var key in storeKeys)
        {
            if (key == "ebay")
            {
                _logger.LogInformation("Ingesting listings for {Store} at {Time}. Count={Count}", "ebay", DateTime.UtcNow, queries.Count);
                totalCreated += await _orchestrator.IngestNewListingsAsync(StoreType.Ebay, 5, queries, ct);
            }
            else
            {
                _logger.LogInformation("Store {Store} not yet supported for ingest.", key);
            }
        }
        _logger.LogInformation("Listings ingest completed. Created={Count}", totalCreated);
    }
}
