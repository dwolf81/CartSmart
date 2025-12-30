using CartSmart.API.Exceptions;
using CartSmart.API.Models;
using CartSmart.API.Models.DTOs;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace CartSmart.API.Services;

public class DealService : IDealService
{
    private readonly IAuthService _authService;
    private readonly IUrlSanitizer _urlSanitizer;
    private readonly ISupabaseService _supabase;
    private readonly IMemoryCache _cache;

    public DealService(ISupabaseService supabase, IAuthService authService, IUrlSanitizer urlSanitizer, IMemoryCache cache /* other deps */)
    {
        _supabase = supabase;
        _authService = authService;
        _urlSanitizer = urlSanitizer;
        _cache = cache;
    }

    private static Uri? TryCreateHttpUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        // Already absolute?
        if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var abs))
            return abs;

        // Prepend https:// if missing scheme
        var candidate = $"https://{url.Trim()}";
        if (Uri.TryCreate(candidate, UriKind.Absolute, out abs))
            return abs;

        return null;
    }

    private static string? NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var u = TryCreateHttpUri(url);
        if (u == null) return url.Trim().TrimEnd('/').ToLowerInvariant();

        var path = u.AbsolutePath?.TrimEnd('/');
        var host = u.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        return $"{host}{path}".ToLowerInvariant();
    }

    private static decimal? ToDecimal(object? value)
    {
        if (value is null) return null;
        try
        {
            return value switch
            {
                decimal d => d,
                float f => (decimal)f,
                double db => (decimal)db,
                int i => i,
                long l => l,
                string s when decimal.TryParse(s, out var parsed) => parsed,
                _ => Convert.ToDecimal(value)
            };
        }
        catch { return null; }
    }

    private static bool PricesEqual(object? a, object? b)
    {
        var da = ToDecimal(a);
        var db = ToDecimal(b);
        if (!da.HasValue || !db.HasValue) return false;
        return Math.Abs(da.Value - db.Value) < 0.005m; // ~1/2 cent tolerance
    }

/*
    private static string? NormalizeDealUrl(Deal d)
    {
        return d.DealTypeId == 4
            ? NormalizeUrl(d.ExternalOfferUrl)
            : NormalizeUrl(d.Url);
    }
    */

    private static string? ExtractHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var u = TryCreateHttpUri(url);
        if (u == null) return null;

        return u.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
    }

    private static bool DomainEqual(string? a, string? b)
    {
        var ha = ExtractHost(a);
        var hb = ExtractHost(b);
        if (string.IsNullOrEmpty(ha) || string.IsNullOrEmpty(hb)) return false;
        return ha.Equals(hb, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<int?> ResolveStoreIdFromUrlAsync(string? url)
    {
        var host = ExtractHost(url);
        if (string.IsNullOrEmpty(host)) return null;

        var stores = await _supabase.GetAllAsync<Store>();
        foreach (var s in stores)
        {
            if (string.IsNullOrWhiteSpace(s.URL)) continue;
            var storeHost = ExtractHost(s.URL);
            if (!string.IsNullOrEmpty(storeHost) && string.Equals(storeHost, host, StringComparison.OrdinalIgnoreCase))
                return s.Id;
        }
        return null;
    }

    private static string HostFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        var u = TryCreateHttpUri(url);
        if (u == null) return string.Empty;
        return u.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
    }

    private static string DeriveStoreName(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return "Unknown Store";
        var core = host.Split('.')[0];
        if (string.IsNullOrWhiteSpace(core)) core = host;
        return char.ToUpper(core[0]) + core.Substring(1);
    }

    // New: resolve OR create store by domain. Returns store id or null.
    private async Task<Store?> ResolveOrCreateStoreFromUrlAsync(string? url)
    {
        var host = HostFromUrl(url);
        if (string.IsNullOrEmpty(host)) return null;

        var client = _supabase.GetClient();

        // Fetch existing stores (could be optimized with cached map)
        var existingStores = await _supabase.GetAllAsync<Store>();
        var match = existingStores.FirstOrDefault(s =>
        {
            if (string.IsNullOrWhiteSpace(s.URL)) return false;
            return HostFromUrl(s.URL) == host;
        });

        if (match != null) return match;

        // Create new store
        var newStore = new Store
        {
            CreatedAt = DateTime.UtcNow,
            Name = DeriveStoreName(host),
            // Capitalize first letter of the host for storage/display
            URL = string.IsNullOrEmpty(host) ? host : char.ToUpper(host[0]) + host.Substring(1),
            AffiliateCode = null,
            AffiliateCodeVar = null,
            RequiredQueryVars = null,
            BrandId = null,
            UpfrontCost = null,
            UpfrontCostTermId = null
        };

        var inserted = await _supabase.InsertAsync(newStore);
        return inserted;
    }

    public async Task<IEnumerable<DealNav>> GetAllDealsAsync()
    {
        return await _supabase.GetAllAsync<DealNav>();
    }

    public async Task<DealProductDTO?> GetDealProductByIdAsync(int id)
    {
        var client = _supabase.GetClient();

        var dpResp = await client
            .From<DealProduct>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, id)
            .Select("*")
            .Get();

        var dp = dpResp.Models.FirstOrDefault();
        if (dp == null) return null;

        var dResp = await client
            .From<Deal>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, dp.DealId)
            .Select("id, deal_type_id, additional_details, coupon_code, discount_percent, expiration_date, external_offer_url, deleted")
            .Get();

        var d = dResp.Models.FirstOrDefault();
        if (d == null || d.Deleted) return null;

        return new DealProductDTO
        {
            DealProductId = dp.Id,
            DealId = dp.DealId,
            ProductId = dp.ProductId,
            Price = dp.Price,
            Url = dp.Url,
            FreeShipping = dp.FreeShipping,
            ConditionId = dp.ConditionId,
            DealTypeId = d.DealTypeId ?? 0,
            AdditionalDetails = d.AdditionalDetails,
            CouponCode = d.CouponCode,
            DiscountPercent = d.DiscountPercent,
            ExpirationDate = d.ExpirationDate,
            ExternalOfferUrl = d.ExternalOfferUrl
        };
    }

    public async Task<PagedDealsResultDTO<DealDisplayDTO>> GetDealsByUserAsync(int userId,int page, int pageSize)
    {
        //Todo: Check if userId is the same as the current userId and return only active deals if not
        var currentUserId = _authService.GetCurrentUserId();
        var client = _supabase.GetClient();
        // You may need a new RPC or query to get deals submitted by this user
        var allSubmittedDeals = await client
            .Rpc<List<DealDisplayDTO>>("f_get_deals_review", new { p_user_id = userId, p_mode = "Submitted" });

        var totalCount = allSubmittedDeals.Count;
        var pagedDeals = allSubmittedDeals
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new PagedDealsResultDTO<DealDisplayDTO>
        {
            Deals = pagedDeals,
            TotalCount = totalCount
        };
    }
 // Whitelisted fields for anonymous users (support both snake_case and PascalCase)
    private static readonly HashSet<string> AnonymousWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "deal_type_id", "DealTypeId",
        "free_shipping", "FreeShipping",
        "msrp", "MSRP",
        "price", "Price",
        "discount_percent", "DiscountPercent",
        "deal_id", "DealId",

        // Needed for store-grouped ProductPage deals UI
        "store_id", "StoreId",
        "store_name", "StoreName",
        "store_logo_url", "StoreLogoUrl",
        "store_deal_count", "StoreDealCount",
        "additional_deal_count", "AdditionalDealCount"
    };

