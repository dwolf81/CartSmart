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

        public PlaywrightRenderer(ILogger<PlaywrightRenderer> logger)
        {
            _logger = logger;
        }

        public async Task<string?> RenderAsync(Uri uri, int timeoutMs, CancellationToken ct)
        {
            try
            {
                using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
                await using var browser = await playwright.Chromium.LaunchAsync(new Microsoft.Playwright.BrowserTypeLaunchOptions
                {
                    Headless = true,
                    Args = new[] { "--disable-blink-features=AutomationControlled" }
                });
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
    }
}