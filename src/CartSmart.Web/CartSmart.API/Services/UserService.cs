using CartSmart.API.Models;
using CartSmart.API.Models.DTOs;
using Supabase.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats; // Add this line
using CartSmart.API.Services; // ensure namespace visible
using PostgrestOp = Supabase.Postgrest.Constants.Operator;
using Microsoft.Extensions.Caching.Memory; // ADD


namespace CartSmart.API.Services;

public class UserService : IUserService
{
    private readonly ISupabaseService _supabase;
    private readonly IAuthService _authService;
    private readonly IUserReputationService _reputation;
    private readonly IMemoryCache _cache; // ADD

    public UserService(ISupabaseService supabase, IAuthService authService, IUserReputationService reputation, IMemoryCache cache) // ADD cache param
    {
        _supabase = supabase;
        _authService = authService;
        _reputation = reputation;
        _cache = cache; // ASSIGN
    }

    public async Task<IEnumerable<User>> GetAllUsersAsync()
    {
        return await _supabase.GetAllAsync<User>();
    }


    public async Task<User?> GetUserByEmailAsync(string email)
    {
        var users = await _supabase.GetAllAsync<User>();
        return users.FirstOrDefault(u => u.Email == email);
    }

    public async Task<User> GetUserByUsernameAsync(string username)
    {
        var users = await _supabase.GetAllAsync<User>();
        return users.FirstOrDefault(u => u.Username == username)
            ?? throw new Exception("User not found");
    }

    public async Task<User> CreateUserAsync(User user)
    {
        return await _supabase.InsertAsync(user);
    }

    public async Task<User?> UpdateUserAsync(int id, User user)
    {
        if (id != user.Id) return null;
        return await _supabase.UpdateAsync(user);
    }

    public async Task<User> UpdateUserAsync(User updatedUser)
    {
        var currentUserId = _authService.GetCurrentUserId();
        if (string.IsNullOrEmpty(currentUserId) || currentUserId != updatedUser.Id.ToString())
        {
            throw new UnauthorizedAccessException("You can only update your own profile.");
        }

        var currentUser = await GetUserByIdAsync(int.Parse(currentUserId));
        if (currentUser == null)
        {
            throw new Exception("User not found.");
        }

        var changedProperties = new List<string>();

        if (!string.IsNullOrEmpty(updatedUser.Username) && updatedUser.Username != currentUser.Username)
        {
            currentUser.Username = updatedUser.Username;
            changedProperties.Add(nameof(User.Username));
        }
        if (!string.IsNullOrEmpty(updatedUser.Email) && updatedUser.Email != currentUser.Email)
        {
            currentUser.Email = updatedUser.Email;
            changedProperties.Add(nameof(User.Email));
        }
        if (!string.IsNullOrEmpty(updatedUser.FirstName) && updatedUser.FirstName != currentUser.FirstName)
        {
            currentUser.FirstName = updatedUser.FirstName;
            changedProperties.Add(nameof(User.FirstName));
        }
        if (!string.IsNullOrEmpty(updatedUser.LastName) && updatedUser.LastName != currentUser.LastName)
        {
            currentUser.LastName = updatedUser.LastName;
            changedProperties.Add(nameof(User.LastName));
        }
        if (!string.IsNullOrEmpty(updatedUser.Bio) && updatedUser.Bio != currentUser.Bio)
        {
            currentUser.Bio = updatedUser.Bio;
            changedProperties.Add(nameof(User.Bio));
        }
        if (!string.IsNullOrEmpty(updatedUser.ImageUrl) && updatedUser.ImageUrl != currentUser.ImageUrl)
        {
            currentUser.ImageUrl = updatedUser.ImageUrl;
            changedProperties.Add(nameof(User.ImageUrl));
        }
        if (updatedUser.EmailOptIn != currentUser.EmailOptIn)
        {
            currentUser.EmailOptIn = updatedUser.EmailOptIn;
            changedProperties.Add(nameof(User.EmailOptIn));
        }
        if (!string.IsNullOrEmpty(updatedUser.DisplayName) && updatedUser.DisplayName != currentUser.DisplayName)
        {
            currentUser.DisplayName = updatedUser.DisplayName;
            changedProperties.Add(nameof(User.DisplayName));
        }

        // Terms acceptance fields
        if (!string.IsNullOrWhiteSpace(updatedUser.TermsVersion) && updatedUser.TermsVersion != currentUser.TermsVersion)
        {
            currentUser.TermsVersion = updatedUser.TermsVersion.Trim();
            changedProperties.Add(nameof(User.TermsVersion));
        }
        if (updatedUser.TermsAcceptedAtUtc.HasValue && updatedUser.TermsAcceptedAtUtc != currentUser.TermsAcceptedAtUtc)
        {
            currentUser.TermsAcceptedAtUtc = updatedUser.TermsAcceptedAtUtc;
            changedProperties.Add(nameof(User.TermsAcceptedAtUtc));
        }

        if (changedProperties.Any())
        {

            currentUser.Id = int.Parse(currentUserId);
            try
            {
                // Prefer partial update when specific properties are known
                // If your Supabase client supports partial updates, uncomment next line:
                // await _supabase.UpdatePartialAsync(currentUser, changedProperties.ToArray());

                // Fallback to full update to ensure columns persist
                await _supabase.UpdateAsync(currentUser);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to update user profile: {ex.Message}", ex);
            }
        }

        return currentUser;
    }


