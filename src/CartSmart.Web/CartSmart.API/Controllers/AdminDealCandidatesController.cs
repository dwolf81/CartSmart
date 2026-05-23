using CartSmart.API.Models;
using CartSmart.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CartSmart.API.Controllers;

/// <summary>
/// Admin review surface for crawler / AI-sourced deal_candidate rows that
/// already point to a known product_id (i.e. the discovery crawler matched
/// a listing to a live product). Extension-sourced deal_candidates that
/// belong to a product_candidate are handled by AdminProductCandidatesController.
/// </summary>
[ApiController]
[Route("api/admin/deal-candidates")]
public sealed class AdminDealCandidatesController : ControllerBase
{
    private readonly ISupabaseService _supabase;
    private readonly IAuthService _authService;
    private readonly IUserService _userService;
    private readonly IUrlSanitizer _urlSanitizer;
    private readonly IDealService _dealService;
    private readonly ILogger<AdminDealCandidatesController> _logger;

    public AdminDealCandidatesController(
        ISupabaseService supabase,
        IAuthService authService,
        IUserService userService,
        IUrlSanitizer urlSanitizer,
        IDealService dealService,
        ILogger<AdminDealCandidatesController> logger)
    {
        _supabase = supabase;
        _authService = authService;
        _userService = userService;
        _urlSanitizer = urlSanitizer;
        _dealService = dealService;
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
        [FromQuery] string? source = null,
        [FromQuery] int page = 0,
        [FromQuery] int limit = 20)
    {
        var (err, _) = await EnsureAdminAsync();
        if (err != null) return err;

        var l = Math.Clamp(limit, 1, 100);
        var offset = Math.Max(0, page) * l;
        var client = _supabase.GetServiceRoleClient();

        var query = client.From<DealCandidate>().Select("*");

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Filter("status", Supabase.Postgrest.Constants.Operator.Equals, status);
        if (!string.IsNullOrWhiteSpace(source))
            query = query.Filter("source", Supabase.Postgrest.Constants.Operator.Equals, source);

        // List view focuses on rows already tied to a product (crawler/ai flow);
        // extension-sourced candidates are reviewed via product-candidates.
        query = query.Not("product_id", Supabase.Postgrest.Constants.Operator.Is, "null");

        var resp = await query
            .Order("created_at", Supabase.Postgrest.Constants.Ordering.Descending)
            .Range(offset, offset + l - 1)
            .Get();

        var candidates = resp.Models ?? new List<DealCandidate>();

        // Batch-fetch product and store names for display
        var productIds = candidates
            .Where(c => c.ProductId.HasValue)
            .Select(c => c.ProductId!.Value.ToString())
            .Distinct().ToList();
        var storeIds = candidates
            .Select(c => c.StoreId.ToString())
            .Distinct().ToList();

        var productNames = new Dictionary<int, string>();
        if (productIds.Count > 0)
        {
            var prodResp = await client.From<Product>()
                .Select("id, name")
                .Filter("id", Supabase.Postgrest.Constants.Operator.In, productIds)
                .Get();
            foreach (var p in prodResp.Models ?? new List<Product>())
                if (!string.IsNullOrWhiteSpace(p.Name)) productNames[p.Id] = p.Name;
        }

        var storeNames = new Dictionary<int, string>();
        if (storeIds.Count > 0)
        {
            var storeResp = await client.From<Store>()
                .Select("id, name")
                .Filter("id", Supabase.Postgrest.Constants.Operator.In, storeIds)
                .Get();
            foreach (var s in storeResp.Models ?? new List<Store>())
                if (!string.IsNullOrWhiteSpace(s.Name)) storeNames[s.Id] = s.Name;
        }

        var result = candidates.Select(c => new
        {
            c.Id,
            c.CreatedAt,
            c.LastSeenAt,
            c.Source,
            c.StoreId,
            StoreName = storeNames.GetValueOrDefault(c.StoreId),
            c.ProductId,
            ProductName = c.ProductId.HasValue ? productNames.GetValueOrDefault(c.ProductId.Value) : null,
            c.DealUrlCanonical,
            c.ListingPrice,
            c.ListingCurrency,
            c.RawTitle,
            c.AiConfidence,
            c.Status,
            c.AdminNotes,
            c.PromotedDealId,
        });

        return Ok(result);
    }

