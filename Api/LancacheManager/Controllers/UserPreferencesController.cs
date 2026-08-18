using System.Text.Json;
using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Core.Interfaces;
using LancacheManager.Middleware;
using LancacheManager.Hubs;
using LancacheManager.Security;
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
    private readonly SessionService _sessionService;

    public UserPreferencesController(
        ILogger<UserPreferencesController> logger,
        UserPreferencesService preferencesService,
        ISignalRNotificationService notifications,
        SessionService sessionService)
    {
        _logger = logger;
        _preferencesService = preferencesService;
        _notifications = notifications;
        _sessionService = sessionService;
    }

    /// <summary>
    /// Gets the caller's own preferences via their session, or defaults when no session or no stored
    /// preferences exist yet.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(UserPreferencesDto), StatusCodes.Status200OK)]
    public ActionResult<UserPreferencesDto> GetPreferences()
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

    /// <summary>
    /// Replaces the caller's own preferences.
    /// </summary>
    /// <remarks>
    /// The stored/broadcast values are not always exactly what was sent: clock fields are settled
    /// against each other and an unrecognised refresh rate is dropped, and admin-only columns are
    /// redacted before a guest's own save is announced to everyone.
    /// </remarks>
    [HttpPut]
    [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> SavePreferencesAsync([FromBody] UserPreferencesDto preferences)
    {
        var session = GetSession();
        if (session == null)
        {
            return BadRequest(new MessageResponse { Success = false, Message = "No session found" });
        }

        var sessionId = session.Id;

        // What the server stored is not always what arrived: the clock columns are settled against each
        // other and an unrecognised refresh rate is dropped. Sending the request body on would tell every
        // client the values were taken as written.
        var stored = await _preferencesService.SavePreferencesAsync(
            sessionId,
            preferences,
            preserveAdminFields: !session.SessionType.IsAccountHolder());
        if (stored != null)
        {
            RedactForBroadcast(stored, session);
            await _notifications.NotifyAllAsync(SignalREvents.UserPreferencesUpdated, new { sessionId, preferences = stored });
            return Ok(MessageResponse.Ok("Preferences saved successfully"));
        }

        return StatusCode(500, new MessageResponse { Success = false, Message = "Error saving preferences" });
    }

    /// <summary>
    /// Takes the admin-only columns off preferences that are about to be announced on behalf of a guest.
    /// The stored row carries them whoever last wrote it, and this announcement reaches every connected
    /// client rather than only the session it names, so one guest's own save would otherwise hand its
    /// allowed formats, refresh lock and thread caps to everyone. An account holder's write is left
    /// whole: those screens are the ones that set the values and need to see them land.
    /// </summary>
    private static void RedactForBroadcast(UserPreferencesDto stored, UserSession session)
    {
        if (!session.SessionType.IsAccountHolder())
        {
            UserPreferencesService.RedactAdminFields(stored);
        }
    }

    /// <summary>
    /// Updates a single preference by key for the caller's own session.
    /// </summary>
    /// <remarks>
    /// Guests are forbidden from writing admin-only keys.
    /// </remarks>
    [HttpPatch("{key}")]
    [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
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
        if (!session.SessionType.IsAccountHolder() && UserPreferencesService.IsAdminOnlyKey(preferenceKey))
            return Forbid();

        var preferences = await _preferencesService.UpdatePreferenceAsync(sessionId, preferenceKey, value);

        if (preferences != null)
        {
            RedactForBroadcast(preferences, session);
            await _notifications.NotifyAllAsync(SignalREvents.UserPreferencesUpdated, new { sessionId, preferences });
            return Ok(MessageResponse.Ok("Preference updated successfully"));
        }

        // A bad key was already rejected at the ParseFromString check above, and a missing session at the
        // top, so the only way the service comes back empty here is that the write itself failed. Saying
        // "invalid preference key" a second time named the one cause that cannot apply, which is what kept
        // a lost concurrent write looking like a client mistake.
        return StatusCode(500, new MessageResponse { Success = false, Message = "Error updating preference" });
    }

    /// <summary>
    /// Writes the three clock preference columns in one go.
    /// </summary>
    /// <remarks>
    /// A literal segment wins over the "{key}" route above, so this reaches the clock write rather
    /// than being read as a preference key.
    /// </remarks>
    [HttpPatch("clock")]
    [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateClockPreferencesAsync([FromBody] ClockPreferences clock)
    {
        var session = GetSession();
        if (session == null)
        {
            return BadRequest(new MessageResponse { Success = false, Message = "No session found" });
        }

        var sessionId = session.Id;

        var preferences = await _preferencesService.UpdateClockPreferencesAsync(sessionId, clock);

        if (preferences != null)
        {
            RedactForBroadcast(preferences, session);
            await _notifications.NotifyAllAsync(SignalREvents.UserPreferencesUpdated, new { sessionId, preferences });
            return Ok(MessageResponse.Ok("Preference updated successfully"));
        }

        return StatusCode(500, new MessageResponse { Success = false, Message = "Error updating preference" });
    }

    /// <summary>
    /// Gets a session's preferences by ID. A session belonging to the owner answers as one that
    /// does not exist.
    /// </summary>
    [Authorize(Policy = "AccountHolder")]
    [HttpGet("session/{sessionId}")]
    [ProducesResponseType(typeof(UserPreferencesDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<UserPreferencesDto>> GetForSessionAsync(Guid sessionId)
    {
        if (!await _sessionService.CallerMaySeeSessionAsync(GetSession(), sessionId))
        {
            return NotFound(ApiResponse.NotFound("Session"));
        }

        var preferences = _preferencesService.GetPreferences(sessionId);
        if (preferences == null)
        {
            return Ok(UserPreferencesDto.Default());
        }

        return Ok(preferences);
    }

    /// <summary>
    /// Replaces a session's preferences by ID. A session belonging to the owner answers as one
    /// that does not exist.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="SavePreferencesAsync"/>, admin-only columns are not redacted since the
    /// caller is already an admin.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpPut("session/{sessionId}")]
    [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MessageResponse>> SaveForSessionAsync(Guid sessionId, [FromBody] UserPreferencesDto preferences)
    {
        if (!await _sessionService.CallerMaySeeSessionAsync(GetSession(), sessionId))
        {
            return NotFound(ApiResponse.NotFound("Session"));
        }

        var stored = await _preferencesService.SavePreferencesAsync(sessionId, preferences);
        if (stored != null)
        {
            await _notifications.NotifyAllAsync(SignalREvents.UserPreferencesUpdated, new { sessionId, preferences = stored });
            return Ok(MessageResponse.Ok("Preferences saved successfully"));
        }

        return StatusCode(500, new MessageResponse { Success = false, Message = "Error saving preferences" });
    }

    private UserSession? GetSession() => HttpContext.GetUserSession();
    private Guid? GetSessionId() => GetSession()?.Id;
}
