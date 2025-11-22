using LancacheManager.Application.Services;
using LancacheManager.Data;
using LancacheManager.Infrastructure.Repositories;
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
    private readonly StateRepository _stateService;

    public AuthController(
        ApiKeyService apiKeyService,
        DeviceAuthService deviceAuthService,
        IConfiguration configuration,
        ILogger<AuthController> logger,
        AppDbContext dbContext,
        StateRepository stateService)
    {
        _apiKeyService = apiKeyService;
        _deviceAuthService = deviceAuthService;
        _configuration = configuration;
        _logger = logger;
        _dbContext = dbContext;
        _stateService = stateService;
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
            return Ok(new
            {
                requiresAuth = false,
                isAuthenticated = true,
                authenticationType = "disabled",
                deviceId = (string?)null,
                hasData,
                hasEverBeenSetup,
                hasBeenInitialized,
                hasDataLoaded
            });
        }

        return Ok(new
        {
            requiresAuth = true,
            isAuthenticated,
            authenticationType,
            deviceId = isAuthenticated && authenticationType == "device" ? deviceId : null,
            hasData,
            hasEverBeenSetup,
            hasBeenInitialized,
            hasDataLoaded
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
        try
        {
            // Clear ASP.NET session (this clears the HttpOnly session cookie)
            HttpContext.Session.Clear();

            _logger.LogInformation("Session cookies cleared for current request");

            return Ok(new {
                success = true,
                message = "Session cookies cleared successfully"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing session cookies");
            return StatusCode(500, new {
                success = false,
                error = "Failed to clear session cookies",
                details = ex.Message
            });
        }
    }
}
