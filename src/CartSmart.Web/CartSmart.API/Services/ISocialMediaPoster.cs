namespace CartSmart.API.Services;

public interface ISocialMediaPoster
{
    /// <summary>Identifies the platform, e.g. "twitter", "facebook", "instagram".</summary>
    string Platform { get; }

    /// <summary>False when the platform credentials are not configured — post is skipped.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Posts content to the platform. Returns true on success.
    /// Implementations should not throw — they must log and return false on failure.
    /// </summary>
    Task<bool> PostAsync(string caption, string? imageUrl, string? linkUrl, CancellationToken ct = default);
}
