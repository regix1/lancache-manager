using LancacheManager.Security;
using LancacheManager.Data;
using LancacheManager.Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for session management (authenticated and guest sessions)
/// Handles session listing, creation, updates, and deletion
/// </summary>
[ApiController]
[Route("api/sessions")]
public class SessionsController : ControllerBase
{
    private readonly DeviceAuthService _deviceAuthService;
    private readonly GuestSessionService _guestSessionService;
    private readonly ILogger<SessionsController> _logger;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly AppDbContext _dbContext;

    public SessionsController(
        DeviceAuthService deviceAuthService,
        GuestSessionService guestSessionService,
        ILogger<SessionsController> logger,
        IHubContext<DownloadHub> hubContext,
        AppDbContext dbContext)
    {
        _deviceAuthService = deviceAuthService;
        _guestSessionService = guestSessionService;
        _logger = logger;
        _hubContext = hubContext;
        _dbContext = dbContext;
    }

    /// <summary>
    /// GET /api/sessions - List all sessions (authenticated + guest)
    /// RESTful: GET is proper method for retrieving resource collections
    /// </summary>
    [HttpGet]
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
                deviceId = (string?)d.DeviceId,
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

            // Filter out guest sessions that have been upgraded to authenticated
            var authenticatedDeviceIds = new HashSet<string>(devices.Select(d => d.DeviceId));

            var guestSessions = guests
                .Where(g => !authenticatedDeviceIds.Contains(g.SessionId))
                .Select(g => new
                {
                    id = g.SessionId,
                    deviceId = g.DeviceId,
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
    /// GET /api/sessions/{id} - Get specific session
    /// RESTful: GET is proper method for retrieving a single resource
    /// </summary>
    [HttpGet("{id}")]
    [RequireAuth]
    public IActionResult GetSession(string id)
    {
        try
        {
            // Check authenticated devices first
            var device = _deviceAuthService.GetAllDevices().FirstOrDefault(d => d.DeviceId == id);
            if (device != null)
            {
                return Ok(new
                {
                    id = device.DeviceId,
                    deviceId = device.DeviceId,
                    deviceName = device.DeviceName,
                    ipAddress = device.IpAddress,
                    localIp = device.LocalIp,
                    hostname = device.Hostname,
                    operatingSystem = device.OperatingSystem,
                    browser = device.Browser,
                    createdAt = device.RegisteredAt,
                    lastSeenAt = device.LastSeenAt,
                    expiresAt = device.ExpiresAt,
                    isExpired = device.IsExpired,
                    type = "authenticated"
                });
            }

            // Check guest sessions
            var guestSession = _guestSessionService.GetSessionByDeviceId(id);
            if (guestSession != null)
            {
                return Ok(new
                {
                    id = guestSession.SessionId,
                    deviceId = guestSession.DeviceId,
                    deviceName = guestSession.DeviceName,
                    ipAddress = guestSession.IpAddress,
                    operatingSystem = guestSession.OperatingSystem,
                    browser = guestSession.Browser,
                    createdAt = guestSession.CreatedAt,
                    lastSeenAt = guestSession.LastSeenAt,
                    expiresAt = guestSession.ExpiresAt,
                    isExpired = guestSession.IsExpired,
                    isRevoked = guestSession.IsRevoked,
                    type = "guest"
                });
            }

            return NotFound(new { error = "Session not found", sessionId = id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving session: {SessionId}", id);
            return StatusCode(500, new { error = "Failed to retrieve session", message = ex.Message });
        }
    }

    /// <summary>
    /// POST /api/sessions?type=guest - Create guest session
    /// RESTful: POST is proper method for creating resources
    /// </summary>
    [HttpPost]
    public IActionResult CreateSession([FromQuery] string? type, [FromBody] CreateSessionRequest request)
    {
        try
        {
            // Only support guest session creation via this endpoint
            // Authenticated sessions are created via POST /api/devices
            if (type != "guest")
            {
                return BadRequest(new { error = "Only guest session creation is supported. Use POST /api/devices for authenticated sessions." });
            }

            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
            var session = _guestSessionService.CreateSession(
                new GuestSessionService.CreateGuestSessionRequest
                {
                    SessionId = request.SessionId,
                    DeviceName = request.DeviceName,
                    OperatingSystem = request.OperatingSystem,
                    Browser = request.Browser
                },
                ipAddress
            );

            return Created($"/api/sessions/{session.SessionId}", new
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
    /// PATCH /api/sessions/{id}/last-seen - Update last seen timestamp (heartbeat)
    /// RESTful: PATCH is proper method for partial updates
    /// </summary>
    [HttpPatch("{id}/last-seen")]
    public IActionResult UpdateLastSeen(string id)
    {
        try
        {
            // Try updating authenticated device first
            var device = _deviceAuthService.GetAllDevices().FirstOrDefault(d => d.DeviceId == id);
            if (device != null)
            {
                _deviceAuthService.UpdateLastSeen(id);
                return Ok(new { success = true, type = "authenticated", sessionId = id });
            }

            // Try updating guest session
            var (isValid, _) = _guestSessionService.ValidateSessionWithReason(id);
            if (isValid)
            {
                _guestSessionService.UpdateLastSeen(id);
                return Ok(new { success = true, type = "guest", sessionId = id });
            }

            // Session not found or invalid
            return NotFound(new { error = "Session not found or invalid", sessionId = id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating last seen for session: {SessionId}", id);
            return StatusCode(500, new { error = "Failed to update last seen" });
        }
    }

    /// <summary>
    /// DELETE /api/sessions/{id} - Revoke/delete session
    /// RESTful: DELETE is proper method for removing resources
    /// </summary>
    [HttpDelete("{id}")]
    [RequireAuth]
    public async Task<IActionResult> RevokeSession(string id)
    {
        try
        {
            // Check if it's an authenticated device
            var device = _deviceAuthService.GetAllDevices().FirstOrDefault(d => d.DeviceId == id);
            if (device != null)
            {
                var (success, message) = _deviceAuthService.RevokeDevice(id);
                if (success)
                {
                    // Broadcast revocation
                    await _hubContext.Clients.All.SendAsync("UserSessionRevoked", new
                    {
                        sessionId = id,
                        sessionType = "authenticated"
                    });

                    _logger.LogInformation("Device session revoked: {SessionId}", id);
                    return Ok(new { success = true, message = "Session revoked successfully", sessionId = id });
                }
                return BadRequest(new { error = message });
            }

            // Check if it's a guest session
            var guestSession = _guestSessionService.GetSessionByDeviceId(id);
            if (guestSession != null)
            {
                var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                var revokedBy = ipAddress ?? "Unknown IP";

                var success = _guestSessionService.RevokeSession(id, revokedBy);
                if (success)
                {
                    // Broadcast revocation
                    await _hubContext.Clients.All.SendAsync("UserSessionRevoked", new
                    {
                        sessionId = id,
                        sessionType = "guest"
                    });

                    _logger.LogInformation("Guest session revoked: {SessionId}", id);
                    return Ok(new { success = true, message = "Session revoked successfully", sessionId = id });
                }
            }

            return NotFound(new { error = "Session not found", sessionId = id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error revoking session: {SessionId}", id);
            return StatusCode(500, new { error = "Failed to revoke session", message = ex.Message });
        }
    }

    public class CreateSessionRequest
    {
        public string SessionId { get; set; } = string.Empty;
        public string? DeviceName { get; set; }
        public string? OperatingSystem { get; set; }
        public string? Browser { get; set; }
    }
}
