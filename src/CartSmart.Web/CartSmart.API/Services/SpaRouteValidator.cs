using CartSmart.API.Models;
using Microsoft.Extensions.Caching.Memory;
using Op = Supabase.Postgrest.Constants.Operator;

namespace CartSmart.API.Services;

public sealed class SpaRouteValidator : ISpaRouteValidator
{
    private readonly ISupabaseService _supabase;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SpaRouteValidator> _logger;

    private static readonly TimeSpan PositiveCacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Exact, indexable static SPA paths (mirror of the static routes in App.jsx
    /// and the sitemap). Anything matching here returns 200 without a DB hit.
    /// </summary>
    private static readonly HashSet<string> KnownStaticPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/",
        "/login", "/signup",
        "/categories", "/stores",
        "/about", "/contact", "/faq",
        "/privacy", "/terms", "/cookies", "/cookie-policy", "/disclaimer",
        "/forgot-password", "/reset-password", "/activate",
        // Auth-walled / non-indexable, but a logged-in user navigating here
        // should still get 200 + the SPA shell. The fallback handler tags 200
        // responses on these with X-Robots-Tag: noindex separately.
        "/settings", "/feed", "/deal-review", "/profile",
        "/admin/manual-price", "/admin/scrape-report", "/admin/social-posts",
        "/admin/product-candidates", "/admin/deal-candidates",
    };

    /// <summary>
    /// Path prefixes that should always 200 regardless of any tail segment —
    /// e.g. /profile/jdoe should serve the SPA shell so the profile component
    /// can decide what to render. Profile usernames change too often to be
    /// worth a per-request DB check; we leave that to the React side.
    /// </summary>
    private static readonly string[] KnownStaticPrefixes = new[]
    {
        "/profile/", "/admin/",
    };

    public SpaRouteValidator(
        ISupabaseService supabase,
        IMemoryCache cache,
        ILogger<SpaRouteValidator> logger)
    {
        _supabase = supabase;
        _cache = cache;
        _logger = logger;
    }

    public async Task<int> ResolveStatusAsync(string path, CancellationToken ct = default)
    {
        var normalized = NormalizePath(path);
        var cacheKey = "spa-route:" + normalized.ToLowerInvariant();
        if (_cache.TryGetValue(cacheKey, out int cached))
            return cached;

        int status = await ResolveUncachedAsync(normalized, ct);

        _cache.Set(cacheKey, status, status == 200 ? PositiveCacheTtl : NegativeCacheTtl);
        return status;
    }

    private async Task<int> ResolveUncachedAsync(string path, CancellationToken ct)
    {
        // Empty path or root → 200.
        if (string.IsNullOrEmpty(path) || path == "/") return 200;

        // Known exact static SPA paths.
        if (KnownStaticPaths.Contains(path)) return 200;

        // Known SPA prefixes (no slug validation needed).
        foreach (var prefix in KnownStaticPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return 200;
        }

        // Asset-looking paths (e.g. /missing.png) — static-file middleware
        // already had a shot at these. If we're in the fallback they didn't
        // exist; don't serve the SPA shell for them.
        if (LooksLikeAsset(path)) return 404;

        // Dynamic, indexable routes — validate the slug against the DB.
        if (TryMatchSegment(path, "/products/", out var productSlug))
            return await ProductExistsAsync(productSlug!, ct) ? 200 : 404;

        if (TryMatchSegment(path, "/stores/", out var storeSlug))
            return await StoreExistsAsync(storeSlug!, ct) ? 200 : 404;

        if (TryMatchSegment(path, "/categories/", out var categorySlug))
            return await CategoryExistsAsync(categorySlug!, ct) ? 200 : 404;

        // No known pattern matched — let the SPA render its catch-all 404 page
        // but signal the not-found status to Google.
        return 404;
    }

    // ── DB existence checks ──────────────────────────────────────────────
    //
    // Each one mirrors the filters the sitemap uses so the set of "200" URLs
    // here matches the set of URLs we advertise to crawlers. If a row exists
    // but is excluded from the sitemap (e.g. unapproved store), we deliberately
    // return 404 so Google removes it from the index.

    private async Task<bool> ProductExistsAsync(string slug, CancellationToken ct)
    {
        var client = _supabase.GetServiceRoleClient();
        var resp = await client.From<Product>()
            .Select("id, slug, deleted, enable_service")
            .Filter("slug", Op.Equals, slug)
            .Filter("deleted", Op.Equals, "false")
            .Filter("enable_service", Op.Equals, "true")
            .Limit(1)
            .Get(ct);
        return (resp.Models?.Count ?? 0) > 0;
    }

    private async Task<bool> StoreExistsAsync(string slug, CancellationToken ct)
    {
        var client = _supabase.GetServiceRoleClient();
        var resp = await client.From<Store>()
            .Select("id, slug, approved")
            .Filter("slug", Op.Equals, slug)
            .Filter("approved", Op.Equals, "true")
            .Limit(1)
            .Get(ct);
        return (resp.Models?.Count ?? 0) > 0;
    }

    private async Task<bool> CategoryExistsAsync(string slug, CancellationToken ct)
    {
        var client = _supabase.GetServiceRoleClient();
        var resp = await client.From<ProductType>()
            .Select("id, slug")
            .Filter("slug", Op.Equals, slug)
            .Limit(1)
            .Get(ct);
        return (resp.Models?.Count ?? 0) > 0;
    }

    // ── Path utilities ───────────────────────────────────────────────────

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        // Strip trailing slash except for root.
        if (path.Length > 1 && path.EndsWith('/')) path = path[..^1];
        return path;
    }

    private static bool TryMatchSegment(string path, string prefix, out string? slug)
    {
        slug = null;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var tail = path[prefix.Length..];
        if (string.IsNullOrWhiteSpace(tail)) return false;
        if (tail.Contains('/')) return false; // nested segments aren't valid

        try { slug = Uri.UnescapeDataString(tail); }
        catch { slug = tail; }
        return !string.IsNullOrWhiteSpace(slug);
    }

    /// <summary>
    /// Treat any path whose last segment has a file extension as a static-asset
    /// request (e.g. /img/missing.png, /styles.css). The static-files middleware
    /// already tried and failed by the time we get here.
    /// </summary>
    private static bool LooksLikeAsset(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        var tail = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
        var dot = tail.LastIndexOf('.');
        if (dot <= 0 || dot == tail.Length - 1) return false;
        var ext = tail[(dot + 1)..].ToLowerInvariant();
        // index.html-style paths (with no extension after the dot) are unusual
        // for SPA routes — anything with a recognisable file extension is an asset.
        return ext.Length is >= 2 and <= 5 && ext.All(c => char.IsLetterOrDigit(c));
    }
}
