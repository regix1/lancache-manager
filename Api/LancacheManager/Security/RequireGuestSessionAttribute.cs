using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LancacheManager.Security;

/// <summary>
/// Attribute to require a valid session for read-only endpoints.
/// Allows:
/// - Authenticated users (valid API key via session or header)
/// - Guest users with a valid (non-expired, non-revoked) guest session
/// Respects Security:EnableAuthentication global setting.
/// </summary>
public class RequireGuestSessionAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var httpContext = context.HttpContext;
        var configuration = httpContext.RequestServices.GetRequiredService<IConfiguration>();
        var logger = httpContext.RequestServices.GetService<ILogger<RequireGuestSessionAttribute>>();

        // Check if authentication is globally disabled (Security:EnableAuthentication = false)
        var authEnabled = configuration.GetValue<bool>("Security:EnableAuthentication", true);
        if (!authEnabled)
        {
            base.OnActionExecuting(context);
            return;
        }

        // Priority 1: allow authenticated sessions (API key)
        var apiKeyService = httpContext.RequestServices.GetRequiredService<ApiKeyService>();

        // Session cookie
        var sessionDeviceId = httpContext.Session.GetString("DeviceId");
        var sessionApiKey = httpContext.Session.GetString("ApiKey");
        if (!string.IsNullOrEmpty(sessionDeviceId) &&
            !string.IsNullOrEmpty(sessionApiKey) &&
            apiKeyService.ValidateApiKey(sessionApiKey))
        {
            base.OnActionExecuting(context);
            return;
        }

        // API key header (Swagger / API clients)
        var apiKeyHeader = httpContext.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (!string.IsNullOrEmpty(apiKeyHeader) && apiKeyService.ValidateApiKey(apiKeyHeader))
        {
            base.OnActionExecuting(context);
            return;
        }

        // From here on, we need a device ID to validate device auth or guest sessions.
        // Prefer X-Device-Id header (API clients), but fall back to session DeviceId
        // for browser-based guest sessions where the cookie is authoritative.
        var deviceId = httpContext.Request.Headers["X-Device-Id"].FirstOrDefault();

        // Some browser requests (notably <img> tags) cannot send custom headers.
        // Allow deviceId to be provided via querystring for read-only endpoints.
        // Example: /api/game-images/123/header?deviceId=...
        if (string.IsNullOrEmpty(deviceId))
        {
            deviceId = httpContext.Request.Query["deviceId"].FirstOrDefault();
        }
        if (string.IsNullOrEmpty(deviceId))
        {
            var authMode = httpContext.Session.GetString("AuthMode");
            var sessionDeviceIdFallback = httpContext.Session.GetString("DeviceId");
            if (authMode == "guest" && !string.IsNullOrEmpty(sessionDeviceIdFallback))
            {
                deviceId = sessionDeviceIdFallback;
            }
        }

        if (string.IsNullOrEmpty(deviceId))
        {
            logger?.LogWarning("[RequireGuestSession] No device ID provided");
            context.Result = new UnauthorizedObjectResult(new
            {
                error = "Device ID required",
                message = "Please provide X-Device-Id header",
                code = "DEVICE_ID_REQUIRED"
            });
            return;
        }

        // If this device is an authenticated device, allow.
        var deviceAuthService = httpContext.RequestServices.GetRequiredService<DeviceAuthService>();
        if (deviceAuthService.ValidateDevice(deviceId))
        {
            base.OnActionExecuting(context);
            return;
        }

        // Priority 2: require a valid guest session
        var guestSessionService = httpContext.RequestServices.GetRequiredService<GuestSessionService>();
        var guestSession = guestSessionService.GetSessionByDeviceId(deviceId);
        if (guestSession == null)
        {
            logger?.LogWarning("[RequireGuestSession] No guest session found for device {DeviceId}", deviceId);
            context.Result = new UnauthorizedObjectResult(new
            {
                error = "Guest session required",
                message = "Please start guest mode to access this endpoint.",
                code = "GUEST_SESSION_REQUIRED"
            });
            return;
        }

        var (isValid, reason) = guestSessionService.ValidateSessionWithReason(deviceId);
        if (!isValid)
        {
            logger?.LogWarning("[RequireGuestSession] Invalid guest session for device {DeviceId}: {Reason}", deviceId, reason);

            var code = reason switch
            {
                "revoked" => "GUEST_SESSION_REVOKED",
                "expired" => "GUEST_SESSION_EXPIRED",
                _ => "GUEST_SESSION_INVALID"
            };

            context.Result = new UnauthorizedObjectResult(new
            {
                error = reason == "expired" ? "Session expired" : "Session invalid",
                message = reason == "expired"
                    ? "Your guest session has expired. Please restart guest mode."
                    : "Your guest session is no longer valid.",
                code
            });
            return;
        }

        base.OnActionExecuting(context);
    }
}

