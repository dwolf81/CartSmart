using Microsoft.AspNetCore.Mvc;
using CartSmart.API.Services;
using System.Text.RegularExpressions;

namespace CartSmart.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ContactController : ControllerBase
    {
        private readonly IEmailService _emailService;
        private readonly IConfiguration _cfg;
        private static readonly Dictionary<string, DateTime> _throttle = new();
        private static readonly TimeSpan THROTTLE = TimeSpan.FromSeconds(30);

        public ContactController(IEmailService emailService, IConfiguration cfg)
        {
            _emailService = emailService;
            _cfg = cfg;
        }

        public class ContactDto
        {
            public string? Name { get; set; }
            public string? Email { get; set; }
            public string? Message { get; set; }
        }

        private static bool IsValidEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return false;
            // Case-insensitive to support lowercase addresses and domains
            var pattern = new Regex(@"^[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}$", RegexOptions.IgnoreCase);
            return pattern.IsMatch(email.Trim());
        }

        [HttpPost]
        public async Task<IActionResult> Post([FromBody] ContactDto dto)
        {
            if (dto == null) return BadRequest(new { message = "Invalid payload." });

            var name = (dto.Name ?? string.Empty).Trim();
            var email = (dto.Email ?? string.Empty).Trim();
            var msg = (dto.Message ?? string.Empty).Trim();

            if (name.Length < 2) return BadRequest(new { message = "Name too short." });
            if (!IsValidEmail(email)) return BadRequest(new { message = "Invalid email." });
            if (msg.Length < 10) return BadRequest(new { message = "Message too short." });
            if (msg.Length > 5000) return BadRequest(new { message = "Message too long." });

            var key = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            lock (_throttle)
            {
                if (_throttle.TryGetValue(key, out var last) && DateTime.UtcNow - last < THROTTLE)
                    return StatusCode(429, new { message = "Please wait before sending again." });
                _throttle[key] = DateTime.UtcNow;
            }

            var bodyPlain = $"Name: {name}\nEmail: {email}\nIP: {key}\n\nMessage:\n{msg}";
            var adminTo = _cfg["Email:To"] ?? _cfg["SendGrid:DefaultFromEmail"] ?? "no-reply@localhost";
            await _emailService.SendAsync(adminTo, $"Contact Form: {name}", bodyPlain, null, null, null, email);

            return Ok(new { message = "Sent." });
        }
    }
}