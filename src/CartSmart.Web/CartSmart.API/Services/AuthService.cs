using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CartSmart.API.Models;
using CartSmart.API.Models.DTOs;
using CartSmart.API.Security;
using CartSmart.API.Models;
using Microsoft.Extensions.Caching.Memory;

namespace CartSmart.API.Services
{
    public class AuthService : IAuthService
    {
        private readonly ISupabaseService _supabase;
        private readonly IHttpContextAccessor _http;
        private readonly IConfiguration _configuration;
        private readonly IGoogleAuthService _googleAuthService;
        private readonly IAppleAuthService _appleAuthService; // REPLACED Facebook
        private readonly IMemoryCache _cache;            // NEW

        private const int MaxFailedAttempts = 5;
        private static readonly TimeSpan BaseLockoutDuration = TimeSpan.FromMinutes(15);
        

        public AuthService(
            ISupabaseService supabase,
            IHttpContextAccessor http,
            IConfiguration configuration,
            IGoogleAuthService googleAuthService,
            IAppleAuthService appleAuthService,
            IMemoryCache cache)        // NEW
        {
            _supabase = supabase;
            _http = http;
            _configuration = configuration;
            _googleAuthService = googleAuthService;
            _appleAuthService = appleAuthService;
            _cache = cache;            // NEW
        }

        public async Task<AuthResponse> RegisterAsync(RegisterRequest request)
        {
            try
            {
                var client = _supabase.GetClient();

                // Normalize email
                var emailNorm = request.Email?.Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(emailNorm))
                    return new AuthResponse { Success = false, Message = "Email is required." };

                var canonical = CanonicalizeEmail(emailNorm);

                // Duplicate check by canonical email (non-deleted)
                var existing = await client
                    .From<User>()
                    .Where(u => u.CanonicalEmail == canonical && u.Deleted == false)
                    .Limit(1)
                    .Get();

                if (existing.Models.Any())
                {
                    return new AuthResponse
                    {
                        Success = false,
                        Message = "An account for this inbox already exists."
                    };
                }

                // Validate password against policy BEFORE hashing
                if (!PasswordPolicy.TryValidate(request.Password, request.Email, request.FirstName, request.LastName, out var policyError))
                {
                    return new AuthResponse { Success = false, Message = policyError };
                }

                var (hashedPassword, salt) = await PasswordHasher.HashPasswordAsync(request.Password);

                // Create user profile in Supabase
                var userInsert = new UserInsert
                {
                    Email = emailNorm,
                    Username = "User_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    DisplayName = request.FirstName + " " + request.LastName,
                    FirstName = request.FirstName,
                    LastName = request.LastName,
                    CreatedAt = DateTime.UtcNow,
                    Level = 1,
                    Password = hashedPassword,
                    Salt = salt,
                    Active = false, // manual signup requires activation
                    EmailOptIn = request.EmailOptIn,
                    CanonicalEmail = canonical
                };

                await client.From<UserInsert>().Insert(userInsert);

                var user = new User
                {
                    Id = userInsert.Id,
                    Email = userInsert.Email,
                    Level = userInsert.Level,
                    TokenVersion = 1
                    
                };

                var token = GenerateJwtToken(user);

                return new AuthResponse
                {
                    Success = true,
                    Token = token,
                    User = new UserDTO
                    {
                        Id = userInsert.Id,
                        Email = request.Email,
                        UserName = request.Username,
                        FirstName = request.FirstName,
                        LastName = request.LastName,
                        DisplayName = request.FirstName + " " + request.LastName
                    }
                };
            }
            catch (Exception ex)
            {
                return new AuthResponse
                {
                    Success = false,
                    Message = $"Registration failed: {ex.Message}"
                };
            }
        }

