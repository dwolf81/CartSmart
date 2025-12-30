using System.Threading.Tasks;
using CartSmart.API.Models;

namespace CartSmart.API.Services
{
    public interface IUserReputationService
    {
        Task EnsureRowAsync(int userId);
        Task RecordSubmissionVerifiedAsync(int userId);
        Task RecordSubmissionRemovedAsync(int userId);
        Task RecordFlagTrueAsync(int userId);
        Task RecordFlagFalseAsync(int userId);
        Task<int> GetTrustScoreAsync(int userId);
        Task<UserReputation?> GetRawAsync(int userId);
    }
}