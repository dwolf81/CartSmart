using CartSmart.API.Models;
using CartSmart.API.Models.DTOs;
using System.Threading.Tasks;

namespace CartSmart.API.Services
{
    public interface IAuthService
    {
        Task<AuthResponse> RegisterAsync(RegisterRequest request);
        Task<AuthResponse> LoginAsync(LoginRequest request);
        Task<AuthResponse> SocialLoginAsync(SocialLoginRequest request);
        Task<bool> ValidateTokenAsync(string token);
        Task<bool> IsUserAuthenticatedAsync();
        string? GetCurrentUserId();
        Task<AuthUserDTO?> GetCurrentUser();
        Task<AuthUserDTO?> FindByEmailAsync(string email);
        Task SetEmailConfirmedAsync(int userId);
        Task<bool> UpdatePasswordAsync(int userId, string newPassword);
        Task<bool> VerifyPassword(string userId, string password);
        Task<User?> FindByIdAsync(int userId);
        void ClearUserSession();
        Task SetActiveAsync(int userId);
    }

}