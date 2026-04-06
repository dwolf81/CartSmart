using CartSmart.Core.Worker;
using CartSmart.Providers;
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
        // Scraping assembly is optional in local/dev due endpoint application-control policies.
        // Register a no-op scraper so Function host can still start and API-backed checks continue.
        services.AddSingleton<IHtmlScraper, NoopHtmlScraper>();
        // Prefer Functions configuration (local.settings.json Values) over raw environment.
        // When not running via the Functions host, local.settings.json is NOT loaded automatically.
        // To make F5 debugging work, load local.settings.json manually and hydrate environment if needed.
        var supabaseUrl = config["SUPABASE_URL"] ?? Environment.GetEnvironmentVariable("SUPABASE_URL");
        var supabaseKey = config["SUPABASE_KEY"] ?? Environment.GetEnvironmentVariable("SUPABASE_KEY");

        if (string.IsNullOrEmpty(supabaseUrl) || string.IsNullOrEmpty(supabaseKey))
        {
            var localSettingsPath = Path.Combine(AppContext.BaseDirectory, "local.settings.json");
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
                        if (string.IsNullOrEmpty(supabaseKey) && values.TryGetProperty("SUPABASE_KEY", out var keyEl))
                        {
                            supabaseKey = keyEl.GetString();
                            if (!string.IsNullOrEmpty(supabaseKey))
                                Environment.SetEnvironmentVariable("SUPABASE_KEY", supabaseKey);
                        }
                    }
                }
                catch { /* ignore parse errors for local debug */ }
            }
        }

        supabaseUrl ??= string.Empty;
        supabaseKey ??= string.Empty;

        services.AddSingleton(_ => new Client(supabaseUrl, supabaseKey, new SupabaseOptions
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
            var localSettingsPath = Path.Combine(AppContext.BaseDirectory, "local.settings.json");
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
    })
    .Build();

await host.RunAsync();