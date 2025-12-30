using CartSmart.API.Models;

namespace CartSmart.API.Services;

public class FacebookUserInfo
{
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
}

public interface IFacebookAuthService
{
    Task<FacebookUserInfo> ValidateTokenAsync(string accessToken);
    Task<string?> GetFacebookUserEmailAsync(string accessToken);
} 