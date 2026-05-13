using LancacheManager.Core.Interfaces;
using LancacheManager.Security;

namespace LancacheManager.Middleware;

/// <summary>
/// Optional API-key gate for the Prometheus metrics endpoint (/metrics).
///
/// The endpoint itself is marked .AllowAnonymous() in Program.cs so the authorization
/// FallbackPolicy (RequireAuthenticatedUser) does not reject scrape requests. This middleware
/// is what enforces the API-key requirement when it is enabled.
///
/// Configuration priority:
/// 1. UI toggle (StateService) - if set via UI, takes precedence
/// 2. Environment variable / appsettings.json (Security:RequireAuthForMetrics) - default fallback
///
/// Values:
/// - false (default): metrics are PUBLIC - no authentication required
/// - true: metrics require an API key via X-Api-Key header or Authorization: Bearer &lt;key&gt;
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
        if (!context.Request.Path.StartsWithSegments("/metrics"))
        {
            await _next(context);
            return;
        }

        var uiToggleValue = stateRepository.GetRequireAuthForMetrics();
        var configValue = _configuration.GetValue<bool>("Security:RequireAuthForMetrics", false);
        var requireAuth = uiToggleValue ?? configValue;

        if (!requireAuth)
        {
            await _next(context);
            return;
        }

        var result = authHelper.ValidateApiKey(context);
        if (result.IsAuthenticated)
        {
            await _next(context);
            return;
        }

        _logger.LogWarning("Metrics endpoint accessed without valid API key from {IP}",
            context.Connection.RemoteIpAddress);

        await AuthenticationHelper.WriteErrorResponseAsync(
            context, result.StatusCode, result.ErrorMessage ?? "API key required for metrics");
    }
}
