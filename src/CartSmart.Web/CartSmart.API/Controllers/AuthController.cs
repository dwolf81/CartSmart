using Microsoft.AspNetCore.Mvc;
using CartSmart.API.Services;
using CartSmart.API.Models.DTOs;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Microsoft.Extensions.Configuration;
using CartSmart.API.Security;
using CartSmart.API.Models;
using Microsoft.AspNetCore.Authorization;

namespace CartSmart.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [AllowAnonymous]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;
        private readonly IConfiguration _configuration;

        public AuthController(IAuthService authService, IConfiguration configuration)
        {
            _authService = authService;
            _configuration = configuration;
        }

        [HttpPost("register")]
        public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request,[FromServices] IUserTokenService tokens,
            [FromServices] IEmailService emailService)
        {
            // Verify reCAPTCHA
            if (!string.IsNullOrEmpty(request.RecaptchaToken))
            {
                var isValid = await VerifyRecaptcha(request.RecaptchaToken);
                if (!isValid)
                {
                    return BadRequest(new { message = "reCAPTCHA verification failed" });
                }
            }

            var response = await _authService.RegisterAsync(request);
            if (!response.Success)
            {
                return BadRequest(response);
            }
            if (response.Success && response.User != null)
            {
                // manual register: create activation token
                var clientUser = await _authService.FindByEmailAsync(response.User.Email);
                if (clientUser != null && !clientUser.Active)
                {
                    var token = await tokens.CreateAsync(clientUser.Id, "acct-activate", TimeSpan.FromHours(24));
                    var appUrl = _configuration["App:BaseUrl"]?.TrimEnd('/') ?? "http://localhost:3000";
                    var link = $"{appUrl}/activate?token={Uri.EscapeDataString(token)}";
                    await emailService.SendAsync(
                        response.User.Email,
                        "Activate your CartSmart account",
                        // Plain text version (include full URL)
                        $"Welcome to CartSmart!\n\nTo activate your account, visit:\n{link}\n\nIf you did not create this account, ignore this email.",
                        // HTML version (descriptive anchor + fallback URL)
                        $@"
<p>Welcome to <strong>CartSmart</strong>!</p>
<p>To activate your account, click the button below:</p>
<p><a href=""{link}"" style=""display:inline-block;padding:10px 16px;background:#4CAF50;color:#ffffff;text-decoration:none;border-radius:4px;font-weight:600;"">
Activate your CartSmart account
</a></p>
<p>If the button doesn’t work, copy and paste this URL into your browser:<br>
<span style=""font-size:12px;color:#555;"">{System.Net.WebUtility.HtmlEncode(link)}</span></p>
<p>If you did not create this account, you can ignore this email.</p>
"
                    );
                }
            }
            return Ok(response);
        }

        [HttpPost("login")]
       
        public async Task<ActionResult<AuthResponse>> Login(LoginRequest request)
        {
            var result = await _authService.LoginAsync(request);
            if (!result.Success)
            {
                if (result.LockedOut)
                    return StatusCode(423, new { message = result.Message }); // 423 Locked
                if (result.ActivationRequired)
                    return StatusCode(403, new { message = result.Message, activationRequired = true });
                return Unauthorized(new { message = result.Message });
            }
            return Ok(result);
        }

        [HttpPost("social-login")]
       
        public async Task<ActionResult<AuthResponse>> SocialLogin(SocialLoginRequest request)
        {
            var response = await _authService.SocialLoginAsync(request);
            if (!response.Success)
            {
                return BadRequest(response);
            }
            return Ok(response);
        }

        [HttpGet("check-auth")]
       
        public async Task<IActionResult> CheckAuth()
        {
            var user = await _authService.GetCurrentUser();
            if (user == null)
            {
                return Unauthorized(new { message = "User not logged in." });
            }
            return Ok(new { user });
        }

        [HttpPost("logout")]
        public IActionResult Logout()
        {
            _authService.ClearUserSession();

            var expired = new CookieOptions
            {
                Expires = DateTimeOffset.UnixEpoch,   // already expired
                MaxAge = TimeSpan.Zero,
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None,         // MUST match how it was originally set
                Path = "/"
                // Domain = "yourdomain.com" // add if you set Domain when creating
            };

            // Expire the JWT cookie actually used
            Response.Cookies.Append("access_token", string.Empty, expired);

            // (Optional) clean up legacy names if previously used
            Response.Cookies.Append("AuthToken", string.Empty, expired);
            Response.Cookies.Append("RefreshToken", string.Empty, expired);

            // Prevent caching
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "0";

            return Ok(new { message = "Logged out" });
        }

        [HttpPost("welcome-email")]
        public async Task<IActionResult> SendWelcome(string email, [FromServices] IEmailService emailService)
        {
            await emailService.SendAsync(
                email,
                "Welcome to CartSmart",
                "Thanks for joining CartSmart!",
                "<p>Thanks for joining <strong>CartSmart</strong>!</p>"
            );
            return Ok();
        }

        // Request password reset (does not reveal if email exists)
        public sealed class RequestPasswordResetDTO { public string Email { get; set; } = ""; }

        [HttpPost("request-password-reset")]
       
        public async Task<IActionResult> RequestPasswordReset([FromBody] RequestPasswordResetDTO dto,
            [FromServices] IUserTokenService tokens,
            [FromServices] IEmailService email)
        {
            if (string.IsNullOrWhiteSpace(dto.Email)) return Ok();

            var user = await _authService.FindByEmailAsync(dto.Email);
            if (user == null) return Ok();

            // If SSO-only (no password set), silently succeed
            if (user.SsoProvider != null && !user.HasPassword)
                return Ok();

            var token = await tokens.CreateAsync(user.Id, "pwd-reset", TimeSpan.FromHours(1));
            var appUrl = _configuration["App:BaseUrl"]?.TrimEnd('/') ?? "http://localhost:3000";
            var link = $"{appUrl}/reset-password?token={Uri.EscapeDataString(token)}";

            await email.SendAsync(
                user.Email,
                "Reset your CartSmart password",
                $"Use this link to reset your password: {link}",
                $"<p>Use this link to reset your password:</p><p><a href=\"{link}\">Reset Password</a></p>"
            );

            return Ok();
        }

        public sealed class ResetPasswordDTO { public string Token { get; set; } = ""; public string NewPassword { get; set; } = ""; }

        [HttpPost("reset-password")]
       
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDTO req, [FromServices] IUserTokenService tokens)
        {
            // req has Token and NewPassword
            var t = await tokens.GetValidAsync(req.Token, "pwd-reset");
            if (t == null) return BadRequest(new { message = "Invalid or expired token" });

            var user = await _authService.FindByIdAsync(t.UserId);
            if (user == null) return BadRequest(new { message = "Account not found." });

            if (!PasswordPolicy.TryValidate(req.NewPassword, user.Email, user.FirstName, user.LastName, out var policyError))
                return BadRequest(new { message = policyError });

            var updated = await _authService.UpdatePasswordAsync(t.UserId, req.NewPassword);
            if (!updated) return StatusCode(500, new { message = "Failed to update password." });

            await tokens.ConsumeAsync(t.Id);

            return Ok(new { message = "Password updated." });
        }

        // Send email confirmation
        [HttpPost("send-confirm-email")]
       
        public async Task<IActionResult> SendConfirmEmail([FromServices] IUserTokenService tokens,
            [FromServices] IEmailService email)
        {
            var user = await _authService.GetCurrentUser();
            if (user == null) return Unauthorized();

            if (user.EmailConfirmed) return Ok();

            var token = await tokens.CreateAsync(user.Id, "email-confirm", TimeSpan.FromHours(24));
            var appUrl = _configuration["App:BaseUrl"]?.TrimEnd('/') ?? "http://localhost:3000";
            var link = $"{appUrl}/confirm-email?token={Uri.EscapeDataString(token)}";

            await email.SendAsync(
                user.Email,
                "Confirm your CartSmart email",
                $"Confirm your email: {link}",
                $"<p>Confirm your email by clicking <a href=\"{link}\">this link</a>.</p>"
            );

            return Ok();
        }

        // Confirm email (token comes from query or body)
        [HttpPost("confirm-email")]
       
        public async Task<IActionResult> ConfirmEmail([FromQuery] string? tokenQ,
            [FromBody] Dictionary<string, string>? body,
            [FromServices] IUserTokenService tokens)
        {
            var token = tokenQ ?? body?.GetValueOrDefault("token") ?? "";
            if (string.IsNullOrWhiteSpace(token)) return BadRequest(new { message = "Token required" });

            var t = await tokens.GetValidAsync(token, "email-confirm");
            if (t == null) return BadRequest(new { message = "Invalid or expired token" });

            await _authService.SetEmailConfirmedAsync(t.UserId);
            await tokens.ConsumeAsync(t.Id);

            return Ok();
        }

        // Allow SSO users to set a password later
        public sealed class SetPasswordDTO { public string NewPassword { get; set; } = ""; }

        [HttpPost("set-password")]
       
        public async Task<IActionResult> SetPassword([FromBody] SetPasswordDTO dto)
        {
            var user = await _authService.GetCurrentUser();
            if (user == null) return Unauthorized();

            // If already has a password, require a different flow (change password)
            if (user.HasPassword)
                return BadRequest(new { message = "Password already set." });

            await _authService.UpdatePasswordAsync(user.Id, dto.NewPassword);
            return Ok();
        }

        [HttpPost("send-activation")]
        public async Task<IActionResult> SendActivation([FromBody] Dictionary<string,string> body,
            [FromServices] IUserTokenService tokens,
            [FromServices] IEmailService emailService)
        {
            var email = body.GetValueOrDefault("email") ?? "";
            if (string.IsNullOrWhiteSpace(email)) return Ok();
            var user = await _authService.FindByEmailAsync(email);
            if (user == null || user.Active) return Ok();

            var token = await tokens.CreateAsync(user.Id, "acct-activate", TimeSpan.FromHours(24));
            var appUrl = _configuration["App:BaseUrl"]?.TrimEnd('/') ?? "http://localhost:3000";
            var link = $"{appUrl}/activate?token={Uri.EscapeDataString(token)}";

            await emailService.SendAsync(
                email,
                "Activate your CartSmart account",
                // Plain text
                $"Welcome to CartSmart!\n\nTo activate your account, visit:\n{link}\n\nIf you did not create this account, ignore this email.",
                // HTML
                $@"
<p>Welcome to <strong>CartSmart</strong>!</p>
<p>To activate your account, click the button below:</p>
<p><a href=""{link}"" style=""display:inline-block;padding:10px 16px;background:#4CAF50;color:#ffffff;text-decoration:none;border-radius:4px;font-weight:600;"">
Activate your CartSmart account
</a></p>
<p>If the button doesn’t work, copy and paste this URL into your browser:<br>
<span style=""font-size:12px;color:#555;"">{System.Net.WebUtility.HtmlEncode(link)}</span></p>
<p>If you did not create this account, you can ignore this email.</p>
"
    );

    return Ok(new { message = "Activation email sent." });
        }

        [HttpPost("activate")]
       
        public async Task<IActionResult> Activate([FromBody] Dictionary<string,string> body,
            [FromServices] IUserTokenService tokens)
        {
            var token = body.GetValueOrDefault("token") ?? "";
            if (string.IsNullOrWhiteSpace(token)) return BadRequest(new { message = "Token required" });
            var t = await tokens.GetValidAsync(token, "acct-activate");
            if (t == null) return BadRequest(new { message = "Invalid or expired token" });
            await _authService.SetActiveAsync(t.UserId);
            await tokens.ConsumeAsync(t.Id);
            return Ok(new { message = "Account activated." });
        }

        private async Task<bool> VerifyRecaptcha(string token)
        {
            var secretKey = _configuration["ReCaptcha:SecretKey"];
            var httpClient = new HttpClient();

            var response = await httpClient.PostAsync($"https://www.google.com/recaptcha/api/siteverify",
                new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("secret", secretKey),
                    new KeyValuePair<string, string>("response", token)
                }));

            var jsonResponse = await response.Content.ReadAsStringAsync();
            dynamic result = Newtonsoft.Json.JsonConvert.DeserializeObject(jsonResponse);

            return result.success == true;
        }
    }
}