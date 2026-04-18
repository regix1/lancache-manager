using System.Text.Json;
using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Core.Interfaces;
using LancacheManager.Middleware;
using LancacheManager.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static LancacheManager.Core.Services.UserPreferencesService;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for user preferences management.
/// Uses session-based auth via HttpContext.Items["Session"].
/// </summary>
[ApiController]
[Route("api/user-preferences")]
[Authorize]
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
            return Ok(UserPreferencesDto.Default());
        }

        var preferences = _preferencesService.GetPreferences(sessionId.Value);
        if (preferences == null)
        {
            _logger.LogInformation("No preferences found for session {SessionId}, returning defaults", sessionId);
            return Ok(UserPreferencesDto.Default());
        }

        return Ok(preferences);
    }

    [HttpPut]
    public async Task<IActionResult> SavePreferencesAsync([FromBody] UserPreferencesDto preferences)
    {
        var session = GetSession();
        if (session == null)
        {
            return BadRequest(new MessageResponse { Success = false, Message = "No session found" });
        }

        var sessionId = session.Id;

        // Strip admin-only fields for non-admin sessions
        if (session.SessionType != SessionType.Admin)
            UserPreferencesService.StripAdminOnlyFields(preferences);

        var success = await _preferencesService.SavePreferencesAsync(sessionId, preferences);
        if (success)
        {
            await _notifications.NotifyAllAsync(SignalREvents.UserPreferencesUpdated, new { sessionId, preferences });
            return Ok(MessageResponse.Ok("Preferences saved successfully"));
        }

        return StatusCode(500, new MessageResponse { Success = false, Message = "Error saving preferences" });
    }

    [HttpPatch("{key}")]
    public async Task<IActionResult> UpdatePreferenceAsync(string key, [FromBody] JsonElement value)
    {
        var session = GetSession();
        if (session == null)
        {
            return BadRequest(new MessageResponse { Success = false, Message = "No session found" });
        }

        var sessionId = session.Id;

        var preferenceKey = PreferenceKeyJsonConverter.ParseFromString(key);
        if (preferenceKey == PreferenceKey.Unknown)
            return BadRequest(new MessageResponse { Success = false, Message = "Invalid preference key" });

        // Guests cannot write admin-only preference keys
        if (session.SessionType != SessionType.Admin && UserPreferencesService.IsAdminOnlyKey(preferenceKey))
            return Forbid();

        var preferences = await _preferencesService.UpdatePreferenceAndGetAsync(sessionId, preferenceKey, value);

        if (preferences != null)
        {
            await _notifications.NotifyAllAsync(SignalREvents.UserPreferencesUpdated, new { sessionId, preferences });
            return Ok(MessageResponse.Ok("Preference updated successfully"));
        }

        return BadRequest(new MessageResponse { Success = false, Message = "Invalid preference key" });
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpGet("session/{sessionId}")]
    public IActionResult GetPreferencesForSession(Guid sessionId)
    {
        var preferences = _preferencesService.GetPreferences(sessionId);
        if (preferences == null)
        {
            return Ok(UserPreferencesDto.Default());
        }

        return Ok(preferences);
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPut("session/{sessionId}")]
    public async Task<IActionResult> SavePreferencesForSessionAsync(Guid sessionId, [FromBody] UserPreferencesDto preferences)
    {
        var success = await _preferencesService.SavePreferencesAsync(sessionId, preferences);
        if (success)
        {
            await _notifications.NotifyAllAsync(SignalREvents.UserPreferencesUpdated, new { sessionId, preferences });
            return Ok(MessageResponse.Ok("Preferences saved successfully"));
        }

        return StatusCode(500, new MessageResponse { Success = false, Message = "Error saving preferences" });
    }

    private UserSession? GetSession() => HttpContext.GetUserSession();
    private Guid? GetSessionId() => GetSession()?.Id;
}
