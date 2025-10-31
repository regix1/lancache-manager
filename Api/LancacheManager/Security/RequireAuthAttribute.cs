using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LancacheManager.Security;

/// <summary>
/// Attribute to require API Key authentication on controller endpoints
/// Respects Security:EnableAuthentication global setting
/// </summary>
public class RequireAuthAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var httpContext = context.HttpContext;
        var configuration = httpContext.RequestServices.GetRequiredService<IConfiguration>();

        // Check if authentication is globally disabled (Security:EnableAuthentication = false)
        // This allows running in development/testing without authentication
        var authEnabled = configuration.GetValue<bool>("Security:EnableAuthentication", true);
        if (!authEnabled)
        {
            // Skip authentication if disabled - allow all requests through
            base.OnActionExecuting(context);
            return;
        }

        var apiKeyService = httpContext.RequestServices.GetRequiredService<ApiKeyService>();

        // Check for API key in header - ONLY API KEY, NO DEVICE ID
        var apiKey = httpContext.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (!string.IsNullOrEmpty(apiKey) && apiKeyService.ValidateApiKey(apiKey))
        {
            base.OnActionExecuting(context);
            return;
        }

        // Not authenticated - API Key required
        context.Result = new UnauthorizedObjectResult(new
        {
            error = "Authentication required",
            message = "Please provide X-Api-Key header"
        });
    }
}

/// <summary>
/// Global authentication middleware that handles API Key, Device, and Guest Session authentication
/// Can be globally disabled via Security:EnableAuthentication = false
/// </summary>
public class AuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AuthenticationMiddleware> _logger;
    private readonly ApiKeyService _apiKeyService;
    private readonly IConfiguration _configuration;

    // Endpoints that require authentication (API Key ONLY - Device ID and Guest Sessions not allowed)
    private readonly HashSet<string> _protectedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/management/cache/clear-all",
        "/api/management/cache/clear-cancel",
        "/api/management/database",
        "/api/management/reset-logs",
        "/api/management/process-all-logs",
        // NOTE: /api/management/cancel-processing is NOT protected - it must work even when database is locked
        "/api/management/logs/remove-service",
        "/api/auth/devices", // GET and DELETE require auth
        "/api/auth/revoke"
    };

    // Patterns for protected paths (contains check)
    private readonly string[] _protectedPatterns =
    {
        "/api/management/cache/clear",
        "/api/management/cache/delete"
    };

    // Public endpoints that don't require any authentication
    private readonly HashSet<string> _publicPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/auth/check",
        "/api/auth/register",
        "/api/auth/key",
        "/api/auth/guest/register"
    };

    public AuthenticationMiddleware(
        RequestDelegate next,
        ILogger<AuthenticationMiddleware> logger,
        ApiKeyService apiKeyService,
        IConfiguration configuration)
    {
        _next = next;
        _logger = logger;
        _apiKeyService = apiKeyService;
        _configuration = configuration;
    }

    public async Task InvokeAsync(HttpContext context, GuestSessionService guestSessionService)
    {
        var path = context.Request.Path.Value?.ToLower() ?? "";
        var apiKey = context.Request.Headers["X-Api-Key"].FirstOrDefault();

        // Skip authentication for swagger and metrics endpoints
        // These have their own dedicated authentication middleware
        if (path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/metrics", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // GLOBAL KILL SWITCH: Check if authentication is globally disabled
        // When Security:EnableAuthentication = false, ALL authentication is bypassed
        // This includes API Key, Device Auth, and Guest Sessions
        var authEnabled = _configuration.GetValue<bool>("Security:EnableAuthentication", true);
        if (!authEnabled)
        {
            // Skip all authentication checks if disabled - allow all requests through
            await _next(context);
            return;
        }

        // Skip validation for public endpoints
        if (_publicPaths.Contains(path))
        {
            await _next(context);
            return;
        }

        // Validate guest sessions (if using guest session ID, not API key)
        if (string.IsNullOrEmpty(apiKey))
        {
            var guestSessionId = context.Request.Headers["X-Guest-Session-Id"].FirstOrDefault();
            if (!string.IsNullOrEmpty(guestSessionId))
            {
                var (isValid, reason) = guestSessionService.ValidateSessionWithReason(guestSessionId);
                if (!isValid)
                {
                    // Only log warning if session exists but is revoked/expired (not if it doesn't exist)
                    if (reason == "revoked")
                    {
                        _logger.LogWarning("Revoked guest session attempt: {SessionId} from {IP}",
                            guestSessionId, context.Connection.RemoteIpAddress);
                    }
                    else if (reason == "expired")
                    {
                        _logger.LogWarning("Expired guest session attempt: {SessionId} from {IP}",
                            guestSessionId, context.Connection.RemoteIpAddress);
                    }
                    // If reason is null, session doesn't exist - don't log (could be deleted or invalid ID)

                    context.Response.StatusCode = 401;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            error = "Session revoked",
                            message = reason == "revoked"
                                ? "Your guest session has been revoked by an administrator."
                                : "Your guest session has expired. Please restart guest mode.",
                            code = "GUEST_SESSION_REVOKED"
                        }));
                    return;
                }
            }
        }

        // Check if this is a protected endpoint
        bool requiresAuth = false;

        // Check exact paths
        if (_protectedPaths.Contains(path))
        {
            requiresAuth = true;
        }

        // Check patterns
        if (!requiresAuth)
        {
            foreach (var pattern in _protectedPatterns)
            {
                if (path.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    requiresAuth = true;
                    break;
                }
            }
        }

        // Check DELETE method on certain controllers
        if (!requiresAuth && context.Request.Method == "DELETE")
        {
            if (path.StartsWith("/api/management", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/api/auth/devices", StringComparison.OrdinalIgnoreCase))
            {
                requiresAuth = true;
            }
        }

        if (requiresAuth)
        {
            // Check for API key in header - ONLY API KEY, NO DEVICE ID
            // (apiKey already extracted earlier in the method)
            if (!string.IsNullOrEmpty(apiKey) && _apiKeyService.ValidateApiKey(apiKey))
            {
                await _next(context);
                return;
            }

            // Not authenticated - API Key required
            _logger.LogWarning("Unauthorized access attempt to {Path} from {IP}",
                path, context.Connection.RemoteIpAddress);

            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    error = "Authentication required",
                    message = "Please provide X-Api-Key header",
                    path
                }));
            return;
        }

        await _next(context);
    }
}