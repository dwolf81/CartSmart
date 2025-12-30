using System;
using System.Linq;
using System.Threading.Tasks;
using CartSmart.API.Models;

namespace CartSmart.API.Services
{
    public sealed class UserReputationService : IUserReputationService
    {
        private readonly ISupabaseService _supabase;

        // Tunables
        private const int PenaltyRemovedSubmission = 12;
        private const int PenaltyFalseFlag = 6;
        private const int MaxScore = 100;

        public UserReputationService(ISupabaseService supabase)
        {
            _supabase = supabase;
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;

        private static int ComputeTrustScore(UserReputation r)
        {
            var positive = (r.SubmitCorrect * 5) + (r.FlagTrue * 2);
            var negative = r.PenaltyPoints + (r.ConsecutiveFalseFlags * 2);
            return Clamp(positive - negative, 0, MaxScore);
        }

        public async Task<UserReputation?> GetRawAsync(int userId)
        {
            // MVP: use GetAll + FirstOrDefault (you can add a filtered fetch helper later)
            var rows = await _supabase.GetAllAsync<UserReputation>();
            return rows.FirstOrDefault(r => r.UserId == userId);
        }

        public async Task EnsureRowAsync(int userId)
        {
            var existing = await GetRawAsync(userId);
            if (existing != null) return;

            var row = new UserReputation
            {
                UserId = userId,
                SubmitTotal = 0,
                SubmitCorrect = 0,
                FlagTotal = 0,
                FlagTrue = 0,
                ConsecutiveFalseFlags = 0,
                PenaltyPoints = 0,
                UpdatedAtUtc = DateTime.UtcNow
            };

            await _supabase.InsertAsync(row);
        }

        public async Task RecordSubmissionVerifiedAsync(int userId)
        {
            await EnsureRowAsync(userId);
            var cur = await GetRawAsync(userId) ?? new UserReputation { UserId = userId };

            cur.SubmitTotal += 1;
            cur.SubmitCorrect += 1;
            cur.ConsecutiveFalseFlags = 0;
            cur.UpdatedAtUtc = DateTime.UtcNow;

            await _supabase.UpdateAsync(cur);
        }

        public async Task RecordSubmissionRemovedAsync(int userId)
        {
            await EnsureRowAsync(userId);
            var cur = await GetRawAsync(userId) ?? new UserReputation { UserId = userId };

            cur.SubmitTotal += 1;
            cur.PenaltyPoints += PenaltyRemovedSubmission;
            cur.UpdatedAtUtc = DateTime.UtcNow;

            await _supabase.UpdateAsync(cur);
        }

        public async Task RecordFlagTrueAsync(int userId)
        {
            await EnsureRowAsync(userId);
            var cur = await GetRawAsync(userId) ?? new UserReputation { UserId = userId };

            cur.FlagTotal += 1;
            cur.FlagTrue += 1;
            cur.ConsecutiveFalseFlags = 0;
            cur.UpdatedAtUtc = DateTime.UtcNow;

            await _supabase.UpdateAsync(cur);
        }

        public async Task RecordFlagFalseAsync(int userId)
        {
            await EnsureRowAsync(userId);
            var cur = await GetRawAsync(userId) ?? new UserReputation { UserId = userId };

            cur.FlagTotal += 1;
            cur.ConsecutiveFalseFlags += 1;
            cur.PenaltyPoints += PenaltyFalseFlag;
            cur.UpdatedAtUtc = DateTime.UtcNow;

            await _supabase.UpdateAsync(cur);
        }

        public async Task<int> GetTrustScoreAsync(int userId)
        {
            var row = await GetRawAsync(userId) ?? new UserReputation
            {
                UserId = userId,
                UpdatedAtUtc = DateTime.UtcNow
            };
            return ComputeTrustScore(row);
        }


    }
}