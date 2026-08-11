using System.Threading.RateLimiting;

namespace LancacheManager.Security;

/// <summary>
/// Centralized authentication helper for consistent auth checks across middleware.
/// Simplified - only API key validation remains (used by metrics middleware).
/// </summary>
public class AuthenticationHelper
{
    /// <summary>
    /// Wrong keys allowed from one caller address before that address is answered 429 instead of
    /// being told the key is wrong. The rate limiting attributes cover three endpoints; the key
    /// itself is checked on every route that carries the header, which is all of them, so the count
    /// lives with the check rather than on an endpoint.
    ///
    /// A key is 32 bytes of randomness, so this is not what makes guessing one infeasible. What it
    /// buys is that a flood costs the installation a fixed amount of work and that the failures are
    /// counted rather than only logged.
    ///
    /// Static because this class is scoped and a window that lasts one request counts nothing.
    /// Kestrel reports no address at all only for a request that arrived over a Unix socket; those
    /// are not throttled, rather than sharing one window with every other address-less caller, which
    /// would let one bad script lock the rest of them out. Reaching that socket already means access
    /// to the host.
    /// </summary>
    private static readonly PartitionedRateLimiter<HttpContext> _invalidKeys =
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
            context.Connection.RemoteIpAddress is { } address
                ? RateLimitPartition.GetFixedWindowLimiter(address.ToString(), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                })
                : RateLimitPartition.GetNoLimiter<string>(string.Empty));

    private readonly ApiKeyService _apiKeyService;
    private readonly ILogger<AuthenticationHelper> _logger;

    private bool? _invalidKeysThrottled;

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
        ApiKey
    }

    /// <summary>
    /// Attempts to authenticate the request via API key.
    /// </summary>
    public AuthResult ValidateApiKey(HttpContext context)
    {
        var apiKey = ExtractApiKey(context);

        if (string.IsNullOrEmpty(apiKey))
        {
            return new AuthResult(false, ErrorMessage: "API key required", StatusCode: 401);
        }

        if (!_apiKeyService.ValidateApiKey(apiKey))
        {
            _logger.LogWarning("Invalid API key from {IP}", context.Connection.RemoteIpAddress);

            // One request can arrive here twice. The authentication handler checks the key on every
            // route that carries the header, and the setup endpoints and the metrics gate check it
            // again themselves, because both have to hold while authentication is turned off.
            // Counting both would spend two of the ten on a single wrong key and halve the budget on
            // exactly those routes, so the window is taken on the first check of a request and the
            // answer repeated after that. The field lasts one request, this class being scoped.
            if (_invalidKeysThrottled is null)
            {
                using var attempt = _invalidKeys.AttemptAcquire(context);
                _invalidKeysThrottled = !attempt.IsAcquired;
            }

            return _invalidKeysThrottled.Value
                ? new AuthResult(false, ErrorMessage: "Too many invalid API keys", StatusCode: 429)
                : new AuthResult(false, ErrorMessage: "Invalid API key", StatusCode: 403);
        }

        return new AuthResult(true, AuthMethod.ApiKey);
    }

    /// <summary>
    /// Gets the API key from request headers.
    /// Accepts X-Api-Key header (primary) or Authorization: Bearer &lt;key&gt; (Prometheus convention).
    /// </summary>
    private static string? ExtractApiKey(HttpContext context)
    {
        var apiKey = context.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (apiKey != null)
            return apiKey;

        // Support Authorization: Bearer <key> so Prometheus scrape_config can use the
        // standard `authorization: { type: Bearer, credentials: <key> }` block.
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
            return authHeader["Bearer ".Length..].Trim();

        return null;
    }

    /// <summary>
    /// Writes a standard JSON error response.
    /// </summary>
    public static async Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string errorMessage)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync($"{{\"error\":\"{errorMessage}\"}}");
    }
}
