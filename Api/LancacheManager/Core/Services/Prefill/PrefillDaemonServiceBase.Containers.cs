using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Docker.DotNet;
using Docker.DotNet.Models;
using LancacheManager.Models;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Core.Services;

public abstract partial class PrefillDaemonServiceBase
{
    /// <summary>
    /// Cleans up orphaned prefill daemon containers from previous app runs.
    /// Looks for containers matching THIS service's container prefix pattern.
    /// </summary>
    private async Task CleanupOrphanedContainersAsync(CancellationToken cancellationToken)
    {
        if (!_containerGateway.IsAvailable) return;

        try
        {
            // Mark this service's "Active" sessions in DB as orphaned. Platform-scoped so a later
            // daemon's startup cannot re-orphan a row an earlier daemon just reactivated.
            await _sessionService.MarkOrphansAsync(Platform);

            // Find all running containers matching this service's prefix
            var containers = await _containerGateway.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["name"] = new Dictionary<string, bool>
                        {
                            [ContainerPrefix] = true
                        }
                    }
                },
                cancellationToken);

            if (containers.Count == 0)
            {
                _logger.LogInformation("No orphaned {ServiceName} prefill daemon containers found", ServiceName);
                return;
            }

            _logger.LogWarning("Found {Count} orphaned {ServiceName} prefill daemon containers to cleanup", containers.Count, ServiceName);

            foreach (var container in containers)
            {
                try
                {
                    // Persistent containers that are still running are meant to OUTLIVE a manager
                    // restart: leave them running here and let ReadoptPersistentContainersAsync
                    // reconnect to their daemon socket and rebuild the in-memory session. Only
                    // non-persistent (or stopped) containers are torn down as orphans below.
                    if (IsPersistentLabel(container.Labels) &&
                        string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation(
                            "Preserving running persistent {ServiceName} container {Name} ({Id}) for re-adoption",
                            ServiceName,
                            container.Names.FirstOrDefault() ?? "unknown",
                            ShortContainerId(container.ID));
                        continue;
                    }

                    // Stop and remove the container
                    if (container.State == "running")
                    {
                        await _containerGateway.StopContainerAsync(
                            container.ID,
                            new ContainerStopParameters { WaitBeforeKillSeconds = 1 },
                            cancellationToken);
                    }

                    // RemoveVolumes: a login-required daemon (Xbox/Epic) stores its anonymous token
                    // in an anonymous container volume; without RemoveVolumes those volumes linger
                    // after teardown and accumulate. Force kills it if still running.
                    if (await RemoveContainerForceAsync(container.ID, cancellationToken, removeVolumes: true) != ContainerRemovalOutcome.Removed)
                    {
                        // Already gone, or another sweep is mid-removal - not actually cleaned up by
                        // THIS call, so don't mark it cleaned or log success for it.
                        _logger.LogDebug("Container {Id} removal already in progress or already gone, skipping cleanup mark", ShortContainerId(container.ID));
                        continue;
                    }

                    _logger.LogInformation("Cleaned up orphaned container: {Name} ({Id})",
                        container.Names.FirstOrDefault() ?? "unknown",
                        ShortContainerId(container.ID));

                    // Mark as cleaned in database
                    await _sessionService.MarkOrphanCleanedAsync(container.ID);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cleanup orphaned container {Id}", container.ID[..12]);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during orphaned container cleanup");
        }
    }

    /// <summary>
    /// True when the supplied container labels carry <c>lancache.prefill.persistent=true</c>.
    /// </summary>
    private static bool IsPersistentLabel(IDictionary<string, string>? labels)
        => labels != null
           && labels.TryGetValue(PersistentLabelKey, out var value)
           && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Re-adopts persistent prefill daemon containers that survived a manager restart. These containers
    /// are LEFT RUNNING by <see cref="CleanupOrphanedContainersAsync"/> (which only removes
    /// non-persistent / stopped containers). For each still-running container carrying THIS service's
    /// <c>lancache.prefill.persistent=true</c> label, this reconnects to the existing daemon socket and
    /// rebuilds the in-memory <see cref="DaemonSession"/> WITHOUT creating a new container, so the
    /// persistent session reappears (with its daemon-reported auth state) after a restart instead of the
    /// admin having to click Start again. Each container is reconnected in isolation: one failing
    /// container never aborts the others or startup. A labeled container that is not running is left for
    /// the admin to Start.
    /// </summary>
    private async Task ReadoptPersistentContainersAsync(CancellationToken cancellationToken)
    {
        if (!_containerGateway.IsAvailable) return;

        try
        {
            // Prefix-based (not exact-name) filter deliberately: this also catches leftover
            // random-suffix persistent containers from before deterministic naming shipped, so the
            // migration below cleans those up too, not just future exact-name duplicates.
            var containers = await _containerGateway.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["name"] = new Dictionary<string, bool> { [ContainerPrefix] = true },
                        ["label"] = new Dictionary<string, bool> { [$"{PersistentLabelKey}=true"] = true }
                    }
                },
                cancellationToken);

            // The most recent persistent DB row for this service drives BOTH whether a vanished container
            // may be recreated (FullPersistence + enabled + last life ended involuntarily) AND the
            // recreated session's expiry anchor. Fetched once for this reconcile pass.
            var latestRow = await _sessionService.GetLatestPersistentSessionAsync(Platform);
            var shouldRecreate = ShouldRecreatePersistentContainer(latestRow);

            if (containers.Count == 0)
            {
                // Retry arm: a prior FullPersistence recreate may have removed the dead
                // container then failed to create the replacement, leaving zero containers but a
                // non-Terminated DB row. Recreate now so a transient failure does not degrade into a
                // manual-start-only state. No container ever existed for a never-started service, so a
                // null / Terminated latest row (shouldRecreate == false) correctly creates nothing.
                if (shouldRecreate)
                {
                    _logger.LogInformation(
                        "Startup re-adopt: no persistent {ServiceName} container exists but the last session ended involuntarily under FullPersistence; recreating from the saved volume login",
                        ServiceName);
                    await RecreatePersistentContainerAsync(latestRow, targetToRemove: null, cancellationToken);
                }
                return;
            }

            var decision = PersistentSingletonGates.DecideExistingContainerAction(
                containers.ToList(), GetAdoptedPersistentContainerIds(), shouldRecreate);

            // Adopt-newest-remove-rest: whenever more than one persistent container matched, every
            // container besides the one we are about to adopt/remove is a leaked duplicate from M1 -
            // clean them all up as a one-time migration regardless of which branch below runs.
            foreach (var extra in decision.ExtrasToRemove)
            {
                _logger.LogWarning(
                    "Startup re-adopt: removing extra/leaked persistent {ServiceName} container {Id}",
                    ServiceName, ShortContainerId(extra.ID));
                await RemoveContainerForceAsync(extra.ID, cancellationToken);
            }

            switch (decision.Action)
            {
                case PersistentContainerAction.Remove:
                    // No running container to adopt - only stopped/leftover ones. Nothing legitimately
                    // running is affected by removing them; CleanupOrphanedContainersAsync would never
                    // touch these (it exempts anything persistent-labeled), so this is the only place
                    // that reaps a dead persistent container left behind by a prior crash.
                    _logger.LogInformation(
                        "Startup re-adopt: removing stopped persistent {ServiceName} container {Id} (no running container to adopt)",
                        ServiceName, ShortContainerId(decision.Target!.ID));
                    await RemoveContainerForceAsync(decision.Target!.ID, cancellationToken);
                    return;

                case PersistentContainerAction.Recreate:
                    // FullPersistence, enabled service whose container died involuntarily while the manager
                    // was down: remove the dead container, then recreate it so the daemon self-authenticates
                    // from its still-populated named auth volume (the fresh-login guard preserves it).
                    _logger.LogInformation(
                        "Startup re-adopt: persistent {ServiceName} container {Id} died while the manager was down; FullPersistence is enabled and the last session ended involuntarily, so removing it and recreating from the saved volume login",
                        ServiceName, ShortContainerId(decision.Target!.ID));
                    await RecreatePersistentContainerAsync(latestRow, targetToRemove: decision.Target, cancellationToken);
                    return;

                case PersistentContainerAction.Adopt:
                    break;

                default:
                    // CreateFresh (already adopted or nothing to do) / RetryLater (mid-removal - the
                    // next Start attempt or restart will re-decide) - nothing more to do at startup.
                    return;
            }

            var target = decision.Target!;
            _logger.LogInformation(
                "Re-adopting running persistent {ServiceName} prefill daemon container {Id} after restart",
                ServiceName, ShortContainerId(target.ID));

            try
            {
                await ReconnectPersistentSessionAsync(target, cancellationToken);

                if (GetActivePersistentSession() == null)
                {
                    // ReconnectPersistentSessionAsync has several silent (non-throwing) failure exits
                    // (missing session-id label, missing socket secret, etc.). Left alone, the
                    // container keeps running but is invisible to _sessions forever - exactly leak M1.
                    // Remove it instead so it cannot become an invisible zombie.
                    _logger.LogWarning(
                        "Re-adopt of persistent {ServiceName} container {Id} produced no active session; removing it",
                        ServiceName, ShortContainerId(target.ID));
                    await RemoveContainerForceAsync(target.ID, cancellationToken);
                }
            }
            catch (FileNotFoundException)
            {
                // The container outlived its data directory: the manager only bind-mounts the folder
                // the daemon publishes its socket into, so wiping the data directory leaves a running
                // container listening on a path that no longer exists on this host. Removing it is the
                // whole answer, and it is the outcome this path is written to produce, so it reads as
                // one line rather than a stack trace for a failure nobody has to look into. The reason
                // was already logged where the socket was found missing.
                _logger.LogInformation(
                    "Removing persistent {ServiceName} container {Id}: its data directory is gone, so it can no longer be reached",
                    ServiceName, ShortContainerId(target.ID));
                await RemoveContainerForceAsync(target.ID, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to re-adopt persistent {ServiceName} container {Id}; removing it so it cannot become an invisible zombie",
                    ServiceName, ShortContainerId(target.ID));
                await RemoveContainerForceAsync(target.ID, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during persistent {ServiceName} container re-adoption", ServiceName);
        }
    }

    /// <summary>
    /// Whether a persistent container that died while the manager was down should be RECREATED (rather
    /// than merely reaped) on startup: true only for a FullPersistence, enabled service. A null config,
    /// or a disabled/unknown service, returns false - the manager never fabricates a container for a
    /// service the admin did not enable. Drives the
    /// <see cref="PersistentSingletonGates.DecideExistingContainerAction"/> recreate decision from
    /// <see cref="ReadoptPersistentContainersAsync"/>.
    /// </summary>
    /// <summary>
    /// Whether a persistent container that vanished should be RECREATED (rather than merely reaped or
    /// left absent) on startup, given the most recent persistent DB row for this service
    /// (<paramref name="latestRow"/>). Reads config for the effective mode + enabled flag and delegates
    /// the pure rule to <see cref="PersistentSingletonGates.ShouldRecreatePersistentContainer"/>: recreate
    /// only for a FullPersistence, enabled service whose last life ended involuntarily (row not
    /// Terminated). A null config, disabled/unknown service, admin-terminated row, or no row → false.
    /// </summary>
    private bool ShouldRecreatePersistentContainer(PrefillSession? latestRow)
    {
        var config = _stateService.GetScheduledPrefillConfig();
        if (config == null)
        {
            // Config is production-guaranteed non-null (Validate runs on every load); this only guards the
            // test null-returning state-service proxy, which can feed a null config through the startup path.
            return false;
        }

        // Resolve the service ONCE and derive both enabled + effective mode from it, instead of letting
        // GetEffectivePersistenceMode(Platform) re-walk GetServicesInRunOrder for the same service. Same
        // override-then-global precedence as that helper, including its fail-loud on a null global mode.
        var service = config.GetServicesInRunOrder().FirstOrDefault(s => s.ServiceId == Platform);
        var enabled = service?.Schedules.Any(schedule => schedule.Enabled) == true;
        var effectiveMode = service?.PersistenceMode
            ?? config.PersistenceMode
            ?? throw new InvalidOperationException(
                $"Scheduled prefill config's global PersistenceMode is null for {Platform}; ScheduledPrefillConfigFactory.Validate() must run first.");

        return PersistentSingletonGates.ShouldRecreatePersistentContainer(effectiveMode, enabled, latestRow?.Status);
    }

    /// <summary>
    /// Removes a dead persistent container (when <paramref name="targetToRemove"/> is non-null) and
    /// creates a fresh one for a FullPersistence service so the daemon self-authenticates from its named
    /// auth volume. Anchors the new session's expiry to the prior life's still-future validity window
    /// (from <paramref name="latestRow"/>) and marks the create involuntary so the fresh-login guard
    /// preserves the login. A create failure is logged but never aborts startup - the dead session's DB
    /// row stays non-Terminated, so the next startup's retry arm attempts the recreate again.
    /// </summary>
    private async Task RecreatePersistentContainerAsync(
        PrefillSession? latestRow, ContainerListResponse? targetToRemove, CancellationToken cancellationToken)
    {
        var anchoredExpiresAt = ResolveRecreatedPersistentExpiry(
            latestRow?.ExpiresAtUtc, DateTime.UtcNow, _stateService.GetAdminPersistentLoginValidityDays());

        if (targetToRemove != null)
        {
            await RemoveContainerForceAsync(targetToRemove.ID, cancellationToken);
        }

        try
        {
            // Non-reconnect create (fresh DB row via CreateSessionAsync -> isReconnect:false).
            // involuntaryRecreate makes the fresh-login guard preserve the login already on this
            // service's named auth volume so the recreated daemon self-authenticates.
            if (latestRow is not null)
                await _sessionService.InterruptRunsAsync(latestRow.SessionId, "instance-changed", cancellationToken);
            await CreateSessionAsync(
                ScheduledPrefillConstants.DeriveSystemUserId(),
                isPersistent: true,
                persistentExpiresAtOverrideUtc: anchoredExpiresAt,
                involuntaryRecreate: true,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            // Must not abort startup for the other services. The dead session's DB row stays
            // non-Terminated, so the next startup's zero-container retry arm attempts the recreate again.
            _logger.LogError(ex,
                "Failed to recreate persistent {ServiceName} container after outage; will retry on the next startup",
                ServiceName);
        }
    }

    /// <summary>
    /// Reconnects to one already-running persistent daemon container (identified by its
    /// <c>lancache.prefill.*</c> labels) and rebuilds + registers its in-memory session. Recovers the
    /// per-container socket secret and TCP host port from the container's inspected env / port bindings
    /// (these are not held in memory across a manager restart), recomputes the bind-mounted
    /// command/response/socket paths from the session id, and delegates the connect+register to
    /// <see cref="ConnectAndRegisterSessionAsync"/> with <c>isReconnect:true</c>. Never creates a container.
    /// </summary>
    private async Task ReconnectPersistentSessionAsync(ContainerListResponse container, CancellationToken cancellationToken)
    {
        if (!_containerGateway.IsAvailable) return;

        var containerId = container.ID;
        var shortId = ShortContainerId(containerId);

        var labels = container.Labels ?? new Dictionary<string, string>();
        if (!labels.TryGetValue(SessionIdLabelKey, out var sessionId) || string.IsNullOrWhiteSpace(sessionId))
        {
            _logger.LogWarning("Persistent container {Id} is missing the {Label} label; skipping re-adoption", shortId, SessionIdLabelKey);
            return;
        }

        // Defensive: re-adopt runs once on startup, but never adopt the same session twice.
        if (_sessions.ContainsKey(sessionId))
        {
            return;
        }

        labels.TryGetValue(UserIdLabelKey, out var userIdRaw);
        var userId = Guid.TryParse(userIdRaw, out var parsedUserId) ? parsedUserId : Guid.Empty;

        // Recover per-container secrets/ports from the live container (not held across restart).
        var inspect = await _containerGateway.InspectContainerAsync(containerId, cancellationToken);
        var env = inspect.Config?.Env ?? new List<string>();

        var socketSecret = GetEnvValue(env, "PREFILL_SOCKET_SECRET");
        if (string.IsNullOrEmpty(socketSecret))
        {
            _logger.LogWarning(
                "Persistent container {Id} has no PREFILL_SOCKET_SECRET in its env; cannot reconnect securely", shortId);
            return;
        }

        var useTcpMode = env.Any(e => e.StartsWith("PREFILL_TCP_PORT=", StringComparison.Ordinal));

        int? tcpHostPort = null;
        if (useTcpMode)
        {
            tcpHostPort = ResolveHostTcpPortFromInspect(inspect);
            if (!tcpHostPort.HasValue)
            {
                _logger.LogWarning(
                    "Persistent container {Id} is in TCP mode but no host port binding was found; cannot reconnect", shortId);
                return;
            }
        }

        // Recompute the bind-mounted command/response dirs + socket path from the session id. These
        // survive the manager restart because they live on the host (bind mounts), and the daemon
        // inside the running container is still listening on the same socket.
        var basePath = GetDaemonBasePath();
        var sessionPath = Path.Combine(basePath, "sessions", sessionId);
        var commandsDir = Path.Combine(sessionPath, "commands");
        var responsesDir = Path.Combine(sessionPath, "responses");
        var socketPath = Path.Combine(responsesDir, "daemon.sock");

        // This method only ever adopts a PERSISTENT container (called from ReadoptPersistentContainersAsync
        // and the persistent create/adopt path), so the fallback (used only when Docker returned no Names,
        // which should not happen in practice) must match the deterministic persistent name, not a
        // sessionId-based guest-shaped name.
        var containerName = container.Names?.FirstOrDefault()?.TrimStart('/') ?? $"{ContainerPrefix}persistent";

        // Preserve the original validity window when the prior DB record is available; otherwise restamp
        // from the admin-configured persistent login validity.
        var dbRecord = await _sessionService.GetSessionAsync(sessionId);
        var createdAtUtc = dbRecord?.CreatedAtUtc ?? DateTime.UtcNow;
        var expiresAt = dbRecord != null && dbRecord.ExpiresAtUtc > DateTime.UtcNow
            ? dbRecord.ExpiresAtUtc
            : DateTime.UtcNow.AddDays(_stateService.GetAdminPersistentLoginValidityDays());

        _logger.LogInformation(
            "Re-adopting persistent {ServiceName} session {SessionId} from running container {Id}",
            ServiceName, sessionId, shortId);

        await ConnectAndRegisterSessionAsync(
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
            isTemporary: false,
            isPersistent: true,
            ipAddress: null,
            userAgent: null,
            networkDiagnostics: null,
            isReconnect: true,
            involuntaryRecreate: false,
            cancellationToken);
    }


    /// <summary>
    /// Ids of every persistent session currently registered in-memory, keyed by their live
    /// container id. Used by the singleton gate to avoid re-adopting (and thus double-registering)
    /// a container that is already tracked.
    /// </summary>
    private HashSet<string> GetAdoptedPersistentContainerIds()
        => new(_sessions.Values.Where(s => s.IsPersistent).Select(s => s.ContainerId));

    /// <summary>
    /// Lists Docker containers matching this service's deterministic persistent name + label, then
    /// asks <see cref="PersistentSingletonGates.DecideExistingContainerAction"/> what to do about
    /// them. Retries briefly (a few x ~1s) while the decision is <see cref="PersistentContainerAction.RetryLater"/>
    /// (a match is mid-removal, e.g. logout's stop-then-start racing this call) before giving up and
    /// letting the caller proceed to create - <see cref="CreatePersistentContainerWithConflictRetryAsync"/>
    /// is the final backstop against a still-lingering name conflict at that point.
    /// </summary>
    private async Task<PersistentContainerDecision> ResolvePersistentContainerDecisionAsync(CancellationToken cancellationToken)
    {
        var deterministicName = $"{ContainerPrefix}persistent";
        const int maxRetryLaterAttempts = 3;

        for (var attempt = 0; ; attempt++)
        {
            var containers = await _containerGateway.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["name"] = new Dictionary<string, bool> { [deterministicName] = true },
                        ["label"] = new Dictionary<string, bool> { [$"{PersistentLabelKey}=true"] = true }
                    }
                },
                cancellationToken);

            var decision = PersistentSingletonGates.DecideExistingContainerAction(containers.ToList(), GetAdoptedPersistentContainerIds());

            if (decision.Action != PersistentContainerAction.RetryLater || attempt >= maxRetryLaterAttempts)
            {
                return decision;
            }

            _logger.LogInformation(
                "Persistent {ServiceName} container is mid-removal; retrying in 1s (attempt {Attempt}/{Max})",
                ServiceName, attempt + 1, maxRetryLaterAttempts);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    /// <summary>
    /// Creates the persistent container, retrying on a 409 "name already in use" conflict (another
    /// container still occupies the deterministic name - e.g. a concurrent removal that
    /// <see cref="ResolvePersistentContainerDecisionAsync"/> raced with). After a few short retries,
    /// force-removes whatever is occupying the name and makes one final create attempt. Follows the
    /// same message-matching <see cref="DockerApiException"/> handling style as <see cref="TerminateSessionAsync"/>.
    /// </summary>
    private async Task<CreateContainerResponse> CreatePersistentContainerWithConflictRetryAsync(
        CreateContainerParameters parameters, CancellationToken cancellationToken)
    {
        const int maxRetries = 3;
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                return await _containerGateway.CreateContainerAsync(parameters, cancellationToken);
            }
            catch (DockerApiException ex) when (IsNameConflict(ex))
            {
                _logger.LogWarning(
                    "Persistent container name {Name} still in use (attempt {Attempt}/{Max}); retrying",
                    parameters.Name, attempt + 1, maxRetries);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        _logger.LogWarning(
            "Persistent container name {Name} still conflicting after {Max} retries; force-removing and creating once more",
            parameters.Name, maxRetries);
        await ForceRemoveContainersByExactNameAsync(parameters.Name!, cancellationToken);
        return await _containerGateway.CreateContainerAsync(parameters, cancellationToken);
    }

    private static bool IsNameConflict(DockerApiException ex)
        => ex.Message.Contains("Conflict", StringComparison.OrdinalIgnoreCase)
           && ex.Message.Contains("already in use", StringComparison.OrdinalIgnoreCase);

    private async Task ForceRemoveContainersByExactNameAsync(string containerName, CancellationToken cancellationToken)
    {
        var matches = await _containerGateway.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["name"] = new Dictionary<string, bool> { [containerName] = true }
                }
            },
            cancellationToken);

        foreach (var match in matches.Where(c => (c.Names ?? new List<string>()).Any(n => n.TrimStart('/') == containerName)))
        {
            await RemoveContainerForceAsync(match.ID, cancellationToken);
        }
    }

    /// <summary>
    /// Outcome of a force-remove attempt. Distinguishes a container that is CONFIRMED absent now
    /// (<see cref="Removed"/> by this call, or <see cref="AlreadyAbsent"/>) from one whose absence is NOT
    /// yet confirmed (<see cref="RemovalInProgress"/> - another sweep is mid-removal), so a caller that must
    /// only act on a confirmed-absent container (e.g. deleting its bind-mount directory) can tell them apart.
    /// </summary>
    private enum ContainerRemovalOutcome
    {
        /// <summary>This call removed the container.</summary>
        Removed,
        /// <summary>The container was already gone (another teardown/sweep removed it, or AutoRemove did).</summary>
        AlreadyAbsent,
        /// <summary>Another removal is in flight; the container's absence is not yet confirmed.</summary>
        RemovalInProgress
    }

    /// <summary>
    /// Force-removes a container by id, tolerating the two races already handled elsewhere in this
    /// class (already gone; another sweep mid-removal) - see <see cref="TerminateSessionAsync"/> for
    /// the origin of this exact exception-matching shape. Returns <see cref="ContainerRemovalOutcome.Removed"/>
    /// when this call removed the container, <see cref="ContainerRemovalOutcome.AlreadyAbsent"/> when it was
    /// already gone (both mean the container is CONFIRMED absent now), or
    /// <see cref="ContainerRemovalOutcome.RemovalInProgress"/> when another removal is mid-flight (absence not
    /// yet confirmed). RemoveVolumes defaults to false: most callers force-remove a leaked/duplicate/stopped
    /// sibling that shares the SAME named auth volume with a surviving container
    /// (<see cref="GetPersistentConfigVolumeName"/>) and must never wipe it; pass <paramref name="removeVolumes"/>
    /// true only for teardown paths that own the container's volume outright (e.g. non-persistent session
    /// termination, orphan cleanup).
    /// </summary>
    private async Task<ContainerRemovalOutcome> RemoveContainerForceAsync(string containerId, CancellationToken cancellationToken, bool removeVolumes = false)
    {
        try
        {
            await _containerGateway.RemoveContainerAsync(
                containerId,
                new ContainerRemoveParameters { Force = true, RemoveVolumes = removeVolumes },
                cancellationToken);
            return ContainerRemovalOutcome.Removed;
        }
        catch (DockerContainerNotFoundException)
        {
            // Already gone - CONFIRMED absent (another teardown/sweep or AutoRemove got there first).
            return ContainerRemovalOutcome.AlreadyAbsent;
        }
        catch (DockerApiException ex) when (ex.Message.Contains("removal") && ex.Message.Contains("already in progress"))
        {
            // Another removal is already in flight; the container's absence is NOT yet confirmed.
            return ContainerRemovalOutcome.RemovalInProgress;
        }
    }

    private static string ShortContainerId(string containerId)
        => containerId.Length >= 12 ? containerId[..12] : containerId;

    /// <summary>
    /// Returns the value of a <c>KEY=VALUE</c> entry from a container's inspected env list, or null.
    /// </summary>
    private static string? GetEnvValue(IList<string> env, string key)
    {
        var prefix = key + "=";
        foreach (var entry in env)
        {
            if (entry.StartsWith(prefix, StringComparison.Ordinal))
            {
                return entry[prefix.Length..];
            }
        }
        return null;
    }

    /// <summary>
    /// Reads the published host TCP port from an inspected container's port bindings (host-config first,
    /// then live network settings). Returns null when no positive host port is published.
    /// </summary>
    private static int? ResolveHostTcpPortFromInspect(ContainerInspectResponse inspect)
    {
        static int? FirstHostPort(IDictionary<string, IList<PortBinding>>? bindings)
        {
            if (bindings == null) return null;
            foreach (var kvp in bindings)
            {
                var hostPort = kvp.Value?.FirstOrDefault()?.HostPort;
                if (!string.IsNullOrEmpty(hostPort) && int.TryParse(hostPort, out var port) && port > 0)
                {
                    return port;
                }
            }
            return null;
        }

        return FirstHostPort(inspect.HostConfig?.PortBindings) ?? FirstHostPort(inspect.NetworkSettings?.Ports);
    }

    /// <summary>
    /// Fetches and logs the daemon container's stdout/stderr to help diagnose socket connection failures.
    /// </summary>
    private async Task LogContainerLogsAsync(string containerId, string sessionId, CancellationToken cancellationToken)
    {
        if (!_containerGateway.IsAvailable) return;

        try
        {
            // Check if container is still running
            var inspect = await _containerGateway.InspectContainerAsync(containerId, cancellationToken);
            _logger.LogWarning("Daemon container state for session {SessionId}: Running={Running}, Status={Status}, ExitCode={ExitCode}",
                sessionId, inspect.State.Running, inspect.State.Status, inspect.State.ExitCode);

            var logParams = new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Tail = "50" };
            using var logStream = await _containerGateway.GetContainerLogsAsync(containerId, false, logParams, cancellationToken);
            using var memoryStream = new MemoryStream();
            await logStream.CopyOutputToAsync(null, memoryStream, null, cancellationToken);
            memoryStream.Position = 0;
            using var reader = new StreamReader(memoryStream);
            var logs = await reader.ReadToEndAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(logs))
            {
                _logger.LogWarning("Daemon container logs for session {SessionId}:\n{Logs}", sessionId, logs);
            }
            else
            {
                _logger.LogWarning("Daemon container produced no logs for session {SessionId}", sessionId);
            }
        }
        catch (DockerContainerNotFoundException)
        {
            _logger.LogWarning("Daemon container {ContainerId} already removed (AutoRemove) for session {SessionId}", containerId, sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not retrieve daemon container logs for session {SessionId}", sessionId);
        }
    }

    private static string GenerateSocketSecret()
    {
        // 32 bytes -> 64 hex chars, stable ASCII
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    private async Task EnsureImageExistsAsync(CancellationToken cancellationToken)
    {
        if (!_containerGateway.IsAvailable) return;

        var imageName = GetImageName();
        _logger.LogInformation("Pulling latest prefill daemon image: {ImageName}", imageName);

        try
        {
            // Always pull to ensure we have the latest version
            await _containerGateway.CreateImageAsync(
                new ImagesCreateParameters
                {
                    FromImage = imageName.Split(':')[0],
                    Tag = imageName.Contains(':') ? imageName.Split(':')[1] : "latest"
                },
                null,
                new Progress<JSONMessage>(msg =>
                {
                    if (!string.IsNullOrEmpty(msg.Status))
                    {
                        // Only log significant progress, not every layer
                        if (msg.Status.Contains("Pulling") || msg.Status.Contains("Downloaded") || msg.Status.Contains("up to date"))
                        {
                            _logger.LogInformation("Pull: {Status}", msg.Status);
                        }
                    }
                    if (!string.IsNullOrEmpty(msg.ErrorMessage))
                    {
                        _logger.LogError("Pull error: {Error}", msg.ErrorMessage);
                    }
                }),
                cancellationToken);

            var imageInfo = await _containerGateway.InspectImageAsync(imageName, cancellationToken);
            _logger.LogInformation("Image ready: {ImageName} (ID: {ImageId})", imageName, imageInfo.ID[..12]);
        }
        catch (Exception ex)
        {
            // Check if we have a local copy we can use
            try
            {
                var imageInfo = await _containerGateway.InspectImageAsync(imageName, cancellationToken);
                _logger.LogWarning(ex, "Failed to pull latest image, using cached version: {ImageId}", imageInfo.ID[..12]);
            }
            catch (DockerImageNotFoundException)
            {
                _logger.LogError(ex, "Failed to pull image {ImageName} and no cached version available. " +
                    "The {ServiceName} Prefill feature requires this image.",
                    imageName, ServiceName);
                throw;
            }
        }
    }

    private string GetDaemonBasePath()
    {
        var configured = _configuration["Prefill:DaemonBasePath"];
        if (!string.IsNullOrEmpty(configured))
        {
            return _pathResolver.ResolvePath(configured);
        }

        return _pathResolver.GetPrefillDirectory();
    }

    private bool ShouldUseTcpMode()
    {
        var useTcp = _networkOptions.CurrentValue.UseTcp;
        if (useTcp.HasValue)
        {
            return useTcp.Value;
        }

        return OperatingSystemDetector.IsWindows;
    }

    private int GetContainerTcpPort()
    {
        var configured = _configuration.GetValue<int?>("Prefill:TcpPort");
        return configured.HasValue && configured.Value > 0 ? configured.Value : DefaultTcpPort;
    }

    private int GetHostTcpPort()
    {
        var configured = _configuration.GetValue<int?>("Prefill:HostTcpPort");
        if (configured.HasValue && configured.Value > 0)
        {
            return configured.Value;
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private string GetTcpHost()
    {
        return _configuration["Prefill:TcpHost"] ?? "127.0.0.1";
    }

    private async Task<string> GetHostDataPathAsync(CancellationToken cancellationToken = default)
    {
        // Return cached value if available
        if (_cachedHostDataPath != null)
            return _cachedHostDataPath;

        // Check for explicit configuration first
        var configuredPath = _configuration["Prefill:HostDataPath"];
        if (!string.IsNullOrEmpty(configuredPath) &&
            !string.Equals(configuredPath, "auto", StringComparison.OrdinalIgnoreCase))
        {
            _cachedHostDataPath = _pathResolver.ResolvePath(configuredPath);
            return _cachedHostDataPath;
        }

        if (OperatingSystemDetector.IsWindows)
        {
            _cachedHostDataPath = _pathResolver.GetDataDirectory();
            return _cachedHostDataPath;
        }

        // Auto-detect by inspecting our own container's mounts
        if (_containerGateway.IsAvailable && _isRunningInContainer)
        {
            try
            {
                var containerId = GetOwnContainerId();
                if (!string.IsNullOrEmpty(containerId))
                {
                    var inspect = await _containerGateway.InspectContainerAsync(containerId, cancellationToken);

                    // Find the mount for /data
                    var dataMount = inspect.Mounts?.FirstOrDefault(m => m.Destination == "/data");
                    if (dataMount != null)
                    {
                        _cachedHostDataPath = dataMount.Source;
                        _logger.LogInformation("Auto-detected host data path: {HostDataPath}", _cachedHostDataPath);
                        return _cachedHostDataPath;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-detect host data path from container mounts");
            }
        }

        // Fallback - assume running directly on host
        _cachedHostDataPath = _isRunningInContainer ? "/data" : _pathResolver.GetDataDirectory();
        _logger.LogWarning("Could not auto-detect host data path, using fallback: {HostDataPath}. " +
            "Set Prefill__HostDataPath environment variable if prefill containers can't access command files.",
            _cachedHostDataPath);
        return _cachedHostDataPath;
    }

    private string? GetOwnContainerId()
    {
        // Try to get container ID from cgroup
        try
        {
            // In Docker, /proc/1/cpuset contains the container ID
            if (File.Exists("/proc/1/cpuset"))
            {
                var cpuset = File.ReadAllText("/proc/1/cpuset").Trim();
                // Format: /docker/<container_id> or /kubepods/.../<container_id>
                var parts = cpuset.Split('/');
                if (parts.Length > 0)
                {
                    var lastPart = parts[^1];
                    if (lastPart.Length >= 12)
                        return lastPart;
                }
            }

            // Try /proc/self/cgroup
            if (File.Exists("/proc/self/cgroup"))
            {
                var cgroup = File.ReadAllText("/proc/self/cgroup");
                foreach (var line in cgroup.Split('\n'))
                {
                    // Format: 0::/docker/<container_id>
                    if (line.Contains("/docker/"))
                    {
                        var idx = line.LastIndexOf("/docker/");
                        if (idx >= 0)
                        {
                            var id = line[(idx + 8)..].Trim();
                            if (id.Length >= 12)
                                return id;
                        }
                    }
                }
            }

            // Try hostname (often set to container ID in Docker)
            var hostname = Environment.GetEnvironmentVariable("HOSTNAME");
            if (!string.IsNullOrEmpty(hostname) && hostname.Length >= 12)
            {
                return hostname;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get own container ID");
        }

        return null;
    }

    private int GetSessionTimeoutMinutes()
    {
        return _configuration.GetValue<int>("Prefill:SessionTimeoutMinutes", DefaultSessionTimeoutMinutes);
    }

    private int GetStallTimeoutSeconds()
    {
        return _configuration.GetValue<int>("Prefill:StallTimeoutSeconds", DefaultStallTimeoutSeconds);
    }

    private int GetAbandonedLoginTimeoutSeconds()
    {
        return _configuration.GetValue<int>("Prefill:AbandonedLoginTimeoutSeconds", DefaultAbandonedLoginTimeoutSeconds);
    }

    /// <summary>
    /// Determines if host network mode should be used based on lancache-dns configuration.
    /// Returns true if lancache-dns uses host networking or if explicitly configured.
    /// </summary>
    private async Task<bool> ShouldUseHostNetworkingAsync(CancellationToken cancellationToken = default)
    {
        // Check explicit configuration first
        var networkMode = _networkOptions.CurrentValue.NetworkMode;
        if (!string.IsNullOrEmpty(networkMode) &&
            !string.Equals(networkMode, "auto", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Using explicit Prefill:NetworkMode configuration: {NetworkMode}", networkMode);
            return networkMode.Equals("host", StringComparison.OrdinalIgnoreCase);
        }

        // Auto-detect: if lancache-dns uses host networking, we should too
        if (_containerGateway.IsAvailable)
        {
            try
            {
                var containers = await _containerGateway.ListContainersAsync(
                    new ContainersListParameters { All = false },
                    cancellationToken);

                _logger.LogDebug("Searching for lancache-dns container among {Count} running containers", containers.Count);

                var dnsContainer = containers.FirstOrDefault(c =>
                    c.Names.Any(n =>
                        n.Contains("lancache-dns", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("lancachedns", StringComparison.OrdinalIgnoreCase) ||
                        (n.Contains("dns", StringComparison.OrdinalIgnoreCase) && n.Contains("lancache", StringComparison.OrdinalIgnoreCase))));

                if (dnsContainer != null)
                {
                    _logger.LogInformation("Found lancache-dns container: {ContainerName}", dnsContainer.Names.FirstOrDefault());
                    var inspect = await _containerGateway.InspectContainerAsync(dnsContainer.ID, cancellationToken);
                    _logger.LogInformation("lancache-dns network mode: {NetworkMode}", inspect.HostConfig.NetworkMode);
                    if (inspect.HostConfig.NetworkMode == "host")
                    {
                        _logger.LogInformation("Detected lancache-dns using host networking. Prefill containers will use host network mode.");
                        return true;
                    }
                }
                else
                {
                    _logger.LogDebug("No lancache-dns container found. Container names searched: {Names}",
                        string.Join(", ", containers.SelectMany(c => c.Names)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check lancache-dns network mode");
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the network mode for prefill containers.
    /// Options: "host" (use host networking), "bridge" (default), or a custom network name.
    /// </summary>
    private string? GetNetworkMode()
    {
        var networkMode = _networkOptions.CurrentValue.NetworkMode;
        if (string.Equals(networkMode, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return networkMode;
    }
}
