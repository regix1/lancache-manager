using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// Simplified RESTful controller for authentication status checking
/// Note: Device registration, session management, and API key operations have been moved to:
/// - POST /api/devices (device registration)
/// - GET/POST/PATCH/DELETE /api/sessions (session management)
/// - GET/POST /api/api-keys (API key management)
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly ApiKeyService _apiKeyService;
    private readonly DeviceAuthService _deviceAuthService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;
    private readonly AppDbContext _dbContext;
    private readonly StateService _stateService;
    private readonly GuestSessionService _guestSessionService;
    private readonly ISignalRNotificationService _notifications;

    public AuthController(
        ApiKeyService apiKeyService,
        DeviceAuthService deviceAuthService,
        IConfiguration configuration,
        ILogger<AuthController> logger,
        AppDbContext dbContext,
        StateService stateService,
        GuestSessionService guestSessionService,
        ISignalRNotificationService notifications)
    {
        _apiKeyService = apiKeyService;
        _deviceAuthService = deviceAuthService;
        _configuration = configuration;
        _logger = logger;
        _dbContext = dbContext;
        _stateService = stateService;
        _guestSessionService = guestSessionService;
        _notifications = notifications;
    }

    /// <summary>
    /// GET /api/auth/status - Check if authentication is required and if current session is authenticated
    /// RESTful: This is now a status/query endpoint, properly named
    /// </summary>
    [HttpGet("status")]
    public IActionResult CheckAuthStatus()
    {
        // Check if authentication is enabled
        var authEnabled = _configuration.GetValue<bool>("Security:EnableAuthentication", true);

        // Check if already authenticated (do this first, before slow DB checks)
        bool isAuthenticated = false;
        string? authenticationType = null;
        string? deviceId = null;

        // Priority 1: Check session cookie
        var sessionDeviceId = HttpContext.Session.GetString("DeviceId");
        var sessionApiKey = HttpContext.Session.GetString("ApiKey");

        if (!string.IsNullOrEmpty(sessionDeviceId) && !string.IsNullOrEmpty(sessionApiKey))
        {
            if (_apiKeyService.ValidateApiKey(sessionApiKey))
            {
                isAuthenticated = true;
                authenticationType = "session";
                deviceId = sessionDeviceId;
            }
        }

        // Priority 2: Check for API key in header (backward compatibility)
        if (!isAuthenticated)
        {
            var apiKeyHeader = Request.Headers["X-Api-Key"].FirstOrDefault();
            if (!string.IsNullOrEmpty(apiKeyHeader) && _apiKeyService.ValidateApiKey(apiKeyHeader))
            {
                isAuthenticated = true;
                authenticationType = "api-key";
                deviceId = Request.Headers["X-Device-Id"].FirstOrDefault();
            }
        }

        // Priority 3: Auto-restore session if device is registered (handles app restart)
        // When app restarts, in-memory sessions are lost but devices persist in DB
        if (!isAuthenticated)
        {
            var requestDeviceId = Request.Headers["X-Device-Id"].FirstOrDefault();
            if (!string.IsNullOrEmpty(requestDeviceId))
            {
                // Check if this device is registered and valid in the database
                if (_deviceAuthService.ValidateDevice(requestDeviceId))
                {
                    // Device exists and is valid - restore session
                    var apiKey = _apiKeyService.GetOrCreateApiKey();
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        HttpContext.Session.SetString("DeviceId", requestDeviceId);
                        HttpContext.Session.SetString("ApiKey", apiKey);
                        HttpContext.Session.SetString("AuthMode", "authenticated");

                        isAuthenticated = true;
                        authenticationType = "session-restored";
                        deviceId = requestDeviceId;

                        _logger.LogInformation("Auto-restored session for device {DeviceId} after app restart", requestDeviceId);
                    }
                }
            }
        }

        // Priority 4: Check for guest session (handles guest mode page refresh)
        string? authMode = null;
        int? guestTimeRemaining = null;
        bool? prefillEnabled = null;
        int? prefillTimeRemaining = null;
        if (!isAuthenticated)
        {
            var requestDeviceId = Request.Headers["X-Device-Id"].FirstOrDefault();
            if (!string.IsNullOrEmpty(requestDeviceId))
            {
                // Check if this is a valid guest session
                var (isValidGuest, reason) = _guestSessionService.ValidateSessionWithReason(requestDeviceId);
                if (isValidGuest)
                {
                    // Valid guest session - set mode and calculate time remaining
                    authMode = "guest";
                    var guestSession = _guestSessionService.GetSessionByDeviceId(requestDeviceId);
                    if (guestSession != null)
                    {
                        var remaining = guestSession.ExpiresAt - DateTime.UtcNow;
                        guestTimeRemaining = (int)Math.Ceiling(remaining.TotalMinutes);

                        // Check prefill permission
                        prefillEnabled = guestSession.PrefillEnabled && !guestSession.IsPrefillExpired;
                        if (prefillEnabled == true && guestSession.PrefillExpiresAt.HasValue)
                        {
                            var prefillRemaining = guestSession.PrefillExpiresAt.Value - DateTime.UtcNow;
                            prefillTimeRemaining = (int)Math.Ceiling(prefillRemaining.TotalMinutes);
                            if (prefillTimeRemaining <= 0)
                            {
                                prefillEnabled = false;
                                prefillTimeRemaining = null;
                            }
                        }
                    }
                    _logger.LogDebug("Valid guest session found for device {DeviceId}, expires in {Minutes} minutes, prefill={Prefill}", requestDeviceId, guestTimeRemaining, prefillEnabled);
                }
                else if (reason == "expired")
                {
                    authMode = "expired";
                    _logger.LogDebug("Guest session expired for device {DeviceId}", requestDeviceId);
                }
            }
        }

        // Check for guest mode eligibility (these might be slow, so do them last)
        bool hasData = false;
        bool hasEverBeenSetup = false;
        bool hasBeenInitialized = false;
        bool hasDataLoaded = false;

        try
        {
            hasData = _dbContext.Downloads.Any();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check if database has data");
        }

        try
        {
            hasEverBeenSetup = _deviceAuthService.HasAnyDeviceEverBeenRegistered();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check if any device has been registered");
        }

        try
        {
            hasBeenInitialized = _stateService.GetSetupCompleted();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check if setup has been completed");
        }

        try
        {
            hasDataLoaded = _stateService.HasDataLoaded();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check if data has been loaded");
        }

        // Check if device is banned from prefill
        bool isBanned = false;
        var checkDeviceId = deviceId ?? Request.Headers["X-Device-Id"].FirstOrDefault();
        if (!string.IsNullOrEmpty(checkDeviceId))
        {
            try
            {
                // Check 1: Direct device ID match in bans
                isBanned = _dbContext.BannedSteamUsers
                    .Any(b => b.BannedDeviceId == checkDeviceId && 
                              !b.IsLifted && 
                              (b.ExpiresAtUtc == null || b.ExpiresAtUtc > DateTime.UtcNow));

                // Check 2: If not found by device ID, check if any sessions from this device
                // have a username that is banned (handles same user, different device scenarios)
                if (!isBanned)
                {
                    var usernames = _dbContext.PrefillSessions
                        .Where(s => s.DeviceId == checkDeviceId && !string.IsNullOrEmpty(s.SteamUsername))
                        .Select(s => s.SteamUsername!.ToLower())
                        .Distinct()
                        .ToList();

                    if (usernames.Any())
                    {
                        isBanned = _dbContext.BannedSteamUsers
                            .Any(b => usernames.Contains(b.Username.ToLower()) &&
                                      !b.IsLifted &&
                                      (b.ExpiresAtUtc == null || b.ExpiresAtUtc > DateTime.UtcNow));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check if device is banned");
            }
        }

        // If authentication is disabled, always return authenticated
        if (!authEnabled)
        {
            return Ok(new AuthStatusResponse
            {
                RequiresAuth = false,
                IsAuthenticated = true,
                AuthenticationType = "disabled",
                DeviceId = null,
                HasData = hasData,
                HasEverBeenSetup = hasEverBeenSetup,
                HasBeenInitialized = hasBeenInitialized,
                HasDataLoaded = hasDataLoaded,
                IsBanned = isBanned
            });
        }

        return Ok(new AuthStatusResponse
        {
            RequiresAuth = true,
            IsAuthenticated = isAuthenticated,
            AuthenticationType = authenticationType,
            DeviceId = isAuthenticated && authenticationType == "device" ? deviceId : null,
            AuthMode = authMode, // "guest", "expired", or null
            GuestTimeRemaining = guestTimeRemaining, // minutes remaining for guest sessions
            HasData = hasData,
            HasEverBeenSetup = hasEverBeenSetup,
            HasBeenInitialized = hasBeenInitialized,
            HasDataLoaded = hasDataLoaded,
            // Prefill permission for guests
            PrefillEnabled = prefillEnabled,
            PrefillTimeRemaining = prefillTimeRemaining,
            // Ban status for prefill access
            IsBanned = isBanned
        });
    }

    /// <summary>
    /// POST /api/auth/clear-session - Clear session cookies
    /// Used when all sessions are cleared from database management
    /// This endpoint clears HttpOnly session cookies that can't be cleared by JavaScript
    /// No authentication required since session is already invalid in database
    /// </summary>
    [HttpPost("clear-session")]
    public IActionResult ClearSession()
    {
        // Clear ASP.NET session (this clears the HttpOnly session cookie)
        HttpContext.Session.Clear();

        _logger.LogInformation("Session cookies cleared for current request");

        return Ok(new SessionClearResponse
        {
            Success = true,
            Message = "Session cookies cleared successfully"
        });
    }

    /// <summary>
    /// GET /api/auth/guest/config - Get guest session configuration
    /// RESTful: GET is proper method for retrieving configuration
    /// </summary>
    [HttpGet("guest/config")]
    [RequireAuth]
    public IActionResult GetGuestConfig()
    {
        var durationHours = _guestSessionService.GetGuestSessionDurationHours();
        var isLocked = _stateService.GetGuestModeLocked();

        return Ok(new GuestConfigResponse
        {
            DurationHours = durationHours,
            IsLocked = isLocked,
            Message = "Guest configuration retrieved successfully"
        });
    }

    /// <summary>
    /// POST /api/auth/guest/config/duration - Update guest session duration
    /// RESTful: POST is used for configuration updates
    /// </summary>
    [HttpPost("guest/config/duration")]
    [RequireAuth]
    public IActionResult SetGuestSessionDuration([FromBody] SetGuestDurationRequest request)
    {
        if (request.DurationHours < 1 || request.DurationHours > 168)
        {
            return BadRequest(new
            {
                success = false,
                error = "Duration must be between 1 and 168 hours"
            });
        }

        _guestSessionService.SetGuestSessionDurationHours(request.DurationHours);

        _logger.LogInformation("Guest session duration updated to {Hours} hours", request.DurationHours);

        return Ok(new GuestDurationResponse
        {
            Success = true,
            DurationHours = request.DurationHours,
            Message = $"Guest session duration updated to {request.DurationHours} hours"
        });
    }

    /// <summary>
    /// POST /api/auth/guest/config/lock - Lock or unlock guest mode
    /// RESTful: POST is used for configuration updates
    /// </summary>
    [HttpPost("guest/config/lock")]
    [RequireAuth]
    public async Task<IActionResult> SetGuestModeLock([FromBody] SetGuestLockRequest request)
    {
        if (request == null)
        {
            return BadRequest(new
            {
                success = false,
                error = "Lock state is required"
            });
        }

        _stateService.SetGuestModeLocked(request.IsLocked);

        _logger.LogInformation("Guest mode {Action} by admin", request.IsLocked ? "locked" : "unlocked");

        // Broadcast to all clients via SignalR so login modals update in real-time
        await _notifications.NotifyAllAsync(SignalREvents.GuestModeLockChanged, new
        {
            isLocked = request.IsLocked
        });

        return Ok(new GuestLockResponse
        {
            Success = true,
            IsLocked = request.IsLocked,
            Message = request.IsLocked
                ? "Guest mode has been locked. New guests cannot log in."
                : "Guest mode has been unlocked. Guests can now log in."
        });
    }

    /// <summary>
    /// GET /api/auth/guest/status - Get guest mode status (public endpoint - no auth required)
    /// Used by login modal to check if guest mode is available
    /// </summary>
    [HttpGet("guest/status")]
    public IActionResult GetGuestStatus()
    {
        var durationHours = _guestSessionService.GetGuestSessionDurationHours();
        var isLocked = _stateService.GetGuestModeLocked();

        return Ok(new
        {
            isLocked = isLocked,
            durationHours = durationHours
        });
    }

    public class SetGuestDurationRequest
    {
        public int DurationHours { get; set; }
    }

    public class SetGuestLockRequest
    {
        public bool IsLocked { get; set; }
    }

    /// <summary>
    /// GET /api/auth/guest/prefill/config - Get guest prefill configuration
    /// </summary>
    [HttpGet("guest/prefill/config")]
    [RequireAuth]
    public IActionResult GetGuestPrefillConfig()
    {
        var enabledByDefault = _stateService.GetGuestPrefillEnabledByDefault();
        var durationHours = _stateService.GetGuestPrefillDurationHours();

        return Ok(new
        {
            enabledByDefault,
            durationHours,
            message = "Guest prefill configuration retrieved successfully"
        });
    }

    /// <summary>
    /// POST /api/auth/guest/prefill/config - Update guest prefill configuration
    /// </summary>
    [HttpPost("guest/prefill/config")]
    [RequireAuth]
    public IActionResult SetGuestPrefillConfig([FromBody] SetGuestPrefillConfigRequest request)
    {
        _stateService.SetGuestPrefillEnabledByDefault(request.EnabledByDefault);

        // Validate duration (1 or 2 hours only)
        if (request.DurationHours == 1 || request.DurationHours == 2)
        {
            _stateService.SetGuestPrefillDurationHours(request.DurationHours);
        }

        _logger.LogInformation("Guest prefill config updated: EnabledByDefault={Enabled}, Duration={Hours}h",
            request.EnabledByDefault, request.DurationHours);

        return Ok(new
        {
            success = true,
            enabledByDefault = _stateService.GetGuestPrefillEnabledByDefault(),
            durationHours = _stateService.GetGuestPrefillDurationHours(),
            message = "Guest prefill configuration updated successfully"
        });
    }

    /// <summary>
    /// POST /api/auth/guest/prefill/toggle/{deviceId} - Toggle prefill permission for a specific guest session
    /// </summary>
    [HttpPost("guest/prefill/toggle/{deviceId}")]
    [RequireAuth]
    public async Task<IActionResult> ToggleGuestPrefill(string deviceId, [FromBody] ToggleGuestPrefillRequest request)
    {
        var session = _guestSessionService.GetSessionByDeviceId(deviceId);
        if (session == null)
        {
            return NotFound(new { success = false, error = "Guest session not found" });
        }

        var success = _guestSessionService.SetPrefillPermission(deviceId, request.Enabled, request.DurationHours);
        if (!success)
        {
            return BadRequest(new { success = false, error = "Failed to update prefill permission" });
        }

        // Get updated session info
        var updatedSession = _guestSessionService.GetSessionByDeviceId(deviceId);

        _logger.LogInformation("Prefill permission {Action} for guest {DeviceId}, expires: {Expires}",
            request.Enabled ? "enabled" : "disabled",
            deviceId,
            updatedSession?.PrefillExpiresAt);

        // Notify the guest via SignalR so their UI updates immediately
        await _notifications.NotifyAllAsync(SignalREvents.GuestPrefillPermissionChanged, new
        {
            deviceId,
            enabled = updatedSession?.PrefillEnabled ?? false,
            expiresAt = updatedSession?.PrefillExpiresAt?.ToString("o")
        });

        return Ok(new
        {
            success = true,
            prefillEnabled = updatedSession?.PrefillEnabled ?? false,
            prefillExpiresAt = updatedSession?.PrefillExpiresAt,
            message = request.Enabled
                ? $"Prefill access granted for {request.DurationHours ?? _stateService.GetGuestPrefillDurationHours()} hour(s)"
                : "Prefill access revoked"
        });
    }

    public class SetGuestPrefillConfigRequest
    {
        public bool EnabledByDefault { get; set; }
        public int DurationHours { get; set; } = 2;
    }

    public class ToggleGuestPrefillRequest
    {
        public bool Enabled { get; set; }
        public int? DurationHours { get; set; }
    }
}
