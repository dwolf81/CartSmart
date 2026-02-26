using CartSmart.Core.Worker;

public sealed class NoopHtmlScraper : IHtmlScraper
{
    public Task<ScrapeResult?> ScrapeAsync(Uri uri, string[]? overridePriceSelectors, CancellationToken ct)
        => Task.FromResult<ScrapeResult?>(null);
}
