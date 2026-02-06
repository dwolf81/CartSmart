using CartSmart.Core.Worker;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CartSmart.Functions;

public class RefreshDealsFunction
{
    private readonly IDealUpdateOrchestrator _orchestrator;
    private readonly ILogger<RefreshDealsFunction> _logger;

    public RefreshDealsFunction(IDealUpdateOrchestrator orchestrator, ILogger<RefreshDealsFunction> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    // Production schedule: every 5 minutes. Cron: second minute hour day month dayOfWeek (UTC)
    [Function("RefreshDeals")]
    public async Task Run([TimerTrigger("0 */5 * * * *", UseMonitor = true)] TimerInfo timerInfo, CancellationToken ct)
    {
        _logger.LogInformation("Deal refresh started at {Time}", DateTime.UtcNow);
        var result = await _orchestrator.RefreshDealsAsync(batchSize:50, ct);
        _logger.LogInformation("Deal refresh completed. Total={Total} Updated={Updated} Expired={Expired} Sold={Sold} Errors={Errors}",
            result.Total, result.Updated, result.Expired, result.Sold, result.Errors);
    }
}