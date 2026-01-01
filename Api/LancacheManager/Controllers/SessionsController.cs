using LancacheManager.Application.DTOs;
using LancacheManager.Application.Services;
using LancacheManager.Security;
using LancacheManager.Data;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Infrastructure.Repositories.Interfaces;
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
    private readonly IDatabaseRepository _databaseRepository;
    private readonly ConnectionTrackingService _connectionTrackingService;
    private readonly UserPreferencesService _userPreferencesService;
    private readonly StateRepository _stateService;

    public SessionsController(
        DeviceAuthService deviceAuthService,
        GuestSessionService guestSessionService,
        ILogger<SessionsController> logger,
        IHubContext<DownloadHub> hubContext,
        AppDbContext dbContext,
        IDatabaseRepository databaseRepository,
        ConnectionTrackingService connectionTrackingService,
        UserPreferencesService userPreferencesService,
        StateRepository stateService)
    {
        _deviceAuthService = deviceAuthService;
        _guestSessionService = guestSessionService;
        _logger = logger;
        _hubContext = hubContext;
        _dbContext = dbContext;
        _databaseRepository = databaseRepository;
        _connectionTrackingService = connectionTrackingService;
        _userPreferencesService = userPreferencesService;
        _stateService = stateService;
    }

    /// <summary>
    /// GET /api/sessions - List all sessions (authenticated + guest)
    /// Supports pagination via ?page=1&pageSize=20 query parameters
    /// RESTful: GET is proper method for retrieving resource collections
    /// </summary>
    [HttpGet]
    [RequireAuth]
    public IActionResult GetAllSessions([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        // Validate pagination parameters
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var devices = _deviceAuthService.GetAllDevices();
        var guests = _guestSessionService.GetAllSessions();

        // Convert to unified format
        var authenticatedSessions = devices.Select(d => new
        {
            id = d.DeviceId, // Use DeviceId as the primary identifier
            deviceId = d.DeviceId,
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
        // If the same device ID exists in both authenticated and guest, show only authenticated
        var authenticatedDeviceIds = new HashSet<string>(devices.Select(d => d.DeviceId));

        var guestSessions = guests
            .Where(g => !authenticatedDeviceIds.Contains(g.DeviceId))
            .Select(g => new
            {
                id = g.DeviceId, // Use DeviceId as the primary identifier
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

        // Sort: authenticated users first, then guests, both by creation date (newest first)
        var allSessionsSorted = authenticatedSessions.Concat(guestSessions)
            .OrderBy(s => s.type == "guest" ? 1 : 0)  // authenticated (0) before guests (1)
            .ThenByDescending(s => s.createdAt)  // newest first within each type
            .ToList();

        // Apply pagination
        var totalCount = allSessionsSorted.Count;
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        var skip = (page - 1) * pageSize;

        var paginatedSessions = allSessionsSorted
            .Skip(skip)
            .Take(pageSize)
            .ToList();

        return Ok(new
        {
            sessions = paginatedSessions,
            pagination = new
            {
                page = page,
                pageSize = pageSize,
                totalCount = totalCount,
                totalPages = totalPages,
                hasNextPage = page < totalPages,
                hasPreviousPage = page > 1
            },
            count = totalCount,
            authenticatedCount = authenticatedSessions.Count,
            guestCount = guestSessions.Count
        });
    }

    /// <summary>
    /// GET /api/sessions/{id} - Get specific session
    /// RESTful: GET is proper method for retrieving a single resource
    /// </summary>
    [HttpGet("{id}")]
    [RequireAuth]
    public IActionResult GetSession(string id)
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
                id = guestSession.DeviceId,
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

        return NotFound(new ErrorResponse { Error = "Session not found" });
    }

    /// <summary>
    /// POST /api/sessions?type=guest - Create guest session
    /// RESTful: POST is proper method for creating resources
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateSession([FromQuery] string? type, [FromBody] CreateSessionRequest request)
    {
        // Block session creation during database reset operations
        if (_databaseRepository.IsResetOperationRunning)
        {
            _logger.LogWarning("Guest session creation rejected - database reset in progress");
            return StatusCode(503, new ServiceUnavailableResponse
            {
                Error = "Service temporarily unavailable",
                Message = "Database reset in progress. Please wait and try again.",
                RetryAfter = 30
            });
        }

        // Only support guest session creation via this endpoint
        // Authenticated sessions are created via POST /api/devices
        if (type != "guest")
        {
            return BadRequest(new ErrorResponse { Error = "Only guest session creation is supported. Use POST /api/devices for authenticated sessions." });
        }

        // Check if guest mode is locked by admin
        if (_stateService.GetGuestModeLocked())
        {
            _logger.LogWarning("Guest session creation rejected - guest mode is locked");
            return StatusCode(403, new ErrorResponse { Error = "Guest mode is currently disabled by the administrator" });
        }

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var session = _guestSessionService.CreateSession(
            new GuestSessionService.CreateGuestSessionRequest
            {
                DeviceId = request.DeviceId, // Browser fingerprint
                DeviceName = request.DeviceName,
                OperatingSystem = request.OperatingSystem,
                Browser = request.Browser
            },
            ipAddress
        );

        // Ensure session data exists so session ID is generated
        HttpContext.Session.SetString("DeviceId", session.DeviceId);
        HttpContext.Session.SetString("AuthMode", "guest");

        // Broadcast session creation via SignalR for real-time updates
        await _hubContext.Clients.All.SendAsync("UserSessionCreated", new
        {
            deviceId = session.DeviceId,
            sessionType = "guest"
        });

        return Created($"/api/sessions/{session.DeviceId}", new SessionCreateResponse
        {
            Success = true,
            DeviceId = session.DeviceId,
            ExpiresAt = session.ExpiresAt,
            Message = "Guest session created successfully"
        });
    }

    /// <summary>
    /// PATCH /api/sessions/current/last-seen - Update last seen timestamp for current session (heartbeat)
    /// RESTful: Uses device ID from X-Device-Id header instead of URL parameter
    /// This prevents 404 errors when sessions are cleared on app restart
    /// </summary>
    [HttpPatch("current/last-seen")]
    public async Task<IActionResult> UpdateCurrentLastSeen()
    {
        // Get device ID from request header
        var deviceId = Request.Headers["X-Device-Id"].FirstOrDefault();
        if (string.IsNullOrEmpty(deviceId))
        {
            return BadRequest(new ErrorResponse { Error = "X-Device-Id header is required" });
        }

        // Try updating authenticated device first
        var device = _deviceAuthService.GetAllDevices().FirstOrDefault(d => d.DeviceId == deviceId);
        if (device != null)
        {
            _deviceAuthService.UpdateLastSeen(deviceId);

            // Notify authenticated users (admins viewing UserTab) that this session's lastSeenAt was updated
            // Only send to authenticated group to avoid warnings on guest clients
            await _hubContext.Clients.Group(Hubs.DownloadHub.AuthenticatedUsersGroup).SendAsync("SessionLastSeenUpdated", new
            {
                deviceId = deviceId,
                sessionType = "authenticated",
                lastSeenAt = DateTime.UtcNow.ToString("o")
            });

            return Ok(new SessionHeartbeatResponse { Success = true, Type = "authenticated" });
        }

        // Try updating guest session
        var guestSession = _guestSessionService.GetSessionByDeviceId(deviceId);
        if (guestSession != null && !guestSession.IsRevoked && guestSession.ExpiresAt > DateTime.UtcNow)
        {
            _guestSessionService.UpdateLastSeen(deviceId);

            // Notify authenticated users (admins viewing UserTab) that this session's lastSeenAt was updated
            // Only send to authenticated group to avoid warnings on guest clients
            await _hubContext.Clients.Group(Hubs.DownloadHub.AuthenticatedUsersGroup).SendAsync("SessionLastSeenUpdated", new
            {
                deviceId = deviceId,
                sessionType = "guest",
                lastSeenAt = DateTime.UtcNow.ToString("o")
            });

            return Ok(new SessionHeartbeatResponse { Success = true, Type = "guest" });
        }

        // Session not found or invalid - this is OK, it just means the app restarted
        // Return 200 instead of 404 to prevent errors in the frontend
        _logger.LogDebug("Session not found for device {DeviceId} - likely app restart", deviceId);
        return Ok(new SessionHeartbeatResponse { Success = true, Type = "none", Message = "Session will be restored on next auth check" });
    }


    /// <summary>
    /// DELETE /api/sessions/current - Delete current session (logout)
    /// RESTful: Uses device ID from X-Device-Id header instead of URL parameter
    /// This prevents 404 errors when sessions are cleared on app restart
    /// </summary>
    [HttpDelete("current")]
    [RequireAuth]
    public async Task<IActionResult> DeleteCurrentSession()
    {
        // Get device ID from request header
        var deviceId = Request.Headers["X-Device-Id"].FirstOrDefault();
        if (string.IsNullOrEmpty(deviceId))
        {
            return BadRequest(new ErrorResponse { Error = "X-Device-Id header is required" });
        }

        // Check if it's an authenticated device
        var device = _deviceAuthService.GetAllDevices().FirstOrDefault(d => d.DeviceId == deviceId);
        if (device != null)
        {
            var (success, message) = _deviceAuthService.RevokeDevice(deviceId);
            if (success)
            {
                // Clear session cookie
                HttpContext.Session.Clear();

                // Broadcast deletion
                await _hubContext.Clients.All.SendAsync("UserSessionRevoked", new
                {
                    deviceId = deviceId,
                    sessionType = "authenticated"
                });

                _logger.LogInformation("Device session deleted: {DeviceId}", deviceId);
                return Ok(new SessionDeleteResponse { Success = true, Message = "Session deleted successfully" });
            }
            return BadRequest(new ErrorResponse { Error = message });
        }

        // Check if it's a guest session
        var guestSession = _guestSessionService.GetSessionByDeviceId(deviceId);
        if (guestSession != null)
        {
            var deleteSuccess = _guestSessionService.DeleteSession(deviceId);
            if (deleteSuccess)
            {
                // Broadcast deletion
                await _hubContext.Clients.All.SendAsync("UserSessionRevoked", new
                {
                    deviceId = deviceId,
                    sessionType = "guest"
                });

                _logger.LogInformation("Guest session deleted: {DeviceId}", deviceId);
                return Ok(new SessionDeleteResponse { Success = true, Message = "Session deleted successfully" });
            }
        }

        // Session not found - this is OK if the app restarted
        // Clear local session anyway
        HttpContext.Session.Clear();
        return Ok(new SessionDeleteResponse { Success = true, Message = "Session cleared successfully" });
    }

    /// <summary>
    /// DELETE /api/sessions/{id}?action=delete|revoke - Delete or revoke session
    /// RESTful: DELETE is proper method for removing resources
    /// ?action=delete - Permanently removes session from database (default)
    /// ?action=revoke - Marks guest session as revoked but keeps in history (guests only)
    /// </summary>
    [HttpDelete("{id}")]
    [RequireAuth]
    public async Task<IActionResult> DeleteSession(string id, [FromQuery] string action = "delete")
    {
        var isPermanentDelete = action.ToLower() == "delete";

        // Check if it's an authenticated device
        var device = _deviceAuthService.GetAllDevices().FirstOrDefault(d => d.DeviceId == id);
        if (device != null)
        {
            // Authenticated devices are always deleted (no revoke-only option)
            var (success, message) = _deviceAuthService.RevokeDevice(id);
            if (success)
            {
                // Clear session cookie if deleting own session
                var currentSessionDeviceId = HttpContext.Session.GetString("DeviceId");
                if (currentSessionDeviceId == id)
                {
                    HttpContext.Session.Clear();
                }

                // Broadcast deletion
                await _hubContext.Clients.All.SendAsync("UserSessionRevoked", new
                {
                    deviceId = id,
                    sessionType = "authenticated"
                });

                _logger.LogInformation("Device session deleted: {DeviceId}", id);
                return Ok(new SessionDeleteResponse { Success = true, Message = "Session deleted successfully" });
            }
            return BadRequest(new ErrorResponse { Error = message });
        }

        // Check if it's a guest session
        var guestSession = _guestSessionService.GetSessionByDeviceId(id);
        if (guestSession != null)
        {
            bool success;
            string actionMessage;

            if (isPermanentDelete)
            {
                // Permanently delete the guest session
                success = _guestSessionService.DeleteSession(id);
                actionMessage = "deleted";
            }
            else
            {
                // Just revoke the guest session (keep in history)
                var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                var revokedBy = ipAddress ?? "Unknown IP";
                success = _guestSessionService.RevokeSession(id, revokedBy);
                actionMessage = "revoked";
            }

            if (success)
            {
                // Broadcast action
                await _hubContext.Clients.All.SendAsync("UserSessionRevoked", new
                {
                    deviceId = id,
                    sessionType = "guest"
                });

                _logger.LogInformation("Guest session {Action}: {DeviceId}", actionMessage, id);
                return Ok(new SessionDeleteResponse { Success = true, Message = $"Session {actionMessage} successfully" });
            }
        }

        return NotFound(new ErrorResponse { Error = "Session not found" });
    }

    /// <summary>
    /// PATCH /api/sessions/{id}/refresh-rate - Set the refresh rate for a specific guest session
    /// RESTful: PATCH is proper method for partial resource updates
    /// Request body: { "refreshRate": "LIVE" | "ULTRA" | "REALTIME" | "STANDARD" | "RELAXED" | "SLOW" | null }
    /// Pass null or empty string to reset to default guest refresh rate
    /// </summary>
    [HttpPatch("{id}/refresh-rate")]
    [RequireAuth]
    public async Task<IActionResult> SetSessionRefreshRate(string id, [FromBody] SetSessionRefreshRateRequest request)
    {
        // Check if it's a guest session
        var guestSession = _guestSessionService.GetSessionByDeviceId(id);
        if (guestSession == null)
        {
            return NotFound(new ErrorResponse { Error = "Guest session not found" });
        }

        string? refreshRate = null;
        var isReset = string.IsNullOrWhiteSpace(request.RefreshRate);

        if (!isReset)
        {
            var validRates = new[] { "LIVE", "ULTRA", "REALTIME", "STANDARD", "RELAXED", "SLOW" };
            if (!validRates.Contains(request.RefreshRate!.ToUpperInvariant()))
            {
                return BadRequest(new ErrorResponse { Error = "Invalid refresh rate. Must be LIVE, ULTRA, REALTIME, STANDARD, RELAXED, or SLOW" });
            }
            refreshRate = request.RefreshRate.ToUpperInvariant();
        }

        // Update the refresh rate in user preferences (null clears custom rate)
        _userPreferencesService.UpdatePreferenceAndGet(id, "RefreshRate", refreshRate);

        if (isReset)
        {
            _logger.LogInformation("Guest session refresh rate reset to default: DeviceId={DeviceId}", id);
        }
        else
        {
            _logger.LogInformation("Guest session refresh rate set: DeviceId={DeviceId}, Rate={Rate}", id, refreshRate);
        }

        // Push the new rate to the guest if they're connected
        var connectionId = _connectionTrackingService.GetConnectionId(id);
        if (!string.IsNullOrEmpty(connectionId))
        {
            // If reset, send the default guest refresh rate
            var rateToSend = refreshRate ?? _stateService.GetDefaultGuestRefreshRate();
            await _hubContext.Clients.Client(connectionId).SendAsync("GuestRefreshRateUpdated", new
            {
                refreshRate = rateToSend,
                isDefault = isReset
            });
            _logger.LogDebug("Pushed refresh rate to connected guest: DeviceId={DeviceId}, ConnectionId={ConnectionId}, Rate={Rate}, IsDefault={IsDefault}",
                id, connectionId, rateToSend, isReset);
        }
        else
        {
            _logger.LogDebug("Guest not connected, refresh rate will apply on reconnect: DeviceId={DeviceId}", id);
        }

        return Ok(new SetSessionRefreshRateResponse
        {
            Success = true,
            Message = isReset ? "Refresh rate reset to default" : "Refresh rate updated",
            RefreshRate = refreshRate ?? _stateService.GetDefaultGuestRefreshRate()
        });
    }

    /// <summary>
    /// Reset all guest sessions to default preferences
    /// </summary>
    [HttpPost("bulk/reset-to-defaults")]
    [RequireAuth]
    public async Task<IActionResult> BulkResetToDefaults()
    {
        var guestSessions = _guestSessionService.GetAllSessions()
            .Where(s => !s.IsRevoked && !s.IsExpired)
            .ToList();
        var affectedCount = 0;

        // Get default preferences from state
        var state = _stateService.GetState();
        var defaultPrefsDto = new UserPreferencesService.UserPreferencesDto
        {
            SelectedTheme = null, // null means use default guest theme
            SharpCorners = state.DefaultGuestSharpCorners,
            DisableFocusOutlines = true, // Default
            DisableTooltips = state.DefaultGuestDisableTooltips,
            PicsAlwaysVisible = false, // Default
            DisableStickyNotifications = false, // Default
            ShowDatasourceLabels = state.DefaultGuestShowDatasourceLabels,
            UseLocalTimezone = state.DefaultGuestUseLocalTimezone,
            Use24HourFormat = state.DefaultGuestUse24HourFormat,
            ShowYearInDates = state.DefaultGuestShowYearInDates,
            RefreshRate = null // null means use default guest refresh rate
        };

        foreach (var session in guestSessions)
        {
            try
            {
                // Reset preferences for this session
                _userPreferencesService.SavePreferences(session.DeviceId, defaultPrefsDto);
                affectedCount++;

                // Notify the session about the preference change
                await _hubContext.Clients.All.SendAsync("UserPreferencesUpdated", new
                {
                    sessionId = session.DeviceId,
                    preferences = new
                    {
                        selectedTheme = defaultPrefsDto.SelectedTheme,
                        sharpCorners = defaultPrefsDto.SharpCorners,
                        disableFocusOutlines = defaultPrefsDto.DisableFocusOutlines,
                        disableTooltips = defaultPrefsDto.DisableTooltips,
                        picsAlwaysVisible = defaultPrefsDto.PicsAlwaysVisible,
                        disableStickyNotifications = defaultPrefsDto.DisableStickyNotifications,
                        showDatasourceLabels = defaultPrefsDto.ShowDatasourceLabels,
                        useLocalTimezone = defaultPrefsDto.UseLocalTimezone,
                        use24HourFormat = defaultPrefsDto.Use24HourFormat,
                        showYearInDates = defaultPrefsDto.ShowYearInDates,
                        refreshRate = defaultPrefsDto.RefreshRate
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to reset preferences for guest session {SessionId}", session.DeviceId);
            }
        }

        _logger.LogInformation("Reset {Count} guest sessions to default preferences", affectedCount);

        return Ok(new { affectedCount, message = $"Reset {affectedCount} guest sessions to defaults" });
    }

    /// <summary>
    /// Clear all guest sessions (revoke and delete)
    /// </summary>
    [HttpDelete("bulk/clear-guests")]
    [RequireAuth]
    public async Task<IActionResult> BulkClearGuests()
    {
        var guestSessions = _guestSessionService.GetAllSessions()
            .Where(s => !s.IsRevoked && !s.IsExpired)
            .ToList();
        var clearedCount = 0;

        foreach (var session in guestSessions)
        {
            try
            {
                _guestSessionService.RevokeSession(session.DeviceId, "Admin (bulk action)");
                clearedCount++;

                // Notify connected clients
                await _hubContext.Clients.All.SendAsync("UserSessionRevoked", new
                {
                    deviceId = session.DeviceId,
                    sessionType = "guest"
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to revoke guest session {SessionId}", session.DeviceId);
            }
        }

        _logger.LogInformation("Cleared {Count} guest sessions", clearedCount);

        return Ok(new { clearedCount, message = $"Cleared {clearedCount} guest sessions" });
    }

    public class SetSessionRefreshRateRequest
    {
        public string RefreshRate { get; set; } = string.Empty;
    }

    public class SetSessionRefreshRateResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string RefreshRate { get; set; } = string.Empty;
    }

    public class CreateSessionRequest
    {
        public string DeviceId { get; set; } = string.Empty; // Browser fingerprint
        public string? DeviceName { get; set; }
        public string? OperatingSystem { get; set; }
        public string? Browser { get; set; }
    }
}
