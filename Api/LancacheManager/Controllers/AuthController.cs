using LancacheManager.Application.Services;
using LancacheManager.Data;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly ApiKeyService _apiKeyService;
    private readonly DeviceAuthService _deviceAuthService;
    private readonly GuestSessionService _guestSessionService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;
    private readonly AppDbContext _dbContext;
    private readonly StateRepository _stateService;
    private readonly SteamKit2Service _steamKit2Service;
    private readonly SteamAuthRepository _steamAuthStorage;

    public AuthController(
        ApiKeyService apiKeyService,
        DeviceAuthService deviceAuthService,
        GuestSessionService guestSessionService,
        IConfiguration configuration,
        ILogger<AuthController> logger,
        AppDbContext dbContext,
        StateRepository stateService,
        SteamKit2Service steamKit2Service,
        SteamAuthRepository steamAuthStorage)
    {
        _apiKeyService = apiKeyService;
        _deviceAuthService = deviceAuthService;
        _guestSessionService = guestSessionService;
        _configuration = configuration;
        _logger = logger;
        _dbContext = dbContext;
        _stateService = stateService;
        _steamKit2Service = steamKit2Service;
        _steamAuthStorage = steamAuthStorage;
    }

    /// <summary>
    /// Check if authentication is required and if current session is authenticated
    /// </summary>
    [HttpGet("check")]
    public IActionResult CheckAuth()
    {
        // Check if authentication is enabled
        var authEnabled = _configuration.GetValue<bool>("Security:EnableAuthentication", true);

        // Check if already authenticated (do this first, before slow DB checks)
        var apiKey = Request.Headers["X-Api-Key"].FirstOrDefault();
        var deviceId = Request.Headers["X-Device-Id"].FirstOrDefault();

        bool isAuthenticated = false;
        string? authenticationType = null;

        if (!string.IsNullOrEmpty(apiKey) && _apiKeyService.ValidateApiKey(apiKey))
        {
            isAuthenticated = true;
            authenticationType = "api-key";
        }
        else if (!string.IsNullOrEmpty(deviceId) && _deviceAuthService.ValidateDevice(deviceId))
        {
            isAuthenticated = true;
            authenticationType = "device";
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
    /// Register a device with the API key
    /// </summary>
    [HttpPost("register")]
    public IActionResult RegisterDevice([FromBody] DeviceAuthService.RegisterDeviceRequest request)
    {
        try
        {
            // Get server-detected IP (what the server sees in the HTTP connection)
            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
            var userAgent = Request.Headers["User-Agent"].FirstOrDefault();

            _logger.LogInformation("Device registration attempt from Server IP: {ServerIP}, Local IP: {LocalIP}, Device: {DeviceId}",
                ipAddress, request.LocalIp ?? "not detected", request.DeviceId);

            var result = _deviceAuthService.RegisterDevice(request, ipAddress, userAgent);

            if (result.Success)
            {
                return Ok(result);
            }

            return Unauthorized(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during device registration");
            return StatusCode(500, new { error = "Registration failed", message = ex.Message });
        }
    }

    /// <summary>
    /// Logout (deregister) the current device
    /// Allows the authenticated device to free up the slot for another user
    /// </summary>
    [HttpPost("logout")]
    [RequireAuth]
    public IActionResult Logout()
    {
        try
        {
            var deviceId = Request.Headers["X-Device-Id"].FirstOrDefault();

            if (string.IsNullOrEmpty(deviceId))
            {
                return BadRequest(new { error = "Device ID required" });
            }

            var (success, message) = _deviceAuthService.RevokeDevice(deviceId);

            if (success)
            {
                _logger.LogInformation("Device logged out: {DeviceId}", deviceId);
                return Ok(new { success = true, message = "Logged out successfully" });
            }

            return BadRequest(new { error = message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during logout");
            return StatusCode(500, new { error = "Logout failed", message = ex.Message });
        }
    }

    /// <summary>
    /// Regenerate the API key (requires admin authentication)
    /// SECURITY: This will logout all Steam sessions and revoke all device registrations
    /// </summary>
    [HttpPost("regenerate-key")]
    [RequireAuth]
    public async Task<IActionResult> RegenerateApiKey()
    {
        try
        {
            // SECURITY: Logout from Steam first (both in-memory session AND state)
            // This ensures old encrypted tokens become unreadable AND active session is terminated
            var steamWasAuthenticated = _stateService.GetSteamAuthMode() == "authenticated";
            if (steamWasAuthenticated)
            {
                _logger.LogInformation("Logging out from Steam (in-memory + state) before API key regeneration for security");

                // Disconnect the active Steam session
                await _steamKit2Service.LogoutAsync();

                // Clear tokens from Steam auth file (using Microsoft Data Protection API)
                _steamAuthStorage.ClearSteamAuthData();
                _logger.LogInformation("Cleared Steam auth data from encrypted file (data/steam_auth/credentials.json)");
            }

            var (oldKey, newKey) = _apiKeyService.ForceRegenerateApiKey();

            // Display the new key (or auth disabled message)
            _apiKeyService.DisplayApiKey(_configuration);

            // Revoke all existing device registrations
            var revokedDeviceCount = _deviceAuthService.RevokeAllDevices();

            // Revoke all guest sessions
            var guestSessions = _guestSessionService.GetAllSessions();
            var revokedGuestCount = 0;
            foreach (var session in guestSessions.Where(s => !s.IsRevoked))
            {
                if (_guestSessionService.RevokeSession(session.SessionId, "Admin (API Key Regeneration)"))
                {
                    revokedGuestCount++;
                }
            }

            _logger.LogWarning(
                "API key regenerated by admin. Old key: {OldKey}..., New key: {NewKey}... | Revoked: {DeviceCount} device(s), {GuestCount} guest(s) | Steam: {SteamLogout}",
                oldKey[..System.Math.Min(oldKey.Length, 20)],
                newKey[..System.Math.Min(newKey.Length, 20)],
                revokedDeviceCount,
                revokedGuestCount,
                steamWasAuthenticated ? "Logged out" : "Not connected");

            return Ok(new
            {
                success = true,
                message = $"API key regenerated successfully. {revokedDeviceCount} device(s) and {revokedGuestCount} guest session(s) revoked." +
                          (steamWasAuthenticated ? " Steam session terminated and logged out." : ""),
                warning = "All users must re-authenticate with the new key. Steam re-authentication required. Check container logs for the new API key."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error regenerating API key");
            return StatusCode(500, new { error = "Failed to regenerate API key", message = ex.Message });
        }
    }

    /// <summary>
    /// Get the current API key (only works from localhost)
    /// </summary>
    [HttpGet("key")]
    public IActionResult GetApiKey()
    {
        // Only allow from localhost
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        if (remoteIp == null || (!remoteIp.ToString().StartsWith("127.") && !remoteIp.ToString().StartsWith("::1")))
        {
            _logger.LogWarning("API key request denied from non-localhost IP: {IP}", remoteIp);
            return Forbid("This endpoint is only accessible from localhost");
        }

        var key = _apiKeyService.GetOrCreateApiKey();
        return Ok(new
        {
            apiKey = key,
            message = "Save this key! It's required for authentication.",
            warning = "This endpoint is only accessible from localhost"
        });
    }

    /// <summary>
    /// Check if the API key is valid (all valid keys are admin keys)
    /// </summary>
    [HttpGet("api-key-type")]
    [RequireAuth]
    public IActionResult GetApiKeyType()
    {
        try
        {
            var apiKey = Request.Headers["X-Api-Key"].FirstOrDefault();

            if (string.IsNullOrEmpty(apiKey))
            {
                return Ok(new
                {
                    hasApiKey = false,
                    keyType = "none",
                    hasPrimaryKey = false
                });
            }

            var isValid = _apiKeyService.ValidateApiKey(apiKey);

            return Ok(new
            {
                hasApiKey = isValid,
                keyType = isValid ? "admin" : "none",
                hasPrimaryKey = isValid // All valid keys are admin keys now
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking API key type");
            return StatusCode(500, new { error = "Failed to check API key type", message = ex.Message });
        }
    }

    /// <summary>
    /// Get all registered devices (requires API key authentication)
    /// </summary>
    [HttpGet("devices")]
    [RequireAuth]
    public IActionResult GetDevices()
    {
        try
        {
            var devices = _deviceAuthService.GetAllDevices();
            return Ok(new
            {
                devices,
                count = devices.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving devices");
            return StatusCode(500, new { error = "Failed to retrieve devices", message = ex.Message });
        }
    }

    /// <summary>
    /// Revoke a specific device registration (requires admin authentication)
    /// </summary>
    [HttpDelete("devices/{deviceId}")]
    [RequireAuth]
    public IActionResult RevokeDevice(string deviceId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return BadRequest(new { error = "Device ID is required" });
            }

            var (success, message) = _deviceAuthService.RevokeDevice(deviceId);
            if (success)
            {
                return Ok(new
                {
                    success = true,
                    message = $"Device {deviceId} has been revoked successfully"
                });
            }

            return NotFound(new { error = "Device not found", message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error revoking device: {DeviceId}", deviceId);
            return StatusCode(500, new { error = "Failed to revoke device", message = ex.Message });
        }
    }

    /// <summary>
    /// Create a guest session
    /// </summary>
    [HttpPost("guest/register")]
    public IActionResult RegisterGuestSession([FromBody] GuestSessionService.CreateGuestSessionRequest request)
    {
        try
        {
            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
            var session = _guestSessionService.CreateSession(request, ipAddress);
            return Ok(new
            {
                success = true,
                sessionId = session.SessionId,
                expiresAt = session.ExpiresAt,
                message = "Guest session created successfully"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating guest session");
            return StatusCode(500, new { error = "Failed to create guest session", message = ex.Message });
        }
    }

    /// <summary>
    /// Validate a guest session
    /// </summary>
    [HttpPost("guest/validate")]
    public IActionResult ValidateGuestSession([FromBody] ValidateGuestSessionRequest request)
    {
        try
        {
            var isValid = _guestSessionService.ValidateSession(request.SessionId);
            return Ok(new
            {
                isValid,
                message = isValid ? "Session is valid" : "Session is invalid or expired"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating guest session");
            return StatusCode(500, new { error = "Failed to validate session", message = ex.Message });
        }
    }

    /// <summary>
    /// Get all sessions (authenticated devices + guest sessions) - requires API key
    /// </summary>
    [HttpGet("sessions")]
    [RequireAuth]
    public IActionResult GetAllSessions()
    {
        try
        {
            var devices = _deviceAuthService.GetAllDevices();
            var guests = _guestSessionService.GetAllSessions();

            // Convert to unified format
            var authenticatedSessions = devices.Select(d => new
            {
                id = d.DeviceId,
                deviceName = d.DeviceName,
                ipAddress = d.IpAddress,
                localIp = d.LocalIp,
                hostname = d.Hostname,
                operatingSystem = d.OperatingSystem,
                browser = d.Browser,
                createdAt = d.RegisteredAt,
                lastSeenAt = d.LastSeenAt,
                expiresAt = d.ExpiresAt,
                isExpired = d.IsExpired,
                isRevoked = false,
                revokedAt = (DateTime?)null,
                revokedBy = (string?)null,
                type = "authenticated"
            }).ToList();

            var guestSessions = guests.Select(g => new
            {
                id = g.SessionId,
                deviceName = g.DeviceName,
                ipAddress = g.IpAddress,
                localIp = (string?)null,
                hostname = (string?)null,
                operatingSystem = (string?)null,
                browser = (string?)null,
                createdAt = g.CreatedAt,
                lastSeenAt = g.LastSeenAt,
                expiresAt = g.ExpiresAt,
                isExpired = g.IsExpired,
                isRevoked = g.IsRevoked,
                revokedAt = g.RevokedAt,
                revokedBy = g.RevokedBy,
                type = "guest"
            }).ToList();

            var allSessions = authenticatedSessions.Concat(guestSessions)
                .OrderByDescending(s => s.lastSeenAt ?? s.createdAt)
                .ToList();

            return Ok(new
            {
                sessions = allSessions,
                count = allSessions.Count,
                authenticatedCount = authenticatedSessions.Count,
                guestCount = guestSessions.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving sessions");
            return StatusCode(500, new { error = "Failed to retrieve sessions", message = ex.Message });
        }
    }

    /// <summary>
    /// Revoke a guest session (requires API key authentication)
    /// </summary>
    [HttpPost("guest/{sessionId}/revoke")]
    [RequireAuth]
    public IActionResult RevokeGuestSession(string sessionId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return BadRequest(new { error = "Session ID is required" });
            }

            // Get the device name of the admin who is revoking
            // Authenticated users send both X-Api-Key (for auth) and X-Device-Id (for identification)
            var deviceId = Request.Headers["X-Device-Id"].FirstOrDefault();
            var revokedBy = "Unknown Admin";

            if (!string.IsNullOrEmpty(deviceId))
            {
                var devices = _deviceAuthService.GetAllDevices();
                var device = devices.FirstOrDefault(d => d.DeviceId == deviceId);
                if (device != null)
                {
                    revokedBy = device.Hostname ?? device.DeviceName ?? device.DeviceId;
                }
            }

            var success = _guestSessionService.RevokeSession(sessionId, revokedBy);
            if (success)
            {
                return Ok(new
                {
                    success = true,
                    message = $"Guest session {sessionId} has been revoked successfully"
                });
            }

            return NotFound(new { error = "Guest session not found" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error revoking guest session: {SessionId}", sessionId);
            return StatusCode(500, new { error = "Failed to revoke guest session", message = ex.Message });
        }
    }

    /// <summary>
    /// Permanently delete a guest session (requires API key authentication)
    /// </summary>
    [HttpDelete("guest/{sessionId}")]
    [RequireAuth]
    public IActionResult DeleteGuestSession(string sessionId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return BadRequest(new { error = "Session ID is required" });
            }

            var success = _guestSessionService.DeleteSession(sessionId);
            if (success)
            {
                return Ok(new
                {
                    success = true,
                    message = $"Guest session {sessionId} has been deleted successfully"
                });
            }

            return NotFound(new { error = "Guest session not found" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting guest session: {SessionId}", sessionId);
            return StatusCode(500, new { error = "Failed to delete guest session", message = ex.Message });
        }
    }

    public class ValidateGuestSessionRequest
    {
        public string SessionId { get; set; } = string.Empty;
    }
}
