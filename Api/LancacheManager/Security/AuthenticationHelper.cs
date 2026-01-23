namespace LancacheManager.Security;

/// <summary>
/// Centralized authentication helper for consistent auth checks across middleware and attributes.
/// </summary>
public class AuthenticationHelper
{
    private readonly ApiKeyService _apiKeyService;
    private readonly DeviceAuthService _deviceAuthService;
    private readonly ILogger<AuthenticationHelper> _logger;

    public AuthenticationHelper(
        ApiKeyService apiKeyService,
        DeviceAuthService deviceAuthService,
        ILogger<AuthenticationHelper> logger)
    {
        _apiKeyService = apiKeyService;
        _deviceAuthService = deviceAuthService;
        _logger = logger;
    }

    /// <summary>
    /// Authentication result with details about how authentication succeeded or why it failed.
    /// </summary>
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
    /// Attempts to authenticate via device session.
    /// </summary>
    public AuthResult ValidateDeviceSession(HttpContext context)
    {
        var deviceId = context.Session.GetString("DeviceId");

        if (string.IsNullOrEmpty(deviceId))
        {
            return new AuthResult(false, ErrorMessage: "No device session", StatusCode: 401);
        }

        if (!_deviceAuthService.ValidateDevice(deviceId))
        {
            return new AuthResult(false, ErrorMessage: "Invalid device session", StatusCode: 401);
        }

        return new AuthResult(true, AuthMethod.DeviceSession);
    }

    /// <summary>
    /// Attempts to authenticate via any supported method (API key or device session).
    /// </summary>
    public AuthResult ValidateAnyMethod(HttpContext context)
    {
        // Try API key first
        var apiKey = GetApiKeyFromHeader(context);
        if (!string.IsNullOrEmpty(apiKey))
        {
            if (_apiKeyService.ValidateApiKey(apiKey))
            {
                return new AuthResult(true, AuthMethod.ApiKey);
            }
            // API key was provided but invalid
            _logger.LogWarning("Invalid API key from {IP}", context.Connection.RemoteIpAddress);
            return new AuthResult(false, ErrorMessage: "Invalid API key", StatusCode: 403);
        }

        // Try device session
        var deviceId = context.Session.GetString("DeviceId");
        if (!string.IsNullOrEmpty(deviceId) && _deviceAuthService.ValidateDevice(deviceId))
        {
            return new AuthResult(true, AuthMethod.DeviceSession);
        }

        return new AuthResult(false, ErrorMessage: "Authentication required", StatusCode: 401);
    }

    /// <summary>
    /// Checks if request has any valid authentication without failing.
    /// Useful for optional authentication scenarios.
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
