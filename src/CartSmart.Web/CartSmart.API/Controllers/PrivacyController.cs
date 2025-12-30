using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using CartSmart.API.Models;
using CartSmart.API.Services;            // ADD: namespace where ISupabaseService lives
using System.Linq;                       // ADD: for FirstOrDefault
using System;                            // ADD: for DateTime

namespace CartSmart.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class PrivacyController : ControllerBase
    {
        private readonly ISupabaseService _supabase;
        private readonly IAuthService _auth;

        public PrivacyController(ISupabaseService supabase, IAuthService auth)
        {
            _supabase = supabase;
            _auth = auth;
        }

        [HttpGet("preferences")]
        public async Task<IActionResult> Get()
        {
            var uidStr = _auth.GetCurrentUserId();
            if (!int.TryParse(uidStr, out var uid)) return Unauthorized();
            var prefs = (await _supabase.GetAllAsync<UserPrivacyPreference>()).FirstOrDefault(p => p.UserId == uid);
            return Ok(prefs ?? new UserPrivacyPreference { UserId = uid });
        }

        public class UpdatePrivacyDTO
        {
            public bool Performance { get; set; }
            public bool Analytics { get; set; }
            public bool Advertising { get; set; }
            public bool SaleShareOptOut { get; set; }
        }

        [HttpPost("preferences")]
        public async Task<IActionResult> Update(UpdatePrivacyDTO dto)
        {
            var uidStr = _auth.GetCurrentUserId();
            if (!int.TryParse(uidStr, out var uid)) return Unauthorized();

            var client = _supabase.GetClient();
            var existing = (await _supabase.GetAllAsync<UserPrivacyPreference>()).FirstOrDefault(p => p.UserId == uid);
            if (existing == null)
            {
                var created = await _supabase.InsertAsync(new UserPrivacyPreference
                {
                    UserId = uid,
                    Performance = dto.Performance,
                    Analytics = dto.Analytics,
                    Advertising = dto.Advertising,
                    SaleShareOptOut = dto.SaleShareOptOut,
                    UpdatedAt = DateTime.UtcNow
                });
                return Ok(created);
            }
            existing.Performance = dto.Performance;
            existing.Analytics = dto.Analytics;
            existing.Advertising = dto.Advertising;
            existing.SaleShareOptOut = dto.SaleShareOptOut;
            existing.UpdatedAt = DateTime.UtcNow;
            var updated = await _supabase.UpdateAsync(existing);
            return Ok(updated);
        }
    }
}