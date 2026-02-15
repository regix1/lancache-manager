using LancacheManager.Models;
using LancacheManager.Security;

namespace LancacheManager.Middleware;

public class SessionAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SessionAuthMiddleware> _logger;

    // Public endpoints - no session required at all
    private static readonly HashSet<string> PublicExactPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/auth/status",
        "/api/auth/login",
        "/api/auth/guest",
        "/api/auth/logout",
        "/api/auth/guest/status",
        "/api/system/setup",
        "/api/system/config",
        "/api/version",
        "/health"
    };

    private static readonly string[] PublicPrefixes = new[]
    {
        "/api/themes",
        "/api/game-images",
        "/swagger",
        "/metrics"
    };

    // Guest-allowed GET endpoints (guest OR admin can access)
    private static readonly string[] GuestGetPrefixes = new[]
    {
        "/api/downloads",
        "/api/stats",
        "/api/speeds",
        "/api/devices",
        "/api/metrics",
        "/api/events",
        "/api/cache/size",
        "/api/depots/status",
        "/api/client-groups"
    };

    public SessionAuthMiddleware(RequestDelegate next, ILogger<SessionAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        var method = context.Request.Method;

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

        // Check if this is a public endpoint
        if (IsPublicEndpoint(path))
        {
            // Still try to attach session for context, but don't require it
            await TryAttachSession(context);
            await _next(context);
            return;
        }

        // SignalR hubs - handled by hub auth, let through
        if (path.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Try to validate session
        var session = await TryAttachSession(context);

        if (session == null)
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Authentication required\"}");
            return;
        }

        // Check if guest is accessing admin-only endpoint
        if (session.SessionType == "guest" && !IsGuestAllowed(path, method))
        {
            context.Response.StatusCode = 403;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Admin access required\"}");
            return;
        }

        await _next(context);
    }

    private async Task<UserSession?> TryAttachSession(HttpContext context)
    {
        var rawToken = SessionService.GetSessionTokenFromCookie(context);
        if (string.IsNullOrEmpty(rawToken))
            return null;

        var sessionService = context.RequestServices.GetRequiredService<SessionService>();
        var session = await sessionService.ValidateSessionAsync(rawToken);

        if (session != null)
        {
            context.Items["Session"] = session;

            // Update last seen (fire-and-forget, don't block request)
            _ = sessionService.UpdateLastSeenAsync(session);
        }

        return session;
    }

    private static bool IsPublicEndpoint(string path)
    {
        if (PublicExactPaths.Contains(path))
            return true;

        foreach (var prefix in PublicPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsGuestAllowed(string path, string method)
    {
        // Guests can only do GET requests on allowed endpoints
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var prefix in GuestGetPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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
