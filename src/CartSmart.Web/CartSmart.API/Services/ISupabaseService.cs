using Supabase;
using Supabase.Interfaces;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Services
{
    public interface ISupabaseService
    {
        Task<List<T>> GetAllAsync<T>() where T : BaseModel, new();
        Task<T> InsertAsync<T>(T model) where T : BaseModel, new();
        Task<T> UpdateAsync<T>(T model) where T : BaseModel, new();
        Task DeleteAsync<T>(int id) where T : BaseModel, new();
        Supabase.Client GetClient();
        Supabase.Client GetServiceRoleClient();
        Task<ISupabaseTable<T, Supabase.Realtime.RealtimeChannel>> QueryTable<T>() where T : BaseModel, new();
        Task<string> UploadFileWithServiceRoleAsync(string bucket, string path, Stream fileStream, Supabase.Storage.FileOptions options);
        string GetPublicUrl(string bucket, string path);

        // Wrapper helpers
        Task<T?> UpsertAsync<T>(T model, string? onConflict = null, bool ignoreDuplicates = false)
            where T : BaseModel, new();

        Task<T?> SingleAsync<T>(string column, object value)
            where T : BaseModel, new();
    }
}