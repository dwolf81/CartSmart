using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Supabase;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CartSmart.Functions;

/// <summary>
/// Runs at 8 AM UTC every day to select the top 2 golf deals and generate
/// caption variations via OpenAI. Posts are created with status "pending_approval"
/// for admin review before they go live.
/// </summary>
public class GenerateDailyPostsFunction
{
    private const int DealStatusActive = 2;
    private const int DailyPostCount = 2;

    private readonly Client _supabase;
    private readonly HttpClient _http;
    private readonly ILogger<GenerateDailyPostsFunction> _logger;
    private readonly string _openAiApiKey;
    private readonly string _openAiModel;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public GenerateDailyPostsFunction(
        Client supabase,
        IHttpClientFactory httpClientFactory,
        ILogger<GenerateDailyPostsFunction> logger)
    {
        _supabase = supabase;
        _http = httpClientFactory.CreateClient(nameof(GenerateDailyPostsFunction));
        _logger = logger;
        _openAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
        _openAiModel = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini";
    }

    // Runs daily at 8 AM UTC
    [Function("GenerateDailyPosts")]
    public async Task RunDaily(
        [TimerTrigger("0 0 8 * * *", UseMonitor = true)] TimerInfo timerInfo,
        CancellationToken ct)
    {
        _logger.LogInformation("GenerateDailyPosts started at {Time}", DateTime.UtcNow);
        var count = await GeneratePostsAsync(isWeekly: false, ct);
        _logger.LogInformation("GenerateDailyPosts completed. Created={Count}", count);
    }

    // Runs every Monday at 9 AM UTC
    [Function("GenerateWeeklyDigest")]
    public async Task RunWeekly(
        [TimerTrigger("0 0 9 * * 1", UseMonitor = true)] TimerInfo timerInfo,
        CancellationToken ct)
    {
        _logger.LogInformation("GenerateWeeklyDigest started at {Time}", DateTime.UtcNow);
        var ok = await GenerateWeeklyAsync(ct);
        _logger.LogInformation("GenerateWeeklyDigest completed. Success={Ok}", ok);
    }

    // ── Daily Generation ──────────────────────────────────────────────────

    private async Task<int> GeneratePostsAsync(bool isWeekly, CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var cutoffUtc = DateTime.UtcNow.AddHours(-24);

        // 1. Fetch active deal_products
        var dpResp = await _supabase.From<CartSmart.API.Models.DealProduct>()
            .Select("id, deal_id, product_id, price, url, deal_status_id")
            .Filter("deal_status_id", Supabase.Postgrest.Constants.Operator.Equals, DealStatusActive.ToString())
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Filter("price", Supabase.Postgrest.Constants.Operator.GreaterThan, "0")
            .Order("price", Supabase.Postgrest.Constants.Ordering.Ascending)
            .Limit(100)
            .Get();

        var dealProducts = dpResp.Models ?? [];
        if (dealProducts.Count == 0)
        {
            _logger.LogInformation("No active deal products found");
            return 0;
        }

        // 2. Fetch associated deals (for discount_percent)
        var dealIds = dealProducts.Select(dp => (object)dp.DealId).Distinct().ToArray();
        var dealsResp = await _supabase.From<CartSmart.API.Models.Deal>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.In, dealIds)
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Get();
        var dealById = (dealsResp.Models ?? []).ToDictionary(d => d.Id);

