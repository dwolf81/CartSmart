using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CartSmart.API.Models;
using CartSmart.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;

namespace CartSmart.API.Controllers;

/// <summary>
/// Admin review surface for ProductCandidate rows submitted by the Chrome
/// extension's "Add Product" button. Mirrors the AdminSocialPostsController
/// pattern (EnsureAdminAsync gate, list/detail/approve/reject endpoints).
/// </summary>
[ApiController]
[Route("api/admin/product-candidates")]
public sealed class AdminProductCandidatesController : ControllerBase
{
    private readonly ISupabaseService _supabase;
    private readonly IAuthService _authService;
    private readonly IUserService _userService;
    private readonly IUrlSanitizer _urlSanitizer;
    private readonly IProductImageService _productImageService;
    private readonly IDealService _dealService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AdminProductCandidatesController> _logger;

    public AdminProductCandidatesController(
        ISupabaseService supabase,
        IAuthService authService,
        IUserService userService,
        IUrlSanitizer urlSanitizer,
        IProductImageService productImageService,
        IDealService dealService,
        IHttpClientFactory httpClientFactory,
        ILogger<AdminProductCandidatesController> logger)
    {
        _supabase = supabase;
        _authService = authService;
        _userService = userService;
        _urlSanitizer = urlSanitizer;
        _productImageService = productImageService;
        _dealService = dealService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private async Task<(IActionResult? Error, User? Admin)> EnsureAdminAsync()
    {
        var idStr = _authService.GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(idStr) || !int.TryParse(idStr, out var id))
            return (Unauthorized(), null);
        var u = await _userService.GetUserByIdAsync(id);
        if (u == null) return (Unauthorized(), null);
        if (!u.Admin) return (Forbid(), null);
        return (null, u);
    }

    // ── List ──────────────────────────────────────────────────────────────

    [HttpGet]
    [Authorize]
    public async Task<IActionResult> List(
        [FromQuery] string? status = "pending_review",
        [FromQuery] int page = 0,
        [FromQuery] int limit = 20)
    {
        var (err, _) = await EnsureAdminAsync();
        if (err != null) return err;

        var l = Math.Clamp(limit, 1, 100);
        var offset = Math.Max(0, page) * l;

        var client = _supabase.GetServiceRoleClient();
        var query = client.From<ProductCandidate>().Select("*");
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Filter("status", Supabase.Postgrest.Constants.Operator.Equals, status);

        var resp = await query
            .Order("last_submitted_at", Supabase.Postgrest.Constants.Ordering.Descending)
            .Range(offset, offset + l - 1)
            .Get();

        var candidates = resp.Models ?? new List<ProductCandidate>();

        // Look up store names for the candidates' source_store_ids in one batch so
        // the admin grid can show a friendly name instead of a numeric id.
        var storeNamesById = new Dictionary<int, string?>();
        var storeIds = candidates.Select(c => c.SourceStoreId).Distinct().ToList();
        if (storeIds.Count > 0)
        {
            var storesResp = await client.From<Store>()
                .Select("id, name")
                .Filter("id", Supabase.Postgrest.Constants.Operator.In,
                    storeIds.Select(id => id.ToString()).ToList())
                .Get();
            foreach (var s in storesResp.Models ?? new List<Store>())
                storeNamesById[s.Id] = s.Name;
        }

        var items = candidates.Select(c => new
        {
            id = c.Id,
            createdAt = c.CreatedAt,
            lastSubmittedAt = c.LastSubmittedAt,
            source = c.Source,
            sourceStoreId = c.SourceStoreId,
            sourceStoreName = storeNamesById.TryGetValue(c.SourceStoreId, out var name) ? name : null,
            sourceUrlCanonical = c.SourceUrlCanonical,
            name = c.Name,
            nameNormalized = c.NameNormalized,
            brandText = c.BrandText,
            brandId = c.BrandId,
            productTypeId = c.ProductTypeId,
            msrp = c.MSRP,
            slugSuggested = c.SlugSuggested,
            imageUrlOriginal = c.ImageUrlOriginal,
            imageUrl = c.ImageUrl ?? c.ImageUrlOriginal,
            description = c.Description,
            status = c.Status,
            suggestedMergeProductId = c.SuggestedMergeProductId,
            mergedIntoProductId = c.MergedIntoProductId,
            adminNotes = c.AdminNotes,
            submittedByUserId = c.SubmittedByUserId,
            submissionCount = c.SubmissionCount,
            submittersJsonb = c.SubmittersJsonb,
        });

        return Ok(items);
    }

