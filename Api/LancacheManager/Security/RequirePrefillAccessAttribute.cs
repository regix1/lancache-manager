using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LancacheManager.Security;

/// <summary>
/// Attribute to require Prefill access on controller endpoints.
/// Allows:
/// - Authenticated users (with valid API key/session)
/// - Guest users with PrefillEnabled permission
/// Respects Security:EnableAuthentication global setting.
/// </summary>
public class RequirePrefillAccessAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var httpContext = context.HttpContext;
        var configuration = httpContext.RequestServices.GetRequiredService<IConfiguration>();
        var logger = httpContext.RequestServices.GetService<ILogger<RequirePrefillAccessAttribute>>();

        // Check if authentication is globally disabled (Security:EnableAuthentication = false)
        var authEnabled = configuration.GetValue<bool>("Security:EnableAuthentication", true);
        if (!authEnabled)
        {
            base.OnActionExecuting(context);
            return;
        }

        // Prefer X-Device-Id header, but fall back to session DeviceId for browser guest flows.
        var deviceId = httpContext.Request.Headers["X-Device-Id"].FirstOrDefault();
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
            logger?.LogWarning("[RequirePrefillAccess] No device ID provided");
            context.Result = new UnauthorizedObjectResult(new
            {
                error = "Device ID required",
                message = "Please provide X-Device-Id header"
            });
            return;
        }

        // Priority 1: Check if user is fully authenticated (API key)
        var apiKeyService = httpContext.RequestServices.GetRequiredService<ApiKeyService>();
        var deviceAuthService = httpContext.RequestServices.GetRequiredService<DeviceAuthService>();

        // Check session cookie
        var sessionDeviceId = httpContext.Session.GetString("DeviceId");
        var sessionApiKey = httpContext.Session.GetString("ApiKey");

        if (!string.IsNullOrEmpty(sessionDeviceId) && !string.IsNullOrEmpty(sessionApiKey))
        {
            if (apiKeyService.ValidateApiKey(sessionApiKey))
            {
                logger?.LogDebug("[RequirePrefillAccess] Authenticated via session for device {DeviceId}", deviceId);
                base.OnActionExecuting(context);
                return;
            }
        }

        // Check API key header
        var apiKeyHeader = httpContext.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (!string.IsNullOrEmpty(apiKeyHeader) && apiKeyService.ValidateApiKey(apiKeyHeader))
        {
            logger?.LogDebug("[RequirePrefillAccess] Authenticated via API key for device {DeviceId}", deviceId);
            base.OnActionExecuting(context);
            return;
        }

        // Check device auth
        if (deviceAuthService.ValidateDevice(deviceId))
        {
            logger?.LogDebug("[RequirePrefillAccess] Authenticated via device auth for device {DeviceId}", deviceId);
            base.OnActionExecuting(context);
            return;
        }

        // Priority 2: Check if guest has prefill permission
        var guestSessionService = httpContext.RequestServices.GetRequiredService<GuestSessionService>();
        var guestSession = guestSessionService.GetSessionByDeviceId(deviceId);

        if (guestSession != null)
        {
            // Check if guest session is valid
            var (isValid, reason) = guestSessionService.ValidateSessionWithReason(deviceId);

            if (!isValid)
            {
                logger?.LogWarning("[RequirePrefillAccess] Invalid guest session for device {DeviceId}: {Reason}", deviceId, reason);
                context.Result = new UnauthorizedObjectResult(new
                {
                    error = reason == "expired" ? "Session expired" : "Session invalid",
                    message = reason == "expired"
                        ? "Your guest session has expired. Please restart guest mode."
                        : "Your guest session is no longer valid.",
                    code = reason == "expired" ? "GUEST_SESSION_EXPIRED" : "GUEST_SESSION_INVALID"
                });
                return;
            }

            // Check if guest has prefill permission
            if (guestSession.PrefillEnabled && !guestSession.IsPrefillExpired)
            {
                logger?.LogDebug("[RequirePrefillAccess] Guest with prefill permission granted for device {DeviceId}", deviceId);
                base.OnActionExecuting(context);
                return;
            }
            else
            {
                logger?.LogWarning("[RequirePrefillAccess] Guest without prefill permission denied for device {DeviceId}", deviceId);
                context.Result = new ObjectResult(new
                {
                    error = "Prefill access denied",
                    message = guestSession.IsPrefillExpired
                        ? "Your prefill access has expired. Please contact an administrator."
                        : "Prefill access is not enabled for your guest session.",
                    code = "PREFILL_ACCESS_DENIED"
                })
                {
                    StatusCode = 403
                };
                return;
            }
        }

        // No valid authentication or guest session
        logger?.LogWarning("[RequirePrefillAccess] No valid authentication for device {DeviceId}", deviceId);
        context.Result = new UnauthorizedObjectResult(new
        {
            error = "Authentication required",
            message = "Please authenticate or use a guest session with prefill permission",
            code = "AUTH_REQUIRED"
        });
    }
}
