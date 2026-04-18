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

    /// <summary>
    /// Gets the authenticated user session or throws <see cref="UnauthorizedAccessException"/> if none is present.
    /// Intended for use inside controllers guarded by <c>[Authorize]</c> where a null session post-middleware
    /// indicates a server misconfiguration (policy bypass, handler not registered, etc.) rather than a
    /// legitimate user state.
    /// </summary>
    public static UserSession GetRequiredUserSession(this HttpContext context)
        => context.GetUserSession()
           ?? throw new UnauthorizedAccessException(
                  "Request reached controller without an authenticated session. " +
                  "Ensure [Authorize] is configured and SessionAuthenticationHandler is running.");

    /// <summary>
    /// Gets the authenticated session ID as a <see cref="Guid"/>, or throws <see cref="UnauthorizedAccessException"/>
    /// if no session is present. Convenience wrapper over <see cref="GetRequiredUserSession"/>.
    /// </summary>
    public static Guid GetRequiredSessionId(this HttpContext context)
        => context.GetRequiredUserSession().Id;
}
