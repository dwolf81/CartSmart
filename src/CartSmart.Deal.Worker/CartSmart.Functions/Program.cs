using CartSmart.Core.Worker;
using CartSmart.Providers;
using CartSmart.Scraping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Supabase;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;
        static string? FirstNonEmpty(params string?[] values) =>
            values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        static string GetLocalSettingsPath(HostBuilderContext hostContext)
        {
            var contentRootPath = Path.Combine(hostContext.HostingEnvironment.ContentRootPath, "local.settings.json");
            if (File.Exists(contentRootPath)) return contentRootPath;

            var cwdPath = Path.Combine(Directory.GetCurrentDirectory(), "local.settings.json");
            if (File.Exists(cwdPath)) return cwdPath;

            var baseDirPath = Path.Combine(AppContext.BaseDirectory, "local.settings.json");
            return baseDirPath;
        }

        // Register HTML scraper for price checks (HTTP/AngleSharp only — no Playwright in Functions).
        services.AddSingleton<IHtmlScraper>(sp =>
            new GenericHtmlScraper(
                sp.GetRequiredService<ILogger<GenericHtmlScraper>>()));
        // Prefer Functions configuration (local.settings.json Values) over raw environment.
        // When not running via the Functions host, local.settings.json is NOT loaded automatically.
        // To make F5 debugging work, load local.settings.json manually and hydrate environment if needed.
        var supabaseUrl = FirstNonEmpty(
            Environment.GetEnvironmentVariable("SUPABASE_URL"),
            config["Values:SUPABASE_URL"],
            config["SUPABASE_URL"]);
        var supabaseServiceRoleKey = FirstNonEmpty(
            Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY"),
            config["Values:SUPABASE_SERVICE_ROLE_KEY"],
            config["SUPABASE_SERVICE_ROLE_KEY"]);

        if (string.IsNullOrEmpty(supabaseUrl) || string.IsNullOrEmpty(supabaseServiceRoleKey))
        {
            var localSettingsPath = GetLocalSettingsPath(context);
            if (File.Exists(localSettingsPath))
            {
                try
                {
                    using var stream = File.OpenRead(localSettingsPath);
                    using var doc = System.Text.Json.JsonDocument.Parse(stream);
                    if (doc.RootElement.TryGetProperty("Values", out var values))
                    {
                        if (string.IsNullOrEmpty(supabaseUrl) && values.TryGetProperty("SUPABASE_URL", out var urlEl))
                        {
                            supabaseUrl = urlEl.GetString();
                            if (!string.IsNullOrEmpty(supabaseUrl))
                                Environment.SetEnvironmentVariable("SUPABASE_URL", supabaseUrl);
                        }
                        if (string.IsNullOrEmpty(supabaseServiceRoleKey) && values.TryGetProperty("SUPABASE_SERVICE_ROLE_KEY", out var keyEl))
                        {
                            supabaseServiceRoleKey = keyEl.GetString();
                            if (!string.IsNullOrEmpty(supabaseServiceRoleKey))
                                Environment.SetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY", supabaseServiceRoleKey);
                        }
                    }
                }
                catch { /* ignore parse errors for local debug */ }
            }
        }

        supabaseUrl ??= string.Empty;
        supabaseServiceRoleKey ??= string.Empty;
        supabaseUrl = supabaseUrl.Trim();
        supabaseServiceRoleKey = supabaseServiceRoleKey.Trim();

        if (string.IsNullOrWhiteSpace(supabaseUrl) || string.IsNullOrWhiteSpace(supabaseServiceRoleKey))
            throw new InvalidOperationException("SUPABASE_URL and SUPABASE_SERVICE_ROLE_KEY are required for worker startup.");

        // Safe startup diagnostics to identify config/source mismatches without logging full secrets.
        var keyPrefix = supabaseServiceRoleKey.Length >= 12
            ? supabaseServiceRoleKey[..12]
            : supabaseServiceRoleKey;
        var keyKind = supabaseServiceRoleKey.StartsWith("sb_secret_", StringComparison.OrdinalIgnoreCase)
            ? "secret"
            : supabaseServiceRoleKey.StartsWith("eyJ", StringComparison.Ordinal)
                ? "legacy-jwt"
                : "unknown";
        var urlHost = Uri.TryCreate(supabaseUrl, UriKind.Absolute, out var supabaseUri)
            ? supabaseUri.Host
            : "invalid-url";
        Console.WriteLine($"[Startup] Supabase URL host={urlHost}, keyKind={keyKind}, keyPrefix={keyPrefix}, keyLength={supabaseServiceRoleKey.Length}");

        // Worker always uses service-role so it keeps working when anon/authenticated are fully locked down.
        services.AddSingleton(_ => new Client(supabaseUrl, supabaseServiceRoleKey, new SupabaseOptions
        {
            AutoConnectRealtime = false
        }));

        // Register repository once; expose as IDealRepository and IStopWordsProvider
        services.AddSingleton<SupabaseDealRepository>();
        services.AddSingleton<IDealRepository>(sp => sp.GetRequiredService<SupabaseDealRepository>());
        services.AddSingleton<IStopWordsProvider>(sp => sp.GetRequiredService<SupabaseDealRepository>());

        // Refresh scheduling knobs (priority scoring + tiered next-check).
        services.Configure<RefreshSchedulingOptions>(context.Configuration.GetSection("RefreshScheduling"));

        // eBay OAuth credentials
        var ebayClientId = config["EBAY_CLIENT_ID"] ?? Environment.GetEnvironmentVariable("EBAY_CLIENT_ID");
        var ebayClientSecret = config["EBAY_CLIENT_SECRET"] ?? Environment.GetEnvironmentVariable("EBAY_CLIENT_SECRET");
        // Fallback to local.settings.json Values for dev
        if (string.IsNullOrEmpty(ebayClientId) || string.IsNullOrEmpty(ebayClientSecret))
        {
            var localSettingsPath = GetLocalSettingsPath(context);
            if (File.Exists(localSettingsPath))
            {
                try
                {
                    using var stream = File.OpenRead(localSettingsPath);
                    using var doc = System.Text.Json.JsonDocument.Parse(stream);
                    if (doc.RootElement.TryGetProperty("Values", out var values))
                    {
                        if (string.IsNullOrEmpty(ebayClientId) && values.TryGetProperty("EBAY_CLIENT_ID", out var idEl))
                        {
                            ebayClientId = idEl.GetString();
                            if (!string.IsNullOrEmpty(ebayClientId))
                                Environment.SetEnvironmentVariable("EBAY_CLIENT_ID", ebayClientId);
                        }
                        if (string.IsNullOrEmpty(ebayClientSecret) && values.TryGetProperty("EBAY_CLIENT_SECRET", out var secEl))
                        {
                            ebayClientSecret = secEl.GetString();
                            if (!string.IsNullOrEmpty(ebayClientSecret))
                                Environment.SetEnvironmentVariable("EBAY_CLIENT_SECRET", ebayClientSecret);
                        }
                    }
                }
                catch { }
            }
        }

        // Register eBay auth + client
        services.AddHttpClient<CartSmart.Providers.EbayAuthService>();
        services.AddSingleton<CartSmart.Providers.IEbayAuthService>(sp =>
        {
            var httpFactory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var http = httpFactory.CreateClient(nameof(CartSmart.Providers.EbayAuthService));
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CartSmart.Providers.EbayAuthService>>();
            return new CartSmart.Providers.EbayAuthService(http, logger, ebayClientId ?? string.Empty, ebayClientSecret ?? string.Empty);
        });
        services.AddHttpClient<CartSmart.Providers.EbayStoreClient>();
        services.AddSingleton<IStoreClient>(sp =>
        {
            var httpFactory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var http = httpFactory.CreateClient(nameof(CartSmart.Providers.EbayStoreClient));
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CartSmart.Providers.EbayStoreClient>>();
            var auth = sp.GetRequiredService<CartSmart.Providers.IEbayAuthService>();
            var stopWordsProvider = sp.GetRequiredService<IStopWordsProvider>();
            var supabase = sp.GetRequiredService<Supabase.Client>();
            return new CartSmart.Providers.EbayStoreClient(http, logger, auth, stopWordsProvider, supabase);
        });

        // AI deal validator (optional — only active when OPENAI_API_KEY is configured)
        services.AddHttpClient<CartSmart.Providers.OpenAiDealValidator>();
        services.AddSingleton<IAiDealValidator>(sp =>
        {
            var httpFactory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var http = httpFactory.CreateClient(nameof(CartSmart.Providers.OpenAiDealValidator));
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CartSmart.Providers.OpenAiDealValidator>>();
            return new CartSmart.Providers.OpenAiDealValidator(http, logger);
        });

        services.AddSingleton<IDealUpdateOrchestrator>(sp => new DealUpdateOrchestrator(
            sp.GetRequiredService<IDealRepository>(),
            sp.GetServices<IStoreClient>(),
            sp.GetRequiredService<ILogger<DealUpdateOrchestrator>>(),
            sp.GetRequiredService<IHtmlScraper>(),
            schedulingOptions: sp.GetRequiredService<IOptions<RefreshSchedulingOptions>>().Value,
            maxParallel: 1,
            aiValidator: sp.GetRequiredService<IAiDealValidator>()));

        // Register listing page scraper for HTML store pages
        services.AddSingleton<IListingPageScraper>(sp =>
            new ListingPageScraper(
                sp.GetRequiredService<ILogger<ListingPageScraper>>()));

        // ── Deal Ingestion Pipeline ──────────────────────────────────────────
        services.AddSingleton<SupabaseIngestionRepository>();
        services.AddSingleton<IIngestionRepository>(sp => sp.GetRequiredService<SupabaseIngestionRepository>());

        // AI deal extractor (reuses OpenAI config)
        services.AddHttpClient<CartSmart.Providers.OpenAiDealExtractor>();
        services.AddSingleton<IAiDealExtractor>(sp =>
        {
            var httpFactory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var http = httpFactory.CreateClient(nameof(CartSmart.Providers.OpenAiDealExtractor));
            var logger = sp.GetRequiredService<ILogger<CartSmart.Providers.OpenAiDealExtractor>>();
            return new CartSmart.Providers.OpenAiDealExtractor(http, logger);
        });

        // Signal source providers (one per source type)
        services.AddHttpClient<CartSmart.Providers.EmailSignalSourceProvider>();
        services.AddSingleton<ISignalSourceProvider>(sp =>
        {
            var httpFactory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var http = httpFactory.CreateClient(nameof(CartSmart.Providers.EmailSignalSourceProvider));
            var logger = sp.GetRequiredService<ILogger<CartSmart.Providers.EmailSignalSourceProvider>>();
            return new CartSmart.Providers.EmailSignalSourceProvider(http, logger);
        });
        services.AddHttpClient<CartSmart.Providers.RedditSignalSourceProvider>();
        services.AddSingleton<ISignalSourceProvider>(sp =>
        {
            var httpFactory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var http = httpFactory.CreateClient(nameof(CartSmart.Providers.RedditSignalSourceProvider));
            var logger = sp.GetRequiredService<ILogger<CartSmart.Providers.RedditSignalSourceProvider>>();
            return new CartSmart.Providers.RedditSignalSourceProvider(http, logger);
        });
        services.AddHttpClient<CartSmart.Providers.SocialSignalSourceProvider>();
        services.AddSingleton<ISignalSourceProvider>(sp =>
        {
            var httpFactory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var http = httpFactory.CreateClient(nameof(CartSmart.Providers.SocialSignalSourceProvider));
            var logger = sp.GetRequiredService<ILogger<CartSmart.Providers.SocialSignalSourceProvider>>();
            return new CartSmart.Providers.SocialSignalSourceProvider(http, logger);
        });
        services.AddHttpClient<CartSmart.Providers.RetailSignalSourceProvider>();
        services.AddSingleton<ISignalSourceProvider>(sp =>
        {
            var httpFactory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var http = httpFactory.CreateClient(nameof(CartSmart.Providers.RetailSignalSourceProvider));
            var logger = sp.GetRequiredService<ILogger<CartSmart.Providers.RetailSignalSourceProvider>>();
            return new CartSmart.Providers.RetailSignalSourceProvider(http, logger);
        });
        services.AddHttpClient<CartSmart.Providers.ForumSignalSourceProvider>();
        services.AddSingleton<ISignalSourceProvider>(sp =>
        {
            var httpFactory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var http = httpFactory.CreateClient(nameof(CartSmart.Providers.ForumSignalSourceProvider));
            var logger = sp.GetRequiredService<ILogger<CartSmart.Providers.ForumSignalSourceProvider>>();
            return new CartSmart.Providers.ForumSignalSourceProvider(http, logger);
        });

        // Ingestion pipeline orchestrator
        var autoImportMinConfidence = decimal.TryParse(
            config["Values:IngestionAutoImportMinConfidence"] ?? config["IngestionAutoImportMinConfidence"],
            out var aic) ? aic : 0.90m;

        services.AddSingleton<IIngestionPipelineOrchestrator>(sp => new IngestionPipelineOrchestrator(
            sp.GetRequiredService<IIngestionRepository>(),
            sp.GetRequiredService<IDealRepository>(),
            sp.GetServices<ISignalSourceProvider>(),
            sp.GetRequiredService<IAiDealExtractor>(),
            sp.GetRequiredService<ILogger<IngestionPipelineOrchestrator>>(),
            autoImportMinConfidence));
    })
    .Build();

await host.RunAsync();