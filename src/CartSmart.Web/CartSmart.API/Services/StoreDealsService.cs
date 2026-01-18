using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CartSmart.API.Models.DTOs;
using CartSmart.API.Models;

namespace CartSmart.API.Services
{
    public class StoreDealsService : IStoreDealsService
    {
        private readonly ISupabaseService _supabase;

        public StoreDealsService(ISupabaseService supabase)
        {
            _supabase = supabase;
        }

        public async Task<List<DealDisplayDTO>> GetStoreDealsAsync(long storeId)
        {
            var client = _supabase.GetServiceRoleClient();

            // Supabase.Postgrest's Filter() supports `int` but not `long`.
            // Our store ids are within int range, so cast for filtering.
            var storeIdInt = checked((int)storeId);

            var results = await client
                .Rpc<List<DealDisplayDTO>>("f_store_deals", new { p_store_id = storeId });
            return results ?? new List<DealDisplayDTO>();
        }

        public async Task<List<CategoryProductCardDTO>> GetStoreProductDealsAsync(long storeId)
        {
            var client = _supabase.GetServiceRoleClient();

            var results = await client
                .Rpc<List<CategoryProductCardDTO>>("f_best_deals", new { p_store_id = storeId });

            return results ?? new List<CategoryProductCardDTO>();
        }

        public async Task<List<CategoryProductCardDTO>> GetStoreProductDealsAsync(long storeId, long? productTypeId = null)
        {
            var client = _supabase.GetServiceRoleClient();

            object args = productTypeId.HasValue
                ? new { p_store_id = storeId, p_product_type_id = productTypeId.Value }
                : new { p_store_id = storeId };

            var results = await client
                .Rpc<List<CategoryProductCardDTO>>("f_best_deals", args);

            return results ?? new List<CategoryProductCardDTO>();
        }
    }
}
