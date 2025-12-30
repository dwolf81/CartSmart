using CartSmart.API.Models;

namespace CartSmart.API.Services
{
    public interface IUserTokenService
    {
        Task<string> CreateAsync(int userId, string type, TimeSpan ttl, CancellationToken ct = default);
        Task<UserToken?> GetValidAsync(string token, string type, CancellationToken ct = default);
        Task ConsumeAsync(int tokenId, CancellationToken ct = default);
    }
}