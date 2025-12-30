using Microsoft.AspNetCore.Mvc;
using CartSmart.API.Models;
using CartSmart.API.Services;
using CartSmart.API.Models.DTOs;
using Microsoft.AspNetCore.Authorization;

namespace CartSmart.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [AllowAnonymous]
    public class ProductsController : ControllerBase
    {
        private readonly IProductService _productService;

        public ProductsController(IProductService productService)
        {
            _productService = productService;
        }


        [HttpGet("getproduct/{identifier}")]
        [ResponseCache(Duration = 1800, Location = ResponseCacheLocation.Any, NoStore = false)]
        public async Task<ActionResult<ProductDTO>> GetProductBySlug(string identifier)
        {
            ProductDTO? product;    
            if (int.TryParse(identifier, out int id))
            {
                product = await _productService.GetProductByIdAsync(id);
            }
            else
            {
                product = await _productService.GetProductBySlugAsync(identifier);
            }

            if (product == null)
            {
                return NotFound(); // Explicitly return 404 when product is null
            }
            return Ok(product);
        }

        [HttpGet("getbestproductdeals")]
        [ResponseCache(Duration = 900, Location = ResponseCacheLocation.Any, NoStore = false)]
        public async Task<ActionResult<IEnumerable<Product>>> GetBestProductDeals()
        {
            return Ok(await _productService.GetBestProductDealsAsync());
        }

        [HttpGet("{productId}/ratings")]
        public async Task<ActionResult<IEnumerable<object>>> GetProductRatings(int productId)
        {
            var ratings = await _productService.GetProductRatingsAsync(productId);
            return Ok(ratings);
        }

        [HttpGet("{productId}/variant-filters")]
        public async Task<ActionResult<VariantFilterOptionsDTO>> GetVariantFilters(int productId)
        {
            var dto = await _productService.GetVariantFilterOptionsAsync(productId);
            return Ok(dto);
        }

    }
}