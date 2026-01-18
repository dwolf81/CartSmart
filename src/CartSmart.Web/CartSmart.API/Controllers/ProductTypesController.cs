using CartSmart.API.Models;
using CartSmart.API.Models.DTOs;
using CartSmart.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace CartSmart.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ProductTypesController : ControllerBase
    {
        private readonly ISupabaseService _supabase;

        [Table("deal")]
        private class DealIdRow : BaseModel
        {
            [Column("id")]
            public int Id { get; set; }

            [Column("store_id")]
            public int? StoreId { get; set; }

            [Column("deal_status_id")]
            public int DealStatusId { get; set; }

            [Column("deleted")]
            public bool Deleted { get; set; }
        }

        [Table("deal_product")]
        private class DealProductJoinRow : BaseModel
        {
            [Column("deal_id")]
            public int DealId { get; set; }

            [Column("product_id")]
            public int ProductId { get; set; }

            [Column("deal_status_id")]
            public int DealStatusId { get; set; }

            [Column("deleted")]
            public bool Deleted { get; set; }
        }

        [Table("product")]
        private class ProductTypeIdRow : BaseModel
        {
            [Column("id")]
            public int Id { get; set; }

            [Column("product_type_id")]
            public int ProductTypeId { get; set; }

            [Column("deleted")]
            public bool Deleted { get; set; }
        }

        [Table("product")]
        private class ProductImageRow : BaseModel
        {
            [Column("created_at")]
            public DateTime CreatedAt { get; set; }

            [Column("image_url")]
            public string? ImageUrl { get; set; }

            [Column("deleted")]
            public bool Deleted { get; set; }

            [Column("product_type_id")]
            public int ProductTypeId { get; set; }
        }

        public ProductTypesController(ISupabaseService supabase)
        {
            _supabase = supabase;
        }

        [HttpGet]
        [AllowAnonymous]
        [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any, NoStore = false)]
        public async Task<ActionResult<IEnumerable<ProductTypeCardDTO>>> GetAll()
        {
            // Use service-role for this read to avoid RLS silently returning empty results.
            // Product types are not sensitive and are needed for public navigation.
            var client = _supabase.GetServiceRoleClient();
            var resp = await client
                .From<ProductType>()
                .Select("id, name, slug")
                .Order("name", Supabase.Postgrest.Constants.Ordering.Ascending)
                .Get();

            var productTypes = resp.Models ?? new List<ProductType>();
            var result = new List<ProductTypeCardDTO>(productTypes.Count);

            foreach (var pt in productTypes)
            {
                var imagesResp = await client
                    .From<ProductImageRow>()
                    .Select("created_at, image_url, deleted, product_type_id")
                    .Filter("product_type_id", Supabase.Postgrest.Constants.Operator.Equals, pt.Id.ToString())
                    .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
                    .Order("created_at", Supabase.Postgrest.Constants.Ordering.Descending)
                    .Limit(24)
                    .Get();

                var imageUrls = (imagesResp.Models ?? new List<ProductImageRow>())
                    .Select(p => p.ImageUrl)
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Distinct()
                    .Take(4)
                    .ToList()!;

                result.Add(new ProductTypeCardDTO
                {
                    id = pt.Id,
                    name = pt.Name,
                    slug = pt.Slug,
                    imageUrls = imageUrls
                });
            }

            return Ok(result);
        }

        [HttpGet("by-store/{storeId:int}")]
        [AllowAnonymous]
        [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any, NoStore = false)]
        public async Task<ActionResult<IEnumerable<ProductTypeCardDTO>>> GetByStore([FromRoute] int storeId)
        {
            if (storeId <= 0) return Ok(new List<ProductTypeCardDTO>());

            var client = _supabase.GetServiceRoleClient();

            // Uses Supabase/Postgres RPC for efficiency.
            // Function: f_store_product_types(p_store_id bigint) -> (id, name, slug)
            var rows = await client
                .Rpc<List<ProductTypeCardDTO>>("f_store_product_types", new { p_store_id = storeId });

            var result = (rows ?? new List<ProductTypeCardDTO>())
                .Select(pt => new ProductTypeCardDTO
                {
                    id = pt.id,
                    name = pt.name,
                    slug = pt.slug,
                    imageUrls = new List<string>()
                })
                .ToList();

            return Ok(result);
        }
    }
}
