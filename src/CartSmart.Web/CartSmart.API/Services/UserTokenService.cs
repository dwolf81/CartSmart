using System.Security.Cryptography;
using CartSmart.API.Models;

namespace CartSmart.API.Services
{
    public class UserTokenService : IUserTokenService
    {
        private readonly ISupabaseService _supabase;
        private static string GenerateSecureToken(int bytes = 32)
        {
            var b = new byte[bytes];
            RandomNumberGenerator.Fill(b);
            return Convert.ToBase64String(b).Replace("+", "").Replace("/", "").Replace("=", "");
        }

        public UserTokenService(ISupabaseService supabase)
        {
            _supabase = supabase;
        }

        public async Task<string> CreateAsync(int userId, string type, TimeSpan ttl, CancellationToken ct = default)
        {
            var token = GenerateSecureToken();
            var now = DateTime.UtcNow;
            var row = new UserToken
            {
                UserId = userId,
                Type = type,
                Token = token,
                ExpiresUtc = now.Add(ttl),
                Used = false,
                CreatedUtc = now
            };

            var client = _supabase.GetClient();
            await client.From<UserToken>().Insert(row /*, cancellationToken: ct */);
            return token;
        }

        public async Task<UserToken?> GetValidAsync(string token, string type, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(type))
                return null;

            var client = _supabase.GetClient();

            // Use an ISO format without fractional seconds issues (PostgREST friendly)
            var nowIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

            var resp = await client
                .From<UserToken>()
                .Filter("token", Supabase.Postgrest.Constants.Operator.Equals, token)          // string
                .Filter("type", Supabase.Postgrest.Constants.Operator.Equals, type)            // string (e.g. "pwd-reset")
                .Filter("used", Supabase.Postgrest.Constants.Operator.Equals, "false")         // bool as string
                .Filter("expires_utc", Supabase.Postgrest.Constants.Operator.GreaterThan, nowIso) // date as ISO string
                .Limit(1)
                .Get();

            return resp.Models.FirstOrDefault();
        }

        public async Task ConsumeAsync(int tokenId, CancellationToken ct = default)
        {
            var client = _supabase.GetClient();
            await client
                .From<UserToken>()
                .Where(x => x.Id == tokenId)
                .Set(x => x.Used, true)
                .Update(); // remove ct: ct
        }
    }
}