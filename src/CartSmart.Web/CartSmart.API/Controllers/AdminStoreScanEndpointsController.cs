using CartSmart.API.Models;
using CartSmart.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CartSmart.API.Controllers;

/// <summary>
/// CRUD over store_scan_endpoint — admin-curated listing-index URLs the
/// discovery crawler is allowed to hit. Edited inside the Admin Store Modal,
/// kept in a separate controller so saving/removing endpoints does not
/// require re-saving the entire store row.
/// </summary>
[ApiController]
[Route("api/admin/stores/{storeId:int}/scan-endpoints")]
public sealed class AdminStoreScanEndpointsController : ControllerBase
{
    private readonly ISupabaseService _supabase;
    private readonly IAuthService _authService;
    private readonly IUserService _userService;
    private readonly ILogger<AdminStoreScanEndpointsController> _logger;

    public AdminStoreScanEndpointsController(
        ISupabaseService supabase,
        IAuthService authService,
        IUserService userService,
        ILogger<AdminStoreScanEndpointsController> logger)
    {
        _supabase = supabase;
        _authService = authService;
        _userService = userService;
        _logger = logger;
    }

    private async Task<IActionResult?> EnsureAdminAsync()
    {
        var idStr = _authService.GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(idStr) || !int.TryParse(idStr, out var id))
            return Unauthorized();
        var u = await _userService.GetUserByIdAsync(id);
        if (u == null) return Unauthorized();
        if (!u.Admin) return Forbid();
        return null;
    }

    public sealed class ScanEndpointRequest
    {
        public string Url { get; set; } = string.Empty;
        public string? Label { get; set; }
        public int? ProductTypeId { get; set; }
        public bool IsActive { get; set; } = true;
    }

    [HttpGet]
    [Authorize]
    public async Task<IActionResult> List(int storeId)
    {
        var auth = await EnsureAdminAsync();
        if (auth != null) return auth;

        var client = _supabase.GetServiceRoleClient();
        var resp = await client.From<StoreScanEndpoint>()
            .Filter("store_id", Supabase.Postgrest.Constants.Operator.Equals, storeId.ToString())
            .Order("created_at", Supabase.Postgrest.Constants.Ordering.Ascending)
            .Get();
        return Ok(resp.Models ?? new List<StoreScanEndpoint>());
    }

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create(int storeId, [FromBody] ScanEndpointRequest body)
    {
        var auth = await EnsureAdminAsync();
        if (auth != null) return auth;

        if (body == null || string.IsNullOrWhiteSpace(body.Url))
            return BadRequest(new { message = "url is required" });
        if (!Uri.TryCreate(body.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return BadRequest(new { message = "url must be a valid http(s) URL" });

        var client = _supabase.GetServiceRoleClient();
        var storeResp = await client.From<Store>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, storeId.ToString())
            .Limit(1)
            .Get();
        if (storeResp.Models.FirstOrDefault() == null)
            return NotFound(new { message = "Store not found" });

        var insert = new StoreScanEndpointInsertRow
        {
            StoreId = storeId,
            Url = body.Url.Trim(),
            Label = string.IsNullOrWhiteSpace(body.Label) ? null : body.Label.Trim(),
            ProductTypeId = body.ProductTypeId,
            IsActive = body.IsActive
        };
        var insertResp = await client.From<StoreScanEndpointInsertRow>().Insert(insert);
        var created = insertResp?.Models?.FirstOrDefault();
        return Ok(created);
    }

    [HttpPut("{id:int}")]
    [Authorize]
    public async Task<IActionResult> Update(int storeId, int id, [FromBody] ScanEndpointRequest body)
    {
        var auth = await EnsureAdminAsync();
        if (auth != null) return auth;

        if (body == null || string.IsNullOrWhiteSpace(body.Url))
            return BadRequest(new { message = "url is required" });

        var update = new StoreScanEndpointUpdateRow
        {
            Id = id,
            Url = body.Url.Trim(),
            Label = string.IsNullOrWhiteSpace(body.Label) ? null : body.Label.Trim(),
            ProductTypeId = body.ProductTypeId,
            IsActive = body.IsActive
        };
        var client = _supabase.GetServiceRoleClient();
        await client.From<StoreScanEndpointUpdateRow>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
            .Filter("store_id", Supabase.Postgrest.Constants.Operator.Equals, storeId.ToString())
            .Update(update);

        return NoContent();
    }

    [HttpDelete("{id:int}")]
    [Authorize]
    public async Task<IActionResult> Delete(int storeId, int id)
    {
        var auth = await EnsureAdminAsync();
        if (auth != null) return auth;

        var client = _supabase.GetServiceRoleClient();
        await client.From<StoreScanEndpoint>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
            .Filter("store_id", Supabase.Postgrest.Constants.Operator.Equals, storeId.ToString())
            .Delete();

        return NoContent();
    }
}
