using Docker.DotNet;
using LancacheManager.Models;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Middleware;

namespace LancacheManager.Core.Services;

public abstract partial class PrefillDaemonServiceBase
{
    /// <summary>
    /// Creates/starts a PERSISTENT admin daemon session. Reuses <see cref="CreateSessionAsync"/> with
    /// <c>isPersistent=true</c>, which (a) stamps <see cref="DaemonSession.IsPersistent"/>, (b) sets the
    /// expiry to the admin-configured validity window, and (c) mounts a stable named auth volume so the
    /// daemon's OWN login persists inside the container across restarts. The container starts RUNNING;
    /// the manager performs NO auto-login and NEVER injects/transfers a token. If the named volume
    /// already holds a valid login the daemon auto-authenticates ITSELF; otherwise the admin logs in
    /// interactively via the UI. The <paramref name="service"/> argument identifies the platform for the
    /// caller; this daemon instance is already service-specific.
    /// </summary>
    public async Task<DaemonSession> StartPersistentSessionAsync(
        PrefillPlatform service,
        Guid userId,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Starting persistent {Service} session for user {UserId}", service, userId);

        // The persistent container starts RUNNING. There is intentionally NO manager-side auto-login
        // here and the manager NEVER injects/transfers a token (that breaks Steam across servers).
        // The container mounts its own named auth volume: if that volume already holds a valid login
        // the daemon auto-authenticates ITSELF; otherwise the admin logs in interactively via the UI.
        // The daemon's live status is the source of truth.
        var session = await CreateSessionAsync(
            userId,
            ipAddress,
            userAgent,
            SessionType.Admin,
            isPersistent: true,
            cancellationToken: cancellationToken);

        return session;
    }

