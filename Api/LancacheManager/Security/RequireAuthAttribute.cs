using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LancacheManager.Security;

public class RequireAuthAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var httpContext = context.HttpContext;
        var configuration = httpContext.RequestServices.GetRequiredService<IConfiguration>();

        // Check if authentication is globally disabled
        var authEnabled = configuration.GetValue<bool>("Security:EnableAuthentication", true);
        if (!authEnabled)
        {
            // Skip authentication if disabled
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

public class AuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AuthenticationMiddleware> _logger;
    private readonly ApiKeyService _apiKeyService;
    private readonly IConfiguration _configuration;

    // Endpoints that require authentication (API Key ONLY)
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

    public async Task InvokeAsync(HttpContext context)
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

        // Check if authentication is globally disabled
        var authEnabled = _configuration.GetValue<bool>("Security:EnableAuthentication", true);
        if (!authEnabled)
        {
            // Skip all authentication checks if disabled
            await _next(context);
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
            // Check for API key in header - ONLY API KEY, NO DEVICE ID
            var apiKey = context.Request.Headers["X-Api-Key"].FirstOrDefault();
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