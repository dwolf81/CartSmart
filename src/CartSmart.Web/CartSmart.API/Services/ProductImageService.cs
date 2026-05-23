using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;

namespace CartSmart.API.Services
{
    public class ProductImageService : IProductImageService
    {
        private const int MaxBytes = 10_000_000;

        private readonly ISupabaseService _supabase;
        private readonly ILogger<ProductImageService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public ProductImageService(
            ISupabaseService supabase,
            ILogger<ProductImageService> logger,
            IHttpClientFactory httpClientFactory)
        {
            _supabase = supabase;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        public async Task<RehostImageResult> RehostAsync(
            string imageUrl,
            string bucket,
            string basePath,
            CancellationToken ct = default)
        {
            var uri = TryCreateHttpUri(imageUrl);
            if (uri == null)
                return new RehostImageResult(false, null, null, null, "imageUrl is required and must be http(s).");

            byte[] fileBytes;
            string? contentType;

            try
            {
                using var http = _httpClientFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(15);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; CartSmart/1.0)");
                using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                    return new RehostImageResult(false, null, null, null, $"Failed to fetch image (HTTP {(int)resp.StatusCode}).");

                contentType = resp.Content.Headers.ContentType?.MediaType;
                var length = resp.Content.Headers.ContentLength;
                if (length.HasValue && length.Value > MaxBytes)
                    return new RehostImageResult(false, null, null, null, "Image too large (max 10MB).");

                fileBytes = await resp.Content.ReadAsByteArrayAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Image rehost fetch failed for {Url}", uri);
                return new RehostImageResult(false, null, null, null, $"Image fetch failed: {ex.Message}");
            }

            if (fileBytes == null || fileBytes.Length == 0)
                return new RehostImageResult(false, null, null, null, "Empty image response.");
            if (fileBytes.Length > MaxBytes)
                return new RehostImageResult(false, null, null, null, "Image too large (max 10MB).");

            try
            {
                using var _ = Image.Load(fileBytes);
            }
            catch
            {
                return new RehostImageResult(false, null, null, null, "URL did not return a supported image.");
            }

            var (ext, originalContentType) = GuessImageType(contentType, uri.ToString());
            var originalPath = $"{basePath}{ext}";
            var webpPath = $"{basePath}.webp";

            try
            {
                using (var originalStream = new MemoryStream(fileBytes))
                {
                    await _supabase.UploadFileWithServiceRoleAsync(
                        bucket,
                        originalPath,
                        originalStream,
                        new Supabase.Storage.FileOptions
                        {
                            CacheControl = "3600",
                            Upsert = true,
                            ContentType = originalContentType
                        });
                }

                var webpBytes = await ConvertImageToWebP(fileBytes);
                using (var webpStream = new MemoryStream(webpBytes))
                {
                    await _supabase.UploadFileWithServiceRoleAsync(
                        bucket,
                        webpPath,
                        webpStream,
                        new Supabase.Storage.FileOptions
                        {
                            CacheControl = "3600",
                            Upsert = true,
                            ContentType = "image/webp"
                        });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Image rehost upload failed for bucket={Bucket} path={Path}", bucket, basePath);
                return new RehostImageResult(false, null, null, null, $"Upload failed: {ex.Message}");
            }

            var publicUrl = _supabase.GetPublicUrl(bucket, webpPath);
            return new RehostImageResult(true, publicUrl, originalPath, webpPath, null);
        }

        private static Uri? TryCreateHttpUri(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var abs)
                && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
                return abs;
            var candidate = $"https://{url.Trim()}";
            if (Uri.TryCreate(candidate, UriKind.Absolute, out abs)
                && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
                return abs;
            return null;
        }

        private static (string Ext, string ContentType) GuessImageType(string? contentType, string? url)
        {
            var ct = (contentType ?? string.Empty).ToLowerInvariant();
            if (ct.StartsWith("image/jpeg")) return (".jpg", "image/jpeg");
            if (ct.StartsWith("image/png")) return (".png", "image/png");
            if (ct.StartsWith("image/gif")) return (".gif", "image/gif");
            if (ct.StartsWith("image/webp")) return (".webp", "image/webp");

            try
            {
                if (!string.IsNullOrWhiteSpace(url))
                {
                    var u = TryCreateHttpUri(url);
                    var ext = Path.GetExtension(u?.AbsolutePath ?? url);
                    if (!string.IsNullOrWhiteSpace(ext))
                    {
                        ext = ext.ToLowerInvariant();
                        if (ext is ".jpg" or ".jpeg") return (".jpg", "image/jpeg");
                        if (ext == ".png") return (".png", "image/png");
                        if (ext == ".gif") return (".gif", "image/gif");
                        if (ext == ".webp") return (".webp", "image/webp");
                    }
                }
            }
            catch { }

            return (".bin", "application/octet-stream");
        }

        private static async Task<byte[]> ConvertImageToWebP(byte[] imageBytes)
        {
            using var image = SixLabors.ImageSharp.Image.Load(imageBytes);
            using var output = new MemoryStream();
            await image.SaveAsWebpAsync(output, new WebpEncoder { Quality = 95 });
            return output.ToArray();
        }
    }
}
