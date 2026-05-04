using CartSmart.API.Services;
using CartSmart.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Text.Json.Serialization;

namespace CartSmart.API.Controllers;

[ApiController]
[Route("api/admin/social-posts")]
public sealed class AdminSocialPostsController : ControllerBase
{
    private readonly ISocialPostService _socialPostService;
    private readonly ISupabaseService _supabase;
    private readonly IAuthService _authService;
    private readonly IUserService _userService;
    private readonly ILogger<AdminSocialPostsController> _logger;

    public AdminSocialPostsController(
        ISocialPostService socialPostService,
        ISupabaseService supabase,
        IAuthService authService,
        IUserService userService,
        ILogger<AdminSocialPostsController> logger)
    {
        _socialPostService = socialPostService;
        _supabase = supabase;
        _authService = authService;
        _userService = userService;
        _logger = logger;
    }

    private async Task<IActionResult?> EnsureAdminAsync()
    {
        var userIdStr = _authService.GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(userIdStr) || !int.TryParse(userIdStr, out var userId))
            return Unauthorized();
        var user = await _userService.GetUserByIdAsync(userId);
        if (user == null) return Unauthorized();
        if (!user.Admin) return Forbid();
        return null;
    }

    // ── List ──────────────────────────────────────────────────────────────

    /// <summary>GET /api/admin/social-posts?status=pending_approval&amp;page=0&amp;limit=20</summary>
    [HttpGet]
    [Authorize]
    public async Task<IActionResult> List(
        [FromQuery] string? status = null,
        [FromQuery] int page = 0,
        [FromQuery] int limit = 20,
        CancellationToken ct = default)
    {
        var auth = await EnsureAdminAsync();
        if (auth != null) return auth;

        var posts = await _socialPostService.GetPostsAsync(status, page, limit, ct);
        return Ok(posts);
    }

    // ── Get single ────────────────────────────────────────────────────────

    [HttpGet("{id:long}")]
    [Authorize]
    public async Task<IActionResult> Get(long id, CancellationToken ct = default)
    {
        var auth = await EnsureAdminAsync();
        if (auth != null) return auth;

        var post = await _socialPostService.GetPostAsync(id, ct);
        if (post == null) return NotFound();
        return Ok(post);
    }

    [HttpGet("options/products")]
    [Authorize]
    public async Task<IActionResult> ProductOptions(
        [FromQuery] string? query = null,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var auth = await EnsureAdminAsync();
        if (auth != null) return auth;

        try
        {
            var l = Math.Clamp(limit, 1, 80);
            var client = _supabase.GetServiceRoleClient();
            var q = (query ?? string.Empty).Trim();

            var numeric = int.TryParse(q, out var idFilter) ? idFilter : 0;
            var rows = new List<Product>();

            if (numeric > 0)
            {
                var idResp = await client.From<Product>()
                    .Select("id, name, slug, deleted")
                    .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
                    .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, numeric)
                    .Limit(1)
                    .Get();
                rows.AddRange((idResp.Models ?? []).Where(m => m != null));
            }

            if (!string.IsNullOrWhiteSpace(q))
            {
                var like = $"%{q}%";
                var byName = await client.From<Product>()
                    .Select("id, name, slug, deleted")
                    .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
                    .Filter("name", Supabase.Postgrest.Constants.Operator.ILike, like)
                    .Order("created_at", Supabase.Postgrest.Constants.Ordering.Descending)
                    .Limit(l)
                    .Get();

                var bySlug = await client.From<Product>()
                    .Select("id, name, slug, deleted")
                    .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
                    .Filter("slug", Supabase.Postgrest.Constants.Operator.ILike, like)
                    .Order("created_at", Supabase.Postgrest.Constants.Ordering.Descending)
                    .Limit(l)
                    .Get();

                rows.AddRange((byName.Models ?? []).Where(m => m != null));
                rows.AddRange((bySlug.Models ?? []).Where(m => m != null));
            }
            else
            {
                var recent = await client.From<Product>()
                    .Select("id, name, slug, deleted")
                    .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
                    .Order("created_at", Supabase.Postgrest.Constants.Ordering.Descending)
                    .Limit(l)
                    .Get();
                rows.AddRange((recent.Models ?? []).Where(m => m != null));
            }

            var result = rows
                .Where(x => x != null)
                .GroupBy(x => x.Id)
                .Select(g => g.First())
                .Take(l)
                .Select(p => new { id = p.Id, name = p.Name ?? string.Empty, slug = p.Slug ?? string.Empty })
                .ToList();

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load product options for social posts. query={Query} limit={Limit}", query, limit);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to load product options" });
        }
    }

    [HttpGet("options/deals")]
    [Authorize]
    public async Task<IActionResult> DealOptions(
        [FromQuery] string? query = null,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var auth = await EnsureAdminAsync();
        if (auth != null) return auth;

        try
        {
            var l = Math.Clamp(limit, 1, 80);
            var client = _supabase.GetServiceRoleClient();

            var q = (query ?? string.Empty).Trim();
            var numeric = int.TryParse(q, out var numericFilter) ? numericFilter : 0;
            var dealProducts = new List<DealProduct>();

            if (numeric > 0)
            {
                var byDealId = await client.From<DealProduct>()
                    .Select("id, deal_id, product_id, price, deleted, deal_status_id")
                    .Filter("deal_status_id", Supabase.Postgrest.Constants.Operator.Equals, "2")
                    .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
                    .Filter("price", Supabase.Postgrest.Constants.Operator.GreaterThan, "0")
                    .Filter("deal_id", Supabase.Postgrest.Constants.Operator.Equals, numeric)
                    .Limit(200)
                    .Get();
                dealProducts.AddRange((byDealId.Models ?? []).Where(m => m != null));

                var byProductId = await client.From<DealProduct>()
                    .Select("id, deal_id, product_id, price, deleted, deal_status_id")
                    .Filter("deal_status_id", Supabase.Postgrest.Constants.Operator.Equals, "2")
                    .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
                    .Filter("price", Supabase.Postgrest.Constants.Operator.GreaterThan, "0")
                    .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, numeric)
                    .Limit(200)
                    .Get();
                dealProducts.AddRange((byProductId.Models ?? []).Where(m => m != null));
            }
            else if (!string.IsNullOrWhiteSpace(q))
            {
                var like = $"%{q}%";
                var productsByName = await client.From<Product>()
                    .Select("id, name, slug")
                    .Filter("name", Supabase.Postgrest.Constants.Operator.ILike, like)
                    .Limit(120)
                    .Get();

                var productsBySlug = await client.From<Product>()
                    .Select("id, name, slug")
                    .Filter("slug", Supabase.Postgrest.Constants.Operator.ILike, like)
                    .Limit(120)
                    .Get();

                var ids = (productsByName.Models ?? [])
                    .Where(p => p != null)
                    .Concat((productsBySlug.Models ?? []).Where(p => p != null))
                    .Select(p => (object)p.Id)
                    .Distinct()
                    .Take(120)
                    .ToArray();

                if (ids.Length > 0)
                {
                    var byProducts = await client.From<DealProduct>()
                        .Select("id, deal_id, product_id, price, deleted, deal_status_id")
                        .Filter("deal_status_id", Supabase.Postgrest.Constants.Operator.Equals, "2")
                        .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
                        .Filter("price", Supabase.Postgrest.Constants.Operator.GreaterThan, "0")
                        .Filter("product_id", Supabase.Postgrest.Constants.Operator.In, ids)
                        .Limit(400)
                        .Get();
                    dealProducts.AddRange((byProducts.Models ?? []).Where(m => m != null));
                }
            }
            else
            {
                var recent = await client.From<DealProduct>()
                    .Select("id, deal_id, product_id, price, deleted, deal_status_id")
                    .Filter("deal_status_id", Supabase.Postgrest.Constants.Operator.Equals, "2")
                    .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
                    .Filter("price", Supabase.Postgrest.Constants.Operator.GreaterThan, "0")
                    .Order("created_at", Supabase.Postgrest.Constants.Ordering.Descending)
                    .Limit(200)
                    .Get();
                dealProducts.AddRange((recent.Models ?? []).Where(m => m != null));
            }

            dealProducts = dealProducts
                .Where(x => x != null)
                .GroupBy(x => x.Id)
                .Select(g => g.First())
                .ToList();

            if (dealProducts.Count == 0) return Ok(Array.Empty<object>());

            var dealIds = dealProducts.Select(x => (object)x.DealId).Distinct().ToArray();
            var dealsResp = await client.From<Deal>()
                .Select("id, discount_percent, deleted")
                .Filter("id", Supabase.Postgrest.Constants.Operator.In, dealIds)
                .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
                .Get();
            var dealById = (dealsResp.Models ?? [])
                .Where(d => d != null)
                .GroupBy(d => d.Id)
                .Select(g => g.First())
                .ToDictionary(d => d.Id);

            var productIds = dealProducts.Select(x => (object)x.ProductId).Distinct().ToArray();
            var productsResp = await client.From<Product>()
                .Select("id, name, slug")
                .Filter("id", Supabase.Postgrest.Constants.Operator.In, productIds)
                .Get();
            var productById = (productsResp.Models ?? [])
                .Where(p => p != null)
                .GroupBy(p => p.Id)
                .Select(g => g.First())
                .ToDictionary(p => p.Id);

            var ql = q.ToLowerInvariant();

            // One option row per deal; choose the cheapest active deal_product row as representative.
            var rows = dealProducts
                .Where(dp => dealById.ContainsKey(dp.DealId) && productById.ContainsKey(dp.ProductId))
                .GroupBy(dp => dp.DealId)
                .Select(g => g.OrderBy(x => x.Price).First())
                .Select(dp =>
                {
                    var deal = dealById[dp.DealId];
                    var product = productById[dp.ProductId];
                    return new
                    {
                        dealId = dp.DealId,
                        productId = dp.ProductId,
                        productName = product.Name ?? string.Empty,
                        productSlug = product.Slug ?? string.Empty,
                        price = dp.Price,
                        discountPercent = deal.DiscountPercent ?? 0
                    };
                })
                .Where(r => string.IsNullOrWhiteSpace(q)
                    || r.dealId.ToString().Contains(q)
                    || r.productId.ToString().Contains(q)
                    || r.productName.ToLowerInvariant().Contains(ql)
                    || r.productSlug.ToLowerInvariant().Contains(ql))
                .OrderByDescending(r => r.discountPercent)
                .ThenBy(r => r.price)
                .Take(l)
                .ToList();

            return Ok(rows);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load deal options for social posts. query={Query} limit={Limit}", query, limit);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to load deal options" });
        }
    }

    // ── Approve ───────────────────────────────────────────────────────────

    [HttpPost("{id:long}/approve")]
    [Authorize]
    public async Task<IActionResult> Approve(long id, [FromBody] ApproveRequest req, CancellationToken ct = default)
    {
        var auth = await EnsureAdminAsync();
        if (auth != null) return auth;

        var ok = await _socialPostService.ApproveAsync(id, req.CaptionId, req.AdminNotes, ct);
        if (!ok) return NotFound();
        return Ok(new { message = "Post approved" });
    }

    // ── Reject ────────────────────────────────────────────────────────────

    [HttpPost("{id:long}/reject")]
    [Authorize]
    public async Task<IActionResult> Reject(long id, [FromBody] RejectRequest req, CancellationToken ct = default)
    {
        var auth = await EnsureAdminAsync();
        if (auth != null) return auth;

        var ok = await _socialPostService.RejectAsync(id, req.AdminNotes, ct);
        if (!ok) return NotFound();
        return Ok(new { message = "Post rejected" });
    }

    // ── Update caption ────────────────────────────────────────────────────

    [HttpPut("{id:long}/captions/{captionId:long}")]
    [Authorize]
    public async Task<IActionResult> UpdateCaption(
        long id, long captionId,
        [FromBody] UpdateCaptionRequest req,
        CancellationToken ct = default)
    {
        var auth = await EnsureAdminAsync();
        if (auth != null) return auth;

        if (string.IsNullOrWhiteSpace(req.CaptionText))
            return BadRequest(new { error = "caption_text is required" });

        var ok = await _socialPostService.UpdateCaptionAsync(id, captionId, req.CaptionText, ct);
        if (!ok) return NotFound();
        return Ok(new { message = "Caption updated" });
    }

    // ── Post now ──────────────────────────────────────────────────────────

    [HttpPost("{id:long}/post-now")]
    [Authorize]
    public async Task<IActionResult> PostNow(long id, CancellationToken ct = default)
    {
        var auth = await EnsureAdminAsync();
        if (auth != null) return auth;

        var result = await _socialPostService.PostNowAsync(id, ct);
        return Ok(result);
    }

    // ── Manual generate ───────────────────────────────────────────────────

    [HttpPost("generate")]
    [Authorize]
    public async Task<IActionResult> Generate([FromBody] GenerateRequest? req, CancellationToken ct = default)
    {
        var auth = await EnsureAdminAsync();
        if (auth != null) return auth;

        var options = req == null
            ? null
            : new SocialPostGenerationOptions(
                Count: req.Count,
                MaxPerProductPerDay: req.MaxPerProductPerDay,
                DealIds: req.DealIds,
                ProductIds: req.ProductIds,
                PriorityDealIds: req.PriorityDealIds,
                PriorityProductIds: req.PriorityProductIds,
                ExcludedDealIds: req.ExcludedDealIds,
                ExcludedProductIds: req.ExcludedProductIds);

        var count = await _socialPostService.GenerateDailyPostsAsync(options, ct);
        return Ok(new { generated = count });
    }

    [HttpDelete("{id:long}")]
    [Authorize]
    public async Task<IActionResult> Delete(long id, CancellationToken ct = default)
    {
        var auth = await EnsureAdminAsync();
        if (auth != null) return auth;

        var ok = await _socialPostService.DeleteAsync(id, ct);
        if (!ok) return NotFound();
        return Ok(new { message = "Post deleted" });
    }

    [HttpPost("generate-weekly")]
    [Authorize]
    public async Task<IActionResult> GenerateWeekly(CancellationToken ct = default)
    {
        var auth = await EnsureAdminAsync();
        if (auth != null) return auth;

        var ok = await _socialPostService.GenerateWeeklyDigestAsync(ct);
        return Ok(new { success = ok });
    }

    [HttpPost("{id:long}/generate-card")]
    [Authorize]
    public async Task<IActionResult> GenerateCard(long id, CancellationToken ct = default)
    {
        var auth = await EnsureAdminAsync();
        if (auth != null) return auth;

        var bytes = await _socialPostService.GenerateCardImageAsync(id, ct);
        if (bytes == null || bytes.Length == 0)
            return StatusCode(500, new { message = "Card image generation failed" });

        return File(bytes, "image/png");
    }

    // ── Request models ────────────────────────────────────────────────────

    public sealed record ApproveRequest(
        [property: JsonProperty("caption_id"), JsonPropertyName("caption_id")] long? CaptionId,
        [property: JsonProperty("admin_notes"), JsonPropertyName("admin_notes")] string? AdminNotes);

    public sealed record RejectRequest(
        [property: JsonProperty("admin_notes"), JsonPropertyName("admin_notes")] string? AdminNotes);

    public sealed record UpdateCaptionRequest(
        [property: JsonProperty("caption_text"), JsonPropertyName("caption_text")] string? CaptionText);

    public sealed record GenerateRequest(
        [property: JsonProperty("count"), JsonPropertyName("count")] int? Count,
        [property: JsonProperty("max_per_product_per_day"), JsonPropertyName("max_per_product_per_day")] int? MaxPerProductPerDay,
        [property: JsonProperty("deal_ids"), JsonPropertyName("deal_ids")] List<int>? DealIds,
        [property: JsonProperty("product_ids"), JsonPropertyName("product_ids")] List<int>? ProductIds,
        [property: JsonProperty("priority_deal_ids"), JsonPropertyName("priority_deal_ids")] List<int>? PriorityDealIds,
        [property: JsonProperty("priority_product_ids"), JsonPropertyName("priority_product_ids")] List<int>? PriorityProductIds,
        [property: JsonProperty("excluded_deal_ids"), JsonPropertyName("excluded_deal_ids")] List<int>? ExcludedDealIds,
        [property: JsonProperty("excluded_product_ids"), JsonPropertyName("excluded_product_ids")] List<int>? ExcludedProductIds);
}