    internal async Task<(DaemonSession Session, bool CreatedByEditSession)> StartPersistentSessionForEditAsync(
        PrefillPlatform service,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (!_containerGateway.IsAvailable)
        {
            // This method is only ever called over HTTP, so the sentence below survives to the client
            // as a 503 instead of being swallowed by the generic 500 message. The sibling throw in
            // CreateSessionAsync stays an InvalidOperationException because the hub catches that type
            // by name to forward the message on the SignalR path.
            throw new ServiceUnavailableException(
                "Docker is not running or not accessible. Please start Docker Desktop and try again.")
            {
                StageKey = "errors.prefill.dockerUnavailable"
            };
        }

        await _persistentStartLock.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation(
                "Starting edit-session-owned persistent {Service} session for user {UserId}",
                service,
                userId);

            var createdByEditSession = false;
            string? createdSessionId = null;
            string? createdContainerId = null;
            try
            {
                var session = await CreateSessionCoreAsync(
                    userId,
                    ipAddress: null,
                    userAgent: null,
                    SessionType.Admin,
                    isPersistent: true,
                    reuseExistingSession: true,
                    persistentExpiresAtOverrideUtc: null,
                    involuntaryRecreate: false,
                    cancellationToken,
                    persistentCreationResult: created => createdByEditSession = created,
                    persistentContainerCreated: (sessionId, containerId) =>
                    {
                        createdSessionId = sessionId;
                        createdContainerId = containerId;
                    });

                return (session, createdByEditSession);
            }
            catch (Exception startError)
                when (createdSessionId is not null && createdContainerId is not null)
            {
                try
                {
                    await RollbackPersistentSessionStartForEditCoreAsync(
                        createdSessionId,
                        createdContainerId);
                }
                catch (Exception rollbackError)
                {
                    throw new PersistentPrefillEditStartRollbackException(
                        createdSessionId,
                        createdContainerId,
                        new AggregateException(startError, rollbackError));
                }

                throw;
            }
        }
        finally
        {
            _persistentStartLock.Release();
        }
    }

    internal async Task RollbackPersistentSessionStartForEditAsync(
        string sessionId,
        string containerId)
    {
        await _persistentStartLock.WaitAsync(CancellationToken.None);
        try
        {
            await RollbackPersistentSessionStartForEditCoreAsync(sessionId, containerId);
        }
        finally
        {
            _persistentStartLock.Release();
        }
    }

    private async Task RollbackPersistentSessionStartForEditCoreAsync(
        string sessionId,
        string containerId)
    {
        if (GetSession(sessionId) is { Runs.IsEmpty: false, IsPrefilling: true }) return;
        if (GetSession(sessionId) is not null)
        {
            await TerminateSessionAsync(
                sessionId,
                "Scheduled-prefill edit start rolled back",
                force: true,
                terminatedBy: "edit-session-cleanup");
        }
        else
        {
            var removal = await RemoveContainerForceAsync(
                containerId,
                CancellationToken.None,
                removeVolumes: false);
            if (removal == ContainerRemovalOutcome.RemovalInProgress)
            {
                try
                {
                    await _containerGateway.InspectContainerAsync(
                        containerId,
                        CancellationToken.None);
                }
                catch (DockerContainerNotFoundException)
                {
                    return;
                }

                throw new InvalidOperationException(
                    $"Persistent container {containerId} for edit-owned session {sessionId} is still being removed.");
            }
        }

        try
        {
            await _containerGateway.InspectContainerAsync(containerId, CancellationToken.None);
        }
        catch (DockerContainerNotFoundException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Persistent container {containerId} for edit-owned session {sessionId} survived rollback.");
    }

    /// <summary>
    /// Stops a PERSISTENT session's container via the existing teardown path. Because the session is
    /// persistent, <see cref="TerminateSessionAsync"/> tears the container down with RemoveVolumes=false,
    /// so the daemon's own named auth volume survives and a subsequent <see cref="StartPersistentSessionAsync"/>
    /// re-mounts the same login. No-op if the session is unknown.
    /// </summary>
    public Task StopPersistentSessionAsync(string sessionId, string? terminatedBy = null)
    {
        // Host-shutdown vs explicit admin-stop race: on shutdown StopAsync detaches KeepAcrossRestart /
        // FullPersistence persistent sessions, deliberately leaving the container running WITH its login for
        // re-adoption, and can win TerminateSessionAsync's single-owner TryRemove before an explicit stop
        // reaches it - which would then early-return and report this stop as a silent success while the login
        // survives the restart, violating the explicit-stop-always-erases invariant. During shutdown an
        // explicit stop cannot guarantee that erase (the detach owns the container), so fail loudly rather
        // than silently no-op: the login is preserved for re-adoption and can be stopped again after restart.
        // The only caller is the admin stop endpoint, so this refusal is client-visible: a conflict keeps
        // the "stop it again after the restart" instruction in the response body, which the generic 500
        // body would drop.
        if (_stopping)
        {
            throw new ConflictException(
                $"{ServiceName} daemon is shutting down; the persistent session {sessionId} cannot be stopped right now. " +
                "Its login is preserved for re-adoption on the next start; stop it again after the manager restarts.")
            {
                StageKey = "errors.prefill.daemonShuttingDown",
                Context = new() { ["service"] = ServiceName, ["sessionId"] = sessionId }
            };
        }

        var session = GetSession(sessionId);
        if (session == null)
        {
            _logger.LogWarning("StopPersistentSessionAsync: session {SessionId} not found", sessionId);
            return Task.CompletedTask;
        }

        if (!session.IsPersistent)
        {
            _logger.LogWarning(
                "StopPersistentSessionAsync called for non-persistent session {SessionId}; its volume will be removed on teardown",
                sessionId);
        }

        return StopPersistentSessionCoreAsync(sessionId, terminatedBy);
    }

    /// <summary>
    /// Runs the actual teardown under <see cref="_persistentStartLock"/> so a Stop can never interleave
    /// with a concurrent persistent Start/adopt/replace for this service - e.g. a Stop
    /// racing a Start that is mid-adopt of the very container being stopped. Safe against the lock
    /// already being held by <c>CreateSessionCoreAsync</c>'s Error-state-replacement call into
    /// <see cref="TerminateSessionAsync"/>: that call happens on a DIFFERENT logical entry (the create
    /// path already holds the lock and never re-enters it), and <see cref="TerminateSessionAsync"/>
    /// itself never acquires <see cref="_persistentStartLock"/> - only the two public entry points
    /// (this one and the persistent branch of <c>CreateSessionAsync</c>) do, so there is no reentrant
    /// self-deadlock.
    /// </summary>
    private async Task StopPersistentSessionCoreAsync(string sessionId, string? terminatedBy)
    {
        await _persistentStartLock.WaitAsync();
        try
        {
            // Reuse the existing teardown; IsPersistent keeps RemoveVolumes=false so the daemon's auth survives.
            await TerminateSessionAsync(sessionId, "Persistent session stopped", force: false, terminatedBy: terminatedBy);
        }
        finally
        {
            _persistentStartLock.Release();
        }
    }

    internal async Task<bool> StopPersistentSessionIfOwnedByEditAsync(
        string sessionId,
        Func<bool> stillOwned,
        string? terminatedBy = null)
    {
        await _persistentStartLock.WaitAsync();
        try
        {
            if (!stillOwned() || GetSession(sessionId) is not { IsPersistent: true } session
                || (!session.Runs.IsEmpty && session.IsPrefilling))
            {
                return false;
            }

            await TerminateSessionAsync(
                sessionId,
                "Scheduled-prefill edit session cleaned up",
                force: false,
                terminatedBy: terminatedBy);
            return true;
        }
        finally
        {
            _persistentStartLock.Release();
        }
    }

    /// <summary>
    /// Pure anchor for a persistent session's validity window: <paramref name="createdAtUtc"/> plus the
    /// admin-configured <paramref name="validityDays"/>. This is the exact formula used at session
    /// creation and preserved by the re-adopt path, so re-anchoring with the same validity is a no-op
    /// and changing it moves the date deterministically (never silently extends like now+validity would).
    /// </summary>
    public static DateTime ComputePersistentExpiry(DateTime createdAtUtc, int validityDays)
        => createdAtUtc.AddDays(validityDays);

    /// <summary>
    /// Expiry anchor for a FullPersistence container recreated after it died while the manager was down.
    /// Prefers the prior life's still-future validity window (<paramref name="priorExpiresAtUtc"/>, the
    /// last DB session row's <c>ExpiresAtUtc</c>) so an admin-configured window is not silently extended
    /// by the outage; falls back to <paramref name="nowUtc"/> + <paramref name="validityDays"/> when no
    /// future record survives. Mirrors the re-adopt anchor in
    /// <see cref="ReconnectPersistentSessionAsync"/>.
    /// </summary>
    public static DateTime ResolveRecreatedPersistentExpiry(DateTime? priorExpiresAtUtc, DateTime nowUtc, int validityDays)
        => priorExpiresAtUtc is { } prior && prior > nowUtc
            ? prior
            : ComputePersistentExpiry(nowUtc, validityDays);

    /// <summary>
    /// Re-anchors every RUNNING persistent session's <see cref="DaemonSession.ExpiresAt"/> to
    /// <c>createdAt + validityDays</c> (idempotent) and clears a stale <see cref="DaemonSession.NeedsRelogin"/>
    /// when the new window is in the future (the reaper re-flags it if it is already past). Also persists
    /// the new <c>ExpiresAtUtc</c> to each session's DB record so a manager restart re-adopts the new
    /// window rather than a stale longer one (the re-adopt path prefers a future <c>dbRecord.ExpiresAtUtc</c>).
    /// </summary>
    public async Task UpdatePersistentSessionExpiryAsync(int validityDays)
    {
        var now = DateTime.UtcNow;
        var targets = _sessions.Values
            .Where(s => s.IsPersistent && s.Status == DaemonSessionStatus.Active)
            .ToList();

        foreach (var session in targets)
        {
            session.ExpiresAt = ComputePersistentExpiry(session.CreatedAt, validityDays);
            if (session.ExpiresAt > now)
            {
                session.NeedsRelogin = false;
            }

            // Persist so the new window survives a restart (the re-adopt path prefers a future
            // dbRecord.ExpiresAtUtc; without this a shrink would leave the old longer value on disk).
            await _sessionService.UpdateExpiryAsync(session.Id, session.ExpiresAt);
        }
    }
}