    // ── Detail ────────────────────────────────────────────────────────────

    [HttpGet("{id:long}")]
    [Authorize]
    public async Task<IActionResult> Get(long id)
    {
        var (err, _) = await EnsureAdminAsync();
        if (err != null) return err;

        var client = _supabase.GetServiceRoleClient();
        var resp = await client.From<ProductCandidate>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
            .Limit(1)
            .Get();
        var candidate = resp.Models.FirstOrDefault();
        if (candidate == null) return NotFound();

        var deals = await client.From<DealCandidate>()
            .Filter("product_candidate_id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
            .Order("created_at", Supabase.Postgrest.Constants.Ordering.Descending)
            .Get();

        return Ok(new
        {
            candidate,
            dealCandidates = deals.Models ?? new List<DealCandidate>()
        });
    }

    // ── Approve ───────────────────────────────────────────────────────────

    public sealed class ApproveProductCandidateRequest
    {
        public string? Name { get; set; }
        public string? Slug { get; set; }
        public int? BrandId { get; set; }
        public int? ProductTypeId { get; set; }
        public decimal? Msrp { get; set; }
        public string? Description { get; set; }
        public string? AdminNotes { get; set; }
        // 1 = New, 2 = Used, 3 = Refurbished. Admin choice in the modal wins;
        // the candidate's scraped value is unreliable (extension keyword scan
        // routinely false-positives on retailer pages with "used"/"pre-owned"
        // navigation links).
        public int? ConditionCategoryId { get; set; }
    }

    [HttpPost("{id:long}/approve")]
    [Authorize]
    public async Task<IActionResult> Approve(long id, [FromBody] ApproveProductCandidateRequest? body)
    {
        var (err, admin) = await EnsureAdminAsync();
        if (err != null) return err;
        if (admin == null) return Unauthorized();

        var client = _supabase.GetServiceRoleClient();
        var resp = await client.From<ProductCandidate>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
            .Limit(1)
            .Get();
        var candidate = resp.Models.FirstOrDefault();
        if (candidate == null) return NotFound();
        if (candidate.Status != "pending_review")
            return BadRequest(new { message = $"Candidate is {candidate.Status}, not pending_review." });

        var name = (body?.Name ?? candidate.Name)?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { message = "name is required" });

        var productTypeId = body?.ProductTypeId ?? candidate.ProductTypeId;
        if (productTypeId is null or <= 0)
            return BadRequest(new { message = "productTypeId is required for approval" });

        // ── Collision-safe slug ──────────────────────────────────────────
        var baseSlug = Slugify(body?.Slug ?? name);
        if (string.IsNullOrWhiteSpace(baseSlug))
            return BadRequest(new { message = "Could not derive a slug from the name." });

        var slug = baseSlug;
        for (var attempt = 0; attempt < 25; attempt++)
        {
            var existing = await client.From<Product>()
                .Filter("slug", Supabase.Postgrest.Constants.Operator.Equals, slug)
                .Limit(1)
                .Get();
            if (existing.Models.FirstOrDefault() == null) break;
            slug = $"{baseSlug}-{attempt + 2}";
        }

        // ── Insert Product ───────────────────────────────────────────────
        var insertRow = new ProductAdminInsertRow
        {
            Slug = slug,
            Name = name,
            MSRP = body?.Msrp.HasValue == true ? (float?)body.Msrp.Value : (candidate.MSRP.HasValue ? (float?)candidate.MSRP.Value : null),
            Description = body?.Description ?? candidate.Description,
            ProductTypeId = productTypeId.Value,
            UserId = admin.Id,
            BrandId = body?.BrandId ?? candidate.BrandId,
            EnableService = true,
            Deleted = false
        };
        var insertResp = await client.From<ProductAdminInsertRow>().Insert(insertRow);
        var insertedProduct = insertResp?.Models?.FirstOrDefault();
        if (insertedProduct == null)
            return StatusCode(500, new { message = "Failed to create product." });

        // ── Default placeholder variant (matches ProductsController admin-create flow) ──
        var now = DateTime.UtcNow;
        try
        {
            await client.From<ProductVariant>().Insert(new ProductVariant
            {
                ProductId = insertedProduct.Id,
                VariantName = null,
                UnitCount = null,
                UnitType = null,
                DisplayName = "Default",
                NormalizedTitle = "default",
                IsDefault = true,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ProductCandidate] Approve: failed to create default variant for product {ProductId}", insertedProduct.Id);
            try
            {
                insertedProduct.Deleted = true;
                await client.From<ProductAdminInsertRow>().Update(insertedProduct);
            }
            catch { }
            return StatusCode(500, new { message = "Failed to create default product variant." });
        }

        // ── Move the candidate image into the products bucket ────────────
        string? finalImageUrl = null;
        var sourceImage = candidate.ImageUrl ?? candidate.ImageUrlOriginal;
        if (!string.IsNullOrWhiteSpace(sourceImage))
        {
            var basePath = $"{insertedProduct.Id}/{Guid.NewGuid():N}";
            var rehost = await _productImageService.RehostAsync(sourceImage, "products", basePath);
            if (rehost.Success && !string.IsNullOrWhiteSpace(rehost.PublicUrl))
            {
                finalImageUrl = rehost.PublicUrl;
                await client.From<ProductAdminImageUpdateRow>().Update(new ProductAdminImageUpdateRow
                {
                    Id = insertedProduct.Id,
                    Slug = slug,
                    ImageUrl = finalImageUrl
                });
            }
            else
            {
                _logger.LogInformation("[ProductCandidate] Approve: image rehost skipped: {Error}", rehost.Error);
            }
        }

        // ── Promote linked deal_candidate (if any) ──────────────────────
        var pendingDcResp = await client.From<DealCandidate>()
            .Filter("product_candidate_id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
            .Filter("status", Supabase.Postgrest.Constants.Operator.Equals, "pending_review")
            .Order("created_at", Supabase.Postgrest.Constants.Ordering.Descending)
            .Limit(1)
            .Get();
        var dealCandidate = pendingDcResp.Models.FirstOrDefault();
        int? promotedDealId = null;
        if (dealCandidate != null)
        {
            promotedDealId = await PromoteDealCandidateAsync(
                client, dealCandidate, insertedProduct.Id, admin.Id,
                conditionOverride: body?.ConditionCategoryId);
        }

        // ── Update candidate row to approved ─────────────────────────────
        candidate.Status = "approved";
        candidate.MergedIntoProductId = insertedProduct.Id;
        candidate.AdminNotes = body?.AdminNotes ?? candidate.AdminNotes;
        await client.From<ProductCandidate>().Update(candidate);

        _logger.LogInformation(
            "[ProductCandidate] Approved id={Id} → productId={ProductId} dealId={DealId} admin={AdminId}",
            candidate.Id, insertedProduct.Id, promotedDealId, admin.Id);

        return Ok(new
        {
            status = "approved",
            productId = insertedProduct.Id,
            slug,
            imageUrl = finalImageUrl,
            promotedDealId
        });
    }

    // ── Reject ────────────────────────────────────────────────────────────

    public sealed class RejectRequest { public string? AdminNotes { get; set; } }

    [HttpPost("{id:long}/reject")]
    [Authorize]
    public async Task<IActionResult> Reject(long id, [FromBody] RejectRequest? body)
    {
        var (err, _) = await EnsureAdminAsync();
        if (err != null) return err;

        var client = _supabase.GetServiceRoleClient();
        var resp = await client.From<ProductCandidate>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
            .Limit(1)
            .Get();
        var candidate = resp.Models.FirstOrDefault();
        if (candidate == null) return NotFound();

        candidate.Status = "rejected";
        candidate.AdminNotes = body?.AdminNotes ?? candidate.AdminNotes;
        await client.From<ProductCandidate>().Update(candidate);

        // Reject the linked deal_candidate(s) too so they don't linger.
        var deals = await client.From<DealCandidate>()
            .Filter("product_candidate_id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
            .Filter("status", Supabase.Postgrest.Constants.Operator.Equals, "pending_review")
            .Get();
        foreach (var dc in deals.Models ?? new List<DealCandidate>())
        {
            dc.Status = "rejected";
            await client.From<DealCandidate>().Update(dc);
        }

        return Ok(new { status = "rejected" });
    }

    // ── Product picker (search) for the merge flow ────────────────────────

    [HttpGet("search-products")]
    [Authorize]
    public async Task<IActionResult> SearchProducts([FromQuery] string q, [FromQuery] int? productTypeId = null, [FromQuery] int limit = 20)
    {
        var (err, _) = await EnsureAdminAsync();
        if (err != null) return err;

        var trimmed = (q ?? string.Empty).Trim();
        if (trimmed.Length < 2) return Ok(Array.Empty<object>());

        var client = _supabase.GetServiceRoleClient();
        var query = client.From<Product>()
            .Select("id, name, slug, brand_id, product_type_id, image_url, deleted")
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Filter("name", Supabase.Postgrest.Constants.Operator.ILike, $"%{trimmed}%")
            .Order("name", Supabase.Postgrest.Constants.Ordering.Ascending)
            .Limit(Math.Clamp(limit, 1, 50));
        if (productTypeId is > 0)
            query = query.Filter("product_type_id", Supabase.Postgrest.Constants.Operator.Equals, productTypeId.Value.ToString());

        var resp = await query.Get();
        var products = resp.Models ?? new List<Product>();

        // Lookup brand names so the picker can show "Brand — Product"
        var brandIds = products.Where(p => p.BrandId.HasValue).Select(p => p.BrandId!.Value).Distinct().ToList();
        var brandsById = new Dictionary<int, string?>();
        if (brandIds.Count > 0)
        {
            var bResp = await client.From<Brand>()
                .Select("id, name")
                .Filter("id", Supabase.Postgrest.Constants.Operator.In, brandIds.Select(i => i.ToString()).ToList())
                .Get();
            foreach (var b in bResp.Models ?? new List<Brand>())
                brandsById[b.Id] = b.Name;
        }

        var items = products.Select(p => new
        {
            id = p.Id,
            name = p.Name,
            slug = p.Slug,
            brandId = p.BrandId,
            brandName = p.BrandId.HasValue && brandsById.TryGetValue(p.BrandId.Value, out var n) ? n : null,
            productTypeId = p.ProductTypeId,
            imageUrl = p.ImageUrl,
        });
        return Ok(items);
    }

    // ── Merge into existing product ───────────────────────────────────────

    public sealed class MergeRequest
    {
        public int ProductId { get; set; }
        public string? AdminNotes { get; set; }
        // 1 = New, 2 = Used, 3 = Refurbished. Defaults to New when unset.
        public int? ConditionCategoryId { get; set; }
    }

    [HttpPost("{id:long}/merge-into")]
    [Authorize]
    public async Task<IActionResult> MergeInto(long id, [FromBody] MergeRequest body)
    {
        var (err, admin) = await EnsureAdminAsync();
        if (err != null) return err;
        if (admin == null) return Unauthorized();
        if (body == null || body.ProductId <= 0)
            return BadRequest(new { message = "productId is required" });

        var client = _supabase.GetServiceRoleClient();
        var resp = await client.From<ProductCandidate>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
            .Limit(1)
            .Get();
        var candidate = resp.Models.FirstOrDefault();
        if (candidate == null) return NotFound();
        if (candidate.Status != "pending_review")
            return BadRequest(new { message = $"Candidate is {candidate.Status}, not pending_review." });

        // Confirm target product exists and isn't deleted
        var targetResp = await client.From<Product>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, body.ProductId.ToString())
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Limit(1)
            .Get();
        var target = targetResp.Models.FirstOrDefault();
        if (target == null) return NotFound(new { message = "Target product not found." });

        // Promote the linked deal_candidate(s) as new deals on the target product
        var deals = await client.From<DealCandidate>()
            .Filter("product_candidate_id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
            .Filter("status", Supabase.Postgrest.Constants.Operator.Equals, "pending_review")
            .Get();

        int? lastDealId = null;
        foreach (var dc in deals.Models ?? new List<DealCandidate>())
        {
            lastDealId = await PromoteDealCandidateAsync(
                client, dc, body.ProductId, admin.Id,
                conditionOverride: body.ConditionCategoryId);
        }

        candidate.Status = "merged";
        candidate.MergedIntoProductId = body.ProductId;
        candidate.AdminNotes = body.AdminNotes ?? candidate.AdminNotes;
        await client.From<ProductCandidate>().Update(candidate);

        _logger.LogInformation(
            "[ProductCandidate] Merged id={Id} → productId={ProductId} dealId={DealId} admin={AdminId}",
            candidate.Id, body.ProductId, lastDealId, admin.Id);

        return Ok(new { status = "merged", productId = body.ProductId, promotedDealId = lastDealId });
    }

    // ── Image upload / URL import ─────────────────────────────────────────
    //
    // Both write to the "candidates" bucket. The Approve endpoint already
    // rehosts the candidate's image into the "products" bucket on promotion,
    // so admins can replace the scraped image while the candidate is still
    // pending and the final live product picks up the updated image.

    [HttpPost("{id:long}/image")]
    [Authorize]
    public async Task<IActionResult> UploadImage(long id, IFormFile file)
    {
        var (err, _) = await EnsureAdminAsync();
        if (err != null) return err;
        if (file == null || file.Length == 0)
            return BadRequest(new { message = "No file uploaded" });

        var client = _supabase.GetServiceRoleClient();
        var resp = await client.From<ProductCandidate>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
            .Limit(1)
            .Get();
        var candidate = resp.Models.FirstOrDefault();
        if (candidate == null) return NotFound();

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".bin";
        // Same bucket as live product images — admins manage one storage area.
        // Path is namespaced under candidates/<id>/ so unapproved uploads can't
        // collide with real product folders (which are bare {productId}/...).
        var basePath = $"candidates/{candidate.Id}/{Guid.NewGuid():N}";
        var originalPath = $"{basePath}{ext}";
        var webpPath = $"{basePath}.webp";

        byte[] fileBytes;
        using (var input = file.OpenReadStream())
        using (var ms = new MemoryStream())
        {
            await input.CopyToAsync(ms);
            fileBytes = ms.ToArray();
        }

        try { using var _ = Image.Load(fileBytes); }
        catch { return BadRequest(new { message = "File is not a supported image." }); }

        await _supabase.UploadFileWithServiceRoleAsync(
            "products",
            originalPath,
            new MemoryStream(fileBytes),
            new Supabase.Storage.FileOptions
            {
                CacheControl = "3600",
                Upsert = true,
                ContentType = GetContentType(ext)
            });

        var webpBytes = await ConvertImageToWebPAsync(fileBytes);
        await _supabase.UploadFileWithServiceRoleAsync(
            "products",
            webpPath,
            new MemoryStream(webpBytes),
            new Supabase.Storage.FileOptions
            {
                CacheControl = "3600",
                Upsert = true,
                ContentType = "image/webp"
            });

        var publicUrl = _supabase.GetPublicUrl("products", webpPath);
        candidate.ImageUrl = publicUrl;
        await client.From<ProductCandidate>().Update(candidate);

        return Ok(new { imageUrl = publicUrl });
    }

    public sealed class ImportImageFromUrlRequest
    {
        [JsonPropertyName("imageUrl")]
        public string? ImageUrl { get; set; }
    }

    [HttpPost("{id:long}/image-from-url")]
    [Authorize]
    public async Task<IActionResult> ImportImageFromUrl(long id, [FromBody] ImportImageFromUrlRequest body)
    {
        var (err, _) = await EnsureAdminAsync();
        if (err != null) return err;
        if (string.IsNullOrWhiteSpace(body?.ImageUrl))
            return BadRequest(new { message = "imageUrl is required" });

        var client = _supabase.GetServiceRoleClient();
        var resp = await client.From<ProductCandidate>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
            .Limit(1)
            .Get();
        var candidate = resp.Models.FirstOrDefault();
        if (candidate == null) return NotFound();

        var basePath = $"candidates/{candidate.Id}/{Guid.NewGuid():N}";
        var rehost = await _productImageService.RehostAsync(body.ImageUrl!, "products", basePath);
        if (!rehost.Success || string.IsNullOrWhiteSpace(rehost.PublicUrl))
            return BadRequest(new { message = rehost.Error ?? "Image rehost failed." });

        candidate.ImageUrl = rehost.PublicUrl;
        await client.From<ProductCandidate>().Update(candidate);

        return Ok(new { imageUrl = rehost.PublicUrl });
    }

    // ── AI SEO description generation ─────────────────────────────────────
    //
    // Returns a freshly-written SEO-optimized description for the candidate,
    // also persists it on the candidate row so the admin can review/edit
    // before approval. Best-effort: returns 503 when OPENAI_API_KEY isn't set.

    public sealed class GenerateDescriptionRequest
    {
        public string? ExtraHint { get; set; }
    }

    [HttpPost("{id:long}/generate-description")]
    [Authorize]
    public async Task<IActionResult> GenerateDescription(long id, [FromBody] GenerateDescriptionRequest? body, CancellationToken ct)
    {
        var (err, _) = await EnsureAdminAsync();
        if (err != null) return err;

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
            return StatusCode(503, new { message = "OPENAI_API_KEY not configured" });
        var model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini";

        var client = _supabase.GetServiceRoleClient();
        var resp = await client.From<ProductCandidate>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
            .Limit(1)
            .Get();
        var candidate = resp.Models.FirstOrDefault();
        if (candidate == null) return NotFound();

        // Pull brand + product type names for richer prompt context
        string? brandName = null;
        if (candidate.BrandId.HasValue)
        {
            var bResp = await client.From<Brand>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, candidate.BrandId.Value.ToString())
                .Limit(1).Get();
            brandName = bResp.Models.FirstOrDefault()?.Name;
        }
        string? productTypeName = null;
        if (candidate.ProductTypeId.HasValue)
        {
            var ptResp = await client.From<ProductType>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, candidate.ProductTypeId.Value.ToString())
                .Limit(1).Get();
            productTypeName = ptResp.Models.FirstOrDefault()?.Name;
        }

        var systemPrompt = """
            You write unique, SEO-optimized product descriptions for a golf-products
            catalog. The output is shown on the product detail page and ranked by
            search engines. Constraints:
              - 2–3 short paragraphs, 80–160 words total.
              - Plain text only (no markdown, no headings, no lists, no emoji).
              - Lead with the product name and key benefit; mention brand and
                product category naturally; weave in 2–4 likely search terms.
              - Do not invent specs that aren't supplied. Do not quote a price.
              - Do not copy the scraped description verbatim — rewrite it.
              - End with a brief use-case or audience line.
            Respond with a JSON object ONLY: { "description": "<text>" }
            """;

        var userParts = new List<string>
        {
            $"Product name: {candidate.Name}",
        };
        if (!string.IsNullOrWhiteSpace(brandName)) userParts.Add($"Brand: {brandName}");
        if (!string.IsNullOrWhiteSpace(productTypeName)) userParts.Add($"Category: {productTypeName}");
        if (candidate.MSRP is > 0) userParts.Add($"MSRP (for context only, do not quote): ${candidate.MSRP:F2}");
        if (!string.IsNullOrWhiteSpace(candidate.Description))
            userParts.Add($"Scraped description (rewrite, do not copy):\n{candidate.Description}");
        if (!string.IsNullOrWhiteSpace(body?.ExtraHint))
            userParts.Add($"Admin hint:\n{body!.ExtraHint}");

        var requestBody = new
        {
            model,
            max_completion_tokens = 600,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = string.Join("\n\n", userParts) }
            }
        };

