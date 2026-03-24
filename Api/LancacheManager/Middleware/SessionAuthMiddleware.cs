using LancacheManager.Models;

namespace LancacheManager.Middleware;

/// <summary>
/// Extension methods for accessing the session attached by SessionAuthenticationHandler.
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
