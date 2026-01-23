using LancacheManager.Security;

namespace LancacheManager.Middleware;

/// <summary>
/// Middleware for Swagger endpoint protection
/// - In development: Allow full access for easier debugging
/// - In production: Requires authentication by default (Security:ProtectSwagger=true)
/// - Swagger UI provides built-in "Authorize" button for entering API key
/// </summary>
public class SwaggerAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SwaggerAuthenticationMiddleware> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;

    public SwaggerAuthenticationMiddleware(
        RequestDelegate next,
        ILogger<SwaggerAuthenticationMiddleware> logger,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _configuration = configuration;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context, AuthenticationHelper authHelper)
    {
        // Check if this is a swagger request
        if (!context.Request.Path.StartsWithSegments("/swagger"))
        {
            await _next(context);
            return;
        }

        // Always allow in development mode
        if (_environment.IsDevelopment())
        {
            await _next(context);
            return;
        }

        // Check if swagger protection is enabled (default: true for security)
        var protectSwagger = _configuration.GetValue<bool>("Security:ProtectSwagger", true);
        if (!protectSwagger)
        {
            await _next(context);
            return;
        }

        // Swagger protection is enabled - require authentication
        var result = authHelper.ValidateAnyMethod(context);
        if (result.IsAuthenticated)
        {
            await _next(context);
            return;
        }

        // Not authenticated - return 401
        _logger.LogWarning("Unauthorized swagger access attempt from {IP}",
            context.Connection.RemoteIpAddress);

        await AuthenticationHelper.WriteErrorResponseAsync(
            context, 401, "API key required to access Swagger documentation");
    }
}
