using CartSmart.API.Models;
using CartSmart.API.Models.DTOs;

namespace CartSmart.API.Services
{
    public interface IProductService
    {
        Task<IEnumerable<Product>> GetAllProductsAsync();
        Task<ProductDTO?> GetProductByIdAsync(int id);
        Task<ProductDTO?> GetProductBySlugAsync(string? slug);
        Task<Product> CreateProductAsync(Product product);
        Task<Product?> UpdateProductAsync(int id, Product product);
        Task<bool> DeleteProductAsync(int id);

        Task<IEnumerable<DealDisplayDTO>> GetBestProductDealsAsync();
        Task<IEnumerable<object>> GetProductRatingsAsync(int productId);

        Task<VariantFilterOptionsDTO> GetVariantFilterOptionsAsync(int productId);
    }
}