            //update user level based on trust score
        public async Task<int> UpdateUserLevelAsync(int userId)
        {       
            var currentUser = await GetUserByIdAsync(userId);
            if (currentUser == null)
            {
                throw new Exception("User not found.");
            }

        if (currentUser.Admin)
        {

            //if user is an admin keep it at 100 else compute
            currentUser.Level = (short)100 ;
        }
        else
        {
            var trustScore = await _reputation.GetTrustScoreAsync(userId);

            //if user is an admin keep it at 100 else compute
            currentUser.Level = (short)trustScore;
        }


            await _supabase.UpdateAsync(currentUser);

            return currentUser.Level;
        }

    public async Task<bool> DeleteUserAsync(int id)
    {
        await _supabase.DeleteAsync<User>(id);
        return true;
    }

    public async Task<IEnumerable<User>> GetFollowersAsync(int userId)
    {
        var follows = await _supabase.GetAllAsync<Follow>();
        var users = await _supabase.GetAllAsync<User>();

        return follows
            .Where(f => f.FollowingId == userId)
            .Join(users,
                f => f.FollowerId,
                u => u.Id,
                (f, u) => u);
    }

    public async Task<IEnumerable<User>> GetFollowingAsync(int userId)
    {
        var follows = await _supabase.GetAllAsync<Follow>();
        var users = await _supabase.GetAllAsync<User>();

        return follows
            .Where(f => f.FollowerId == userId)
            .Join(users,
                f => f.FollowingId,
                u => u.Id,
                (f, u) => u);
    }

    public async Task<bool> FollowUserAsync(int followerId, int followingId)
    {
        var follow = new Follow
        {
            FollowerId = followerId,
            FollowingId = followingId,
            CreatedAt = DateTime.UtcNow
        };

        await _supabase.InsertAsync(follow);
        return true;
    }

    public async Task<bool> UnfollowUserAsync(int followerId, int followingId)
    {
        var follows = await _supabase.GetAllAsync<Follow>();
        var follow = follows.FirstOrDefault(f =>
            f.FollowerId == followerId &&
            f.FollowingId == followingId);

        if (follow == null) return false;

        await _supabase.DeleteAsync<Follow>(followingId);
        return true;
    }

    public async Task<UserDTO?> GetUserDTOByIdAsync(int id)
    {
        var users = await _supabase.GetAllAsync<User>();
        var user = users.FirstOrDefault(p => p.Id == id);
        if (user == null) return null;

        return new UserDTO
        {
            Id = user.Id,
            UserName = user.Username,
            FirstName = user.FirstName,
            LastName = user.LastName,
            Level = user.Level,
            Bio = user.Bio,
            ImageUrl = user.ImageUrl,
            EmailOptIn = user.EmailOptIn,
            DealsPosted = user.DealsPosted,
            DisplayName = user.DisplayName,
            AllowReview = user.AllowReview,
            Email = user.Email,
            Admin = user.Admin
        };
    }

    public async Task<User?> GetUserByIdAsync(int id)
    {
        var client = _supabase.GetClient();
        var resp = await client
            .From<User>()
            .Where(u => u.Id == id && u.Deleted == false)
            .Limit(1)
            .Get();
        return resp.Models.FirstOrDefault();
    }

        public async Task<UserDTO?> GetUserBySlugAsync(string? slug)
    {
        var users = await _supabase.GetAllAsync<User>();
        var user = users.FirstOrDefault(p => p.Username.ToLower() == slug.ToLower());

        return user == null ? null : new UserDTO
        {
            Id = user.Id,
            UserName = user.Username,
            FirstName = user.FirstName,
            LastName = user.LastName,
            Level = user.Level,
            Bio = user.Bio,
            ImageUrl = user.ImageUrl,
            EmailOptIn = user.EmailOptIn,
            DealsPosted = user.DealsPosted,
            DisplayName = user.DisplayName,
            AllowReview = user.AllowReview,
            Admin = user.Admin
        };
    }

