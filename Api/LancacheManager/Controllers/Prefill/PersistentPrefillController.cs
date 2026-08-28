using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
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

        if (string.IsNullOrWhiteSpace(request.EditSessionId)
            && string.IsNullOrWhiteSpace(request.EditActionId))
        {
            DaemonSession session = await daemon.StartPersistentSessionAsync(
                request.Service,
                _systemUserId,
                cancellationToken: cancellationToken);
            return Ok(DaemonSessionDto.FromSession(session));
        }

        if (string.IsNullOrWhiteSpace(request.EditSessionId)
            || string.IsNullOrWhiteSpace(request.EditActionId))
        {
            return BadRequest(ApiResponse.Required("editSessionId and editActionId"));
        }

        var lease = daemon.PersistentEditSessionGate.BeginStart(request.EditSessionId, request.EditActionId);
        if (!lease.Accepted)
        {
            return Conflict(ApiResponse.Error("This scheduled-prefill edit session is already being cleaned up."));
        }

        if (!lease.IsOwner)
        {
            var prior = await lease.Completion;
            var priorSession = daemon.GetSession(prior.SessionId);
            return priorSession is null
                ? Conflict(ApiResponse.Error("The edit-session-owned persistent session has already ended."))
                : Ok(DaemonSessionDto.FromSession(priorSession));
        }

        try
        {
            var started = await daemon.StartPersistentSessionForEditAsync(
                request.Service,
                _systemUserId,
                CancellationToken.None);
            daemon.PersistentEditSessionGate.CompleteStart(
                request.EditSessionId,
                request.EditActionId,
                new PersistentPrefillEditSessionStartRecord(
                    request.EditActionId,
                    started.Session.Id,
                    started.CreatedByEditSession));
            return Ok(DaemonSessionDto.FromSession(started.Session));
        }
        catch (Exception ex)
        {
            daemon.PersistentEditSessionGate.FailStart(request.EditSessionId, request.EditActionId, ex);
            throw;
        }
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
            if (daemon.GetSession(request.SessionId) is not null)
            {
                await daemon.StopPersistentSessionAsync(request.SessionId, terminatedBy: "admin");
                return Ok();
            }
        }

        return NotFound(ApiResponse.Error($"No persistent session found with id {request.SessionId}"));
    }

    /// <summary>
    /// Compensates a persistent edit session that was abandoned mid-flight.
    /// </summary>
    /// <remarks>
    /// For each affected service, stops any session the edit itself started, then rolls back or
    /// completes any in-flight login/selection/prefill it began, so an interrupted edit never
    /// leaves an orphaned container or a half-applied change behind.
    /// </remarks>
    [HttpPost("edit-session-cleanup")]
    public async Task<ActionResult> CleanupEditSessionAsync(
        [FromBody] PersistentPrefillEditSessionCleanupRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.EditSessionId)
            || string.IsNullOrWhiteSpace(request.CleanupId))
        {
            return BadRequest(ApiResponse.Required("editSessionId and cleanupId"));
        }

        var duplicateService = request.Services
            .GroupBy(service => service.Service)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateService is not null)
        {
            return BadRequest(ApiResponse.Error(
                $"Cleanup contains duplicate service '{duplicateService.Key}'."));
        }

        var resolved = new List<(
            PersistentPrefillEditSessionCleanupServiceRequest Request,
            PrefillDaemonServiceBase Daemon)>();

        foreach (var serviceRequest in request.Services)
        {
            var daemon = PrefillDaemonServiceBase.ResolveDaemon(_serviceProvider, serviceRequest.Service);
            if (daemon is null)
            {
                return BadRequest(ApiResponse.Error(
                    $"No daemon registered for service '{serviceRequest.Service}'"));
            }

            resolved.Add((serviceRequest, daemon));
        }

        var targets = resolved
            .Select(target => (
                target.Request,
                target.Daemon,
                Lease: target.Daemon.PersistentEditSessionGate.BeginCleanup(
                    request.EditSessionId,
                    request.CleanupId)))
            .ToArray();

        await Task.WhenAll(targets.Select(target => CleanupEditSessionServiceAsync(
            request.EditSessionId,
            request.CleanupId,
            target.Request,
            target.Daemon,
            target.Lease,
            cancellationToken)));

        return Ok();
    }

    /// <summary>
    /// Lists every persistent session across all daemons.
    /// </summary>
    [HttpGet("list")]
    [ProducesResponseType(typeof(List<PersistentPrefillSessionDto>), StatusCodes.Status200OK)]
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
                    // The field's contract is a UTC instant, so the kind is stated at the boundary
                    // rather than left to whatever the value happened to arrive with.
                    CreatedAtUtc = DateTime.SpecifyKind(session.CreatedAt, DateTimeKind.Utc),
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
        CancellationToken cancellationToken)
    {
        var daemon = PrefillDaemonServiceBase.ResolveDaemon(_serviceProvider, service);
        if (daemon is null)
        {
            return BadRequest(ApiResponse.Error($"No daemon registered for service '{service}'"));
        }

        var session = daemon.GetActivePersistentSession();
        if (session is null)
        {
            return NotFound(ApiResponse.Error($"No running persistent session for service '{service}'"));
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

        var editActionError = BeginEditAction(
            daemon!,
            request.EditSessionId,
            request.EditActionId,
            PersistentPrefillEditActionKind.Selection,
            session!.Id,
            out var editAction);
        if (editActionError is not null)
        {
            return editActionError;
        }

        var outcome = PersistentPrefillEditActionOutcome.Failed;
        try
        {
            await using var mutation = await daemon!.PersistentEditSessionGate.EnterMutationAsync(
                cancellationToken);
            await daemon!.SetSelectedAppsAsync(session!.Id, request.AppIds, cancellationToken);
            editAction!.ConfirmEffect(PersistentPrefillEditResourceKind.Selection);
            outcome = PersistentPrefillEditActionOutcome.Succeeded;
            return Ok();
        }
        catch (OperationCanceledException)
        {
            outcome = PersistentPrefillEditActionOutcome.Cancelled;
            throw;
        }
        finally
        {
            editAction?.Complete(outcome);
        }
    }

    /// <summary>
    /// Starts a prefill download on the running persistent session.
    /// </summary>
    /// <remarks>
    /// Same as the guest <c>POST …/prefill</c> route.
    /// </remarks>
    [HttpPost("prefill")]
    [ProducesResponseType(typeof(PrefillResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<PrefillResult>> StartPrefillAsync(
        [FromBody] PersistentStartPrefillRequest request,
        CancellationToken cancellationToken)
    {
        var (daemon, session, error) = ResolveRunningPersistentSession(
            request.Service,
            request.SessionId);
        if (error is not null)
        {
            return error;
        }

        var editActionError = BeginEditAction(
            daemon!,
            request.EditSessionId,
            request.EditActionId,
            PersistentPrefillEditActionKind.Prefill,
            session!.Id,
            out var editAction);
        if (editActionError is not null)
        {
            return editActionError;
        }

        _logger.LogInformation(
            "Starting persistent {Service} prefill on session {SessionId} (force={Force}, selectedApps={Count})",
            request.Service,
            session!.Id,
            request.Force,
            request.AppIds?.Count ?? 0);

        var outcome = PersistentPrefillEditActionOutcome.Failed;
        try
        {
            await using var mutation = await daemon!.PersistentEditSessionGate.EnterMutationAsync(
                cancellationToken);
            if (request.AppIds is { Count: > 0 })
            {
                await daemon!.SetSelectedAppsAsync(session!.Id, request.AppIds, cancellationToken);
                editAction!.ConfirmEffect(PersistentPrefillEditResourceKind.Selection);
            }

            var result = await daemon!.PrefillAsync(
                session!.Id,
                all: request.All,
                recent: request.Recent,
                recentlyPurchased: request.RecentlyPurchased,
                top: request.Top,
                force: request.Force,
                operatingSystems: request.OperatingSystems,
                maxConcurrency: request.MaxConcurrency,
                cancellationToken: cancellationToken);

            if (result.Success)
            {
                editAction!.ConfirmEffect(PersistentPrefillEditResourceKind.Prefill);
                outcome = PersistentPrefillEditActionOutcome.Succeeded;
            }

            return Ok(result);
        }
        catch (PrefillAlreadyRunningException ex)
        {
            outcome = PersistentPrefillEditActionOutcome.Conflict;
            // ex.Message is developer-authored on this exception type, safe to surface directly.
            // Throw the typed 409 so the middleware emits the unified { error, statusCode, traceId } shape.
            throw new ConflictException(ex.Message);
        }
        catch (OperationCanceledException)
        {
            outcome = PersistentPrefillEditActionOutcome.Cancelled;
            throw;
        }
        finally
        {
            editAction?.Complete(outcome);
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

        await daemon!.CancelPrefillAsync(session!.Id, cancellationToken);
        return Ok();
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
        var (daemon, session, error) = ResolveRunningPersistentSession(
            request.Service,
            request.SessionId);
        if (error is not null)
        {
            return error;
        }

        var editActionError = BeginEditAction(
            daemon!,
            request.EditSessionId,
            request.EditActionId,
            PersistentPrefillEditActionKind.Login,
            session!.Id,
            out var editAction);
        if (editActionError is not null)
        {
            return editActionError;
        }

        var outcome = PersistentPrefillEditActionOutcome.Failed;
        try
        {
            await using var mutation = await daemon!.PersistentEditSessionGate.EnterMutationAsync(
                cancellationToken);
            var loginCommandDispatched = false;
            var challenge = await daemon!.StartLoginForEditAsync(
                session!.Id,
                TimeSpan.FromSeconds(30),
                () =>
                {
                    editAction!.ConfirmEffect(PersistentPrefillEditResourceKind.Login);
                    loginCommandDispatched = true;
                },
                cancellationToken);

            if (challenge == null)
            {
                // No challenge means either already logged in, a fail-fast daemon failure, or a genuine timeout.
                var status = await daemon.GetSessionStatusAsync(session.Id, cancellationToken);
                if (status?.Status == "logged-in")
                {
                    outcome = PersistentPrefillEditActionOutcome.NoChange;
                    // RC3: every login response variant carries the
                    // resolved sessionId so the frontend can pin the session this flow belongs to.
                    return Ok(new PersistentLoginStatusResponse
                    {
                        SessionId = session.Id,
                        Status = "logged-in",
                        Message = "Already logged in"
                    });
                }

                if (!string.IsNullOrEmpty(session.LastLoginFailureMessage))
                {
                    return BadRequest(ApiResponse.Error(session.LastLoginFailureMessage));
                }

                return BadRequest(ApiResponse.Error("Login timeout - daemon may not be ready"));
            }

            // Stamp the resolved sessionId onto the challenge so the frontend pins the flow to THIS session
            // (RC3). The value is invariant for the session, so mutating even the cached challenge instance
            // is safe.
            challenge.SessionId = session.Id;
            if (!loginCommandDispatched)
            {
                // A cached challenge resume dispatches no new command, but this edit action has
                // explicitly adopted the still-running login and must own its cleanup.
                editAction!.ConfirmEffect(PersistentPrefillEditResourceKind.Login);
            }
            outcome = PersistentPrefillEditActionOutcome.Succeeded;
            return Ok(challenge);
        }
        catch (OperationCanceledException)
        {
            outcome = PersistentPrefillEditActionOutcome.Cancelled;
            throw;
        }
        finally
        {
            editAction?.Complete(outcome);
        }
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
        // RC3: sessionId is REQUIRED - no fallback defaults - so a
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

        var editActionError = BeginEditAction(
            daemon!,
            request.EditSessionId,
            request.EditActionId,
            PersistentPrefillEditActionKind.Credential,
            session!.Id,
            out var editAction);
        if (editActionError is not null)
        {
            return editActionError;
        }

        var outcome = PersistentPrefillEditActionOutcome.Failed;
        try
        {
            await using var mutation = await daemon!.PersistentEditSessionGate.EnterMutationAsync(
                cancellationToken);
            await daemon!.ProvideCredentialAsync(
                session!.Id,
                request.Challenge,
                request.Credential,
                cancellationToken);
            editAction!.ConfirmEffect(PersistentPrefillEditResourceKind.Login);
            outcome = PersistentPrefillEditActionOutcome.Succeeded;
        }
        catch (DaemonCredentialRejectedException ex)
        {
            outcome = PersistentPrefillEditActionOutcome.Conflict;
            // RC4: a fixed daemon reported it had no matching
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
        catch (OperationCanceledException)
        {
            outcome = PersistentPrefillEditActionOutcome.Cancelled;
            throw;
        }
        finally
        {
            editAction?.Complete(outcome);
        }

        return Ok(new MessageOnlyResponse { Message = "Credential sent" });
    }

    /// <summary>
    /// Polls for the next credential challenge or login state.
    /// </summary>
    /// <remarks>
    /// Of the running persistent session. AccountHolder analogue of
    /// <c>GET {service}/sessions/{id}/challenge</c>. Delegates to
    /// <see cref="PrefillDaemonServiceBase.WaitForChallengeAsync(string, TimeSpan?, CancellationToken)"/>.
    /// </remarks>
    [HttpGet("challenge")]
    [ProducesResponseType(typeof(CredentialChallenge), StatusCodes.Status200OK)]
    public async Task<ActionResult<CredentialChallenge>> GetChallengeAsync(
        [FromQuery] PrefillPlatform service,
        [FromQuery] string? sessionId = null,
        [FromQuery] int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        // RC3: sessionId is REQUIRED - no fallback defaults - so a
        // poller pinned to session A can never be served session B's challenge after a stop/start race.
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return BadRequest(ApiResponse.Required("sessionId"));
        }

        var (daemon, session, error) = ResolveRunningPersistentSession(service, sessionId);
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
                return Ok(new PersistentLoginStatusResponse { SessionId = session.Id, Status = "logged-in" });
            }
            return NoContent();
        }

        // Stamp the resolved sessionId onto the challenge so the frontend can (re-)pin the flow (RC3).
        challenge.SessionId = session.Id;
        return Ok(challenge);
    }

    /// <summary>
    /// Cancels a pending interactive login and resets auth state.
    /// </summary>
    /// <remarks>
    /// For the running persistent session. AccountHolder analogue of the user cancel-login flow.
    /// Delegates to <see cref="PrefillDaemonServiceBase.CancelLoginAsync(string, CancellationToken)"/>.
    /// </remarks>
    [HttpPost("cancel-login")]
    [ProducesResponseType(typeof(MessageOnlyResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MessageOnlyResponse>> CancelLoginAsync(
        [FromBody] PersistentCancelLoginRequest request,
        CancellationToken cancellationToken)
    {
        // RC3: sessionId is REQUIRED - no fallback defaults.
        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return BadRequest(ApiResponse.Required("sessionId"));
        }

        var (daemon, session, error) = ResolveRunningPersistentSession(request.Service);
        if (error is not null)
        {
            return error;
        }

        // RC3: unlike the other pinned routes, a cancel for a since-replaced session is an idempotent
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

        await daemon!.CancelLoginAsync(session.Id, cancellationToken);

        return Ok(new MessageOnlyResponse { Message = "Login cancelled" });
    }

    /// <summary>
    /// Logs the running persistent session out in place.
    /// </summary>
    /// <remarks>
    /// The daemon forgets its stored account without the container being restarted. AccountHolder
    /// analogue of the other persistent-session routes. Delegates to
    /// <see cref="PrefillDaemonServiceBase.LogoutPersistentSessionAsync(string, CancellationToken)"/>.
    /// When the attempt genuinely fails (daemon reports failure, or the round-trip throws),
    /// <c>forgotten</c> is false and the frontend falls back to its existing stop+restart flow. NOTE:
    /// an un-updated steam/epic daemon image reports success here without actually deleting the
    /// stored account file - <c>forgotten:true</c> is not a hard guarantee on such images, and this
    /// endpoint has no way to detect that case; it self-resolves once the image is rebuilt.
    /// </remarks>
    [HttpPost("logout")]
    [ProducesResponseType(typeof(PersistentLogoutResponseDto), StatusCodes.Status200OK)]
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
    /// whose in-place logout (with a frontend stop+restart fallback) is intentionally NOT escalated;
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

    private async Task CleanupEditSessionServiceAsync(
        string editSessionId,
        string cleanupId,
        PersistentPrefillEditSessionCleanupServiceRequest request,
        PrefillDaemonServiceBase daemon,
        PersistentPrefillEditSessionCleanupLease lease,
        CancellationToken cancellationToken)
    {
        if (!lease.IsOwner)
        {
            await lease.Completion;
            return;
        }

        try
        {
            await Task.WhenAll(lease.PendingEditActions);

            var starts = new List<PersistentPrefillEditSessionStartRecord>();
            foreach (var pendingStart in lease.PendingStarts)
            {
                try
                {
                    starts.Add(await pendingStart);
                }
                catch (PersistentPrefillEditStartRollbackException rollback)
                {
                    await daemon.RollbackPersistentSessionStartForEditAsync(
                        rollback.SessionId,
                        rollback.ContainerId);
                }
                catch
                {
                    // A failed start produced no edit-session-owned session to compensate.
                }
            }

            await using var mutation = await daemon.PersistentEditSessionGate.EnterMutationAsync(
                cancellationToken);

            var createdStarts = starts.Where(start => start.CreatedByEditSession).ToArray();
            if (createdStarts.Length > 0)
            {
                var allStoppedOrAbsent = true;
                foreach (var start in createdStarts)
                {
                    allStoppedOrAbsent &= await StopEditSessionOwnedSessionAsync(
                        daemon,
                        editSessionId,
                        start);
                }

                if (allStoppedOrAbsent)
                {
                    daemon.PersistentEditSessionGate.CompleteCleanup(editSessionId, cleanupId);
                    return;
                }
            }

            var untrackedStartSessionId = request.StartSessionId;
            if (starts.Count == 0
                && !string.IsNullOrWhiteSpace(untrackedStartSessionId)
                && !string.Equals(
                    untrackedStartSessionId,
                    request.BaselineSessionId,
                    StringComparison.Ordinal))
            {
                if (await StopUntrackedSessionCreatedByEditAsync(daemon, untrackedStartSessionId))
                {
                    daemon.PersistentEditSessionGate.CompleteCleanup(editSessionId, cleanupId);
                    return;
                }
            }

            foreach (var ownership in daemon.PersistentEditSessionGate.GetCompensableResources(
                editSessionId,
                PersistentPrefillEditResourceKind.Prefill))
            {
                if (TryGetExactPersistentSession(daemon, ownership.SessionId, out var prefillSession)
                    && prefillSession.IsPrefilling)
                {
                    await daemon.CancelPrefillAsync(prefillSession.Id, cancellationToken);
                }

                daemon.PersistentEditSessionGate.CompleteCompensation(editSessionId, ownership);
            }

            foreach (var ownership in daemon.PersistentEditSessionGate.GetCompensableResources(
                editSessionId,
                PersistentPrefillEditResourceKind.Login))
            {
                if (TryGetExactPersistentSession(daemon, ownership.SessionId, out var loginSession))
                {
                    var status = await daemon.GetSessionStatusAsync(
                        loginSession.Id,
                        cancellationToken);
                    if (status?.Status == "logged-in")
                    {
                        var logout = await daemon.LogoutPersistentSessionAsync(
                            loginSession.Id,
                            cancellationToken);
                        if (!logout.LoggedOut)
                        {
                            throw new InvalidOperationException(
                                $"Edit-session-owned login could not be cleared from session {loginSession.Id}.");
                        }
                    }
                    else
                    {
                        await daemon.CancelLoginAsync(loginSession.Id, cancellationToken);
                    }
                }

                daemon.PersistentEditSessionGate.CompleteCompensation(editSessionId, ownership);
            }

            foreach (var ownership in daemon.PersistentEditSessionGate.GetCompensableResources(
                editSessionId,
                PersistentPrefillEditResourceKind.Selection))
            {
                if (TryGetExactPersistentSession(
                    daemon,
                    ownership.SessionId,
                    out var selectionSession))
                {
                    await daemon.SetSelectedAppsAsync(
                        selectionSession.Id,
                        request.BaselineSelectedAppIds,
                        cancellationToken);
                }

                daemon.PersistentEditSessionGate.CompleteCompensation(editSessionId, ownership);
            }

            daemon.PersistentEditSessionGate.CompleteCleanup(editSessionId, cleanupId);
        }
        catch (Exception ex)
        {
            daemon.PersistentEditSessionGate.FailCleanup(editSessionId, cleanupId, ex);
            throw;
        }
    }

    private static ActionResult? BeginEditAction(
        PrefillDaemonServiceBase daemon,
        string? editSessionId,
        string? editActionId,
        PersistentPrefillEditActionKind kind,
        string sessionId,
        out PersistentPrefillEditActionLease? editAction)
    {
        if (string.IsNullOrWhiteSpace(editSessionId) || string.IsNullOrWhiteSpace(editActionId))
        {
            if (!string.IsNullOrWhiteSpace(editSessionId)
                || !string.IsNullOrWhiteSpace(editActionId))
            {
                editAction = null;
                return new BadRequestObjectResult(
                    ApiResponse.Required("editSessionId and editActionId"));
            }
        }

        editAction = daemon.PersistentEditSessionGate.BeginEditAction(
            editSessionId,
            editActionId,
            kind,
            sessionId);
        return editAction.Accepted
            ? null
            : new ConflictObjectResult(
                ApiResponse.Error("This scheduled-prefill edit session is already being cleaned up."));
    }

    private static async Task<bool> StopEditSessionOwnedSessionAsync(
        PrefillDaemonServiceBase daemon,
        string editSessionId,
        PersistentPrefillEditSessionStartRecord start)
    {
        while (true)
        {
            var stopped = await daemon.StopPersistentSessionIfOwnedByEditAsync(
                start.SessionId,
                () => daemon.PersistentEditSessionGate.CanStopStartedSession(editSessionId, start),
                terminatedBy: "edit-session-cleanup");
            if (stopped || daemon.GetSession(start.SessionId) is not { IsPersistent: true })
            {
                return true;
            }

            if (daemon.PersistentEditSessionGate.HasPendingLaterStart(editSessionId, start))
            {
                throw new ConflictException(
                    $"Edit-session cleanup is waiting for a later persistent start before releasing session {start.SessionId}.");
            }

            if (!daemon.PersistentEditSessionGate.CanStopStartedSession(editSessionId, start))
            {
                // A completed later edit session adopted this exact session, so it now owns the container.
                return false;
            }

            // Ownership changed from unresolved to stoppable after the locked check. Retry under the
            // daemon's persistent-start lock so the exact session cannot be replaced mid-stop.
        }
    }

    private static async Task<bool> StopUntrackedSessionCreatedByEditAsync(
        PrefillDaemonServiceBase daemon,
        string sessionId)
    {
        while (true)
        {
            var stopped = await daemon.StopPersistentSessionIfOwnedByEditAsync(
                sessionId,
                () => daemon.PersistentEditSessionGate.CanStopUntrackedSession(sessionId),
                terminatedBy: "edit-session-cleanup");
            if (stopped || daemon.GetSession(sessionId) is not { IsPersistent: true })
            {
                return true;
            }

            if (daemon.PersistentEditSessionGate.HasPendingStart())
            {
                throw new ConflictException(
                    $"Edit-session cleanup is waiting for a persistent start before releasing session {sessionId}.");
            }

            if (!daemon.PersistentEditSessionGate.CanStopUntrackedSession(sessionId))
            {
                return false;
            }
        }
    }

    private static bool TryGetExactPersistentSession(
        PrefillDaemonServiceBase daemon,
        string? sessionId,
        out DaemonSession session)
    {
        session = null!;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var exact = daemon.GetSession(sessionId);
        if (exact is not { IsPersistent: true, Status: DaemonSessionStatus.Active })
        {
            return false;
        }

        session = exact;
        return true;
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

        // Re-anchor every running persistent session immediately so the new validity is the single
        // source of truth for the re-login date (and persist it so a restart keeps the new window).
        foreach (var daemon in PrefillDaemonServiceBase.ResolveAllDaemons(_serviceProvider))
        {
            await daemon.UpdatePersistentSessionExpiryAsync(validityDays);
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
                State = erroredSession ? PersistentSessionNotFoundState.Errored : PersistentSessionNotFoundState.NotStarted
            }));
        }

        if (!session.IsPersistent)
        {
            return (null, null, Forbid());
        }

        // RC3: when a caller pinned a specific session, refuse to
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

/// <summary>Identifies a platform for persistent session operations.</summary>
public sealed class PersistentServiceRequest
{
    public required PrefillPlatform Service { get; init; }
    public string? SessionId { get; init; }
}

/// <summary>Sets selected apps on the running persistent session.</summary>
public sealed class PersistentSelectedAppsRequest
{
    public required PrefillPlatform Service { get; init; }
    public string? SessionId { get; init; }
    public required List<string> AppIds { get; init; }
    public string? EditSessionId { get; init; }
    public string? EditActionId { get; init; }
}

/// <summary>Starts prefill on the running persistent session (mirrors guest StartPrefillRequest).</summary>
public sealed class PersistentStartPrefillRequest
{
    public required PrefillPlatform Service { get; init; }
    public string? SessionId { get; init; }
    public List<string>? AppIds { get; init; }
    public bool All { get; init; }
    public bool Recent { get; init; }
    public bool RecentlyPurchased { get; init; }
    public int? Top { get; init; }
    public bool Force { get; init; }
    public List<string>? OperatingSystems { get; init; }
    public int? MaxConcurrency { get; init; }
    public string? EditSessionId { get; init; }
    public string? EditActionId { get; init; }
}

/// <summary>Request body for starting a persistent session.</summary>
public sealed class StartPersistentSessionRequest
{
    /// <summary>Platform whose daemon should own the persistent session.</summary>
    public required PrefillPlatform Service { get; init; }

    public string? EditSessionId { get; init; }
    public string? EditActionId { get; init; }
}

/// <summary>
/// Request body for the persistent interactive-login routes that only need to identify the platform
/// (login start / cancel-login). The running persistent session is resolved server-side.
/// </summary>
public sealed class PersistentLoginRequest
{
    /// <summary>Platform whose running persistent session should be logged in / cancelled.</summary>
    public required PrefillPlatform Service { get; init; }
    public string? SessionId { get; init; }
    public string? EditSessionId { get; init; }
    public string? EditActionId { get; init; }
}

public sealed class PersistentPrefillEditSessionCleanupRequest
{
    public required string EditSessionId { get; init; }
    public required string CleanupId { get; init; }
    public required List<PersistentPrefillEditSessionCleanupServiceRequest> Services { get; init; }
}

public sealed class PersistentPrefillEditSessionCleanupServiceRequest
{
    public required PrefillPlatform Service { get; init; }
    public string? BaselineSessionId { get; init; }
    public required List<string> BaselineSelectedAppIds { get; init; }
    public string? StartSessionId { get; init; }
    public string? LoginSessionId { get; init; }
    public string? PrefillSessionId { get; init; }
    public string? SelectionSessionId { get; init; }
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

    /// <summary>
    /// Id of the persistent session this credential answers a challenge for. REQUIRED (RC3):
    /// a mismatch against the currently-active session yields a 409
    /// <c>session_replaced</c> rather than letting the credential land on a replacement session.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>The challenge being answered (same shape as the user credential route).</summary>
    public CredentialChallenge? Challenge { get; init; }

    /// <summary>The encrypted credential value (same shape as the user credential route).</summary>
    public string? Credential { get; init; }
    public string? EditSessionId { get; init; }
    public string? EditActionId { get; init; }
}

/// <summary>
/// Request body for cancelling a persistent interactive login. Carries the pinned session id so a
/// cancel for a since-replaced session (RC3) is an idempotent
/// no-op that never cancels the replacement session's login.
/// </summary>
public sealed class PersistentCancelLoginRequest
{
    /// <summary>Platform whose running persistent session's login should be cancelled.</summary>
    public required PrefillPlatform Service { get; init; }

    /// <summary>
    /// Id of the persistent session whose in-flight login should be cancelled. REQUIRED - a mismatch is
    /// treated as an idempotent no-op (200) that does not touch the currently-active session's login.
    /// </summary>
    public string? SessionId { get; init; }
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

    /// <summary>
    /// UTC instant the session was created. The validity window is anchored here rather than on the
    /// login, so the UI can show what a changed window would move the re-login date to before the
    /// change is saved (see <see cref="PrefillDaemonServiceBase.UpdatePersistentSessionExpiryAsync"/>).
    /// </summary>
    public required DateTime CreatedAtUtc { get; init; }

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
