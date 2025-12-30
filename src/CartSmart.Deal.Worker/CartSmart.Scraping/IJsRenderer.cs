using System;
using System.Threading;
using System.Threading.Tasks;

namespace CartSmart.Scraping
{
    public interface IJsRenderer
    {
        Task<string?> RenderAsync(Uri uri, int timeoutMs, CancellationToken ct);
    }
}