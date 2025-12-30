namespace CartSmart.API.Services
{
    public class GoogleUserInfo
    {
        public string Email { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
    }

    public interface IGoogleAuthService
    {
        Task<GoogleUserInfo> ValidateTokenAsync(string token);
    }
} 