// Scramble helper: preserve length, whitespace, and punctuation; randomize letters/digits
private static string ScramblePreserveShape(string? input)
{
    if (string.IsNullOrEmpty(input)) return input ?? string.Empty;

    var sb = new StringBuilder(input.Length);
    foreach (var ch in input)
    {
        if (char.IsWhiteSpace(ch))
        {
            sb.Append(ch);
        }
        else if (char.IsLetter(ch))
        {
            var r = System.Security.Cryptography.RandomNumberGenerator.GetInt32(26);
            var baseChar = char.IsUpper(ch) ? 'A' : 'a';
            sb.Append((char)(baseChar + r));
        }
        else if (char.IsDigit(ch))
        {
            var r = System.Security.Cryptography.RandomNumberGenerator.GetInt32(10);
            sb.Append((char)('0' + r));
        }
        else
        {
            // keep punctuation/symbols to preserve layout
            sb.Append(ch);
        }
    }
    return sb.ToString();
}

private static DealDisplayDTO SanitizeDealForAnonymous(DealDisplayDTO d)
{
    var clone = new DealDisplayDTO();
    var props = typeof(DealDisplayDTO).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    foreach (var p in props)
    {
        if (!p.CanWrite) continue;

        if (AnonymousWhitelist.Contains(p.Name))
        {
            var val = p.GetValue(d);
            p.SetValue(clone, val);
        }
        else
        {
            var t = p.PropertyType;

            // Scramble strings to keep UI filled while hiding details
            if (t == typeof(string))
            {
                var original = (string?)p.GetValue(d);
                var scrambled = ScramblePreserveShape(original ?? string.Empty);
                p.SetValue(clone, scrambled);
            }
            else
            {
                // Clear non-string, non-whitelisted fields
                object? defaultVal = t.IsValueType ? Activator.CreateInstance(t) : null;
                p.SetValue(clone, defaultVal);
            }
        }
    }

    return clone;
}
    public async Task<PagedDealsResultDTO<DealDisplayDTO>> GetDealsByProductAsync(int productId, int? conditionId = null, List<int> dealTypeId = null, int? userId = null, int page = 1, int pageSize = 5)
    {
        var currentUserId = _authService.GetCurrentUserId();

        var client = _supabase.GetClient();

        var productDeals = await client
            .Rpc<List<DealDisplayDTO>>("f_get_product_deals", new
            {
                p_product_id = productId,
                p_user_id = userId,
            });

        if (conditionId.HasValue)
        {
            productDeals = productDeals.Where(d => d.condition_id == conditionId.Value).ToList();
        }

        if (dealTypeId?.Count > 0)
        {
            productDeals = productDeals.Where(d => dealTypeId.Exists(x => x == d.deal_type_id)).ToList();
        }

        var totalCount = productDeals.Count;
        var pagedDeals = productDeals
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        // If authenticated, annotate which deals this user already flagged
        if (currentUserId != null)
        {
            var me = Convert.ToInt32(currentUserId);
            var dpIds = pagedDeals.Select(d => d.deal_product_id).Distinct().ToArray();
            if (dpIds.Length > 0)
            {
                var flags = await _supabase.GetAllAsync<DealFlag>();
                var flaggedSet = flags
                    .Where(f => f.UserId == me && dpIds.Contains(f.DealProductId))
                    .Select(f => f.DealProductId)
                    .ToHashSet();

                foreach (var d in pagedDeals)
                    d.user_flagged = flaggedSet.Contains(d.deal_product_id);
            }
        }

        if (currentUserId == null)
        {
            // Scramble/hide for anonymous (user_flagged remains false)
            //pagedDeals = pagedDeals.Select(SanitizeDealForAnonymous).ToList();
        }

        return new PagedDealsResultDTO<DealDisplayDTO>
        {
            Deals = pagedDeals,
            TotalCount = totalCount
        };

    }

    public async Task<IEnumerable<DealDisplayDTO>> GetDealsByProductGroupedAsync(
        int productId,
        long? storeId = null,
        int? dealTypeId = null,
        int? conditionId = null,
        int? userId = null)
    {
        var currentUserId = _authService.GetCurrentUserId();
        var client = _supabase.GetClient();

        var deals = await client
            .Rpc<List<DealDisplayDTO>>("f_get_product_deals_2", new
            {
                p_product_id = productId,
                p_user_id = userId,
                p_store_id = storeId,
                p_deal_type_id = dealTypeId,
                p_condition_id = conditionId
            });

        // If authenticated, annotate which deals this user already flagged
        if (currentUserId != null)
        {
            var me = Convert.ToInt32(currentUserId);
            var dpIds = deals.Select(d => d.deal_product_id).Distinct().ToArray();
            if (dpIds.Length > 0)
            {
                var flags = await _supabase.GetAllAsync<DealFlag>();
                var flaggedSet = flags
                    .Where(f => f.UserId == me && dpIds.Contains(f.DealProductId))
                    .Select(f => f.DealProductId)
                    .ToHashSet();

                foreach (var d in deals)
                    d.user_flagged = flaggedSet.Contains(d.deal_product_id);
            }
        }

        // Anonymous users should be able to see primary deal details:
        // - Collapsed (storeId == null): full details for each primary row
        // - Expanded store request (storeId != null): keep first row full, obfuscate the rest
        /*
        if (currentUserId == null && storeId != null)
        {
            if (deals.Count > 1)
            {
                var first = deals[0];
                var rest = deals.Skip(1).Select(SanitizeDealForAnonymous);
                deals = new[] { first }.Concat(rest).ToList();
            }
            else
            {
                // Single row expanded result: nothing to obfuscate beyond the primary deal.
            }
        }*/

        return deals;
    }

    public async Task<IEnumerable<DealDisplayDTO>> GetReviewDealsAsync()
    {
        var client = _supabase.GetClient();

        var userId = _authService.GetCurrentUserId();

        var dealsWithUsers = await client
            .Rpc<List<DealDisplayDTO>>("f_get_deals_not_reviewed_by_user", new
            {
                p_user_id = userId,
            });

        return dealsWithUsers;
    }


    public async Task<PagedDealsResultDTO<DealDisplayDTO>> GetUserSubmittedDealsPagedAsync(int page, int pageSize, int? userId = null, int? dealId = null)
    {
        var client = _supabase.GetClient();
        // Determine target user: explicit userId or current
        var currentUserIdStr = _authService.GetCurrentUserId();
        int? currentUserId = string.IsNullOrWhiteSpace(currentUserIdStr) ? null : Convert.ToInt32(currentUserIdStr);
        int effectiveUserId = userId ?? currentUserId ?? 0;
        if (effectiveUserId == 0)
        {
            return new PagedDealsResultDTO<DealDisplayDTO> { Deals = new List<DealDisplayDTO>(), TotalCount = 0 };
        }

        // Fetch all submitted deals (unpaged RPC as before)
        var allDeals = await client
            .Rpc<List<DealDisplayDTO>>("f_get_deals_review", new { p_user_id = effectiveUserId, p_mode = "Submitted" });

        // Optional: if dealId provided but not present, we still proceed (client can show not found message)
        DealDisplayDTO? target = null;
        if (dealId.HasValue)
            target = allDeals.FirstOrDefault(d => d.deal_id == dealId.Value);

        int totalCount = allDeals.Count;

        // Normal paging
        var pagedDeals = allDeals
            .OrderByDescending(d => d.deal_id) // ensure stable order
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        // Ensure target deal is included in the page (prepend if missing)
        if (dealId.HasValue && target != null && !pagedDeals.Any(d => d.deal_id == target.deal_id))
        {
            pagedDeals.Insert(0, target);
        }

        return new PagedDealsResultDTO<DealDisplayDTO>
        {
            Deals = pagedDeals,
            TotalCount = totalCount
        };
    }

    public async Task<PagedDealsResultDTO<DealDisplayDTO>> GetReviewDealsPagedAsync(int page, int pageSize)
    {
        var client = _supabase.GetClient();
        var userId = _authService.GetCurrentUserId();

        // Get all deals not reviewed by user (could be optimized with a paged RPC)
        var allDeals = await client
            .Rpc<List<DealDisplayDTO>>("f_get_deals_review", new { p_user_id = userId, p_mode = "Not Reviewed" });

        var totalCount = allDeals.Count;
        var pagedDeals = allDeals
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new PagedDealsResultDTO<DealDisplayDTO>
        {
            Deals = pagedDeals,
            TotalCount = totalCount
        };
    }

    public async Task<PagedDealsResultDTO<DealDisplayDTO>> GetReviewedDealsPagedAsync(int page, int pageSize)
    {
        var client = _supabase.GetClient();
        var userId = _authService.GetCurrentUserId();
        // You may need a new RPC or query to get deals reviewed by this user
        var allReviewedDeals = await client
             .Rpc<List<DealDisplayDTO>>("f_get_deals_review", new { p_user_id = userId, p_mode = "Reviewed" });

        var totalCount = allReviewedDeals.Count;
        var pagedDeals = allReviewedDeals
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new PagedDealsResultDTO<DealDisplayDTO>
        {
            Deals = pagedDeals,
            TotalCount = totalCount
        };
    }

    public async Task<IEnumerable<DealNav>> GetFeedDealsAsync(int userId)
    {
        var follows = await _supabase.GetAllAsync<Follow>();
        var followingIds = follows
            .Where(f => f.FollowerId == userId)
            .Select(f => f.FollowingId);

        var deals = await _supabase.GetAllAsync<DealNav>();
        return deals
            .Where(d => followingIds.Contains(d.UserId))
            .OrderByDescending(d => d.CreatedAt)
            .Take(50);
    }

    public async Task<Deal> CreateDealAsync(DealProductDTO dto)
    {
        var userId = _authService.GetCurrentUserId();
        if (userId == null) return null;
       

        await EnforceDailySubmissionLimitAsync(Convert.ToInt32(userId));

        // Load deals + deal_product rows for this product
        var allDeals = await _supabase.GetAllAsync<Deal>();
        var allDealProducts = await _supabase.GetAllAsync<DealProduct>();
        var sameProduct = (from dp in allDealProducts
                           join d in allDeals on dp.DealId equals d.Id
                           where dp.ProductId == dto.ProductId && !d.Deleted
                           select new { Deal = d, DP = dp }).ToList();

        var client = _supabase.GetClient();
        Deal? duplicate = null;
        if (dto.DealTypeId == 3)
        {
            //Stacked deal, check existing deal combos for this deal don't already exist for what is submittied in dto

            await EnsureUniqueStackedCombinationAsync(dto.DealIds);
            await ValidateStackedDealsSameStoreAsync(dto.DealIds);
        }
        else
        {
            var priceF = (float)(dto.Price ?? 0m);
            var lo = priceF - 0.01f;
            var hi = priceF + 0.01f;

            // 1) Candidate DealProduct rows for this product near the same price
            var dpResp = await client
                .From<DealProduct>()
                .Filter("product_id", Supabase.Postgrest.Constants.Operator.Equals, dto.ProductId)
                .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")   // pass as string
                .Filter("price", Supabase.Postgrest.Constants.Operator.GreaterThanOrEqual, lo)
                .Filter("price", Supabase.Postgrest.Constants.Operator.LessThanOrEqual, hi)
                .Select("id,deal_id,url,price")
                .Get();

            var candidateDp = dpResp.Models;

            // 2) Load only their deals (not deleted)
            var dealIds = candidateDp.Select(x => x.DealId).Distinct().ToArray();
            var dealsResp = dealIds.Length == 0
                ? null
                : await client.From<Deal>()
                    .Filter("id", Supabase.Postgrest.Constants.Operator.In, dealIds)
                    .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
                    .Select("id,deal_type_id,coupon_code,external_offer_url")
                    .Get();

            var candidateDeals = dealsResp?.Models ?? new List<Deal>();

            // 3) Join in-memory (small set) and compare with NormalizeUrl/PricesEqual
            var targetNormUrl = NormalizeUrl(dto.Url);


            foreach (var dp in candidateDp)
            {
                var d = candidateDeals.FirstOrDefault(x => x.Id == dp.DealId);
                if (d == null) continue;

                var urlMatch = NormalizeUrl(dp.Url) == targetNormUrl;
                var priceMatch = PricesEqual(dp.Price, dto.Price);

                if (dto.DealTypeId == 2 && !string.IsNullOrWhiteSpace(dto.CouponCode))
                {
                    var codeMatch = !string.IsNullOrWhiteSpace(d.CouponCode) &&
                                    string.Equals(d.CouponCode.Trim(), dto.CouponCode.Trim(), StringComparison.OrdinalIgnoreCase);
                    if (urlMatch && priceMatch && codeMatch) { duplicate = d; break; }
                }
                else if (dto.DealTypeId == 4)
                {
                    var domainMatch = DomainEqual(d.ExternalOfferUrl, dto.ExternalOfferUrl);
                    if (urlMatch && priceMatch && domainMatch) { duplicate = d; break; }
                }
                else
                {
                    if (urlMatch && priceMatch) { duplicate = d; break; }
                }
            }
        }

        if (duplicate != null)
            throw new DuplicateDealException("A deal with this URL and price was already submitted for this product.", duplicate.Id);

        // Resolve stores (returns full Store objects)
        var primaryStore = await ResolveOrCreateStoreFromUrlAsync(dto.Url);
        var externalStore = dto.DealTypeId == 4
            ? await ResolveOrCreateStoreFromUrlAsync(dto.ExternalOfferUrl)
            : null;

        // Sanitize URLs using store logic (no extra fetch)
        if (primaryStore != null)
            dto.Url = _urlSanitizer.CleanForStore(dto.Url, primaryStore, injectAffiliate:true);
        else
            dto.Url = _urlSanitizer.Clean(dto.Url, injectAffiliate:true);

        if (dto.DealTypeId == 4)
        {
            if (externalStore != null)
                dto.ExternalOfferUrl = _urlSanitizer.CleanForStore(dto.ExternalOfferUrl, externalStore, injectAffiliate:true);
            else
                dto.ExternalOfferUrl = _urlSanitizer.Clean(dto.ExternalOfferUrl, injectAffiliate:true);
        }
        else
        {
            dto.ExternalOfferUrl = _urlSanitizer.Clean(dto.ExternalOfferUrl, injectAffiliate:true);
        }

        var deal = new Deal
        {
            UserId = Convert.ToInt32(userId),
            DealTypeId = dto.DealTypeId,
            AdditionalDetails = dto.AdditionalDetails,
            CouponCode = string.IsNullOrWhiteSpace(dto.CouponCode) ? null : dto.CouponCode.Trim(),
            DiscountPercent = dto.DiscountPercent,
            ExpirationDate = dto.ExpirationDate,
            ExternalOfferUrl = string.IsNullOrWhiteSpace(dto.ExternalOfferUrl) ? null : dto.ExternalOfferUrl.Trim(),
            CreatedAt = DateTime.UtcNow,
            Deleted = false,
            StoreId = primaryStore?.Id,
            ExternalOfferStoreId = dto.DealTypeId == 4 ? externalStore?.Id : null
        };

        // Admin auto-approve
        var users = await _supabase.GetAllAsync<User>();
        var user = users.FirstOrDefault(u => u.Id == deal.UserId);
        deal.DealStatusId = user?.Admin == true ? 2 : 1;


        var createdDeal = await _supabase.InsertAsync(deal);

        await _supabase.InsertAsync(new DealProduct
        {
            DealId = createdDeal.Id,
            ProductId = dto.ProductId,
            Price = dto.Price ?? 0,
            Url = dto.Url,
            FreeShipping = dto.FreeShipping,
            ConditionId = dto.ConditionId ?? 1,
            CreatedAt = DateTime.UtcNow,
            Deleted = false,
            Primary = true,
            DealStatusId = user?.Admin == true ? 2 : 1
        });

        //If this is an admin user run "f_update_product_best_deal" after inserting the deal
        if (user?.Admin == true)
        {
            await client
                .Rpc("f_update_product_best_deal", new { p_product_id = dto.ProductId });
        }

        // Invalidate cache entries affected by new deal submission
        _cache.Remove("bestDeals");
        _cache.Remove($"product:id:{dto.ProductId}");
        return createdDeal;
    }

    public async Task<List<DealCombo>> CreateDealComboAsync(List<DealCombo> dealCombos, bool? deleteExisting = false)
    {


        if (deleteExisting == true && dealCombos.Count > 0)
        {
            var client = _supabase.GetClient();
            await client.From<DealCombo>()
                .Filter("deal_id", Supabase.Postgrest.Constants.Operator.Equals, dealCombos[0].DealId)
                .Delete();
        }

    var inserted = new List<DealCombo>();
    foreach (var combo in dealCombos)
    {
        var result = await _supabase.InsertAsync(combo);
        inserted.Add(result);
    }
    return inserted;
   
    }

    public async Task<Deal?> UpdateDealAsync(DealProductDTO dto)
    {
        var userId = _authService.GetCurrentUserId();
        var deal = (await _supabase.GetAllAsync<Deal>()).FirstOrDefault(d => d.Id == dto.DealId);
        if (deal == null) return null;
        if (deal.UserId != Convert.ToInt32(userId)) return null;

        //Get product id from dealProductId
        var dealProduct = (await _supabase.GetAllAsync<DealProduct>()).FirstOrDefault(dp => dp.Id == dto.DealProductId);
        if (dealProduct == null) return null;
        dto.ProductId = dealProduct.ProductId;

        // Update Deal fields (shared)
        deal.DealTypeId = dto.DealTypeId;
        deal.AdditionalDetails = dto.AdditionalDetails;
        deal.CouponCode = string.IsNullOrWhiteSpace(dto.CouponCode) ? null : dto.CouponCode.Trim();
        deal.DiscountPercent = dto.DiscountPercent;
        deal.ExpirationDate = dto.ExpirationDate;
        deal.ExternalOfferUrl = string.IsNullOrWhiteSpace(dto.ExternalOfferUrl) ? null : dto.ExternalOfferUrl.Trim();

      

        // Duplicate detection on DealProduct for this product (exclude this dp)
        var allDeals = await _supabase.GetAllAsync<Deal>();
        var allDealProducts = await _supabase.GetAllAsync<DealProduct>();
        var sameProduct = (from x in allDealProducts
                           join d in allDeals on x.DealId equals d.Id
                           where x.ProductId == dto.ProductId && !d.Deleted && d.Id != deal.Id && !x.Deleted
                           select new { Deal = d, DP = x }).ToList();

        var targetNormUrl = NormalizeUrl(dto.Url);
        Deal? duplicate = null;
        if (dto.DealTypeId == 3)
        {
            //Stacked deal, check existing deal combos for this deal don't already exist for what is submittied in dto

            await EnsureUniqueStackedCombinationAsync(dto.DealIds, deal.Id);
            await ValidateStackedDealsSameStoreAsync(dto.DealIds, deal.Id);
        }
        else
        {
            if (deal.DealTypeId == 2 && !string.IsNullOrWhiteSpace(deal.CouponCode))
            {
                var targetCode = deal.CouponCode.Trim();
                duplicate = sameProduct
                    .Where(x => x.Deal.DealTypeId == 2 && !string.IsNullOrWhiteSpace(x.Deal.CouponCode))
                    .FirstOrDefault(x =>
                        string.Equals(x.Deal.CouponCode!.Trim(), targetCode, StringComparison.OrdinalIgnoreCase) &&
                        NormalizeUrl(x.DP.Url) == targetNormUrl &&
                        PricesEqual(x.DP.Price, dto.Price))
                    ?.Deal;
            }
            else if (deal.DealTypeId == 4)
            {
                duplicate = sameProduct
                    .Where(x => x.Deal.DealTypeId == 4)
                    .FirstOrDefault(x =>
                        NormalizeUrl(x.DP.Url) == targetNormUrl &&
                        PricesEqual(x.DP.Price, dto.Price) &&
                        DomainEqual(x.Deal.ExternalOfferUrl, deal.ExternalOfferUrl))
                    ?.Deal;
            }
            else
            {
                duplicate = sameProduct
                    .FirstOrDefault(x =>
                        NormalizeUrl(x.DP.Url) == targetNormUrl &&
                        PricesEqual(x.DP.Price, dto.Price))
                    ?.Deal;
            }
        }
        if (duplicate != null)
            throw new DuplicateDealException("A deal with this URL and price was already submitted for this product.", duplicate.Id);

        // Resolve stores once
        var primaryStore = await ResolveOrCreateStoreFromUrlAsync(dto.Url);
        var externalStore = dto.DealTypeId == 4
            ? await ResolveOrCreateStoreFromUrlAsync(dto.ExternalOfferUrl)
            : null;

        // Sanitize URLs
        if (primaryStore != null)
            dto.Url = _urlSanitizer.CleanForStore(dto.Url, primaryStore, injectAffiliate:true);
        else
            dto.Url = _urlSanitizer.Clean(dto.Url, injectAffiliate:true);

        if (dto.DealTypeId == 4)
        {
            if (externalStore != null)
                dto.ExternalOfferUrl = _urlSanitizer.CleanForStore(dto.ExternalOfferUrl, externalStore, injectAffiliate:true);
            else
                dto.ExternalOfferUrl = _urlSanitizer.Clean(dto.ExternalOfferUrl, injectAffiliate:true);
        }
        else
        {
            dto.ExternalOfferUrl = _urlSanitizer.Clean(dto.ExternalOfferUrl, injectAffiliate:true);
        }

        // Assign store IDs
        if (deal.DealTypeId == 4)
        {
            deal.ExternalOfferStoreId = externalStore?.Id;
            deal.StoreId = primaryStore?.Id;
        }
        else if (dto.DealTypeId != 3)
        {
            deal.StoreId = primaryStore?.Id;
            deal.ExternalOfferStoreId = null;
        }

        // Reset review status unless admin
        var users = await _supabase.GetAllAsync<User>();
        var u = users.FirstOrDefault(x => x.Id == deal.UserId);
        deal.DealStatusId = u?.Admin == true ? 2 : 1;

        var client = _supabase.GetClient();
        await client.From<DealReview>().Where(r => r.DealId == deal.Id && r.DealProductId == dto.DealProductId).Delete();

        var dealUpdate = await _supabase.UpdateAsync(deal);
        if (dealUpdate == null) return null;

        var dealProducts = await _supabase.GetAllAsync<DealProduct>();
        var dp = dealProducts.FirstOrDefault(x => x.DealId == deal.Id && x.ProductId == dto.ProductId && !x.Deleted);
        if (dp == null)
        {
            dp = await _supabase.InsertAsync(new DealProduct
            {
                DealId = deal.Id,
                ProductId = dto.ProductId,
                Price = dto.Price ?? 0,
                Url = dto.Url,
                FreeShipping = dto.FreeShipping,
                ConditionId = dto.ConditionId ?? 1,
                CreatedAt = DateTime.UtcNow,
                Deleted = false,
                DealStatusId = u?.Admin == true ? 2 : 1
            });
        }
        else
        {
            if (dto.Price.HasValue) dp.Price = dto.Price.Value;
            dp.Url = dto.Url;
            dp.FreeShipping = dto.FreeShipping;
            dp.ConditionId = dto.ConditionId ?? dp.ConditionId;
            dp.DealStatusId = u?.Admin == true ? 2 : 1;
            await _supabase.UpdateAsync(dp);
        }

        //If this is an admin user run "f_update_product_best_deal" after inserting the deal
        if (u?.Admin == true)
        {
            await client
                .Rpc("f_update_product_best_deal", new { p_product_id = dto.ProductId });
        }

        // Invalidate cache entries affected by deal update
        _cache.Remove("bestDeals");
        _cache.Remove($"product:id:{dto.ProductId}");
        return dealUpdate;
    }


    private async Task EnsureUniqueStackedCombinationAsync(IEnumerable<int> rawDealIds, int? excludeDealId = null)
    {
        if (rawDealIds == null) throw new ArgumentNullException(nameof(rawDealIds));

        var dealIdList = rawDealIds.ToList();

        if (dealIdList.Count < 2)
            throw new InvalidOperationException("A stacked deal must contain at least two component deals.");

        if (dealIdList.Count != dealIdList.Distinct().Count())
            throw new InvalidOperationException("Duplicate deal ids are not allowed in a stacked deal.");

        var sortedSignature = string.Join(",", dealIdList.OrderBy(i => i));
        var client = _supabase.GetClient();

        // Pull all combo rows whose child (component) id is in the submitted set
        var existingRows = await client
            .From<DealCombo>()
            .Filter("combo_deal_id", Supabase.Postgrest.Constants.Operator.In, dealIdList.Cast<object>().ToArray())
            .Select("deal_id,combo_deal_id")
            .Get();

        if (existingRows.Models.Count == 0) return;

        // Group by parent stacked deal
        var candidateGroups = existingRows.Models
            .GroupBy(c => c.DealId)
            .Where(g => !excludeDealId.HasValue || g.Key != excludeDealId.Value);

        foreach (var g in candidateGroups)
        {
            // Distinct component ids for this existing stacked deal
            var existingComponentIds = g
                .Select(x => x.ComboDealId)
                .Distinct()
                .OrderBy(i => i)
                .ToList();

            // Fetch full set size for that parent to ensure we didn't only partially match
            // (If your table only stores each child once per parent, the count above is sufficient.)
            if (existingComponentIds.Count != dealIdList.Count) continue;

            var existingSignature = string.Join(",", existingComponentIds);
            if (existingSignature == sortedSignature)
                throw new InvalidOperationException("A stacked deal with the same combination of component deals already exists.");
        }
    }

    private async Task ValidateStackedDealsSameStoreAsync(IEnumerable<int> dealIds, int? excludeDealId = null)
    {
        var ids = dealIds?.Distinct().ToList() ?? new List<int>();
        if (ids.Count < 2) throw new InvalidOperationException("A stacked deal must contain at least two component deals.");
        var client = _supabase.GetClient();

        // Load referenced deals (include type)
        var dealsResp = await client
            .From<Deal>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.In, ids.Cast<object>().ToArray())
            .Select("id,store_id,deal_type_id,deleted")
            .Get();

        var componentDeals = dealsResp.Models
            .Where(d => !d.Deleted && (!excludeDealId.HasValue || d.Id != excludeDealId.Value))
            .ToList();

        if (componentDeals.Count != ids.Count)
            throw new InvalidOperationException("One or more selected deals could not be found.");

        // Same-store validation
        var storeIds = componentDeals
            .Select(d => d.StoreId)
            .Distinct()
            .ToList();

        if (storeIds.Count != 1 || storeIds[0] == null)
            throw new InvalidOperationException("All component deals in a stacked deal must come from the same store.");

        // Only one Direct (deal_type_id = 1)
        var directCount = componentDeals.Count(d => (d.DealTypeId ?? 0) == 1);
        if (directCount > 1)
            throw new InvalidOperationException("Only one Direct deal can be included in a stacked deal.");
    }

    public async Task<bool> DeleteDealAsync(int id)
    {
        var userId = _authService.GetCurrentUserId();
        var deal = (await _supabase.GetAllAsync<Deal>()).FirstOrDefault(d => d.Id == id);
        if (deal == null || deal.UserId != Convert.ToInt32(userId)) return false;

        deal.Deleted = true; // flag deal as deleted
        await _supabase.UpdateAsync(deal);

        return true;
    }

    public async Task<bool> FlagDealAsync(int dealProductId, int? dealIssueTypeId, string? comments)
    {
        var userId = _authService.GetCurrentUserId();
        if (userId == null) return false;

        // Upsert logic (prevent duplicate flag by same user on same deal_product)
        var flags = await _supabase.GetAllAsync<DealFlag>();
        var existing = flags.FirstOrDefault(f =>
            f.UserId == Convert.ToInt32(userId) &&
            f.DealProductId == dealProductId);

        if (existing == null)
        {
            //User has not flagged this deal before, create new flag
            var newFlag = new DealFlag
            {
                UserId = Convert.ToInt32(userId),
                DealProductId = dealProductId,
                CreatedAt = DateTime.UtcNow,
                DealIssueTypeId = dealIssueTypeId,
                Comments = string.IsNullOrWhiteSpace(comments) ? null : comments!.Trim()
            };
            await _supabase.InsertAsync(newFlag);
        }

        // Possible requeue
        var client = _supabase.GetClient();
        await client.Rpc<int>("f_maybe_requeue_deal", new { p_deal_product_id = dealProductId });
        

        return true;
    }

    public async Task<bool> ReviewDealAsync(int dealId, int dealProductId, int dealStatusId, int? dealIssueTypeId, string? comment)
    {
        var userId = _authService.GetCurrentUserId();
        if (userId == null) return false;

        var dealReview = new DealReview
        {
            UserId = Convert.ToInt32(userId),
            DealId = dealId,
            DealProductId = dealProductId,
            DealStatusId = dealStatusId,
            Comments = string.IsNullOrWhiteSpace(comment) ? null : comment!.Trim(),
            CreatedAt = DateTime.UtcNow,
            DealIssueTypeId = dealIssueTypeId
        };

        await _supabase.InsertAsync(dealReview);

        // Recompute aggregate status
        var client = _supabase.GetClient();
        await client.Rpc<int>("f_update_deal_status", new { p_deal_product_id = dealProductId });

        return true;
        }
    
    private static int GetDailyLimitForScore(int score)
    {
        if (score >= 80) return 20;
        if (score >= 60) return 10;
        if (score >= 40) return 5;
        if (score >= 20) return 2;
        return 1;
    }

    private async Task EnforceDailySubmissionLimitAsync(int userId)
    {
        // Admin bypass
        var users = await _supabase.GetAllAsync<User>();
        var user = users.FirstOrDefault(u => u.Id == userId);
        if (user?.Admin == true) return;

        // Get user trust-based limit
        var score = user.Level;
        var limit = GetDailyLimitForScore(score);

        // Count today's submissions (UTC)
        var since = DateTime.UtcNow.Date;
        var dealsToday = (await _supabase.GetAllAsync<Deal>())
            .Count(d => d.UserId == userId
                     && d.CreatedAt >= since
                     && !d.Deleted);

        if (dealsToday >= limit)
            throw new DealSubmissionLimitException(
                $"You've reached your daily deal submission limit of {limit}. Try again tomorrow.",
                limit,
                dealsToday);
    }

}