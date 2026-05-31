namespace CartSmart.API.Services;

/// <summary>
/// Decides whether an SPA request path should be served with HTTP 200 or 404.
/// Used by the catch-all fallback in Program.cs so that Google sees a real 404
/// for non-existent product/store/category slugs and for paths that don't map
/// to any React route — fixes "Soft 404" findings in Search Console where
/// invalid URLs were previously returning 200 + the SPA shell.
/// </summary>
public interface ISpaRouteValidator
{
    /// <summary>
    /// Returns 200 if the path corresponds to a real, indexable SPA page, or
    /// 404 otherwise. Results are cached so repeated lookups by crawlers don't
    /// hammer the database.
    /// </summary>
    Task<int> ResolveStatusAsync(string path, CancellationToken ct = default);
}