        // 3. Fetch products for name / image
        var productIds = dealProducts.Select(dp => (object)dp.ProductId).Distinct().ToArray();
        var productsResp = await _supabase.From<CartSmart.API.Models.Product>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.In, productIds)
            .Get();
        var productById = (productsResp.Models ?? []).ToDictionary(p => p.Id);

        // 4. Skip deals/products posted in the last 24 hours
        var existingResp = await _supabase.From<CartSmart.API.Models.SocialPost>()
            .Filter("created_at", Supabase.Postgrest.Constants.Operator.GreaterThan, cutoffUtc.ToString("o"))
            .Filter("status", Supabase.Postgrest.Constants.Operator.NotEqual, "rejected")
            .Get();
        var alreadyPosted = (existingResp.Models ?? []).Select(sp => sp.DealId).ToHashSet();
        var productPostedRecently = (existingResp.Models ?? []).Select(sp => sp.ProductId).ToHashSet();

        // 5. Rank by discount%, take top N
        var candidates = dealProducts
            .Where(dp => !alreadyPosted.Contains(dp.DealId)
                      && !productPostedRecently.Contains(dp.ProductId)
                      && dealById.ContainsKey(dp.DealId)
                      && productById.ContainsKey(dp.ProductId))
            .Select(dp => new
            {
                DealProduct = dp,
                Deal        = dealById[dp.DealId],
                Product     = productById[dp.ProductId],
                Discount    = dealById[dp.DealId].DiscountPercent ?? 0
            })
            .OrderByDescending(x => x.Discount)
            .ThenBy(x => x.DealProduct.Price)
            .Take(DailyPostCount)
            .ToList();

        if (candidates.Count == 0)
        {
            _logger.LogInformation("No new deal candidates for {Date}", today);
            return 0;
        }

        var created = 0;
        foreach (var c in candidates)
        {
            var currentPrice  = c.DealProduct.Price;
            var originalPrice = c.Product.MSRP.HasValue ? (decimal)c.Product.MSRP.Value : (decimal?)null;
            var dealUrl       = c.DealProduct.Url ?? string.Empty;

            var captions = await BuildCaptionsAsync(
                c.Product.Name ?? "Golf Gear",
                currentPrice,
                originalPrice,
                ct);

            var post = new CartSmart.API.Models.SocialPost
            {
                DealId        = c.DealProduct.DealId,
                ProductId     = c.DealProduct.ProductId,
                ProductName   = c.Product.Name ?? "Golf Gear",
                ProductImage  = c.Product.ImageUrl,
                CurrentPrice  = currentPrice,
                OriginalPrice = originalPrice,
                DealUrl       = dealUrl,
                Status        = "pending_approval",
                ScheduledDate = today,
                IsWeekly      = false
            };

            var postResp = await _supabase.From<CartSmart.API.Models.SocialPost>().Insert(post);
            var inserted = postResp.Models?.FirstOrDefault();
            if (inserted == null)
            {
                _logger.LogWarning("Failed to insert post for deal {DealId}", c.Deal.Id);
                continue;
            }

            for (var i = 0; i < captions.Count; i++)
            {
                var caption = new CartSmart.API.Models.SocialPostCaption
                {
                    SocialPostId = inserted.Id,
                    CaptionText  = captions[i],
                    Platform     = "all",
                    Selected     = i == 0
                };
                await _supabase.From<CartSmart.API.Models.SocialPostCaption>().Insert(caption);
            }

            created++;
            _logger.LogInformation("Created post {PostId} for '{ProductName}'",
                inserted.Id, post.ProductName);
        }

        return created;
    }

    // ── Weekly Digest ─────────────────────────────────────────────────────

    private async Task<bool> GenerateWeeklyAsync(CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;

        var dpResp = await _supabase.From<CartSmart.API.Models.DealProduct>()
            .Select("id, deal_id, product_id, price, url, deal_status_id")
            .Filter("deal_status_id", Supabase.Postgrest.Constants.Operator.Equals, DealStatusActive.ToString())
            .Filter("deleted", Supabase.Postgrest.Constants.Operator.Equals, "false")
            .Filter("price", Supabase.Postgrest.Constants.Operator.GreaterThan, "0")
            .Limit(200)
            .Get();

        var dealProducts = dpResp.Models ?? [];
        if (dealProducts.Count == 0) return false;

        var dealIds = dealProducts.Select(dp => (object)dp.DealId).Distinct().ToArray();
        var dealsResp = await _supabase.From<CartSmart.API.Models.Deal>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.In, dealIds)
            .Get();
        var dealById = (dealsResp.Models ?? []).ToDictionary(d => d.Id);

        var productIds = dealProducts.Select(dp => (object)dp.ProductId).Distinct().ToArray();
        var productsResp = await _supabase.From<CartSmart.API.Models.Product>()
            .Filter("id", Supabase.Postgrest.Constants.Operator.In, productIds)
            .Get();
        var productById = (productsResp.Models ?? []).ToDictionary(p => p.Id);

        var topDeals = dealProducts
            .Where(dp => dealById.ContainsKey(dp.DealId) && productById.ContainsKey(dp.ProductId))
            .Select(dp => new
            {
                DealProduct = dp,
                Product     = productById[dp.ProductId],
                Discount    = dealById[dp.DealId].DiscountPercent ?? 0
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

        var lines = topDeals.Select((d, i) =>
        {
            var disc = d.Discount > 0 ? $" ({d.Discount}% off)" : string.Empty;
            return $"{i + 1}. {d.Product.Name ?? "Golf Gear"} — ${d.DealProduct.Price:F2}{disc}";
        });

        var digestCaption = "🏌️ Best Golf Deals This Week 🏌️\n\n"
                          + string.Join("\n", lines)
                          + "\n\n👉 See all deals at cartsmart.com";

        var post = new CartSmart.API.Models.SocialPost
        {
            DealId       = topDeals[0].DealProduct.DealId,
            ProductId    = topDeals[0].DealProduct.ProductId,
            ProductName  = "Best Golf Deals This Week",
            ProductImage = topDeals[0].Product.ImageUrl,
            CurrentPrice = topDeals[0].DealProduct.Price,
            DealUrl      = "https://cartsmart.com",
            Status       = "pending_approval",
            ScheduledDate = today,
            IsWeekly     = true
        };

        var postResp = await _supabase.From<CartSmart.API.Models.SocialPost>().Insert(post);
        var inserted = postResp.Models?.FirstOrDefault();
        if (inserted == null) return false;

        await _supabase.From<CartSmart.API.Models.SocialPostCaption>().Insert(
            new CartSmart.API.Models.SocialPostCaption
            {
                SocialPostId = inserted.Id,
                CaptionText  = digestCaption,
                Platform     = "all",
                Selected     = true
            });

        _logger.LogInformation("Weekly digest post created: {PostId}", inserted.Id);
        return true;
    }

    // ── Caption Generation ────────────────────────────────────────────────

    private async Task<IReadOnlyList<string>> BuildCaptionsAsync(
        string productName, decimal currentPrice, decimal? originalPrice, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_openAiApiKey))
            return FallbackCaptions(productName, currentPrice, originalPrice);

        var (angleName, angleInstruction) = GetCaptionAngle();

        var wasLine = originalPrice.HasValue
            ? $"was ${originalPrice:F2}, now ${currentPrice:F2}"
            : $"${currentPrice:F2}";

        var body = new
        {
            model = _openAiModel,
            max_completion_tokens = 512,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = $"""
                        You write short, witty social media captions for a golf deals website.
                        Natural, human tone — like a golfer sharing a steal with friends.
                        No marketing buzzwords. Keep it simple, fun, and honest.
                        Keep captions accurate to the provided product name.
                        Do not invent specs/features not implied by the product name.
                        Today's angle is "{angleName}": {angleInstruction}
                        Every caption MUST start with exactly: Deal of the Day:
                        Return valid JSON with a "captions" key mapping to an array of exactly 3 caption strings.
                        Each 1-4 lines. Do NOT include hashtags. Do NOT include URLs.
                        """
                },
                new
                {
                    role = "user",
                    content = $"Product: {productName}\nPrice: {wasLine}\nAngle for today: {angleName} — {angleInstruction}\nWrite 3 captions. Each must start with: Deal of the Day:"
                }
            }
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://api.openai.com/v1/chat/completions");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", _openAiApiKey);
            req.Content = JsonContent.Create(body, options: JsonOpts);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("OpenAI caption error {Status}: {Body}", resp.StatusCode, err);
                return FallbackCaptions(productName, currentPrice, originalPrice);
            }

            var raw = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(raw);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "{}";

            using var inner = JsonDocument.Parse(content);
            if (inner.RootElement.TryGetProperty("captions", out var arr))
            {
                var list = new List<string>();
                foreach (var el in arr.EnumerateArray())
                {
                    var t = el.GetString();
                    if (!string.IsNullOrWhiteSpace(t))
                    {
                        var cleaned = RemoveHashtagTokens(t);
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
            _logger.LogError(ex, "Exception calling OpenAI for captions");
        }

        return FallbackCaptions(productName, currentPrice, originalPrice);
    }

    private static IReadOnlyList<string> FallbackCaptions(
        string name, decimal price, decimal? originalPrice)
    {
        var was = originalPrice.HasValue ? $" (was ${originalPrice:F2})" : string.Empty;
        return
        [
            $"Deal of the Day: {name}\n${price:F2}{was}\nBest price I've seen on this",
            $"Deal of the Day: Nice deal on the {name} — ${price:F2}{was}",
            $"Deal of the Day: {name} is on sale\n${price:F2}{was}"
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
}
