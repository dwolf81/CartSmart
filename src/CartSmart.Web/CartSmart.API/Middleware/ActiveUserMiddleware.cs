using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using CartSmart.API.Services;

public class ActiveUserMiddleware
{
    private readonly RequestDelegate _next;
    public ActiveUserMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IAuthService authService)
    {
        var endpoint = context.GetEndpoint();

        // Enforce only if [Authorize] present
        if (endpoint?.Metadata?.GetMetadata<IAuthorizeData>() == null)
        {
            await _next(context);
            return;
        }

        // Skip explicit [AllowAnonymous]
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() != null)
        {
            await _next(context);
            return;
        }

        if (!context.Request.Cookies.TryGetValue("access_token", out var token) || string.IsNullOrEmpty(token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        var valid = await authService.ValidateTokenAsync(token);
        if (!valid)
        {
            context.Response.Cookies.Delete("access_token");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Session invalid.");
            return;
        }

        await _next(context);
    }
}