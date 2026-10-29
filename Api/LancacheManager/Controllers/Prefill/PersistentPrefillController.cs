using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
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
[Authorize(Policy = "AccountHolder")]
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
    [ProducesResponseType(typeof(DaemonSessionDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<DaemonSessionDto>> StartAsync(
        [FromBody] StartPersistentSessionRequest request,
        CancellationToken cancellationToken)
    {
        var daemon = PrefillDaemonServiceBase.ResolveDaemon(_serviceProvider, request.Service);
        if (daemon is null)
        {
            return BadRequest(ApiResponse.Error($"No daemon registered for service '{request.Service}'"));
        }

        DaemonSession session = await daemon.StartPersistentSessionAsync(
            request.Service,
            _systemUserId,
            cancellationToken: cancellationToken);
        return Ok(DaemonSessionDto.FromSession(session));
    }

    /// <summary>
    /// Stops a persistent session.
    /// </summary>
    /// <remarks>
    /// Auth is not persisted, so a later restart requires a fresh login.
    /// </remarks>
    [HttpPost("stop")]
    public async Task<ActionResult> StopAsync([FromBody] StopPersistentSessionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return BadRequest(ApiResponse.Required("sessionId"));
        }

        foreach (var daemon in PrefillDaemonServiceBase.ResolveAllDaemons(_serviceProvider))
        {
            if (daemon.GetSession(request.SessionId) is not null
                || daemon.GetFailedTeardownSession(request.SessionId) is not null)
            {
                await daemon.StopPersistentSessionAsync(request.SessionId, terminatedBy: "admin");
                return Ok();
            }
        }

        return NotFound(ApiResponse.Error($"No persistent session found with id {request.SessionId}"));
    }

    /// <summary>
    /// Lists every persistent session across all daemons.
    /// </summary>
    [HttpGet("list")]
    [ProducesResponseType(typeof(List<PersistentPrefillSessionDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<PersistentPrefillSessionDto>>> ListAsync(CancellationToken cancellationToken)
    {
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
                // interactively. A person's cancel keeps the row signed out while that mark is set, even
                // when the daemon still reports logged-in. Resilient: a single failing/slow status call
                // must never sink the whole list, so it is per-session try/caught (auth defaults false
                // when unavailable). A read that failed is reported as unknown, never as signed out.
                DateTimeOffset? daemonAuthExpiresAtUtc = null;
                bool isAuthenticated = false;
                bool authUnknown = false;
                if (isRunning)
                {
                    try
                    {
                        var status = await daemon.GetSessionStatusAsync(session.Id, cancellationToken);
                        daemonAuthExpiresAtUtc = status?.AuthExpiryUtc;
                        authUnknown = status is null;
                        isAuthenticated = status?.Status == "logged-in"
                            && !ScheduledPrefillRunGates.PersonCanceledSignIn(session);
                    }
                    catch
                    {
                        daemonAuthExpiresAtUtc = null;
                        isAuthenticated = false;
                        authUnknown = true;
                    }
                }

                // The honest re-login date is the EARLIER of the manager's validity window and the
                // daemon's real token expiry, so the UI never promises a window longer than the token.
                var effectiveRelogin = ComputeEffectiveRelogin(session.ExpiresAt, daemonAuthExpiresAtUtc);
                var capabilities = session.Capabilities is { SupportsConcurrentPrefill: true } negotiated ? negotiated : null;

                results.Add(new PersistentPrefillSessionDto
                {
                    Runs = daemon.GetRuns(session.Id),
                    DaemonInstanceId = session.Capabilities?.DaemonInstanceId,
                    Features = capabilities?.Features?.ToArray() ?? [],
                    MaxConcurrentRuns = capabilities?.MaxConcurrentRuns,
                    ActiveRunCount = session.Runs.Values.Count(run => run.TerminalCompletedFlag != 2),
                    Recovering = session.Recovering,
                    SessionId = session.Id,
                    Service = ParsePlatform(session.Platform),
                    IsRunning = isRunning,
                    IsAuthenticated = isAuthenticated,
                    AuthUnknown = authUnknown,
                    AuthExpiresAtUtc = effectiveRelogin,
                    NeedsRelogin = session.NeedsRelogin,
                    DaemonAuthExpiresAtUtc = daemonAuthExpiresAtUtc
                });
            }
        }

        return Ok(results);
    }

    /// <summary>
    /// Lists the owned games for the running persistent session of a platform.
    /// </summary>
    /// <remarks>
    /// Also returns up-to-date cached app ids. This is the AccountHolder analogue of the user-scoped
    /// <c>GET {service}/sessions/{id}/games</c> route: that route enforces
    /// <c>ValidateSessionOwnership</c> (session.UserId == caller), which always 403s for persistent
    /// system-owned sessions whose owner is the derived system user, not the admin's session id.
    /// Bypassing ownership is safe here because the endpoint is <c>[Authorize(Policy = "AccountHolder")]</c>
    /// and is hard-restricted to sessions whose <see cref="DaemonSession.IsPersistent"/> is true.
    /// Reuses the exact same daemon method the user route calls
    /// (<see cref="PrefillDaemonServiceBase.GetOwnedGamesAsync(string, CancellationToken)"/>) so there
    /// is no game-list duplication.
    /// </remarks>
    [HttpGet("games")]
    [ProducesResponseType(typeof(PersistentPrefillGamesDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<PersistentPrefillGamesDto>> GetGamesAsync(
        [FromQuery] PrefillPlatform service,
        CancellationToken cancellationToken,
        [FromQuery] string? expectedSessionId = null,
        [FromQuery] List<string>? operatingSystems = null)
    {
        var (daemon, session, error) = ResolveRunningPersistentSession(service, expectedSessionId);
        if (error is not null) return error;
        var expiresAtUtc = daemon!.CacheStatusClock.GetUtcNow().AddSeconds(120);

        using var prerequisiteDeadline = new CancellationTokenSource(
            expiresAtUtc - daemon.CacheStatusClock.GetUtcNow(),
            daemon.CacheStatusClock);
        using var prerequisite = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            prerequisiteDeadline.Token);

        List<OwnedGame> games;
        List<string> ownedAppIds;
        List<string> cachedAppIds;
        try
        {
            games = await daemon.GetOwnedGamesAsync(session!.Id, prerequisite.Token);
            ownedAppIds = games
                .Select(g => g.AppId.ToString())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            cachedAppIds = await ResolveCachedAppIdsForGamePickerAsync(
                service, ownedAppIds, prerequisite.Token);
        }
        catch (DaemonCommandException ex) when (ex.RequiresLogin && service != PrefillPlatform.Steam)
        {
            throw new DaemonCommandException(ex.ErrorCode, requiresLogin: true, loginStageKey: "errors.prefill.loginRequired");
        }
        catch (OperationCanceledException ex) when (prerequisiteDeadline.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            // The server's own deadline passed; the caller did not cancel, so this is a timeout and not a client cancel.
            _logger.LogWarning(ex, "The game list for {Service} was not ready within its deadline", service);
            throw new ServiceUnavailableException("The service did not return the game list in time. Try again.")
            {
                StageKey = "errors.prefill.ownedGamesTimedOut"
            };
        }

        List<string> outdatedAppIds = [];
        List<string> unknownAppIds = [];
        List<AppCacheStatus> apps = [];
        string? message = null;
        if (cachedAppIds.Count > 0)
        {
            try
            {
                var status = await daemon.GetCacheStatusAsync(
                    session.Id,
                    cachedAppIds,
                    expiresAtUtc,
                    operatingSystems,
                    cancellationToken);
                var normalized = status.Normalize(cachedAppIds);
                apps = normalized.Apps;
                message = normalized.Message;
                (_, outdatedAppIds, unknownAppIds) = normalized.ResolveAppIds(cachedAppIds);
            }
            catch (DaemonCommandException ex) when (!ex.RequiresLogin)
            {
                _logger.LogWarning(ex, "Could not verify cached games for {Service}", service);
                apps = CacheStatusResult.Unknown(cachedAppIds, CacheReason.StatusUnavailable).Apps;
                unknownAppIds = cachedAppIds.ToList();
            }
        }

        var (_, current, currentError) = ResolveRunningPersistentSession(service, session.Id);
        if (currentError is not null) return currentError;
        if (!ReferenceEquals(current, session))
            throw new ConflictException(PersistentLoginConflictReasons.SessionReplaced);

        return Ok(new PersistentPrefillGamesDto
        {
            Games = games,
            Apps = apps,
            CachedAppIds = cachedAppIds,
            OutdatedAppIds = outdatedAppIds,
            UnknownAppIds = unknownAppIds,
            Message = message
        });
    }

    /// <summary>
    /// Sets the selected app list for the running persistent session.
    /// </summary>
    /// <remarks>
    /// Same as the guest <c>POST …/selected-apps</c> route.
    /// </remarks>
    [HttpPost("selected-apps")]
    public async Task<ActionResult> SetSelectedAppsAsync(
        [FromBody] PersistentSelectedAppsRequest request,
        CancellationToken cancellationToken)
    {
        var (daemon, session, error) = ResolveRunningPersistentSession(
            request.Service,
            request.SessionId);
        if (error is not null)
        {
            return error;
        }

        if (!daemon!.PersistentChangeGate.TryEnterChange(out var change))
        {
            return Conflict(ApiResponse.Error($"The persistent {request.Service} container is busy."));
        }

        await using (change!)
        {
            var (claimedDaemon, claimedSession, claimedError) = ResolveRunningPersistentSession(
                request.Service,
                request.SessionId);
            if (claimedError is not null)
            {
                return claimedError;
            }

            if (!ReferenceEquals(claimedDaemon, daemon)
                || !ReferenceEquals(claimedSession, session)
                || claimedSession!.LoginOperationId.HasValue
                || claimedSession.Runs.Values.Any(run => run.TerminalCompletedFlag != 2))
            {
                return Conflict(ApiResponse.Error($"The persistent {request.Service} container is busy."));
            }

            await daemon.SetSelectedAppsAsync(claimedSession.Id, request.AppIds, cancellationToken);
            return Ok();
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
        var (daemon, session, error) = ResolveRunningPersistentSession(
            request.Service,
            request.SessionId);
        if (error is not null)
        {
            return error;
        }

        if (!session!.Runs.IsEmpty)
        {
            if (request.RunId.HasValue)
                await daemon!.CancelPrefillRunAsync(session.Id, request.RunId.Value, cancellationToken);
            else await daemon!.CancelPrefillAsync(session.Id, cancellationToken);
            return Ok();
        }

        if (!daemon!.PersistentChangeGate.TryEnterChange(out var change))
        {
            return Conflict(ApiResponse.Error($"The persistent {request.Service} container is busy."));
        }

        await using (change!)
        {
            var (claimedDaemon, claimedSession, claimedError) = ResolveRunningPersistentSession(
                request.Service,
                request.SessionId);
            if (claimedError is not null)
            {
                return claimedError;
            }

            if (!ReferenceEquals(claimedDaemon, daemon)
                || !ReferenceEquals(claimedSession, session))
            {
                return Conflict(ApiResponse.Error("The persistent prefill session changed."));
            }

            if (request.RunId.HasValue
                && !claimedSession!.Runs.Values.Any(run =>
                    run.PrefillRunId == request.RunId.Value && run.TerminalCompletedFlag != 2))
            {
                return Conflict(ApiResponse.Error("The requested prefill run is no longer active."));
            }

            try
            {
                await daemon.CancelPrefillAsync(claimedSession!.Id, cancellationToken);
            }
            catch (Exception ex) when (ex is not ApiException
                && (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
            {
                // A cancel that cannot find the session it was sent for is a failed daemon request, not
                // the caller's own cancel or a disk fault.
                _logger.LogWarning(ex, "Cancel-prefill failed for persistent session {SessionId}", claimedSession!.Id);
                throw new DaemonCommandException();
            }
            return Ok();
        }
    }

    [HttpGet("runs")]
    public ActionResult<IReadOnlyList<DaemonRunStatus>> GetRuns(
        [FromQuery] PrefillPlatform service, [FromQuery] string? sessionId = null)
    {
        var (daemon, session, error) = ResolveRunningPersistentSession(service, sessionId);
        if (error is not null) return error;
        return Ok(daemon!.GetRuns(session!.Id));
    }

    [HttpGet("runs/{runId:guid}/failed-games")]
    [ProducesResponseType(typeof(IReadOnlyList<PrefillRunFailedGame>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PrefillRunFailedGame>>> GetRunFailedGamesAsync(
        Guid runId, [FromQuery] PrefillPlatform service, CancellationToken cancellationToken)
    {
        var games = await _serviceProvider.GetRequiredService<PrefillSessionService>()
            .GetFailedGamesAsync(runId, service, cancellationToken);
        if (games is null)
            return NotFound(new NotFoundResponse { Error = "Prefill run not found", StageKey = "errors.prefill.runNotFound" });
        return Ok(games);
    }

    [HttpGet("integration-login")]
    [ProducesResponseType(typeof(IntegrationLoginAvailability), StatusCodes.Status200OK)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006", Justification = "The existing public method name is retained for API compatibility.")]
    public async Task<ActionResult<IntegrationLoginAvailability>> GetIntegrationLoginAvailability(
        [FromQuery] PrefillPlatform service)
    {
        var daemon = PrefillDaemonServiceBase.ResolveDaemon(_serviceProvider, service);
        if (daemon is null)
        {
            return BadRequest(ApiResponse.Error($"No daemon registered for service '{service}'"));
        }

        var caller = await IntegrationLease.ResolveCallerAsync(HttpContext);
        return Ok(daemon.GetIntegrationLoginAvailability(caller.AccountId, caller));
    }

    /// <summary>
    /// Starts or resumes the interactive login flow for the running persistent session.
    /// </summary>
    /// <remarks>
    /// Returns the initial credential challenge. AccountHolder analogue of the user route
    /// <c>POST {service}/sessions/{id}/login</c>, which enforces <c>ValidateSessionOwnership</c> and
    /// always 403s for system-owned persistent sessions. Safe to bypass ownership here because this
    /// controller is <c>[Authorize(Policy = "AccountHolder")]</c> and hard-restricted to persistent
    /// sessions. Delegates to <see cref="PrefillDaemonServiceBase.StartLoginAsync(string, TimeSpan?, CancellationToken)"/>.
    /// </remarks>
    [HttpPost("login")]
    [ProducesResponseType(typeof(CredentialChallenge), StatusCodes.Status200OK)]
    public async Task<ActionResult<CredentialChallenge>> StartLoginAsync(
        [FromBody] PersistentLoginRequest request,
        CancellationToken cancellationToken)
    {
        if (request.LoginId == Guid.Empty)
        {
            return BadRequest(ApiResponse.Error("loginId must be a non-empty UUID."));
        }
        var (daemon, session, error) = ResolveRunningPersistentSession(
            request.Service,
            request.SessionId);
        if (error is not null)
        {
            return error;
        }

        var caller = request.ReuseIntegration ? await IntegrationLease.ResolveCallerAsync(HttpContext) : null;
        await using var lease = caller is not null
            ? await daemon!.AcquireIntegrationLoginAsync(caller, cancellationToken)
            : null;
        if (caller is not null)
        {
            var availability = daemon!.GetIntegrationLoginAvailability(caller.AccountId, caller);
            if (!availability.Available) IntegrationLease.Refuse(availability.Reason);
        }

        await using var change = await daemon!.PersistentChangeGate.EnterChangeAsync(
            cancellationToken);
        var loginCommandDispatched = false;

        // The sign-in belongs to the auth session that asked for it, not to the system owner
        // the persistent session runs under.
        var challenge = request.ReuseIntegration
            ? await daemon!.ReusePersistentIntegrationLoginAsync(
                session!.Id,
                caller!.AccountId,
                () => loginCommandDispatched = true,
                HttpContext.GetRequiredSessionId(),
                cancellationToken,
                lease,
                request.LoginId)
            : await daemon!.StartPersistentLoginAsync(
                session!.Id,
                TimeSpan.FromSeconds(30),
                () => loginCommandDispatched = true,
                HttpContext.GetRequiredSessionId(),
                cancellationToken,
                request.LoginId);

        if (challenge == null)
        {
            // No challenge means either already logged in, a fail-fast daemon failure, or a genuine timeout.
            var status = await daemon.GetSessionStatusAsync(session.Id, cancellationToken);
            if (status?.Status == "logged-in")
            {
                // Every successful login response (logged in or a challenge) carries the resolved sessionId so the frontend can pin the session this flow belongs to.
                return Ok(new PersistentLoginStatusResponse
                {
                    SessionId = session.Id,
                    Status = "logged-in",
                    Message = loginCommandDispatched ? "Logged in" : "Already logged in"
                });
            }

            if (!string.IsNullOrEmpty(session.LastLoginFailureMessage))
            {
                return BadRequest(ApiResponse.Error(session.LastLoginFailureMessage));
            }

            return BadRequest(ApiResponse.Error("Login timeout - daemon may not be ready"));
        }

        // Stamp the resolved sessionId on the challenge so the frontend pins the flow to this session. The id
        // never changes for the session, so writing it on a cached challenge is safe.
        challenge.SessionId = session.Id;
        return Ok(challenge);
    }

    /// <summary>
    /// Provides an encrypted credential in response to a login challenge.
    /// </summary>
    /// <remarks>
    /// For the running persistent session. AccountHolder analogue of
    /// <c>POST {service}/sessions/{id}/credential</c>. Reuses the user route's
    /// <see cref="ProvideCredentialRequest"/> payload (Challenge + Credential) and delegates to
    /// <see cref="PrefillDaemonServiceBase.ProvideCredentialAsync(string, CredentialChallenge, string, CancellationToken)"/>.
    /// </remarks>
    [HttpPost("credential")]
    [ProducesResponseType(typeof(MessageOnlyResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MessageOnlyResponse>> ProvideCredentialAsync(
        [FromBody] PersistentProvideCredentialRequest request,
        CancellationToken cancellationToken)
    {
        // sessionId is REQUIRED - no fallback defaults - so a
        // credential can never be resolved against whichever session happens to be active now.
        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return BadRequest(ApiResponse.Required("sessionId"));
        }

        var (daemon, session, error) = ResolveRunningPersistentSession(request.Service, request.SessionId);
        if (error is not null)
        {
            return error;
        }

        if (request.Challenge == null || string.IsNullOrEmpty(request.Credential))
        {
            return BadRequest(ApiResponse.Required("Challenge and credential"));
        }

        try
        {
            await using var change = await daemon!.PersistentChangeGate.EnterChangeAsync(
                cancellationToken);
            await daemon!.ProvideCredentialAsync(
                session!.Id,
                request.Challenge,
                request.Credential,
                cancellationToken);
        }
        catch (DaemonCredentialRejectedException ex)
        {
            // A fixed daemon reported it had no matching
            // pending challenge for this credential. Surface it as a typed conflict the frontend reads
            // via error.cause, so a misrouted/rejected credential is never celebrated as accepted.
            _logger.LogWarning(ex,
                "Daemon rejected credential for persistent {Service} session {SessionId}",
                request.Service, session!.Id);
            return Conflict(new PersistentLoginConflictResponse
            {
                Error = PersistentLoginConflictReasons.CredentialRejected,
                State = "active"
            });
        }

        return Ok(new MessageOnlyResponse { Message = "Credential sent" });
    }

    /// <summary>
    /// Reads the current credential challenge or login state, without waiting.
    /// </summary>
    /// <remarks>
    /// Of the running persistent session. AccountHolder analogue of
    /// <c>GET {service}/sessions/{id}/challenge</c>. Every new challenge and every sign-in ending is pushed over SignalR,
    /// so the browser reads this once after a page reload and once when its hub connection comes back. With
    /// <c>loginAttempt</c>, answers how that sign-in ended once it ended other than signed in.
    /// </remarks>
    [HttpGet("challenge")]
    [ProducesResponseType(typeof(CredentialChallenge), StatusCodes.Status200OK)]
    public async Task<ActionResult<CredentialChallenge>> GetChallengeAsync(
        [FromQuery] PrefillPlatform service,
        [FromQuery] string? sessionId = null,
        [FromQuery] long? loginAttempt = null,
        CancellationToken cancellationToken = default)
    {
        // sessionId is REQUIRED - no fallback defaults - so a
        // reader pinned to session A can never be served session B's challenge after a stop/start race.
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return BadRequest(ApiResponse.Required("sessionId"));
        }

        var (daemon, session, error) = ResolveRunningPersistentSession(service, sessionId);
        if (error is not null)
        {
            return error;
        }

        // A refused or stopped sign-in sends no further challenge, so the reader of that attempt is told how it ended. A
        // sign-in that succeeded answers "logged-in" below.
        if (loginAttempt is { } attempt
            && daemon!.GetLoginEnding(session!, attempt) is { Status: not OperationStatus.Completed } ending)
        {
            return Ok(new PersistentLoginStatusResponse { SessionId = session!.Id, Status = "ended", LoginEnding = ending });
        }

        CredentialChallenge? challenge;
        lock (session!.PrefillLock)
        {
            challenge = session.AuthState != DaemonAuthState.Authenticated ? session.PendingLoginChallenge : null;
        }

        if (challenge == null)
        {
            // Answers from the same state the session list shows, so a person's cancel that holds the
            // container signed out is not read back as signed in
            var status = await daemon!.GetSessionStatusAsync(session.Id, cancellationToken);
            if (status?.Status == "logged-in" && !ScheduledPrefillRunGates.PersonCanceledSignIn(session))
            {
                return Ok(new PersistentLoginStatusResponse { SessionId = session.Id, Status = "logged-in" });
            }
            return NoContent();
        }

        // Stamp the resolved sessionId onto the challenge so the frontend can (re-)pin the flow.
        challenge.SessionId = session.Id;
        return Ok(challenge);
    }

    /// <summary>
    /// Cancels a pending interactive login and resets auth state.
    /// </summary>
    /// <remarks>
    /// For the running persistent session. AccountHolder analogue of the user cancel-login flow.
    /// Delegates to <see cref="PrefillDaemonServiceBase.CancelLoginAsync(string, CancellationToken, long?, Guid?, string?)"/>.
    /// </remarks>
    [HttpPost("cancel-login")]
    [ProducesResponseType(typeof(MessageOnlyResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MessageOnlyResponse>> CancelLoginAsync(
        [FromBody] PersistentCancelLoginRequest request,
        CancellationToken cancellationToken)
    {
        if (request.LoginId == Guid.Empty || (request.LoginId is null && request.LoginAttempt is null))
        {
            return BadRequest(ApiResponse.Error("A non-empty loginId or loginAttempt is required."));
        }
        // sessionId is REQUIRED - no fallback defaults.
        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return BadRequest(ApiResponse.Required("sessionId"));
        }

        var (daemon, session, error) = ResolveRunningPersistentSession(request.Service);
        if (error is not null)
        {
            return error;
        }

        // Unlike the other pinned routes, a cancel for a since-replaced session is an idempotent
        // no-op, NOT a 409 - a cancel targeting session A that B has already replaced has effectively
        // already happened, and must NEVER cancel B's live login. Report success without touching the
        // daemon so the frontend's cancel/reset flow completes cleanly.
        if (!string.Equals(session!.Id, request.SessionId, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Ignoring cancel-login for persistent {Service}: pinned session {Expected} is no longer active (current {Actual})",
                request.Service, request.SessionId, session.Id);
            return Ok(new MessageOnlyResponse { Message = "Login already cancelled" });
        }

        // Same idempotent no-op for a cancel meant for an older login attempt on this session: that
        // attempt has already ended, and a newer one must never be cancelled by it.
        if (!await daemon!.CancelLoginAsync(session.Id, cancellationToken, request.LoginAttempt, request.LoginId, request.StopReason))
        {
            return Ok(new MessageOnlyResponse { Message = "Login already cancelled" });
        }

        return Ok(new MessageOnlyResponse { Message = "Login cancelled" });
    }

    /// <summary>
    /// Logs the running persistent session out in place.
    /// </summary>
    /// <remarks>
    /// The daemon forgets its stored account while the same container keeps running. The caller must
    /// identify that container by its current session id. AccountHolder analogue of the other
    /// persistent-session routes. Delegates to
    /// <see cref="PrefillDaemonServiceBase.LogoutPersistentSessionAsync(string, CancellationToken)"/>.
    /// A daemon refusal or unconfirmed round-trip is a typed conflict and leaves the current container
    /// running with its last confirmed authentication state.
    /// </remarks>
    [HttpPost("logout")]
    [ProducesResponseType(typeof(PersistentLogoutResponseDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<PersistentLogoutResponseDto>> LogoutAsync(
        [FromBody] PersistentLoginRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return BadRequest(ApiResponse.Required("sessionId"));
        }

        var (daemon, session, error) = ResolveRunningPersistentSession(request.Service, request.SessionId);
        if (error is not null)
        {
            return error;
        }

        var result = await daemon!.LogoutPersistentSessionAsync(session!.Id, cancellationToken);
        if (!result.LoggedOut)
        {
            throw new ConflictException(
                $"The persistent {request.Service} session did not confirm logout.")
            {
                StageKey = "management.auth.errors.logoutFailed",
                Context = new() { ["service"] = request.Service, ["sessionId"] = session.Id }
            };
        }

        return Ok(new PersistentLogoutResponseDto
        {
            Forgotten = true
        });
    }

    /// <summary>
    /// Clears logins for every registered service.
    /// </summary>
    /// <remarks>
    /// Admin "clear all logins" action: for every registered service, forget its login with a HARD
    /// guarantee. A RUNNING session is cleared via <see cref="PrefillDaemonServiceBase.ForgetRunningPersistentLoginAsync(string, CancellationToken)"/>,
    /// which logs out in place, VERIFIES that against the daemon's live status, and escalates to
    /// terminating the container + deleting its named auth volume when the logout did not verifiably
    /// take (an un-updated image reports success while its volume login survives). When no session is
    /// running, the service's persistent auth volume is removed outright so a STOPPED service's stored
    /// login is forgotten too. Distinct from <see cref="LogoutAsync(PersistentLoginRequest, CancellationToken)"/>,
    /// whose in-place logout is intentionally not escalated;
    /// this is the only route that hard-removes a RUNNING container's login and the only one that can
    /// forget a login for a service with no running container at all.
    /// </remarks>
    [HttpPost("clear-logins")]
    [ProducesResponseType(typeof(ClearPersistentLoginsResponseDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ClearPersistentLoginsResponseDto>> ClearLoginsAsync(CancellationToken cancellationToken)
    {
        return Ok(new ClearPersistentLoginsResponseDto
        {
            Services = await PrefillDaemonServiceBase.ClearAllPersistentLoginsAsync(_serviceProvider, cancellationToken)
        });
    }

    /// <summary>
    /// Returns the admin-configured persistent login validity window in days.
    /// </summary>
    [HttpGet("validity")]
    [ProducesResponseType(typeof(PersistentLoginValidityDto), StatusCodes.Status200OK)]
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
    [ProducesResponseType(typeof(PersistentLoginValidityDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<PersistentLoginValidityDto>> SetValidityAsync([FromBody] PersistentLoginValidityDto request)
    {
        _stateService.SetAdminPersistentLoginValidityDays(request.Days);

        // Read the window back rather than reusing the request: the setter silently clamps to its own
        // 1-365 range, so a larger number would otherwise stamp the sessions with an expiry the saved
        // window never agreed to, and every later read of that window would contradict it.
        var validityDays = _stateService.GetAdminPersistentLoginValidityDays();

        // Set every stored re-login date to its last sign-in plus the saved days (and persist it so a restart
        // keeps it), so saving the same validity never pushes a date later and saving again finishes a save
        // that failed part way.
        try
        {
            foreach (var daemon in PrefillDaemonServiceBase.ResolveAllDaemons(_serviceProvider))
            {
                await daemon.UpdatePersistentSessionExpiryAsync();
            }
        }
        catch (Exception ex)
        {
            // The days are already stored, so the answer says only the dates are left to save.
            _logger.LogWarning(ex, "Persistent login validity saved, but the re-login dates were not updated");
            throw new ServiceUnavailableException("The validity was saved, but the re-login dates were not updated. Save again to finish.")
            {
                StageKey = "errors.prefill.validityDatesNotSaved"
            };
        }

        return Ok(new PersistentLoginValidityDto
        {
            Days = validityDays
        });
    }

    /// <summary>
    /// Resolves cached app ids for the game picker using manager DB only (no live daemon commands).
    /// </summary>
    private async Task<List<string>> ResolveCachedAppIdsForGamePickerAsync(
        PrefillPlatform platform,
        List<string> ownedAppIds,
        CancellationToken cancellationToken)
    {
        if (ownedAppIds.Count == 0)
        {
            return [];
        }

        var cachedApps = await _cacheService.GetCachedAppsAsync(platform, cancellationToken);
        var cachedSet = cachedApps.Select(app => app.AppId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ownedAppIds
            .Where(cachedSet.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Resolves the daemon + RUNNING persistent session for a platform, applying the exact same
    /// guard pattern as <see cref="GetGamesAsync"/>: returns a populated error <see cref="ActionResult"/>
    /// (BadRequest/NotFound/Forbid) when no running persistent session exists, otherwise returns the
    /// daemon and session with a null error. Defense-in-depth: never resolves a non-persistent session.
    /// </summary>
    private (PrefillDaemonServiceBase? Daemon, DaemonSession? Session, ActionResult? Error) ResolveRunningPersistentSession(
        PrefillPlatform service,
        string? expectedSessionId = null)
    {
        var daemon = PrefillDaemonServiceBase.ResolveDaemon(_serviceProvider, service);
        if (daemon is null)
        {
            return (null, null, BadRequest(ApiResponse.Error($"No daemon registered for service '{service}'")));
        }

        var session = daemon.GetActivePersistentSession();
        if (session is null)
        {
            // Distinguish a session that flipped to Error (e.g. the daemon's socket dropped
            // mid-poll) from no session ever having been started, so the frontend can show
            // "press Start to restart" instead of a generic not-running message.
            var erroredSession = daemon.GetAllSessions().Any(s => s.IsPersistent && s.Status == DaemonSessionStatus.Error);
            return (null, null, NotFound(new PersistentSessionNotFoundResponse
            {
                Error = $"No running persistent session for service '{service}'",
                StageKey = "errors.prefill.noRunningSession",
                Context = new() { ["service"] = service },
                State = erroredSession ? PersistentSessionNotFoundState.Errored : PersistentSessionNotFoundState.NotStarted
            }));
        }

        if (!session.IsPersistent)
        {
            return (null, null, Forbid());
        }

        // When a caller pinned a specific session, refuse to
        // silently substitute a different one. A login flow started against session A must never land on
        // a replacement session B just because B became "the" active session for the service in between
        // - that is the exact cross-session leak that misrouted a dead session's username challenge onto
        // a freshly created container. A 409 lets the frontend reset the flow instead of celebrating a
        // credential the wrong daemon never asked for.
        if (!string.IsNullOrEmpty(expectedSessionId)
            && !string.Equals(session.Id, expectedSessionId, StringComparison.Ordinal))
        {
            return (null, null, Conflict(new PersistentLoginConflictResponse
            {
                Error = PersistentLoginConflictReasons.SessionReplaced,
                State = "active"
            }));
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
