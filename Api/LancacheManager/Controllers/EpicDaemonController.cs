using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// REST API endpoints for Epic daemon sessions.
/// Uses secure encrypted credential exchange for authentication.
///
/// Authorization:
/// </summary>
[ApiController]
[Route("api/epic-daemon")]
public class EpicDaemonController : ControllerBase
{
    private readonly EpicPrefillDaemonService _daemonService;
    private readonly ILogger<EpicDaemonController> _logger;
    private readonly StateService _stateService;
    private readonly UserPreferencesService _userPreferencesService;

    public EpicDaemonController(
        EpicPrefillDaemonService daemonService,
        ILogger<EpicDaemonController> logger,
        StateService stateService,
        UserPreferencesService userPreferencesService)
    {
        _daemonService = daemonService;
        _logger = logger;
        _stateService = stateService;
        _userPreferencesService = userPreferencesService;
    }

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
        var sessionId = GetSession()?.Id.ToString() ?? string.Empty;

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
        var currentSessionId = GetSession()?.Id.ToString() ?? string.Empty;

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound();
        }

        if (session.UserId != currentSessionId)
        {
            return Forbid();
        }

        return Ok(DaemonSessionDto.FromSession(session));
    }

    /// <summary>
    /// Creates a new daemon session
    /// </summary>
    [HttpPost("sessions")]
    public async Task<ActionResult<DaemonSessionDto>> CreateSession()
    {
        var sessionId = GetSessionId();

        _logger.LogInformation("Creating Epic daemon session for session {SessionId}", sessionId);
        var session = await _daemonService.CreateSessionAsync(sessionId);
        return CreatedAtAction(nameof(GetSession), new { sessionId = session.Id }, DaemonSessionDto.FromSession(session));
    }

    /// <summary>
    /// Gets the daemon status for a session
    /// </summary>
    [HttpGet("sessions/{sessionId}/status")]
    public async Task<ActionResult<DaemonStatus>> GetSessionStatus(string sessionId)
    {
        var currentSessionId = GetSession()?.Id.ToString() ?? string.Empty;

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound();
        }

        if (session.UserId != currentSessionId)
        {
            return Forbid();
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
        var currentSessionId = GetSession()?.Id.ToString() ?? string.Empty;

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound();
        }

        if (session.UserId != currentSessionId)
        {
            return Forbid();
        }

        _logger.LogInformation("Starting Epic login for session {SessionId}", sessionId);
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
        var currentSessionId = GetSession()?.Id.ToString() ?? string.Empty;

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound();
        }

        if (session.UserId != currentSessionId)
        {
            return Forbid();
        }

        if (request.Challenge == null || string.IsNullOrEmpty(request.Credential))
        {
            return BadRequest(ApiResponse.Required("Challenge and credential"));
        }

        _logger.LogInformation("Providing {CredentialType} credential for Epic session {SessionId}",
            request.Challenge.CredentialType, sessionId);

        await _daemonService.ProvideCredentialAsync(sessionId, request.Challenge, request.Credential);

        return Ok(ApiResponse.Message("Credential sent"));
    }

    /// <summary>
    /// Waits for the next credential challenge (polling endpoint)
    /// </summary>
    [HttpGet("sessions/{sessionId}/challenge")]
    public async Task<ActionResult<CredentialChallenge>> WaitForChallenge(string sessionId, [FromQuery] int timeoutSeconds = 30)
    {
        var currentSessionId = GetSession()?.Id.ToString() ?? string.Empty;

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound();
        }

        if (session.UserId != currentSessionId)
        {
            return Forbid();
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
        var currentSessionId = GetSession()?.Id.ToString() ?? string.Empty;

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound();
        }

        if (session.UserId != currentSessionId)
        {
            return Forbid();
        }

        var games = await _daemonService.GetOwnedGamesAsync(sessionId);
        return Ok(games);
    }

    /// <summary>
    /// Checks cache status for cached apps (verifies Epic build versions).
    /// </summary>
    [HttpPost("sessions/{sessionId}/cache-status")]
    public async Task<ActionResult<PrefillCacheStatusResponse>> GetCacheStatus(string sessionId, [FromBody] PrefillCacheStatusRequest request)
    {
        var currentSessionId = GetSession()?.Id.ToString() ?? string.Empty;

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound();
        }

        if (session.UserId != currentSessionId)
        {
            return Forbid();
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
        var currentSessionId = GetSession()?.Id.ToString() ?? string.Empty;

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound();
        }

        if (session.UserId != currentSessionId)
        {
            return Forbid();
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
        var currentSessionId = GetSession()?.Id.ToString() ?? string.Empty;

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound();
        }

        if (session.UserId != currentSessionId)
        {
            return Forbid();
        }

        // Enforce thread limit for guest users
        var userSession = GetSession();
        if (userSession != null && request?.MaxConcurrency != null)
        {
            var effectiveLimit = ResolveEffectiveThreadLimit(userSession);
            if (effectiveLimit.HasValue && request.MaxConcurrency > effectiveLimit.Value)
            {
                request.MaxConcurrency = effectiveLimit.Value;
            }
        }

        _logger.LogInformation("Starting Epic prefill for session {SessionId}", sessionId);

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
        var currentSessionId = GetSession()?.Id.ToString() ?? string.Empty;

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound();
        }

        if (session.UserId != currentSessionId)
        {
            return Forbid();
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

    private UserSession? GetSession() => HttpContext.Items["Session"] as UserSession;
    private string GetSessionId() => GetSession()?.Id.ToString() ?? "unknown";

    private int? ResolveEffectiveThreadLimit(UserSession session)
    {
        if (session.SessionType == "admin") return null;
        var prefs = _userPreferencesService.GetPreferences(session.Id);
        return prefs?.MaxThreadCount ?? _stateService.GetDefaultGuestMaxThreadCount();
    }
}
