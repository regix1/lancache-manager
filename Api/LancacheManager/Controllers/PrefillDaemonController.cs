using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// REST API endpoints for Steam Prefill daemon sessions.
/// Uses secure encrypted credential exchange for authentication.
///
/// Authorization:
/// - [RequireAuth] = Authenticated users only (admin endpoints)
/// - [RequirePrefillAccess] = Authenticated users OR guests with prefill permission
/// </summary>
[ApiController]
[Route("api/prefill-daemon")]
public class PrefillDaemonController : ControllerBase
{
    private readonly SteamPrefillDaemonService _daemonService;
    private readonly ILogger<PrefillDaemonController> _logger;

    public PrefillDaemonController(
        SteamPrefillDaemonService daemonService,
        ILogger<PrefillDaemonController> logger)
    {
        _daemonService = daemonService;
        _logger = logger;
    }

    /// <summary>
    /// Gets all active daemon sessions (admin only)
    /// </summary>
    [HttpGet("sessions")]
    [RequireAuth]
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
    [RequirePrefillAccess]
    public ActionResult<IEnumerable<DaemonSessionDto>> GetMySessions()
    {
        var deviceId = GetDeviceId()!; // Attribute guarantees this is not null

        var sessions = _daemonService.GetUserSessions(deviceId)
            .Select(DaemonSessionDto.FromSession);

        return Ok(sessions);
    }

    /// <summary>
    /// Gets a specific session
    /// </summary>
    [HttpGet("sessions/{sessionId}")]
    [RequirePrefillAccess]
    public ActionResult<DaemonSessionDto> GetSession(string sessionId)
    {
        var deviceId = GetDeviceId()!;

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound();
        }

        if (session.UserId != deviceId)
        {
            return Forbid();
        }

