using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CartSmart.Scraping
{
    // Minimal Playwright-based renderer. Assumes Microsoft.Playwright is installed in the project.
    public class PlaywrightRenderer : IJsRenderer
    {
        private readonly ILogger<PlaywrightRenderer> _logger;

        private static int _installAttempted;
        private static int _disabled;

        public PlaywrightRenderer(ILogger<PlaywrightRenderer> logger)
        {
            _logger = logger;
        }

        public async Task<string?> RenderAsync(Uri uri, int timeoutMs, CancellationToken ct)
        {
            if (System.Threading.Volatile.Read(ref _disabled) == 1)
                return null;

            try
            {
                using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
                Microsoft.Playwright.IBrowser? browser = null;

                async Task<Microsoft.Playwright.IBrowser> LaunchAsync()
                {
                    return await playwright.Chromium.LaunchAsync(new Microsoft.Playwright.BrowserTypeLaunchOptions
                    {
                        Headless = true,
                        Args = new[] { "--disable-blink-features=AutomationControlled" }
                    });
                }

                try
                {
                    browser = await LaunchAsync();
                }
                catch (Microsoft.Playwright.PlaywrightException ex) when (LooksLikeMissingBrowserInstall(ex))
                {
                    // This typically happens on fresh machines when the NuGet package is present but the
                    // Playwright browser binaries weren't downloaded yet.
                    var autoInstall = !string.Equals(
                        Environment.GetEnvironmentVariable("PLAYWRIGHT_AUTO_INSTALL"),
                        "false",
                        StringComparison.OrdinalIgnoreCase);

                    if (autoInstall && Interlocked.CompareExchange(ref _installAttempted, 1, 0) == 0)
                    {
                        try
                        {
                            _logger.LogWarning(ex, "Playwright browsers missing; attempting one-time install.");

                            // Runs the same install that playwright.ps1 would perform.
                            // Note: this can take time and downloads browsers; keep it one-time.
                            Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });

                            browser = await LaunchAsync();
                        }
                        catch (Exception installEx)
                        {
                            _logger.LogError(installEx, "Playwright browser install failed; disabling JS rendering.");
                            System.Threading.Volatile.Write(ref _disabled, 1);
                            return null;
                        }
                    }
                    else
                    {
                        _logger.LogError(ex, "Playwright browsers missing; JS rendering disabled. Run: pwsh bin/Debug/netX/playwright.ps1 install");
                        System.Threading.Volatile.Write(ref _disabled, 1);
                        return null;
                    }
                }

                await using var browserScope = browser;
                await using var context = await browser.NewContextAsync(new Microsoft.Playwright.BrowserNewContextOptions
                {
                    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                    IgnoreHTTPSErrors = true
                });
                // Reduce basic bot-detection surface
                await context.AddInitScriptAsync("Object.defineProperty(navigator, 'webdriver', { get: () => undefined })");

                var page = await context.NewPageAsync();
                page.SetDefaultTimeout(timeoutMs);

                // Navigate with a more forgiving wait strategy
                await page.GotoAsync(uri.ToString(), new Microsoft.Playwright.PageGotoOptions
                {
                    WaitUntil = Microsoft.Playwright.WaitUntilState.Load,
                    Timeout = timeoutMs
                });

                // Ensure DOM is ready, then wait for likely price selectors (best-effort)
                try
                {
                    await page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.DOMContentLoaded, new Microsoft.Playwright.PageWaitForLoadStateOptions { Timeout = Math.Min(timeoutMs, 10000) });
                }
                catch { /* ignore */ }

                try
                {
                    var priceSelector = "[data-testid='price-top'] span, span[class*='price'], span[class*='text_sale'], span[id*='price'], div[class*='price']";
                    await page.WaitForSelectorAsync(priceSelector, new Microsoft.Playwright.PageWaitForSelectorOptions { Timeout = Math.Min(timeoutMs, 10000) });
                }
                catch { /* ignore selector timeouts; we'll still capture HTML */ }

                var content = await page.ContentAsync();
                return content;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Playwright render failed for {Url}", uri);
                return null;
            }
        }

        private static bool LooksLikeMissingBrowserInstall(Microsoft.Playwright.PlaywrightException ex)
        {
            var msg = ex?.Message ?? string.Empty;
            return msg.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Please run the following command to download new browsers", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("playwright.ps1 install", StringComparison.OrdinalIgnoreCase);
        }
    }
}