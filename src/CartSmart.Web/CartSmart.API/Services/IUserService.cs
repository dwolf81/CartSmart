using System.Threading.Tasks;
using CartSmart.API.Models;
using CartSmart.API.Models.DTOs;

namespace CartSmart.API.Services
{
    public interface IUserService
    {
        Task<IEnumerable<User>> GetAllUsersAsync();
        Task<User?> GetUserByEmailAsync(string email);
        Task<User> CreateUserAsync(User user);
        Task<User?> UpdateUserAsync(int id, User user);
        Task<bool> DeleteUserAsync(int id);
        Task<User> GetUserByUsernameAsync(string username);
        Task<IEnumerable<User>> GetFollowersAsync(int userId);
        Task<IEnumerable<User>> GetFollowingAsync(int userId);
        Task<bool> FollowUserAsync(int followerId, int followingId);
        Task<bool> UnfollowUserAsync(int followerId, int followingId);
        Task<User> UpdateUserAsync(User user);
        Task<User> GetUserByIdAsync(int id);
        Task<UserDTO?> GetUserDTOByIdAsync(int id);
        Task<UserDTO?> GetUserBySlugAsync(string slug);
        Task<string> UploadUserAvatarAsync(int userId, Stream fileStream, string fileName);
        Task<int> UpdateUserLevelAsync(int userId);
        Task<bool> IsUsernameAvailableAsync(string username, int? excludeUserId = null);
        Task<bool> SoftDeleteUserAsync(int userId);
    }
}