using LancacheManager.Models;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Middleware;

namespace LancacheManager.Core.Services;

public abstract partial class PrefillDaemonServiceBase
{
    /// <summary>
    /// The one adopt a person's Start, a scheduled run, the scheduler tick and Clear stored logins use. It
    /// connects Docker and refuses with the typed Docker-unavailable error, then under the start lock it
    /// finishes a pending image update, returns the registered session, or adopts the running container.
    /// Apart from finishing that image update, it creates no container and removes only a running container
    /// whose session was stopped with no teardown left. Returns the active persistent session, or null when
    /// no container can be adopted.
    /// </summary>
    public async Task<DaemonSession?> AdoptRunningPersistentSessionAsync(CancellationToken cancellationToken)
    {
        // The Docker sentence answers the HTTP Start as a 503 with its key, and the scheduler reports the key.
        await ConnectDockerAsync(cancellationToken);
        if (!_containerGateway.IsAvailable)
        {
            throw new ServiceUnavailableException(
                "Docker is not running or not reachable. Start Docker, or mount /var/run/docker.sock into the LANCache Manager container, then try again.")
            {
                StageKey = "errors.prefill.dockerUnavailable"
            };
        }

        await _persistentStartLock.WaitAsync(cancellationToken);
        try
        {
            if (GetPendingImageChange() is { } pending)
            {
                await ResumeImageChangeAsync(pending, cancellationToken);
                return GetActivePersistentSession();
            }

            if (GetActivePersistentSession() is { } active)
            {
                return active;
            }

            var decision = await ResolvePersistentContainerDecisionAsync(cancellationToken);
            if (decision.Action != PersistentContainerAction.Adopt)
            {
                return null;
            }

            // A stored teardown owns its container. With no teardown left, a stopped session's container
            // would otherwise run until a Start or a restart, since a Stop finds no session to end.
            var target = decision.Target!;
            if (IsTearingDown(target))
            {
                return null;
            }

            if (await IsStoppedSessionContainerAsync(target))
            {
                _logger.LogInformation(
                    "Removing running persistent {ServiceName} container {Id}; its session {SessionId} was stopped",
                    ServiceName, ShortContainerId(target.ID), target.Labels[SessionIdLabelKey]);
                await RemoveContainerForceAsync(target.ID, cancellationToken);
                return null;
            }

            return await AdoptPersistentContainerAsync(target, null, cancellationToken);
        }
        finally
        {
            _persistentStartLock.Release();
        }
    }

    /// <summary>
    /// Starts a persistent daemon session through <see cref="CreateSessionAsync"/> with <c>isPersistent=true</c>.
    /// A named auth volume keeps the daemon's own login across restarts, and the manager never injects a token.
    /// <paramref name="service"/> names the platform for the caller; this daemon is already service-specific.
    /// </summary>
    public async Task<DaemonSession> StartPersistentSessionAsync(
        PrefillPlatform service,
        Guid userId,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        // The adopt connects Docker and refuses first, so a Start that adopts a running container answers
        // without waiting on the image check of a create. A session that was already registered goes on to
        // the create, which checks its image before returning it.
        var registered = GetActivePersistentSession();
        var adopted = await AdoptRunningPersistentSessionAsync(cancellationToken);
        if (adopted is not null && !ReferenceEquals(adopted, registered))
        {
            return adopted;
        }

        _logger.LogInformation(
            "Starting persistent {Service} session for user {UserId}", service, userId);

        // Never inject a token: that breaks Steam across servers.
        var session = await CreateSessionAsync(
            userId,
            ipAddress,
            userAgent,
            SessionType.Admin,
            isPersistent: true,
            cancellationToken: cancellationToken);

        return session;
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
        if (session is not null && !session.IsPersistent)
        {
            _logger.LogWarning(
                "StopPersistentSessionAsync called for non-persistent session {SessionId}; its volume will be removed on teardown",
                sessionId);
        }

        var requestedChange = GetPendingImageChange();
        if (requestedChange is not null
            && !string.Equals(requestedChange.Session.Id, sessionId, StringComparison.Ordinal))
        {
            requestedChange = null;
        }

        return StopPersistentSessionCoreAsync(sessionId, terminatedBy, session, requestedChange);
    }

