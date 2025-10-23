using LancacheManager.Security;

namespace LancacheManager.Middleware;

/// <summary>
/// Middleware for Swagger endpoint (currently allows full access)
/// - Users can view API documentation and swagger.json without authentication
/// - Swagger UI provides built-in "Authorize" button for entering API key or Device ID
/// - Authentication is required only for making actual API calls (protected by AuthenticationMiddleware)
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
        // Allow full access to /swagger endpoints
        // Swagger UI has built-in "Authorize" button for authentication
        // Users can view documentation freely but need auth to make actual API calls
        await _next(context);
    }
}
