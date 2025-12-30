using System.Net.Http.Json;

namespace CartSmart.API.Services;

public class FacebookAuthService : IFacebookAuthService
{
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;

    public FacebookAuthService(IConfiguration configuration)
    {
        _configuration = configuration;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://graph.facebook.com/v12.0/")
        };
    }

    public async Task<FacebookUserInfo> ValidateTokenAsync(string accessToken)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"me?fields=email,first_name,last_name&access_token={accessToken}");

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception("Invalid Facebook token");
            }

            var userData = await response.Content.ReadFromJsonAsync<FacebookGraphResponse>();

            if (userData == null)
            {
                throw new Exception("Invalid Facebook response");
            }

            return new FacebookUserInfo
            {
                Email = userData.Email,
                FirstName = userData.FirstName,
                LastName = userData.LastName
            };
        }
        catch
        {
            throw new Exception("Failed to validate Facebook token");
        }
    }

    public async Task<string?> GetFacebookUserEmailAsync(string accessToken)
    {
        var userInfo = await ValidateTokenAsync(accessToken);
        return userInfo.Email;
    }
}

internal class FacebookGraphResponse
{
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
} 