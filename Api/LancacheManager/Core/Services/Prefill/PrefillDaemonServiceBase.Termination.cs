using Docker.DotNet;
using Docker.DotNet.Models;
using LancacheManager.Models;
using LancacheManager.Core.Services.SteamPrefill;

namespace LancacheManager.Core.Services;

public abstract partial class PrefillDaemonServiceBase
{
    /// <summary>
    /// Checks every authenticated session against the username ban list and terminates the banned ones.
    /// This is the STRONG (re-auth-proof) username ban, for platforms whose auth flow does not reveal a
    /// username until AFTER authentication: OAuth (Epic) and device-code (Xbox) both learn the display
    /// name from the daemon, so the ban cannot be applied at credential time. A session whose daemon
    /// reports no stable display name is skipped and stays covered by the UserId ban enforced at
    /// session-create (which works, but is evadable by re-authenticating).
    ///
    /// OPT-IN: the base deliberately never calls this. Steam rejects a banned user earlier, at credential
    /// time, so invoking it from the base would bolt a second, later enforcement point onto a flow that
    /// has already refused the login. Services that need post-auth enforcement call it themselves, from
    /// <see cref="OnAuthenticatedAsync"/>.
    /// </summary>
    protected async Task KickBannedSessionsAsync()
    {
        foreach (var session in _sessions.Values)
        {
            if (session.AuthState != DaemonAuthState.Authenticated) continue;
            if (string.IsNullOrEmpty(session.Username)) continue;

            if (await _sessionService.IsUsernameBannedAsync(session.Username))
            {
                _logger.LogWarning(
                    "Blocked banned {ServiceName} user {Username} after authentication. Terminating session {SessionId}",
                    ServiceName, session.Username, session.Id);

                await TerminateSessionAsync(session.Id, "Banned by admin", true);
            }
        }
    }

    /// <summary>
    /// Terminates a session and cleans up resources
    /// </summary>
    /// <param name="force">If true, kills the container immediately without graceful shutdown</param>
    public Task TerminateSessionAsync(string sessionId, string reason = "User requested", bool force = false, string? terminatedBy = null)
    {
        SessionTermination termination;
        TaskCompletionSource? completion = null;
        Task work;
        lock (_terminationSync)
        {
            if (!_terminations.TryGetValue(sessionId, out termination!))
            {
                if (!_sessions.TryRemove(sessionId, out var session))
                    return Task.CompletedTask;
                termination = new SessionTermination { Session = session, Reason = reason, TerminatedBy = terminatedBy };
                _terminations[sessionId] = termination;
            }
            if (termination.Complete)
                return Task.CompletedTask;
            if (termination.Work is not { IsCompleted: false })
            {
                if (!termination.Removed && termination.Removal is { IsCompleted: true, IsCompletedSuccessfully: false })
                    termination.Removal = null;
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                termination.Work = completion.Task;
            }
            work = termination.Work;
        }
        if (force && !termination.Session.IsPersistent && !termination.Removed)
            _ = RemoveContainerAsync(termination);
        if (completion != null)
            _ = FinishTerminationAsync(termination, force, completion);
        return work;
    }

