using CartSmart.API.Models.DTOs;

namespace CartSmart.API.Services
{
    public interface IStoreDealsService
    {
        Task<List<DealDisplayDTO>> GetStoreDealsAsync(long storeId);    
        Task<List<CategoryProductCardDTO>> GetStoreProductDealsAsync(long storeId, long? productTypeId = null);
    }
}
