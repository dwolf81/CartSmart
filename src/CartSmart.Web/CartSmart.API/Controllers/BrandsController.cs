using CartSmart.API.Models;
using CartSmart.API.Models.DTOs;
using CartSmart.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CartSmart.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class BrandsController : ControllerBase
    {
        private readonly ISupabaseService _supabase;
        private readonly IAuthService _authService;
        private readonly IUserService _userService;

        public BrandsController(ISupabaseService supabase, IAuthService authService, IUserService userService)
        {
            _supabase = supabase;
            _authService = authService;
            _userService = userService;
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

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetBrands()
        {
            var auth = await EnsureAdminAsync();
            if (auth != null) return auth;

            var client = _supabase.GetServiceRoleClient();
            var resp = await client
                .From<Brand>()
                .Get();

            var results = (resp.Models ?? new List<Brand>())
                .Where(b => !string.IsNullOrWhiteSpace(b.Name))
                .OrderBy(b => b.Name)
                .Select(b => new BrandDTO
                {
                    Id = b.Id,
                    Name = b.Name,
                    Url = b.URL
                })
                .ToList();

            return Ok(results);
        }

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> CreateBrand([FromBody] AdminCreateBrandRequestDTO req)
        {
            var auth = await EnsureAdminAsync();
            if (auth != null) return auth;

            var name = (req?.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                return BadRequest(new { message = "name is required" });

            var client = _supabase.GetServiceRoleClient();

            // Attempt to dedupe by name (case-insensitive) by pulling likely matches.
            // Keep it simple: fetch all and compare in-memory.
            var all = await client.From<Brand>().Get();
            var existing = (all.Models ?? new List<Brand>())
                .FirstOrDefault(b => !string.IsNullOrWhiteSpace(b.Name) && string.Equals(b.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                return Ok(new BrandDTO { Id = existing.Id, Name = existing.Name, Url = existing.URL });
            }

            var toInsert = new Brand
            {
                Name = name,
                URL = string.IsNullOrWhiteSpace(req?.Url) ? null : req!.Url!.Trim()
            };

            var insertResp = await client.From<Brand>().Insert(toInsert);
            var inserted = insertResp.Models.FirstOrDefault();
            if (inserted == null)
                return StatusCode(500, new { message = "Failed to create brand" });

            return Ok(new BrandDTO { Id = inserted.Id, Name = inserted.Name, Url = inserted.URL });
        }
    }
}