    private async Task FinishTerminationAsync(SessionTermination termination, bool force, TaskCompletionSource completion)
    {
        var session = termination.Session;
        try
        {
            // Dispatch physical teardown before cancellation callbacks, history or notification work.
            var removal = termination.Removed ? Task.CompletedTask : StopContainerAsync(termination, force);
            session.Status = DaemonSessionStatus.Error;
            session.ErrorMessage = "Session cleanup is in progress.";
            ClearPendingLoginChallenge(session);
            session.Client.ClearPendingChallenges();
            var cancellation = session.CancellationTokenSource.CancelAsync();
            CompleteLoginOperation(session);
            await removal;
            termination.Removed = true;
            await cancellation;
            foreach (var run in session.Runs.Values.Where(run => run.TerminalCompletedFlag == 0))
            {
                await run.PrefillWork.WaitAsync();
                try
                {
                    await FinishRunAsync(session, run, run.Snapshot with
                    {
                        State = "cancelled",
                        Reason = "session-stopped",
                        Sequence = run.Snapshot.Sequence + 1,
                        CancelledApps = Math.Max(run.Snapshot.CancelledApps, run.Snapshot.TotalApps
                            - run.Snapshot.CompletedApps - run.Snapshot.CachedApps - run.Snapshot.FailedApps - run.Snapshot.SkippedApps),
                        UpdatedAt = DateTimeOffset.UtcNow
                    }, [], CancellationToken.None, containerStopped: true);
                }
                finally { run.PrefillWork.Release(); }
            }
            var registration = GuestGate.GetStarts(session.UserId).FirstOrDefault(start => ReferenceEquals(start.Session, session));
            if (registration != null)
            {
                await registration.Registration.Task;
                await registration.Publication.Task;
            }

            await DrainSessionEventsAsync(session);
            if (!termination.HistoryClosed)
            {
                using var workCts = new CancellationTokenSource(_eventDrainTimeout);
                await session.PrefillWork.WaitAsync(workCts.Token);
                try
                {
                    if (!termination.EntryClosed)
                    {
                        string? appId;
                        long bytesDownloaded;
                        long totalBytes;
                        lock (session.PrefillLock)
                        {
                            appId = session.CurrentAppId;
                            bytesDownloaded = session.CurrentBytesDownloaded;
                            totalBytes = session.CurrentTotalBytes;
                        }
                        if (appId is not null)
                            await _sessionService.CompleteEntryAsync(session.Id, appId, "Cancelled",
                                bytesDownloaded, totalBytes, $"Session terminated: {termination.Reason}");
                        termination.EntryClosed = true;
                    }
                    await _sessionService.CancelEntriesAsync(session.Id);
                    termination.HistoryClosed = true;
                }
                finally { session.PrefillWork.Release(); }
            }
            if (!termination.Persisted)
            {
                await _sessionService.TerminateSessionAsync(session.Id, termination.Reason, termination.TerminatedBy);
                termination.Persisted = true;
            }
            session.Status = DaemonSessionStatus.Terminated;
            session.EndedAt ??= DateTime.UtcNow;
            session.ErrorMessage = null;
            if (!termination.GlobalSent)
            {
                await NotifyHubAsync(EventSessionTerminated, new { sessionId = session.Id, reason = termination.Reason });
                termination.GlobalSent = true;
            }
            if (!termination.OwnerSent)
            {
                foreach (var connection in session.SubscribedConnections.ToArray())
                {
                    if (termination.SentConnections.Contains(connection))
                        continue;
                    await SendToClientAsync(connection, EventSessionEnded,
                        new { sessionId = session.Id, reason = termination.Reason });
                    termination.SentConnections.Add(connection);
                }
                termination.OwnerSent = true;
            }
            if (!termination.ActivityRetired)
            {
                await ReportSessionActivityAsync(session, present: false);
                termination.ActivityRetired = true;
            }
            if (!termination.Disposed)
            {
                session.Client.Dispose();
                session.CancellationTokenSource.Dispose();
                DeleteSessionDirectory(session);
                termination.Disposed = true;
            }
            lock (_terminationSync)
            {
                termination.Complete = true;
                _terminations.Remove(session.Id);
            }
            if (!IsAnyDaemonAuthenticated())
                FireAndForgetAsync(OnAllSessionsLoggedOutAsync, nameof(OnAllSessionsLoggedOutAsync));
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            session.Status = DaemonSessionStatus.Error;
            session.ErrorMessage = "Session cleanup is incomplete. Retry termination.";
            if (session.IsTemporary && PrefillSessionService.IsTerminatableByAdmin(session))
                GuestGate.Retain(session.UserId);
            _logger.LogWarning(ex, "Session cleanup remains incomplete for {SessionId}", session.Id);
            completion.TrySetException(ex);
        }
    }

