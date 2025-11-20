using LancacheManager.Application.Services;
using LancacheManager.Data;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

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
    private readonly IHubContext<DownloadHub> _hubContext;

    public AuthController(
        ApiKeyService apiKeyService,
        DeviceAuthService deviceAuthService,
        GuestSessionService guestSessionService,
        IConfiguration configuration,
        ILogger<AuthController> logger,
        AppDbContext dbContext,
        StateRepository stateService,
        SteamKit2Service steamKit2Service,
        SteamAuthRepository steamAuthStorage,
        IHubContext<DownloadHub> hubContext)
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
        _hubContext = hubContext;
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

            // Check if this device had a guest session before authentication
            var existingGuestSession = _guestSessionService.GetSessionByDeviceId(request.DeviceId);

            var result = _deviceAuthService.RegisterDevice(request, ipAddress, userAgent);

            if (result.Success)
            {
                try
                {
                    // Check if UserSession already exists for this device
                    var existingUserSession = _dbContext.UserSessions.FirstOrDefault(s => s.SessionId == request.DeviceId);

                    if (existingGuestSession != null && existingUserSession != null)
                    {
                        // Upgrade existing guest session to authenticated
                        _logger.LogInformation("Upgrading guest session {SessionId} to authenticated device {DeviceId}",
                            existingGuestSession.SessionId, request.DeviceId);

                        existingUserSession.IsGuest = false;
                        existingUserSession.IsRevoked = false;
                        existingUserSession.RevokedAtUtc = null;
                        existingUserSession.RevokedBy = null;
                        existingUserSession.ExpiresAtUtc = null; // Authenticated sessions don't expire
                        existingUserSession.LastSeenAtUtc = DateTime.UtcNow;
                        _dbContext.SaveChanges();

                        // Revoke the in-memory guest session (so it doesn't show in sessions list)
                        _guestSessionService.RevokeSession(existingGuestSession.SessionId, "System (Upgraded to authenticated)");
                        _logger.LogInformation("Upgraded UserSession {SessionId} from guest to authenticated", existingUserSession.SessionId);
                    }
                    else if (existingUserSession == null)
                    {
                        // Create new UserSession for direct authentication (not from guest)
                        _logger.LogInformation("Creating UserSession for direct authentication: {DeviceId}", request.DeviceId);

                        // Parse UserAgent to get OS and browser info (same logic as DeviceAuthService)
                        var (os, browser) = ParseUserAgent(userAgent);

                        var newUserSession = new UserSession
                        {
                            SessionId = request.DeviceId,
                            IsGuest = false,
                            CreatedAtUtc = DateTime.UtcNow,
                            LastSeenAtUtc = DateTime.UtcNow,
                            ExpiresAtUtc = null, // Authenticated sessions don't expire
                            IsRevoked = false,
                            DeviceName = request.DeviceName ?? "Unknown Device",
                            IpAddress = ipAddress ?? "Unknown",
                            OperatingSystem = os ?? "Unknown",
                            Browser = browser ?? "Unknown"
                        };

                        _dbContext.UserSessions.Add(newUserSession);
                        _dbContext.SaveChanges();
                        _logger.LogInformation("Created UserSession for authenticated device {DeviceId}", request.DeviceId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error creating/updating UserSession during authentication");
                    // Don't fail the registration if this fails
                }

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
    public async Task<IActionResult> Logout()
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
                // Broadcast to all tabs/devices of this user for instant logout
                await _hubContext.Clients.All.SendAsync("UserSessionRevoked", new
                {
                    sessionId = deviceId,
                    sessionType = "authenticated"
                });

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

            // Broadcast to ALL users (authenticated and guests) for instant logout
            await _hubContext.Clients.All.SendAsync("UserSessionsCleared");
            _logger.LogInformation("Broadcasted UserSessionsCleared event - all users will be logged out instantly");

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
    public async Task<IActionResult> RevokeDevice(string deviceId)
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
                // Broadcast to affected user via SignalR for instant logout
                await _hubContext.Clients.All.SendAsync("UserSessionRevoked", new
                {
                    sessionId = deviceId,
                    sessionType = "authenticated"
                });

                _logger.LogInformation("Broadcasted UserSessionRevoked event for device: {DeviceId}", deviceId);

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
                deviceId = (string?)d.DeviceId, // Browser fingerprint device ID (cast to nullable for type consistency)
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

            // Get authenticated device IDs to filter out upgraded guest sessions
            var authenticatedDeviceIds = new HashSet<string>(devices.Select(d => d.DeviceId));

            // Filter out guest sessions that have been upgraded to authenticated
            // (these will have matching authenticated sessions with same device ID)
            var guestSessions = guests
                .Where(g => !authenticatedDeviceIds.Contains(g.SessionId)) // Exclude if upgraded
                .Select(g => new
                {
                    id = g.SessionId,
                    deviceId = g.DeviceId, // Browser fingerprint device ID (extracted from session ID)
                    deviceName = g.DeviceName,
                    ipAddress = g.IpAddress,
                    localIp = (string?)null,
                    hostname = (string?)null,
                    operatingSystem = g.OperatingSystem,
                    browser = g.Browser,
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
    public async Task<IActionResult> RevokeGuestSession(string sessionId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return BadRequest(new { error = "Session ID is required" });
            }

            // Get the IP address of the admin who is revoking
            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
            var revokedBy = ipAddress ?? "Unknown IP";

            var success = _guestSessionService.RevokeSession(sessionId, revokedBy);
            if (success)
            {
                // Broadcast to affected guest user via SignalR for instant logout
                await _hubContext.Clients.All.SendAsync("UserSessionRevoked", new
                {
                    sessionId = sessionId,
                    sessionType = "guest"
                });

                _logger.LogInformation("Broadcasted UserSessionRevoked event for guest session: {SessionId}", sessionId);

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
    public async Task<IActionResult> DeleteGuestSession(string sessionId)
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
                // Broadcast to affected guest user via SignalR for instant logout
                await _hubContext.Clients.All.SendAsync("UserSessionRevoked", new
                {
                    sessionId = sessionId,
                    sessionType = "guest"
                });

                _logger.LogInformation("Broadcasted UserSessionRevoked event for deleted guest session: {SessionId}", sessionId);

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

    [HttpGet("guest/config/duration")]
    public IActionResult GetGuestSessionDuration()
    {
        try
        {
            var durationHours = _guestSessionService.GetGuestSessionDurationHours();
            return Ok(new
            {
                durationHours = durationHours
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting guest session duration");
            return StatusCode(500, new { error = "Failed to get guest session duration", message = ex.Message });
        }
    }

    [HttpPost("guest/config/duration")]
    [RequireAuth]
    public IActionResult SetGuestSessionDuration([FromBody] SetGuestSessionDurationRequest request)
    {
        try
        {
            if (request.DurationHours < 1 || request.DurationHours > 168)
            {
                return BadRequest(new { error = "Duration must be between 1 and 168 hours" });
            }

            _guestSessionService.SetGuestSessionDurationHours(request.DurationHours);

            // Broadcast duration change to all connected clients
            _hubContext.Clients.All.SendAsync("GuestDurationUpdated", new { durationHours = request.DurationHours });

            return Ok(new
            {
                success = true,
                durationHours = request.DurationHours,
                message = $"Guest session duration updated to {request.DurationHours} hour{(request.DurationHours != 1 ? "s" : "")}"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting guest session duration");
            return StatusCode(500, new { error = "Failed to set guest session duration", message = ex.Message });
        }
    }

    /// <summary>
    /// Heartbeat endpoint to update lastSeenAt for active sessions
    /// </summary>
    [HttpPost("heartbeat")]
    public IActionResult Heartbeat()
    {
        try
        {
            // Check for authenticated device
            var deviceId = Request.Headers["X-Device-Id"].FirstOrDefault();
            if (!string.IsNullOrEmpty(deviceId))
            {
                _deviceAuthService.UpdateLastSeen(deviceId);
                return Ok(new { success = true, type = "device" });
            }

            // Check for guest session
            var guestSessionId = Request.Headers["X-Guest-Session-Id"].FirstOrDefault();
            if (!string.IsNullOrEmpty(guestSessionId))
            {
                _guestSessionService.UpdateLastSeen(guestSessionId);
                return Ok(new { success = true, type = "guest" });
            }

            // No valid session found
            return Unauthorized(new { error = "No valid session found" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing heartbeat");
            return StatusCode(500, new { error = "Failed to process heartbeat" });
        }
    }

    /// <summary>
    /// Parse user agent string to extract OS and browser information
    /// </summary>
    private (string? os, string? browser) ParseUserAgent(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
        {
            return (null, null);
        }

        string? os = null;
        string? browser = null;

        // Detect OS
        if (userAgent.Contains("Windows NT 10.0"))
            os = "Windows 10/11";
        else if (userAgent.Contains("Windows NT 6.3"))
            os = "Windows 8.1";
        else if (userAgent.Contains("Windows NT 6.2"))
            os = "Windows 8";
        else if (userAgent.Contains("Windows NT 6.1"))
            os = "Windows 7";
        else if (userAgent.Contains("Windows"))
            os = "Windows";
        else if (userAgent.Contains("Mac OS X"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(userAgent, @"Mac OS X (\d+[._]\d+)");
            os = match.Success ? $"macOS {match.Groups[1].Value.Replace('_', '.')}" : "macOS";
        }
        else if (userAgent.Contains("Linux"))
            os = "Linux";
        else if (userAgent.Contains("Android"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(userAgent, @"Android (\d+(\.\d+)?)");
            os = match.Success ? $"Android {match.Groups[1].Value}" : "Android";
        }
        else if (userAgent.Contains("iPhone") || userAgent.Contains("iPad"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(userAgent, @"OS (\d+_\d+)");
            os = match.Success ? $"iOS {match.Groups[1].Value.Replace('_', '.')}" : "iOS";
        }

        // Detect Browser (order matters - check specific browsers before generic ones)
        if (userAgent.Contains("Edg/"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(userAgent, @"Edg/([\d.]+)");
            browser = match.Success ? $"Edge {match.Groups[1].Value}" : "Edge";
        }
        else if (userAgent.Contains("OPR/") || userAgent.Contains("Opera/"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(userAgent, @"(?:OPR|Opera)/([\d.]+)");
            browser = match.Success ? $"Opera {match.Groups[1].Value}" : "Opera";
        }
        else if (userAgent.Contains("Chrome/"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(userAgent, @"Chrome/([\d.]+)");
            browser = match.Success ? $"Chrome {match.Groups[1].Value}" : "Chrome";
        }
        else if (userAgent.Contains("Safari/") && !userAgent.Contains("Chrome"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(userAgent, @"Version/([\d.]+)");
            browser = match.Success ? $"Safari {match.Groups[1].Value}" : "Safari";
        }
        else if (userAgent.Contains("Firefox/"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(userAgent, @"Firefox/([\d.]+)");
            browser = match.Success ? $"Firefox {match.Groups[1].Value}" : "Firefox";
        }

        return (os, browser);
    }

    public class ValidateGuestSessionRequest
    {
        public string SessionId { get; set; } = string.Empty;
    }

    public class SetGuestSessionDurationRequest
    {
        public int DurationHours { get; set; }
    }
}
