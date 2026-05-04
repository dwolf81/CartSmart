# ProcessSocialCardQueue Usage Guide

## Enqueuing Cards for Generation

The queue-based system allows you to decouple card image generation from the main post creation flow. Instead of synchronously generating the card (which can take 5-15 seconds), you can enqueue a message and process it asynchronously.

### Option 1: Manual Queue Enqueuing (From API)

To manually enqueue a social card generation request from an API endpoint or service:

```csharp
using Azure.Storage.Queues;
using CartSmart.API.Models.DTOs;
using System.Text.Json;

// Get queue client (inject or create)
var queueClient = new QueueClient(
    new Uri($"https://{storageAccountName}.queue.core.windows.net/social-card-queue"),
    new DefaultAzureCredential());

// Create your message
var message = new SocialCardQueueMessage
{
    SocialPostId = 123,
    ProductName = "Nike Air Max",
    ProductImageUrl = "https://example.com/image.jpg",
    CurrentPrice = 89.99m,
    OriginalPrice = 149.99m,
    DealTypeId = 1,
    DealTypeName = "Direct Deal",
    CouponCode = "SAVE20",
    StoreName = "Nike Store",
    StoreImageUrl = "https://example.com/store.png",
    ConditionName = "New",
    FreeShipping = true,
    RetryCount = 0
};

// Serialize and send to queue
var messageJson = JsonSerializer.Serialize(message);
await queueClient.SendMessageAsync(messageJson);
```

### Option 2: Modifying Existing SocialPostService (Async Generation)

To modify the existing `GenerateDailyPostsAsync()` method to use queuing instead of synchronous generation:

**Before (Synchronous)**:
```csharp
// Generate deal card image directly (blocks for 5-15 seconds)
var cardBytes = await _cardImageService.GenerateAsync(cardData, ct);
```

**After (Async via Queue)**:
```csharp
// Enqueue for async processing
var queueMessage = new SocialCardQueueMessage
{
    SocialPostId = inserted.Id,
    ProductName = post.ProductName ?? string.Empty,
    ProductImageUrl = post.ProductImage,
    CurrentPrice = post.CurrentPrice,
    OriginalPrice = post.OriginalPrice,
    DealTypeId = dealDetails?.DealTypeId,
    DealTypeName = dealDetails?.DealTypeName,
    CouponCode = dealDetails?.CouponCode,
    StoreName = null,
    StoreImageUrl = null,
    ConditionName = null,
    FreeShipping = false,
    RetryCount = 0
};

// Send to queue
var messageJson = JsonSerializer.Serialize(queueMessage);
await queueClient.SendMessageAsync(messageJson);

_logger.LogInformation("GenerateDailyPosts: enqueued card generation for post {PostId}", inserted.Id);
```

## Configuration

### Azure Functions Local Settings
Add this to `local.settings.json`:

```json
{
  "AzureWebJobsStorage": "UseDevelopmentStorage=true",
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "SUPABASE_URL": "https://your-project.supabase.co",
    "SUPABASE_SERVICE_ROLE_KEY": "your-service-role-key"
  }
}
```

### Production Configuration
In Azure Functions App Settings:
- `AzureWebJobsStorage`: Connection string to production storage account
- `SUPABASE_URL`: Your Supabase project URL
- `SUPABASE_SERVICE_ROLE_KEY`: Service role key for database access

## Monitoring & Troubleshooting

### Check Queue Status
```bash
# Using Azure Storage Explorer or CLI
az storage queue show --name social-card-queue --connection-string "YOUR_CONNECTION_STRING"
```

### View Function Logs
```bash
# For local development
func host start --verbose

# In Azure
az functionapp log stream --name YOUR_FUNCTION_APP --resource-group YOUR_RG
```

### Common Issues

#### Queue Message Not Processing
- Verify `AzureWebJobsStorage` connection string is correct
- Check if queue `social-card-queue` exists (auto-created on first message)
- Review function logs for parsing errors

#### Images Not Updating
- Check Supabase connection and credentials
- Verify service-role key has write access to social_post table
- Review orchestrator logs for rendering failures

#### Playwright Errors
- Ensure Playwright dependencies are installed
- For Azure Linux: Playwright Azure Functions binaries required
- Check AppInsights for detailed error messages

## Performance Tuning

### Scaling Out
```csharp
// In host.json for multiple concurrent processors
{
  "functionTimeout": "00:10:00",
  "MaxConcurrentFunctions": 10
}
```

### Batch Processing
To process multiple cards in one function, modify the queue trigger:

```csharp
[QueueTrigger("social-card-queue", Connection = "AzureWebJobsStorage")] string[] queueMessages
```

### Rate Limiting
To avoid overwhelming Supabase:
```csharp
// Add delay between database updates
await Task.Delay(TimeSpan.FromMilliseconds(500));
```

## Example: Controller Endpoint

Here's an example controller endpoint that manually triggers card generation:

```csharp
[ApiController]
[Route("api/social/cards")]
public class SocialCardController : ControllerBase
{
    private readonly QueueClient _queueClient;
    private readonly ILogger<SocialCardController> _logger;

    public SocialCardController(QueueClient queueClient, ILogger<SocialCardController> logger)
    {
        _queueClient = queueClient;
        _logger = logger;
    }

    [HttpPost("regenerate/{postId}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> RegenerateCard(long postId, CancellationToken ct)
    {
        var message = new SocialCardQueueMessage
        {
            SocialPostId = postId,
            ProductName = "Product",
            CurrentPrice = 99.99m,
            RetryCount = 0
        };

        var messageJson = JsonSerializer.Serialize(message);
        await _queueClient.SendMessageAsync(messageJson, cancellationToken: ct);

        _logger.LogInformation("Card regeneration queued for post {PostId}", postId);
        return Accepted(new { postId, queued = true });
    }
}
```

Register in Startup:
```csharp
services.AddSingleton(sp =>
{
    var connectionString = configuration.GetConnectionString("AzureWebJobsStorage");
    var queueClient = new QueueClient(
        new Uri($"https://{GetStorageAccountName(connectionString)}.queue.core.windows.net/social-card-queue"),
        new DefaultAzureCredential());
    return queueClient;
});
```

## Dead-Letter Handling

Messages that fail after max retries go to the poison queue. To handle them:

```csharp
[Function("ProcessSocialCardDeadLetter")]
public async Task RunDeadLetter(
    [QueueTrigger("social-card-queue-poison")] string queueMessage,
    ILogger log)
{
    log.LogError("Social card generation failed after retries: {Message}", queueMessage);
    
    // Send alert, store in database, etc.
    // Manually trigger investigation
}
```

## Testing

### Unit Test Example
```csharp
[Test]
public async Task ProcessSocialCard_Success_UpdatesPost()
{
    // Arrange
    var mockService = new Mock<ISocialCardImageService>();
    mockService.Setup(x => x.GenerateAsync(It.IsAny<SocialCardData>(), CancellationToken.None))
        .ReturnsAsync(new byte[] { 137, 80, 78, 71 }); // PNG magic bytes
    
    var orchestrator = new SocialCardOrchestrator(
        mockService.Object,
        mockSupabase.Object,
        mockLogger.Object);

    // Act
    var result = await orchestrator.ProcessSocialCardAsync(
        socialPostId: 1,
        productName: "Test Product",
        currentPrice: 99.99m,
        // ... other params
        ct: CancellationToken.None);

    // Assert
    Assert.IsTrue(result.Success);
    Assert.That(result.ImageDataUri, Does.StartWith("data:image/png;base64,"));
}
```
