using LancacheManager.Application.DTOs;
using LancacheManager.Application.Services;
using LancacheManager.Data;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

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
    private readonly StateRepository _stateService;
    private readonly GuestSessionService _guestSessionService;
    private readonly IHubContext<DownloadHub> _hubContext;

    public AuthController(
        ApiKeyService apiKeyService,
        DeviceAuthService deviceAuthService,
        IConfiguration configuration,
        ILogger<AuthController> logger,
        AppDbContext dbContext,
        StateRepository stateService,
        GuestSessionService guestSessionService,
        IHubContext<DownloadHub> hubContext)
    {
        _apiKeyService = apiKeyService;
        _deviceAuthService = deviceAuthService;
        _configuration = configuration;
        _logger = logger;
        _dbContext = dbContext;
        _stateService = stateService;
        _guestSessionService = guestSessionService;
        _hubContext = hubContext;
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
                    }
                    _logger.LogDebug("Valid guest session found for device {DeviceId}, expires in {Minutes} minutes", requestDeviceId, guestTimeRemaining);
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
                HasDataLoaded = hasDataLoaded
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
            HasDataLoaded = hasDataLoaded
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
        _stateService.SetGuestModeLocked(request.IsLocked);

        _logger.LogInformation("Guest mode {Action} by admin", request.IsLocked ? "locked" : "unlocked");

        // Broadcast to all clients via SignalR so login modals update in real-time
        await _hubContext.Clients.All.SendAsync("GuestModeLockChanged", new
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
}