    [HttpGet("{id:long}")]
    [Authorize]
    public async Task<IActionResult> Get(long id)
    {
        var (err, _) = await EnsureAdminAsync();
        if (err != null) return err;

        var client = _supabase.GetServiceRoleClient();
        var resp = await client.From<DealCandidate>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
            .Limit(1)
            .Get();
        var dc = resp.Models.FirstOrDefault();
        if (dc == null) return NotFound();
        return Ok(dc);
    }

    // ── Approve ───────────────────────────────────────────────────────────

    public sealed class ApproveDealCandidateRequest
    {
        // Admin can override the AI/fuzzy-matched product before approval.
        public int? ProductId { get; set; }
        public decimal? Price { get; set; }
        public int? ConditionCategoryId { get; set; }
        public string? AdminNotes { get; set; }
    }

    [HttpPost("{id:long}/approve")]
    [Authorize]
    public async Task<IActionResult> Approve(long id, [FromBody] ApproveDealCandidateRequest? body)
    {
        var (err, admin) = await EnsureAdminAsync();
        if (err != null) return err;
        if (admin == null) return Unauthorized();

        var client = _supabase.GetServiceRoleClient();
        var resp = await client.From<DealCandidate>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
            .Limit(1)
            .Get();
        var dc = resp.Models.FirstOrDefault();
        if (dc == null) return NotFound();
        if (dc.Status != "pending_review")
            return BadRequest(new { message = $"Candidate is {dc.Status}, not pending_review." });

        var productId = body?.ProductId ?? dc.ProductId;
        if (productId is null or <= 0)
            return BadRequest(new { message = "productId is required (admin must confirm the match)." });

        var price = body?.Price ?? dc.ListingPrice;
        if (price is null or <= 0)
            return BadRequest(new { message = "price is required." });

        var storeResp = await client.From<Store>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, dc.StoreId.ToString())
            .Limit(1)
            .Get();
        var store = storeResp.Models.FirstOrDefault();
        if (store == null) return BadRequest(new { message = "Store not found." });

        var liveUrl = _urlSanitizer.CleanForStore(dc.DealUrlCanonical, store, injectAffiliate: true)
                      ?? dc.DealUrlCanonical;

        var dealInsert = new Deal
        {
            DealStatusId = 2,
            UserId = admin.Id,
            StoreId = dc.StoreId,
            DealTypeId = 1,
            DiscountPercent = 0,
            Deleted = false,
            StoreWide = false
        };
        var dealResp = await client.From<Deal>().Insert(dealInsert);
        var insertedDeal = dealResp?.Models?.FirstOrDefault();
        if (insertedDeal == null)
            return StatusCode(500, new { message = "Failed to insert Deal." });

        var variantResp = await client.From<ProductVariant>()
            .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, productId.Value.ToString())
            .Filter("is_default", Supabase.Postgrest.Constants.Operator.Equals, "true")
            .Limit(1)
            .Get();
        var variant = variantResp.Models.FirstOrDefault();

        var dp = new DealProduct
        {
            DealId = insertedDeal.Id,
            ProductId = productId.Value,
            ProductVariantId = variant?.Id,
            Price = price.Value,
            Url = liveUrl,
            Primary = true,
            DealStatusId = 2,
            ConditionId = body?.ConditionCategoryId ?? dc.ConditionCategoryId,
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
        dc.AdminNotes = body?.AdminNotes ?? dc.AdminNotes;
        if (body?.ProductId.HasValue == true) dc.ProductId = body.ProductId;
        await client.From<DealCandidate>().Update(dc);

        _logger.LogInformation(
            "[DealCandidate] Approved id={Id} dealId={DealId} productId={ProductId} admin={AdminId}",
            dc.Id, insertedDeal.Id, productId, admin.Id);

        return Ok(new { status = "promoted", dealId = insertedDeal.Id, productId });
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
        var resp = await client.From<DealCandidate>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
            .Limit(1)
            .Get();
        var dc = resp.Models.FirstOrDefault();
        if (dc == null) return NotFound();

        dc.Status = "rejected";
        dc.AdminNotes = body?.AdminNotes ?? dc.AdminNotes;
        await client.From<DealCandidate>().Update(dc);
        return Ok(new { status = "rejected" });
    }
}
