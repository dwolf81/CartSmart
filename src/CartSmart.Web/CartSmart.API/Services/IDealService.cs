using CartSmart.API.Models;
using CartSmart.API.Models.DTOs;

namespace CartSmart.API.Services
{
    public interface IDealService
    {
        Task<IEnumerable<DealNav>> GetAllDealsAsync();
        Task<DealProductDTO?> GetDealProductByIdAsync(int id);
        Task<PagedDealsResultDTO<DealDisplayDTO>> GetDealsByUserAsync(int userId,int page, int pageSize);
        Task<PagedDealsResultDTO<DealDisplayDTO>> GetDealsByProductAsync(int productId, int? conditionId, List<int> dealTypeId,int? userId, int page, int pageSize);
        Task<IEnumerable<DealDisplayDTO>> GetDealsByProductGroupedAsync(int productId, long? storeId = null, int? dealTypeId = null, int? conditionId = null, int? userId = null);

        Task<IEnumerable<DealDisplayDTO>> GetReviewDealsAsync();
        Task<PagedDealsResultDTO<DealDisplayDTO>> GetReviewDealsPagedAsync(int page, int pageSize);
        Task<PagedDealsResultDTO<DealDisplayDTO>> GetReviewedDealsPagedAsync(int page, int pageSize);
        Task<PagedDealsResultDTO<DealDisplayDTO>> GetUserSubmittedDealsPagedAsync(int page, int pageSize, int? userId = null, int? dealId = null);

        Task<IEnumerable<DealNav>> GetFeedDealsAsync(int userId);
        Task<Deal> CreateDealAsync(DealProductDTO dto);

        Task<List<DealCombo>> CreateDealComboAsync(List<DealCombo> dealCombos, bool? deleteExisting = false);
    
        Task<Deal?> UpdateDealAsync(DealProductDTO dto);
        Task<bool> DeleteDealAsync(int id);
        Task<bool> FlagDealAsync(int dealProductId, int? dealIssueTypeId, string? comments);
        Task<bool> ReviewDealAsync(int dealId, int dealProductId, int dealStatusId, int? dealIssueTypeId, string? comment);
    }
}