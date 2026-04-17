using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using CartSmart.API.Models;
using CartSmart.API.Services;
using CartSmart.API.Models.DTOs;
using CartSmart.API.Exceptions;
using static Supabase.Postgrest.Constants;

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

        [HttpGet("variant-options/{dealId}")]
        [AllowAnonymous]
        public async Task<ActionResult<IEnumerable<DealVariantOptionDTO>>> GetDealVariantOptions(
            long dealId,
            [FromQuery] int productId,
            [FromQuery] int? conditionId = null)
        {
            if (dealId <= 0 || productId <= 0) return BadRequest(new { message = "Invalid dealId or productId." });
            var result = await _dealService.GetDealVariantOptionsAsync(productId, dealId, conditionId);
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
            catch (UnauthorizedAccessException)
            {
                return StatusCode(403, new { message = "Admin access required." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        // ─── Ingested Deals (from ingestion pipeline) ──────────────────────────

        [HttpGet("ingested-queue")]
        [Authorize]
        public async Task<IActionResult> GetIngestedQueue(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10,
            [FromServices] ISupabaseService supabase = null!)
        {
            var client = supabase.GetServiceRoleClient();

            var allPending = await client.From<ExtractedDeal>()
                .Filter("status", Operator.Equals, "pending_review")
                .Order("created_at", Ordering.Descending)
                .Get();

            var totalCount = allPending.Models.Count;
            var page_items = allPending.Models
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            // Batch-load related raw_signals and ingestion_sources
            var signalIds = page_items.Select(d => d.RawSignalId).Distinct().ToList();
            var signals = new Dictionary<long, RawSignal>();
            var sources = new Dictionary<long, IngestionSource>();

            if (signalIds.Count > 0)
            {
                var sigResponse = await client.From<RawSignal>()
                    .Filter("id", Operator.In, signalIds.Select(id => (object)id.ToString()).ToArray())
                    .Get();
                foreach (var s in sigResponse.Models)
                    signals[s.Id] = s;

                var sourceIds = sigResponse.Models.Select(s => s.IngestionSourceId).Distinct().ToList();
                if (sourceIds.Count > 0)
                {
                    var srcResponse = await client.From<IngestionSource>()
                        .Filter("id", Operator.In, sourceIds.Select(id => (object)id.ToString()).ToArray())
                        .Get();
                    foreach (var src in srcResponse.Models)
                        sources[src.Id] = src;
                }
            }

            // Also look up product/store names
            var productIds = page_items.Where(d => d.ProductId.HasValue).Select(d => d.ProductId!.Value).Distinct().ToList();
            var storeIds = page_items.Where(d => d.StoreId.HasValue).Select(d => d.StoreId!.Value).Distinct().ToList();
            var products = new Dictionary<long, string>();
            var stores = new Dictionary<int, string>();

            if (productIds.Count > 0)
            {
                var prodResp = await client.From<Product>()
                    .Filter("id", Operator.In, productIds.Select(id => (object)id.ToString()).ToArray())
                    .Get();
                foreach (var p in prodResp.Models)
                    products[p.Id] = p.Name;
            }
            if (storeIds.Count > 0)
            {
                var storeResp = await client.From<Store>()
                    .Filter("id", Operator.In, storeIds.Select(id => (object)id.ToString()).ToArray())
                    .Get();
                foreach (var s in storeResp.Models)
                    stores[s.Id] = s.Name;
            }

            var items = page_items.Select(d =>
            {
                signals.TryGetValue(d.RawSignalId, out var signal);
                IngestionSource? source = null;
                if (signal != null)
                    sources.TryGetValue(signal.IngestionSourceId, out source);

                return new
                {
                    id = d.Id,
                    title = d.Title,
                    price = d.Price,
                    currency = d.Currency,
                    coupon_code = d.CouponCode,
                    url = d.Url,
                    discount_percent = d.DiscountPercent,
                    deal_type_id = d.DealTypeId,
                    expiration_date = d.ExpirationDate,
                    confidence_score = d.ConfidenceScore,
                    ai_reasoning = d.AiReasoning,
                    status = d.Status,
                    created_at = d.CreatedAt,
                    product_id = d.ProductId,
                    product_name = d.ProductId.HasValue && products.ContainsKey(d.ProductId.Value) ? products[d.ProductId.Value] : null,
                    store_id = d.StoreId,
                    store_name = d.StoreId.HasValue && stores.ContainsKey(d.StoreId.Value) ? stores[d.StoreId.Value] : null,
                    source_name = source?.Name,
                    source_type = source?.SourceType,
                    signal_title = signal?.Title,
                    signal_url = signal?.Url,
                    signal_author = signal?.Author,
                    signal_body = signal?.Body,
                    store_wide = d.StoreWide,
                };
            }).ToList();

            return Ok(new { deals = items, totalCount });
        }

        [HttpPost("review-ingested")]
        [Authorize]
        public async Task<IActionResult> ReviewIngested(
            [FromQuery] long extractedDealId,
            [FromQuery] string action,
            [FromServices] ISupabaseService supabase = null!,
            [FromServices] IAuthService authService = null!)
        {
            if (action != "approve" && action != "reject")
                return BadRequest(new { message = "action must be 'approve' or 'reject'" });

            var client = supabase.GetServiceRoleClient();

            var dealResp = await client.From<ExtractedDeal>()
                .Filter("id", Operator.Equals, extractedDealId.ToString())
                .Get();

            var extracted = dealResp.Models.FirstOrDefault();
            if (extracted == null)
                return NotFound(new { message = "Extracted deal not found" });

            if (extracted.Status != "pending_review")
                return BadRequest(new { message = $"Deal is already {extracted.Status}" });

            var userIdStr = authService.GetCurrentUserId();
            int.TryParse(userIdStr, out var userId);

            if (action == "reject")
            {
                await client.From<ExtractedDeal>()
                    .Filter("id", Operator.Equals, extractedDealId.ToString())
                    .Set(x => x.Status, "rejected")
                    .Set(x => x.ReviewedBy!, (long)userId)
                    .Set(x => x.ReviewedAt!, DateTime.UtcNow)
                    .Update();
                return Ok(new { message = "Ingested deal rejected" });
            }

            // Approve: create Deal + DealProduct, link back
            var newDeal = new Deal
            {
                CouponCode = extracted.CouponCode,
                DealStatusId = 2, // Active
                UserId = userId,
                StoreId = extracted.StoreId,
                DiscountPercent = extracted.DiscountPercent,
                DealTypeId = extracted.DealTypeId ?? 1,
                ExpirationDate = extracted.ExpirationDate,
                Deleted = false,
                StoreWide = extracted.StoreWide,
                CreatedAt = DateTime.UtcNow,
            };
            var dealInsert = await client.From<Deal>().Insert(newDeal);
            var createdDeal = dealInsert.Models.First();

            if (extracted.ProductId.HasValue)
            {
                var newDealProduct = new DealProduct
                {
                    DealId = createdDeal.Id,
                    ProductId = (int)extracted.ProductId.Value,
                    Price = extracted.Price ?? 0,
                    Url = extracted.Url,
                    DealStatusId = 2, // Active
                    Deleted = false,
                    Primary = true,
                    CreatedAt = DateTime.UtcNow,
                    ItemCount = 1,
                };
                await client.From<DealProduct>().Insert(newDealProduct);
            }

            await client.From<ExtractedDeal>()
                .Filter("id", Operator.Equals, extractedDealId.ToString())
                .Set(x => x.Status, "manually_imported")
                .Set(x => x.DealId!, (long)createdDeal.Id)
                .Set(x => x.ReviewedBy!, (long)userId)
                .Set(x => x.ReviewedAt!, DateTime.UtcNow)
                .Set(x => x.ImportedAt!, DateTime.UtcNow)
                .Update();

            return Ok(new { message = "Ingested deal approved and imported", dealId = createdDeal.Id });
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

        [HttpPost("hide")]
        [Authorize]
        public async Task<IActionResult> HideDeal([FromBody] HideDealRequest request)
        {
            if (request.DealId <= 0)
                return BadRequest(new { message = "dealId is required." });

            try
            {
                await _dealService.HideDealAsync(request.DealId);
                return Ok(new { message = "Deal hidden" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPost("unhide")]
        [Authorize]
        public async Task<IActionResult> UnhideDeal([FromBody] HideDealRequest request)
        {
            if (request.DealId <= 0)
                return BadRequest(new { message = "dealId is required." });

            try
            {
                await _dealService.UnhideDealAsync(request.DealId);
                return Ok(new { message = "Deal unhidden" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
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

        [HttpPost("admin-delete")]
        [Authorize]
        public async Task<IActionResult> AdminDelete([FromBody] AdminDeleteDealRequest request)
        {
            if (request.DealId <= 0)
                return BadRequest(new { message = "dealId is required." });

            if (!request.DeleteDeal && (!request.DealProductId.HasValue || request.DealProductId.Value <= 0))
                return BadRequest(new { message = "dealProductId is required when deleteDeal is false." });

            try
            {
                var ok = await _dealService.AdminDeleteAsync(request.DealId, request.DealProductId, request.DeleteDeal);
                if (!ok) return NotFound();
                return Ok(new { message = "Deleted" });
            }
            catch (UnauthorizedAccessException)
            {
                return StatusCode(403, new { message = "Admin access required." });
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

    public class AdminDeleteDealRequest
    {
        public long DealId { get; set; }
        public long? DealProductId { get; set; }
        public bool DeleteDeal { get; set; }
    }

    public class HideDealRequest
    {
        public long DealId { get; set; }
    }
}