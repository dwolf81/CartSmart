using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace CartSmart.API.Services
{
    public class AppleAuthService : IAppleAuthService
    {
        private readonly IConfiguration _config;
        private static readonly HttpClient Http = new HttpClient();

        public AppleAuthService(IConfiguration config) => _config = config;

        public async Task<AppleUserInfo> ValidateTokenAsync(string idToken, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(idToken))
                throw new SecurityTokenException("Missing id_token");

            var clientId = _config["Authentication:Apple:ClientId"] ?? _config["Apple:ClientId"];
            if (string.IsNullOrWhiteSpace(clientId))
                throw new InvalidOperationException("Apple ClientId not configured.");

            var keys = await Http.GetFromJsonAsync<AppleJwks>("https://appleid.apple.com/auth/keys", cancellationToken: ct)
                       ?? throw new InvalidOperationException("Failed to fetch Apple JWKS.");

            var handler = new JwtSecurityTokenHandler();
            var token = handler.ReadJwtToken(idToken);
            var kid = token.Header.Kid;
            var jwk = keys.Keys.FirstOrDefault(k => k.Kid == kid)
                      ?? throw new SecurityTokenException("Apple key not found.");

            var e = Base64UrlEncoder.DecodeBytes(jwk.E);
            var n = Base64UrlEncoder.DecodeBytes(jwk.N);
            var rsa = new System.Security.Cryptography.RSAParameters { Exponent = e, Modulus = n };
            using var rsaKey = System.Security.Cryptography.RSA.Create();
            rsaKey.ImportParameters(rsa);
            var key = new RsaSecurityKey(rsaKey) { KeyId = jwk.Kid };

            var validation = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = "https://appleid.apple.com",
                ValidateAudience = true,
                ValidAudience = clientId,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(5)
            };

            handler.ValidateToken(idToken, validation, out _);

            var email = token.Claims.FirstOrDefault(c => c.Type == "email")?.Value ?? "";
            var subject = token.Claims.First(c => c.Type == "sub").Value;
            var first = token.Claims.FirstOrDefault(c => c.Type == ClaimTypes.GivenName)?.Value ?? "";
            var last = token.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Surname)?.Value ?? "";

            if (string.IsNullOrWhiteSpace(email))
                throw new SecurityTokenException("Apple token does not include email.");

            return new AppleUserInfo(subject, email, first, last);
        }

        private sealed class AppleJwks
        {
            public List<AppleJwk> Keys { get; set; } = new();
        }

        private sealed class AppleJwk
        {
            public string Kty { get; set; } = "";
            public string Kid { get; set; } = "";
            public string Use { get; set; } = "";
            public string Alg { get; set; } = "";
            public string N { get; set; } = "";
            public string E { get; set; } = "";
        }
    }
}