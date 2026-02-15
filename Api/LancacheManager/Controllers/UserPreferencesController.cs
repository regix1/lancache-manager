using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using Microsoft.AspNetCore.Mvc;
using static LancacheManager.Core.Services.UserPreferencesService;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for user preferences management.
/// Uses session-based auth via HttpContext.Items["Session"].
/// </summary>
[ApiController]
[Route("api/user-preferences")]
public class UserPreferencesController : ControllerBase
{
    private readonly ILogger<UserPreferencesController> _logger;
    private readonly UserPreferencesService _preferencesService;
    private readonly ISignalRNotificationService _notifications;

    public UserPreferencesController(
        ILogger<UserPreferencesController> logger,
        UserPreferencesService preferencesService,
        ISignalRNotificationService notifications)
    {
        _logger = logger;
        _preferencesService = preferencesService;
        _notifications = notifications;
    }

    [HttpGet]
    public IActionResult GetPreferences()
    {
        var sessionId = GetSessionId();

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

        var preferences = _preferencesService.GetPreferences(sessionId.Value);
        if (preferences == null)
        {
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

    [HttpPut]
    public async Task<IActionResult> SavePreferences([FromBody] UserPreferencesDto preferences)
    {
        var sessionId = GetSessionId();
        if (sessionId == null)
        {
            return BadRequest(new PreferencesUpdateResponse { Message = "No session found" });
        }

        var success = _preferencesService.SavePreferences(sessionId.Value, preferences);
        if (success)
        {
            await _notifications.NotifyAllAsync(SignalREvents.UserPreferencesUpdated, new { sessionId, preferences });
            return Ok(new PreferencesUpdateResponse { Message = "Preferences saved successfully" });
        }

        return StatusCode(500, new PreferencesUpdateResponse { Message = "Error saving preferences" });
    }

    [HttpPatch("{key}")]
    public async Task<IActionResult> UpdatePreference(string key, [FromBody] object value)
    {
        var sessionId = GetSessionId();
        if (sessionId == null)
        {
            return BadRequest(new PreferencesUpdateResponse { Message = "No session found" });
        }

        var preferences = _preferencesService.UpdatePreferenceAndGet(sessionId.Value, key, value);

        if (preferences != null)
        {
            await _notifications.NotifyAllAsync(SignalREvents.UserPreferencesUpdated, new { sessionId, preferences });
            return Ok(new PreferencesUpdateResponse { Message = "Preference updated successfully" });
        }

        return BadRequest(new PreferencesUpdateResponse { Message = "Invalid preference key" });
    }

    [HttpGet("session/{sessionId}")]
    public IActionResult GetPreferencesForSession(Guid sessionId)
    {
        var preferences = _preferencesService.GetPreferences(sessionId);
        if (preferences == null)
        {
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

    [HttpPut("session/{sessionId}")]
    public async Task<IActionResult> SavePreferencesForSession(Guid sessionId, [FromBody] UserPreferencesDto preferences)
    {
        var success = _preferencesService.SavePreferences(sessionId, preferences);
        if (success)
        {
            await _notifications.NotifyAllAsync(SignalREvents.UserPreferencesUpdated, new { sessionId, preferences });
            return Ok(new PreferencesUpdateResponse { Message = "Preferences saved successfully" });
        }

        return StatusCode(500, new PreferencesUpdateResponse { Message = "Error saving preferences" });
    }

    private UserSession? GetSession() => HttpContext.Items["Session"] as UserSession;
    private Guid? GetSessionId() => GetSession()?.Id;
}