        string? generated;
        try
        {
            using var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(45);
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            req.Content = JsonContent.Create(requestBody, options: new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });

            using var openAiResp = await http.SendAsync(req, ct);
            if (!openAiResp.IsSuccessStatusCode)
            {
                var errBody = await openAiResp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("[ProductCandidate] OpenAI description error {Status}: {Body}", openAiResp.StatusCode, errBody);
                return StatusCode(502, new { message = "OpenAI request failed." });
            }
            var rawJson = await openAiResp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(rawJson);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            if (string.IsNullOrWhiteSpace(content))
                return StatusCode(502, new { message = "OpenAI returned no content." });
            using var inner = JsonDocument.Parse(content);
            generated = inner.RootElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ProductCandidate] generate-description failed for candidate {Id}", id);
            return StatusCode(500, new { message = "Generation failed." });
        }

        if (string.IsNullOrWhiteSpace(generated))
            return StatusCode(502, new { message = "OpenAI returned no description." });

        candidate.Description = generated;
        await client.From<ProductCandidate>().Update(candidate);

        return Ok(new { description = generated });
    }

    private static string GetContentType(string ext) =>
        (ext ?? string.Empty).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };

    private static async Task<byte[]> ConvertImageToWebPAsync(byte[] imageBytes)
    {
        using var image = Image.Load(imageBytes);
        using var output = new MemoryStream();
        await image.SaveAsWebpAsync(output, new WebpEncoder { Quality = 95 });
        return output.ToArray();
    }

    // ── Shared: promote a deal_candidate into a real Deal + DealProduct ──

    private async Task<int?> PromoteDealCandidateAsync(
        Supabase.Client client,
        DealCandidate dc,
        int productId,
        int adminUserId,
        int? conditionOverride = null)
    {
        var storeResp = await client.From<Store>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, dc.StoreId.ToString())
            .Limit(1)
            .Get();
        var store = storeResp.Models.FirstOrDefault();
        if (store == null)
        {
            _logger.LogWarning("[ProductCandidate] PromoteDeal: store {StoreId} not found", dc.StoreId);
            return null;
        }

        // Re-canonicalize with affiliate injection now that we're going live
        var liveUrl = _urlSanitizer.CleanForStore(dc.DealUrlCanonical, store, injectAffiliate: true)
                      ?? dc.DealUrlCanonical;

        var dealInsert = new Deal
        {
            DealStatusId = 2,             // Active
            UserId = adminUserId,
            StoreId = dc.StoreId,
            DealTypeId = 1,                // Direct
            DiscountPercent = 0,
            Deleted = false,
            StoreWide = false
        };
        var dealResp = await client.From<Deal>().Insert(dealInsert);
        var insertedDeal = dealResp?.Models?.FirstOrDefault();
        if (insertedDeal == null)
        {
            _logger.LogError("[ProductCandidate] PromoteDeal: failed to insert Deal for candidate {Id}", dc.Id);
            return null;
        }

        // Default variant for the product (created on approval or pre-existing)
        var variantResp = await client.From<ProductVariant>()
            .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, productId.ToString())
            .Filter("is_default", Supabase.Postgrest.Constants.Operator.Equals, "true")
            .Limit(1)
            .Get();
        var variant = variantResp.Models.FirstOrDefault();

        // Condition: admin's modal pick wins; otherwise default to New (1).
        // We don't fall back to dc.ConditionCategoryId because the extension's
        // keyword scan false-positives on "used" / "pre-owned" navigation links
        // and would silently mark new-product deals as used.
        var dp = new DealProduct
        {
            DealId = insertedDeal.Id,
            ProductId = productId,
            ProductVariantId = variant?.Id,
            Price = dc.ListingPrice ?? 0m,
            Url = liveUrl,
            Primary = true,
            DealStatusId = 2,
            ConditionId = conditionOverride ?? 1,
            ItemCount = 1,
            FreeShipping = false,
            LastCheckedAt = DateTime.UtcNow,
            ErrorCount = 0,
            Deleted = false
        };
        await client.From<DealProduct>().Insert(dp);

        // Mirror the normal CreateDeal flow: backfill derived deal_product rows
        // for any existing store-wide coupon/external + stacked deals at this
        // store so the promoted candidate produces a full deal stack instead
        // of just the direct row.
        await _dealService.ApplyDerivedDealProductsForDirectDealAsync(insertedDeal.Id);

        dc.Status = "promoted";
        dc.PromotedDealId = insertedDeal.Id;
        await client.From<DealCandidate>().Update(dc);

        return insertedDeal.Id;
    }

    // ── Slug helper (mirror ProductsController.Slugify) ────────────────────

    private static string Slugify(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var n = input.Trim().ToLowerInvariant();
        n = Regex.Replace(n, @"[^a-z0-9\s-]", "");
        n = Regex.Replace(n, @"\s+", " ").Trim();
        n = n.Replace(' ', '-');
        n = Regex.Replace(n, @"-+", "-").Trim('-');
        return n;
    }
}
