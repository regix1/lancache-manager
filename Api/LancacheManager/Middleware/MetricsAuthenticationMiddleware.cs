using LancacheManager.Security;

namespace LancacheManager.Middleware;

/// <summary>
/// Middleware to optionally require API key authentication for Prometheus metrics endpoint
/// Controlled by Security__RequireAuthForMetrics environment variable
/// </summary>
public class MetricsAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MetricsAuthenticationMiddleware> _logger;

    public MetricsAuthenticationMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        ILogger<MetricsAuthenticationMiddleware> logger)
    {
        _next = next;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ApiKeyService apiKeyService)
    {
        // Only apply to /metrics endpoint
        if (!context.Request.Path.StartsWithSegments("/metrics"))
        {
            await _next(context);
            return;
        }

        // Check if authentication is required for metrics
        var requireAuth = _configuration.GetValue<bool>("Security:RequireAuthForMetrics", false);

        if (!requireAuth)
        {
            // Metrics are public, allow access
            await _next(context);
            return;
        }

        // Metrics require authentication - check for API key
        if (!context.Request.Headers.TryGetValue("X-Api-Key", out var extractedApiKey))
        {
            _logger.LogWarning("Metrics endpoint accessed without API key");
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"API key required for metrics. Set X-Api-Key header.\"}");
            return;
        }

        var validApiKey = apiKeyService.GetOrCreateApiKey();
        if (extractedApiKey != validApiKey)
        {
            _logger.LogWarning("Metrics endpoint accessed with invalid API key");
            context.Response.StatusCode = 403;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Invalid API key\"}");
            return;
        }

        // Valid API key, proceed
        await _next(context);
    }
}
