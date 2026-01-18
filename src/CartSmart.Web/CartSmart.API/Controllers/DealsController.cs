using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using CartSmart.API.Models;
using CartSmart.API.Services;
using CartSmart.API.Models.DTOs;
using CartSmart.API.Exceptions;

namespace CartSmart.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
     
    public class DealsController : ControllerBase
    {
        private readonly IDealService _dealService;

        public DealsController(IDealService dealService)
        {
            _dealService = dealService;
        }

        [HttpGet("{id}")]
        [AllowAnonymous]
        public async Task<ActionResult<DealProductDTO>> GetDealProduct(int id)
        {
            var dealProduct = await _dealService.GetDealProductByIdAsync(id);
            if (dealProduct == null)
            {
                return NotFound();
            }
            return dealProduct;
        }

        [HttpGet("product/{productId}")]
        [AllowAnonymous]
        public async Task<ActionResult<IEnumerable<DealNav>>> GetProductDeals(int productId, [FromQuery] int? conditionId = null, [FromQuery] List<int> dealTypeId = null, [FromQuery] int? userId = null,[FromQuery] int page = 1, [FromQuery] int pageSize = 5)
        {
            return Ok(await _dealService.GetDealsByProductAsync(productId, conditionId, dealTypeId, userId, page, pageSize));
        }

        [HttpGet("product2/{productId}")]
        [AllowAnonymous]
        public async Task<ActionResult<IEnumerable<DealDisplayDTO>>> GetProductDeals2(
            int productId,
            [FromQuery] long? storeId = null,
            [FromQuery] int? dealTypeId = null,
            [FromQuery] int? conditionId = null,
            [FromQuery] int? userId = null)
        {
            var result = await _dealService.GetDealsByProductGroupedAsync(productId, storeId, dealTypeId, conditionId, userId);
            return Ok(result);
        }

        [HttpPost("product2/{productId}")]
        [AllowAnonymous]
        public async Task<ActionResult<IEnumerable<DealDisplayDTO>>> GetProductDeals2Post(
            int productId,
            [FromBody] GetProductDeals2RequestDTO request)
        {
            request ??= new GetProductDeals2RequestDTO();

            var result = await _dealService.GetDealsByProductGroupedAsync(
                productId,
                request.StoreId,
                request.DealTypeId,
                request.ConditionId,
                request.UserId,
                request.AttributeFilters);

            return Ok(result);
        }

        [HttpGet("getreviewdeals")]
        [AllowAnonymous]
        public async Task<ActionResult<IEnumerable<DealNav>>> GetReviewDeals()
        {
            return Ok(await _dealService.GetReviewDealsAsync());
        }


        [HttpGet("user-submitted")]
        [AllowAnonymous]
        public async Task<ActionResult<PagedDealsResultDTO<DealDisplayDTO>>> GetDealsByUserAsync(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] int? userId = null,
            [FromQuery] int? dealId = null)
        {
            var result = await _dealService.GetUserSubmittedDealsPagedAsync(page, pageSize, userId, dealId);
            return Ok(result);
        }

        [HttpGet("review-queue")]
        [AllowAnonymous]
        public async Task<ActionResult<PagedDealsResultDTO<DealDisplayDTO>>> GetReviewQueue([FromQuery] int page = 1, [FromQuery] int pageSize = 10)
        {
            var result = await _dealService.GetReviewDealsPagedAsync(page, pageSize);
            return Ok(result);
        }

        [HttpGet("reviewed")]
        [AllowAnonymous]
        public async Task<ActionResult<PagedDealsResultDTO<DealDisplayDTO>>> GetReviewedDeals([FromQuery] int page = 1, [FromQuery] int pageSize = 10)
        {
            var result = await _dealService.GetReviewedDealsPagedAsync(page, pageSize);
            return Ok(result);
        }

        [HttpPost]
        [Authorize]
        public async Task<ActionResult<DealProductDTO>> CreateDeal([FromBody] DealProductDTO dealDto)
        {
            try
            {

                if (dealDto.DealTypeId == 3 && dealDto.DealIds.Count < 2)
                {
                    return BadRequest(new { message = "At least two deals must be selected for a combo deal." });   
                }

                var createdDeal = await _dealService.CreateDealAsync(dealDto);

                if (createdDeal != null && dealDto.DealTypeId == 3)
                {
                    var dealCombos = new List<DealCombo>();
                    int order = 1;
                    foreach (var comboId in dealDto.DealIds)
                    {
                        dealCombos.Add(new DealCombo
                        {
                            DealId = createdDeal.Id,
                            ComboDealId = comboId,
                            Order = order++
                        });
                    }
                    await _dealService.CreateDealComboAsync(dealCombos);
                }

                return CreatedAtAction(nameof(GetDealProduct), new { id = createdDeal.Id }, createdDeal);
            }
            catch (DuplicateDealException ex)
            {
                return Conflict(new { message = ex.Message, existingDealId = ex.ExistingDealId });
            }
            catch (DealSubmissionLimitException ex)
            {
                return StatusCode(429, new
                {
                    message = ex.Message,
                    limit = ex.Limit,
                    used = ex.Used
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("store-wide")]
        [Authorize]
        public async Task<ActionResult<Deal>> CreateStoreWideDeal([FromBody] StoreWideDealDTO dealDto)
        {
            try
            {
                var createdDeal = await _dealService.CreateStoreWideDealAsync(dealDto);
                return Ok(createdDeal);
            }
            catch (DuplicateDealException ex)
            {
                return Conflict(new { message = ex.Message, existingDealId = ex.ExistingDealId });
            }
            catch (DealSubmissionLimitException ex)
            {
                return StatusCode(429, new
                {
                    message = ex.Message,
                    limit = ex.Limit,
                    used = ex.Used
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("reviewdeal")]
        [Authorize]
        public async Task<IActionResult> ReviewDeal(
            [FromQuery] int dealId,
            [FromQuery] int? dealProductId,
            [FromQuery] int dealStatusId,
            [FromQuery] string? comment = null,
            [FromQuery] int? dealIssueTypeId = null)
        {
            try
            {
                await _dealService.ReviewDealAsync(
                    dealId,
                    dealProductId,
                    dealStatusId,
                    dealIssueTypeId,
                    comment
                );
                return Ok(new { message = "Deal reviewed successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPut("{id}")]
        [Authorize]
        public async Task<IActionResult> UpdateDeal(int id, [FromBody] DealProductDTO dealDto)
        {

            try
            {

            if (id != dealDto.DealProductId)
            {
                return BadRequest();
            }

                if (dealDto.DealTypeId == 3 && dealDto.DealIds.Count < 2)
                {
                    return BadRequest(new { message = "At least two deals must be selected for a combo deal." });
                }

            var updatedDeal = await _dealService.UpdateDealAsync(dealDto);
            if (updatedDeal == null)
            {
                return NotFound();
            }

                if (updatedDeal != null && dealDto.DealTypeId == 3)
                {
                    var dealCombos = new List<DealCombo>();
                    int order = 1;
                    foreach (var comboId in dealDto.DealIds)
                    {
                        dealCombos.Add(new DealCombo
                        {
                            DealId = updatedDeal.Id,
                            ComboDealId = comboId,
                            Order = order++
                        });
                    }
                    await _dealService.CreateDealComboAsync(dealCombos,true);
                }

                return Ok(updatedDeal);
            }
            catch (DuplicateDealException ex)
            {
                return Conflict(new { message = ex.Message, existingDealId = ex.ExistingDealId });
            }
            catch (DealSubmissionLimitException ex)
            {
                return StatusCode(429, new
                {
                    message = ex.Message,
                    limit = ex.Limit,
                    used = ex.Used
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

        }

        [HttpPut("store-wide/{dealId:int}")]
        [Authorize]
        public async Task<IActionResult> UpdateStoreWideDeal(int dealId, [FromBody] StoreWideDealDTO dealDto)
        {
            try
            {
                var updated = await _dealService.UpdateStoreWideDealAsync(dealId, dealDto);
                if (updated == null) return NotFound();
                return Ok(updated);
            }
            catch (DuplicateDealException ex)
            {
                return Conflict(new { message = ex.Message, existingDealId = ex.ExistingDealId });
            }
            catch (DealSubmissionLimitException ex)
            {
                return StatusCode(429, new
                {
                    message = ex.Message,
                    limit = ex.Limit,
                    used = ex.Used
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // New endpoint: supports store-deal flags (deal_id only) and always records deal_id.
        [HttpPost("flag")]
        [Authorize]
        public async Task<IActionResult> FlagDeal([FromBody] FlagDealRequest request)
        {
            if (request.DealId <= 0)
                return BadRequest(new { message = "dealId is required." });

            try
            {
                var ok = await _dealService.FlagDealAsync(request.DealId, request.DealProductId, request.DealIssueTypeId, request.Comment);
                if (!ok) return NotFound();
                return Ok(new { message = "Flag recorded" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        // Legacy endpoint kept for backward compatibility; id is deal_product_id.
        // We resolve deal_id from the request or the deal_product row.
        [HttpPost("{id}/flag")]
        [Authorize]
        public async Task<IActionResult> FlagDealLegacy(
            int id,
            [FromBody] FlagDealRequest request,
            [FromServices] ISupabaseService supabase)
        {
            try
            {
                long dealId = request.DealId;
                if (dealId <= 0)
                {
                    var dp = (await supabase.GetAllAsync<DealProduct>()).FirstOrDefault(x => x.Id == id);
                    if (dp == null) return NotFound();
                    dealId = dp.DealId;
                }

                var ok = await _dealService.FlagDealAsync(dealId, request.DealProductId ?? id, request.DealIssueTypeId, request.Comment);
                if (!ok) return NotFound();
                return Ok(new { message = "Flag recorded" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpDelete("{id}")]
        [Authorize]
        public async Task<IActionResult> DeleteDeal(int id)
        {
            var result = await _dealService.DeleteDealAsync(id);
            if (!result)
            {
                return NotFound();
            }
            return NoContent();
        }

        // Log a click (anonymous users are recorded as user_id = 0)
        [HttpPost("{dealId:int}/click")]
        [AllowAnonymous]
        public async Task<IActionResult> LogClick(
            int dealId,
            [FromQuery] int? productId,
            [FromQuery] bool external,
            [FromServices] IAuthService authService,
            [FromServices] ISupabaseService supabase)
        {
            if (dealId <= 0)
                return BadRequest(new { message = "Invalid dealId." });
            if (productId.HasValue && productId.Value <= 0)
                return BadRequest(new { message = "Invalid productId." });

            var userId = 1;
            var userIdStr = authService.GetCurrentUserId();
            if (!string.IsNullOrWhiteSpace(userIdStr) && int.TryParse(userIdStr, out var parsed))
                userId = parsed;

            var dealClick = new DealClick
            {
                DealId = dealId,
                ProductId = productId,
                External = external,
                UserId = userId,
                CreatedAt = DateTime.UtcNow
            };

            await supabase.InsertAsync(dealClick);
            return Ok(new { message = "logged" });
        }
    }

    // Add/adjust DTOs (or update existing)
    public class FlagDealRequest
    {
        public long DealId { get; set; }
        public long? DealProductId { get; set; }
        public int? DealIssueTypeId { get; set; }
        public string? Comment { get; set; }
    }

    public class ReviewDealRequest
    {
        public int DealId { get; set; }
        public int DealProductId { get; set; }
        public int DealStatusId { get; set; }
        public int? DealIssueTypeId { get; set; } // only needed on reject
        public string? Comment { get; set; }
    }
}