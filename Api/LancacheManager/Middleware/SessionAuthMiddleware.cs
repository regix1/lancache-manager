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
        "/api/system/refresh-rate",
        "/api/user-preferences",
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
        "/api/client-groups",
        "/api/user-preferences",
        "/api/system/default-guest-preferences",
        "/api/system/default-guest-refresh-rate",
        "/api/system/refresh-rate",
        "/api/system/prefill-defaults",
        "/api/system/permissions"
    };

    // Prefill endpoints allowed for guests WITH active prefill access (GET + POST)
    private static readonly string[] GuestPrefillPrefixes = new[]
    {
        "/api/steam-daemon",
        "/api/epic-daemon",
        "/api/prefill-admin/cache"
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
        if (session.SessionType == "guest" && !IsGuestAllowed(path, method, session))
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

    // Guest-allowed read/write endpoints (GET, PUT, PATCH)
    private static readonly string[] GuestReadWritePrefixes = new[]
    {
        "/api/user-preferences"
    };

    private static bool IsGuestAllowed(string path, string method, UserSession session)
    {
        // Check standard guest GET endpoints first
        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var prefix in GuestGetPrefixes)
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        // Check guest read/write endpoints (GET, PUT, PATCH)
        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var prefix in GuestReadWritePrefixes)
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        // Check prefill endpoints (GET + POST) - only if guest has active prefill access
        var hasPrefillAccess = session.PrefillExpiresAtUtc != null && session.PrefillExpiresAtUtc > DateTime.UtcNow;
        if (hasPrefillAccess &&
            (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var prefix in GuestPrefillPrefixes)
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
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
