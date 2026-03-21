using LancacheManager.Models;
using LancacheManager.Security;

namespace LancacheManager.Middleware;

/// <summary>
/// Lightweight middleware that attaches the UserSession to HttpContext.Items["Session"]
/// for backward compatibility with controllers that use GetUserSession().
/// Authentication and authorization decisions are now handled by
/// SessionAuthenticationHandler + [Authorize]/[AllowAnonymous] attributes on controllers.
/// </summary>
public class SessionAuthMiddleware
{
    private readonly RequestDelegate _next;

    public SessionAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Skip non-API paths (static files, SPA)
        if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/metrics", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Try to attach session to HttpContext.Items for backward compatibility.
        // The actual authentication is handled by SessionAuthenticationHandler.
        await TryAttachSessionAsync(context);

        await _next(context);
    }

    private static async Task TryAttachSessionAsync(HttpContext context)
    {
        // If the auth handler already populated the session, skip duplicate work
        if (context.Items.ContainsKey("Session"))
            return;

        var rawToken = SessionService.GetSessionTokenFromCookie(context);
        if (string.IsNullOrEmpty(rawToken))
            return;

        var sessionService = context.RequestServices.GetRequiredService<SessionService>();
        var session = await sessionService.ValidateSessionAsync(rawToken);

        if (session != null)
        {
            context.Items["Session"] = session;

            // Update last seen (fire-and-forget, don't block request)
            _ = sessionService.UpdateLastSeenAsync(session);
        }
    }
}

/// <summary>
/// Extension methods for accessing session from HttpContext.
/// </summary>
public static class SessionMiddlewareExtensions
{
    public static UserSession? GetUserSession(this HttpContext context)
    {
        return context.Items.TryGetValue("Session", out var session)
            ? session as UserSession
            : null;
    }
}
