using CartSmart.Core.Worker;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CartSmart.Functions;

/// <summary>
/// Processes pending raw signals: AI extraction → product/store matching → scoring → auto-import or queue for review.
/// Runs every 5 minutes.
/// </summary>
public class ProcessIngestionSignalsFunction
{
    private readonly IIngestionPipelineOrchestrator _orchestrator;
    private readonly ILogger<ProcessIngestionSignalsFunction> _logger;
    private readonly IConfiguration _config;

    public ProcessIngestionSignalsFunction(
        IIngestionPipelineOrchestrator orchestrator,
        ILogger<ProcessIngestionSignalsFunction> logger,
        IConfiguration config)
    {
        _orchestrator = orchestrator;
        _logger = logger;
        _config = config;
    }

    [Function("ProcessIngestionSignals")]
    public async Task Run([TimerTrigger("0 */60 * * * *", UseMonitor = true)] TimerInfo timerInfo, CancellationToken ct)
    {
        var batchSize = int.TryParse(_config["Values:IngestionProcessBatchSize"] ?? _config["IngestionProcessBatchSize"], out var b) ? b : 20;

        _logger.LogInformation("ProcessIngestionSignals started at {Time}, batchSize={BatchSize}", DateTime.UtcNow, batchSize);
        var result = await _orchestrator.ProcessSignalsAsync(batchSize, ct);
        _logger.LogInformation(
            "ProcessIngestionSignals completed: extracted={Extracted}, auto_imported={Auto}, queued={Queued}, errors={Errors}",
            result.SignalsExtracted, result.DealsAutoImported, result.DealsQueuedForReview, result.Errors);
    }
}
