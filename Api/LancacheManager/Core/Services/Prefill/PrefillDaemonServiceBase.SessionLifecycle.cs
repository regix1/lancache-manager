using Docker.DotNet;
using Docker.DotNet.Models;
using LancacheManager.Models;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;

namespace LancacheManager.Core.Services;

public abstract partial class PrefillDaemonServiceBase
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("{ServiceName}PrefillDaemonService starting...", ServiceName);

        // Initialize Docker client
        try
        {
            await ConnectDockerAsync(cancellationToken);

            // Ensure image is available. After a Docker timeout the pull falls back to the cached image, the
            // orphan sweep and the FullPersistence recreate keep going; any other startup Docker timeout ends
            // startup at the catch below, the client stays connected, and the next Start adopts or creates.
            if (_containerGateway.IsAvailable)
            {
                string? startupImageId = null;
                try
                {
                    startupImageId = await EnsureImageExistsAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not TimeoutException)
                {
                    _logger.LogWarning(ex,
                        "Could not resolve the current {ServiceName} image during startup; existing containers remain eligible for adoption",
                        ServiceName);
                }

                // Cleanup orphaned containers from previous runs
                await CleanupOrphanedContainersAsync(cancellationToken);

                // Re-adopt persistent containers that survived this restart (reconnect, don't recreate)
                _startupImageId = startupImageId;
                try
                {
                    await ReadoptPersistentContainersAsync(cancellationToken);
                }
                finally
                {
                    _startupImageId = null;
                }

                if (!string.IsNullOrWhiteSpace(startupImageId))
                {
                    try
                    {
                        await ReconcilePersistentImageAsync(ResolveImageName(), startupImageId, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is not TimeoutException)
                    {
                        _logger.LogWarning(ex,
                            "Persistent {ServiceName} image replacement failed during startup; the scheduled image check will retry",
                            ServiceName);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Log clean message without stack trace
            if (ex is TimeoutException)
            {
                _logger.LogWarning("Docker did not answer while {ServiceName} Prefill started; the client stays connected and the next Start adopts or creates.", ServiceName);
            }
            else
            {
                _logger.LogInformation("{ServiceName} Prefill startup was canceled.", ServiceName);
            }
            _logger.LogTrace(ex, "Docker initialization error details");
        }

        _logger.LogInformation("{ServiceName}PrefillDaemonService started. Docker available: {DockerAvailable}", ServiceName, _containerGateway.IsAvailable);
    }

    /// <summary>
    /// Connects the Docker client when it is not connected yet, and checks it with the version call. A Docker
    /// that is missing or does not answer in time is logged, and the gateway's <c>IsAvailable</c> stays
    /// false; each caller then gives its own refusal. Callers that arrive while a connect runs wait for that
    /// one, so none of them reaches Docker before its version answered. The connect runs on its own token,
    /// because the gateway bounds it; a caller's cancel ends only that caller's wait.
    /// </summary>
    private Task ConnectDockerAsync(CancellationToken cancellationToken)
    {
        if (_containerGateway.IsAvailable)
        {
            return Task.CompletedTask;
        }

        Task connect;
        lock (_dockerConnectSync)
        {
            // A connect can finish between the read above and this lock; it then leaves nothing to start.
            if (_containerGateway.IsAvailable)
            {
                return Task.CompletedTask;
            }

            // A connect that already ended failed, since IsAvailable is still false, so a new one starts.
            if (_dockerConnect is null || _dockerConnect.IsCompleted)
            {
                _dockerConnect = ConnectOnceAsync();
            }
            connect = _dockerConnect;
        }
        return connect.WaitAsync(cancellationToken);

        async Task ConnectOnceAsync()
        {
            Uri dockerUri;
            if (OperatingSystemDetector.IsWindows)
            {
                dockerUri = new Uri("npipe://./pipe/docker_engine");
            }
            else
            {
                dockerUri = new Uri("unix:///var/run/docker.sock");
            }

            if (!OperatingSystemDetector.IsWindows && !File.Exists("/var/run/docker.sock"))
            {
                _logger.LogWarning("Docker socket not found at /var/run/docker.sock. " +
                    "Mount the Docker socket to enable prefill containers: -v /var/run/docker.sock:/var/run/docker.sock");
            }

            try
            {
                var version = await _containerGateway.ConnectAsync(dockerUri, CancellationToken.None);
                _logger.LogInformation("Docker client connected. Docker version: {Version}", version.Version);
            }
            catch (Exception ex)
            {
                // Docker is often not running, so the stack trace goes to Trace only.
                _logger.LogWarning("{ServiceName} Prefill could not reach Docker; the next Start tries again. Start Docker, or mount /var/run/docker.sock into the LANCache Manager container, to enable it.", ServiceName);
                _logger.LogTrace(ex, "Docker connection error details");
            }
        }
    }

    // Ends the daemon round trips of a shutdown (logout, daemon shutdown) at the app's one stop deadline, so
    // the container stop and the database writes after them fit in Docker's 10 s stop time. Never canceled
    // until BeginStopBudget runs.
    private CancellationToken _daemonStopBudget;

    /// <summary>
    /// Starts the one stop deadline of the whole app and hands it to every prefill daemon service and to the
    /// scheduler. The host stops its services one after another, so a budget each service started when the
    /// host reached it would add up across services and run past Docker's 10 s stop time.
    /// </summary>
    public static void BeginStopBudget(IEnumerable<IHostedService> hostedServices)
    {
        var budget = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        foreach (var service in hostedServices)
        {
            if (service is PrefillDaemonServiceBase daemon)
                daemon._daemonStopBudget = budget.Token;
            else if (service is ScheduledPrefillService scheduler)
                scheduler.SetStopBudget(budget.Token);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("{ServiceName}PrefillDaemonService stopping...", ServiceName);

        // Close the creation gate before snapshotting _sessions: a create that completes after this point
        // is rejected at (or pulled back out by) its registration re-check.
        _stopping = true;

        await _persistentStartLock.WaitAsync(cancellationToken);
        try
        {
            // Read the effective persistence policy once for this shutdown pass. GetScheduledPrefillConfig
            // is synchronous and lock-safe on StateService (no IO, no cache to go stale), so it is safe to
            // call while the host is tearing this hosted service down.
            var config = _stateService.GetScheduledPrefillConfig();
            if (GetPendingImageChange() is { } pendingImageChange
                && !ShouldDetachOnShutdown(pendingImageChange.Session, config))
            {
                // A pending update Docker cannot finish stays pending; the other sessions still stop below.
                try
                {
                    await CancelPendingImageChangeAsync(
                        pendingImageChange,
                        terminatedBy: "system",
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Pending {ServiceName} image update could not be cancelled at shutdown", ServiceName);
                }
            }
            var sessions = _sessions.Values.ToList();

            // Detach KeepAcrossRestart / FullPersistence persistent sessions CONCURRENTLY: each detach is an
            // independent bounded drain + local-handle release (no shared mutable state, container untouched),
            // so several stuck drains share one shutdown-wide budget instead of costing the timeout x N
            // sequentially. The host cancellation token is threaded in so a cancelled shutdown cuts each drain
            // short. Guest / KillOnRestart terminations all start before any is awaited too: each stops its
            // container before its database writes, so a database that fails one write leaves no later
            // container running until the next start.
            var detachTasks = new List<Task>();
            var terminations = new List<Task>();
            foreach (var session in sessions)
            {
                if (ShouldDetachOnShutdown(session, config))
                {
                    detachTasks.Add(DetachPersistentSessionForShutdownAsync(session, cancellationToken));
                }
                else
                {
                    terminations.Add(TerminateSessionAsync(session.Id, "Service shutdown", reasonKey: ServiceShutdownReasonKey));
                }
            }

            await Task.WhenAll(detachTasks.Concat(terminations));

            // The next manager start reads the login state from the row, so a row that missed it gets one more write.
            // It runs after the containers are let go, and within the host's stop time, because a database that does not
            // answer would otherwise hold every container until the host kills the process.
            foreach (var session in sessions)
            {
                if (_loginStateRowsOutOfDate.ContainsKey(session.Id))
                {
                    await SaveLoginStateAsync(session, cancellationToken);
                }
            }
        }
        finally
        {
            _persistentStartLock.Release();
        }

        _logger.LogInformation("{ServiceName}PrefillDaemonService stopped", ServiceName);
    }

    /// <summary>
    /// Whether a session should be DETACHED (container left running for re-adoption) rather than
    /// terminated on manager shutdown. Guest sessions never detach. A persistent session detaches when
    /// its effective <see cref="PersistenceMode"/> is KeepAcrossRestart or FullPersistence.
    /// A null config is a should-not-happen path (the field is required and
    /// <see cref="ScheduledPrefillConfigFactory.Validate"/> guarantees it non-null in production): rather
    /// than throwing during host shutdown (which would abort teardown of the remaining sessions) or
    /// silently leaving a container running whose mode we cannot confirm, fall back to today's
    /// terminate + erase (KillOnRestart) and log it. This is a deliberate, logged exception to the
    /// no-fallback-defaults rule, justified by the shutdown context.
    /// </summary>
    private bool ShouldDetachOnShutdown(DaemonSession session, ScheduledPrefillConfigDto? config)
    {
        if (!session.IsPersistent)
        {
            return false;
        }

        if (config == null)
        {
            _logger.LogWarning(
                "Scheduled prefill config unavailable during {ServiceName} shutdown; terminating persistent session {SessionId} as KillOnRestart",
                ServiceName, session.Id);
            return false;
        }

        return config.GetEffectivePersistenceMode(Platform) != PersistenceMode.KillOnRestart;
    }

    /// <summary>
    /// Restart-persistence detach used at manager shutdown for a persistent session whose effective
    /// <see cref="PersistenceMode"/> is KeepAcrossRestart or FullPersistence. Leaves the container
    /// RUNNING with its login so the next start re-adopts it (see
    /// <see cref="ReadoptPersistentContainersAsync"/>). Releases ONLY the manager PROCESS's local handle:
    /// it drops the in-memory session and disposes the daemon client + CTS. It deliberately does NOT run
    /// <see cref="TerminateSessionAsync"/>, so it skips the daemon logout, the DB Active-&gt;Terminated
    /// flip (the row self-heals Active-&gt;Orphaned-&gt;Active on the next start), both SignalR
    /// termination broadcasts, the container stop/kill/remove, and the session command/response directory
    /// delete (re-adopt recomputes and reuses those exact paths from the session id).
    /// </summary>
    private async Task DetachPersistentSessionForShutdownAsync(DaemonSession session, CancellationToken cancellationToken = default)
    {
        // Remove from the in-memory store FIRST so a socket OnDisconnected raised while the client is
        // disposed below finds no matching session and therefore fires no EventSessionUpdated broadcast.
        if (!_sessions.TryRemove(session.Id, out var detached))
        {
            return;
        }

        _logger.LogInformation(
            "Detaching persistent {ServiceName} session {SessionId} on shutdown: leaving its container running for re-adoption on next start",
            ServiceName, session.Id);

        // Stop manager-side monitoring cleanly, drain any in-flight daemon event callbacks (bounded) so
        // none writes a DB row or broadcasts after this returns - pairing with the reference-equality
        // guard in the event handlers - then release this process's local handles. The container and the
        // daemon inside it are untouched and keep running.
        await DisposeClientWithDrainAsync(detached, cancellationToken);
    }

    /// <summary>
    /// Tears down a create that raced shutdown - rejected at the pre-registration gate or pulled back out
    /// by the post-registration re-check. Routes by the session's effective persistence mode: a
    /// KeepAcrossRestart / FullPersistence persistent session is DETACHED (its container is left running
    /// for the next start to re-adopt); a guest or KillOnRestart session has its fresh container REMOVED so
    /// nothing survives shutdown. Drops any in-memory registration first (no-op on the pre-gate path).
    /// </summary>
    internal async Task RejectEscapedCreateAsync(DaemonSession session, bool involuntaryRecreate, bool wasRegistered, CancellationToken cancellationToken = default, ScheduledPrefillConfigDto? config = null)
    {
        var removed = _sessions.TryRemove(session.Id, out var removedSession);

        // Single-owner teardown. A POST-registration reject (wasRegistered) races StopAsync's shutdown
        // snapshot for the same session; whoever wins the TryRemove owns the teardown. If shutdown already
        // claimed it (our TryRemove lost, or removed a different instance under the same id) do NOT dispose
        // the client or touch the container: shutdown's Detach/Terminate runs the full mode-correct teardown,
        // and a second DisposeClientWithDrainAsync would double-dispose the client while its Cancel() threw on
        // the already-disposed CTS. Release the create and let shutdown finish. A PRE-registration reject
        // (wasRegistered == false) is the SOLE owner of a never-registered session (StopAsync's snapshot can
        // never see it), so its TryRemove legitimately returns false and it must still tear the create down.
        if (wasRegistered && (!removed || !ReferenceEquals(removedSession, session)))
        {
            _logger.LogInformation(
                "{ServiceName} session {SessionId} create raced shutdown; shutdown already owns its teardown, releasing the create without disposing the client",
                ServiceName, session.Id);
            return;
        }

        // Reuse a config snapshot threaded from RollBackCreateIfShuttingDownAsync when present, else read it
        // once here (the pre/post-registration gate call sites pass none). Both the detach decision below and
        // the preserve decision inside TearDownRejectedCreateAsync then read the SAME snapshot.
        config ??= _stateService.GetScheduledPrefillConfig();
        if (ShouldDetachOnShutdown(session, config))
        {
            _logger.LogInformation(
                "Persistent {ServiceName} session {SessionId} create raced shutdown; detaching (leaving its container running for re-adoption)",
                ServiceName, session.Id);
            await DisposeClientWithDrainAsync(session, cancellationToken);
        }
        else
        {
            await TearDownRejectedCreateAsync(session, involuntaryRecreate, config);
        }
    }

    /// <summary>
    /// Final ownership re-check for a fresh create AFTER it has inserted its DB row, closing the one
    /// interleaving the post-registration re-check cannot: shutdown can flip <see cref="_stopping"/> and take
    /// its one-time <c>_sessions</c> snapshot AFTER that earlier re-check passed but while this create is
    /// still finalizing (DB insert + <c>EventSessionCreated</c> broadcast). Because the session was registered
    /// before that snapshot, shutdown's teardown claims it, yet the create would still leave a fresh Active
    /// row and broadcast a creation, resurrecting a session after termination completed. Returns true when the
    /// create must be rejected (the caller aborts before broadcasting). For terminate modes (guest /
    /// KillOnRestart) the container is being removed, so the just-inserted row is rolled back to Terminated;
    /// for detach modes (KeepAcrossRestart / FullPersistence) the container is intentionally left running for
    /// re-adoption, so the row is left Active exactly as a normal shutdown detach leaves it. A re-adopt
    /// (<paramref name="isReconnect"/>) is exempt: the startup re-adopt runs before StopAsync, and the Start,
    /// scheduled and Clear adopts hold _persistentStartLock, which StopAsync takes before its snapshot.
    /// </summary>
    internal async Task<bool> RollBackCreateIfShuttingDownAsync(
        DaemonSession session, bool involuntaryRecreate, bool isReconnect, CancellationToken cancellationToken)
    {
        // The ConcurrentDictionary registration already fenced store/load, but be explicit before re-reading
        // the volatile shutdown flag.
        Interlocked.MemoryBarrier();
        if (!_stopping || isReconnect)
        {
            return false;
        }

        var config = _stateService.GetScheduledPrefillConfig();
        var detach = ShouldDetachOnShutdown(session, config);

        _logger.LogWarning(
            "{ServiceName} session {SessionId} finished creating as shutdown began; rejecting it ({Mode}) so no Active row or creation broadcast outlives shutdown",
            ServiceName, session.Id, detach ? "detach" : "terminate");

        if (!detach)
        {
            // Terminate mode removes the container, so the just-inserted Active row must not survive.
            await _sessionService.TerminateSessionAsync(session.Id, "Create raced shutdown", terminatedBy: "system");
        }

        // Pass the config snapshot already read here so the reject chain does not re-read it (and cannot
        // decide off a different snapshot than the detach decision above).
        await RejectEscapedCreateAsync(session, involuntaryRecreate, wasRegistered: true, cancellationToken, config);
        return true;
    }

    /// <summary>
    /// Bounded, best-effort drain of a session's in-flight fire-and-forget daemon event callbacks, used
    /// during teardown (detach + terminate) before the client is disposed so a late status/progress event
    /// cannot write a DB row or broadcast after teardown declared completion. Never throws - a drain
    /// failure or timeout must not block or fault the shutdown/teardown.
    /// </summary>
    private async Task DrainSessionEventsAsync(DaemonSession session, CancellationToken cancellationToken = default)
    {
        try
        {
            await session.Client.DrainEventsAsync(_eventDrainTimeout, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error draining in-flight daemon events for session {SessionId} during teardown", session.Id);
        }
    }

    /// <summary>
    /// Creates a new daemon session for a user.
    /// Spawns a Docker container with dedicated command/response directories.
    /// </summary>
    public async Task<DaemonSession> CreateSessionAsync(
        Guid userId,
        string? ipAddress = null,
        string? userAgent = null,
        SessionType sessionType = SessionType.Admin,
        bool isPersistent = false,
        bool reuseExistingSession = true,
        DateTime? persistentExpiresAtOverrideUtc = null,
        bool involuntaryRecreate = false,
        CancellationToken cancellationToken = default,
        DateTime? persistentLastLoginAtUtc = null)
    {
        await ConnectDockerAsync(cancellationToken);
        if (!_containerGateway.IsAvailable)
        {
            throw new ServiceUnavailableException(
                "Docker is not running or not reachable. Start Docker, or mount /var/run/docker.sock into the LANCache Manager container, then try again.")
            {
                StageKey = "errors.prefill.dockerUnavailable"
            };
        }

        if (!isPersistent)
        {
            if (sessionType == SessionType.Guest)
            {
                // A Start whose create failed and whose cleanup Docker also failed is cleaned again here, so
                // the guest is not refused once Docker works. A cleanup that fails again was logged where it
                // failed, and the gate below still refuses this Start.
                var kept = GuestGate.GetStarts(userId)
                    .Where(keptStart => !keptStart.Active && !keptStart.Removed && keptStart.SessionId != null).ToArray();
                if (kept.Length != 0)
                {
                    try { await Task.WhenAll(kept.Select(keptStart => keptStart.Daemon.CleanGuestStartAsync(keptStart))); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Guest {UserId} cleanup of an earlier failed start is incomplete; unfinished stages remain available for retry", userId);
                    }
                    GuestGate.FinishStop(userId, cleanupPending: false);
                }
                var start = GuestGate.EnterCreate(userId, this);
                var cleanupPending = false;
                var completed = false;
                using var createCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, start.Cancellation.Token);
                try
                {
                    var guest = await _sessionService.GetGuestSessionAsync(userId);
                    var now = DateTime.UtcNow;
                    if (guest == null || guest.IsRevoked || guest.ExpiresAtUtc <= now
                        || !(Security.SessionService.GetPrefillExpiresAt(guest, Platform) > now))
                        throw new ForbiddenException("Session access is no longer valid.") { StageKey = "errors.prefill.accessEnded" };
                    GuestGate.Check(start);
                    var session = await CreateSessionCoreAsync(userId, ipAddress, userAgent, sessionType, isPersistent,
                        reuseExistingSession, persistentExpiresAtOverrideUtc, involuntaryRecreate, createCts.Token, guestStart: start);
                    GuestGate.Check(start);
                    if (!IsSessionLive(session))
                        throw new ForbiddenException("This prefill session was stopped.") { StageKey = "errors.prefill.sessionStopped" };
                    GuestGate.CompleteCreate(start);
                    completed = true;
                    return session;
                }
                catch (Exception ex)
                {
                    start.Registration.TrySetResult();
                    start.Publication.TrySetResult();
                    try { await CleanGuestStartAsync(start); }
                    catch (Exception) { cleanupPending = true; }
                    // A stop that canceled an await inside the create ends it as the stopped refusal. A create
                    // that failed for its own reason keeps that reason in the log.
                    try { GuestGate.Check(start); }
                    catch (ForbiddenException) when (ex is not (OperationCanceledException or ForbiddenException))
                    {
                        _logger.LogWarning(ex, "Guest {UserId} create failed and was then stopped", userId);
                        throw;
                    }
                    throw;
                }
                finally
                {
                    start.Registration.TrySetResult();
                    start.Publication.TrySetResult();
                    try { await start.CancellationTask; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Guest creation cancellation callback failed for {UserId}", userId);
                    }
                    if (!completed)
                        GuestGate.FinishCreate(start, cleanupPending);
                    start.Cancellation.Dispose();
                }
            }
            return await CreateSessionCoreAsync(userId, ipAddress, userAgent, sessionType, isPersistent, reuseExistingSession, persistentExpiresAtOverrideUtc, involuntaryRecreate, cancellationToken);
        }

        // Persistent starts are serialized per service (this instance) so two concurrent "Start
        // persistent session" calls can never both pass the reuse/adopt checks and each create a
        // container (leak M3): the second caller blocks here, then its reuse-check inside
        // CreateSessionCoreAsync finds the session the first caller just created/adopted. Guest
        // sessions take no lock - multiple concurrent guest sessions are expected.
        var persistentImageId = await EnsureImageExistsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(persistentImageId))
        {
            throw new InvalidOperationException($"Docker returned no immutable image ID for {ResolveImageName()}.");
        }

        await _persistentStartLock.WaitAsync(cancellationToken);
        try
        {
            return await CreateSessionCoreAsync(
                userId,
                ipAddress,
                userAgent,
                sessionType,
                isPersistent,
                reuseExistingSession,
                persistentExpiresAtOverrideUtc,
                involuntaryRecreate,
                cancellationToken,
                resolvedImageId: persistentImageId,
                persistentLastLoginAtUtc: persistentLastLoginAtUtc);
        }
        finally
        {
            _persistentStartLock.Release();
        }
    }


    /// <summary>
    /// Reconnects to a running persistent container as its session; each caller first checks that the
    /// session was not stopped. Returns the active persistent session, or null when the container was not
    /// adopted. A Docker timeout says nothing about the container, and a caller's cancel is not an adopt
    /// failure, so both end the caller and the container stays.
    /// </summary>
    private async Task<DaemonSession?> AdoptPersistentContainerAsync(
        ContainerListResponse target, PersistentImageChange? imageChange, CancellationToken cancellationToken)
    {
        try
        {
            await ReconnectPersistentSessionAsync(target, startupReadopt: false, cancellationToken, imageChange);
        }
        catch (Exception ex) when (ex is not TimeoutException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Failed to adopt existing persistent {ServiceName} container {Id}",
                ServiceName, ShortContainerId(target.ID));
        }

        return GetActivePersistentSession();
    }

    private async Task<DaemonSession> CreateSessionCoreAsync(
        Guid userId,
        string? ipAddress,
        string? userAgent,
        SessionType sessionType,
        bool isPersistent,
        bool reuseExistingSession,
        DateTime? persistentExpiresAtOverrideUtc,
        bool involuntaryRecreate,
        CancellationToken cancellationToken,
        Func<string, string, string, Task>? persistentContainerCreated = null,
        GuestPrefillStart? guestStart = null,
        PersistentImageChange? imageChange = null,
        string? resolvedImageId = null,
        DateTime? persistentLastLoginAtUtc = null)
    {
        if (isPersistent && imageChange is null && !string.IsNullOrWhiteSpace(resolvedImageId))
        {
            await ReconcilePersistentImageUnderStartLockAsync(ResolveImageName(), resolvedImageId, cancellationToken);
        }

        // Check if user already has an active session - return it instead of creating a new one.
        // This match is userId-keyed and only safe because every persistent create path derives userId
        // via DeriveSystemUserId (a single stable system-user id) and always passes
        // reuseExistingSession:true - see PersistentSingletonGates and its callers. A future caller that
        // creates a persistent session for a different, per-admin userId must not reach this reuse check
        // as-is, or it would collide with (and silently return) another admin's persistent session.
        if (reuseExistingSession)
        {
            var existingSession = _sessions.Values.FirstOrDefault(s => s.UserId == userId && s.Status == DaemonSessionStatus.Active);
            if (existingSession != null)
            {
                _logger.LogInformation("Returning existing active session {SessionId} for user {UserId}", existingSession.Id, userId);
                return existingSession;
            }
        }

        // Enforce UserId-based bans at session-create time. For anonymous services (e.g. Battle.net)
        // there is no credential step, so this is the only point at which a ban can be enforced.
        // Username-based (Steam/Epic) bans continue to be enforced at credential-provide time.
        if (await _sessionService.IsUserIdBannedAsync(userId))
        {
            _logger.LogWarning("Refusing to create {ServiceName} session for banned user {UserId}", ServiceName, userId);
            throw new ForbiddenException("You are banned from using the prefill feature.")
            {
                StageKey = "errors.prefill.banned"
            };
        }

        resolvedImageId ??= await EnsureImageExistsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(resolvedImageId))
        {
            throw new InvalidOperationException($"Docker returned no immutable image ID for {ResolveImageName()}.");
        }

        // Whether the fresh-login guard should preserve a FullPersistence volume login: true for a
        // caller-declared involuntary recreate (startup outage-recreate) OR an in-place error-state
        // replacement (an errored session, not an admin stop). The flag keeps this Start's decision
        // independent of the row, so it holds even when the Orphaned write below finds no row; the
        // Orphaned write covers a later Start.
        var involuntaryReplacement = false;
        DaemonSession? replacedSession = null;

        if (isPersistent)
        {
            // A teardown that failed (a person's Stop, or an errored session's replacement that keeps its login)
            // left the session out of every list, so this Start finishes it first, with its own reason and login
            // choice, before deciding what to create.
            List<string> failedTeardownIds;
            lock (_terminationSync)
            {
                failedTeardownIds = _terminations.Values
                    .Where((SessionTermination termination) =>
                        IsFailedPersistentTeardown(termination) && termination.ImageChange is null)
                    .Select((SessionTermination termination) => termination.Session.Id)
                    .ToList();
            }
            foreach (var failedTeardownId in failedTeardownIds)
            {
                await TerminateSessionAsync(failedTeardownId);
            }

            // Error-state replacement (kills leak M2): a socket-disconnect flips a persistent session
            // to Error without tearing its container down; the reuse-check above only matches Active,
            // so left alone every future Start would try to create a duplicate alongside it. Replace
            // it in place instead.
            var existingErrorSession = _sessions.Values.FirstOrDefault(s => PersistentSingletonGates.ShouldReplaceErroredSession(s));
            if (existingErrorSession != null)
            {
                _logger.LogInformation(
                    "Existing persistent {ServiceName} session {SessionId} is in Error state; replacing it",
                    ServiceName, existingErrorSession.Id);
                // The session errored without a person's stop (a lost socket, or a sign-in or download cancel
                // the daemon never confirmed), so under FullPersistence a later Start that finds this row
                // keeps the login, also when the removal below fails.
                try
                {
                    await TerminateSessionAsync(existingErrorSession.Id, "Replacing errored persistent session", force: true, keepLogin: true);
                }
                finally
                {
                    await _sessionService.MarkOrphanedAsync(existingErrorSession.Id);
                }
                involuntaryReplacement = true;
                replacedSession = existingErrorSession;
            }

            // Docker-level adopt-or-replace pre-create check (kills leak M1's leftover/409 path):
            // an unadopted-but-running container for this service should be reconnected to instead of
            // shadowed by a second container; a stopped one should be removed first so the
            // deterministic name is free.
            var decision = await ResolvePersistentContainerDecisionAsync(cancellationToken);

            if (imageChange?.CreatedContainerId is { Length: > 0 } pendingContainerId)
            {
                var candidates = decision.ExtrasToRemove
                    .Append(decision.Target)
                    .Where(container => container is not null)
                    .Cast<ContainerListResponse>()
                    .ToArray();
                if (candidates.Any(container => !string.Equals(container.ID, pendingContainerId, StringComparison.Ordinal)))
                {
                    throw new ConflictException(
                        $"A different persistent {ServiceName} container appeared while an image update was pending.")
                    {
                        StageKey = "errors.prefill.sessionReplaced",
                        Context = new() { ["sessionId"] = imageChange.Session.Id }
                    };
                }
            }

            foreach (var extra in decision.ExtrasToRemove)
            {
                _logger.LogInformation(
                    "Removing extra leaked persistent {ServiceName} container {Id}",
                    ServiceName, ShortContainerId(extra.ID));
                await RemoveContainerForceAsync(extra.ID, cancellationToken);
            }

            if (decision.Action == PersistentContainerAction.Adopt)
            {
                var target = decision.Target!;
                if (await IsStoppedSessionContainerAsync(target))
                {
                    _logger.LogInformation(
                        "Removing running persistent {ServiceName} container {Id}; its session {SessionId} was stopped",
                        ServiceName, ShortContainerId(target.ID), target.Labels[SessionIdLabelKey]);
                }
                else
                {
                    var adopted = await AdoptPersistentContainerAsync(target, imageChange, cancellationToken);
                    if (adopted != null)
                    {
                        return adopted;
                    }

                    _logger.LogWarning(
                        "Adoption of persistent {ServiceName} container {Id} did not produce an active session; removing it and creating fresh",
                        ServiceName, ShortContainerId(target.ID));
                }

                // The container holds the fixed name with no session. Remove it, then create a fresh one below.
                await RemoveContainerForceAsync(target.ID, cancellationToken);
            }
            else if (decision.Action == PersistentContainerAction.Remove)
            {
                _logger.LogInformation(
                    "Removing stopped persistent {ServiceName} container {Id} before creating fresh",
                    ServiceName, ShortContainerId(decision.Target!.ID));
                await RemoveContainerForceAsync(decision.Target!.ID, cancellationToken);
            }
            else if (decision.Action == PersistentContainerAction.RetryLater)
            {
                // ResolvePersistentContainerDecisionAsync's bounded retries were exhausted while a
                // matching container was still mid-removal or restarting. Falling through to create
                // here would either collide with Docker's own in-flight removal for the deterministic
                // name (a 409 that CreatePersistentContainerWithConflictRetryAsync would then have to
                // force through) or force-remove a container that might still recover from a restart
                // loop. Fail the start explicitly instead - the name will be free (or the container
                // will have stabilized) by the time the admin/scheduler retries. Persistent starts are
                // only ever requested over HTTP, so a conflict carries the retry-shortly sentence to the
                // caller instead of collapsing into the middleware's generic 500 body.
                throw new ConflictException(
                    $"An existing persistent {ServiceName} container is still being removed or restarting. Please try again shortly.")
                {
                    StageKey = "errors.prefill.containerBusy",
                    Context = new() { ["service"] = ServiceName }
                };
            }
        }

        // An image update's earlier attempt is over once its container is gone or was removed above. It ended
        // without a person's Stop, so its row reads Orphaned, never Terminated, and FullPersistence keeps the login.
        // The next attempt takes the earlier attempt's id, so one update keeps one row, unless a teardown still
        // holds that id.
        string? reusedAttemptId = null;
        if (imageChange?.CreatedSessionId is { Length: > 0 } earlierAttemptId)
        {
            await _sessionService.MarkOrphanedAsync(earlierAttemptId);
            lock (_terminationSync)
            {
                if (!_terminations.ContainsKey(earlierAttemptId))
                {
                    reusedAttemptId = earlierAttemptId;
                }
            }
        }

        var sessionId = reusedAttemptId ?? Guid.NewGuid().ToString("N")[..16];
        var basePath = GetDaemonBasePath();
        var sessionPath = Path.Combine(basePath, "sessions", sessionId);
        var commandsDir = Path.Combine(sessionPath, "commands");
        var responsesDir = Path.Combine(sessionPath, "responses");

        // The removed attempt's socket file would answer the new container's first connect.
        if (reusedAttemptId is not null && Directory.Exists(sessionPath))
        {
            Directory.Delete(sessionPath, recursive: true);
        }

        if (guestStart != null)
        {
            guestStart.SessionId = sessionId;
            guestStart.ContainerName = $"{ContainerPrefix}{sessionId}";
            guestStart.Directory = sessionPath;
            GuestGate.Check(guestStart);
        }

        // Create directories inside this container
        Directory.CreateDirectory(commandsDir);
        Directory.CreateDirectory(responsesDir);

        // For Docker bind mounts, we need to translate container paths to host paths
        // /data inside this container maps to the host's data directory
        var hostDataPath = await GetHostDataPathAsync(cancellationToken);
        var hostCommandsDir = commandsDir;
        var hostResponsesDir = responsesDir;
        var containerDataRoot = _pathResolver.GetDataDirectory();
        if (!string.IsNullOrEmpty(hostDataPath) &&
            _isRunningInContainer &&
            commandsDir.StartsWith(containerDataRoot, StringComparison.OrdinalIgnoreCase))
        {
            hostCommandsDir = commandsDir.Replace(containerDataRoot, hostDataPath, StringComparison.OrdinalIgnoreCase);
            hostResponsesDir = responsesDir.Replace(containerDataRoot, hostDataPath, StringComparison.OrdinalIgnoreCase);
        }

        _logger.LogInformation("Creating daemon container for session {SessionId}, user {UserId}", sessionId, userId);
        _logger.LogDebug("Container paths: commands={CommandsDir}, responses={ResponsesDir}", commandsDir, responsesDir);
        _logger.LogDebug("Host paths: commands={HostCommandsDir}, responses={HostResponsesDir}", hostCommandsDir, hostResponsesDir);

        // Create and start container. Persistent containers get a deterministic name (one per
        // service) so Docker itself enforces the singleton via a 409 name conflict if this fix's
        // pre-create adopt-or-replace check above somehow missed a concurrent creator; guest/temporary
        // containers keep the unique sessionId-based name so many can run at once.
        var containerName = isPersistent ? $"{ContainerPrefix}persistent" : $"{ContainerPrefix}{sessionId}";
        var imageName = ResolveImageName();

        // Get network configuration for prefill container
        // Auto-detect from lancache-dns container if not explicitly configured
        var useHostNetworking = await ShouldUseHostNetworkingAsync(cancellationToken);
        var lancacheDnsIp = useHostNetworking ? null : await _locator.DetectDnsContainerBridgeIpAsync(cancellationToken);
        var explicitNetworkMode = GetNetworkMode();

        // Build host config with proper network settings
        var binds = new List<string>
        {
            $"{hostCommandsDir}:/commands",
            $"{hostResponsesDir}:/responses"
        };

        // Persistent sessions: pin the daemon's auth/config dir (declared as an anonymous VOLUME
        // by the image) to a STABLE NAMED volume keyed by service. This survives teardown
        // (RemoveVolumes=false on persistent stop) so the daemon's OWN login persists INSIDE the
        // container's volume across container/manager restarts. The manager never injects or transfers
        // any token - the daemon authenticates itself from this volume; otherwise the admin logs in
        // interactively. Temporary/guest containers keep the default anonymous volume and are wiped.
        if (isPersistent)
        {
            binds.Add($"{GetPersistentConfigVolumeName()}:{PersistentConfigContainerPath}");
            _logger.LogInformation(
                "Persistent session {SessionId}: mounting named auth volume {Volume} at {Path}",
                sessionId, GetPersistentConfigVolumeName(), PersistentConfigContainerPath);
        }

        var hostConfig = new HostConfig
        {
            Binds = binds,
            AutoRemove = false  // Temporarily disabled for debugging socket disconnection
        };

        // Disable IPv6 to ensure DNS queries go through IPv4 lancache-dns
        // This prevents IPv6 DNS bypass which can cause prefill to miss the cache
        // Note: Sysctls are not allowed with host networking mode
        var shouldDisableIpv6 = !useHostNetworking &&
                                (string.IsNullOrEmpty(explicitNetworkMode) ||
                                 !explicitNetworkMode.Equals("host", StringComparison.OrdinalIgnoreCase));
        if (shouldDisableIpv6)
        {
            hostConfig.Sysctls = new Dictionary<string, string>
            {
                ["net.ipv6.conf.all.disable_ipv6"] = "1"
            };
        }

        // Determine network configuration strategy:
        // 1. If explicitly configured with NetworkMode, use that
        // 2. If lancache-dns uses host networking, use host mode (auto-detected)
        // 3. Otherwise use default networking
        // DNS is always configured when available (except for host mode which inherits host DNS)
        if (!string.IsNullOrEmpty(explicitNetworkMode))
        {
            hostConfig.NetworkMode = explicitNetworkMode;
            _logger.LogInformation("Configuring prefill container network mode (explicit): {NetworkMode}", explicitNetworkMode);
        }
        else if (useHostNetworking)
        {
            hostConfig.NetworkMode = "host";
            _logger.LogInformation("Configuring prefill container to use host networking (auto-detected from lancache-dns)");
        }

        // Configure DNS for non-host network modes
        var isHostMode = useHostNetworking ||
                         (!string.IsNullOrEmpty(explicitNetworkMode) &&
                          explicitNetworkMode.Equals("host", StringComparison.OrdinalIgnoreCase));

        if (!isHostMode && !string.IsNullOrEmpty(lancacheDnsIp))
        {
            hostConfig.DNS = new List<string> { lancacheDnsIp };
            _logger.LogInformation("Configuring prefill container DNS to use lancache-dns: {DnsIp}", lancacheDnsIp);
        }
        else if (!isHostMode && string.IsNullOrEmpty(lancacheDnsIp))
        {
            _logger.LogWarning("Could not auto-detect lancache-dns configuration. Prefill may fail if the host's DNS " +
                "doesn't resolve CDN to lancache. Set Prefill__LancacheDnsIp or Prefill__NetworkMode=host.");
        }

        // Socket path for Unix Domain Socket communication
        var socketPath = Path.Combine(responsesDir, "daemon.sock");
        var useTcpMode = ShouldUseTcpMode();
        var tcpContainerPort = useTcpMode ? GetContainerTcpPort() : (int?)null;
        var tcpHostPort = useTcpMode ? GetHostTcpPort() : (int?)null;

        // Build command and environment for daemon mode
        var cmd = new List<string> { "daemon" };

        var env = new List<string>
        {
            $"PREFILL_COMMANDS_DIR=/commands",
            $"PREFILL_RESPONSES_DIR=/responses"
        };

        var socketSecret = GenerateSocketSecret();
        env.Add($"PREFILL_SOCKET_SECRET={socketSecret}");
        _logger.LogInformation("Passing generated socket secret to prefill daemon container");

        // GUEST/temporary containers get a hard lifetime cap so an abandoned anonymous session
        // self-terminates inside the daemon. Persistent/admin containers are left indefinite
        // (no cap) - their lifecycle is governed by NeedsRelogin + admin teardown.
        if (sessionType == SessionType.Guest)
        {
            var maxLifetimeSeconds = GetGuestPermissionDurationHours() * 3600;
            env.Add($"PREFILL_MAX_LIFETIME_SECONDS={maxLifetimeSeconds}");
            _logger.LogInformation(
                "Guest session {SessionId}: capping daemon lifetime at {Seconds}s",
                sessionId, maxLifetimeSeconds);
        }

        // Inject LANCACHE_IP unconditionally for both host and bridge mode.
        // The daemon honors this env var to bypass container DNS for CDN traffic
        // (URL-rewrite + Host-header spoof). It is NOT a fallback for HostConfig.DNS -
        // they serve different purposes and may be used together or independently.
        // Delegated to the shared locator: config (Prefill__LancacheIp literal/hostname) ->
        // heartbeat-verified answer the detected lancache DNS advertises for the test domain ->
        // lancache-dns .env/docker-inspect LANCACHE_IP -> heartbeat-verified auto-detect. Passing
        // includeHostSideCandidates:true additionally probes the Docker bridge gateway and
        // host.docker.internal, so a HOST-NETWORKED lancache box now auto-detects too. Loopback is
        // never a cache candidate, so it can never be injected here.
        var lancacheLocation = await _locator.LocateAsync(includeHostSideCandidates: true, cancellationToken);
        var lancacheIp = lancacheLocation.CacheIps.FirstOrDefault();
        _lastInjectedLancacheIp = string.IsNullOrWhiteSpace(lancacheIp) ? null : lancacheIp;
        _lastLancacheIpSource = lancacheLocation.Source;
        if (!string.IsNullOrWhiteSpace(lancacheIp))
        {
            env.Add($"LANCACHE_IP={lancacheIp}");
            _logger.LogInformation(
                "Injecting LANCACHE_IP={Ip} into prefill daemon (source: {Source}) - DNS-independent CDN routing",
                lancacheIp, lancacheLocation.Source);
        }
        else if (string.Equals(lancacheLocation.Source, "config", StringComparison.OrdinalIgnoreCase))
        {
            // Prefill__LancacheIp is configured (non-whitespace) but could not be resolved to an IP.
            // Preserve the original hard-fail contract (the old ResolveLancacheServerIpAsync threw)
            // rather than silently starting a daemon that would route CDN traffic nowhere (H4).
            var configured = _networkOptions.CurrentValue.LancacheIp;
            throw new ServiceUnavailableException(
                $"Prefill__LancacheIp='{configured}' could not be resolved to an IP address.")
            {
                StageKey = "errors.prefill.lancacheIpUnresolved"
            };
        }
        else
        {
            _logger.LogWarning(
                "Prefill__LancacheIp is not set. The daemon will rely on container DNS to resolve CDN hostnames, " +
                "which may fail in host networking mode or with non-lancache DNS chains. " +
                "Set Prefill__LancacheIp=<your-lancache-server-ip> for reliable operation.");
        }

        if (useTcpMode && tcpContainerPort.HasValue)
        {
            env.Add($"PREFILL_TCP_PORT={tcpContainerPort.Value}");
            _logger.LogInformation("Creating daemon container for session {SessionId} using TCP mode (host port {HostPort})",
                sessionId, tcpHostPort);
        }
        else
        {
            env.Add("PREFILL_USE_SOCKET=true");
            env.Add("PREFILL_SOCKET_PATH=/responses/daemon.sock");
            _logger.LogInformation("Creating daemon container for session {SessionId} using socket mode", sessionId);
        }

        if (useTcpMode && tcpContainerPort.HasValue && tcpHostPort.HasValue)
        {
            hostConfig.PortBindings = new Dictionary<string, IList<PortBinding>>
            {
                [$"{tcpContainerPort.Value}/tcp"] = new List<PortBinding>
                {
                    new() { HostPort = tcpHostPort.Value.ToString(), HostIP = "127.0.0.1" }
                }
            };
        }

        // Label PERSISTENT containers so a manager restart can re-adopt them (re-attach to the still
        // running daemon socket and rebuild the in-memory session) instead of force-removing them in
        // CleanupOrphanedContainersAsync. Temporary/guest containers are intentionally left unlabeled so
        // they keep being reaped as orphans.
        Dictionary<string, string>? containerLabels = null;
        if (isPersistent)
        {
            containerLabels = new Dictionary<string, string>
            {
                [PersistentLabelKey] = "true",
                [ServiceLabelKey] = ServiceName,
                [SessionIdLabelKey] = sessionId,
                [UserIdLabelKey] = userId.ToString()
            };
        }

        var createParameters = new CreateContainerParameters
        {
            Name = containerName,
            Image = resolvedImageId,
            Cmd = cmd,
            Env = env,
            HostConfig = hostConfig,
            Labels = containerLabels,
            ExposedPorts = useTcpMode && tcpContainerPort.HasValue
                ? new Dictionary<string, EmptyStruct> { [$"{tcpContainerPort.Value}/tcp"] = default }
                : null
        };

        if (guestStart != null)
        {
            GuestGate.Check(guestStart);
            cancellationToken.ThrowIfCancellationRequested();
            guestStart.CreateDispatched = true;
        }
        CreateContainerResponse createResponse;
        try
        {
            createResponse = isPersistent
                ? await CreatePersistentContainerWithConflictRetryAsync(createParameters, cancellationToken)
                : await _containerGateway.CreateContainerAsync(createParameters, cancellationToken);
        }
        catch (TimeoutException) when (guestStart is null && !isPersistent)
        {
            // Docker may have made the container before the create gave up, and an own session has no cleanup
            // by name elsewhere, so it and its volumes are removed here. The timeout stays the answer.
            try
            {
                await ForceRemoveContainersByExactNameAsync(containerName, CancellationToken.None, removeVolumes: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove container {ContainerName} after its create timed out", containerName);
            }
            DeleteFailedCreateDirectory();
            throw;
        }

        var containerId = createResponse.ID;
        if (guestStart != null)
        {
            guestStart.ContainerId = containerId;
            GuestGate.Check(guestStart);
        }
        _logger.LogInformation("Created container {ContainerId} for session {SessionId}", containerId, sessionId);
        if (persistentContainerCreated is not null)
        {
            await persistentContainerCreated(sessionId, containerId, containerName);
        }

        try
        {
            var started = await _containerGateway.StartContainerAsync(containerId, null, cancellationToken);
            if (guestStart != null)
                GuestGate.Check(guestStart);
            if (!started)
            {
                throw new InvalidOperationException($"Failed to start container {containerId}");
            }

            _logger.LogInformation("Started container {ContainerId} for session {SessionId}", containerId, sessionId);

            // Verify container is actually running (it may have crashed immediately)
            await Task.Delay(1000, cancellationToken); // Give it a moment to crash if it's going to
            try
            {
                var inspect = await _containerGateway.InspectContainerAsync(containerId, cancellationToken);
                if (!inspect.State.Running)
                {
                    var exitCode = inspect.State.ExitCode;
                    var error = inspect.State.Error;
                    _logger.LogError("Container {ContainerId} exited immediately! ExitCode: {ExitCode}, Error: {Error}",
                        containerId, exitCode, error);

                    try
                    {
                        var logParams = new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Tail = "50" };
                        using var logStream = await _containerGateway.GetContainerLogsAsync(containerId, false, logParams, cancellationToken);
                        using var memoryStream = new MemoryStream();
                        await logStream.CopyOutputToAsync(null, memoryStream, null, cancellationToken);
                        memoryStream.Position = 0;
                        using var reader = new StreamReader(memoryStream);
                        var logs = await reader.ReadToEndAsync(cancellationToken);
                        if (!string.IsNullOrWhiteSpace(logs))
                        {
                            _logger.LogError("Container logs:\n{Logs}", logs);
                        }
                        else
                        {
                            _logger.LogError("Container produced no logs before exiting");
                        }
                    }
                    catch (Exception logEx)
                    {
                        _logger.LogWarning(logEx, "Could not retrieve container logs");
                    }

                    throw new ServiceUnavailableException($"Container crashed on startup. ExitCode: {exitCode}. Check if image '{imageName}' exists and is valid.")
                    {
                        StageKey = "errors.prefill.containerExitedOnStart"
                    };
                }

                _logger.LogInformation("Container {ContainerId} verified running for session {SessionId}", containerId, sessionId);
            }
            catch (DockerContainerNotFoundException)
            {
                _logger.LogError("Container {ContainerId} was removed before we could verify it (crashed immediately with AutoRemove). " +
                    "Image '{ImageName}' may not exist or is crashing on startup.", containerId, imageName);
                throw new ServiceUnavailableException($"Container crashed immediately. Ensure image '{imageName}' exists and is properly configured.")
                {
                    StageKey = "errors.prefill.containerExitedOnStart"
                };
            }

            // Check internet and DNS from inside the container so a broken network shows on the session.
            var networkDiagnostics = await TestContainerConnectivityAsync(containerId, containerName, isHostMode, cancellationToken);

            // Guests end at the admin's per-service duration, never at the shorter standard timeout, which would
            // cut a duration above 2 hours short. Own sessions use the standard timeout; persistent sessions date
            // from their last sign-in below.
            var createdAtUtc = DateTime.UtcNow;
            var isTemporary = sessionType == SessionType.Guest;
            var standardExpiresAt = createdAtUtc.AddMinutes(GetSessionTimeoutMinutes());
            DateTime expiresAt;
            // A persistent create carries the sign-in time of the life it replaces: an image update and a
            // FullPersistence recreate pass it, an errored session's replacement reads it from that session, and
            // any other persistent create reads it from the latest row that a person's Stop or a login removal
            // has not ended. A create with none of these is its own first sign-in.
            var carriedLastLoginAtUtc = persistentLastLoginAtUtc ?? replacedSession?.LastLoginAt;
            if (carriedLastLoginAtUtc is null && isPersistent
                && await _sessionService.GetLatestPersistentSessionAsync(Platform) is { Status: not PrefillSessionStatus.Terminated } latestRow)
            {
                carriedLastLoginAtUtc = latestRow.LastLoginAtUtc;
            }
            var lastLoginAtUtc = carriedLastLoginAtUtc ?? createdAtUtc;
            if (isPersistent)
            {
                // Persistent admin login: expiry governs when NeedsRelogin is flagged (the reaper never
                // tears a persistent session down). An image update and a FullPersistence startup recreate pass
                // the stored date of the life they replace, past or not; every other persistent create dates
                // its window from the sign-in time above, so no create extends the admin-configured validity.
                expiresAt = persistentExpiresAtOverrideUtc
                    ?? ComputePersistentExpiry(lastLoginAtUtc, _stateService.GetAdminPersistentLoginValidityDays());
            }
            else if (isTemporary)
            {
                expiresAt = createdAtUtc.AddHours(GetGuestPermissionDurationHours());
            }
            else
            {
                expiresAt = standardExpiresAt;
            }

            var session = await ConnectAndRegisterSessionAsync(
                sessionId,
                userId,
                containerId,
                containerName,
                commandsDir,
                responsesDir,
                socketPath,
                socketSecret,
                useTcpMode,
                tcpHostPort,
                createdAtUtc,
                lastLoginAtUtc,
                expiresAt,
                isTemporary,
                isPersistent,
                ipAddress,
                userAgent,
                networkDiagnostics,
                isReconnect: false,
                startupReadopt: false,
                involuntaryRecreate: involuntaryRecreate || involuntaryReplacement,
                cancellationToken,
                guestStart,
                imageChange,
                configuredImage: imageName,
                imageId: resolvedImageId,
                replacedSession: replacedSession);

            return session;
        }
        catch (Exception) when (guestStart is null && imageChange is null && !_stopping && !_sessions.ContainsKey(sessionId))
        {
            // A persistent or own-session create that ended before its session registered removes its
            // container; an own session also removes its volumes. A persistent Start's next Start then
            // creates a fresh one and the fresh-login guard decides about the saved login, rather than
            // adopting this one as a reconnect that never signs in. No daemon logout, so the persistent
            // named volume keeps that login, and the removal finishes even when this create was canceled.
            // Guests have their own name-based cleanup, shutdown tears down its own creates, an image update
            // adopts the container it created, and a teardown that already took the session (a person's End
            // during the create) owns its removal. A removal Docker refuses or does not answer was
            // logged by the helper; the create's own failure is the answer.
            lock (_terminationSync)
            {
                if (_terminations.ContainsKey(sessionId))
                    throw;
            }
            try
            {
                // A removal still in progress keeps the folder, since the container may still use it.
                if (await RemoveContainerForceAsync(containerId, CancellationToken.None, removeVolumes: !isPersistent)
                    != ContainerRemovalOutcome.RemovalInProgress)
                {
                    DeleteFailedCreateDirectory();
                }
            }
            catch (ServiceUnavailableException) { }
            throw;
        }

        void DeleteFailedCreateDirectory()
        {
            try
            {
                Directory.Delete(sessionPath, true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error cleaning up session directory for {SessionId}", sessionId);
            }
        }
    }

    /// <summary>
    /// Constructs the per-session daemon client (Unix socket or TCP) used to talk to the daemon process
    /// running inside the container. Extracted from <see cref="ConnectAndRegisterSessionAsync"/> as a
    /// virtual seam so orchestration tests can substitute a fake client and exercise the full startup
    /// re-adopt / recreate path without a live daemon socket. Production behavior is unchanged.
    /// </summary>
    protected virtual IDaemonClient CreateDaemonClient(bool useTcpMode, int? tcpHostPort, string socketPath, string socketSecret)
        // Pass the daemon service's own ILogger straight through (the client params are non-generic
        // ILogger?). A prior `_logger as ILogger<TcpDaemonClient>` cast always yielded null in production
        // - _logger is ILogger<TSomeDaemonService> - so the client's drain timeout/fault warnings were
        // silently dropped.
        => useTcpMode && tcpHostPort.HasValue
            ? new TcpDaemonClient(GetTcpHost(), tcpHostPort.Value, socketSecret, _logger)
            { HkdfInfo = CredentialEncryptionHkdfInfo, SignInLostStageKey = SignInLostStageKey }
            : new SocketDaemonClient(socketPath, socketSecret, _logger)
            { HkdfInfo = CredentialEncryptionHkdfInfo, SignInLostStageKey = SignInLostStageKey };

    /// <summary>
    /// Builds the in-memory <see cref="DaemonSession"/> for an already-created/running daemon container,
    /// wires its socket/TCP client event handlers, connects (with retry), registers the session in the
    /// in-memory store, and persists/refreshes the backing <see cref="PrefillSession"/> DB record. Shared
    /// by the normal create path (<see cref="CreateSessionAsync"/>, <paramref name="isReconnect"/>=false)
    /// and the startup re-adopt path (<see cref="ReconnectPersistentSessionAsync"/>,
    /// <paramref name="isReconnect"/>=true) so the connect+register logic lives in exactly one place. The
    /// Docker container itself is created/started by the caller; this method NEVER creates or starts a
    /// container. On reconnect the DB record (just flipped to Orphaned by <c>MarkOrphansAsync</c>) is
    /// reactivated in place instead of inserting a duplicate.
    /// </summary>
    private async Task<DaemonSession> ConnectAndRegisterSessionAsync(
        string sessionId,
        Guid userId,
        string containerId,
        string containerName,
        string commandsDir,
        string responsesDir,
        string socketPath,
        string socketSecret,
        bool useTcpMode,
        int? tcpHostPort,
        DateTime createdAtUtc,
        DateTime lastLoginAtUtc,
        DateTime expiresAt,
        bool isTemporary,
        bool isPersistent,
        string? ipAddress,
        string? userAgent,
        NetworkDiagnostics? networkDiagnostics,
        bool isReconnect,
        bool startupReadopt,
        bool involuntaryRecreate,
        CancellationToken cancellationToken,
        GuestPrefillStart? guestStart = null,
        PersistentImageChange? imageChange = null,
        string? configuredImage = null,
        string? imageId = null,
        DaemonSession? replacedSession = null)
    {
        // Parse user agent for OS and browser info
        var (os, browser) = UserAgentParser.Parse(userAgent);

        var session = new DaemonSession
        {
            Id = sessionId,
            UserId = userId,
            AuthState = InitialAuthState,
            ContainerId = containerId,
            ContainerName = containerName,
            ConfiguredImage = configuredImage ?? ResolveImageName(),
            ImageId = imageId ?? string.Empty,
            CommandsDir = commandsDir,
            ResponsesDir = responsesDir,
            CreatedAt = createdAtUtc,
            LastLoginAt = lastLoginAtUtc,
            ExpiresAt = expiresAt,
            IsTemporary = isTemporary,
            IsPersistent = isPersistent,
            Platform = ServiceName,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            OperatingSystem = os,
            Browser = browser,
            LastSeenAt = DateTime.UtcNow,
            NetworkDiagnostics = networkDiagnostics,
            SocketPath = useTcpMode ? null : socketPath
        };
        if (isReconnect && InitialAuthState != DaemonAuthState.Authenticated)
        {
            session.LoginSettled = false;
        }
        if (imageChange is not null)
        {
            session.NeedsRelogin = imageChange.NeedsRelogin;
            session.PreserveLoginExpiry = true;
        }

        // Create daemon client with service-specific HKDF info for credential encryption
        IDaemonClient daemonClient = CreateDaemonClient(useTcpMode, tcpHostPort, socketPath, socketSecret);
        session.Client = daemonClient;
        if (isReconnect)
        {
            await LoadRunsAsync(session, cancellationToken);
            session.Recovering = session.Runs.Values.Any(run => run.TerminalCompletedFlag == 0);
        }
        if (guestStart != null)
        {
            guestStart.Session = session;
            session.Client = daemonClient;
        }

        // Wire up socket events to session handlers
        daemonClient.OnCredentialChallenge += async (CredentialChallenge challenge) =>
        {
            await OnCredentialChallengeAsync(session, challenge);
        };
        daemonClient.OnStatusUpdate += async (DaemonStatus status) =>
        {
            await ApplyStatusAsync(session, status, null);
        };
        daemonClient.OnProgressUpdate += async (SocketPrefillProgress progress) =>
        {
            await OnProgressChangeAsync(session, progress);
        };
        daemonClient.OnError += async (string error) =>
        {
            _logger.LogWarning("Socket error for session {SessionId}: {Error}", sessionId, error);
            await Task.CompletedTask;
        };
        daemonClient.OnDisconnected += async () =>
        {
            await OnDisconnectedAsync(session, daemonClient);
        };

        // Connect to daemon (socket or TCP) with retry
        const int maxRetries = 3;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation("Connecting to daemon for session {SessionId} (attempt {Attempt}/{MaxRetries})",
                    sessionId, attempt, maxRetries);
                await daemonClient.ConnectAsync(cancellationToken);
                _logger.LogInformation("Connected to daemon for session {SessionId}", sessionId);
                break;
            }
            catch (FileNotFoundException ex)
            {
                if (isReconnect && session.Runs.Values.Any(run => run.TerminalCompletedFlag == 0))
                {
                    session.Recovering = true;
                    _logger.LogWarning(ex, "Daemon socket is unavailable for retained runs on session {SessionId}", sessionId);
                    break;
                }
                // The daemon owns its socket; the manager only bind-mounts the directory holding it.
                // A missing socket file means that directory went away under a still-running container
                // - the data directory was deleted or remounted - so the daemon is listening on a path
                // that no longer exists on this host. No number of retries can create it, and the
                // container's own logs say nothing about a host-side mount, so both would only delay
                // the outcome. Rethrowing immediately hands the caller its existing recovery, which
                // removes the container so a clean one takes over on the next start.
                // Information, not a warning: nothing here needs looking into. The container outlived
                // the directory it publishes into, the caller removes it, and the pair of lines is
                // the normal account of that cleanup rather than a problem report.
                _logger.LogInformation(
                    "Session {SessionId} has no daemon socket at {SocketPath}; its data directory was deleted or remounted, so the container is being removed rather than retried",
                    sessionId, ex.FileName ?? "the expected path");
                throw;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger.LogWarning(ex, "Socket connection attempt {Attempt} failed for session {SessionId}, retrying...",
                    attempt, sessionId);

                // Fetch daemon container logs to diagnose why the connection failed
                await LogContainerLogsAsync(containerId, sessionId, cancellationToken);

                // Wait before retry with increasing delay
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "All {MaxRetries} socket connection attempts failed for session {SessionId}", maxRetries, sessionId);

                if (isReconnect && session.Runs.Values.Any(run => run.TerminalCompletedFlag == 0))
                {
                    session.Recovering = true;
                    break;
                }

                // Fetch daemon container logs to diagnose why the connection failed
                await LogContainerLogsAsync(containerId, sessionId, cancellationToken);

                throw;
            }
        }

        session.Client = daemonClient;

        // Belt-and-braces: guarantee a brand NEW persistent container (never a re-adopted one) cannot
        // silently inherit a stale login left in its named auth volume by a previous life (e.g. a
        // crash/reboot where TerminateSessionAsync's stop-side logout never got a chance to run). Runs
        // BEFORE the session is registered/persisted/broadcast below, so nothing - a SignalR listener
        // reacting to EventSessionCreated, a status poll - can ever observe this session (and therefore
        // attempt a login against it) until any stale login has already been erased.
        // One read of the persistence mode for this create. The guard and the log-off mark copy both use
        // it, so a mode change cannot keep the volume and also skip the mark.
        bool? preserveVolumeLogin = null;
        if (isPersistent && !isReconnect && imageChange is null)
        {
            preserveVolumeLogin = await ShouldPreserveVolumeLoginAsync(session, involuntaryRecreate);
        }

        var volumeLoginPreserved = await ApplyFreshPersistentLoginGuardAsync(
            session,
            isPersistent,
            isReconnect,
            startupReadopt,
            involuntaryRecreate,
            imageChange,
            preserveVolumeLogin);

        // A person's cancel of the last sign-in keeps the container signed out across a manager restart, read from
        // the adopted container's own row, across an image update or the replacement of an errored session, read
        // from the memory of the session it replaces (its row can miss the last write), and across a recreate
        // that keeps the saved login, read from the life it replaces. The witness that the daemon had reached
        // logged-in is loaded with the mark. Loaded before registration, so no status the daemon reports can
        // start a sign-in first.
        // The log-off mark is read the same way and from the same place as the cancel mark, so every create that
        // follows another life keeps a container that was logged off signed out.
        if (isReconnect)
        {
            var stored = await _sessionService.GetSessionAsync(sessionId);
            session.LoginCanceledByPerson = stored?.LoginCanceledByPerson == true;
            session.LoggedInWhilePersonCanceled = stored?.LoggedInWhilePersonCanceled == true;
            session.SignedOut = stored?.SignedOut == true;
        }
        else if ((imageChange?.Session ?? replacedSession) is { } predecessor)
        {
            lock (predecessor.PrefillLock)
            {
                session.LoginCanceledByPerson = predecessor.LoginCanceledByPerson;
                session.LoggedInWhilePersonCanceled = predecessor.LoggedInWhilePersonCanceled;
                session.SignedOut = predecessor.SignedOut;
            }
        }
        else if (preserveVolumeLogin == true)
        {
            var stored = await _sessionService.GetLatestPersistentSessionAsync(Platform);
            session.LoginCanceledByPerson = stored?.LoginCanceledByPerson == true;
            session.LoggedInWhilePersonCanceled = stored?.LoggedInWhilePersonCanceled == true;
            session.SignedOut = stored?.SignedOut == true;
        }

        // Creation gate: if shutdown began after this create started, do NOT register the session -
        // StopAsync's one-time _sessions snapshot has already passed, so registering here would let the
        // session escape teardown, AND because it was never registered StopAsync cannot clean it up
        // either, stranding a running container that survives shutdown and gets adopted next startup
        // (which for KillOnRestart would violate the mode). Tear down everything the create built - the
        // container, the session's bind-mount directory, and the client/CTS - then fail the create. A plain
        // create has no DB row yet; it is inserted only after registration below. An image update's create
        // wrote its row when it created the container: under KillOnRestart StopAsync's cancel of the pending
        // change closes that row, and in the other modes the next startup re-adopt reactivates it.
        if (_stopping && !isReconnect)
        {
            // Pre-registration: this session was never in _sessions, so StopAsync's snapshot can never see
            // it - the create is its sole owner and must tear it down (wasRegistered: false).
            await RejectEscapedCreateAsync(session, involuntaryRecreate, wasRegistered: false, cancellationToken);
            throw new ServiceUnavailableException(
                $"{ServiceName} daemon is shutting down; refusing to register a new session.")
            {
                StageKey = "errors.prefill.shuttingDown"
            };
        }

        if (guestStart != null)
        {
            GuestGate.Check(guestStart);
            guestStart.Registered = true;
        }
        _sessions[sessionId] = session;

        // The session is now live in _sessions; mirror its presence into the activity registry. A shutdown
        // race that rejects it below tears the session down as the process exits, so a lingering present
        // entry is harmless (the registry dies with it).
        await ReportSessionActivityAsync(session, present: true);

        // Post-registration re-check closes the check-then-act race behind the gate above: shutdown can
        // flip _stopping and take its one-time _sessions snapshot in the tiny window between that check and
        // this registration, leaving the session escaped (never visited by StopAsync). Fence (the
        // ConcurrentDictionary write already fences store/load, but be explicit) then re-read: if shutdown
        // began, pull the just-registered session back out and tear it down by mode. A plain create has no
        // DB row yet - it is inserted only below; an image update's row already exists, and StopAsync's
        // cancel of the pending change closes it (KillOnRestart) or the startup re-adopt reactivates it.
        Interlocked.MemoryBarrier();
        if (_stopping && !isReconnect)
        {
            // Post-registration: this session was registered, so StopAsync's snapshot may have claimed it.
            // Reject as a registered owner (wasRegistered: true) - RejectEscapedCreateAsync tears down only
            // if it still owns the session; if shutdown already won the TryRemove it leaves the client alone.
            await RejectEscapedCreateAsync(session, involuntaryRecreate, wasRegistered: true, cancellationToken);
            throw new ServiceUnavailableException(
                $"{ServiceName} daemon is shutting down; refusing to register a new session.")
            {
                StageKey = "errors.prefill.shuttingDown"
            };
        }

        // Persist session to database for admin visibility and orphan tracking. On reconnect the backing
        // record was just flipped to Orphaned by MarkOrphansAsync, and an image update wrote its new
        // container's record when it created that container, so both reactivate it IN PLACE (reusing the
        // same unique SessionId keeps the PrefillHistory linkage) rather than inserting a duplicate.
        if (isReconnect || imageChange is not null)
        {
            await _sessionService.ReactivateSessionAsync(
                sessionId,
                userId,
                containerId,
                containerName,
                session.ExpiresAt,
                ServiceName);
        }
        else
        {
            await _sessionService.CreateSessionAsync(
                sessionId,
                userId,
                containerId,
                containerName,
                session.ExpiresAt,
                session.LastLoginAt,
                ServiceName);
        }
        // The new row starts unmarked. A mark carried over from the life this one replaces, or set by a person's
        // Cancel after registration whose write found no row yet, is stored now, so a manager restart keeps it.
        if (!isReconnect && (session.LoginCanceledByPerson || session.SignedOut))
        {
            await SaveLoginStateAsync(session);
        }

        if (guestStart != null)
        {
            guestStart.Registration.TrySetResult();
            GuestGate.Check(guestStart);
            if (!IsSessionLive(session))
                throw new ForbiddenException("This prefill session was stopped.") { StageKey = "errors.prefill.sessionStopped" };
        }
        _logger.LogInformation(
            "{Action} daemon session {SessionId} for user {UserId}",
            isReconnect ? "Re-adopted" : "Created", sessionId, userId);

        // A re-adopted persistent container keeps its login inside its named auth volume across a
        // manager restart, but this fresh in-memory session starts at InitialAuthState
        // (NotAuthenticated for account services). Reconcile once with the daemon's LIVE status,
        // routed through the same ApplyStatusAsync transition every socket status push uses, so
        // a still-logged-in container immediately reads as Authenticated (and broadcasts
        // AuthStateChanged) unless a person's cancel is still stored, in which case it stays signed out.
        // Best-effort: an unresponsive daemon leaves the conservative NotAuthenticated default.
        if (isPersistent || !isReconnect)
        {
            try
            {
                var signOutCount = session.SignOutCount;
                var liveStatus = await daemonClient.GetStatusAsync(cancellationToken);
                if (liveStatus is not null)
                {
                    // A person's cancel keeps a logged-in report from signing the container in. The
                    // status is still applied so that report can arm the next connection drop.
                    await ApplyStatusAsync(session, liveStatus, signOutCount);
                    await RecoverRunsAsync(session, liveStatus, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to reconcile adopted persistent {ServiceName} session {SessionId} with live daemon status",
                    ServiceName, sessionId);
            }
        }

        // Final ownership re-check AFTER the DB insert (and any reconnect reconciliation), before the
        // creation broadcast: shutdown may have flipped _stopping and taken its one-time snapshot between the
        // post-registration re-check above and here. If so, roll the create back (mode-aware: terminate modes
        // flip the just-inserted row to Terminated, detach modes leave it Active for re-adoption) and abort,
        // so a fresh Active row / EventSessionCreated can never materialize after termination completed.
        if (await RollBackCreateIfShuttingDownAsync(session, involuntaryRecreate, isReconnect, cancellationToken))
        {
            throw new ServiceUnavailableException(
                $"{ServiceName} daemon is shutting down; refusing to register a new session.")
            {
                StageKey = "errors.prefill.shuttingDown"
            };
        }

        // Send session creation to the account holders for real-time updates
        var sessionDtoCreated = DaemonSessionDto.FromSession(session);
        if (guestStart != null)
        {
            GuestGate.Check(guestStart);
            if (!IsSessionLive(session))
                throw new ForbiddenException("This prefill session was stopped.") { StageKey = "errors.prefill.sessionStopped" };
        }
        try { await NotifyHubAsync(session, EventSessionCreated, sessionDtoCreated); }
        finally { guestStart?.Publication.TrySetResult(); }
        if (guestStart != null)
            GuestGate.Check(guestStart);

        // Runs only when ApplyFreshPersistentLoginGuardAsync returned true. The daemon uses its saved login only
        // after a login command, so the command is sent headlessly and nobody is asked to sign in while a valid
        // login sits on the volume. A saved login that fails is canceled inside the attempt with no dialog; login
        // dialogs open only from a person's action. A re-adopted daemon that reported a drop already runs the
        // sign-in-again retry, so no second sign-in starts here.
        var signInAgainRunning = false;
        if (isReconnect)
        {
            lock (session.PrefillLock) signInAgainRunning = session.SignInAgainAfterDrop is not null;
        }
        if (volumeLoginPreserved && !signInAgainRunning)
        {
            LastHeadlessSelfAuthAttempt = InvokeSafeAsync(async () =>
            {
                // A container recreated while Steam is unreachable (after a reboot, an error or an image
                // update during a retry) keeps trying its saved login on the same waits as a dropped sign-in.
                // The try installs the retry's stop token, so a drop during it reuses that token, and a
                // person's sign-in that takes over that retry stops this try too. A person's Cancel of a Log in
                // waiting behind the try does not stop it; that Cancel's mark keeps the container signed out if
                // the try signs in.
                CancellationTokenSource? installed = null;
                CancellationToken takeOver;
                lock (session.PrefillLock)
                {
                    var stop = session.SignInAgainCancel;
                    if (stop is null)
                    {
                        installed = new CancellationTokenSource();
                        session.SignInAgainCancel = installed;
                        stop = installed;
                    }
                    takeOver = stop.Token;
                }
                try
                {
                    if (await AttemptHeadlessPersistentSelfAuthAsync(session, takeOver) == LoginAttemptOutcome.NoResponse
                        && Platform == PrefillPlatform.Steam)
                    {
                        StartSignInAgain(session);
                    }
                }
                finally
                {
                    if (installed is not null)
                    {
                        lock (session.PrefillLock)
                        {
                            // A drop during this try reuses this source for its retry. Clearing it
                            // here would leave that retry with a token a person's Cancel cannot see.
                            if (ReferenceEquals(session.SignInAgainCancel, installed)
                                && session.SignInAgainAfterDrop is null)
                                session.SignInAgainCancel = null;
                        }
                    }
                }
            }, nameof(AttemptHeadlessPersistentSelfAuthAsync));
        }

        return session;
    }

    /// <summary>
    /// Tears down everything a fresh create built when it is rejected at the shutdown creation gate
    /// (before registration): stops and removes the container - preserving a persistent named auth volume
    /// exactly as the normal teardown does, and deliberately WITHOUT a daemon logout so a FullPersistence
    /// volume login survives - deletes the session's command/response bind-mount directory, and disposes
    /// the client + CTS. A plain create has no DB row at this point (it inserts it only after registration),
    /// so there is nothing to flip to Terminated; an image update's create already wrote its row, and
    /// StopAsync's cancel of the pending change closes it (KillOnRestart) or the startup re-adopt
    /// reactivates it (the other modes). Reuses the same container-removal primitive and
    /// directory cleanup as <see cref="TerminateSessionAsync"/>. Internal so it can be unit-tested for the
    /// directory + client/CTS teardown without standing up Docker.
    /// </summary>
    internal async Task TearDownRejectedCreateAsync(DaemonSession session, bool involuntaryRecreate = false, ScheduledPrefillConfigDto? config = null)
    {
        // The bind-mount directory is deleted ONLY once the container is CONFIRMED absent (removed by this
        // call, or already gone) - deleting it while the container might still be live would break the
        // daemon. "No container to remove" counts as confirmed absent.
        var containerRemoved = string.IsNullOrEmpty(session.ContainerId);

        if (_containerGateway.IsAvailable && !string.IsNullOrEmpty(session.ContainerId))
        {
            // A rejected create never finished login, but its named auth volume can hold a PRIOR life's
            // login. Preserve it ONLY for the verified FullPersistence-involuntary case (same predicate the
            // fresh-login guard uses); otherwise best-effort erase before removal, matching the
            // erase-on-stop policy for KillOnRestart / KeepAcrossRestart. The threaded config is reused so
            // the whole shutdown-reject chain decides off one config snapshot.
            if (!await ShouldPreserveVolumeLoginAsync(session, involuntaryRecreate, config))
            {
                await TryBestEffortLogoutAsync(session, "create rejected at shutdown gate");
            }

            // The gateway limits the removal, so an unresponsive Docker call cannot block shutdown.
            try
            {
                var outcome = await RemoveContainerForceAsync(session.ContainerId, CancellationToken.None, removeVolumes: !session.IsPersistent);
                // Confirmed absent (removed now, or already gone) -> the dir is safe to delete. An in-flight
                // removal by another sweep leaves absence unconfirmed, so retain the dir until it is.
                containerRemoved = outcome is ContainerRemovalOutcome.Removed or ContainerRemovalOutcome.AlreadyAbsent;
                if (outcome == ContainerRemovalOutcome.RemovalInProgress)
                {
                    _logger.LogWarning(
                        "Container {ContainerId} for a create rejected at the shutdown gate is mid-removal by another sweep; retaining its bind-mount directory until removal is confirmed",
                        session.ContainerId);
                }
            }
            catch (Exception ex)
            {
                // Loud: a stranded running container is exactly what the gate exists to prevent. The next
                // startup's orphan sweep / re-adopt reconciles the CONTAINER; nothing reaps the bind-mount
                // directory, so it is retained deliberately as a safety measure - never delete it while the
                // container may still be live (a genuinely stuck removal leaking one dir is the safer trade).
                _logger.LogError(ex,
                    "Failed to remove container {ContainerId} for a create rejected at the shutdown gate; retaining its bind-mount directory (never deleted under a possibly-live container)",
                    session.ContainerId);
            }
        }

        await DisposeClientWithDrainAsync(session);

        if (containerRemoved)
        {
            DeleteSessionDirectory(session);
        }
    }

    /// <summary>
    /// Drain-then-dispose funnel for a session's client: cancels manager-side work, drains in-flight
    /// daemon event callbacks (bounded, best-effort) so none writes/broadcasts after this returns, then
    /// disposes the client and CTS. Used by the detach and rejected-create teardown paths.
    /// </summary>
    private async Task DisposeClientWithDrainAsync(DaemonSession session, CancellationToken cancellationToken = default)
    {
        // Tolerate an already-disposed CTS. The single-owner reject guard means only one teardown path
        // should reach here for a given session, but a shutdown race can still hand this an instance whose
        // CTS the winner already disposed; Cancel() would then throw ObjectDisposedException. Degrade to a
        // no-op rather than fault teardown. (Dispose() below is idempotent and never throws on a disposed CTS.)
        try
        {
            session.CancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        await DrainSessionEventsAsync(session, cancellationToken);
        session.Client.Dispose();
        session.CancellationTokenSource.Dispose();
    }

    /// <summary>Deletes a session's command/response bind-mount directory, warn-and-continue on error.</summary>
    private void DeleteSessionDirectory(DaemonSession session)
    {
        try
        {
            var sessionDir = Path.GetDirectoryName(session.CommandsDir);
            if (sessionDir != null && Directory.Exists(sessionDir))
            {
                Directory.Delete(sessionDir, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error cleaning up session directory for {SessionId}", session.Id);
        }
    }

    /// <summary>
    /// Best-effort, bounded logout of a persistent session's live daemon socket. Used both when a
    /// persistent session is stopped (<see cref="TerminateSessionAsync"/>) and, as a belt-and-braces
    /// guard, right after a brand new persistent container connects for the first time (see
    /// <see cref="ApplyFreshPersistentLoginGuardAsync"/>). A dead/hung socket must never block the
    /// caller's teardown or startup flow, so every failure (timeout, transport error, or the daemon
    /// reporting failure) is swallowed and logged - callers proceed regardless. No-op for
    /// non-persistent sessions. Returns whether the daemon is left holding no account: it acknowledged
    /// the logout, or declined it because it was not signed in.
    /// </summary>
    private async Task<bool> TryBestEffortLogoutAsync(DaemonSession session, string context)
    {
        if (!session.IsPersistent)
        {
            return true;
        }

        try
        {
            // 15s, not 5s: when logout races an in-flight login, the daemon (Steam/Epic/Xbox) cancels
            // the login task and AWAITS its unwind before acking - up to an 8s LogoutLoginTaskTimeout
            // on the daemon side (Xbox's chain through PollForTokenAsync + the XSTS/XASU token mint is
            // the slowest). A shorter caller-side timeout here fires before that unwind can ever
            // finish, so every stop-during-login logged a spurious "failed; proceeding anyway" even
            // though the daemon was about to answer. This is linked (CancellationTokenSource.
            // CreateLinkedTokenSource in DaemonClientBase.SendCoreAsync) with LogoutWithReasonAsync's
            // own 10s->15s command timeout - keep both in sync, comfortably above the 8s budget.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_daemonStopBudget);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var outcome = await session.Client.LogoutWithReasonAsync(cts.Token);
            if (!outcome.Success && outcome.RequiresLogin)
            {
                // Older daemon image: its pre-login command gate rejects "logout" outright while the
                // session hasn't finished authenticating (see the erase-on-stop regression diagnosis) -
                // this is expected for a container being cancelled mid-challenge, not a real failure.
                _logger.LogInformation(
                    "Best-effort logout for persistent {ServiceName} session {SessionId} ({Context}): " +
                    "daemon declined logout before authentication (older daemon image); nothing to log out",
                    ServiceName, session.Id, context);
                return true;
            }
            _logger.LogInformation(
                "Best-effort logout for persistent {ServiceName} session {SessionId} ({Context}): {Result}",
                ServiceName, session.Id, context, outcome.Success ? "acknowledged" : "daemon reported failure");
            return outcome.Success;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex,
                "Best-effort logout for persistent {ServiceName} session {SessionId} ({Context}) failed; proceeding anyway",
                ServiceName, session.Id, context);
            return false;
        }
    }

    /// <summary>
    /// Called once when a persistent daemon's socket first connects. A newly created container's old volume
    /// login is erased unless one of the cases below keeps it; a re-adopted container keeps its login. Returns
    /// true when a saved login should be tried: a FullPersistence volume login whose stored date is ahead, an
    /// image update whose old container was signed in or signing in, or the startup re-adopt
    /// (<paramref name="startupReadopt"/>) outside KillOnRestart while the date is ahead. Internal so tests can
    /// call it without Docker.
    /// </summary>
    internal async Task<bool> ApplyFreshPersistentLoginGuardAsync(
        DaemonSession session,
        bool isPersistent,
        bool isReconnect,
        bool startupReadopt,
        bool involuntaryRecreate = false,
        PersistentImageChange? imageChange = null,
        bool? preserveVolumeLogin = null)
    {
        if (!isPersistent)
        {
            return false;
        }

        if (isReconnect)
        {
            // A container a restart left running can be a new daemon that never signed in (an image update's
            // container that had not registered yet), so the startup re-adopt signs it in from the saved login
            // while the stored date is ahead. KillOnRestart keeps no login across a restart. The other adopts
            // never sign in: Clear stored logins adopts and then logs out.
            if (!startupReadopt || session.ExpiresAt <= DateTime.UtcNow)
            {
                return false;
            }
            var config = _stateService.GetScheduledPrefillConfig();
            return config != null && config.GetEffectivePersistenceMode(Platform) != PersistenceMode.KillOnRestart;
        }

        if (imageChange is not null)
        {
            _logger.LogInformation(
                "Preserving the named-volume login for persistent {ServiceName} session {SessionId} during a planned image update",
                ServiceName,
                session.Id);
            return imageChange.Authenticated || imageChange.SigningInAgain;
        }

        if (preserveVolumeLogin ?? await ShouldPreserveVolumeLoginAsync(session, involuntaryRecreate))
        {
            _logger.LogInformation(
                "Skipping fresh-login guard for persistent {ServiceName} session {SessionId}: FullPersistence preserves the saved volume login across a verified involuntary death (explicit recreate flag or prior session not terminated)",
                ServiceName, session.Id);
            // A past stored date keeps the login on the volume but asks a person to sign in again.
            return session.ExpiresAt > DateTime.UtcNow;
        }

        _logger.LogInformation(
            "Running fresh-login guard for persistent {ServiceName} session {SessionId}: erasing any inherited volume login (not FullPersistence, or the prior FullPersistence session was terminated / none exists)",
            ServiceName, session.Id);
        await TryBestEffortLogoutAsync(session, "fresh persistent container create");
        return false;
    }

    /// <summary>
    /// Whether a persistent session's saved named-volume login should be PRESERVED (not erased) - true
    /// only for FullPersistence across a VERIFIED involuntary death: an explicit involuntary-recreate flag
    /// (startup outage-recreate or error-state replacement), or the most recent persistent DB row for this
    /// service ending NON-Terminated. An explicit admin Stop leaves a Terminated row (and its best-effort
    /// logout can silently fail), so a Terminated / absent row - or any non-FullPersistence mode - returns
    /// false so the caller erases as a backstop. Shared by the fresh-login guard and the rejected-create
    /// teardown so both make the identical erase-vs-preserve decision.
    /// </summary>
    private async Task<bool> ShouldPreserveVolumeLoginAsync(DaemonSession session, bool involuntaryRecreate, ScheduledPrefillConfigDto? config = null)
    {
        if (!session.IsPersistent)
        {
            return false;
        }

        // Reuse a config snapshot threaded from the shutdown-reject chain when present, else read it here
        // (the fresh-login guard call site passes none).
        config ??= _stateService.GetScheduledPrefillConfig();
        if (config == null || config.GetEffectivePersistenceMode(Platform) != PersistenceMode.FullPersistence)
        {
            return false;
        }

        if (involuntaryRecreate)
        {
            return true;
        }

        var latest = await _sessionService.GetLatestPersistentSessionAsync(Platform);
        return latest is { Status: not PrefillSessionStatus.Terminated };
    }

    /// <summary>
    /// Mirrors a daemon session's presence into the unified activity registry so the Prefill Sessions and
    /// persistent-container status dots read the one ActivityUpdated event. Reports the per-session
    /// present/downloading state, then recomputes this platform's aggregate presence. Best-effort: the
    /// registry swallows its own broadcast failures, so a presence update never disrupts daemon work.
    /// </summary>
    private async Task ReportSessionActivityAsync(DaemonSession session, bool present)
    {
        if (_activityRegistry is null)
        {
            return;
        }

        await _activityRegistry.ReportAsync(
            ActivityDomains.PrefillSession, session.Id, ActivityAspects.Present, present);
        await _activityRegistry.ReportAsync(
            ActivityDomains.PrefillSession, session.Id, ActivityAspects.Downloading,
            present && session.Runs.Values.Any(run => run.TerminalCompletedFlag == 0));

        await ReportPlatformActivityAsync();
    }

    /// <summary>
    /// Recomputes and reports this platform's aggregate presence: whether a persistent container is
    /// running/authenticated, and (only for the anonymous Battle.net/Riot daemons, which have no separate
    /// mapping service) whether the integration is connected. Login-based platforms leave their integration
    /// badge to the owning mapping service, so this never reports an integration aspect for them.
    /// Internal (not private): DaemonConnectivityReconciler also calls this on a timer for BattleNet/Riot,
    /// since Docker availability can change with zero session activity to otherwise trigger a refresh.
    /// </summary>
    internal async Task ReportPlatformActivityAsync()
    {
        if (_activityRegistry is null)
        {
            return;
        }

        var platformKey = Platform.ToString().ToLowerInvariant();
        var persistent = GetActivePersistentSession();

        await _activityRegistry.ReportAsync(
            ActivityDomains.PersistentContainer, platformKey, ActivityAspects.Running, persistent is not null);
        await _activityRegistry.ReportAsync(
            ActivityDomains.PersistentContainer, platformKey, ActivityAspects.Authenticated,
            persistent?.AuthState == DaemonAuthState.Authenticated);

        if (!Platform.RequiresLogin())
        {
            // The anonymous Battle.net/Riot "Connected" badge reflects Docker availability (the daemon
            // needs no login), matching what BattleNetDaemonStatus/RiotDaemonStatus render.
            await _activityRegistry.ReportAsync(
                ActivityDomains.Integration, platformKey, ActivityAspects.Connected, IsDockerAvailable);
        }
    }
}
