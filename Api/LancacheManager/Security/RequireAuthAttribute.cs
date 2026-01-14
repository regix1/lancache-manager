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

        // Priority 1: Check session cookie
        var sessionDeviceId = httpContext.Session.GetString("DeviceId");
        var sessionApiKey = httpContext.Session.GetString("ApiKey");

        if (!string.IsNullOrEmpty(sessionDeviceId) && !string.IsNullOrEmpty(sessionApiKey))
        {
            var apiKeyService = httpContext.RequestServices.GetRequiredService<ApiKeyService>();
            if (apiKeyService.ValidateApiKey(sessionApiKey))
            {
                // Valid session - allow through
                base.OnActionExecuting(context);
                return;
            }
        }

        // Priority 2: Check for API key in header (for Swagger UI and API clients)
        var apiKeyHeader = httpContext.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (!string.IsNullOrEmpty(apiKeyHeader))
        {
            var apiKeyService = httpContext.RequestServices.GetRequiredService<ApiKeyService>();
            var isValid = apiKeyService.ValidateApiKey(apiKeyHeader);
            
            var logger = httpContext.RequestServices.GetService<ILogger<RequireAuthAttribute>>();
            logger?.LogDebug("[RequireAuth] X-Api-Key header present, validation result: {IsValid}", isValid);
            
            if (isValid)
            {
                base.OnActionExecuting(context);
                return;
            }
        }

        // Not authenticated - API Key or session required
        context.Result = new UnauthorizedObjectResult(new
        {
            error = "Authentication required",
            message = "Please authenticate with a valid session or API key"
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
        "/api/management/logs/remove-service"
    };

    // Patterns for protected paths (contains check)
    private readonly string[] _protectedPatterns =
    {
        "/api/management/cache/clear",
        "/api/management/cache/delete"
    };

    private static bool IsPublicEndpoint(HttpContext context, string path)
    {
        // Keep this list small and method-specific.
        var method = context.Request.Method;

        // Auth status + guest availability checks (bootstrap)
        if (HttpMethods.IsGet(method) && path.Equals("/api/auth/status", StringComparison.OrdinalIgnoreCase))
            return true;
        if (HttpMethods.IsGet(method) && path.Equals("/api/auth/guest/status", StringComparison.OrdinalIgnoreCase))
            return true;
        if (HttpMethods.IsPost(method) && path.Equals("/api/auth/clear-session", StringComparison.OrdinalIgnoreCase))
            return true;

        // App bootstrap UX (unauthenticated)
        if (HttpMethods.IsGet(method) && path.StartsWith("/api/themes", StringComparison.OrdinalIgnoreCase))
            return true;
        if (HttpMethods.IsGet(method) && path.Equals("/api/user-preferences", StringComparison.OrdinalIgnoreCase))
            return true;
        if (HttpMethods.IsGet(method) && path.Equals("/api/config", StringComparison.OrdinalIgnoreCase))
            return true;
        if (HttpMethods.IsGet(method) && path.Equals("/api/version", StringComparison.OrdinalIgnoreCase))
            return true;
        if (HttpMethods.IsGet(method) && path.Equals("/api/system/refresh-rate", StringComparison.OrdinalIgnoreCase))
            return true;
        if (HttpMethods.IsGet(method) && path.Equals("/api/steam-auth/status", StringComparison.OrdinalIgnoreCase))
            return true;

        // Device registration (authenticated mode bootstrap)
        if (HttpMethods.IsPost(method) && path.Equals("/api/devices", StringComparison.OrdinalIgnoreCase))
            return true;

        // Guest session registration endpoint
        if (HttpMethods.IsPost(method) && path.Equals("/api/sessions", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

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

    public async Task InvokeAsync(
        HttpContext context,
        GuestSessionService guestSessionService,
        DeviceAuthService deviceAuthService)
    {
        var path = context.Request.Path.Value?.ToLower() ?? "";

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

        // Skip validation for explicit public endpoints
        if (IsPublicEndpoint(context, path))
        {
            await _next(context);
            return;
        }

        // Priority 1: Check session cookie
        var sessionDeviceId = context.Session.GetString("DeviceId");
        var sessionApiKey = context.Session.GetString("ApiKey");
        bool isSessionValid = false;

        if (!string.IsNullOrEmpty(sessionDeviceId) && !string.IsNullOrEmpty(sessionApiKey))
        {
            if (_apiKeyService.ValidateApiKey(sessionApiKey))
            {
                isSessionValid = true;
            }
        }

        // Priority 2: Check for API key in header (backward compatibility)
        var apiKeyHeader = context.Request.Headers["X-Api-Key"].FirstOrDefault();
        bool isApiKeyValid = false;

        if (!string.IsNullOrEmpty(apiKeyHeader) && _apiKeyService.ValidateApiKey(apiKeyHeader))
        {
            isApiKeyValid = true;
        }

        // Device ID - for guests, device ID = session ID.
        // Most API calls send X-Device-Id, but some browser-initiated requests (notably <img>)
        // cannot attach custom headers. Support safe fallbacks for those cases.
        var deviceId = context.Request.Headers["X-Device-Id"].FirstOrDefault();

        // Fallback 1: guest session cookie (same-site) - if server session indicates guest mode.
        if (string.IsNullOrEmpty(deviceId))
        {
            var sessionAuthMode = context.Session.GetString("AuthMode");
            var sessionDeviceIdFallback = context.Session.GetString("DeviceId");
            if (sessionAuthMode == "guest" && !string.IsNullOrEmpty(sessionDeviceIdFallback))
            {
                deviceId = sessionDeviceIdFallback;
            }
        }


        // Priority 3: Validate registered devices (device auth)
        bool isDeviceValid = false;
        if (!string.IsNullOrEmpty(deviceId) && deviceAuthService.ValidateDevice(deviceId))
        {
            isDeviceValid = true;
        }

        // Priority 4: Validate guest sessions (only when not authenticated)
        bool isGuestValid = false;

        if (!isSessionValid && !isApiKeyValid && !isDeviceValid && !string.IsNullOrEmpty(deviceId))
        {
            // For guests, the device ID IS the session ID (we use device fingerprint as session ID)
            var (isValid, reason) = guestSessionService.ValidateSessionWithReason(deviceId);
            if (isValid)
            {
                // Valid guest session - allow through
                isGuestValid = true;
                await _next(context);
                return;
            }
            else if (!string.IsNullOrEmpty(reason))
            {
                // Only block if session exists but is revoked/expired (not if it doesn't exist)
                if (reason == "revoked")
                {
                    _logger.LogWarning("Revoked guest session attempt: {SessionId} from {IP}",
                        deviceId, context.Connection.RemoteIpAddress);

                    context.Response.StatusCode = 401;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            error = "Session revoked",
                            message = "Your guest session has been revoked.",
                            code = "GUEST_SESSION_REVOKED"
                        }));
                    return;
                }
                else if (reason == "expired")
                {
                    _logger.LogWarning("Expired guest session attempt: {SessionId} from {IP}",
                        deviceId, context.Connection.RemoteIpAddress);

                    context.Response.StatusCode = 401;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            error = "Session expired",
                            message = "Your guest session has expired. Please restart guest mode.",
                            code = "GUEST_SESSION_EXPIRED"
                        }));
                    return;
                }
                // If reason is "not_found", treat as unauthenticated below
            }
        }

        // Default behavior: Any /api/* endpoint that is not explicitly public requires at least
        // a valid authenticated session OR a valid device auth OR a valid guest session.
        //
        // This prevents OutputCache from serving cached controller responses to unauthenticated users
        // and provides defense-in-depth in case a controller action is missing a [Require*] attribute.
        if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            if (isSessionValid || isApiKeyValid || isDeviceValid || isGuestValid)
            {
                await _next(context);
                return;
            }

            _logger.LogWarning("Unauthorized API access attempt to {Path} from {IP}",
                path, context.Connection.RemoteIpAddress);

            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    error = "Authentication required",
                    message = "Please authenticate or start guest mode",
                    path
                }));
            return;
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
            // Check if authenticated via any method (session, API key header, or guest)
            if (isSessionValid || isApiKeyValid)
            {
                await _next(context);
                return;
            }

            // Not authenticated - authentication required
            _logger.LogWarning("Unauthorized access attempt to {Path} from {IP}",
                path, context.Connection.RemoteIpAddress);

            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    error = "Authentication required",
                    message = "Please authenticate with a valid session or API key",
                    path
                }));
            return;
        }

        await _next(context);
    }
}
