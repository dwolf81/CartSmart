using System.Text.Json.Serialization;

namespace CartSmart.Core.Worker;

/// <summary>
/// Strongly-typed representation of listing_selectors within a store's scrape_config JSON.
/// Used by ListingPageScraper to locate individual listing items on a page.
/// </summary>
public sealed class ListingScrapeConfig
{
    [JsonPropertyName("container")]
    public string? Container { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("price")]
    public string? Price { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("condition")]
    public string? Condition { get; set; }

    [JsonPropertyName("next_page")]
    public string? NextPage { get; set; }
}

/// <summary>
/// A single listing extracted from an HTML page.
/// </summary>
public sealed record ScrapedListing(
    string ItemId,
    string? Title,
    string? Url,
    decimal? Price,
    string? Currency,
    int? ConditionCategoryId
);
