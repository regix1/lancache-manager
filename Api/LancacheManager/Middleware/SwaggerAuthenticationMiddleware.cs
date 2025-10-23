using LancacheManager.Security;

namespace LancacheManager.Middleware;

/// <summary>
/// Middleware to require authentication for Swagger documentation endpoints
/// Always requires either X-Api-Key or X-Device-Id header
/// </summary>
public class SwaggerAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SwaggerAuthenticationMiddleware> _logger;

    public SwaggerAuthenticationMiddleware(
        RequestDelegate next,
        ILogger<SwaggerAuthenticationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ApiKeyService apiKeyService, DeviceAuthService deviceAuthService)
    {
        // Only apply to /swagger endpoints
        if (!context.Request.Path.StartsWithSegments("/swagger"))
        {
            await _next(context);
            return;
        }

        // Swagger ALWAYS requires authentication - check for API key first
        if (context.Request.Headers.TryGetValue("X-Api-Key", out var extractedApiKey))
        {
            if (apiKeyService.ValidateApiKey(extractedApiKey))
            {
                // Valid API key, proceed
                await _next(context);
                return;
            }
        }

        // Check for device ID
        if (context.Request.Headers.TryGetValue("X-Device-Id", out var deviceId))
        {
            if (deviceAuthService.ValidateDevice(deviceId))
            {
                // Valid device ID, proceed
                await _next(context);
                return;
            }
        }

        // Not authenticated
        _logger.LogWarning("Unauthorized access attempt to /swagger from {IP}", context.Connection.RemoteIpAddress);
        context.Response.StatusCode = 401;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"error\":\"Authentication required\",\"message\":\"Please provide either X-Api-Key or X-Device-Id header\"}");
    }
}