        public async Task<AuthResponse> LoginAsync(LoginRequest request)
        {
            try
            {
                var client = _supabase.GetClient();

                var user = await client.From<User>()
                    .Where(u => u.Email == request.EmailAddress && u.Deleted == false)
                    .Single();

                if (user == null)
                    return new AuthResponse { Success = false, Message = "Invalid email or password" };

                if (user != null && user.LockoutUntilUtc.HasValue)
                {
                    // If Kind is Unspecified assume it was materialized in local time; convert to UTC.
                    var raw = user.LockoutUntilUtc.Value;
                    if (raw.Kind == DateTimeKind.Unspecified)
                    {
                        // Treat as local then convert to UTC
                        var utc = DateTime.SpecifyKind(raw, DateTimeKind.Local).ToUniversalTime();
                        user.LockoutUntilUtc = utc;
                    }
                    else if (raw.Kind == DateTimeKind.Local)
                    {
                        user.LockoutUntilUtc = raw.ToUniversalTime();
                    }
                    // If already Utc, leave as is.
                }

                // Already locked?
                if (user.LockoutUntilUtc.HasValue && user.LockoutUntilUtc.Value > DateTime.UtcNow)
                {
                    var mins = Math.Ceiling((user.LockoutUntilUtc.Value - DateTime.UtcNow).TotalMinutes);
                    return new AuthResponse
                    {
                        Success = false,
                        Message = $"Account locked. Try again in {mins} minute(s).",
                        LockedOut = true
                    };
                }

                // Lockout expired? Clear fields before verifying
                if (user.LockoutUntilUtc.HasValue && user.LockoutUntilUtc.Value <= DateTime.UtcNow)
                {
                    user.LockoutUntilUtc = null;
                    user.FailedLoginCount = 0;
                    await client.From<User>()
                        .Where(u => u.Id == user.Id)
                        .Set(u => u.LockoutUntilUtc, (DateTime?)null)
                        .Set(u => u.FailedLoginCount, 0)
                        .Update();
                }

                var pwdOk = await PasswordHasher.VerifyPasswordAsync(request.Password, user.Password, user.Salt);

                if (!pwdOk)
                {
                    // Increment and persist
                    user.FailedLoginCount += 1;

                    // Reached threshold? Set lockout; DO NOT reset count to 0 here
                    if (user.FailedLoginCount >= MaxFailedAttempts)
                    {
                        // GetLockoutDuration returns TimeSpan
                        var lockSpan = GetLockoutDuration(user.FailedLoginCount);
                        user.LockoutUntilUtc = DateTime.UtcNow.Add(lockSpan); // correct usage                        
                    }

                    var update = client.From<User>().Where(u => u.Id == user.Id)
                        .Set(u => u.FailedLoginCount, user.FailedLoginCount);

                    if (user.LockoutUntilUtc != null)
                        update = update.Set(u => u.LockoutUntilUtc, user.LockoutUntilUtc);

                    await update.Update();

                    if (user.LockoutUntilUtc != null)
                    {
                        var mins = Math.Ceiling((user.LockoutUntilUtc.Value - DateTime.UtcNow).TotalMinutes);
                        return new AuthResponse
                        {
                            Success = false,
                            Message = $"Account locked. Try again in {mins} minute(s).",
                            LockedOut = true
                        };
                    }

                    return new AuthResponse { Success = false, Message = "Invalid email or password" };
                }

                // Success: clear counters if needed
                if (user.FailedLoginCount != 0 || user.LockoutUntilUtc != null)
                {
                    await client.From<User>()
                        .Where(u => u.Id == user.Id)
                        .Set(u => u.FailedLoginCount, 0)
                        .Set(u => u.LockoutUntilUtc, (DateTime?)null)
                        .Update();
                }

                if (!user.Active)
                {
                    return new AuthResponse
                    {
                        Success = false,
                        Message = "Account not activated. Please check your email.",
                        ActivationRequired = true
                    };
                }

                var token = GenerateJwtToken(user);

                var isHttps = _http.HttpContext?.Request.IsHttps == true;
                _http.HttpContext?.Response.Cookies.Append("access_token", token, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = isHttps,                 // MUST be true if SameSite=None
                    SameSite = isHttps ? SameSiteMode.None : SameSiteMode.Lax,
                    Path = "/",
                    Expires = DateTimeOffset.UtcNow.AddDays(30)
                });

                return new AuthResponse
                {
                    Success = true,
                    Token = token,
                    User = new UserDTO
                    {
                        Id = user.Id,
                        Email = user.Email,
                        UserName = user.Username,
                        FirstName = user.FirstName,
                        LastName = user.LastName,
                        ImageUrl = user.ImageUrl,
                        Bio = user.Bio,
                        Level = user.Level,
                        EmailOptIn = user.EmailOptIn,
                        DisplayName = user.DisplayName,
                        AllowReview = user.AllowReview
                    }
                };
            }
            catch (Exception ex)
            {
                return new AuthResponse
                {
                    Success = false,
                    Message = $"Login failed: {ex.Message}"
                };
            }
        }


        public async Task<bool> IsUserAuthenticatedAsync()
        {
            string? token = null;

            // cookie first
            if (_http.HttpContext?.Request.Cookies.TryGetValue("access_token", out var cookieToken) == true)
                token = cookieToken;

            // then Authorization header
            if (string.IsNullOrEmpty(token))
            {
                var authHeader = _http.HttpContext?.Request.Headers["Authorization"].FirstOrDefault();
                if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer "))
                    token = authHeader.Substring("Bearer ".Length).Trim();
            }

            if (string.IsNullOrEmpty(token)) return false;

            return await ValidateTokenAsync(token);
        }

        // Make session methods no-ops to avoid exceptions
        public void SetUserSession(string token)
        {
            // no-op: session not used
        }

        public void ClearUserSession()
        {
            // no-op: session not used
        }

        public async Task<AuthResponse> SocialLoginAsync(SocialLoginRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.Provider) || string.IsNullOrWhiteSpace(request.Token))
                    return new AuthResponse { Success = false, Message = "Invalid social login request." };

                // 1) Validate social token and extract profile
                string provider = request.Provider.Trim().ToLowerInvariant();
                string email, firstName, lastName, subject;

                if (provider == "google")
                {
                    var googleUser = await _googleAuthService.ValidateTokenAsync(request.Token);
                    email = googleUser.Email;
                    firstName = googleUser.FirstName ?? "";
                    lastName = googleUser.LastName ?? "";
                }
                else if (provider == "apple")
                {
                    var appleUser = await _appleAuthService.ValidateTokenAsync(request.Token);
                    email = appleUser.Email;
                    firstName = appleUser.FirstName ?? "";
                    lastName = appleUser.LastName ?? "";
                }
                else
                {
                    return new AuthResponse { Success = false, Message = "Unsupported provider." };
                }

                var emailNorm = email?.Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(emailNorm))
                    return new AuthResponse { Success = false, Message = "Provider did not supply an email." };

                var client = _supabase.GetClient();

                // 2) Find existing user by email (non-deleted)
                var canonical = CanonicalizeEmail(emailNorm);
                var existingResp = await client
                    .From<User>()
                    .Where(u => u.CanonicalEmail == canonical && u.Deleted == false)
                    .Limit(1)
                    .Get();

                var user = existingResp.Models.FirstOrDefault();

                if (user == null)
                {
                    // 3) Create new SSO user (active + email confirmed)
                    var randomPwd = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
                    var (hash, salt) = await PasswordHasher.HashPasswordAsync(randomPwd);

                    var userInsert = new UserInsert
                    {
                        Email = emailNorm,
                        Username = "User_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                        DisplayName = string.Join(" ", new[] { firstName, lastName }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim(),
                        FirstName = firstName,
                        LastName = lastName,
                        CreatedAt = DateTime.UtcNow,
                        Level = 1,
                        Password = hash,
                        Salt = salt,
                        Active = true, // SSO signup doesn't require activation
                        EmailOptIn = request.OptedIntoEmails ?? false,
                        EmailConfirmed = true,
                        SsoProvider = provider,
                        CanonicalEmail = canonical,
                    };

                    await client.From<UserInsert>().Insert(userInsert);

                    // Re-fetch created user
                    var createdResp = await client
                        .From<User>()
                        .Where(u => u.Email == emailNorm && u.Deleted == false)
                        .Limit(1)
                        .Get();
                    user = createdResp.Models.FirstOrDefault();

                    if (user == null)
                        return new AuthResponse { Success = false, Message = "Failed to create user." };
                }
                else
                {
                    // 4) Ensure SSO users are active & confirmed
                    if (!user.Active || !user.EmailConfirmed || string.IsNullOrEmpty(user.SsoProvider) || string.IsNullOrEmpty(user.SsoSubject))
                    {
                        await client
                            .From<User>()
                            .Where(u => u.Id == user.Id)
                            .Set(u => u.Active, true)
                            .Set(u => u.EmailConfirmed, true)
                            .Set(u => u.SsoProvider, provider)
                            .Update();

                        user.Active = true;
                        user.EmailConfirmed = true;
                        user.SsoProvider = provider;
                    }
                }

                // 5) Issue session (JWT cookie) + response
                var token = GenerateJwtToken(user);
                var isHttps = _http.HttpContext?.Request.IsHttps == true;
                _http.HttpContext?.Response.Cookies.Append("access_token", token, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = isHttps,                 // MUST be true if SameSite=None
                    SameSite = isHttps ? SameSiteMode.None : SameSiteMode.Lax,
                    Path = "/",
                    Expires = DateTimeOffset.UtcNow.AddDays(30)
                });

                return new AuthResponse
                {
                    Success = true,
                    Token = token,
                    User = new UserDTO
                    {
                        Id = user.Id,
                        Email = user.Email,
                        UserName = user.Username,
                        FirstName = user.FirstName,
                        LastName = user.LastName,
                        ImageUrl = user.ImageUrl,
                        Bio = user.Bio,
                        Level = user.Level,
                        EmailOptIn = user.EmailOptIn,
                        DisplayName = user.DisplayName,
                        AllowReview = user.AllowReview
                    }
                };
            }
            catch (Exception ex)
            {
                return new AuthResponse { Success = false, Message = $"Social login failed: {ex.Message}" };
            }
        }

        private string GenerateJwtToken(User user)
        {
            var handler = new JwtSecurityTokenHandler();
            var key = Encoding.ASCII.GetBytes(_configuration["Jwt:Secret"] ?? throw new InvalidOperationException("JWT Secret not configured"));
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),              // fallback for older parsing
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim("level", user.Level.ToString()),
                new Claim("tv", (user.TokenVersion <= 0 ? 1 : user.TokenVersion).ToString())
            };
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddDays(7),
                SigningCredentials = new SigningCredentials(
                    new SymmetricSecurityKey(key),
                    SecurityAlgorithms.HmacSha256Signature)
            };
            var token = handler.CreateToken(tokenDescriptor);
            return handler.WriteToken(token);
        }

        // Legacy overload: delegate to unified method (avoid producing incompatible tokens).
        private string GenerateJwtToken(string userId, string email)
        {
            // Minimal User stub for compatibility
            var user = new User { Id = int.Parse(userId), Email = email, Level = 1, TokenVersion = 1 };
            return GenerateJwtToken(user);
        }

        private string GenerateReferralCode()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper();
        }

        private UserDTO MapToUserDTO(User user)
        {
            return new UserDTO
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email,
                Level = user.Level
            };
        }

        public async Task<bool> ValidateTokenAsync(string token)
        {
            if (string.IsNullOrEmpty(token))
                return false;

            try
            {
                var tokenHandler = new JwtSecurityTokenHandler();
                var key = Encoding.ASCII.GetBytes(_configuration["Jwt:Secret"]);
                tokenHandler.ValidateToken(token, new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ClockSkew = TimeSpan.Zero
                }, out SecurityToken validatedToken);

                var jwt = (JwtSecurityToken)validatedToken;

                var userIdClaim = jwt.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value
                    ?? jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value;

                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out var uid))
                    return false;

                var tokenVersionClaim = jwt.Claims.FirstOrDefault(c => c.Type == "tv")?.Value;
                var tokenVersion = 1;
                if (!string.IsNullOrEmpty(tokenVersionClaim) && !int.TryParse(tokenVersionClaim, out tokenVersion))
                    return false;

                // Cache key
                var cacheKey = $"authstate:{uid}";
                if (_cache.TryGetValue(cacheKey, out (int Version, bool Active, bool Deleted) state))
                {
                    if (state.Deleted || !state.Active) return false;
                    if (state.Version != tokenVersion) return false;
                    return true;
                }

                // Not cached or expired: query minimal user row
                var client = _supabase.GetClient();
                var resp = await client
                    .From<User>()
                    .Select("id,active,deleted,token_version") // reduce payload
                    .Where(u => u.Id == uid)
                    .Limit(1)
                    .Get();

                var user = resp.Models.FirstOrDefault();
                if (user == null || user.Deleted || !user.Active) return false;

                var dbVersion = user.TokenVersion <= 0 ? 1 : user.TokenVersion;
                if (dbVersion != tokenVersion) return false;

                // Store in cache (e.g. 90s sliding)
                _cache.Set(cacheKey,
                    (dbVersion, user.Active, user.Deleted),
                    new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(90) });

                return true;
            }
            catch
            {
                return false;
            }
        }
        public async Task SetActiveAsync(int userId)
        {
            var client = _supabase.GetClient();
            await client
                .From<User>()
                .Where(u => u.Id == userId)
                .Set(u => u.Active, true)
                .Set(u => u.EmailConfirmed, true)
                .Update();
        }

        public string? GetCurrentUserId()
        {
            string? token = null;
            if (_http.HttpContext?.Request.Cookies.TryGetValue("access_token", out var cookieToken) == true)
                token = cookieToken;
            if (string.IsNullOrEmpty(token))
            {
                var authHeader = _http.HttpContext?.Request.Headers["Authorization"].FirstOrDefault();
                if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer "))
                    token = authHeader.Substring("Bearer ".Length).Trim();
            }
            if (string.IsNullOrEmpty(token)) return null;

            var handler = new JwtSecurityTokenHandler();
            var jwtToken = handler.ReadJwtToken(token);

            return jwtToken.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value
                   ?? jwtToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value;
        }

        public async Task<AuthUserDTO?> GetCurrentUser()
        {
            var idStr = GetCurrentUserId();
            if (string.IsNullOrEmpty(idStr)) return null;
            if (!int.TryParse(idStr, out var id)) return null;

            var client = _supabase.GetClient();
            var resp = await client
                .From<User>()
                .Where(u => u.Id == id && u.Deleted == false)
                .Limit(1)
                .Get();

            var u = resp.Models.FirstOrDefault();
            return u == null ? null : MapToAuthUserDTO(u);
        }

        public async Task<AuthUserDTO?> FindByEmailAsync(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return null;
            var client = _supabase.GetClient();
            var resp = await client
                .From<User>()
                .Where(u => u.Email == email && u.Deleted == false)
                .Limit(1)
                .Get();

            var u = resp.Models.FirstOrDefault();
            return u == null ? null : MapToAuthUserDTO(u);
        }

        public async Task SetEmailConfirmedAsync(int userId)
        {
            var client = _supabase.GetClient();
            await client
                .From<User>()
                .Where(u => u.Id == userId)
                .Set(u => u.EmailConfirmed, true)
                .Update();
        }

        public async Task<bool> UpdatePasswordAsync(int userId, string newPassword)
        {
            var user = await FindByIdAsync(userId);
            if (user == null) return false;

            if (!PasswordPolicy.TryValidate(newPassword, user.Email, user.FirstName, user.LastName, out _))
                return false;

            var (hash, salt) = await PasswordHasher.HashPasswordAsync(newPassword);

            var client = _supabase.GetClient();
            await client
                .From<User>()
                .Where(u => u.Id == userId)
                .Set(u => u.Password, hash)
                .Set(u => u.Salt, salt)
                .Set(u => u.PasswordLastChangedUtc, DateTime.UtcNow)
                .Update();

            return true;
        }

        public async Task<bool> VerifyPassword(string userId, string password)
        {
            if (!int.TryParse(userId, out var id)) return false;
            var user = await FindByIdAsync(id);
            if (user == null || string.IsNullOrEmpty(user.Password) || string.IsNullOrEmpty(user.Salt))
                return false;

            return await PasswordHasher.VerifyPasswordAsync(password, user.Password, user.Salt);
        }

        private static AuthUserDTO MapToAuthUserDTO(User u) => new()
        {
            Id = u.Id,
            Email = u.Email ?? "",
            DisplayName = u.DisplayName,
            EmailConfirmed = u.EmailConfirmed,
            SsoProvider = u.SsoProvider,
            HasPassword = !string.IsNullOrEmpty(u.Password)  // Was u.PasswordHash
        };

        public async Task<User?> FindByIdAsync(int userId)
        {
            var client = _supabase.GetClient();
            var resp = await client
                .From<User>()
                .Where(u => u.Id == userId && u.Deleted == false)
                .Limit(1)
                .Get();
            return resp.Models.FirstOrDefault();
        }

        // Optional exponential lockout calc
        private TimeSpan GetLockoutDuration(int failedCount) =>
            TimeSpan.FromMinutes(Math.Min(60, BaseLockoutDuration.TotalMinutes * Math.Pow(2, Math.Max(0, (failedCount / MaxFailedAttempts) - 1))));

        public async Task<bool> ResetLockoutAsync(int userId)
        {
            var client = _supabase.GetClient();
            await client
                .From<User>()
                .Where(u => u.Id == userId)
                .Set(u => u.FailedLoginCount, 0)
                .Set(u => u.LockoutUntilUtc, (DateTime?)null)
                .Update();
            return true;
        }

        // Add helper inside AuthService (near other private methods)
        private string CanonicalizeEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return "";
            email = email.Trim().ToLowerInvariant();
            var at = email.IndexOf('@');
            if (at < 1) return email;
            var local = email.Substring(0, at);
            var domain = email.Substring(at + 1);

            // Gmail rules: ignore dots, drop +tag
            if (domain == "gmail.com" || domain == "googlemail.com")
            {
                var plus = local.IndexOf('+');
                if (plus >= 0) local = local.Substring(0, plus);
                local = local.Replace(".", "");
            }
            else
            {
                // Generic: drop +tag only
                var plus = local.IndexOf('+');
                if (plus >= 0) local = local.Substring(0, plus);
            }
            return $"{local}@{domain}";
        }
    }



}