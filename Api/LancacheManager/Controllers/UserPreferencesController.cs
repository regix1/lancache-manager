using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;
using static LancacheManager.Core.Services.UserPreferencesService;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for user preferences management
/// Handles reading and updating user preferences per session (device or guest)
/// </summary>
[ApiController]
[Route("api/user-preferences")]
public class UserPreferencesController : ControllerBase
{
    private readonly ILogger<UserPreferencesController> _logger;
    private readonly UserPreferencesService _preferencesService;
    private readonly DeviceAuthService _deviceAuthService;
    private readonly GuestSessionService _guestSessionService;
    private readonly ISignalRNotificationService _notifications;

    public UserPreferencesController(
        ILogger<UserPreferencesController> logger,
        UserPreferencesService preferencesService,
        DeviceAuthService deviceAuthService,
        GuestSessionService guestSessionService,
        ISignalRNotificationService notifications)
    {
        _logger = logger;
        _preferencesService = preferencesService;
        _deviceAuthService = deviceAuthService;
        _guestSessionService = guestSessionService;
        _notifications = notifications;
    }

    /// <summary>
    /// Get user preferences for the current session
    /// Returns default preferences if no session exists (for unauthenticated users)
    /// </summary>
    [HttpGet]
    public IActionResult GetPreferences()
    {
        var sessionId = GetSessionId();

        // If no session, return defaults (allows app to load before guest session is created)
        if (sessionId == null)
        {
            _logger.LogInformation("No session found, returning default preferences");
            return Ok(new UserPreferencesDto
            {
                SelectedTheme = null,
                SharpCorners = false,
                DisableFocusOutlines = false,
                DisableTooltips = false,
                PicsAlwaysVisible = false,
                DisableStickyNotifications = false,
                UseLocalTimezone = false,
                Use24HourFormat = true,
                ShowDatasourceLabels = true,
                ShowYearInDates = false,
                RefreshRate = null,
                AllowedTimeFormats = null
            });
        }

        var preferences = _preferencesService.GetPreferences(sessionId);
        if (preferences == null)
        {
            // Return default preferences if none exist for this session
            _logger.LogInformation("No preferences found for session {SessionId}, returning defaults", sessionId);
            return Ok(new UserPreferencesDto
            {
                SelectedTheme = null,
                SharpCorners = false,
                DisableFocusOutlines = false,
                DisableTooltips = false,
                PicsAlwaysVisible = false,
                DisableStickyNotifications = false,
                UseLocalTimezone = false,
                Use24HourFormat = true,
                ShowDatasourceLabels = true,
                ShowYearInDates = false,
                RefreshRate = null,
                AllowedTimeFormats = null
            });
        }

        return Ok(preferences);
    }

    /// <summary>
    /// Update user preferences for the current session
    /// RESTful: PUT is proper method for updating/replacing resources
    /// </summary>
    [HttpPut]
    public async Task<IActionResult> SavePreferences([FromBody] UserPreferencesDto preferences)
    {
        var sessionId = GetSessionId();
        if (sessionId == null)
        {
            return Unauthorized(new PreferencesUpdateResponse { Message = "No valid session found" });
        }

        var success = _preferencesService.SavePreferences(sessionId, preferences);
        if (success)
        {
            _logger.LogInformation("Broadcasting UserPreferencesUpdated for session {SessionId} (own preferences)", sessionId);

            // Broadcast preference update via SignalR
            await _notifications.NotifyAllAsync(SignalREvents.UserPreferencesUpdated, new { sessionId, preferences });

            _logger.LogInformation("UserPreferencesUpdated broadcast complete");

            return Ok(new PreferencesUpdateResponse { Message = "Preferences saved successfully" });
        }

        return StatusCode(500, new PreferencesUpdateResponse { Message = "Error saving preferences" });
    }

