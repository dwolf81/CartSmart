using CartSmart.Core.Worker;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CartSmart.Functions;

/// <summary>
/// Polls all due ingestion sources and stores raw signals for processing.
/// Runs every 15 minutes. Sources have their own poll_interval_minutes setting
/// so not all sources are polled on every run.
/// </summary>
public class CollectIngestionSignalsFunction
{
    private readonly IIngestionPipelineOrchestrator _orchestrator;
    private readonly ILogger<CollectIngestionSignalsFunction> _logger;

    public CollectIngestionSignalsFunction(IIngestionPipelineOrchestrator orchestrator, ILogger<CollectIngestionSignalsFunction> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    [Function("CollectIngestionSignals")]
    public async Task Run([TimerTrigger("0 */5 * * * *", UseMonitor = true)] TimerInfo timerInfo, CancellationToken ct)
    {
        _logger.LogInformation("CollectIngestionSignals started at {Time}", DateTime.UtcNow);
        var result = await _orchestrator.CollectSignalsAsync(ct);
        _logger.LogInformation(
            "CollectIngestionSignals completed: collected={Collected}, duplicates={Dupes}, errors={Errors}",
            result.SignalsCollected, result.DuplicatesSkipped, result.Errors);
    }
}
