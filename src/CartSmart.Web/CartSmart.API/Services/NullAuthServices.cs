namespace CartSmart.API.Services;

public class NullGoogleAuthService : IGoogleAuthService
{
    public Task<GoogleUserInfo> ValidateTokenAsync(string token)
    {
        throw new NotImplementedException("Google authentication is not configured");
    }
}

public class NullFacebookAuthService : IFacebookAuthService
{
    public Task<FacebookUserInfo> ValidateTokenAsync(string accessToken)
    {
        throw new NotImplementedException("Facebook authentication is not configured");
    }

    public Task<string?> GetFacebookUserEmailAsync(string accessToken)
    {
        throw new NotImplementedException("Facebook authentication is not configured");
    }
} 

    public class NullAppleAuthService : IAppleAuthService
    {
        public Task<AppleUserInfo> ValidateTokenAsync(string idToken, CancellationToken ct = default)
            => throw new InvalidOperationException("Apple OAuth not configured.");
    }