    /// <summary>
    /// Update a specific preference field
    /// </summary>
    [HttpPatch("{key}")]
    public async Task<IActionResult> UpdatePreference(string key, [FromBody] object value)
    {
        var sessionId = GetSessionId();
        if (sessionId == null)
        {
            return Unauthorized(new PreferencesUpdateResponse { Message = "No valid session found" });
        }

        // Use UpdatePreferenceAndGet to get updated preferences in the same transaction
        // This prevents race conditions where GetPreferences reads stale data
        var preferences = _preferencesService.UpdatePreferenceAndGet(sessionId, key, value);

        if (preferences != null)
        {
            _logger.LogInformation("Broadcasting UserPreferencesUpdated for session {SessionId} (single pref: {Key}={Value})", sessionId, key, value);

            // Broadcast preference update via SignalR
            await _notifications.NotifyAllAsync(SignalREvents.UserPreferencesUpdated, new { sessionId, preferences });

            _logger.LogInformation("UserPreferencesUpdated broadcast complete");

            return Ok(new PreferencesUpdateResponse { Message = "Preference updated successfully" });
        }

        return BadRequest(new PreferencesUpdateResponse { Message = "Invalid preference key" });
    }

    /// <summary>
    /// Get preferences for a specific session (admin only)
    /// Returns default preferences if none exist for the session
    /// </summary>
    [HttpGet("session/{sessionId}")]
    public IActionResult GetPreferencesForSession(string sessionId)
    {
        // Verify the requesting user is authenticated (not a guest)
        var requestingSessionId = GetSessionId();
        if (requestingSessionId == null || !IsAuthenticated())
        {
            return Unauthorized(new PreferencesUpdateResponse { Message = "Admin access required" });
        }

        var preferences = _preferencesService.GetPreferences(sessionId);
        if (preferences == null)
        {
            // Return default preferences instead of 404
            _logger.LogInformation("No preferences found for session {SessionId}, returning defaults", sessionId);
            return Ok(new UserPreferencesDto
            {
                SelectedTheme = null,
                SharpCorners = false,
                DisableFocusOutlines = false,
                DisableTooltips = false,
                PicsAlwaysVisible = false,
                DisableStickyNotifications = false,
                UseLocalTimezone = false,
                Use24HourFormat = true,
                ShowDatasourceLabels = true,
                ShowYearInDates = false,
                RefreshRate = null,
                AllowedTimeFormats = null
            });
        }

        return Ok(preferences);
    }

    /// <summary>
    /// Update preferences for a specific session (admin only)
    /// RESTful: PUT is proper method for updating/replacing resources
    /// </summary>
    [HttpPut("session/{sessionId}")]
    public async Task<IActionResult> SavePreferencesForSession(string sessionId, [FromBody] UserPreferencesDto preferences)
    {
        // Verify the requesting user is authenticated (not a guest)
        var requestingSessionId = GetSessionId();
        if (requestingSessionId == null || !IsAuthenticated())
        {
            return Unauthorized(new PreferencesUpdateResponse { Message = "Admin access required" });
        }

        var success = _preferencesService.SavePreferences(sessionId, preferences);
        if (success)
        {
            // Broadcast preference update via SignalR to notify the target user
            await _notifications.NotifyAllAsync(SignalREvents.UserPreferencesUpdated, new { sessionId, preferences });

            return Ok(new PreferencesUpdateResponse { Message = "Preferences saved successfully" });
        }

        return StatusCode(500, new PreferencesUpdateResponse { Message = "Error saving preferences" });
    }

    /// <summary>
    /// Helper method to get the current session ID from device ID (works for both authenticated and guest users)
    /// </summary>
    private string? GetSessionId()
    {
        var deviceId = Request.Headers["X-Device-Id"].FirstOrDefault();
        if (string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        // Check if this is an authenticated device (not guest)
        if (_deviceAuthService.ValidateDevice(deviceId))
        {
            return deviceId;
        }

        // Check if this is a guest session (for guests, device ID = session ID)
        if (_guestSessionService.ValidateSession(deviceId))
        {
            return deviceId;
        }

        // No valid session found (user hasn't registered as guest or authenticated yet)
        return null;
    }

    /// <summary>
    /// Helper method to check if the current session is authenticated (not a guest)
    /// </summary>
    private bool IsAuthenticated()
    {
        var deviceId = Request.Headers["X-Device-Id"].FirstOrDefault();
        return !string.IsNullOrEmpty(deviceId) && _deviceAuthService.ValidateDevice(deviceId);
    }
}
