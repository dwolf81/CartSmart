namespace CartSmart.API.Services
{
    /// <summary>
    /// Downloads an image from an arbitrary URL, validates and converts to WebP,
    /// then uploads original + .webp variants to a Supabase storage bucket.
    /// Used by the live-product image importer and by the candidate pipeline
    /// (which writes to the "candidates" bucket pre-approval, then re-hosts to
    /// "products" on admin approval).
    /// </summary>
    public interface IProductImageService
    {
        /// <summary>
        /// Fetches <paramref name="imageUrl"/>, decodes/validates, and uploads to
        /// <paramref name="bucket"/> under <paramref name="basePath"/>.
        /// Returns the public URL of the WebP variant on success.
        /// </summary>
        Task<RehostImageResult> RehostAsync(
            string imageUrl,
            string bucket,
            string basePath,
            CancellationToken ct = default);
    }

    public sealed record RehostImageResult(
        bool Success,
        string? PublicUrl,
        string? OriginalPath,
        string? WebpPath,
        string? Error);
}
