using System.ComponentModel.DataAnnotations;
using LancacheManager.Application.DTOs;
using LancacheManager.Data;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Repositories.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
    [EnableRateLimiting("auth")]
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

                    var (os, browser) = UserAgentParser.Parse(userAgent);

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

    public class RegisterDeviceRequest
    {
        [Required]
        [StringLength(128, MinimumLength = 16, ErrorMessage = "DeviceId must be between 16 and 128 characters")]
        [RegularExpression(@"^[a-zA-Z0-9_-]+$", ErrorMessage = "DeviceId contains invalid characters")]
        public string DeviceId { get; set; } = string.Empty;

        [Required]
        [StringLength(100, MinimumLength = 40, ErrorMessage = "ApiKey must be between 40 and 100 characters")]
        public string ApiKey { get; set; } = string.Empty;

        [StringLength(100, ErrorMessage = "DeviceName cannot exceed 100 characters")]
        public string? DeviceName { get; set; }

        [StringLength(45, ErrorMessage = "LocalIp cannot exceed 45 characters")] // Max IPv6 length
        public string? LocalIp { get; set; }
    }
}
