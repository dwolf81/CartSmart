# CartSmart Deal Service

Azure Functions backend to periodically refresh deals from external stores using APIs when available or HTML scraping fallback.

## Projects
- CartSmart.Core: Core interfaces, orchestrator, repository, shared models.
- CartSmart.Providers: Store-specific provider implementations (eBay stub now).
- CartSmart.Scraping: Generic HTML scraper using AngleSharp.
- CartSmart.Functions: Azure Functions host with timer trigger.
- CartSmart.Tests: Unit tests (TBD) for orchestrator logic.

## Environment Variables
Set `SUPABASE_URL` and `SUPABASE_KEY` for repository access.

## Timer Schedule
`0 */5 * * * *` (every 5 minutes)

## Extending Providers
1. Implement `IStoreClient`.
2. Register via `services.AddSingleton<IStoreClient, NewStoreClient>();` and optionally HttpClient.
3. Add heuristics for URL inference in `DealUpdateOrchestrator` or map via `Store` entity.

## Price History
`DealPriceHistory` table assumed as `deal_price_history` with columns (id, deal_id, price, currency, changed_at).

## Future Improvements
See TODO: distributed queue scaling, proper status fields, richer parsing, anti-bot strategies, telemetry, retry policies, structured logging, resilience.
