using LancacheManager.Application.DTOs;
using LancacheManager.Data;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Repositories.Interfaces;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for device registration and management
/// Handles device registration (authentication), listing, and revocation
/// </summary>
[ApiController]
[Route("api/devices")]
public class DevicesController : ControllerBase
{
    private readonly DeviceAuthService _deviceAuthService;
    private readonly GuestSessionService _guestSessionService;
    private readonly AppDbContext _dbContext;
    private readonly IDatabaseRepository _databaseRepository;
    private readonly ILogger<DevicesController> _logger;
    private readonly IHubContext<DownloadHub> _hubContext;

    public DevicesController(
        DeviceAuthService deviceAuthService,
        GuestSessionService guestSessionService,
        AppDbContext dbContext,
        IDatabaseRepository databaseRepository,
        ILogger<DevicesController> logger,
        IHubContext<DownloadHub> hubContext)
    {
        _deviceAuthService = deviceAuthService;
        _guestSessionService = guestSessionService;
        _dbContext = dbContext;
        _databaseRepository = databaseRepository;
        _logger = logger;
        _hubContext = hubContext;
    }

    /// <summary>
    /// GET /api/devices - List all registered devices
    /// RESTful: GET is proper method for retrieving resource collections
    /// </summary>
    [HttpGet]
    [RequireAuth]
    public IActionResult GetAllDevices()
    {
        var devices = _deviceAuthService.GetAllDevices();
        return Ok(new DeviceListResponse
        {
            Devices = devices.Cast<object>().ToList(),
            Count = devices.Count
        });
    }

    /// <summary>
    /// POST /api/devices - Register a new device (authenticate)
    /// RESTful: POST is proper method for creating resources
    /// Request body: { "deviceId": "...", "apiKey": "...", "deviceName": "..." }
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> RegisterDevice([FromBody] RegisterDeviceRequest request)
    {
        // Block authentication during database reset operations
        if (_databaseRepository.IsResetOperationRunning)
        {
            _logger.LogWarning("Device registration rejected - database reset in progress");
            return StatusCode(503, new ServiceUnavailableResponse
            {
                Error = "Service temporarily unavailable",
                Message = "Database reset in progress. Please wait and try again.",
                RetryAfter = 30
            });
        }

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers["User-Agent"].FirstOrDefault();

        _logger.LogInformation("Device registration attempt from Server IP: {ServerIP}, Local IP: {LocalIP}, Device: {DeviceId}",
            ipAddress, request.LocalIp ?? "not detected", request.DeviceId);

        // Check if this device had a guest session before authentication
        var existingGuestSession = _guestSessionService.GetSessionByDeviceId(request.DeviceId);

        // Map controller's request model to service's request model
        var serviceRequest = new LancacheManager.Security.DeviceAuthService.RegisterDeviceRequest
        {
            DeviceId = request.DeviceId,
            ApiKey = request.ApiKey,
            DeviceName = request.DeviceName,
            LocalIp = request.LocalIp
        };

        var result = _deviceAuthService.RegisterDevice(serviceRequest, ipAddress, userAgent);

        if (result.Success)
        {
            // Set session cookie for authenticated session
            HttpContext.Session.SetString("DeviceId", request.DeviceId);
            HttpContext.Session.SetString("ApiKey", request.ApiKey);
            HttpContext.Session.SetString("AuthMode", "authenticated");

            try
            {
                // Reload UserSession from database to get the ApiKey that was set by RegisterDevice
                // We need to detach any tracked entities first to avoid stale cache
                var trackedEntity = _dbContext.ChangeTracker.Entries<UserSession>()
                    .FirstOrDefault(e => e.Entity.DeviceId == request.DeviceId);
                if (trackedEntity != null)
                {
                    trackedEntity.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
                }

                // Now query fresh from database
                var existingUserSession = _dbContext.UserSessions.FirstOrDefault(s => s.DeviceId == request.DeviceId);

                if (existingGuestSession != null && existingUserSession != null)
                {
                    // Upgrade existing guest session to authenticated
                    _logger.LogInformation("Upgrading guest session {DeviceId} to authenticated device {DeviceId}",
                        existingGuestSession.DeviceId, request.DeviceId);

                    // The ApiKey was already set by RegisterDevice -> SaveDeviceRegistration
                    // Now we just need to flip the IsGuest flag
                    existingUserSession.IsGuest = false;
                    existingUserSession.IsRevoked = false;
                    existingUserSession.RevokedAtUtc = null;
                    existingUserSession.RevokedBy = null;
                    existingUserSession.ExpiresAtUtc = null; // Authenticated sessions don't expire
                    existingUserSession.LastSeenAtUtc = DateTime.UtcNow;
                    _dbContext.SaveChanges();

                    // CRITICAL: Remove guest session from GuestSessionService cache
                    // We DON'T delete from database (the record was just updated to IsGuest=false)
                    // We just remove from cache so GetSessionByDeviceId() won't find it
                    _guestSessionService.RemoveFromCache(existingGuestSession.DeviceId);

                    // Reload device cache to ensure the upgraded session is recognized by DeviceAuthService
                    _deviceAuthService.ReloadDeviceCache();

                    _logger.LogInformation("Upgraded UserSession {DeviceId} from guest to authenticated", existingUserSession.DeviceId);
                }
                else if (existingUserSession == null)
                {
                    // Create new UserSession for direct authentication
                    _logger.LogInformation("Creating UserSession for direct authentication: {DeviceId}", request.DeviceId);

                    var (os, browser) = ParseUserAgent(userAgent);

                    var newUserSession = new UserSession
                    {
                        DeviceId = request.DeviceId,
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

                // Broadcast session creation via SignalR for real-time updates
                await _hubContext.Clients.All.SendAsync("UserSessionCreated", new
                {
                    deviceId = request.DeviceId,
                    sessionType = "authenticated"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating/updating UserSession during authentication");
                // Don't fail the registration if this fails
            }

            return Created($"/api/devices/{request.DeviceId}", result);
        }

        return Unauthorized(result);
    }

    // Note: Device/session revocation has been moved to SessionsController
    // Use DELETE /api/sessions/{id} for revoking both authenticated devices and guest sessions

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

        // Detect Browser
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

    public class RegisterDeviceRequest
    {
        public string DeviceId { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string? DeviceName { get; set; }
        public string? LocalIp { get; set; }
    }
}
