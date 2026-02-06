using CartSmart.Core.Worker;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CartSmart.Functions;

public class ExpireDealsFunction
{
    private readonly IDealUpdateOrchestrator _orchestrator;
    private readonly ILogger<ExpireDealsFunction> _logger;

    public ExpireDealsFunction(IDealUpdateOrchestrator orchestrator, ILogger<ExpireDealsFunction> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    // Production schedule: hourly at minute 5. Cron: second minute hour day month dayOfWeek (UTC)
    [Function("SweepExpiredDeals")]
    public async Task Run([TimerTrigger("0 5 * * * *", UseMonitor = true)] TimerInfo timerInfo, CancellationToken ct)
    {
        _logger.LogInformation("Expired deal sweep started at {Time}", DateTime.UtcNow);
        var count = await _orchestrator.SweepExpiredDealsAsync(ct);
        _logger.LogInformation("Expired deal sweep completed. Expired={Count}", count);
    }
}
