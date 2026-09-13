using Docker.DotNet;
using Docker.DotNet.Models;
using LancacheManager.Models;
using LancacheManager.Core.Services.SteamPrefill;
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
            Uri dockerUri;
            if (OperatingSystemDetector.IsWindows)
            {
                dockerUri = new Uri("npipe://./pipe/docker_engine");
            }
            else
            {
                dockerUri = new Uri("unix:///var/run/docker.sock");
            }

            // Check if Docker socket exists
            if (!OperatingSystemDetector.IsWindows && !File.Exists("/var/run/docker.sock"))
            {
                _logger.LogWarning("Docker socket not found at /var/run/docker.sock. " +
                    "Mount the Docker socket to enable prefill containers: -v /var/run/docker.sock:/var/run/docker.sock");
            }

            _containerGateway.Connect(dockerUri);

            // Test connection
            try
            {
                var version = await _containerGateway.GetVersionAsync(cancellationToken);
                _logger.LogInformation("Docker client connected. Docker version: {Version}", version.Version);
            }
            catch (Exception ex)
            {
                // Log clean message without stack trace - Docker not running is expected in many setups
                _logger.LogWarning("{ServiceName} Prefill feature will be disabled - Docker is not available. Start Docker Desktop to enable it.", ServiceName);
                _logger.LogTrace(ex, "Docker connection error details");
                _containerGateway.Reset();
            }

            // Ensure image is available
            if (_containerGateway.IsAvailable)
            {
                await EnsureImageExistsAsync(cancellationToken);

                // Cleanup orphaned containers from previous runs
                await CleanupOrphanedContainersAsync(cancellationToken);

                // Re-adopt persistent containers that survived this restart (reconnect, don't recreate)
                await ReadoptPersistentContainersAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Log clean message without stack trace
            _logger.LogWarning("Failed to initialize Docker client - {ServiceName} Prefill feature will be disabled.", ServiceName);
            _logger.LogTrace(ex, "Docker initialization error details");
            _containerGateway.Reset();
        }

        _logger.LogInformation("{ServiceName}PrefillDaemonService started. Docker available: {DockerAvailable}", ServiceName, _containerGateway.IsAvailable);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("{ServiceName}PrefillDaemonService stopping...", ServiceName);

        // Close the creation gate before snapshotting _sessions: a create that completes after this point
        // is rejected at (or pulled back out by) its registration re-check.
        _stopping = true;

        // Read the effective persistence policy once for this shutdown pass. GetScheduledPrefillConfig
        // is synchronous and lock-safe on StateService (no IO, no cache to go stale), so it is safe to
        // call while the host is tearing this hosted service down.
        var config = _stateService.GetScheduledPrefillConfig();

        var sessions = _sessions.Values.ToList();

        // Detach KeepAcrossRestart / FullPersistence persistent sessions CONCURRENTLY: each detach is an
        // independent bounded drain + local-handle release (no shared mutable state, container untouched),
        // so several stuck drains share one shutdown-wide budget instead of costing the timeout x N
        // sequentially. The host cancellation token is threaded in so a cancelled shutdown cuts each drain
        // short. Guest / KillOnRestart terminates run sequentially - each stops/removes a container and
        // flips DB rows.
        var detachTasks = new List<Task>();
        foreach (var session in sessions)
        {
            if (ShouldDetachOnShutdown(session, config))
            {
                detachTasks.Add(DetachPersistentSessionForShutdownAsync(session, cancellationToken));
            }
            else
            {
                await TerminateSessionAsync(session.Id, "Service shutdown");
            }
        }

        if (detachTasks.Count > 0)
        {
            await Task.WhenAll(detachTasks);
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
    /// (<paramref name="isReconnect"/>) is exempt: it runs only during StartAsync, never during StopAsync.
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
        CancellationToken cancellationToken = default)
    {
        if (!_containerGateway.IsAvailable)
        {
            throw new InvalidOperationException(
                "Docker is not running or not accessible. Please start Docker Desktop and try again.");
        }

        if (!isPersistent)
        {
            if (sessionType == SessionType.Guest)
            {
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
                        throw new ForbiddenException("Session access is no longer valid.");
                    GuestGate.Check(start);
                    var session = await CreateSessionCoreAsync(userId, ipAddress, userAgent, sessionType, isPersistent,
                        reuseExistingSession, persistentExpiresAtOverrideUtc, involuntaryRecreate, createCts.Token, guestStart: start);
                    GuestGate.Check(start);
                    if (!IsSessionLive(session))
                        throw new ForbiddenException("This prefill session was stopped.");
                    GuestGate.CompleteCreate(start);
                    completed = true;
                    return session;
                }
                catch
                {
                    start.Registration.TrySetResult();
                    start.Publication.TrySetResult();
                    try { await CleanGuestStartAsync(start); }
                    catch (Exception) { cleanupPending = true; }
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
        await _persistentStartLock.WaitAsync(cancellationToken);
        try
        {
            return await CreateSessionCoreAsync(userId, ipAddress, userAgent, sessionType, isPersistent, reuseExistingSession, persistentExpiresAtOverrideUtc, involuntaryRecreate, cancellationToken);
        }
        finally
        {
            _persistentStartLock.Release();
        }
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
        Action<bool>? persistentCreationResult = null,
        Action<string, string>? persistentContainerCreated = null,
        GuestPrefillStart? guestStart = null)
    {
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
                persistentCreationResult?.Invoke(false);
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

        // Always pull latest image before creating session
        await EnsureImageExistsAsync(cancellationToken);

        // Whether the fresh-login guard should preserve a FullPersistence volume login: true for a
        // caller-declared involuntary recreate (startup outage-recreate) OR an in-place error-state
        // replacement (an involuntary socket death, not an admin stop). Terminating the errored session
        // below flips its DB row to Terminated, so the guard's row-status heuristic alone would wrongly
        // erase; this explicit flag overrides it for that verified-involuntary path.
        var involuntaryReplacement = false;

        if (isPersistent)
        {
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
                await TerminateSessionAsync(existingErrorSession.Id, "Replacing errored persistent session", force: true);
                involuntaryReplacement = true;
            }

            // Docker-level adopt-or-replace pre-create check (kills leak M1's leftover/409 path):
            // an unadopted-but-running container for this service should be reconnected to instead of
            // shadowed by a second container; a stopped one should be removed first so the
            // deterministic name is free.
            var decision = await ResolvePersistentContainerDecisionAsync(cancellationToken);

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
                try
                {
                    await ReconnectPersistentSessionAsync(target, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to adopt existing persistent {ServiceName} container {Id}",
                        ServiceName, ShortContainerId(target.ID));
                }

                var adopted = GetActivePersistentSession();
                if (adopted != null)
                {
                    persistentCreationResult?.Invoke(false);
                    return adopted;
                }

                // Adoption failed (thrown or one of ReconnectPersistentSessionAsync's silent exits) -
                // the container is now a zombie occupying the deterministic name. Remove it rather
                // than leaving it running and invisible, then fall through to create fresh below.
                _logger.LogWarning(
                    "Adoption of persistent {ServiceName} container {Id} did not produce an active session; removing it and creating fresh",
                    ServiceName, ShortContainerId(target.ID));
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

        var sessionId = Guid.NewGuid().ToString("N")[..16];
        var basePath = GetDaemonBasePath();
        var sessionPath = Path.Combine(basePath, "sessions", sessionId);
        var commandsDir = Path.Combine(sessionPath, "commands");
        var responsesDir = Path.Combine(sessionPath, "responses");

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
        var imageName = GetImageName();

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
            throw new InvalidOperationException(
                $"Prefill__LancacheIp='{configured}' could not be resolved to an IP address.");
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
            Image = imageName,
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
        var createResponse = isPersistent
            ? await CreatePersistentContainerWithConflictRetryAsync(createParameters, cancellationToken)
            : await _containerGateway.CreateContainerAsync(createParameters, cancellationToken);

        var containerId = createResponse.ID;
        if (guestStart != null)
        {
            guestStart.ContainerId = containerId;
            GuestGate.Check(guestStart);
        }
        _logger.LogInformation("Created container {ContainerId} for session {SessionId}", containerId, sessionId);
        persistentContainerCreated?.Invoke(sessionId, containerId);

        // Start container
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

                // Try to get logs
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

                throw new InvalidOperationException($"Container crashed on startup. ExitCode: {exitCode}. Check if image '{imageName}' exists and is valid.");
            }

            _logger.LogInformation("Container {ContainerId} verified running for session {SessionId}", containerId, sessionId);
        }
        catch (DockerContainerNotFoundException)
        {
            _logger.LogError("Container {ContainerId} was removed before we could verify it (crashed immediately with AutoRemove). " +
                "Image '{ImageName}' may not exist or is crashing on startup.", containerId, imageName);
            throw new InvalidOperationException($"Container crashed immediately. Ensure image '{imageName}' exists and is properly configured.");
        }

        // Run container network diagnostics (internet connectivity and DNS resolution)
        // This helps troubleshoot prefill issues - the container needs both:
        // 1. Internet access to reach the service
        // 2. DNS resolving lancache domains to your cache server
        var networkDiagnostics = await TestContainerConnectivityAsync(containerId, containerName, isHostMode, cancellationToken);

        // Manager-enforced lifetime. Guest/temporary containers are capped at
        // createdAt + GetGuestPermissionDurationHours() so they are reaped by ProcessSessionExpiryAsync
        // exactly when the admin-configured per-service permission duration elapses. That duration is
        // already admin-validated (clamped 1-3h), so it is authoritative on its own - it is NOT further
        // clamped against the generic standard session timeout backstop below (that backstop defaults to
        // 120 minutes and would silently override any per-service duration configured above 2h, which is
        // exactly the bug this comment used to gloss over: the container was correctly told to run for
        // the full configured duration via PREFILL_MAX_LIFETIME_SECONDS, but the manager's own tracked
        // expiry - and therefore the reaper and any UI countdown - was getting cut short). Admin/persistent
        // sessions are never subject to the cap and keep the standard timeout.
        var createdAtUtc = DateTime.UtcNow;
        var isTemporary = sessionType == SessionType.Guest;
        var standardExpiresAt = createdAtUtc.AddMinutes(GetSessionTimeoutMinutes());
        DateTime expiresAt;
        if (isPersistent)
        {
            // Persistent admin login: expiry governs when NeedsRelogin is flagged (the reaper never
            // tears a persistent session down). A FullPersistence startup recreate passes an explicit
            // anchor (the prior life's still-future window) so the outage does not silently extend the
            // admin-configured validity; every other persistent create uses the configured window.
            expiresAt = persistentExpiresAtOverrideUtc
                ?? createdAtUtc.AddDays(_stateService.GetAdminPersistentLoginValidityDays());
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
            expiresAt,
            isTemporary,
            isPersistent,
            ipAddress,
            userAgent,
            networkDiagnostics,
            isReconnect: false,
            involuntaryRecreate: involuntaryRecreate || involuntaryReplacement,
            cancellationToken, guestStart);

        persistentCreationResult?.Invoke(isPersistent);
        return session;
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
            { HkdfInfo = CredentialEncryptionHkdfInfo }
            : new SocketDaemonClient(socketPath, socketSecret, _logger)
            { HkdfInfo = CredentialEncryptionHkdfInfo };

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
        DateTime expiresAt,
        bool isTemporary,
        bool isPersistent,
        string? ipAddress,
        string? userAgent,
        NetworkDiagnostics? networkDiagnostics,
        bool isReconnect,
        bool involuntaryRecreate,
        CancellationToken cancellationToken,
        GuestPrefillStart? guestStart = null)
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
            CommandsDir = commandsDir,
            ResponsesDir = responsesDir,
            CreatedAt = createdAtUtc,
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
            await OnStatusChangeAsync(session, status);
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
            if (session.Capabilities?.SupportsConcurrentPrefill == true || !session.Runs.IsEmpty)
            {
                lock (session.PrefillLock)
                {
                    if (!IsSessionLive(session) || !ReferenceEquals(session.Client, daemonClient)) return;
                    session.Recovering = true;
                    foreach (var run in session.Runs.Values.Where(run => run.TerminalCompletedFlag == 0))
                        run.Recovering = true;
                }
                await NotifyHubAsync(EventSessionUpdated, DaemonSessionDto.FromSession(session));
                FireAndForgetAsync(() => RefreshRunsAsync(session.Id), nameof(RefreshRunsAsync));
                return;
            }
            Guid? runId;
            lock (session.PrefillLock)
            {
                if (!IsSessionLive(session) || !ReferenceEquals(session.Client, daemonClient)
                    || session.Status is DaemonSessionStatus.Error or DaemonSessionStatus.Terminated
                    || session.CancellationTokenSource.IsCancellationRequested)
                    return;
                session.Status = DaemonSessionStatus.Error;
                runId = session.PrefillRunId;
            }
            _logger.LogWarning("Socket disconnected for session {SessionId}", sessionId);
            try
            {
                var failure = new DaemonCommandException();
                await TransitionToTerminalAsync(session, PrefillState.Failed, runId, failure.Message, failure.StageKey);
                DaemonSessionDto snapshot;
                lock (session.PrefillLock)
                {
                    if (!IsSessionLive(session) || !ReferenceEquals(session.Client, daemonClient))
                        return;
                    snapshot = DaemonSessionDto.FromSession(session);
                }
                await NotifyHubAsync(EventSessionUpdated, snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify the socket disconnect for session {SessionId}", sessionId);
            }
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
        var volumeLoginPreserved = await ApplyFreshPersistentLoginGuardAsync(session, isPersistent, isReconnect, involuntaryRecreate);

        // Creation gate: if shutdown began after this create started, do NOT register the session -
        // StopAsync's one-time _sessions snapshot has already passed, so registering here would let the
        // session escape teardown, AND because it was never registered StopAsync cannot clean it up
        // either, stranding a running container that survives shutdown and gets adopted next startup
        // (which for KillOnRestart would violate the mode). Tear down everything the create built - the
        // container, the session's bind-mount directory, and the client/CTS - then fail the create. No DB
        // row exists yet; it is inserted only after registration below. A re-adopt (isReconnect) is
        // exempt: it only runs during StartAsync, never during StopAsync.
        if (_stopping && !isReconnect)
        {
            // Pre-registration: this session was never in _sessions, so StopAsync's snapshot can never see
            // it - the create is its sole owner and must tear it down (wasRegistered: false).
            await RejectEscapedCreateAsync(session, involuntaryRecreate, wasRegistered: false, cancellationToken);
            throw new InvalidOperationException(
                $"{ServiceName} daemon is shutting down; refusing to register a new session.");
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
        // began, pull the just-registered session back out and tear it down by mode. No DB row exists yet -
        // it is inserted only below.
        Interlocked.MemoryBarrier();
        if (_stopping && !isReconnect)
        {
            // Post-registration: this session was registered, so StopAsync's snapshot may have claimed it.
            // Reject as a registered owner (wasRegistered: true) - RejectEscapedCreateAsync tears down only
            // if it still owns the session; if shutdown already won the TryRemove it leaves the client alone.
            await RejectEscapedCreateAsync(session, involuntaryRecreate, wasRegistered: true, cancellationToken);
            throw new InvalidOperationException(
                $"{ServiceName} daemon is shutting down; refusing to register a new session.");
        }

        // Persist session to database for admin visibility and orphan tracking. On reconnect the backing
        // record was just flipped to Orphaned by MarkOrphansAsync, so reactivate it IN PLACE (reusing the
        // same unique SessionId keeps the PrefillHistory linkage) rather than inserting a duplicate.
        if (isReconnect)
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
                ServiceName);
        }

        if (guestStart != null)
        {
            guestStart.Registration.TrySetResult();
            GuestGate.Check(guestStart);
            if (!IsSessionLive(session))
                throw new ForbiddenException("This prefill session was stopped.");
        }
        _logger.LogInformation(
            "{Action} daemon session {SessionId} for user {UserId}",
            isReconnect ? "Re-adopted" : "Created", sessionId, userId);

        // A re-adopted persistent container keeps its login inside its named auth volume across a
        // manager restart, but this fresh in-memory session starts at InitialAuthState
        // (NotAuthenticated for account services). Reconcile once with the daemon's LIVE status,
        // routed through the same OnStatusChangeAsync transition every socket status push uses, so
        // a still-logged-in container immediately reads as Authenticated (and broadcasts
        // AuthStateChanged) instead of showing needs-login until the next interactive login.
        // Best-effort: an unresponsive daemon leaves the conservative NotAuthenticated default.
        if (isPersistent || !isReconnect)
        {
            try
            {
                var liveStatus = await daemonClient.GetStatusAsync(cancellationToken);
                if (liveStatus is not null)
                {
                    await OnStatusChangeAsync(session, liveStatus);
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
            throw new InvalidOperationException(
                $"{ServiceName} daemon is shutting down; refusing to register a new session.");
        }

        // Broadcast session creation to all clients for real-time updates (both hubs)
        var sessionDtoCreated = DaemonSessionDto.FromSession(session);
        if (guestStart != null)
        {
            GuestGate.Check(guestStart);
            if (!IsSessionLive(session))
                throw new ForbiddenException("This prefill session was stopped.");
        }
        try { await NotifyHubAsync(EventSessionCreated, sessionDtoCreated); }
        finally { guestStart?.Publication.TrySetResult(); }
        if (guestStart != null)
            GuestGate.Check(guestStart);

        // Whenever the fresh-login guard PRESERVED a FullPersistence volume login - a startup
        // outage-recreate, an errored-session replacement, or a manual fresh Start whose prior life
        // was not explicitly stopped - the daemon can authenticate itself, but only does so when it
        // receives a login command. Issue that command headlessly in the background so nobody has to
        // click Log in for a login that already exists (the intent: an admin should never be asked to
        // re-login while a valid stored login sits on the volume, and scheduled prefill keeps working
        // unattended after an outage). The invariants hold because they live in the guard itself: an
        // explicit admin stop marks the row Terminated so the guard ERASES (never fires here); guests,
        // re-adopts, and non-FullPersistence modes make the guard return false. An unusable stored
        // login is silently cancelled inside the attempt; no modal, notification, or challenge is ever
        // pushed - login-modal visibility stays anchored to the user's own action.
        if (volumeLoginPreserved)
        {
            LastHeadlessSelfAuthAttempt = InvokeSafeAsync(
                () => AttemptHeadlessPersistentSelfAuthAsync(session),
                nameof(AttemptHeadlessPersistentSelfAuthAsync));
        }

        return session;
    }

    /// <summary>
    /// Tears down everything a fresh create built when it is rejected at the shutdown creation gate
    /// (before registration): stops and removes the container - preserving a persistent named auth volume
    /// exactly as the normal teardown does, and deliberately WITHOUT a daemon logout so a FullPersistence
    /// volume login survives - deletes the session's command/response bind-mount directory, and disposes
    /// the client + CTS. No DB row exists at this point (the create inserts it only after registration),
    /// so there is nothing to flip to Terminated. Reuses the same container-removal primitive and
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

            // Bounded token (never CancellationToken.None on a shutdown path): an unresponsive Docker call
            // must not block shutdown.
            using var removeCts = new CancellationTokenSource(_containerTeardownTimeout);
            try
            {
                var outcome = await RemoveContainerForceAsync(session.ContainerId, removeCts.Token, removeVolumes: !session.IsPersistent);
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
    /// non-persistent sessions.
    /// </summary>
    private async Task TryBestEffortLogoutAsync(DaemonSession session, string context)
    {
        if (!session.IsPersistent)
        {
            return;
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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
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
            }
            else
            {
                _logger.LogInformation(
                    "Best-effort logout for persistent {ServiceName} session {SessionId} ({Context}): {Result}",
                    ServiceName, session.Id, context, outcome.Success ? "acknowledged" : "daemon reported failure");
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex,
                "Best-effort logout for persistent {ServiceName} session {SessionId} ({Context}) failed; proceeding anyway",
                ServiceName, session.Id, context);
        }
    }

    /// <summary>
    /// Belt-and-braces guard called once, right after a persistent session's daemon socket first
    /// connects: forces one best-effort logout for a NEWLY CREATED persistent container (never a
    /// re-adopted one) so it can never silently inherit a stale login left in its named auth volume by
    /// a previous life (e.g. a crash/reboot where <see cref="TerminateSessionAsync"/>'s stop-side
    /// logout never got a chance to run). No-op for non-persistent sessions and for re-adopted
    /// sessions - an adopted, still-running container's login IS its current life and must be
    /// preserved. Internal (not private) so tests can exercise the decision seam directly without
    /// standing up a real Docker container and daemon socket - see
    /// <c>InternalsVisibleTo("LancacheManager.Tests")</c> on the containing project.
    /// Returns true iff the guard deliberately PRESERVED a FullPersistence volume login (the skip
    /// branch) - the caller uses that to decide whether a headless self-auth attempt makes sense
    /// (there is a stored login on the volume worth issuing a login command for).
    /// </summary>
    internal async Task<bool> ApplyFreshPersistentLoginGuardAsync(
        DaemonSession session, bool isPersistent, bool isReconnect, bool involuntaryRecreate = false)
    {
        if (!isPersistent || isReconnect)
        {
            return false;
        }

        if (await ShouldPreserveVolumeLoginAsync(session, involuntaryRecreate))
        {
            _logger.LogInformation(
                "Skipping fresh-login guard for persistent {ServiceName} session {SessionId}: FullPersistence preserves the saved volume login across a verified involuntary death (explicit recreate flag or prior session not terminated)",
                ServiceName, session.Id);
            return true;
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
            ActivityDomains.PrefillSession, session.Id, ActivityAspects.Downloading, present && session.IsPrefilling);

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
