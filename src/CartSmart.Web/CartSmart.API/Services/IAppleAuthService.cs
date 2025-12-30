using System.Threading;
using System.Threading.Tasks;

namespace CartSmart.API.Services
{
    public record AppleUserInfo(string Subject, string Email, string FirstName, string LastName);

    public interface IAppleAuthService
    {
        Task<AppleUserInfo> ValidateTokenAsync(string idToken, CancellationToken ct = default);
    }
}