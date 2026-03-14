using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Middleware;
using LancacheManager.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers.Base;

/// <summary>
/// Abstract base controller for daemon session endpoints.
/// Provides all 14 shared REST endpoints for Steam and Epic daemon controllers.
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
    protected abstract int? ResolveEffectiveThreadLimit(UserSession session);

    /// <summary>
    /// Gets all active daemon sessions (admin only)
    /// </summary>
    [HttpGet("sessions")]
    public ActionResult<IEnumerable<DaemonSessionDto>> GetAllSessions()
    {
        var sessions = _daemonService.GetAllSessions()
            .Select(DaemonSessionDto.FromSession);

        return Ok(sessions);
    }

    /// <summary>
    /// Gets sessions for the current user
    /// </summary>
    [HttpGet("sessions/mine")]
    public ActionResult<IEnumerable<DaemonSessionDto>> GetMySessions()
    {
        var sessionId = GetUserSession()?.Id.ToString() ?? string.Empty;

        var sessions = _daemonService.GetUserSessions(sessionId)
            .Select(DaemonSessionDto.FromSession);

        return Ok(sessions);
    }

    /// <summary>
    /// Gets a specific session
    /// </summary>
    [HttpGet("sessions/{sessionId}")]
    public ActionResult<DaemonSessionDto> GetSession(string sessionId)
    {
        var ownershipResult = ValidateSessionOwnership(sessionId);
        if (ownershipResult != null)
        {
            return ownershipResult;
        }

        var session = _daemonService.GetSession(sessionId)!;
        return Ok(DaemonSessionDto.FromSession(session));
    }

    /// <summary>
    /// Creates a new daemon session
    /// </summary>
    [HttpPost("sessions")]
    public async Task<ActionResult<DaemonSessionDto>> CreateSession()
    {
        var sessionId = GetSessionId();

        _logger.LogInformation("Creating {Platform} daemon session for session {SessionId}", _platformName, sessionId);
        var session = await _daemonService.CreateSessionAsync(sessionId);
        return CreatedAtAction(nameof(GetSession), new { sessionId = session.Id }, DaemonSessionDto.FromSession(session));
    }

    /// <summary>
    /// Gets the daemon status for a session
    /// </summary>
    [HttpGet("sessions/{sessionId}/status")]
    public async Task<ActionResult<DaemonStatus>> GetSessionStatus(string sessionId)
    {
        var ownershipResult = ValidateSessionOwnership(sessionId);
        if (ownershipResult != null)
        {
            return ownershipResult;
        }

        var status = await _daemonService.GetSessionStatusAsync(sessionId);
        if (status == null)
        {
            return Ok(new DaemonStatus { Status = "unknown" });
        }

        return Ok(status);
    }

    /// <summary>
    /// Starts the login process for a session.
    /// Returns a credential challenge if credentials are needed.
    /// </summary>
    [HttpPost("sessions/{sessionId}/login")]
    public async Task<ActionResult<CredentialChallenge>> StartLogin(string sessionId)
    {
        var ownershipResult = ValidateSessionOwnership(sessionId);
        if (ownershipResult != null)
        {
            return ownershipResult;
        }

        _logger.LogInformation("Starting {Platform} login for session {SessionId}", _platformName, sessionId);
        var challenge = await _daemonService.StartLoginAsync(sessionId, TimeSpan.FromSeconds(30));

        if (challenge == null)
        {
            // No challenge means either already logged in or timeout
            var status = await _daemonService.GetSessionStatusAsync(sessionId);
            if (status?.Status == "logged-in")
            {
                return Ok(new { message = "Already logged in", status = "logged-in" });
            }
            return BadRequest(ApiResponse.Error("Login timeout - daemon may not be ready"));
        }

        return Ok(challenge);
    }

    /// <summary>
    /// Provides an encrypted credential in response to a challenge
    /// </summary>
    [HttpPost("sessions/{sessionId}/credential")]
    public async Task<ActionResult> ProvideCredential(string sessionId, [FromBody] ProvideCredentialRequest request)
    {
        var ownershipResult = ValidateSessionOwnership(sessionId);
        if (ownershipResult != null)
        {
            return ownershipResult;
        }

        if (request.Challenge == null || string.IsNullOrEmpty(request.Credential))
        {
            return BadRequest(ApiResponse.Required("Challenge and credential"));
        }

        _logger.LogInformation("Providing {CredentialType} credential for {Platform} session {SessionId}",
            request.Challenge.CredentialType, _platformName, sessionId);

        await _daemonService.ProvideCredentialAsync(sessionId, request.Challenge, request.Credential);

        return Ok(ApiResponse.Message("Credential sent"));
    }

    /// <summary>
    /// Waits for the next credential challenge (polling endpoint)
    /// </summary>
    [HttpGet("sessions/{sessionId}/challenge")]
    public async Task<ActionResult<CredentialChallenge>> WaitForChallenge(string sessionId, [FromQuery] int timeoutSeconds = 30)
    {
        var ownershipResult = ValidateSessionOwnership(sessionId);
        if (ownershipResult != null)
        {
            return ownershipResult;
        }

        var challenge = await _daemonService.WaitForChallengeAsync(sessionId, TimeSpan.FromSeconds(timeoutSeconds));

        if (challenge == null)
        {
            // Check if logged in
            var status = await _daemonService.GetSessionStatusAsync(sessionId);
            if (status?.Status == "logged-in")
            {
                return Ok(new { status = "logged-in" });
            }
            return NoContent(); // No challenge, not logged in yet
        }

        return Ok(challenge);
    }

    /// <summary>
    /// Gets owned games for a logged-in session
    /// </summary>
    [HttpGet("sessions/{sessionId}/games")]
    public async Task<ActionResult<List<OwnedGame>>> GetOwnedGames(string sessionId)
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
    /// Checks cache status for cached apps.
    /// </summary>
    [HttpPost("sessions/{sessionId}/cache-status")]
    public async Task<ActionResult<PrefillCacheStatusResponse>> GetCacheStatus(string sessionId, [FromBody] PrefillCacheStatusRequest request)
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
    /// Sets selected apps for prefill
    /// </summary>
    [HttpPost("sessions/{sessionId}/selected-apps")]
    public async Task<ActionResult> SetSelectedApps(string sessionId, [FromBody] SetSelectedAppsRequest request)
    {
        var ownershipResult = ValidateSessionOwnership(sessionId);
        if (ownershipResult != null)
        {
            return ownershipResult;
        }

        if (request.AppIds == null || request.AppIds.Count == 0)
        {
            return BadRequest(ApiResponse.Required("AppIds"));
        }

        await _daemonService.SetSelectedAppsAsync(sessionId, request.AppIds);
        return Ok(new { message = "Apps selected", count = request.AppIds.Count });
    }

    /// <summary>
    /// Starts a prefill operation
    /// </summary>
    [HttpPost("sessions/{sessionId}/prefill")]
    public async Task<ActionResult<PrefillResult>> StartPrefill(string sessionId, [FromBody] StartPrefillRequest? request)
    {
        var ownershipResult = ValidateSessionOwnership(sessionId);
        if (ownershipResult != null)
        {
            return ownershipResult;
        }

        // Enforce thread limit for guest users
        var userSession = GetUserSession();
        if (userSession != null && request?.MaxConcurrency != null)
        {
            var effectiveLimit = ResolveEffectiveThreadLimit(userSession);
            if (effectiveLimit.HasValue && request.MaxConcurrency > effectiveLimit.Value)
            {
                request.MaxConcurrency = effectiveLimit.Value;
            }
        }

        _logger.LogInformation("Starting {Platform} prefill for session {SessionId}", _platformName, sessionId);

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

    /// <summary>
    /// Terminates a daemon session
    /// </summary>
    [HttpDelete("sessions/{sessionId}")]
    public async Task<ActionResult> TerminateSession(string sessionId)
    {
        var ownershipResult = ValidateSessionOwnership(sessionId);
        if (ownershipResult != null)
        {
            return ownershipResult;
        }

        await _daemonService.TerminateSessionAsync(sessionId, "Terminated via API");
        return NoContent();
    }

    /// <summary>
    /// Gets daemon service status (public endpoint)
    /// </summary>
    [HttpGet("status")]
    public ActionResult GetStatus()
    {
        var sessions = _daemonService.GetAllSessions().ToList();

        return Ok(new
        {
            dockerAvailable = _daemonService.IsDockerAvailable,
            activeSessions = sessions.Count,
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
        var currentSessionId = GetUserSession()?.Id.ToString() ?? string.Empty;

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound();
        }

        if (session.UserId != currentSessionId)
        {
            return Forbid();
        }

        return null;
    }

    protected UserSession? GetUserSession() => HttpContext.GetUserSession();
    protected string GetSessionId() => GetUserSession()?.Id.ToString() ?? "unknown";
}
