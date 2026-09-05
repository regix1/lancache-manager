using LancacheManager.Models;
using LancacheManager.Security;

namespace LancacheManager.Middleware;

public sealed class AccessSetupMiddleware
{
    private readonly RequestDelegate _next;

    public AccessSetupMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, AccessService accessService)
    {
        if (!accessService.IsSetupRequired()
            || !RequiresGate(context.Request.Path)
            || IsAllowed(context.Request))
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status428PreconditionRequired;
        await context.Response.WriteAsJsonAsync(ApiResponse.Error("Authentication setup is required"));
    }

    private static bool RequiresGate(PathString path)
        => path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs");

    private static bool IsAllowed(HttpRequest request)
    {
        var path = request.Path;
        if (HttpMethods.IsGet(request.Method)
            && (path == "/api/system/setup" || path == "/api/system/config"))
        {
            return true;
        }

        return path.StartsWithSegments("/api/auth/status")
            || path.StartsWithSegments("/api/auth/login")
            || path.StartsWithSegments("/api/auth/logout")
            || path.StartsWithSegments("/api/auth/setup")
            || path.StartsWithSegments("/api/auth/oidc")
            || path.StartsWithSegments("/api/account-setup")
            || path.StartsWithSegments("/api/setup")
            || path.StartsWithSegments("/health");
    }
}
