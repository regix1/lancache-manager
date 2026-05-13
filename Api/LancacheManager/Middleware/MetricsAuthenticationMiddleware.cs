using System.Security.Claims;
using LancacheManager.Core.Interfaces;
using LancacheManager.Security;

namespace LancacheManager.Middleware;

/// <summary>
/// Middleware to optionally require API key authentication for Prometheus metrics endpoint (/metrics).
///
/// This middleware MUST be registered before UseAuthorization() in Program.cs so it can set
/// context.User before the FallbackPolicy (RequireAuthenticatedUser) is evaluated.
///
/// Configuration Priority:
/// 1. UI Toggle (StateService) - if set via UI, takes precedence
/// 2. Environment Variable / appsettings.json (Security:RequireAuthForMetrics) - default fallback
///
/// Values:
/// - false (default): Metrics are PUBLIC - a synthetic principal is set so the FallbackPolicy
///   is satisfied without requiring any credentials from the scraper.
/// - true: Metrics require an API key via X-Api-Key header or Authorization: Bearer &lt;key&gt;.
///
/// Use Cases:
/// - false: Prometheus/Grafana can scrape metrics without authentication (common LAN setup)
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

    public async Task InvokeAsync(HttpContext context, AuthenticationHelper authHelper, IStateService stateRepository)
    {
        // Only apply to /metrics endpoint - all other paths skip this middleware
        if (!context.Request.Path.StartsWithSegments("/metrics"))
        {
            await _next(context);
            return;
        }

        // Check if authentication is required for metrics
        // Priority: UI toggle (StateService) > env var/config (IConfiguration)
        var uiToggleValue = stateRepository.GetRequireAuthForMetrics();
        var configValue = _configuration.GetValue<bool>("Security:RequireAuthForMetrics", false);
        var requireAuth = uiToggleValue ?? configValue;

        if (!requireAuth)
        {
            // Metrics are public — set a synthetic authenticated principal so the
            // FallbackPolicy (RequireAuthenticatedUser) does not reject the request.
            context.User = CreateMetricsPrincipal();
            await _next(context);
            return;
        }

        // Metrics require authentication — validate API key (X-Api-Key or Authorization: Bearer).
        var result = authHelper.ValidateApiKey(context);
        if (result.IsAuthenticated)
        {
            context.User = CreateMetricsPrincipal();
            await _next(context);
            return;
        }

        _logger.LogWarning("Metrics endpoint accessed without valid API key from {IP}",
            context.Connection.RemoteIpAddress);

        await AuthenticationHelper.WriteErrorResponseAsync(
            context, result.StatusCode, result.ErrorMessage ?? "API key required for metrics");
    }

    private static ClaimsPrincipal CreateMetricsPrincipal() =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "prometheus-scraper")],
            authenticationType: "Metrics"));
}
