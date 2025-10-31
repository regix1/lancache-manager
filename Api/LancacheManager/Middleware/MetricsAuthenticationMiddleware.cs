using LancacheManager.Security;

namespace LancacheManager.Middleware;

/// <summary>
/// Middleware to optionally require API key authentication for Prometheus metrics endpoint (/metrics)
///
/// Configuration:
/// - Security:RequireAuthForMetrics = false (default): Metrics are PUBLIC - no authentication required
/// - Security:RequireAuthForMetrics = true: Metrics require API Key in X-Api-Key header
///
/// Use Cases:
/// - false: Prometheus/Grafana can scrape metrics without authentication (common setup)
/// - true: Secure metrics endpoint with API key (for internet-exposed instances)
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
        // Only apply to /metrics endpoint - all other paths skip this middleware
        if (!context.Request.Path.StartsWithSegments("/metrics"))
        {
            await _next(context);
            return;
        }

        // Check if authentication is required for metrics (default: false - public access)
        // This is SEPARATE from Security:EnableAuthentication - metrics can be public while app is secured
        var requireAuth = _configuration.GetValue<bool>("Security:RequireAuthForMetrics", false);

        if (!requireAuth)
        {
            // Metrics are public - allow Prometheus/Grafana to scrape without authentication
            await _next(context);
            return;
        }

        // Metrics require authentication - check for API key
        var apiKey = context.Request.Headers["X-Api-Key"].FirstOrDefault();

        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("Metrics endpoint accessed without API key from {IP}",
                context.Connection.RemoteIpAddress);
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"API key required for metrics. Set X-Api-Key header.\"}");
            return;
        }

        if (!apiKeyService.ValidateApiKey(apiKey))
        {
            _logger.LogWarning("Metrics endpoint accessed with invalid API key from {IP}",
                context.Connection.RemoteIpAddress);
            context.Response.StatusCode = 403;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Invalid API key\"}");
            return;
        }

        // Valid API key, proceed
        await _next(context);
    }
}
