using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using CartSmart.API.Services;
using CartSmart.API.Models;
using CartSmart.API.Models.DTOs;
using CartSmart.API.Security;
using Microsoft.AspNetCore.Http;

namespace CartSmart.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class UsersController : ControllerBase
    {
        private readonly IUserService _userService;
        private readonly IAuthService _authService;

        public UsersController(IUserService userService, IAuthService authService)
        {
            _userService = userService;
            _authService = authService;
        }

        // Get current user's terms acceptance
        [HttpGet("terms")]
        public async Task<IActionResult> GetTermsAcceptance()
        {
            var userIdStr = _authService.GetCurrentUserId();
            if (string.IsNullOrWhiteSpace(userIdStr) || !int.TryParse(userIdStr, out var userId))
                return Unauthorized();

            var user = await _userService.GetUserByIdAsync(userId);
            if (user == null) return NotFound(new { message = "User not found." });

            return Ok(new
            {
                termsAccepted = user.TermsAcceptedAtUtc.HasValue,
                termsVersion = user.TermsVersion,
                acceptedAtUtc = user.TermsAcceptedAtUtc
            });
        }

        public class AcceptTermsRequest
        {
            public string Version { get; set; } = "v1";
        }

        // Accept terms for current user
        [HttpPost("terms/accept")]
        public async Task<IActionResult> AcceptTerms([FromBody] AcceptTermsRequest req)
        {
            var userIdStr = _authService.GetCurrentUserId();
            if (string.IsNullOrWhiteSpace(userIdStr) || !int.TryParse(userIdStr, out var userId))
                return Unauthorized();

            var user = await _userService.GetUserByIdAsync(userId);
            if (user == null) return NotFound(new { message = "User not found." });

            user.TermsVersion = string.IsNullOrWhiteSpace(req?.Version) ? "v1" : req!.Version.Trim();
            user.TermsAcceptedAtUtc = DateTime.UtcNow;

            var updated = await _userService.UpdateUserAsync(user);

            return Ok(new
            {
                termsAccepted = true,
                termsVersion = updated.TermsVersion,
                acceptedAtUtc = updated.TermsAcceptedAtUtc
            });
        }

        // Get current user's profile (for Settings page)
        [HttpGet("profile")]
        public async Task<IActionResult> GetProfile()
        {
            var userIdStr = _authService.GetCurrentUserId();
            if (string.IsNullOrWhiteSpace(userIdStr) || !int.TryParse(userIdStr, out var userId))
                return Unauthorized();

            var dto = await _userService.GetUserDTOByIdAsync(userId);
            if (dto == null) return NotFound(new { message = "User not found" });

            return Ok(new
            {
                userName = dto.UserName,
                email = dto.Email,
                firstName = dto.FirstName,
                lastName = dto.LastName,
                displayName = dto.DisplayName,
                imageUrl = dto.ImageUrl,     // add avatar for header/profile
                allowReview = dto.AllowReview, // add review access for menu gating
                admin = dto.Admin,
                level = dto.Level,
                optedIntoEmails = dto.EmailOptIn,
                dealsPosted = dto.DealsPosted,
                bio = dto.Bio,
                id = dto.Id
            });
        }

        public class ProfileUpdateRequest
        {
            public string? UserName { get; set; }
            public string? FirstName { get; set; }
            public string? LastName { get; set; }
            public string CurrentPassword { get; set; } = string.Empty;
        }

        // Update current user's profile; requires current password
        [HttpPut("profile")]
        public async Task<IActionResult> UpdateOwnProfile([FromBody] ProfileUpdateRequest req)
        {
            if (req == null) return BadRequest(new { message = "Invalid payload." });

            var userIdStr = _authService.GetCurrentUserId();
            if (string.IsNullOrWhiteSpace(userIdStr) || !int.TryParse(userIdStr, out var userId))
                return Unauthorized();

            if (string.IsNullOrWhiteSpace(req.CurrentPassword))
                return BadRequest(new { message = "Current password is required." });

            // Verify current password
            var verified = await _authService.VerifyPassword(userIdStr, req.CurrentPassword);
            if (!verified) return BadRequest(new { message = "Current password is incorrect." });

            // Load current user
            var existing = await _userService.GetUserByIdAsync(userId);
            if (existing == null) return NotFound(new { message = "User not found." });

            // Username policy + availability
            if (!string.IsNullOrWhiteSpace(req.UserName) &&
                !string.Equals(existing.Username, req.UserName, StringComparison.OrdinalIgnoreCase))
            {
                if (!UsernamePolicy.TryValidate(req.UserName, out var policyMsg))
                    return BadRequest(new { message = policyMsg });

                var available = await _userService.IsUsernameAvailableAsync(req.UserName, userId);
                if (!available) return BadRequest(new { message = "Username is already taken." });

                existing.Username = req.UserName.Trim();
            }

            // Update optional profile fields (email is read-only for now)
            if (!string.IsNullOrWhiteSpace(req.FirstName)) existing.FirstName = req.FirstName.Trim();
            if (!string.IsNullOrWhiteSpace(req.LastName)) existing.LastName = req.LastName.Trim();

            var updated = await _userService.UpdateUserAsync(existing);

            return Ok(new
            {
                userName = updated.Username,
                email = updated.Email,
                firstName = updated.FirstName,
                lastName = updated.LastName,
                optedIntoEmails = updated.EmailOptIn,
                displayName = updated.DisplayName,
                level = updated.Level
            });
        }

        // Validate username: policy + availability
        [HttpGet("validate-username")]
        public async Task<IActionResult> ValidateUsername([FromQuery] string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return BadRequest(new { valid = false, available = false, message = "Username is required." });

            if (!UsernamePolicy.TryValidate(username, out var msg))
                return Ok(new { valid = false, available = false, message = msg });

            int? excludeId = null;
            var uid = _authService.GetCurrentUserId();
            if (int.TryParse(uid, out var parsed)) excludeId = parsed;

            var available = await _userService.IsUsernameAvailableAsync(username, excludeId);
            return Ok(new { valid = true, available, message = available ? "" : "Username is already taken." });
        }

        // Update current user's settings (opt-in)
        [HttpPut("/api/user/settings")]
        public async Task<IActionResult> UpdateSettings([FromBody] dynamic body)
        {
            bool optedIntoEmails;
            try
            {
                optedIntoEmails = (bool)(body?.optedIntoEmails ?? false);
            }
            catch
            {
                return BadRequest(new { message = "Invalid payload." });
            }

            var userIdStr = _authService.GetCurrentUserId();
            if (string.IsNullOrWhiteSpace(userIdStr) || !int.TryParse(userIdStr, out var userId))
                return Unauthorized();

            var user = await _userService.GetUserByIdAsync(userId);
            if (user == null) return NotFound(new { message = "User not found." });

            user.EmailOptIn = optedIntoEmails;
            await _userService.UpdateUserAsync(user);

            return Ok(new { optedIntoEmails });
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateProfile(string id, [FromBody] UserDTO updatedUser)
        {
            try
            {
                if (int.TryParse(id, out int userId))
                {
                    var user = new User
                    {
                        Id = userId,
                        Username = updatedUser.UserName,
                        Email = updatedUser.Email,
                        Bio = updatedUser.Bio,
                        ImageUrl = updatedUser.ImageUrl,
                        FirstName = updatedUser.FirstName,
                        LastName = updatedUser.LastName,
                        EmailOptIn = updatedUser.EmailOptIn,
                        DisplayName = updatedUser.DisplayName,
                    };

                    var updatedUserResult = await _userService.UpdateUserAsync(user);
                    
                    // Convert back to DTO for response
                    var userDto = new UserDTO
                    {
                        Id = updatedUserResult.Id,
                        UserName = updatedUserResult.Username,
                        Email = updatedUserResult.Email,
                        Bio = updatedUserResult.Bio,
                        ImageUrl = updatedUserResult.ImageUrl,
                        FirstName = updatedUserResult.FirstName,
                        LastName = updatedUserResult.LastName,
                        EmailOptIn = updatedUserResult.EmailOptIn,
                        DisplayName = updatedUserResult.DisplayName,
                        Level = updatedUserResult.Level // <-- Add this missing property
                    };

                    return Ok(userDto);
                }
                else
                {
                    return NotFound(new { message = "User not found" });
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }

        }

        [HttpGet("{id}/getprofile")]
        [AllowAnonymous]
        public async Task<ActionResult<UserDTO>> GetUserBySlug(string id)
        {
            UserDTO? user;
            if (int.TryParse(id, out int userId))
            {
                user = await _userService.GetUserDTOByIdAsync(userId);
            }
            else
            {
                user = await _userService.GetUserBySlugAsync(id);
            }

            if (user == null)
            {
                return NotFound(); // Explicitly return 404 when product is null
            }
            return Ok(user);
        }

        [HttpPost("change-password")]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req)
        {
            try
            {
                var currentUserId = _authService.GetCurrentUserId();
                if (string.IsNullOrEmpty(currentUserId))
                    return Unauthorized();

                var ok = await _authService.VerifyPassword(currentUserId, req.CurrentPassword);
                if (!ok)
                    return BadRequest(new { message = "Current password is incorrect" });

                if (!int.TryParse(currentUserId, out var uid))
                    return Unauthorized();

                var user = await _authService.FindByIdAsync(uid);
                if (user == null)
                    return NotFound(new { message = "User not found" });

                if (!PasswordPolicy.TryValidate(req.NewPassword, user.Email, user.FirstName, user.LastName, out var policyError))
                    return BadRequest(new { message = policyError });

                var updated = await _authService.UpdatePasswordAsync(uid, req.NewPassword);
                if (!updated)
                    return StatusCode(500, new { message = "Failed to update password." });

                return Ok(new { message = "Password changed successfully." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{id}/avatar")]
        public async Task<IActionResult> UploadAvatar(int id, IFormFile file)
        {
            try
            {
                if (file == null || file.Length == 0)
                    return BadRequest("No file uploaded");

                using var stream = file.OpenReadStream();
                var imageUrl = await _userService.UploadUserAvatarAsync(id, stream, file.FileName);


                return Ok(new { imageUrl });
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }


        public class ChangePasswordRequest
        {
            public string CurrentPassword { get; set; } = string.Empty;
            public string NewPassword { get; set; } = string.Empty;
        }

        public class AccountDeleteRequest
        {
            public string CurrentPassword { get; set; } = string.Empty;
            public string Confirmation { get; set; } = string.Empty; // expects "DELETE"
        }

        [HttpDelete("account")]        
        public async Task<IActionResult> DeleteAccount([FromBody] AccountDeleteRequest req)
        {
            if (req == null) return BadRequest(new { message = "Invalid payload." });
            if (string.IsNullOrWhiteSpace(req.CurrentPassword))
                return BadRequest(new { message = "Current password is required." });
            if (!string.Equals(req.Confirmation, "DELETE", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "Confirmation phrase must be DELETE." });

            var userIdStr = _authService.GetCurrentUserId();
            if (string.IsNullOrWhiteSpace(userIdStr) || !int.TryParse(userIdStr, out var userId))
                return Unauthorized();

            var verified = await _authService.VerifyPassword(userIdStr, req.CurrentPassword);
            if (!verified) return BadRequest(new { message = "Current password is incorrect." });

            var ok = await _userService.SoftDeleteUserAsync(userId);
            if (!ok) return NotFound(new { message = "User not found or already deleted." });

            return Ok(new { message = "Account deleted. All associated deals were marked as deleted." });
        }
    }
}