namespace LancacheManager.Security;

/// <summary>
/// Centralized authentication helper for consistent auth checks across middleware.
/// Simplified — only API key validation remains (used by Metrics/Swagger middleware).
/// </summary>
public class AuthenticationHelper
{
    private readonly ApiKeyService _apiKeyService;
    private readonly ILogger<AuthenticationHelper> _logger;

    public AuthenticationHelper(
        ApiKeyService apiKeyService,
        ILogger<AuthenticationHelper> logger)
    {
        _apiKeyService = apiKeyService;
        _logger = logger;
    }

    public record AuthResult(
        bool IsAuthenticated,
        AuthMethod Method = AuthMethod.None,
        string? ErrorMessage = null,
        int StatusCode = 401);

    public enum AuthMethod
    {
        None,
        ApiKey,
        DeviceSession,
        GuestSession
    }

    /// <summary>
    /// Attempts to authenticate the request via API key.
    /// </summary>
    public AuthResult ValidateApiKey(HttpContext context)
    {
        var apiKey = GetApiKeyFromHeader(context);

        if (string.IsNullOrEmpty(apiKey))
        {
            return new AuthResult(false, ErrorMessage: "API key required", StatusCode: 401);
        }

        if (!_apiKeyService.ValidateApiKey(apiKey))
        {
            _logger.LogWarning("Invalid API key from {IP}", context.Connection.RemoteIpAddress);
            return new AuthResult(false, ErrorMessage: "Invalid API key", StatusCode: 403);
        }

        return new AuthResult(true, AuthMethod.ApiKey);
    }

    /// <summary>
    /// Attempts to authenticate via any supported method.
    /// Simplified to just API key validation.
    /// </summary>
    public AuthResult ValidateAnyMethod(HttpContext context)
    {
        return ValidateApiKey(context);
    }

    /// <summary>
    /// Checks if request has any valid authentication without failing.
    /// </summary>
    public bool IsAuthenticated(HttpContext context)
    {
        return ValidateAnyMethod(context).IsAuthenticated;
    }

    /// <summary>
    /// Gets the API key from request headers.
    /// </summary>
    public static string? GetApiKeyFromHeader(HttpContext context)
    {
        return context.Request.Headers["X-Api-Key"].FirstOrDefault();
    }

    /// <summary>
    /// Writes a standard JSON error response.
    /// </summary>
    public static async Task WriteErrorResponseAsync(
        HttpContext context,
        int statusCode,
        string errorMessage)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync($"{{\"error\":\"{errorMessage}\"}}");
    }
}
