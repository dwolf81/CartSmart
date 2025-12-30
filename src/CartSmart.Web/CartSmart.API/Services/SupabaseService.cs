using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Supabase;
using Supabase.Postgrest.Models;
using Supabase.Interfaces;
using Supabase.Postgrest.Attributes;
using System.Reflection;

namespace CartSmart.API.Services;
public class SupabaseService : ISupabaseService
{
    private readonly Client _supabaseClient;
    private readonly string _serviceRoleKey;
    private readonly string _url;

    public SupabaseService(IConfiguration configuration)
    {
        _url = configuration["Supabase:Url"];
        var apiKey = configuration["Supabase:ApiKey"];
        _serviceRoleKey = configuration["Supabase:ServiceRole"];

        var options = new SupabaseOptions
        {
            AutoConnectRealtime = false, // Disable real-time updates
        };

        _supabaseClient = new Client(_url, apiKey, options);
    }

    public Client GetClient() => _supabaseClient;

    // Fetch all rows from a table (Generic method)
    public async Task<List<T>> GetAllAsync<T>() where T : BaseModel, new()
    {
        var result = await _supabaseClient.From<T>().Get();
        return result.Models;
    }

    // Insert a new row into a table
    public async Task<T> InsertAsync<T>(T model) where T : BaseModel, new()
    {
        var response = await _supabaseClient.From<T>().Insert(model);
        return response.Models.FirstOrDefault();
    }

    // Update an existing row
    public async Task<T> UpdateAsync<T>(T model) where T : BaseModel, new()
    {
        var response = await _supabaseClient.From<T>().Update(model);
        return response.Models.FirstOrDefault();
    }
    
    // Delete a row from a table
    public async Task DeleteAsync<T>(int id) where T : BaseModel, new()
    {
        await _supabaseClient.From<T>().Filter("id", Supabase.Postgrest.Constants.Operator.Equals, id).Delete();
    }

    // ✅ Expose Supabase queries via this method
    public async Task<ISupabaseTable<T, Supabase.Realtime.RealtimeChannel>> QueryTable<T>() where T : BaseModel, new()
    {
        return _supabaseClient.From<T>();
    }

    public async Task<string> UploadFileWithServiceRoleAsync(string bucket, string path, Stream fileStream, Supabase.Storage.FileOptions options)
    {
        byte[] fileBytes;
        using (var ms = new MemoryStream())
        {
            await fileStream.CopyToAsync(ms);
            fileBytes = ms.ToArray();
        }

        // Create a temporary service-role client only when needed
        var tempServiceClient = new Supabase.Client(
            _url,
            _serviceRoleKey,
            new Supabase.SupabaseOptions
            {
                AutoRefreshToken = true,
                AutoConnectRealtime = false
            }
        );

        await tempServiceClient.Storage
            .From(bucket)
            .Upload(fileBytes, path, options);

        return GetPublicUrl(bucket, path);
    }

    public string GetPublicUrl(string bucket, string path)
    {
        return _supabaseClient.Storage
            .From(bucket)
            .GetPublicUrl(path);
    }

    // Helper: prefer [Column("db_name")] attribute; else convert PascalCase -> snake_case
    private static string ResolveColumnName(PropertyInfo pi)
    {
        var colAttr = pi.GetCustomAttribute<ColumnAttribute>();
        if (colAttr != null && !string.IsNullOrWhiteSpace(colAttr.ColumnName))
            return colAttr.ColumnName;

        // Fallback: PascalCase → snake_case
        var name = pi.Name;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    public async Task<T?> UpsertAsync<T>(T model, string? onConflict = null, bool ignoreDuplicates = false)
        where T : BaseModel, new()
    {
        // Simple upsert - relies on table's PRIMARY KEY or UNIQUE constraints
        var resp = await _supabaseClient.From<T>().Upsert(model);
        return resp.Models.FirstOrDefault();
    }

    public async Task<T?> SingleAsync<T>(string column, object value)
        where T : BaseModel, new()
    {
        try
        {
            return await _supabaseClient
                .From<T>()
                .Filter(column, Supabase.Postgrest.Constants.Operator.Equals, value)
                .Single();
        }
        catch
        {
            return default;
        }
    }
}