using CartSmart.API.Models;
using Microsoft.Extensions.Logging;
using Supabase.Postgrest.Exceptions;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AttributeModel = CartSmart.API.Models.Attribute;

namespace CartSmart.API.Services;

public class SocialPostService : ISocialPostService
{
    private const int DealStatusActive = 2;
    private const int DailyPostCount = 2;

    private readonly ISupabaseService _supabase;
    private readonly IEnumerable<ISocialMediaPoster> _posters;
    private readonly IUrlSanitizer _urlSanitizer;
    private readonly HttpClient _http;
    private readonly ILogger<SocialPostService> _logger;
    private readonly ISocialCardImageService _cardImageService;
    private readonly string _openAiApiKey;
    private readonly string _openAiModel;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SocialPostService(
        ISupabaseService supabase,
        IEnumerable<ISocialMediaPoster> posters,
        IUrlSanitizer urlSanitizer,
        HttpClient http,
        ILogger<SocialPostService> logger,
        ISocialCardImageService cardImageService)
    {
        _supabase = supabase;
        _posters = posters;
        _urlSanitizer = urlSanitizer;
        _http = http;
        _logger = logger;
        _cardImageService = cardImageService;
        _openAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
        _openAiModel = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini";
    }

    // ── Query ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SocialPostDto>> GetPostsAsync(
        string? status, int page, int limit, CancellationToken ct = default)
    {
        var client = _supabase.GetServiceRoleClient();
        var query = client.From<SocialPost>()
            .Order("created_at", Supabase.Postgrest.Constants.Ordering.Descending)
            .Limit(Math.Clamp(limit, 1, 100))
            .Offset(Math.Max(0, page) * Math.Clamp(limit, 1, 100));

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Filter("status", Supabase.Postgrest.Constants.Operator.Equals, status);

        var postsResp = await query.Get();
        var posts = postsResp.Models ?? [];

        if (posts.Count == 0)
            return [];

        var postIds = posts.Select(p => (object)p.Id).ToArray();
        var captionsResp = await client.From<SocialPostCaption>()
            .Filter("social_post_id", Supabase.Postgrest.Constants.Operator.In, postIds)
            .Get();

        var captionsByPostId = (captionsResp.Models ?? [])
            .GroupBy(c => c.SocialPostId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var detailsByPostId = await BuildDealDetailsByPostIdAsync(posts, ct);

        return posts.Select(p => ToDto(
            p,
            captionsByPostId.GetValueOrDefault(p.Id) ?? [],
            detailsByPostId.GetValueOrDefault(p.Id))).ToList();
    }

    public async Task<SocialPostDto?> GetPostAsync(long id, CancellationToken ct = default)
    {
        var client = _supabase.GetServiceRoleClient();

        var postResp = await client.From<SocialPost>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
            .Single();

        if (postResp == null) return null;

        var captionsResp = await client.From<SocialPostCaption>()
            .Filter("social_post_id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
            .Order("id", Supabase.Postgrest.Constants.Ordering.Ascending)
            .Get();

        var detailsByPostId = await BuildDealDetailsByPostIdAsync([postResp], ct);

        return ToDto(postResp, captionsResp.Models ?? [], detailsByPostId.GetValueOrDefault(postResp.Id));
    }

    // ── Approval / Rejection ──────────────────────────────────────────────

    public async Task<bool> ApproveAsync(long id, long? captionId, string? adminNotes, CancellationToken ct = default)
    {
        var client = _supabase.GetServiceRoleClient();

        // If captionId provided, mark it selected and deselect others
        if (captionId.HasValue)
        {
            var allCaptions = (await client.From<SocialPostCaption>()
                .Filter("social_post_id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
                .Get()).Models ?? [];

            foreach (var cap in allCaptions)
            {
                cap.Selected = cap.Id == captionId.Value;
                await client.From<SocialPostCaption>().Upsert(cap);
            }
        }

        // Update post status
        var post = await client.From<SocialPost>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
            .Single();

        if (post == null) return false;

        post.Status = "approved";
        if (!string.IsNullOrWhiteSpace(adminNotes))
            post.AdminNotes = adminNotes;

        await client.From<SocialPost>().Upsert(post);
        return true;
    }

    public async Task<bool> RejectAsync(long id, string? adminNotes, CancellationToken ct = default)
    {
        var client = _supabase.GetServiceRoleClient();

        var post = await client.From<SocialPost>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
            .Single();

        if (post == null) return false;

        post.Status = "rejected";
        if (!string.IsNullOrWhiteSpace(adminNotes))
            post.AdminNotes = adminNotes;

        await client.From<SocialPost>().Upsert(post);
        return true;
    }

    public async Task<bool> UpdateCaptionAsync(long postId, long captionId, string newText, CancellationToken ct = default)
    {
        var client = _supabase.GetServiceRoleClient();

        var caption = await client.From<SocialPostCaption>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, captionId.ToString())
            .Filter("social_post_id", Supabase.Postgrest.Constants.Operator.Equals, postId.ToString())
            .Single();

        if (caption == null) return false;

        caption.CaptionText = newText;
        await client.From<SocialPostCaption>().Upsert(caption);
        return true;
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var client = _supabase.GetServiceRoleClient();
        var post = await client.From<SocialPost>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
            .Single();

        if (post == null) return false;

        await client.From<SocialPost>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
            .Delete();

        return true;
    }

    // ── Post Now ──────────────────────────────────────────────────────────

    public async Task<PostNowResult> PostNowAsync(long id, CancellationToken ct = default)
    {
        var post = await GetPostAsync(id, ct);
        if (post == null)
            return new PostNowResult(false, []);

        if (post.Status != "approved")
            return new PostNowResult(false, [new PlatformResult("all", false, false)]);

        // Find the selected caption, fall back to first available
        var caption = post.Captions.FirstOrDefault(c => c.Selected)
                   ?? post.Captions.FirstOrDefault();

        if (caption == null)
            return new PostNowResult(false, []);

        var platformResults = new List<PlatformResult>();

        foreach (var poster in _posters)
        {
            if (!poster.IsConfigured)
            {
                platformResults.Add(new PlatformResult(poster.Platform, false, Skipped: true));
                continue;
            }

            // Build platform-specific caption
            var platformCaption = BuildPlatformCaption(caption.CaptionText, post.DealUrl, poster.Platform);

            var success = await poster.PostAsync(platformCaption, post.ProductImage, post.DealUrl, ct);
            platformResults.Add(new PlatformResult(poster.Platform, success, Skipped: false));
        }

        // Mark posted if at least one platform succeeded
        var anySuccess = platformResults.Any(r => r.Success);
        if (anySuccess)
        {
            var client = _supabase.GetServiceRoleClient();
            var entity = await client.From<SocialPost>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, id.ToString())
                .Single();

            if (entity != null)
            {
                entity.Status = "posted";
                entity.PostedAt = DateTime.UtcNow;
                await client.From<SocialPost>().Upsert(entity);
            }
        }

        return new PostNowResult(anySuccess, platformResults);
    }

    // ── Generation ────────────────────────────────────────────────────────

    public async Task<int> GenerateDailyPostsAsync(SocialPostGenerationOptions? options = null, CancellationToken ct = default)
    {
        var client = _supabase.GetServiceRoleClient();
        var today = DateTime.UtcNow.Date;
        var targetCount = Math.Clamp(options?.Count ?? DailyPostCount, 1, 10);
        var maxPerProductPerDay = Math.Clamp(options?.MaxPerProductPerDay ?? 1, 1, 10);
        var cutoffUtc = DateTime.UtcNow.AddHours(-24);

        var includeDealIds = (options?.DealIds ?? []).Distinct().ToHashSet();
        var includeProductIds = (options?.ProductIds ?? []).Distinct().ToHashSet();
        var priorityDealIds = (options?.PriorityDealIds ?? []).Distinct().ToHashSet();
        var priorityProductIds = (options?.PriorityProductIds ?? []).Distinct().ToHashSet();
        var excludedDealIds = (options?.ExcludedDealIds ?? []).Distinct().ToHashSet();
        var excludedProductIds = (options?.ExcludedProductIds ?? []).Distinct().ToHashSet();

        // Fetch top active deals with product info
        var dealProductsResp = await client.From<DealProduct>()
            .Select("id, deal_id, product_id, price, url, deal_status_id, condition_id, item_count")
            .Filter("deal_status_id", Supabase.Postgrest.Constants.Operator.Equals, DealStatusActive.ToString())
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Filter("price", Supabase.Postgrest.Constants.Operator.GreaterThan, "0")
            .Order("price", Supabase.Postgrest.Constants.Ordering.Ascending)
            .Limit(500)
            .Get();

        var dealProducts = (dealProductsResp.Models ?? [])
            .Where(dp => excludedDealIds.Count == 0 || !excludedDealIds.Contains(dp.DealId))
            .Where(dp => excludedProductIds.Count == 0 || !excludedProductIds.Contains(dp.ProductId))
            .Where(dp => includeDealIds.Count == 0 || includeDealIds.Contains(dp.DealId))
            .Where(dp => includeProductIds.Count == 0 || includeProductIds.Contains(dp.ProductId))
            .ToList();

        if (dealProducts.Count == 0)
        {
            _logger.LogInformation("GenerateDailyPosts: no active deal products found");
            return 0;
        }

        // Fetch deals to get discount_percent
        var dealIds = dealProducts.Select(dp => (object)dp.DealId).Distinct().ToArray();
        var dealsResp = await client.From<Deal>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.In, dealIds)
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Get();

        var dealById = (dealsResp.Models ?? []).ToDictionary(d => d.Id);

        var storeIds = (dealsResp.Models ?? [])
            .Where(d => d.StoreId.HasValue)
            .Select(d => (object)d.StoreId!.Value)
            .Distinct()
            .ToArray();
        var storeById = new Dictionary<int, Store>();
        if (storeIds.Length > 0)
        {
            var storesResp = await client.From<Store>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.In, storeIds)
                .Get();
            storeById = (storesResp.Models ?? []).ToDictionary(s => s.Id);
        }

        // Fetch products
        var productIds = dealProducts.Select(dp => (object)dp.ProductId).Distinct().ToArray();
        var productsResp = await client.From<Product>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.In, productIds)
            .Get();

        var productById = (productsResp.Models ?? []).ToDictionary(p => p.Id);

        // Keep the 24-hour product cap behavior.
        var recentResp = await client.From<SocialPost>()
            .Filter("created_at", Supabase.Postgrest.Constants.Operator.GreaterThan, cutoffUtc.ToString("o"))
            .Filter("status", Supabase.Postgrest.Constants.Operator.NotEqual, "rejected")
            .Get();

        var recentPosts = recentResp.Models ?? [];

        // Match the DB uniqueness rule exactly: one non-rejected post per (deal_id, scheduled_date).
        var scheduledDateStr = today.ToString("yyyy-MM-dd");
        var scheduledResp = await client.From<SocialPost>()
            .Filter("scheduled_date", Supabase.Postgrest.Constants.Operator.Equals, scheduledDateStr)
            .Filter("status", Supabase.Postgrest.Constants.Operator.NotEqual, "rejected")
            .Get();

        var scheduledPosts = scheduledResp.Models ?? [];
        var alreadyPostedDealIds = scheduledPosts.Select(sp => sp.DealId).ToHashSet();
        var productPostCountToday = recentPosts
            .GroupBy(sp => sp.ProductId)
            .ToDictionary(g => g.Key, g => g.Count());

        var candidates = dealProducts
            .Where(dp => !alreadyPostedDealIds.Contains(dp.DealId)
                      && dealById.ContainsKey(dp.DealId)
                      && productById.ContainsKey(dp.ProductId))
            .Select(dp => new
            {
                DealProduct = dp,
                Deal = dealById[dp.DealId],
                Product = productById[dp.ProductId],
                DiscountPercent = dealById[dp.DealId].DiscountPercent ?? 0,
                PriorityBoost = (priorityDealIds.Contains(dp.DealId) ? 2 : 0)
                              + (priorityProductIds.Contains(dp.ProductId) ? 1 : 0)
            })
            .OrderByDescending(x => x.PriorityBoost)
            .ThenByDescending(x => x.DiscountPercent)
            .ThenBy(x => x.DealProduct.Price)
            .ToList();

        if (candidates.Count == 0)
        {
            _logger.LogInformation("GenerateDailyPosts: no new candidates found for {Date}", today);
            return 0;
        }

        // Pre-fetch deal details (combo steps, coupon codes, deal type names) for caption generation
        var candidateDealsList = candidates.Select(c => c.Deal).ToList();
        var dealDetailsByDealId = await BuildDealDetailsForGenerationAsync(candidateDealsList, ct);

        var created = 0;
        foreach (var c in candidates)
        {
            if (created >= targetCount) break;

            var currentProductCount = productPostCountToday.GetValueOrDefault(c.DealProduct.ProductId, 0);
            if (currentProductCount >= maxPerProductPerDay)
                continue;

            var msrp = c.Product.MSRP;
            var originalPrice = msrp.HasValue ? (decimal)msrp.Value : (decimal?)null;
            var rawUrl = c.DealProduct.Url ?? string.Empty;
            var dealUrl = rawUrl;
            if (c.Deal.StoreId.HasValue && storeById.TryGetValue(c.Deal.StoreId.Value, out var store))
                dealUrl = _urlSanitizer.CleanForStore(rawUrl, store, injectAffiliate: true) ?? rawUrl;
            else
                dealUrl = _urlSanitizer.Clean(rawUrl, injectAffiliate: true) ?? rawUrl;

            // Generate captions via OpenAI
            var dealDetails = dealDetailsByDealId.GetValueOrDefault(c.Deal.Id);
            var shareUrl = BuildShareUrl(c.Deal.DealTypeId, dealUrl, c.Product.Slug, c.Deal.Id);
            var captions = await GenerateCaptionsAsync(
                c.Product.Name ?? "Golf Gear",
                c.DealProduct.Price,
                originalPrice,
                shareUrl,
                dealDetails,
                ct);

            // Insert social_post
            var post = new SocialPost
            {
                DealId = c.DealProduct.DealId,
                ProductId = c.DealProduct.ProductId,
                ProductName = c.Product.Name ?? "Golf Gear",
                ProductImage = c.Product.ImageUrl,
                CurrentPrice = c.DealProduct.Price,
                OriginalPrice = originalPrice,
                DealUrl = dealUrl,
                Status = "pending_approval",
                ScheduledDate = today,
                IsWeekly = false
            };

            SocialPost? inserted = null;
            try
            {
                var postResp = await client.From<SocialPost>().Insert(post);
                inserted = postResp.Models?.FirstOrDefault();
            }
            catch (PostgrestException ex) when (ex.Message.Contains("\"code\":\"23505\"", StringComparison.Ordinal)
                || ex.Message.Contains("23505", StringComparison.Ordinal))
            {
                // Concurrent or repeated generation can race on the unique index; skip safely.
                alreadyPostedDealIds.Add(c.DealProduct.DealId);
                _logger.LogInformation(
                    "GenerateDailyPosts: duplicate (deal_id, scheduled_date) for deal {DealId} on {ScheduledDate}; skipping.",
                    c.DealProduct.DealId,
                    scheduledDateStr);
                continue;
            }

            if (inserted == null)
            {
                _logger.LogWarning("GenerateDailyPosts: failed to insert post for deal {DealId}", c.Deal.Id);
                continue;
            }

            // Insert caption variations
            for (var i = 0; i < captions.Count; i++)
            {
                var caption = new SocialPostCaption
                {
                    SocialPostId = inserted.Id,
                    CaptionText = captions[i],
                    Platform = "all",
                    Selected = i == 0   // first caption is pre-selected
                };
                await client.From<SocialPostCaption>().Insert(caption);
            }

            // Generate deal card image and persist the base64 data-URI
            var cardData = new SocialCardData(
                ProductName:    post.ProductName ?? string.Empty,
                ProductImageUrl: post.ProductImage,
                CurrentPrice:   post.CurrentPrice,
                OriginalPrice:  post.OriginalPrice,
                DealTypeId:     dealDetails?.DealTypeId,
                DealTypeName:   dealDetails?.DealTypeName,
                CouponCode:     dealDetails?.CouponCode,
                StoreName:      dealDetails?.StoreName,
                StoreImageUrl:  dealDetails?.StoreImageUrl,
                ConditionName:  dealDetails?.ConditionName,
                VariantDetails: dealDetails?.VariantDetails,
                ItemCount:      dealDetails?.ItemCount,
                FreeShipping:   dealDetails?.FreeShipping ?? false);

            var cardBytes = await _cardImageService.GenerateAsync(cardData, ct);
            if (cardBytes is { Length: > 0 })
            {
                var dataUri = "data:image/png;base64," + Convert.ToBase64String(cardBytes);
                inserted.ImageUrl = dataUri;
                await client.From<SocialPost>().Upsert(inserted);
                _logger.LogInformation("GenerateDailyPosts: card image generated for post {PostId}", inserted.Id);
            }

            created++;
            productPostCountToday[c.DealProduct.ProductId] = currentProductCount + 1;
            _logger.LogInformation("GenerateDailyPosts: created post {PostId} for product '{Name}'",
                inserted.Id, post.ProductName);
        }

        return created;
    }

    public async Task<bool> GenerateWeeklyDigestAsync(CancellationToken ct = default)
    {
        var client = _supabase.GetServiceRoleClient();
        var today = DateTime.UtcNow.Date;

        // Pull the best candidate per day from the last 7 days
        var dealProductsResp = await client.From<DealProduct>()
            .Select("id, deal_id, product_id, price, url, deal_status_id, item_count")
            .Filter("deal_status_id", Supabase.Postgrest.Constants.Operator.Equals, DealStatusActive.ToString())
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Filter("price", Supabase.Postgrest.Constants.Operator.GreaterThan, "0")
            .Limit(200)
            .Get();

        var dealProducts = dealProductsResp.Models ?? [];
        if (dealProducts.Count == 0) return false;

        var dealIds = dealProducts.Select(dp => (object)dp.DealId).Distinct().ToArray();
        var dealsResp = await client.From<Deal>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.In, dealIds)
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Get();
        var dealById = (dealsResp.Models ?? []).ToDictionary(d => d.Id);

        var productIds = dealProducts.Select(dp => (object)dp.ProductId).Distinct().ToArray();
        var productsResp = await client.From<Product>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.In, productIds)
            .Get();
        var productById = (productsResp.Models ?? []).ToDictionary(p => p.Id);

        // Pick unique products by best discount (highest first), then lowest price as tie-breaker.
        var topDeals = dealProducts
            .Where(dp => dealById.ContainsKey(dp.DealId) && productById.ContainsKey(dp.ProductId))
            .Select(dp => new
            {
                DealProduct = dp,
                Deal = dealById[dp.DealId],
                Product = productById[dp.ProductId],
                Discount = dealById[dp.DealId].DiscountPercent ?? 0
            })
            .GroupBy(x => x.Product.Id)
            .Select(g => g
                .OrderByDescending(x => x.Discount)
                .ThenBy(x => x.DealProduct.Price)
                .First())
            .OrderByDescending(x => x.Discount)
            .ThenBy(x => x.DealProduct.Price)
            .Take(5)
            .ToList();

        if (topDeals.Count == 0) return false;

        // Build a weekly digest caption
        var lines = topDeals.Select((d, i) =>
        {
            var name = d.Product.Name ?? "Golf Gear";
            var price = d.DealProduct.Price;
            var discount = d.Discount > 0 ? $" ({d.Discount}% off)" : string.Empty;
            return $"{i + 1}. {name} — ${price:F2}{discount}";
        });

        var digestCaption = "🏌️ Best Golf Deals This Week 🏌️\n\n"
                          + string.Join("\n", lines)
                          + "\n\n👉 See all deals at cartsmart.com";

        // Insert as weekly post
        var post = new SocialPost
        {
            DealId = topDeals[0].DealProduct.DealId,
            ProductId = topDeals[0].DealProduct.ProductId,
            ProductName = "Best Golf Deals This Week",
            ProductImage = topDeals[0].Product.ImageUrl,
            CurrentPrice = topDeals[0].DealProduct.Price,
            DealUrl = "https://cartsmart.com",
            Status = "pending_approval",
            ScheduledDate = today,
            IsWeekly = true
        };

        var postResp = await client.From<SocialPost>().Insert(post);
        var inserted = postResp.Models?.FirstOrDefault();
        if (inserted == null) return false;

        await client.From<SocialPostCaption>().Insert(new SocialPostCaption
        {
            SocialPostId = inserted.Id,
            CaptionText = digestCaption,
            Platform = "all",
            Selected = true
        });

        _logger.LogInformation("GenerateWeeklyDigest: created weekly digest post {PostId}", inserted.Id);
        return true;
    }

    // ── Caption Generation ────────────────────────────────────────────────

    private async Task<IReadOnlyList<string>> GenerateCaptionsAsync(
        string productName,
        decimal currentPrice,
        decimal? originalPrice,
        string dealUrl,
        SocialDealDetailsDto? dealDetails,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_openAiApiKey))
        {
            _logger.LogWarning("OPENAI_API_KEY not configured — using fallback captions");
            return BuildFallbackCaptions(productName, currentPrice, originalPrice, dealDetails, dealUrl);
        }

        var pricingContext = BuildPricingContext(currentPrice, originalPrice);
        var flavor = GetProductFlavor(productName);
        var dealKind = GetDealKindLabel(dealDetails?.DealTypeId);
        var linkLabel = dealDetails?.DealTypeId is null or 1 ? "Product link" : "More details";

        var (angleName, angleInstruction) = GetCaptionAngle();

        var systemPrompt = $"""
            You write short, witty social media captions for a golf deals website.
            Tone: casual, punchy, fun — like a golfer texting a friend about a steal they just found.
            No numbered steps. No "how to get it" section. No long instructions.
            Avoid hype like "amazing", "incredible", "must-have", "act now", "limited time".
            Product accuracy rules:
            - Keep the caption anchored to the provided product name and product context.
            - Do not invent product features, specs, or categories not implied by the product name/context.
            - Do not confuse product types (example: do not describe a putter like a driver).
            Today's angle is "{angleName}": {angleInstruction}
            Every caption MUST start with exactly: Deal of the Day:
            Each caption should include:
            - one witty remark about the product or deal, following today's angle
            - basic price/savings info
            - exactly one link line using the provided link label and URL
            - no hashtags
            Return valid JSON with a "captions" key mapping to an array of exactly 3 caption strings.
            """;

        var userPrompt = $"""
            Product: {productName}
            Price summary: {pricingContext}
            Product context: {flavor}
            Deal type: {dealKind}
            Link label: {linkLabel}
            URL: {dealUrl}
            Angle for today: {angleName} — {angleInstruction}
            Write 3 caption variations. Each must start with "Deal of the Day:".
            """;

        var body = new
        {
            model = _openAiModel,
            max_completion_tokens = 512,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = userPrompt }
            }
        };

        try
        {
            using var req = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Post,
                "https://api.openai.com/v1/chat/completions");

            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", _openAiApiKey);
            req.Content = JsonContent.Create(body, options: JsonOpts);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("OpenAI caption API error {Status}: {Body}", resp.StatusCode, err);
                return BuildFallbackCaptions(productName, currentPrice, originalPrice, dealDetails, dealUrl);
            }

            var rawJson = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(rawJson);

            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "{}";

            using var inner = JsonDocument.Parse(content);
            if (inner.RootElement.TryGetProperty("captions", out var captionsEl))
            {
                var list = new List<string>();
                foreach (var el in captionsEl.EnumerateArray())
                {
                    var text = el.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var cleaned = RemoveHashtagTokens(text);
                        cleaned = EnsureDealOfTheDayPrefix(cleaned);
                        if (!string.IsNullOrWhiteSpace(cleaned))
                            list.Add(cleaned);
                    }
                }
                if (list.Count > 0) return list;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception generating captions from OpenAI");
        }

        return BuildFallbackCaptions(productName, currentPrice, originalPrice, dealDetails, dealUrl);
    }

    private static IReadOnlyList<string> BuildFallbackCaptions(
        string productName, decimal currentPrice, decimal? originalPrice, SocialDealDetailsDto? dealDetails, string dealUrl)
    {
        var priceHeader = BuildPriceHeader(productName, currentPrice, originalPrice);
        var linkLabel = dealDetails?.DealTypeId is null or 1 ? "Product link" : "More details";
        var linkLine = $"{linkLabel}: {dealUrl}";

        return
        [
            $"Deal of the Day: {priceHeader}\n{GetProductFlavor(productName)}\n{linkLine}",
            $"Deal of the Day: {priceHeader}\nMy golf group chat would roast me if I didn't share this.\n{linkLine}",
            $"Deal of the Day: {priceHeader}\nNot a miracle cure for my game, but this price is real.\n{linkLine}"
        ];
    }

    private static (string Name, string Instruction) GetCaptionAngle()
    {
        return (DateTime.UtcNow.DayOfYear % 5) switch
        {
            0 => ("Price Drop",    "Lead with the savings — make the reader feel like they're getting away with something. Add a witty money joke."),
            1 => ("Perfect For",   "Describe the specific type of golfer who needs this. Be playful about who the buyer is."),
            2 => ("On The Course", "Put the product in a real on-course moment. Make it feel situational and relatable."),
            3 => ("Vs. Full Price","Compare paying this deal price to paying full retail. Make paying full price sound almost embarrassing."),
            _ => ("Quick Take",    "One sharp, punchy verdict about the deal. Short, confident, makes someone want to click.")
        };
    }

    private static string EnsureDealOfTheDayPrefix(string caption)
    {
        const string prefix = "Deal of the Day:";
        if (string.IsNullOrWhiteSpace(caption)) return caption;
        return caption.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? caption
            : $"{prefix} {caption}";
    }

    private static string RemoveHashtagTokens(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.None);
        var cleaned = lines
            .Select(line => string.Join(' ', line
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(token => !token.StartsWith('#')))
                .Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line));

        return string.Join("\n", cleaned).Trim();
    }

    private static string GetDealKindLabel(int? dealTypeId) => dealTypeId switch
    {
        1 or null => "Direct deal",
        2 => "Coupon deal",
        3 => "Stacked deal",
        4 => "External offer deal",
        _ => "Deal"
    };

    private static string BuildShareUrl(int? dealTypeId, string directDealUrl, string? productSlug, int dealId)
    {
        if (dealTypeId is null or 1)
            return directDealUrl;

        return BuildCartSmartDealUrl(productSlug, dealId);
    }

    private static string BuildCartSmartDealUrl(string? productSlug, int dealId)
    {
        var baseUrl = Environment.GetEnvironmentVariable("CARTSMART_SITE_URL")
            ?? Environment.GetEnvironmentVariable("REACT_APP_SITE_URL")
            ?? "https://cartsmart.com";
        baseUrl = baseUrl.TrimEnd('/');

        if (string.IsNullOrWhiteSpace(productSlug))
            return $"{baseUrl}/?dealId={dealId}";

        return $"{baseUrl}/products/{Uri.EscapeDataString(productSlug)}?dealId={dealId}";
    }

    private static string BuildDealStepsContext(SocialDealDetailsDto? details, string primaryDealUrl)
    {
        if (details == null || details.DealTypeId is null or 1)
            return $"Direct deal — visitor goes to the site and purchases at the listed price. Link: {primaryDealUrl}";

        var sb = new System.Text.StringBuilder();
        if (details.DealTypeId == 2)
        {
            sb.Append("Coupon deal.");
            if (!string.IsNullOrWhiteSpace(details.CouponCode))
                sb.Append($" Apply coupon code \"{details.CouponCode}\" at checkout.");
            else
                sb.Append(" Coupon is automatically applied — no code needed.");
            if (!string.IsNullOrWhiteSpace(details.AdditionalDetails))
                sb.Append($" Additional info: {details.AdditionalDetails}");
            sb.Append($" Checkout link: {primaryDealUrl}");
        }
        else if (details.Steps.Count > 0)
        {
            var label = details.DealTypeId == 4 ? "External/stacked" : "Stacked";
            sb.AppendLine($"{label} deal with {details.Steps.Count} step(s):");
            for (var i = 0; i < details.Steps.Count; i++)
            {
                var step = details.Steps[i];
                sb.Append($"  Step {step.StepNumber}:");
                if (!string.IsNullOrWhiteSpace(step.AdditionalDetails))
                    sb.Append($" {step.AdditionalDetails}");
                if (!string.IsNullOrWhiteSpace(step.CouponCode))
                    sb.Append($" Coupon: {step.CouponCode}.");
                if (!string.IsNullOrWhiteSpace(step.ExternalStoreName))
                    sb.Append($" Via {step.ExternalStoreName}.");
                var stepUrl = ResolveStepActionUrl(details, step, i, primaryDealUrl);
                sb.Append($" Link: {stepUrl}");
                sb.AppendLine();
            }
            if (ShouldAppendPurchaseStep(details, primaryDealUrl))
                sb.AppendLine($"  Step {details.Steps.Count + 1}: Complete the purchase on the product page. Link: {primaryDealUrl}");

            if (!string.IsNullOrWhiteSpace(details.AdditionalDetails))
                sb.Append($"Overall: {details.AdditionalDetails}");
        }
        else
        {
            sb.Append($"Direct deal — visitor goes to the site and purchases at the listed price. Link: {primaryDealUrl}");
            if (!string.IsNullOrWhiteSpace(details.AdditionalDetails))
                sb.Append($" Additional info: {details.AdditionalDetails}");
        }

        return sb.ToString();
    }

    private static string BuildFallbackStepsText(SocialDealDetailsDto? details, string defaultDealUrl)
    {
        if (details == null || details.DealTypeId is null or 1)
            return $"1. Click and purchase at the listed price: {defaultDealUrl}";

        if (details.DealTypeId == 2)
        {
            return !string.IsNullOrWhiteSpace(details.CouponCode)
                ? $"1. Open deal link and add to cart: {defaultDealUrl}\n2. Apply coupon code: {details.CouponCode} at checkout"
                : $"1. Open deal link and add to cart: {defaultDealUrl}\n2. Coupon auto-applies at checkout";
        }

        if (details.Steps.Count > 0)
        {
            var lines = details.Steps.Select((s, idx) => BuildFallbackStepLine(details, s, idx, defaultDealUrl)).ToList();
            if (ShouldAppendPurchaseStep(details, defaultDealUrl))
                lines.Add($"{details.Steps.Count + 1}. Complete the purchase on the product page | Link: {defaultDealUrl}");

            return string.Join("\n", lines);
        }

        if (!string.IsNullOrWhiteSpace(details.AdditionalDetails))
            return $"1. {details.AdditionalDetails} | Link: {defaultDealUrl}";

        return $"1. Click the link to get this deal: {defaultDealUrl}";
    }

    private static string BuildFallbackStepLine(
        SocialDealDetailsDto details,
        SocialDealStepDto step,
        int stepIndex,
        string defaultDealUrl)
    {
        var line = $"{step.StepNumber}.";
        if (!string.IsNullOrWhiteSpace(step.AdditionalDetails))
            line += $" {step.AdditionalDetails}";
        if (!string.IsNullOrWhiteSpace(step.CouponCode))
            line += $" (Use code: {step.CouponCode})";
        if (!string.IsNullOrWhiteSpace(step.ExternalStoreName))
            line += $" via {step.ExternalStoreName}";

        var stepUrl = ResolveStepActionUrl(details, step, stepIndex, defaultDealUrl);
        line += $" | Link: {stepUrl}";
        return line;
    }

    private static bool ShouldAppendPurchaseStep(SocialDealDetailsDto details, string primaryDealUrl)
    {
        return details.DealTypeId is 3 or 4
            && details.Steps.Count > 0
            && !string.IsNullOrWhiteSpace(primaryDealUrl);
    }

    private static string BuildPriceHeader(string productName, decimal currentPrice, decimal? originalPrice)
    {
        var priceBits = new List<string> { $"{productName} — ${currentPrice:F2}" };
        if (originalPrice.HasValue && originalPrice.Value > currentPrice)
        {
            var savingsAmount = originalPrice.Value - currentPrice;
            var savingsPercent = originalPrice.Value > 0
                ? Math.Round((savingsAmount / originalPrice.Value) * 100m)
                : 0m;

            priceBits.Add($"MSRP ${originalPrice.Value:F2}");
            priceBits.Add($"Save ${savingsAmount:F2} ({savingsPercent:F0}%)");
        }

        return string.Join(" | ", priceBits);
    }

    private static string BuildPricingContext(decimal currentPrice, decimal? originalPrice)
    {
        if (!originalPrice.HasValue || originalPrice.Value <= currentPrice)
            return $"Current price ${currentPrice:F2}. MSRP unavailable.";

        var savingsAmount = originalPrice.Value - currentPrice;
        var savingsPercent = originalPrice.Value > 0
            ? Math.Round((savingsAmount / originalPrice.Value) * 100m)
            : 0m;

        return $"Current price ${currentPrice:F2}. MSRP ${originalPrice.Value:F2}. Savings ${savingsAmount:F2} ({savingsPercent:F0}%).";
    }

    private static string ResolveStepActionUrl(
        SocialDealDetailsDto details,
        SocialDealStepDto step,
        int stepIndex,
        string primaryDealUrl)
    {
        if (!string.IsNullOrWhiteSpace(step.ExternalOfferUrl))
            return step.ExternalOfferUrl;

        if (!string.IsNullOrWhiteSpace(step.DealUrl))
            return step.DealUrl;

        return primaryDealUrl;
    }

    private static string GetProductFlavor(string productName)
    {
        var n = (productName ?? string.Empty).ToLowerInvariant();
        if (n.Contains("driver")) return "Driver deal that might finally beat my usual fairway miss.";
        if (n.Contains("putter")) return "If I still three-putt, at least I got a deal on the putter.";
        if (n.Contains("ball") || n.Contains("balls")) return "Golf balls vanish fast, so this one actually saves money.";
        if (n.Contains("wedge")) return "Wedge prices are wild lately, this one is refreshingly sane.";
        if (n.Contains("rangefinder")) return "No more guessing yardages and pretending I knew the number.";
        if (n.Contains("iron")) return "Iron set pricing has been rough, this one looks legit.";
        if (n.Contains("bag")) return "A bag deal is rare when you actually need one right now.";
        if (n.Contains("gps") || n.Contains("watch")) return "Golf tech usually hurts the wallet, this one hurts less.";
        return "Solid golf deal I'd actually send to a friend without feeling spammy.";
    }

    private async Task<Dictionary<long, string>> BuildVariantDetailsByVariantIdAsync(IEnumerable<long?>? variantIds)
    {
        var ids = (variantIds ?? Enumerable.Empty<long?>())
            .Where(id => id.HasValue && id.Value > 0)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
            return new Dictionary<long, string>();

        var client = _supabase.GetServiceRoleClient();
        var idObjects = ids.Cast<object>().ToArray();

        var variantAttributesResp = await client.From<ProductVariantAttribute>()
            .Filter("product_variant_id", Supabase.Postgrest.Constants.Operator.In, idObjects)
            .Get();

        var variantAttributes = (variantAttributesResp.Models ?? [])
            .Where(row => row.ProductVariantId > 0 && row.AttributeId > 0)
            .ToList();

        if (variantAttributes.Count == 0)
            return new Dictionary<long, string>();

        var attributeIds = variantAttributes.Select(row => row.AttributeId).Distinct().ToArray();
        var attributeIdObjects = attributeIds.Cast<object>().ToArray();

        var attributesResp = await client.From<AttributeModel>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.In, attributeIdObjects)
            .Get();

        var enumIds = variantAttributes
            .Where(row => row.EnumValueId.HasValue)
            .Select(row => row.EnumValueId!.Value)
            .Distinct()
            .ToArray();

        var enumLabelById = new Dictionary<int, string>();
        if (enumIds.Length > 0)
        {
            var enumResp = await client.From<AttributeEnumValue>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.In, enumIds.Cast<object>().ToArray())
                .Get();

            enumLabelById = (enumResp.Models ?? [])
                .ToDictionary(
                    row => row.Id,
                    row => !string.IsNullOrWhiteSpace(row.DisplayName) ? row.DisplayName : row.EnumKey);
        }

        var attributeLabelById = (attributesResp.Models ?? [])
            .ToDictionary(
                row => row.Id,
                row => !string.IsNullOrWhiteSpace(row.Description) ? row.Description! : row.AttributeKey);

        var result = new Dictionary<long, string>();
        foreach (var group in variantAttributes.GroupBy(row => row.ProductVariantId))
        {
            var parts = group
                .GroupBy(row => row.AttributeId)
                .Select(attributeGroup =>
                {
                    var attributeId = attributeGroup.Key;
                    var attributeLabel = attributeLabelById.GetValueOrDefault(attributeId) ?? $"Attribute {attributeId}";
                    var values = attributeGroup
                        .Select(row => BuildVariantAttributeValue(row, enumLabelById))
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(value => value)
                        .ToList();

                    return new
                    {
                        AttributeLabel = attributeLabel,
                        Text = values.Count > 0 ? $"{attributeLabel}: {string.Join(", ", values)}" : string.Empty
                    };
                })
                .Where(part => !string.IsNullOrWhiteSpace(part.Text))
                .OrderBy(part => part.AttributeLabel)
                .Select(part => part.Text)
                .ToList();

            if (parts.Count > 0)
                result[group.Key] = string.Join(" • ", parts);
        }

        return result;
    }

    private static string? BuildVariantAttributeValue(
        ProductVariantAttribute row,
        IReadOnlyDictionary<int, string> enumLabelById)
    {
        if (row.EnumValueId.HasValue)
            return enumLabelById.GetValueOrDefault(row.EnumValueId.Value) ?? row.EnumValueId.Value.ToString();

        if (!string.IsNullOrWhiteSpace(row.ValueText))
            return row.ValueText.Trim();

        if (row.ValueNum.HasValue)
            return row.ValueNum.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        if (row.ValueBool.HasValue)
            return row.ValueBool.Value ? "Yes" : "No";

        return null;
    }

    // ── Caption Builder per Platform ──────────────────────────────────────

    private static string BuildPlatformCaption(string? caption, string? linkUrl, string platform)
    {
        var text = caption ?? string.Empty;
        var hasUrl = text.Contains("http://", StringComparison.OrdinalIgnoreCase)
            || text.Contains("https://", StringComparison.OrdinalIgnoreCase);

        return platform switch
        {
            "twitter" => string.IsNullOrWhiteSpace(linkUrl) || hasUrl ? text : $"{text}\n{linkUrl}",
            "facebook" => string.IsNullOrWhiteSpace(linkUrl) || hasUrl ? StripHashtags(text) : $"{StripHashtags(text)}\n{linkUrl}",
            "instagram" => text,
            _ => text
        };
    }

    private static string StripHashtags(string text)
    {
        // Remove hashtag tokens from Facebook posts (they don't add value there)
        var parts = text.Split(' ', '\n')
            .Where(t => !t.StartsWith('#'))
            .ToArray();
        return string.Join(' ', parts).Trim();
    }

    // ── Mapping ───────────────────────────────────────────────────────────

    private async Task<Dictionary<int, SocialDealDetailsDto>> BuildDealDetailsForGenerationAsync(
        IReadOnlyList<Deal> deals,
        CancellationToken ct)
    {
        _ = ct;
        var result = new Dictionary<int, SocialDealDetailsDto>();
        if (deals.Count == 0) return result;

        var client = _supabase.GetServiceRoleClient();
        var dealIds = deals.Select(d => d.Id).Distinct().ToArray();
        var dealIdObjects = dealIds.Select(id => (object)id).ToArray();

        // Fetch combo steps
        var comboResp = await client.From<DealCombo>()
            .Filter("deal_id", Supabase.Postgrest.Constants.Operator.In, dealIdObjects)
            .Get();

        var combosByParentId = (comboResp.Models ?? [])
            .GroupBy(c => c.DealId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Order ?? int.MaxValue).ThenBy(c => c.id).ToList());

        var comboDealIdObjects = combosByParentId.Values
            .SelectMany(rows => rows.Select(r => (object)r.ComboDealId))
            .Distinct().ToArray();

        var stepDealsById = new Dictionary<int, Deal>();
        if (comboDealIdObjects.Length > 0)
        {
            var stepDealsResp = await client.From<Deal>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.In, comboDealIdObjects)
                .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
                .Get();
            stepDealsById = (stepDealsResp.Models ?? []).ToDictionary(d => d.Id);
        }

        var allDeals = deals.Concat(stepDealsById.Values).ToList();

        var allDealIds = allDeals.Select(d => d.Id).Distinct().ToArray();
        var allDealIdObjects = allDealIds.Select(id => (object)id).ToArray();
        var dealProductsResp = await client.From<DealProduct>()
            .Filter("deal_id", Supabase.Postgrest.Constants.Operator.In, allDealIdObjects)
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Get();

        var primaryDealProductByDeal = new Dictionary<int, DealProduct>();
        foreach (var dp in (dealProductsResp.Models ?? []))
        {
            if (!primaryDealProductByDeal.TryGetValue(dp.DealId, out var existing)
                || (dp.Primary && !existing.Primary)
                || (dp.Primary == existing.Primary && dp.Price < existing.Price))
            {
                primaryDealProductByDeal[dp.DealId] = dp;
            }
        }

        var variantDetailsById = await BuildVariantDetailsByVariantIdAsync(
            primaryDealProductByDeal.Values.Select(dp => dp.ProductVariantId));

        var dealTypeIds = allDeals.Where(d => d.DealTypeId.HasValue)
            .Select(d => d.DealTypeId!.Value).Distinct().ToArray();
        var dealTypeNameById = new Dictionary<int, string>();
        if (dealTypeIds.Length > 0)
        {
            var dtResp = await client.From<DealType>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.In, dealTypeIds.Select(id => (object)id).ToArray())
                .Get();
            dealTypeNameById = (dtResp.Models ?? [])
                .Where(dt => !string.IsNullOrWhiteSpace(dt.Name))
                .ToDictionary(dt => dt.Id, dt => dt.Name!.Trim());
        }

        var externalStoreIds = allDeals.Where(d => d.ExternalOfferStoreId.HasValue)
            .Select(d => d.ExternalOfferStoreId!.Value);
        var directStoreIds = allDeals.Where(d => d.StoreId.HasValue)
            .Select(d => d.StoreId!.Value);
        var storeIds = externalStoreIds.Concat(directStoreIds).Distinct().ToArray();
        var storeById = new Dictionary<int, Store>();
        if (storeIds.Length > 0)
        {
            var storesResp = await client.From<Store>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.In, storeIds.Select(id => (object)id).ToArray())
                .Get();
            storeById = (storesResp.Models ?? []).ToDictionary(s => s.Id);
        }

        var conditionIds = primaryDealProductByDeal.Values
            .Where(dp => dp.ConditionId.HasValue)
            .Select(dp => dp.ConditionId!.Value)
            .Distinct()
            .ToArray();
        var conditionNameById = new Dictionary<int, string>();
        if (conditionIds.Length > 0)
        {
            var conditionsResp = await client.From<Condition>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.In, conditionIds.Select(id => (object)id).ToArray())
                .Get();
            conditionNameById = (conditionsResp.Models ?? [])
                .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .ToDictionary(c => c.Id, c => c.Name!.Trim());
        }

        foreach (var deal in deals)
        {
            var typeName = deal.DealTypeId.HasValue
                ? dealTypeNameById.GetValueOrDefault(deal.DealTypeId.Value) : null;
            var extStore = deal.ExternalOfferStoreId.HasValue
                ? storeById.GetValueOrDefault(deal.ExternalOfferStoreId.Value) : null;
            var primaryStore = deal.StoreId.HasValue
                ? storeById.GetValueOrDefault(deal.StoreId.Value) : null;
            var primaryDealProduct = primaryDealProductByDeal.GetValueOrDefault(deal.Id);
            var conditionName = primaryDealProduct?.ConditionId is int conditionId
                ? conditionNameById.GetValueOrDefault(conditionId) : null;

            var steps = new List<SocialDealStepDto>();
            if (combosByParentId.TryGetValue(deal.Id, out var comboList) && comboList.Count > 0)
            {
                var stepNum = 1;
                foreach (var combo in comboList)
                {
                    if (!stepDealsById.TryGetValue(combo.ComboDealId, out var stepDeal)) continue;
                    var stepStore = stepDeal.ExternalOfferStoreId.HasValue
                        ? storeById.GetValueOrDefault(stepDeal.ExternalOfferStoreId.Value) : null;
                    var stepTypeName = stepDeal.DealTypeId.HasValue
                        ? dealTypeNameById.GetValueOrDefault(stepDeal.DealTypeId.Value) : null;
                    steps.Add(new SocialDealStepDto(
                        StepNumber: stepNum++,
                        DealId: stepDeal.Id,
                        DealTypeId: stepDeal.DealTypeId,
                        DealTypeName: stepTypeName,
                        CouponCode: stepDeal.CouponCode,
                        AdditionalDetails: stepDeal.AdditionalDetails,
                        DealUrl: primaryDealProductByDeal.GetValueOrDefault(stepDeal.Id)?.Url,
                        ExternalOfferUrl: stepDeal.ExternalOfferUrl,
                        ExternalStoreName: stepStore?.Name,
                        ExternalStoreUrl: stepStore?.URL));
                }
            }
            else if (deal.DealTypeId is 3 or 4)
            {
                steps.Add(new SocialDealStepDto(
                    StepNumber: 1,
                    DealId: deal.Id,
                    DealTypeId: deal.DealTypeId,
                    DealTypeName: typeName,
                    CouponCode: deal.CouponCode,
                    AdditionalDetails: deal.AdditionalDetails,
                    DealUrl: primaryDealProductByDeal.GetValueOrDefault(deal.Id)?.Url,
                    ExternalOfferUrl: deal.ExternalOfferUrl,
                    ExternalStoreName: extStore?.Name,
                    ExternalStoreUrl: extStore?.URL));
            }

            result[deal.Id] = new SocialDealDetailsDto(
                DealTypeId: deal.DealTypeId,
                DealTypeName: typeName,
                CouponCode: deal.CouponCode,
                StoreName: primaryStore?.Name ?? extStore?.Name,
                StoreImageUrl: primaryStore?.ImageUrl ?? extStore?.ImageUrl,
                ConditionName: conditionName,
                VariantDetails: primaryDealProduct?.ProductVariantId is long variantId
                    ? variantDetailsById.GetValueOrDefault(variantId)
                    : null,
                ItemCount: primaryDealProduct?.ItemCount > 1 ? primaryDealProduct.ItemCount : null,
                FreeShipping: primaryDealProduct?.FreeShipping ?? false,
                CartSmartDealUrl: null,
                AdditionalDetails: deal.AdditionalDetails,
                ExternalOfferUrl: deal.ExternalOfferUrl,
                ExternalStoreName: extStore?.Name,
                ExternalStoreUrl: extStore?.URL,
                Steps: steps);
        }

        return result;
    }

    private async Task<Dictionary<long, SocialDealDetailsDto>> BuildDealDetailsByPostIdAsync(
        IReadOnlyList<SocialPost> posts,
        CancellationToken ct)
    {
        _ = ct;
        var detailsByPostId = new Dictionary<long, SocialDealDetailsDto>();
        if (posts.Count == 0)
            return detailsByPostId;

        var client = _supabase.GetServiceRoleClient();
        var parentDealIds = posts.Select(p => p.DealId).Distinct().ToArray();
        var parentDealIdObjects = parentDealIds.Select(id => (object)id).ToArray();

        var parentDealsResp = await client.From<Deal>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.In, parentDealIdObjects)
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Get();

        var parentDeals = parentDealsResp.Models ?? [];
        if (parentDeals.Count == 0)
            return detailsByPostId;

        var comboResp = await client.From<DealCombo>()
            .Filter("deal_id", Supabase.Postgrest.Constants.Operator.In, parentDealIdObjects)
            .Get();

        var combosByParentId = (comboResp.Models ?? [])
            .GroupBy(c => c.DealId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(c => c.Order ?? int.MaxValue).ThenBy(c => c.id).ToList());

        var comboDealIds = combosByParentId.Values
            .SelectMany(rows => rows.Select(r => r.ComboDealId))
            .Distinct()
            .ToArray();

        var allDealIds = parentDeals.Select(d => d.Id)
            .Concat(comboDealIds)
            .Distinct()
            .ToArray();

        var allDealIdObjects = allDealIds.Select(id => (object)id).ToArray();
        var allDealsResp = await client.From<Deal>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.In, allDealIdObjects)
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Get();

        var allDeals = allDealsResp.Models ?? [];
        var dealById = allDeals.ToDictionary(d => d.Id);

        var dealTypeIds = allDeals
            .Where(d => d.DealTypeId.HasValue)
            .Select(d => d.DealTypeId!.Value)
            .Distinct()
            .ToArray();

        var dealTypeNameById = new Dictionary<int, string>();
        if (dealTypeIds.Length > 0)
        {
            var dealTypeResp = await client.From<DealType>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.In, dealTypeIds.Select(id => (object)id).ToArray())
                .Get();
            dealTypeNameById = (dealTypeResp.Models ?? [])
                .Where(dt => !string.IsNullOrWhiteSpace(dt.Name))
                .ToDictionary(dt => dt.Id, dt => dt.Name!.Trim());
        }

        var postProductIds = posts.Select(p => p.ProductId).Distinct().ToArray();
        var productsResp = await client.From<Product>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.In, postProductIds.Select(id => (object)id).ToArray())
            .Get();
        var productSlugById = (productsResp.Models ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Slug))
            .ToDictionary(p => p.Id, p => p.Slug!);

        var dealProductsResp = await client.From<DealProduct>()
            .Filter("deal_id", Supabase.Postgrest.Constants.Operator.In, allDealIdObjects)
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Get();

        var dealProducts = dealProductsResp.Models ?? [];
        var primaryDealProductByDealAndProduct = new Dictionary<(int DealId, int ProductId), DealProduct>();
        var primaryDealProductByDeal = new Dictionary<int, DealProduct>();

        foreach (var dp in dealProducts)
        {
            if (postProductIds.Length > 0 && !postProductIds.Contains(dp.ProductId))
                continue;

            var pairKey = (dp.DealId, dp.ProductId);
            if (!primaryDealProductByDealAndProduct.TryGetValue(pairKey, out var existingPair)
                || (dp.Primary && !existingPair.Primary)
                || (dp.Primary == existingPair.Primary && dp.Price < existingPair.Price))
            {
                primaryDealProductByDealAndProduct[pairKey] = dp;
            }

            if (!primaryDealProductByDeal.TryGetValue(dp.DealId, out var existingDeal)
                || (dp.Primary && !existingDeal.Primary)
                || (dp.Primary == existingDeal.Primary && dp.Price < existingDeal.Price))
            {
                primaryDealProductByDeal[dp.DealId] = dp;
            }
        }

        var variantDetailsById = await BuildVariantDetailsByVariantIdAsync(
            primaryDealProductByDealAndProduct.Values
                .Concat(primaryDealProductByDeal.Values)
                .Select(dp => dp.ProductVariantId));

        var storeIds = allDeals
            .SelectMany(d => new[] { d.StoreId, d.ExternalOfferStoreId })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();

        var storeById = new Dictionary<int, Store>();
        if (storeIds.Length > 0)
        {
            var storesResp = await client.From<Store>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.In, storeIds.Select(id => (object)id).ToArray())
                .Get();
            storeById = (storesResp.Models ?? []).ToDictionary(s => s.Id);
        }

        var conditionIds = dealProducts
            .Where(dp => dp.ConditionId.HasValue)
            .Select(dp => dp.ConditionId!.Value)
            .Distinct()
            .ToArray();

        var conditionNameById = new Dictionary<int, string>();
        if (conditionIds.Length > 0)
        {
            var conditionsResp = await client.From<Condition>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.In, conditionIds.Select(id => (object)id).ToArray())
                .Get();
            conditionNameById = (conditionsResp.Models ?? [])
                .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .ToDictionary(c => c.Id, c => c.Name!.Trim());
        }

        foreach (var post in posts)
        {
            if (!dealById.TryGetValue(post.DealId, out var parentDeal))
                continue;

            var parentTypeName = parentDeal.DealTypeId.HasValue
                ? dealTypeNameById.GetValueOrDefault(parentDeal.DealTypeId.Value)
                : null;

            var parentStore = parentDeal.ExternalOfferStoreId.HasValue
                ? storeById.GetValueOrDefault(parentDeal.ExternalOfferStoreId.Value)
                : null;
            var directStore = parentDeal.StoreId.HasValue
                ? storeById.GetValueOrDefault(parentDeal.StoreId.Value)
                : null;
            var parentDealProduct = primaryDealProductByDealAndProduct.GetValueOrDefault((parentDeal.Id, post.ProductId))
                ?? primaryDealProductByDeal.GetValueOrDefault(parentDeal.Id);
            var conditionName = parentDealProduct?.ConditionId is int conditionId
                ? conditionNameById.GetValueOrDefault(conditionId)
                : null;

            var steps = new List<SocialDealStepDto>();
            if (combosByParentId.TryGetValue(parentDeal.Id, out var comboSteps) && comboSteps.Count > 0)
            {
                var stepNumber = 1;
                foreach (var combo in comboSteps)
                {
                    if (!dealById.TryGetValue(combo.ComboDealId, out var stepDeal))
                        continue;

                    var stepStore = stepDeal.ExternalOfferStoreId.HasValue
                        ? storeById.GetValueOrDefault(stepDeal.ExternalOfferStoreId.Value)
                        : null;
                    var stepTypeName = stepDeal.DealTypeId.HasValue
                        ? dealTypeNameById.GetValueOrDefault(stepDeal.DealTypeId.Value)
                        : null;

                    var dealProduct = primaryDealProductByDealAndProduct.GetValueOrDefault((stepDeal.Id, post.ProductId))
                        ?? primaryDealProductByDeal.GetValueOrDefault(stepDeal.Id);

                    steps.Add(new SocialDealStepDto(
                        StepNumber: stepNumber,
                        DealId: stepDeal.Id,
                        DealTypeId: stepDeal.DealTypeId,
                        DealTypeName: stepTypeName,
                        CouponCode: stepDeal.CouponCode,
                        AdditionalDetails: stepDeal.AdditionalDetails,
                        DealUrl: dealProduct?.Url,
                        ExternalOfferUrl: stepDeal.ExternalOfferUrl,
                        ExternalStoreName: stepStore?.Name,
                        ExternalStoreUrl: stepStore?.URL));
                    stepNumber++;
                }
            }
            else if (parentDeal.DealTypeId is 3 or 4)
            {
                steps.Add(new SocialDealStepDto(
                    StepNumber: 1,
                    DealId: parentDeal.Id,
                    DealTypeId: parentDeal.DealTypeId,
                    DealTypeName: parentTypeName,
                    CouponCode: parentDeal.CouponCode,
                    AdditionalDetails: parentDeal.AdditionalDetails,
                    DealUrl: post.DealUrl,
                    ExternalOfferUrl: parentDeal.ExternalOfferUrl,
                    ExternalStoreName: parentStore?.Name,
                    ExternalStoreUrl: parentStore?.URL));
            }

            detailsByPostId[post.Id] = new SocialDealDetailsDto(
                DealTypeId: parentDeal.DealTypeId,
                DealTypeName: parentTypeName,
                CouponCode: parentDeal.CouponCode,
                StoreName: directStore?.Name ?? parentStore?.Name,
                StoreImageUrl: directStore?.ImageUrl ?? parentStore?.ImageUrl,
                ConditionName: conditionName,
                VariantDetails: parentDealProduct?.ProductVariantId is long variantId
                    ? variantDetailsById.GetValueOrDefault(variantId)
                    : null,
                ItemCount: parentDealProduct?.ItemCount > 1 ? parentDealProduct.ItemCount : null,
                FreeShipping: parentDealProduct?.FreeShipping ?? false,
                CartSmartDealUrl: BuildCartSmartDealUrl(productSlugById.GetValueOrDefault(post.ProductId), parentDeal.Id),
                AdditionalDetails: parentDeal.AdditionalDetails,
                ExternalOfferUrl: parentDeal.ExternalOfferUrl,
                ExternalStoreName: parentStore?.Name,
                ExternalStoreUrl: parentStore?.URL,
                Steps: steps);
        }

        return detailsByPostId;
    }

    private static SocialPostDto ToDto(
        SocialPost post,
        IReadOnlyList<SocialPostCaption> captions,
        SocialDealDetailsDto? dealDetails)
    {
        return new SocialPostDto(
            Id:            post.Id,
            DealId:        post.DealId,
            ProductId:     post.ProductId,
            ProductName:   post.ProductName ?? string.Empty,
            ProductImage:  post.ProductImage,
            CartSmartDealUrl: dealDetails?.CartSmartDealUrl,
            CurrentPrice:  post.CurrentPrice,
            OriginalPrice: post.OriginalPrice,
            DealUrl:       post.DealUrl,
            DealDetails:   dealDetails,
            Status:        post.Status,
            ScheduledDate: post.ScheduledDate,
            PostedAt:      post.PostedAt,
            IsWeekly:      post.IsWeekly,
            AdminNotes:    post.AdminNotes,
            CreatedAt:     post.CreatedAt,
            CardImageUrl:  post.ImageUrl,
            Captions: captions.Select(c => new SocialPostCaptionDto(
                Id:          c.Id,
                CaptionText: c.CaptionText ?? string.Empty,
                Platform:    c.Platform,
                Selected:    c.Selected)).ToList()
        );
    }

    // ── Card Image ────────────────────────────────────────────────────────

    public async Task<byte[]?> GenerateCardImageAsync(long postId, CancellationToken ct = default)
    {
        try
        {
            var client = _supabase.GetServiceRoleClient();

            var postResp = await client.From<SocialPost>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, postId.ToString())
                .Single();
            if (postResp == null) return null;

            SocialDealDetailsDto? details = null;
            try
            {
                // Deal details enrich the card but should not hard-fail generation.
                var detailsByPostId = await BuildDealDetailsByPostIdAsync([postResp], ct);
                details = detailsByPostId.GetValueOrDefault(postResp.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "GenerateCardImageAsync: failed to load optional deal details for post {PostId}; generating basic card",
                    postId);
            }

            var cardData = new SocialCardData(
                ProductName:     postResp.ProductName ?? string.Empty,
                ProductImageUrl: postResp.ProductImage,
                CurrentPrice:    postResp.CurrentPrice,
                OriginalPrice:   postResp.OriginalPrice,
                DealTypeId:      details?.DealTypeId,
                DealTypeName:    details?.DealTypeName,
                CouponCode:      details?.CouponCode,
                StoreName:       details?.StoreName,
                StoreImageUrl:   details?.StoreImageUrl,
                ConditionName:   details?.ConditionName,
                VariantDetails:  details?.VariantDetails,
                ItemCount:       details?.ItemCount,
                FreeShipping:    details?.FreeShipping ?? false);

            var cardBytes = await _cardImageService.GenerateAsync(cardData, ct);
            if (cardBytes is { Length: > 0 })
            {
                var dataUri = "data:image/png;base64," + Convert.ToBase64String(cardBytes);
                postResp.ImageUrl = dataUri;
                await client.From<SocialPost>().Upsert(postResp);
                _logger.LogInformation("GenerateCardImageAsync: card image updated for post {PostId}", postId);
            }
            else
            {
                _logger.LogWarning(
                    "GenerateCardImageAsync: renderer returned no image bytes for post {PostId} (Playwright may be unavailable on this host)",
                    postId);
            }

            return cardBytes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateCardImageAsync: unhandled exception for post {PostId}", postId);
            return null;
        }
    }
}
