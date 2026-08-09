using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Middleware;
using LancacheManager.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers.Base;

/// <summary>
/// Abstract base controller for daemon session endpoints.
/// Provides the shared owned-games, cache-status, selected-apps and prefill REST endpoints for every
/// daemon controller. Session creation, login, credential exchange and termination are served by the
/// prefill hub, and the admin active-session list by <c>PrefillAdminController.GetActiveSessions</c>,
/// so this class does not route them.
/// Subclasses provide the route prefix and platform-specific thread limit resolution.
/// </summary>
/// <typeparam name="TService">The concrete daemon service type (must extend PrefillDaemonServiceBase)</typeparam>
[ApiController]
public abstract class DaemonControllerBase<TService> : ControllerBase
    where TService : PrefillDaemonServiceBase
{
    protected readonly TService _daemonService;
    protected readonly ILogger _logger;
    protected readonly StateService _stateService;
    protected readonly UserPreferencesService _userPreferencesService;

    /// <summary>
    /// Platform display name used in log messages (e.g., "Steam" or "Epic").
    /// </summary>
    protected readonly string _platformName;

    protected DaemonControllerBase(
        TService daemonService,
        ILogger logger,
        StateService stateService,
        UserPreferencesService userPreferencesService,
        string platformName)
    {
        _daemonService = daemonService;
        _logger = logger;
        _stateService = stateService;
        _userPreferencesService = userPreferencesService;
        _platformName = platformName;
    }

    /// <summary>
    /// Resolves the effective thread limit for the given user session.
    /// Returns null for admin users (no limit).
    /// </summary>
    protected abstract int? ResolveThreadLimit(UserSession session);

    /// <summary>
    /// Lists the games owned by the logged-in session, for the prefill picker.
    /// </summary>
    /// <remarks>
    /// The list comes from the daemon rather than a local catalogue, so it reflects what
    /// that account can actually download.
    /// </remarks>
    [HttpGet("sessions/{sessionId}/games")]
    [ProducesResponseType(typeof(List<OwnedGame>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<OwnedGame>>> GetOwnedGamesAsync(string sessionId)
    {
        var ownershipResult = ValidateSessionOwnership(sessionId);
        if (ownershipResult != null)
        {
            return ownershipResult;
        }

        var games = await _daemonService.GetOwnedGamesAsync(sessionId);
        return Ok(games);
    }

    /// <summary>
    /// Splits the requested apps into up-to-date and stale groups.
    /// </summary>
    /// <remarks>
    /// This lets the picker show what a prefill would actually download. App ids are passed
    /// in the request body rather than the query string because a full selection is far too long for a URL.
    /// </remarks>
    [HttpPost("sessions/{sessionId}/cache-status")]
    [ProducesResponseType(typeof(PrefillCacheStatusResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PrefillCacheStatusResponse>> GetCacheStatusAsync(string sessionId, [FromBody] PrefillCacheStatusRequest request)
    {
        var ownershipResult = ValidateSessionOwnership(sessionId);
        if (ownershipResult != null)
        {
            return ownershipResult;
        }

        if (request.AppIds == null || request.AppIds.Count == 0)
        {
            return BadRequest(ApiResponse.Error("No app IDs provided"));
        }

        var status = await _daemonService.GetCacheStatusAsync(sessionId, request.AppIds);
        var upToDate = status.Apps.Where(a => a.IsUpToDate).Select(a => a.AppId).ToList();
        var outdated = status.Apps.Where(a => !a.IsUpToDate).Select(a => a.AppId).ToList();

        return Ok(new PrefillCacheStatusResponse
        {
            UpToDateAppIds = upToDate,
            OutdatedAppIds = outdated,
            Message = status.Message
        });
    }

    /// <summary>
    /// Stores which apps the next prefill should cover.
    /// </summary>
    /// <remarks>
    /// An empty list is the picker's "clear selection" action and is accepted; only a missing
    /// list is rejected, so clearing a selection never has to be expressed as a delete.
    /// </remarks>
    [HttpPost("sessions/{sessionId}/selected-apps")]
    [ProducesResponseType(typeof(SelectedAppsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult> SetSelectedAppsAsync(string sessionId, [FromBody] SetSelectedAppsRequest request)
    {
        var ownershipResult = ValidateSessionOwnership(sessionId);
        if (ownershipResult != null)
        {
            return ownershipResult;
        }

        if (request.AppIds == null)
        {
            return BadRequest(ApiResponse.Required("AppIds"));
        }

        await _daemonService.SetSelectedAppsAsync(sessionId, request.AppIds);
        return Ok(new SelectedAppsResponse { Message = "Apps selected", Count = request.AppIds.Count });
    }

    /// <summary>
    /// Runs a prefill for the session's current selection and waits for it to finish.
    /// </summary>
    /// <remarks>
    /// The caller gets the real outcome rather than a job id. A prefill that is already in
    /// flight for this session comes back as 409 Conflict instead of starting a second one.
    /// </remarks>
    [HttpPost("sessions/{sessionId}/prefill")]
    [ProducesResponseType(typeof(PrefillResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<PrefillResult>> StartPrefillAsync(string sessionId, [FromBody] StartPrefillRequest? request)
    {
        var ownershipResult = ValidateSessionOwnership(sessionId);
        if (ownershipResult != null)
        {
            return ownershipResult;
        }

        // Enforce thread limit for guest users
        var userSession = HttpContext.GetUserSession();
        if (userSession != null && request?.MaxConcurrency != null)
        {
            var effectiveLimit = ResolveThreadLimit(userSession);
            if (effectiveLimit.HasValue && request.MaxConcurrency > effectiveLimit.Value)
            {
                request.MaxConcurrency = effectiveLimit.Value;
            }
        }

        _logger.LogInformation("Starting {Platform} prefill for session {SessionId}", _platformName, sessionId);

        try
        {
            var result = await _daemonService.PrefillAsync(
                sessionId,
                all: request?.All ?? false,
                recent: request?.Recent ?? false,
                recentlyPurchased: request?.RecentlyPurchased ?? false,
                top: request?.Top,
                force: request?.Force ?? false,
                operatingSystems: request?.OperatingSystems,
                maxConcurrency: request?.MaxConcurrency);

            return Ok(result);
        }
        catch (PrefillAlreadyRunningException ex)
        {
            // A prefill is already in flight for this session (local start guard OR the daemon's
            // "already in progress" rejection). Surface as 409 Conflict, not a 500. ex.Message is
            // developer-authored on this exception type (never raw framework/internal text), so
            // it is safe to surface via the canonical ApiResponse factory.
            _logger.LogInformation("{Platform} prefill rejected for session {SessionId}: {Message}",
                _platformName, sessionId, ex.Message);
            throw new ConflictException(ex.Message);
        }
    }

    /// <summary>
    /// Builds the daemon service status shown on a platform's management card.
    /// </summary>
    /// <remarks>
    /// This is shared by every daemon controller but routed only by the platforms whose card
    /// polls it over REST, so it is not an action here; a controller that needs the route
    /// declares its own <c>status</c> action and calls this one.
    /// </remarks>
    protected ActionResult GetStatus()
    {
        var sessions = _daemonService.GetAllSessions().ToList();

        return Ok(new
        {
            dockerAvailable = _daemonService.IsDockerAvailable,
            activeSessions = sessions.Count,
            // Authenticated session count lets login-required daemon cards (e.g. Xbox) reflect a
            // logged-in state and swap/hide their login control, rather than always showing "Login".
            authenticatedSessions = sessions.Count(s => s.AuthState == DaemonAuthState.Authenticated),
            maxSessionsPerUser = 1,
            sessionTimeoutMinutes = 120
        });
    }

    /// <summary>
    /// Validates that the current user owns the specified session.
    /// Returns an error ActionResult if validation fails, or null if the session is valid and owned.
    /// </summary>
    protected ActionResult? ValidateSessionOwnership(string sessionId)
    {
        var currentSessionId = HttpContext.GetRequiredSessionId();

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound(ApiResponse.NotFound("Session"));
        }

        if (session.UserId != currentSessionId)
        {
            return Forbid();
        }

        return null;
    }

}
