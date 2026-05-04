using CartSmart.API.Models.DTOs;
using CartSmart.API.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CartSmart.Functions;

/// <summary>
/// Async worker function for social card image generation.
/// Receives messages from Azure Queue Storage (social-card-queue by default)
/// and generates PNG card images for social media posts.
/// 
/// Queue message format: JSON-serialized SocialCardQueueMessage
/// Scalable: processes one message at a time; queue can buffer spikes.
/// Retry: Azure Functions runtime handles dead-letter after max retries.
/// </summary>
public class ProcessSocialCardQueueFunction
{
    private readonly ISocialCardOrchestrator _orchestrator;
    private readonly ILogger<ProcessSocialCardQueueFunction> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ProcessSocialCardQueueFunction(
        ISocialCardOrchestrator orchestrator,
        ILogger<ProcessSocialCardQueueFunction> logger)
    {
        _orchestrator = orchestrator ?? 
            throw new ArgumentNullException(nameof(orchestrator));
        _logger = logger ?? 
            throw new ArgumentNullException(nameof(logger));
    }

    [Function("ProcessSocialCardQueue")]
    public async Task Run(
        [QueueTrigger("social-card-queue", Connection = "AzureWebJobsStorage")] string queueMessage,
        FunctionContext context,
        CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation(
                "ProcessSocialCardQueue: processing message (length={MessageLength} bytes)",
                queueMessage?.Length ?? 0);

            if (string.IsNullOrWhiteSpace(queueMessage))
            {
                _logger.LogWarning("ProcessSocialCardQueue: received empty queue message, skipping");
                return;
            }

            // Deserialize the queue message
            var message = JsonSerializer.Deserialize<SocialCardQueueMessage>(queueMessage, JsonOpts);
            if (message == null)
            {
                _logger.LogWarning(
                    "ProcessSocialCardQueue: failed to deserialize queue message: {Message}",
                    queueMessage);
                return;
            }

            _logger.LogInformation(
                "ProcessSocialCardQueue: parsed message for post {PostId}, product '{ProductName}'",
                message.SocialPostId, message.ProductName);

            // Process the social card
            var result = await _orchestrator.ProcessSocialCardAsync(
                socialPostId: message.SocialPostId,
                productName: message.ProductName,
                productImageUrl: message.ProductImageUrl,
                currentPrice: message.CurrentPrice,
                originalPrice: message.OriginalPrice,
                dealTypeId: message.DealTypeId,
                dealTypeName: message.DealTypeName,
                couponCode: message.CouponCode,
                storeName: message.StoreName,
                storeImageUrl: message.StoreImageUrl,
                conditionName: message.ConditionName,
                variantDetails: message.VariantDetails,
                itemCount: message.ItemCount,
                freeShipping: message.FreeShipping,
                ct: ct);

            if (result.Success)
            {
                _logger.LogInformation(
                    "ProcessSocialCardQueue: successfully generated card for post {PostId}. Image size: {Size} bytes",
                    result.SocialPostId,
                    result.ImageDataUri.Length);
            }
            else
            {
                _logger.LogError(
                    "ProcessSocialCardQueue: failed to generate card for post {PostId}. Error: {Error}",
                    result.SocialPostId, result.ErrorMessage);

                // Azure Functions runtime automatically retries queue-triggered functions
                // Failed messages are dead-lettered after max retries (configurable in host.json).
                // Throwing here will trigger the runtime's retry mechanism.
                throw new InvalidOperationException(
                    $"Failed to generate social card for post {result.SocialPostId}: {result.ErrorMessage}");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("ProcessSocialCardQueue: operation cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "ProcessSocialCardQueue: unexpected error processing message: {Message}",
                queueMessage);
            throw; // Re-throw to trigger retry by Azure Functions runtime
        }
    }
}
