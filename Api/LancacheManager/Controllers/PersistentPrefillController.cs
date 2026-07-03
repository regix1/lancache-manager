using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// Admin-only management of persistent (long-lived) prefill daemon sessions. These sessions never
/// persist auth: each start runs a fresh, unauthenticated container that the admin logs into
/// interactively via the UI. The reaper never tears them down and instead flags
/// <see cref="DaemonSession.NeedsRelogin"/> once past their expiry.
/// Mirrors <see cref="ScheduledPrefillConfigController"/> for base route/auth style and reuses the
/// exact daemon-per-platform resolution + system user id derivation from <c>ScheduledPrefillService</c>.
/// </summary>
[ApiController]
[Route("api/system/prefill/persistent")]
[Authorize(Policy = "AdminOnly")]
public class PersistentPrefillController : ControllerBase
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IStateService _stateService;
    private readonly PrefillCacheService _cacheService;
    private readonly ILogger<PersistentPrefillController> _logger;

    /// <summary>
    /// Deterministic pseudo-user Guid that owns every persistent session, derived identically to
    /// <c>ScheduledPrefillConstants.DeriveSystemUserId()</c> (SHA-256 of
    /// <see cref="ScheduledPrefillConstants.SystemUserId"/>) so both code paths agree on identity.
    /// </summary>
    private readonly Guid _systemUserId = ScheduledPrefillConstants.DeriveSystemUserId();

    public PersistentPrefillController(
        IServiceProvider serviceProvider,
        IStateService stateService,
        PrefillCacheService cacheService,
        ILogger<PersistentPrefillController> logger)
    {
        _serviceProvider = serviceProvider;
        _stateService = stateService;
        _cacheService = cacheService;
        _logger = logger;
    }

    /// <summary>
    /// Starts a persistent admin-owned session for the given platform.
    /// </summary>
    [HttpPost("start")]
    public async Task<ActionResult<DaemonSessionDto>> StartAsync([FromBody] StartPersistentSessionRequest request)
    {
        var daemon = PrefillDaemonServiceBase.ResolveDaemon(_serviceProvider, request.Service);
        if (daemon is null)
        {
            return BadRequest($"No daemon registered for service '{request.Service}'");
        }

        DaemonSession session = await daemon.StartPersistentSessionAsync(request.Service, _systemUserId);
        return Ok(DaemonSessionDto.FromSession(session));
    }

    /// <summary>
    /// Stops a persistent session. Auth is not persisted, so a later restart requires a fresh login.
    /// </summary>
    [HttpPost("stop")]
    public async Task<ActionResult> StopAsync([FromBody] StopPersistentSessionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return BadRequest("sessionId is required");
        }

        foreach (var daemon in PrefillDaemonServiceBase.ResolveAllDaemons(_serviceProvider))
        {
            if (daemon.GetSession(request.SessionId) is not null)
            {
                await daemon.StopPersistentSessionAsync(request.SessionId, terminatedBy: "admin");
                return Ok();
            }
        }

        return NotFound($"No persistent session found with id {request.SessionId}");
    }

    /// <summary>
    /// Lists every persistent session across all daemons.
    /// </summary>
    [HttpGet("list")]
    public async Task<ActionResult<List<PersistentPrefillSessionDto>>> ListAsync(CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var results = new List<PersistentPrefillSessionDto>();

        foreach (var daemon in PrefillDaemonServiceBase.ResolveAllDaemons(_serviceProvider))
        {
            foreach (DaemonSession session in daemon.GetAllSessions())
            {
                if (!session.IsPersistent)
                {
                    continue;
                }

                var isRunning = session.Status == DaemonSessionStatus.Active;

                // Query the daemon's REAL token expiry + live login state only for running sessions.
                // The daemon status is the UI's source of truth for authentication (not NeedsRelogin),
                // because persistent containers always start unauthenticated and are logged in
                // interactively. Resilient: a single failing/slow status call must never sink the whole
                // list, so it is per-session try/caught (auth defaults false when unavailable).
                DateTimeOffset? daemonAuthExpiresAtUtc = null;
                bool isAuthenticated = false;
                if (isRunning)
                {
                    try
                    {
                        var status = await daemon.GetSessionStatusAsync(session.Id, cancellationToken);
                        daemonAuthExpiresAtUtc = status?.AuthExpiryUtc;
                        isAuthenticated = status?.Status == "logged-in";
                    }
                    catch
                    {
                        daemonAuthExpiresAtUtc = null;
                        isAuthenticated = false;
                    }
                }

                // The honest re-login date is the EARLIER of the manager's validity window and the
                // daemon's real token expiry, so the UI never promises a window longer than the token.
                var effectiveRelogin = ComputeEffectiveRelogin(session.ExpiresAt, daemonAuthExpiresAtUtc);
                var remaining = (effectiveRelogin - nowUtc).TotalSeconds;
                long remainingSeconds = remaining > 0 ? (long)remaining : 0L;

                results.Add(new PersistentPrefillSessionDto
                {
                    SessionId = session.Id,
                    Service = ParsePlatform(session.Platform),
                    IsRunning = isRunning,
                    IsAuthenticated = isAuthenticated,
                    AuthExpiresAtUtc = effectiveRelogin,
                    AuthTimeRemainingSeconds = remainingSeconds,
                    NeedsRelogin = session.NeedsRelogin,
                    DaemonAuthExpiresAtUtc = daemonAuthExpiresAtUtc,
                    IsPrefilling = session.IsPrefilling,
                    TotalBytesTransferred = session.TotalBytesTransferred,
                    CurrentAppName = session.CurrentAppName
                });
            }
        }

        return Ok(results);
    }

    /// <summary>
    /// Lists the owned games (and up-to-date cached app ids) for the RUNNING persistent session of a
    /// platform. This is the AdminOnly analogue of the user-scoped
    /// <c>GET {service}/sessions/{id}/games</c> route: that route enforces
    /// <c>ValidateSessionOwnership</c> (session.UserId == caller), which always 403s for persistent
    /// system-owned sessions whose owner is the derived system user, not the admin's session id.
    /// Bypassing ownership is safe here because the endpoint is <c>[Authorize(Policy = "AdminOnly")]</c>
    /// and is hard-restricted to sessions whose <see cref="DaemonSession.IsPersistent"/> is true.
    /// Reuses the exact same daemon method the user route calls
    /// (<see cref="PrefillDaemonServiceBase.GetOwnedGamesAsync(string, CancellationToken)"/>) so there
    /// is no game-list duplication.
    /// </summary>
    [HttpGet("games")]
    public async Task<ActionResult<PersistentPrefillGamesDto>> GetGamesAsync(
        [FromQuery] PrefillPlatform service,
        CancellationToken cancellationToken)
    {
        var daemon = PrefillDaemonServiceBase.ResolveDaemon(_serviceProvider, service);
        if (daemon is null)
        {
            return BadRequest($"No daemon registered for service '{service}'");
        }

        var session = daemon.GetActivePersistentSession();
        if (session is null)
        {
            return NotFound($"No running persistent session for service '{service}'");
        }

        // Defense-in-depth: never operate on a non-persistent session here.
        if (!session.IsPersistent)
        {
            return Forbid();
        }

        var games = await daemon.GetOwnedGamesAsync(session.Id, cancellationToken);

        var ownedAppIds = games
            .Select(g => g.AppId.ToString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Game picker cache badges use manager DB only. Do not call the daemon (set-selected-apps /
        // get-selected-apps-status / check-cache-status): that is slow for large libraries, mutates
        // selection, and fails with 500 if the socket drops while the session is stopping.
        var cachedAppIds = await ResolveCachedAppIdsForGamePickerAsync(ownedAppIds, cancellationToken);

        return Ok(new PersistentPrefillGamesDto
        {
            Games = games,
            CachedAppIds = cachedAppIds
        });
    }

    /// <summary>
    /// Sets the daemon's selected app list for the RUNNING persistent session (same as guest
    /// <c>POST …/selected-apps</c>).
    /// </summary>
    [HttpPost("selected-apps")]
    public async Task<ActionResult> SetSelectedAppsAsync(
        [FromBody] PersistentSelectedAppsRequest request,
        CancellationToken cancellationToken)
    {
        var (daemon, session, error) = ResolveRunningPersistentSession(request.Service);
        if (error is not null)
        {
            return error;
        }

        await daemon!.SetSelectedAppsAsync(session!.Id, request.AppIds, cancellationToken);
        return Ok();
    }

    /// <summary>
    /// Starts a prefill/download on the RUNNING persistent session (same as guest
    /// <c>POST …/prefill</c>).
    /// </summary>
    [HttpPost("prefill")]
    public async Task<ActionResult<PrefillResult>> StartPrefillAsync(
        [FromBody] PersistentStartPrefillRequest request,
        CancellationToken cancellationToken)
    {
        var (daemon, session, error) = ResolveRunningPersistentSession(request.Service);
        if (error is not null)
        {
            return error;
        }

        _logger.LogInformation(
            "Starting persistent {Service} prefill on session {SessionId} (force={Force}, selectedApps={Count})",
            request.Service,
            session!.Id,
            request.Force,
            request.AppIds?.Count ?? 0);

        if (request.AppIds is { Count: > 0 })
        {
            await daemon!.SetSelectedAppsAsync(session.Id, request.AppIds, cancellationToken);
        }

        try
        {
            var result = await daemon!.PrefillAsync(
                session.Id,
                all: request.All,
                recent: request.Recent,
                recentlyPurchased: request.RecentlyPurchased,
                top: request.Top,
                force: request.Force,
                operatingSystems: request.OperatingSystems,
                maxConcurrency: request.MaxConcurrency,
                cancellationToken: cancellationToken);

            return Ok(result);
        }
        catch (PrefillAlreadyRunningException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Cancels an in-flight prefill on the RUNNING persistent session.
    /// </summary>
    [HttpPost("cancel-prefill")]
    public async Task<ActionResult> CancelPrefillAsync(
        [FromBody] PersistentServiceRequest request,
        CancellationToken cancellationToken)
    {
        var (daemon, session, error) = ResolveRunningPersistentSession(request.Service);
        if (error is not null)
        {
            return error;
        }

        await daemon!.CancelPrefillAsync(session!.Id, cancellationToken);
        return Ok();
    }

    /// <summary>
    /// Starts (or resumes) the interactive login flow for the RUNNING persistent session of a
    /// platform and returns the initial credential challenge. AdminOnly analogue of the user route
    /// <c>POST {service}/sessions/{id}/login</c>, which enforces <c>ValidateSessionOwnership</c> and
    /// always 403s for system-owned persistent sessions. Safe to bypass ownership here because this
    /// controller is <c>[Authorize(Policy = "AdminOnly")]</c> and hard-restricted to persistent
    /// sessions. Delegates to <see cref="PrefillDaemonServiceBase.StartLoginAsync(string, TimeSpan?, CancellationToken)"/>.
    /// </summary>
    [HttpPost("login")]
    public async Task<ActionResult<CredentialChallenge>> StartLoginAsync(
        [FromBody] PersistentLoginRequest request,
        CancellationToken cancellationToken)
    {
        var (daemon, session, error) = ResolveRunningPersistentSession(request.Service);
        if (error is not null)
        {
            return error;
        }

        var challenge = await daemon!.StartLoginAsync(session!.Id, TimeSpan.FromSeconds(30), cancellationToken);

        if (challenge == null)
        {
            // No challenge means either already logged in, a fail-fast daemon failure, or a genuine timeout.
            var status = await daemon.GetSessionStatusAsync(session.Id, cancellationToken);
            if (status?.Status == "logged-in")
            {
                return Ok(new { message = "Already logged in", status = "logged-in" });
            }

            if (!string.IsNullOrEmpty(session.LastLoginFailureMessage))
            {
                return BadRequest(ApiResponse.Error(session.LastLoginFailureMessage));
            }

            return BadRequest(ApiResponse.Error("Login timeout - daemon may not be ready"));
        }

        return Ok(challenge);
    }

    /// <summary>
    /// Provides an encrypted credential in response to a challenge for the RUNNING persistent session.
    /// AdminOnly analogue of <c>POST {service}/sessions/{id}/credential</c>. Reuses the user route's
    /// <see cref="ProvideCredentialRequest"/> payload (Challenge + Credential) and delegates to
    /// <see cref="PrefillDaemonServiceBase.ProvideCredentialAsync(string, CredentialChallenge, string, CancellationToken)"/>.
    /// </summary>
    [HttpPost("credential")]
    public async Task<ActionResult> ProvideCredentialAsync(
        [FromBody] PersistentProvideCredentialRequest request,
        CancellationToken cancellationToken)
    {
        var (daemon, session, error) = ResolveRunningPersistentSession(request.Service);
        if (error is not null)
        {
            return error;
        }

        if (request.Challenge == null || string.IsNullOrEmpty(request.Credential))
        {
            return BadRequest(ApiResponse.Required("Challenge and credential"));
        }

        await daemon!.ProvideCredentialAsync(session!.Id, request.Challenge, request.Credential, cancellationToken);

        return Ok(ApiResponse.Message("Credential sent"));
    }

    /// <summary>
    /// Polls for the next credential challenge / login state of the RUNNING persistent session.
    /// AdminOnly analogue of <c>GET {service}/sessions/{id}/challenge</c>. Delegates to
    /// <see cref="PrefillDaemonServiceBase.WaitForChallengeAsync(string, TimeSpan?, CancellationToken)"/>.
    /// </summary>
    [HttpGet("challenge")]
    public async Task<ActionResult<CredentialChallenge>> GetChallengeAsync(
        [FromQuery] PrefillPlatform service,
        [FromQuery] int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        var (daemon, session, error) = ResolveRunningPersistentSession(service);
        if (error is not null)
        {
            return error;
        }

        var challenge = await daemon!.WaitForChallengeAsync(session!.Id, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);

        if (challenge == null)
        {
            var status = await daemon.GetSessionStatusAsync(session.Id, cancellationToken);
            if (status?.Status == "logged-in")
            {
                return Ok(new { status = "logged-in" });
            }
            return NoContent();
        }

        return Ok(challenge);
    }

    /// <summary>
    /// Cancels a pending interactive login for the RUNNING persistent session and resets auth state.
    /// AdminOnly analogue of the user cancel-login flow. Delegates to
    /// <see cref="PrefillDaemonServiceBase.CancelLoginAsync(string, CancellationToken)"/>.
    /// </summary>
    [HttpPost("cancel-login")]
    public async Task<ActionResult> CancelLoginAsync(
        [FromBody] PersistentLoginRequest request,
        CancellationToken cancellationToken)
    {
        var (daemon, session, error) = ResolveRunningPersistentSession(request.Service);
        if (error is not null)
        {
            return error;
        }

        await daemon!.CancelLoginAsync(session!.Id, cancellationToken);

        return Ok(ApiResponse.Message("Login cancelled"));
    }

    /// <summary>
    /// Logs the RUNNING persistent session out in place - the daemon forgets its stored account
    /// without the container being restarted. AdminOnly analogue of the other persistent-session
    /// routes. Delegates to
    /// <see cref="PrefillDaemonServiceBase.LogoutPersistentSessionAsync(string, CancellationToken)"/>.
    /// When the attempt genuinely fails (daemon reports failure, or the round-trip throws),
    /// <c>forgotten</c> is false and the frontend falls back to its existing stop+restart flow. NOTE:
    /// an un-updated steam/epic daemon image reports success here without actually deleting the
    /// stored account file - <c>forgotten:true</c> is not a hard guarantee on such images, and this
    /// endpoint has no way to detect that case; it self-resolves once the image is rebuilt.
    /// </summary>
    [HttpPost("logout")]
    public async Task<ActionResult<PersistentLogoutResponseDto>> LogoutAsync(
        [FromBody] PersistentLoginRequest request,
        CancellationToken cancellationToken)
    {
        var (daemon, session, error) = ResolveRunningPersistentSession(request.Service);
        if (error is not null)
        {
            return error;
        }

        var result = await daemon!.LogoutPersistentSessionAsync(session!.Id, cancellationToken);

        return Ok(new PersistentLogoutResponseDto
        {
            Forgotten = result.LoggedOut,
            Fallback = result.LoggedOut ? null : "restart-required"
        });
    }

    /// <summary>
    /// Returns the admin-configured persistent login validity window in days.
    /// </summary>
    [HttpGet("validity")]
    public ActionResult<PersistentLoginValidityDto> GetValidity()
    {
        return Ok(new PersistentLoginValidityDto
        {
            Days = _stateService.GetAdminPersistentLoginValidityDays()
        });
    }

    /// <summary>
    /// Updates the admin-configured persistent login validity window (1-365 days).
    /// </summary>
    [HttpPut("validity")]
    public async Task<ActionResult<PersistentLoginValidityDto>> SetValidityAsync([FromBody] PersistentLoginValidityDto request)
    {
        try
        {
            _stateService.SetAdminPersistentLoginValidityDays(request.Days);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(ex.Message);
        }

        // Re-anchor every running persistent session immediately so the new validity is the single
        // source of truth for the re-login date (and persist it so a restart keeps the new window).
        foreach (var daemon in PrefillDaemonServiceBase.ResolveAllDaemons(_serviceProvider))
        {
            await daemon.UpdatePersistentSessionExpiryAsync(request.Days);
        }

        return Ok(new PersistentLoginValidityDto
        {
            Days = _stateService.GetAdminPersistentLoginValidityDays()
        });
    }

    /// <summary>
    /// Resolves cached app ids for the game picker using manager DB only (no live daemon commands).
    /// </summary>
    private async Task<List<string>> ResolveCachedAppIdsForGamePickerAsync(
        List<string> ownedAppIds,
        CancellationToken cancellationToken)
    {
        if (ownedAppIds.Count == 0)
        {
            return [];
        }

        try
        {
            var cachedApps = await _cacheService.GetCachedAppsAsync();
            var ownedSet = new HashSet<string>(ownedAppIds, StringComparer.Ordinal);
            return cachedApps
                .Select(a => a.AppId.ToString())
                .Where(id => ownedSet.Contains(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve cached app ids for persistent game picker");
            return [];
        }
    }

    /// <summary>
    /// Resolves the daemon + RUNNING persistent session for a platform, applying the exact same
    /// guard pattern as <see cref="GetGamesAsync"/>: returns a populated error <see cref="ActionResult"/>
    /// (BadRequest/NotFound/Forbid) when no running persistent session exists, otherwise returns the
    /// daemon and session with a null error. Defense-in-depth: never resolves a non-persistent session.
    /// </summary>
    private (PrefillDaemonServiceBase? Daemon, DaemonSession? Session, ActionResult? Error) ResolveRunningPersistentSession(
        PrefillPlatform service)
    {
        var daemon = PrefillDaemonServiceBase.ResolveDaemon(_serviceProvider, service);
        if (daemon is null)
        {
            return (null, null, BadRequest($"No daemon registered for service '{service}'"));
        }

        var session = daemon.GetActivePersistentSession();
        if (session is null)
        {
            return (null, null, NotFound($"No running persistent session for service '{service}'"));
        }

        if (!session.IsPersistent)
        {
            return (null, null, Forbid());
        }

        return (daemon, session, null);
    }

    private static PrefillPlatform ParsePlatform(string platformName)
    {
        if (Enum.TryParse<PrefillPlatform>(platformName, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return PrefillPlatform.Steam;
    }

    /// <summary>
    /// Pure display-time cap for the persistent re-login date: the EARLIER of the manager's validity
    /// window (<paramref name="expiresAt"/>) and the daemon's real token expiry (<paramref name="token"/>).
    /// Returns <paramref name="expiresAt"/> when the token is null or later, so the UI never shows a
    /// re-login date that outlives the real token. The stored <c>ExpiresAt</c> is never capped (the
    /// token is unknown at creation); the cap applies only here, at display.
    /// </summary>
    public static DateTime ComputeEffectiveRelogin(DateTime expiresAt, DateTimeOffset? token)
    {
        if (token is { } tok && tok.UtcDateTime < expiresAt)
        {
            return tok.UtcDateTime;
        }

        return expiresAt;
    }
}

/// <summary>Identifies a platform for persistent session operations.</summary>
public sealed class PersistentServiceRequest
{
    public required PrefillPlatform Service { get; init; }
}

/// <summary>Sets selected apps on the running persistent session.</summary>
public sealed class PersistentSelectedAppsRequest
{
    public required PrefillPlatform Service { get; init; }
    public required List<string> AppIds { get; init; }
}

/// <summary>Starts prefill on the running persistent session (mirrors guest StartPrefillRequest).</summary>
public sealed class PersistentStartPrefillRequest
{
    public required PrefillPlatform Service { get; init; }
    public List<string>? AppIds { get; init; }
    public bool All { get; init; }
    public bool Recent { get; init; }
    public bool RecentlyPurchased { get; init; }
    public int? Top { get; init; }
    public bool Force { get; init; }
    public List<string>? OperatingSystems { get; init; }
    public int? MaxConcurrency { get; init; }
}

/// <summary>Request body for starting a persistent session.</summary>
public sealed class StartPersistentSessionRequest
{
    /// <summary>Platform whose daemon should own the persistent session.</summary>
    public required PrefillPlatform Service { get; init; }
}

/// <summary>
/// Request body for the persistent interactive-login routes that only need to identify the platform
/// (login start / cancel-login). The running persistent session is resolved server-side.
/// </summary>
public sealed class PersistentLoginRequest
{
    /// <summary>Platform whose running persistent session should be logged in / cancelled.</summary>
    public required PrefillPlatform Service { get; init; }
}

/// <summary>
/// Request body for providing a credential to the running persistent session. Carries the platform
/// plus the exact same <see cref="CredentialChallenge"/> + credential payload the user route uses
/// (<see cref="ProvideCredentialRequest"/>), so the frontend can reuse its login challenge types.
/// </summary>
public sealed class PersistentProvideCredentialRequest
{
    /// <summary>Platform whose running persistent session the credential targets.</summary>
    public required PrefillPlatform Service { get; init; }

    /// <summary>The challenge being answered (same shape as the user credential route).</summary>
    public CredentialChallenge? Challenge { get; init; }

    /// <summary>The encrypted credential value (same shape as the user credential route).</summary>
    public string? Credential { get; init; }
}

/// <summary>Request body for stopping a persistent session.</summary>
public sealed class StopPersistentSessionRequest
{
    /// <summary>Id of the persistent session to stop.</summary>
    public required string SessionId { get; init; }
}

/// <summary>Result of a persistent-session logout attempt.</summary>
public sealed class PersistentLogoutResponseDto
{
    /// <summary>
    /// True when the daemon acknowledged the in-place logout; the container was not restarted. Not a
    /// hard guarantee the account file was deleted - an un-updated steam/epic daemon image also
    /// reports success while only tearing down the live session (see
    /// <see cref="PrefillDaemonServiceBase.LogoutPersistentSessionAsync(string, CancellationToken)"/>).
    /// </summary>
    public required bool Forgotten { get; init; }

    /// <summary>
    /// Present only when <see cref="Forgotten"/> is false: the attempt genuinely failed (daemon
    /// reported failure, or the round-trip threw), so the caller must fall back to a stop+restart to
    /// clear the session's auth state.
    /// </summary>
    public string? Fallback { get; init; }
}

/// <summary>Typed view of a persistent prefill session.</summary>
public sealed class PersistentPrefillSessionDto
{
    /// <summary>Daemon session id.</summary>
    public required string SessionId { get; init; }

    /// <summary>Platform that owns the session.</summary>
    public required PrefillPlatform Service { get; init; }

    /// <summary>True while the daemon session is in the Active status.</summary>
    public required bool IsRunning { get; init; }

    /// <summary>
    /// True when the daemon reports it is actually logged in (live <c>status</c> == "logged-in").
    /// This is the UI's source of truth for authentication, not <see cref="NeedsRelogin"/>. Defaults
    /// to false when the session is not running or the status call is unavailable.
    /// </summary>
    public required bool IsAuthenticated { get; init; }

    /// <summary>UTC instant at which the persistent login validity expires (DaemonSession.ExpiresAt).</summary>
    public required DateTime AuthExpiresAtUtc { get; init; }

    /// <summary>Seconds remaining until <see cref="AuthExpiresAtUtc"/> (0 once elapsed).</summary>
    public required long AuthTimeRemainingSeconds { get; init; }

    /// <summary>True when the session is past expiry and the admin must re-authenticate in place.</summary>
    public required bool NeedsRelogin { get; init; }

    /// <summary>
    /// The daemon's REAL underlying token expiry queried live from its <c>status</c> command
    /// (Steam JWT ValidTo / Epic refresh_expires_at / Xbox refresh-token expiry). Distinct from
    /// <see cref="AuthExpiresAtUtc"/>, which is the manager's 90-day persistent login-validity window.
    /// Null when the session is not running, the status call fails, or the daemon does not report it.
    /// </summary>
    public DateTimeOffset? DaemonAuthExpiresAtUtc { get; init; }

    /// <summary>True while a prefill download is in progress on this session.</summary>
    public bool IsPrefilling { get; init; }

    /// <summary>Aggregate bytes transferred during the current or last prefill run.</summary>
    public long TotalBytesTransferred { get; init; }

    /// <summary>Name of the app currently being prefilled, if any.</summary>
    public string? CurrentAppName { get; init; }
}

/// <summary>Persistent login validity window, in days.</summary>
public sealed class PersistentLoginValidityDto
{
    /// <summary>Validity window in days (1-365).</summary>
    public required int Days { get; init; }
}

/// <summary>
/// Owned games plus up-to-date cached app ids for a persistent session. Matches the shape the
/// frontend GameSelectionModal expects (games[], cachedAppIds[]).
/// </summary>
public sealed class PersistentPrefillGamesDto
{
    /// <summary>Owned games for the persistent session (same payload as the user games route).</summary>
    public required List<OwnedGame> Games { get; init; }

    /// <summary>App ids whose cached content is up to date for the session.</summary>
    public required List<string> CachedAppIds { get; init; }
}
