namespace CartSmart.Core.Worker;

public sealed class ScrapeResult
{
    public string? Html { get; init; }
    public decimal? ExtractedPrice { get; init; }
    public string? Currency { get; init; }
    public bool? InStock { get; init; }
    public bool? Sold { get; init; }
    public bool BlockedByBotProtection { get; init; }
    public Dictionary<string,string>? RawSignals { get; init; }
}