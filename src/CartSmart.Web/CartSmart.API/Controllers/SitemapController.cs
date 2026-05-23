using CartSmart.API.Models;
using CartSmart.API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Text;
using System.Xml;

namespace CartSmart.API.Controllers;

/// <summary>
/// Serves /sitemap.xml — crawlable by Google and other search engines.
/// Includes static pages and all active product pages.
/// Response is cached for 6 hours to avoid hammering the DB on every bot visit.
/// </summary>
[Route("sitemap.xml")]
public class SitemapController : ControllerBase
{
    private readonly ISupabaseService _supabase;
    private readonly IConfiguration _config;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SitemapController> _logger;

    private const string CacheKey = "sitemap:xml";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);

    public SitemapController(
        ISupabaseService supabase,
        IConfiguration config,
        IMemoryCache cache,
        ILogger<SitemapController> logger)
    {
        _supabase = supabase;
        _config = config;
        _cache = cache;
        _logger = logger;
    }

    [HttpGet]
    [ResponseCache(Duration = 21600, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> GetSitemap()
    {
        if (_cache.TryGetValue(CacheKey, out string? cached) && cached != null)
            return Content(cached, "application/xml", Encoding.UTF8);

        var baseUrl = (_config["App:BaseUrl"] ?? "https://cartsmart.com").TrimEnd('/');

        try
        {
            var client = _supabase.GetServiceRoleClient();

            var productTask = client.From<Product>()
                .Select("slug")
                .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
                .Filter("enable_service", Supabase.Postgrest.Constants.Operator.Equals, "true")
                .Get();

            var storeTask = client.From<Store>()
                .Select("slug")
                .Filter("approved", Supabase.Postgrest.Constants.Operator.Equals, "true")
                .Get();

            var categoryTask = client.From<ProductType>()
                .Select("slug")
                .Get();

            await Task.WhenAll(productTask, storeTask, categoryTask);

            var products = ((await productTask).Models ?? new List<Product>())
                .Where(p => !string.IsNullOrWhiteSpace(p.Slug))
                .ToList();

            var stores = ((await storeTask).Models ?? new List<Store>())
                .Where(s => !string.IsNullOrWhiteSpace(s.Slug))
                .ToList();

            var categories = ((await categoryTask).Models ?? new List<ProductType>())
                .Where(c => !string.IsNullOrWhiteSpace(c.Slug))
                .ToList();

            var xml = BuildSitemapXml(baseUrl, products, stores, categories);

            _cache.Set(CacheKey, xml, CacheDuration);
            return Content(xml, "application/xml", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate sitemap");
            return StatusCode(500);
        }
    }

    private static string BuildSitemapXml(
        string baseUrl,
        IReadOnlyList<Product> products,
        IReadOnlyList<Store> stores,
        IReadOnlyList<ProductType> categories)
    {
        // Use MemoryStream so the XmlWriter can write a true UTF-8 declaration.
        // StringWriter always reports UTF-16, which causes an InvalidOperationException
        // when XmlWriterSettings.Encoding = UTF-8 and OmitXmlDeclaration = false.
        using var ms = new MemoryStream(512 + products.Count * 120);
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            OmitXmlDeclaration = false,
        };

        using (var writer = XmlWriter.Create(ms, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("urlset", "http://www.sitemaps.org/schemas/sitemap/0.9");

            // ── Static pages ──────────────────────────────────────────────────
            var staticPages = new[]
            {
                (path: "/",           changefreq: "daily",   priority: "1.0"),

                (path: "/stores",     changefreq: "weekly",  priority: "0.7"),
                (path: "/categories", changefreq: "weekly",  priority: "0.7"),
                (path: "/about",      changefreq: "monthly", priority: "0.4"),
                (path: "/contact",    changefreq: "monthly", priority: "0.3"),
            };

            foreach (var page in staticPages)
                WriteUrl(writer, $"{baseUrl}{page.path}", page.changefreq, page.priority, lastMod: null);

            // ── Store pages ───────────────────────────────────────────────────
            foreach (var s in stores)
            {
                var loc = $"{baseUrl}/stores/{Uri.EscapeDataString(s.Slug!)}";
                WriteUrl(writer, loc, "weekly", "0.7", lastMod: null);
            }

            // ── Category pages ────────────────────────────────────────────────
            foreach (var c in categories)
            {
                var loc = $"{baseUrl}/categories/{Uri.EscapeDataString(c.Slug!)}";
                WriteUrl(writer, loc, "daily", "0.6", lastMod: null);
            }

            // ── Product pages ─────────────────────────────────────────────────
            foreach (var p in products)
            {
                var loc = $"{baseUrl}/products/{Uri.EscapeDataString(p.Slug!)}";
                WriteUrl(writer, loc, "daily", "0.8", lastMod: null);
            }

            writer.WriteEndElement(); // urlset
            writer.WriteEndDocument();
        } // writer is flushed and disposed here

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteUrl(XmlWriter writer, string loc, string changefreq, string priority, string? lastMod)
    {
        writer.WriteStartElement("url");
        writer.WriteElementString("loc", loc);
        if (lastMod != null)
            writer.WriteElementString("lastmod", lastMod);
        writer.WriteElementString("changefreq", changefreq);
        writer.WriteElementString("priority", priority);
        writer.WriteEndElement();
    }
}