    private Task RemoveContainerAsync(SessionTermination termination)
    {
        TaskCompletionSource completion;
        lock (_terminationSync)
        {
            if (termination.Removal != null)
                return termination.Removal;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            termination.Removal = completion.Task;
        }
        _ = RemoveAsync();
        return completion.Task;

        async Task RemoveAsync()
        {
            try
            {
                if (!string.IsNullOrEmpty(termination.Session.ContainerId))
                    await RemoveGuestContainerAsync(termination.Session.ContainerId, !termination.Session.IsPersistent);
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }
    }

    private async Task StopContainerAsync(SessionTermination termination, bool force)
    {
        var session = termination.Session;
        if (session.IsPersistent)
            await TryBestEffortLogoutAsync(session, "session stop/teardown");
        if (string.IsNullOrEmpty(session.ContainerId))
            return;
        bool removalStarted;
        lock (_terminationSync) removalStarted = termination.Removal != null;
        if (!force && !removalStarted)
        {
            try
            {
                using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await session.Client.ShutdownAsync(shutdownCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Graceful shutdown failed for {SessionId}; forcing removal", session.Id);
            }
            lock (_terminationSync) removalStarted = termination.Removal != null;
            if (!removalStarted)
            {
                try
                {
                    using var stopCts = new CancellationTokenSource(_containerTeardownTimeout);
                    await _containerGateway.StopContainerAsync(session.ContainerId,
                        new ContainerStopParameters { WaitBeforeKillSeconds = 1 }, stopCts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Container stop failed for {SessionId}; forcing removal", session.Id);
                }
            }
        }
        await RemoveContainerAsync(termination);
    }

    private async Task RemoveGuestContainerAsync(string containerId, bool removeVolumes)
    {
        if (!_containerGateway.IsAvailable)
            throw new InvalidOperationException("Docker is unavailable; container absence cannot be confirmed.");
        try
        {
            using var killCts = new CancellationTokenSource(_containerTeardownTimeout);
            await _containerGateway.KillContainerAsync(containerId, new ContainerKillParameters(), killCts.Token);
        }
        catch (DockerContainerNotFoundException)
        {
            // Removal below confirms absence and handles an already-removed container.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Container kill failed for {ContainerId}; forcing removal", containerId);
        }
        using var removeCts = new CancellationTokenSource(_containerTeardownTimeout);
        var outcome = await RemoveContainerForceAsync(containerId, removeCts.Token, removeVolumes);
        if (outcome == ContainerRemovalOutcome.RemovalInProgress)
            throw new InvalidOperationException("Container removal is still in progress.");
    }

    /// <summary>
    /// Gets a session by ID
    /// </summary>
    public DaemonSession? GetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    /// <summary>
    /// Gets all active sessions
    /// </summary>
    public IEnumerable<DaemonSession> GetAllSessions(bool includeTerminating = false)
    {
        lock (_terminationSync)
        {
            return includeTerminating
                ? _sessions.Values.Concat(_terminations.Values.Where(t => !t.Complete && !t.Session.IsPersistent).Select(t => t.Session)).ToList()
                : _sessions.Values.ToList();
        }
    }

    /// <summary>
    /// Returns the single running persistent (admin, named-volume) session for this daemon, or
    /// <c>null</c> when none is up. Centralizes the "running persistent session" lookup so the
    /// scheduled-prefill reuse path and <c>PersistentPrefillController</c> resolve it identically.
    /// </summary>
    public DaemonSession? GetActivePersistentSession()
    {
        return _sessions.Values.FirstOrDefault(s => s.IsPersistent && s.Status == DaemonSessionStatus.Active);
    }

    /// <summary>
    /// Resolves the concrete daemon singleton for a given platform. The single canonical copy of a
    /// switch that was previously duplicated verbatim in <c>PersistentPrefillController</c> and
    /// <c>ScheduledPrefillService</c> — both now call this instead. <c>default</c> is unreachable for
    /// any defined <see cref="PrefillPlatform"/> value; it stays defensive (returns null rather than
    /// throwing) to match the exact behavior of both prior copies.
    /// </summary>
    public static PrefillDaemonServiceBase? ResolveDaemon(IServiceProvider provider, PrefillPlatform platform)
    {
        switch (platform)
        {
            case PrefillPlatform.Steam:
                return provider.GetService<SteamDaemonService>();
            case PrefillPlatform.Epic:
                return provider.GetService<EpicPrefillDaemonService>();
            case PrefillPlatform.Xbox:
                return provider.GetService<XboxPrefillDaemonService>();
            case PrefillPlatform.BattleNet:
                return provider.GetService<BattleNetDaemonService>();
            case PrefillPlatform.Riot:
                return provider.GetService<RiotDaemonService>();
            default:
                return null;
        }
    }

    /// <summary>
    /// Resolves all platform daemon singletons. The single canonical copy of a generator previously
    /// duplicated verbatim in <c>PersistentPrefillController</c>. Enumerates <see cref="PrefillPlatform"/>
    /// itself rather than a hand-duplicated platform list, so a future platform added to the enum is
    /// picked up here automatically - only <see cref="ResolveDaemon"/>'s switch needs a matching case,
    /// since that is an unavoidable mapping to a concrete type.
    /// </summary>
    public static IEnumerable<PrefillDaemonServiceBase> ResolveAllDaemons(IServiceProvider provider)
    {
        foreach (var platform in Enum.GetValues<PrefillPlatform>())
        {
            var daemon = ResolveDaemon(provider, platform);
            if (daemon != null)
            {
                yield return daemon;
            }
        }
    }

    internal static async Task<GuestPrefillStopResult> TerminateGuestSessionsAsync(
        IEnumerable<PrefillDaemonServiceBase> daemons, Guid userId, string reason, string? terminatedBy = null)
    {
        var services = daemons.ToArray();
        var targets = services.SelectMany(daemon => daemon.GetAllSessions(includeTerminating: true)
            .Where(session => session.UserId == userId && session.IsTemporary && PrefillSessionService.IsTerminatableByAdmin(session))
            .Select(session => (Daemon: daemon, Session: session))).ToArray();
        var stops = targets.Select(target => target.Daemon.TerminateSessionAsync(target.Session.Id, reason, force: true, terminatedBy)).ToArray();
        var starts = GuestGate.GetStarts(userId);
        await Task.WhenAll(starts.Select(start => start.Completion.Task));
        var cleanup = starts.Where(start => !start.Removed && start.SessionId != null)
            .Select(start => start.Daemon.CleanGuestStartAsync(start)).ToArray();
        try { await Task.WhenAll(stops.Concat(cleanup)); }
        catch (Exception ex)
        {
            services.FirstOrDefault()?._logger.LogWarning(ex,
                "Guest {UserId} cleanup is incomplete; unfinished stages remain available for retry", userId);
        }
        var finalStops = services.SelectMany(daemon => daemon.GetAllSessions(includeTerminating: true)
            .Where(session => session.UserId == userId && session.IsTemporary && PrefillSessionService.IsTerminatableByAdmin(session))
            .Where(session => !targets.Any(target => ReferenceEquals(target.Session, session)))
            .Select(session => daemon.TerminateSessionAsync(session.Id, reason, force: true, terminatedBy))).ToArray();
        try { await Task.WhenAll(finalStops); }
        catch (Exception ex)
        {
            services.FirstOrDefault()?._logger.LogWarning(ex,
                "Guest {UserId} late session cleanup is incomplete; unfinished stages remain available for retry", userId);
        }
        var failed = services.Sum(daemon => daemon.GetAllSessions(includeTerminating: true)
            .Count(session => session.UserId == userId && session.IsTemporary && PrefillSessionService.IsTerminatableByAdmin(session)));
        var pending = GuestGate.GetStarts(userId).Count(start => !start.Active && !start.Removed);
        GuestGate.FinishStop(userId, failed != 0 || pending != 0);
        return new GuestPrefillStopResult(failed, pending);
    }

    private async Task CleanGuestStartAsync(GuestPrefillStart start)
    {
        await start.Cleanup.WaitAsync();
        try
        {
            if (start.Removed)
                return;
            if (!start.ContainerRemoved && start.Registered)
            {
                await TerminateSessionAsync(start.SessionId!, "Guest session creation stopped", force: true);
            }
            else if (!start.ContainerRemoved && start.CreateDispatched)
            {
                if (start.ContainerId == null)
                {
                    using var lookupCts = new CancellationTokenSource(_containerTeardownTimeout);
                    var containers = await _containerGateway.ListContainersAsync(new ContainersListParameters
                    {
                        All = true,
                        Filters = new Dictionary<string, IDictionary<string, bool>>
                        {
                            ["name"] = new Dictionary<string, bool> { [start.ContainerName!] = true }
                        }
                    }, lookupCts.Token);
                    var match = containers.SingleOrDefault(container => container.Names != null
                        && container.Names.Any(name => name.TrimStart('/') == start.ContainerName));
                    if (match == null)
                        throw new InvalidOperationException("Container creation has an unresolved outcome.");
                    start.ContainerId = match.ID;
                }
                await RemoveGuestContainerAsync(start.ContainerId, removeVolumes: true);
            }
            start.ContainerRemoved = true;
            if (!start.Registered && start.Session != null)
                await DisposeClientWithDrainAsync(start.Session);
            if (start.Directory != null)
            {
                try { Directory.Delete(start.Directory, recursive: true); }
                catch (DirectoryNotFoundException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not delete stopped session directory {SessionId}", start.SessionId);
                }
            }
            start.Removed = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Guest creation cleanup remains incomplete for {SessionId}", start.SessionId);
            throw;
        }
        finally { start.Cleanup.Release(); }
    }

    /// <summary>
    /// Gets sessions for a specific user
    /// </summary>
    public IEnumerable<DaemonSession> GetUserSessions(Guid userId)
    {
        return _sessions.Values.Where(s => s.UserId == userId).ToList();
    }

    /// <summary>
    /// Checks if any prefill daemon session is currently authenticated.
    /// Used by depot mapping service to detect when prefill is using the shared credentials.
    /// </summary>
    public bool IsAnyDaemonAuthenticated()
    {
        return _sessions.Values.Any(s =>
            s.Status == DaemonSessionStatus.Active &&
            s.AuthState == DaemonAuthState.Authenticated);
    }

    /// <summary>
    /// Terminates all active prefill sessions.
    /// Called when authentication is logged out.
    /// </summary>
    /// <param name="reason">Reason for termination (for logging)</param>
    /// <param name="includePersistent">
    /// When false (the default), persistent (system-owned) sessions are skipped: they are stopped only
    /// via the dedicated <c>PersistentPrefillController.StopAsync</c> path, so a credential logout (e.g.
    /// <c>SteamKit2Service.Authentication.LogoutAsync</c>'s PICS logout) never tears down the reused
    /// persistent container. Passing true bypasses that guard AND the per-service persistent start lock
    /// (<c>_persistentStartLock</c>), so only pass true from a caller that cannot race a persistent start
    /// (e.g. full service shutdown); otherwise stop persistent sessions via
    /// <c>PersistentPrefillController.StopAsync</c> / <c>StopPersistentSessionAsync</c> instead.
    /// </param>
    public async Task TerminateAllSessionsAsync(
        string reason = "Authentication logged out",
        bool includePersistent = false)
    {
        var sessions = _sessions.Values
            .Where(s => includePersistent || PrefillSessionService.IsTerminatableByAdmin(s))
            .ToList();
        var terminatedCount = 0;

        foreach (var session in sessions)
        {
            try
            {
                await TerminateSessionAsync(session.Id, reason, force: true, terminatedBy: "system");
                terminatedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to terminate session {SessionId} during auth logout", session.Id);
            }
        }

        if (terminatedCount > 0)
        {
            _logger.LogInformation("Terminated {Count} prefill sessions due to: {Reason}",
                terminatedCount, reason);
        }
    }
}