    /// <summary>
    /// Runs the actual teardown under <see cref="_persistentStartLock"/> so a Stop can never interleave
    /// with a concurrent persistent Start/adopt/replace for this service - e.g. a Stop
    /// racing a Start that is mid-adopt of the very container being stopped. <see cref="TerminateSessionAsync"/>
    /// never acquires <see cref="_persistentStartLock"/>, so a holder that calls it, such as this Stop or
    /// <c>CreateSessionCoreAsync</c>'s Error-state replacement, cannot deadlock on itself.
    /// </summary>
    private async Task StopPersistentSessionCoreAsync(
        string sessionId,
        string? terminatedBy,
        DaemonSession? requestedSession,
        PersistentImageChange? requestedChange)
    {
        await _persistentStartLock.WaitAsync();
        try
        {
            if (GetPendingImageChange() is { } pending
                && string.Equals(pending.Session.Id, sessionId, StringComparison.Ordinal))
            {
                await CancelPendingImageChangeAsync(
                    pending,
                    terminatedBy,
                    CancellationToken.None);
                return;
            }

            var session = GetSession(sessionId) ?? GetFailedTeardownSession(sessionId);
            if (session is null
                && ((requestedSession is { IsPersistent: true } && !IsSessionLive(requestedSession))
                    || requestedChange is not null))
            {
                session = GetActivePersistentSession();
            }

            if (session is null)
            {
                _logger.LogWarning("StopPersistentSessionAsync: session {SessionId} not found", sessionId);
                return;
            }

            // A teardown that Docker failed earlier, such as an errored session's replacement, is stored with
            // its own login choice. This Stop makes it an explicit stop that erases the login, and records the
            // stop before Docker is asked: that replacement already marked the row Orphaned.
            bool storedTeardown;
            lock (_terminationSync)
            {
                storedTeardown = _terminations.TryGetValue(session.Id, out var stored)
                    && !stored.Complete && stored.ImageChange is null;
                if (storedTeardown)
                {
                    stored!.KeepLogin = false;
                    stored.TerminatedBy = terminatedBy;
                }
            }
            if (storedTeardown)
            {
                await _sessionService.TerminateSessionAsync(
                    session.Id, "Persistent session stopped", terminatedBy, replaceOrphaned: true);
            }

            // Reuse the existing teardown; IsPersistent keeps RemoveVolumes=false so the daemon's auth survives.
            await TerminateSessionAsync(session.Id, "Persistent session stopped", force: false, terminatedBy: terminatedBy);
        }
        finally
        {
            _persistentStartLock.Release();
        }
    }

    /// <summary>
    /// A persistent session's re-login date: its last sign-in <paramref name="lastLoginAtUtc"/> plus the
    /// admin-configured <paramref name="validityDays"/>. A fresh create and a person's finished Log in start a
    /// window with it, and a validity save recomputes every date with it, so saving the same days again lands
    /// on the same date.
    /// </summary>
    public static DateTime ComputePersistentExpiry(DateTime lastLoginAtUtc, int validityDays)
        => lastLoginAtUtc.AddDays(validityDays);

    /// <summary>
    /// Sets every live persistent session's and stored row's re-login date to its last sign-in plus the saved
    /// validity days, and clears a stale <see cref="DaemonSession.NeedsRelogin"/> when the date is ahead. Dates
    /// never build on an earlier save, so saving again finishes a save that failed part way. Holds the start
    /// lock so a session that is registering is set too, and rewrites each row from memory so a Log in that
    /// lands during the save keeps its date.
    /// </summary>
    public async Task UpdatePersistentSessionExpiryAsync()
    {
        await _persistentStartLock.WaitAsync();
        try
        {
            var validityDays = _stateService.GetAdminPersistentLoginValidityDays();
            var now = DateTime.UtcNow;
            var running = _sessions.Values.Where(s => s.IsPersistent && s.Status != DaemonSessionStatus.Terminated).ToList();
            foreach (var session in running)
            {
                session.ExpiresAt = ComputePersistentExpiry(session.LastLoginAt, validityDays);
                if (session.ExpiresAt > now)
                {
                    session.NeedsRelogin = false;
                }
            }

            // An image update waiting on its new container registers that container with the date it carries.
            lock (_imageStateLock)
            {
                if (_pendingImageChange is { } pending)
                {
                    pending.ExpiresAt = ComputePersistentExpiry(pending.LastLoginAt, validityDays);
                }
            }

            await _sessionService.UpdateExpiryAsync(Platform, validityDays);
            foreach (var session in running)
            {
                await SaveLoginStateAsync(session);
                // Another open schedules tab reloads its container list on this event and shows the new date.
                await NotifyHubAsync(session, EventSessionUpdated, DaemonSessionDto.FromSession(session));
            }
        }
        finally
        {
            _persistentStartLock.Release();
        }
    }
}
