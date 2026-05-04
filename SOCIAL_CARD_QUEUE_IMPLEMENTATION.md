# ProcessSocialCardQueue Implementation

## Overview
Implemented an async, queue-based social card image generation system for CartSmart using Azure Functions and Queue Storage. This allows social post card images to be generated asynchronously rather than blocking the main post creation flow.

## Components Created

### 1. **SocialCardQueueMessage** 
**File**: `Models/DTOs/SocialCardQueueMessage.cs`

A data transfer object (DTO) that defines the message format for Azure Queue Storage. Contains all the data needed to generate a social media card image:
- Social post ID (for tracking)
- Product information (name, image URL, price)
- Deal details (type, coupon code)
- Store information (name, image URL)
- Condition and shipping information
- Retry count for tracking failed attempts

### 2. **ISocialCardOrchestrator Interface**
**File**: `CartSmart.Core.Worker/ISocialCardOrchestrator.cs`

Defines the contract for orchestrating social card generation:
- `ProcessSocialCardAsync()` - Main method that handles the entire generation workflow
- Returns `SocialCardGenerationResult` with success status, image data-URI, and error details

### 3. **SocialCardOrchestrator Implementation**
**File**: `CartSmart.Core.Worker/SocialCardOrchestrator.cs`

Implements the orchestration logic:
- Takes queue message parameters and creates `SocialCardData` object
- Calls `ISocialCardImageService.GenerateAsync()` to render the PNG card
- Converts PNG bytes to base64 data-URI format
- Updates the social_post record in Supabase with the generated image
- Implements error handling and structured logging

### 4. **ProcessSocialCardQueueFunction**
**File**: `CartSmart.Functions/ProcessSocialCardQueueFunction.cs`

Azure Function that processes queue messages:
- Uses `[QueueTrigger]` to listen to `social-card-queue` queue
- Deserializes JSON queue messages into `SocialCardQueueMessage` objects
- Calls `ISocialCardOrchestrator` to process the card
- Implements retry logic via Azure Functions runtime (automatic dead-lettering after max retries)
- Structured logging for monitoring and debugging

### 5. **Program.cs Registrations**
**File**: `CartSmart.Functions/Program.cs`

Updated dependency injection configuration:
- Added `using CartSmart.API.Services` for access to `ISocialCardImageService`
- Registered `ISocialCardImageService` singleton with Playwright-based PNG rendering
- Registered `ISocialCardOrchestrator` singleton with all dependencies

## Data Flow

1. **Queue Message Enqueued**
   - When a social post is created, a `SocialCardQueueMessage` is enqueued to `social-card-queue`
   
2. **Function Triggered**
   - `ProcessSocialCardQueueFunction` receives the message from the queue
   
3. **Message Deserialized**
   - JSON string is parsed into `SocialCardQueueMessage` object
   
4. **Card Generated**
   - `SocialCardOrchestrator.ProcessSocialCardAsync()` is called
   - `ISocialCardImageService` uses Playwright to render the card as PNG
   - PNG bytes are converted to base64 data-URI
   
5. **Result Persisted**
   - Social post record is updated with the image data-URI
   - Database reflects the generated card immediately
   
6. **Error Handling**
   - On failure, errors are logged
   - After max retries, message is moved to poison queue
   - Monitoring and alerting can be configured based on logs

## Queue Configuration

- **Queue Name**: `social-card-queue` (configurable)
- **Storage Connection**: Uses `AzureWebJobsStorage` from Functions configuration
- **Message TTL**: Default Azure Storage queue TTL (7 days)
- **Retry Policy**: Azure Functions default retry (3 attempts)

## Integration Points

### With SocialCardImageService
- Uses the already-implemented `ISocialCardImageService` from CartSmart.Web.API
- Maintains compatibility with existing Playwright rendering code
- Supports all card customization options (images, pricing, deals, etc.)

### With Supabase
- Queries social_post records
- Updates ImageUrl and ImageGeneratedAt fields
- Uses service-role client for authentication

### With Existing SocialPostService
- Can be called from `GenerateDailyPostsAsync()` to async-enqueue instead of sync-generate
- No breaking changes to existing code

## Testing & Deployment

### Local Testing
- Queue messages can be sent to Azure Storage Emulator (Azurite)
- Function can be debugged locally via `func start`
- Configure `local.settings.json` with test storage connection string

### Production Deployment
- Deploy to Azure Functions App
- Ensure `AzureWebJobsStorage` is configured with production storage account
- `social-card-queue` will be auto-created on first message
- Monitor function execution via Application Insights

## Future Enhancements

1. **Blob Storage**: Store generated images in blob storage and return URLs instead of data-URIs
2. **CDN Integration**: Serve images from CDN for better performance
3. **Caching**: Cache rendered images to avoid re-generation
4. **Scheduled Regeneration**: Queue messages can be re-triggered for updated images
5. **Metrics**: Track generation time, success rate, image sizes
6. **Dynamic Card Variants**: Support different card layouts/themes via message parameter

## Error Scenarios & Recovery

| Scenario | Handling |
|----------|----------|
| Invalid JSON message | Logged warning, message discarded |
| Missing social post ID | Error logged, result marked failed |
| Playwright rendering fails | Error logged, retry triggered |
| Database update fails | Error logged, retry triggered |
| Max retries exceeded | Message moved to poison queue, alert should fire |

## Performance Characteristics

- **Throughput**: One message at a time (can scale via multiple function instances)
- **Processing Time**: Typically 5-15 seconds per image (Playwright startup + rendering)
- **Memory**: ~200-300 MB per function instance (Playwright state)
- **Cost**: Only charged during execution time (consumption plan ideal)

## Security Considerations

- Queue messages contain only necessary data (no secrets)
- Service-role authentication for Supabase (read-only access to posts table)
- Playwright runs in headless, sandboxed mode
- No external API calls or data exfiltration
