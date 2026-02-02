using CartSmart.API.Models;
using CartSmart.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace CartSmart.API.Controllers;

[ApiController]
[Route("api/admin/manual-price")]
public sealed class AdminManualPriceTasksController : ControllerBase
{
    private const int DealStatusActive = 2;
    private const int DealStatusSold = 7;
    private const int DealStatusOutOfStock = 8;

    private readonly ISupabaseService _supabase;
    private readonly IAuthService _authService;
    private readonly IUserService _userService;
    private readonly IMemoryCache _cache;

    public AdminManualPriceTasksController(
        ISupabaseService supabase,
        IAuthService authService,
        IUserService userService,
        IMemoryCache cache)
    {
        _supabase = supabase;
        _authService = authService;
        _userService = userService;
        _cache = cache;
    }

    private async Task<User?> GetCurrentUserAsync()
    {
        var userIdStr = _authService.GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(userIdStr) || !int.TryParse(userIdStr, out var userId))
            return null;
        return await _userService.GetUserByIdAsync(userId);
    }

    private async Task<IActionResult?> EnsureAdminAsync()
    {
        var user = await GetCurrentUserAsync();
        if (user == null) return Unauthorized();
        if (!user.Admin) return Forbid();
        return null;
    }

    [HttpGet("tasks")]
    [Authorize]
    public async Task<IActionResult> ListPending([FromQuery] int limit = 50)
    {
        var authResult = await EnsureAdminAsync();
        if (authResult != null) return authResult;

        var l = limit <= 0 ? 50 : Math.Min(limit, 200);
        var client = _supabase.GetServiceRoleClient();

        var tasksResp = await client
            .From<ManualPriceTask>()
            .Filter("status", Supabase.Postgrest.Constants.Operator.Equals, "pending")
            .Order("created_at", Supabase.Postgrest.Constants.Ordering.Descending)
            .Limit(l)
            .Get();

        var tasks = tasksResp.Models ?? new List<ManualPriceTask>();
        if (tasks.Count == 0) return Ok(Array.Empty<object>());

        var dealProductIds = tasks
            .Select(t => t.DealProductId)
            .Where(id => id > 0)
            .Distinct()
            .Cast<object>()
            .ToArray();

        var dealProducts = new Dictionary<int, DealProduct>();
        if (dealProductIds.Length > 0)
        {
            var dpResp = await client
                .From<DealProduct>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.In, dealProductIds)
                .Get();

            dealProducts = (dpResp.Models ?? new List<DealProduct>()).ToDictionary(x => x.Id, x => x);
        }

        var productIds = dealProducts.Values
            .Select(dp => dp.ProductId)
            .Where(id => id > 0)
            .Distinct()
            .Cast<object>()
            .ToArray();

        Dictionary<int, Product> productsById = new();
        if (productIds.Length > 0)
        {
            var pResp = await client
                .From<Product>()
                .Select("id, name, slug, image_url")
                .Filter("id", Supabase.Postgrest.Constants.Operator.In, productIds)
                .Get();
            productsById = (pResp.Models ?? new List<Product>()).ToDictionary(x => x.Id, x => x);
        }

        var payload = tasks.Select(t =>
        {
            dealProducts.TryGetValue(t.DealProductId, out var dp);
            Product? p = null;
            if (dp != null) productsById.TryGetValue(dp.ProductId, out p);

            return new
            {
                id = t.Id,
                dealProductId = t.DealProductId,
                url = t.Url,
                reason = t.Reason,
                status = t.Status,
                createdAt = t.CreatedAt,
                current = dp == null ? null : new
                {
                    dealId = dp.DealId,
                    productId = dp.ProductId,
                    productVariantId = dp.ProductVariantId,
                    price = dp.Price,
                    dealStatusId = dp.DealStatusId,
                    lastCheckedAt = dp.LastCheckedAt,
                },
                product = p == null ? null : new
                {
                    id = p.Id,
                    name = p.Name,
                    slug = p.Slug,
                    imageUrl = p.ImageUrl
                }
            };
        });

        return Ok(payload);
    }

    public sealed class SubmitManualPriceRequest
    {
        public decimal? Price { get; set; }
        public string? Currency { get; set; }
        public bool? InStock { get; set; }
        public bool? Sold { get; set; }
        public string? Notes { get; set; }
    }

    [HttpPost("tasks/{taskId:long}/submit")]
    [Authorize]
    public async Task<IActionResult> Submit(long taskId, [FromBody] SubmitManualPriceRequest? request)
    {
        var authResult = await EnsureAdminAsync();
        if (authResult != null) return authResult;
        if (taskId <= 0) return BadRequest(new { message = "Invalid taskId" });

        var user = await GetCurrentUserAsync();
        if (user == null) return Unauthorized();

        request ??= new SubmitManualPriceRequest();
        var currency = string.IsNullOrWhiteSpace(request.Currency) ? "USD" : request.Currency.Trim().ToUpperInvariant();

        var client = _supabase.GetServiceRoleClient();

        var taskResp = await client
            .From<ManualPriceTask>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, taskId.ToString())
            .Limit(1)
            .Get();
        var task = taskResp.Models?.FirstOrDefault();
        if (task == null) return NotFound(new { message = "Task not found" });
        if (!string.Equals(task.Status, "pending", StringComparison.OrdinalIgnoreCase))
            return NotFound(new { message = "Task not pending" });

        var dpResp = await client
            .From<DealProduct>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, task.DealProductId.ToString())
            .Limit(1)
            .Get();
        var dp = dpResp.Models?.FirstOrDefault();
        if (dp == null) return NotFound(new { message = "Deal product not found" });

        var now = DateTime.UtcNow;
        var statusChanged = false;
        var priceChanged = false;

        if (request.Sold == true && dp.DealStatusId != DealStatusSold)
        {
            dp.DealStatusId = DealStatusSold;
            statusChanged = true;
        }
        else if (request.InStock == false && dp.DealStatusId != DealStatusOutOfStock)
        {
            dp.DealStatusId = DealStatusOutOfStock;
            statusChanged = true;
        }
        else if (request.InStock == true && dp.DealStatusId != DealStatusActive)
        {
            dp.DealStatusId = DealStatusActive;
            statusChanged = true;
        }

        if (request.Price.HasValue && request.Price.Value > 0 && dp.Price != request.Price.Value)
        {
            dp.Price = request.Price.Value;
            priceChanged = true;
        }

        dp.ErrorCount = 0;
        dp.StaleAt = null;
        dp.LastCheckedAt = now;
        dp.NextCheckAt = now.AddHours(12);

        await client
            .From<DealProduct>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, dp.Id.ToString())
            .Update(dp);

        if (priceChanged)
        {
            var hist = new DealProductPriceHistory
            {
                DealProductId = dp.Id,
                Price = dp.Price,
                Currency = currency,
                ChangedAt = now
            };
            await client.From<DealProductPriceHistory>().Insert(hist);
        }

        if (statusChanged || priceChanged)
        {
            try
            {
                await client.Rpc("f_update_product_best_deal", new { p_product_id = dp.ProductId });
            }
            catch
            {
                // best-effort
            }
        }

        var update = new ManualPriceTaskUpdateRow
        {
            Id = taskId,
            Status = "completed",
            SubmittedAt = now,
            SubmittedPrice = request.Price,
            SubmittedCurrency = currency,
            SubmittedInStock = request.InStock,
            SubmittedSold = request.Sold,
            SubmittedBy = user.Username,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes
        };

        await client
            .From<ManualPriceTaskUpdateRow>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, taskId.ToString())
            .Update(update);

        _cache.Remove("bestDeals");
        _cache.Remove($"product:id:{dp.ProductId}");

        return Ok(new { ok = true });
    }
}