    public async Task<string> UploadUserAvatarAsync(int userId, Stream fileStream, string fileName)
    {
        var currentUserId = _authService.GetCurrentUserId();
        if (string.IsNullOrEmpty(currentUserId) || currentUserId != userId.ToString())
        {
            throw new UnauthorizedAccessException("You can only update your own profile image.");
        }

        var fileExt = Path.GetExtension(fileName);
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var newFileName = $"{Guid.NewGuid()}";

        // File paths
        var baseFilePath = $"{userId}/{newFileName}";
        var originalFilePath = $"{baseFilePath}{fileExt}";
        var webp100FilePath = $"{baseFilePath}_100x100.webp";
        var webp32FilePath = $"{baseFilePath}_32x32.webp";

        try
        {
            // Convert stream to byte array for reuse
            byte[] fileBytes;
            using (var ms = new MemoryStream())
            {
                await fileStream.CopyToAsync(ms);
                fileBytes = ms.ToArray();
            }

            // Upload original file
            using (var originalStream = new MemoryStream(fileBytes))
            {
                await _supabase.UploadFileWithServiceRoleAsync(
                    "avatars",
                    originalFilePath,
                    originalStream,
                    new Supabase.Storage.FileOptions
                    {
                        CacheControl = "3600",
                        Upsert = true,
                        ContentType = GetContentType(fileExt)
                    }
                );
            }

            // Create and upload 100x100 WebP version
            var webp100Bytes = await ResizeImageToWebP(fileBytes, 100, 100);
            using (var webp100Stream = new MemoryStream(webp100Bytes))
            {
                await _supabase.UploadFileWithServiceRoleAsync(
                    "avatars",
                    webp100FilePath,
                    webp100Stream,
                    new Supabase.Storage.FileOptions
                    {
                        CacheControl = "3600",
                        Upsert = true,
                        ContentType = "image/webp"
                    }
                );
            }

            // Create and upload 32x32 WebP version
            var webp32Bytes = await ResizeImageToWebP(fileBytes, 32, 32);
            using (var webp32Stream = new MemoryStream(webp32Bytes))
            {
                await _supabase.UploadFileWithServiceRoleAsync(
                    "avatars",
                    webp32FilePath,
                    webp32Stream,
                    new Supabase.Storage.FileOptions
                    {
                        CacheControl = "3600",
                        Upsert = true,
                        ContentType = "image/webp"
                    }
                );
            }

            // Return the original file's public URL
            var publicUrl = _supabase.GetPublicUrl("avatars", webp100FilePath);
            return publicUrl;
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to upload avatar", ex);
        }
    }

    private async Task<byte[]> ResizeImageToWebP(byte[] imageBytes, int width, int height)
    {
        try
        {
            using var image = SixLabors.ImageSharp.Image.Load(imageBytes);

            // Resize the image maintaining aspect ratio, but fitting within the specified dimensions
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(width, height),
                Mode = ResizeMode.Crop, // Crop to fit exact dimensions
                Position = AnchorPositionMode.Center
            }));

            // Convert to WebP
            using var output = new MemoryStream();
            await image.SaveAsWebpAsync(output, new WebpEncoder
            {
                Quality = 85 // Adjust quality as needed (0-100)
            });

            return output.ToArray();
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to resize image to WebP: {ex.Message}", ex);
        }
    }

    private string GetContentType(string fileExtension)
    {
        return fileExtension.ToLower() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
    }

        public async Task<bool> IsUsernameAvailableAsync(string username, int? excludeUserId = null)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;

        var client = _supabase.GetClient();
        var uname = username.Trim();

        var query = client
            .From<User>()
            .Select("id")
            .Filter("user_name", PostgrestOp.ILike, uname)          // case-insensitive exact match (no wildcards)
            .Filter("deleted", PostgrestOp.Equals, "false");

        if (excludeUserId.HasValue)
            query = query.Filter("id", PostgrestOp.NotEqual, excludeUserId.Value);

        var resp = await query.Limit(1).Get();
        return !resp.Models.Any();
    }

    public async Task<bool> SoftDeleteUserAsync(int userId) // NEW
    {
        var client = _supabase.GetClient();

        // Load user
        var userResp = await client
            .From<User>()
            .Where(u => u.Id == userId && u.Deleted == false)
            .Limit(1)
            .Get();
        var user = userResp.Models.FirstOrDefault();
        if (user == null) return false;

        // Mark user deleted/inactive
        user.Deleted = true;
        user.Active = false;
        user.TokenVersion += 1; // invalidate existing tokens
        await _supabase.UpdateAsync(user);

        // Soft delete all deals by this user (assumes Deal model with UserId + Deleted bool)
        try
        {
            var dealsResp = await client
                .From<Deal>()                 // Ensure a Deal model exists; rename if different.
                .Filter("user_id", PostgrestOp.Equals, userId)
                .Filter("deleted", PostgrestOp.Equals, "false")
                .Get();

            foreach (var d in dealsResp.Models)
            {
                d.Deleted = true;
                await _supabase.UpdateAsync(d);
            }
        }
        catch
        {
            // swallow; user deletion still proceeds
        }

        _cache.Remove($"authstate:{user.Id}"); 

        return true;
    }
}