        return Ok(DaemonSessionDto.FromSession(session));
    }

    /// <summary>
    /// Creates a new daemon session
    /// </summary>
    [HttpPost("sessions")]
    [RequirePrefillAccess]
    public async Task<ActionResult<DaemonSessionDto>> CreateSession()
    {
        var deviceId = GetDeviceId()!;

        try
        {
            _logger.LogInformation("Creating daemon session for device {DeviceId}", deviceId);
            var session = await _daemonService.CreateSessionAsync(deviceId);
            return CreatedAtAction(nameof(GetSession), new { sessionId = session.Id }, DaemonSessionDto.FromSession(session));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse.Error(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create daemon session for device {DeviceId}", deviceId);
            return StatusCode(500, ApiResponse.InternalError("creating daemon session"));
        }
    }

    /// <summary>
    /// Gets the daemon status for a session
    /// </summary>
    [HttpGet("sessions/{sessionId}/status")]
    [RequirePrefillAccess]
    public async Task<ActionResult<DaemonStatus>> GetSessionStatus(string sessionId)
    {
        var deviceId = GetDeviceId()!;

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound();
        }

        if (session.UserId != deviceId)
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
    [RequirePrefillAccess]
    public async Task<ActionResult<CredentialChallenge>> StartLogin(string sessionId)
    {
        var deviceId = GetDeviceId()!;

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound();
        }

        if (session.UserId != deviceId)
        {
            return Forbid();
        }

        try
        {
            _logger.LogInformation("Starting login for session {SessionId}", sessionId);
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting login for session {SessionId}", sessionId);
            return StatusCode(500, ApiResponse.Error(ex.Message));
        }
    }

    /// <summary>
    /// Provides an encrypted credential in response to a challenge
    /// </summary>
    [HttpPost("sessions/{sessionId}/credential")]
    [RequirePrefillAccess]
    public async Task<ActionResult> ProvideCredential(string sessionId, [FromBody] ProvideCredentialRequest request)
    {
        var deviceId = GetDeviceId()!;

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound();
        }

        if (session.UserId != deviceId)
        {
            return Forbid();
        }

        if (request.Challenge == null || string.IsNullOrEmpty(request.Credential))
        {
            return BadRequest(ApiResponse.Required("Challenge and credential"));
        }

        try
        {
            _logger.LogInformation("Providing {CredentialType} credential for session {SessionId}",
                request.Challenge.CredentialType, sessionId);

            await _daemonService.ProvideCredentialAsync(sessionId, request.Challenge, request.Credential);

            return Ok(ApiResponse.Message("Credential sent"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error providing credential for session {SessionId}", sessionId);
            return StatusCode(500, ApiResponse.Error(ex.Message));
        }
    }

    /// <summary>
    /// Waits for the next credential challenge (polling endpoint)
    /// </summary>
    [HttpGet("sessions/{sessionId}/challenge")]
    [RequirePrefillAccess]
    public async Task<ActionResult<CredentialChallenge>> WaitForChallenge(string sessionId, [FromQuery] int timeoutSeconds = 30)
    {
        var deviceId = GetDeviceId()!;

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound();
        }

        if (session.UserId != deviceId)
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
    [RequirePrefillAccess]
    public async Task<ActionResult<List<OwnedGame>>> GetOwnedGames(string sessionId)
    {
        var deviceId = GetDeviceId()!;

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound();
        }

        if (session.UserId != deviceId)
        {
            return Forbid();
        }

        try
        {
            var games = await _daemonService.GetOwnedGamesAsync(sessionId);
            return Ok(games);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse.Error(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting owned games for session {SessionId}", sessionId);
            return StatusCode(500, ApiResponse.Error(ex.Message));
        }
    }

    /// <summary>
    /// Sets selected apps for prefill
    /// </summary>
    [HttpPost("sessions/{sessionId}/selected-apps")]
    [RequirePrefillAccess]
    public async Task<ActionResult> SetSelectedApps(string sessionId, [FromBody] SetSelectedAppsRequest request)
    {
        var deviceId = GetDeviceId()!;

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound();
        }

        if (session.UserId != deviceId)
        {
            return Forbid();
        }

        if (request.AppIds == null || request.AppIds.Count == 0)
        {
            return BadRequest(ApiResponse.Required("AppIds"));
        }

        try
        {
            await _daemonService.SetSelectedAppsAsync(sessionId, request.AppIds);
            return Ok(new { message = "Apps selected", count = request.AppIds.Count });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse.Error(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting selected apps for session {SessionId}", sessionId);
            return StatusCode(500, ApiResponse.Error(ex.Message));
        }
    }

    /// <summary>
    /// Starts a prefill operation
    /// </summary>
    [HttpPost("sessions/{sessionId}/prefill")]
    [RequirePrefillAccess]
    public async Task<ActionResult<PrefillResult>> StartPrefill(string sessionId, [FromBody] StartPrefillRequest? request)
    {
        var deviceId = GetDeviceId()!;

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound();
        }

        if (session.UserId != deviceId)
        {
            return Forbid();
        }

        try
        {
            _logger.LogInformation("Starting prefill for session {SessionId}", sessionId);

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
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse.Error(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during prefill for session {SessionId}", sessionId);
            return StatusCode(500, ApiResponse.Error(ex.Message));
        }
    }

    /// <summary>
    /// Terminates a daemon session
    /// </summary>
    [HttpDelete("sessions/{sessionId}")]
    [RequirePrefillAccess]
    public async Task<ActionResult> TerminateSession(string sessionId)
    {
        var deviceId = GetDeviceId()!;

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound();
        }

        if (session.UserId != deviceId)
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
    [RequireGuestSession]
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

    private string? GetDeviceId()
    {
        return Request.Headers["X-Device-Id"].FirstOrDefault();
    }

    #region Request DTOs

    public class ProvideCredentialRequest
    {
        public CredentialChallenge? Challenge { get; set; }
        public string? Credential { get; set; }
    }

    public class SetSelectedAppsRequest
    {
        public List<uint>? AppIds { get; set; }
    }

    public class StartPrefillRequest
    {
        public bool All { get; set; }
        public bool Recent { get; set; }
        public bool RecentlyPurchased { get; set; }
        public int? Top { get; set; }
        public bool Force { get; set; }
        public List<string>? OperatingSystems { get; set; }
        public int? MaxConcurrency { get; set; }
    }

    #endregion
}
