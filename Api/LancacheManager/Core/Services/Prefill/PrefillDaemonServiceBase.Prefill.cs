using LancacheManager.Models;
using LancacheManager.Core.Services.SteamPrefill;

namespace LancacheManager.Core.Services;

public abstract partial class PrefillDaemonServiceBase
{
    /// <summary>
    /// Cancels a running prefill operation.
    /// Sends cancel-prefill command to the daemon to abort the download.
    /// </summary>
    public async Task CancelPrefillAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        var active = session.Runs.Values.Where(run => run.TerminalCompletedFlag != 2).ToArray();
        if (active.Length > 1) throw new DaemonCommandException("ambiguous-operation");
        if (active.Length == 1)
            await CancelPrefillRunAsync(sessionId, active[0].PrefillRunId, false, cancellationToken);
    }

    /// <summary>
    /// Gets owned games for a logged-in session
    /// </summary>
    public async Task<List<OwnedGame>> GetOwnedGamesAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        var games = await session.Client.GetOwnedGamesAsync(cancellationToken);

        // Epic's raw artwork entries only exist so the banner can be picked on this side; nothing in the
        // browser reads them, and a few hundred titles of them is hundreds of KB the page parses and
        // throws away. Dropping them here rather than on the model keeps the socket read working.
        foreach (var game in games)
        {
            game.KeyImages = null;
        }

        return games;
    }

    /// <summary>
    /// Checks cache status by comparing cached depots against manifests.
    /// </summary>
    public virtual Task<CacheStatusResult> GetCacheStatusAsync(
        string sessionId,
        List<string> appIds,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default)
        => GetSteamCacheStatusAsync(sessionId, appIds, expiresAtUtc, null, cancellationToken);

    public Task<CacheStatusResult> GetCacheStatusAsync(
        string sessionId,
        List<string> appIds,
        DateTimeOffset expiresAtUtc,
        List<string>? operatingSystems,
        CancellationToken cancellationToken = default)
        => Platform == PrefillPlatform.Steam && operatingSystems is { Count: > 0 }
            ? GetSteamCacheStatusAsync(sessionId, appIds, expiresAtUtc, operatingSystems, cancellationToken)
            : GetCacheStatusAsync(sessionId, appIds, expiresAtUtc, cancellationToken);

    private async Task<CacheStatusResult> GetSteamCacheStatusAsync(
        string sessionId,
        List<string> appIds,
        DateTimeOffset expiresAtUtc,
        List<string>? operatingSystems,
        CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        var requested = appIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var numeric = requested
            .Select(appId => (AppId: appId, Parsed: uint.TryParse(appId, out var value), Value: value))
            .ToList();
        var numericAppIds = numeric.Where(app => app.Parsed).Select(app => app.Value).Distinct().ToArray();
        var completed = numeric.Where(app => !app.Parsed)
            .ToDictionary(app => app.AppId, app => AppCacheStatus.Unknown(app.AppId, CacheReason.InvalidAppId),
                StringComparer.OrdinalIgnoreCase);
        if (numericAppIds.Length == 0)
            return new CacheStatusResult { Version = 2, Apps = requested.Select(appId => completed[appId]).ToList() };

        if (CacheStatusClock.GetUtcNow() >= expiresAtUtc)
            return CacheStatusResult.Unknown(requested, CacheReason.DeadlineReached);

        PrefillCacheSnapshot snapshot;
        var snapshotRemaining = expiresAtUtc - CacheStatusClock.GetUtcNow();
        if (snapshotRemaining <= TimeSpan.Zero)
            return CacheStatusResult.Unknown(requested, CacheReason.DeadlineReached);
        using (var snapshotDeadline = new CancellationTokenSource(snapshotRemaining, CacheStatusClock))
        using (var snapshotToken = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            snapshotDeadline.Token))
        {
            try
            {
                snapshot = await _cacheService.GetCacheSnapshotAsync(numericAppIds, snapshotToken.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (snapshotDeadline.IsCancellationRequested)
            {
                return CacheStatusResult.Unknown(requested, CacheReason.DeadlineReached);
            }
        }

        if (session.Client is not DaemonClientBase client)
        {
            foreach (var app in numeric.Where(app => app.Parsed))
                completed[app.AppId] = AppCacheStatus.Unknown(app.AppId, CacheReason.UnsupportedDaemon);
            return new CacheStatusResult { Version = 2, Apps = requested.Select(appId => completed[appId]).ToList() };
        }
        client.CacheStatusClock = CacheStatusClock;
        string? message = null;
        CacheReason? stopReason = null;
        foreach (var batch in numericAppIds.Chunk(16))
        {
            if (CacheStatusClock.GetUtcNow() >= expiresAtUtc)
            {
                stopReason = CacheReason.DeadlineReached;
                break;
            }

            CacheStatusResult status;
            try
            {
                var scope = snapshot.Scope.Where(item => batch.Contains(item.AppId)).ToList();
                status = await client.CheckCacheStatusAsync(
                    batch,
                    snapshot.Depots,
                    scope,
                    expiresAtUtc,
                    operatingSystems,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (CacheStatusClock.GetUtcNow() >= expiresAtUtc)
            {
                status = CacheStatusResult.Unknown(batch.Select(value => value.ToString()), CacheReason.DeadlineReached);
            }
            catch (DaemonCommandException ex) when (!ex.RequiresLogin)
            {
                status = CacheStatusResult.Unknown(batch.Select(value => value.ToString()),
                    session.Client is DaemonClientBase { IsConnected: false }
                        ? CacheReason.Disconnected
                        : CacheReason.StatusUnavailable);
            }

            message ??= status.Message;
            var normalized = status.Normalize(batch.Select(value => value.ToString()));
            foreach (var value in batch)
            {
                var row = normalized.Apps.SingleOrDefault(app => uint.TryParse(app.AppId, out var parsed) && parsed == value)
                    ?? AppCacheStatus.Unknown(value.ToString(), CacheReason.InvalidResult);
                foreach (var appId in numeric.Where(app => app.Parsed && app.Value == value).Select(app => app.AppId))
                    completed[appId] = row.Copy(appId);
            }
            stopReason = status.StopReason ?? GetStopReason(normalized.Apps);
            if (stopReason.HasValue)
                break;
        }

        stopReason ??= CacheStatusClock.GetUtcNow() >= expiresAtUtc ? CacheReason.DeadlineReached : null;
        return new CacheStatusResult
        {
            Version = 2,
            Apps = requested.Select(appId => completed.TryGetValue(appId, out var row)
                ? row
                : AppCacheStatus.Unknown(appId, stopReason ?? CacheReason.InvalidResult)).ToList(),
            Message = message
        };
    }

    /// <summary>
    /// Shared cache-status path for daemons whose app identifiers are strings rather than Steam
    /// depot/manifest ids. Epic, Xbox, Battle.net, and Riot speak the same daemon command contract.
    /// </summary>
    protected async Task<CacheStatusResult> GetStringAppCacheStatusAsync(
        string sessionId,
        List<string> appIds,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        var requested = appIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var requestedSet = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (CacheStatusClock.GetUtcNow() >= expiresAtUtc)
            return CacheStatusResult.Unknown(requested, CacheReason.DeadlineReached);
        Dictionary<string, CachedAppInfo> cachedApps;
        var readRemaining = expiresAtUtc - CacheStatusClock.GetUtcNow();
        if (readRemaining <= TimeSpan.Zero)
            return CacheStatusResult.Unknown(requested, CacheReason.DeadlineReached);
        using (var readDeadline = new CancellationTokenSource(readRemaining, CacheStatusClock))
        using (var readToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, readDeadline.Token))
        {
            try
            {
                cachedApps = (await _cacheService.GetCachedAppsAsync(Platform, readToken.Token))
                    .Where(app => requestedSet.Contains(app.AppId))
                    .ToDictionary(app => app.AppId, StringComparer.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (readDeadline.IsCancellationRequested)
            {
                return CacheStatusResult.Unknown(requested, CacheReason.DeadlineReached);
            }
        }

        var completed = new Dictionary<string, AppCacheStatus>(StringComparer.OrdinalIgnoreCase);
        if (session.Client is not DaemonClientBase client)
            return CacheStatusResult.Unknown(requested, CacheReason.UnsupportedDaemon);
        client.CacheStatusClock = CacheStatusClock;
        string? message = null;
        CacheReason? stopReason = null;
        foreach (var batch in requested.Chunk(16))
        {
            if (CacheStatusClock.GetUtcNow() >= expiresAtUtc)
            {
                stopReason = CacheReason.DeadlineReached;
                break;
            }

            var batchApps = batch.Select(appId => new CachedAppInput
            {
                AppId = appId,
                Revision = cachedApps.TryGetValue(appId, out var cached) ? cached.CacheRevision : null
            }).ToList();
            CacheStatusResult status;
            try
            {
                status = await client.CheckCacheStatusAsync(batchApps, expiresAtUtc, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (CacheStatusClock.GetUtcNow() >= expiresAtUtc)
            {
                status = CacheStatusResult.Unknown(batchApps.Select(app => app.AppId), CacheReason.DeadlineReached);
            }
            catch (DaemonCommandException ex) when (!ex.RequiresLogin)
            {
                status = CacheStatusResult.Unknown(batchApps.Select(app => app.AppId),
                    session.Client is DaemonClientBase { IsConnected: false }
                        ? CacheReason.Disconnected
                        : CacheReason.StatusUnavailable);
            }

            message ??= status.Message;
            var normalized = status.Normalize(batch);
            foreach (var row in normalized.Apps)
                completed[row.AppId] = row;
            stopReason = status.StopReason ?? GetStopReason(normalized.Apps);
            if (stopReason.HasValue)
                break;
        }

        stopReason ??= CacheStatusClock.GetUtcNow() >= expiresAtUtc ? CacheReason.DeadlineReached : null;
        return new CacheStatusResult
        {
            Version = 2,
            Apps = requested.Select(appId => completed.TryGetValue(appId, out var row)
                ? row
                : AppCacheStatus.Unknown(appId, stopReason ?? CacheReason.InvalidResult)).ToList(),
            Message = message
        };
    }

    private static CacheReason? GetStopReason(IEnumerable<AppCacheStatus> apps)
    {
        foreach (var reason in apps.Select(app => app.Reason))
        {
            if (reason is CacheReason.DeadlineReached
                or CacheReason.Disconnected
                or CacheReason.ConnectionChanged
                or CacheReason.StatusUnavailable
                or CacheReason.InvalidResult
                or CacheReason.AuthenticationRequired)
            {
                return reason;
            }
        }
        return null;
    }

    /// <summary>
    /// Sets selected apps for prefill
    /// </summary>
    public async Task SetSelectedAppsAsync(string sessionId, List<string> appIds, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        _logger.LogInformation("SetSelectedAppsAsync: Sending {Count} app IDs to daemon for session {SessionId}", appIds.Count, sessionId);

        await session.Client.SetSelectedAppsAsync(appIds, cancellationToken);

        _logger.LogInformation("SetSelectedAppsAsync: Daemon acknowledged for session {SessionId}", sessionId);
    }

    /// <summary>
    /// Starts a prefill operation
    /// </summary>
    public async Task<PrefillResult> PrefillAsync(
        string sessionId,
        bool all = false,
        bool recent = false,
        bool recentlyPurchased = false,
        int? top = null,
        bool force = false,
        List<string>? operatingSystems = null,
        int? maxConcurrency = null,
        CancellationToken cancellationToken = default,
        Guid? scheduleId = null, List<string>? appIds = null, Guid? operationId = null,
        string? scheduleName = null, string? notificationMode = null, Guid? parentOperationId = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        if (session.Capabilities is null)
        {
            // This read also applies the sign-in state it carries, which a session whose adoption read failed still lacks.
            if (await GetSessionStatusAsync(sessionId, cancellationToken) is null)
                throw new DaemonCommandException();
            if (session.Capabilities is null)
            {
                // Another pass holds the lease and applies its own read; this Start waits for it.
                await session.RecoveryWork.WaitAsync(cancellationToken);
                session.RecoveryWork.Release();
            }
            // An image replace closes admission while it holds the lease; StartRunAsync answers the same.
            if (session.AdmissionClosed || session.Recovering)
                throw new DaemonCommandException("outcome-unknown");
            // A Prefill Sessions reset holds the lease without a read, and a failed pass stores nothing.
            if (session.Capabilities is null)
                throw new DaemonCommandException();
        }
        if (session.Capabilities.SupportsConcurrentPrefill)
        {
            List<CachedAppInput> cachedApps = force
                ? []
                : (await _cacheService.GetCachedAppsAsync(Platform, cancellationToken))
                    .Select(app => new CachedAppInput { AppId = app.AppId, Revision = app.CacheRevision })
                    .ToList();
            var selection = all ? "all" : recent ? "recent" : recentlyPurchased ? "recently_purchased"
                : top.HasValue ? "top" : "selected";
            if ((all ? 1 : 0) + (recent ? 1 : 0) + (recentlyPurchased ? 1 : 0) + (top.HasValue ? 1 : 0) > 1
                || (selection == "selected" && appIds is not { Count: > 0 })
                || (selection != "selected" && appIds is not null)
                || appIds?.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 100) == true)
                throw new LancacheManager.Middleware.ValidationException("Choose a non-empty explicit selection or one preset.");
            ScheduledPrefillConfigFactory.RefuseTopCountAboveLimit(Platform, top);
            var options = new DaemonRunOptions
            {
                AppIds = appIds?.Distinct(StringComparer.Ordinal).ToArray(),
                Selection = selection,
                Force = force,
                TopCount = top,
                OperatingSystems = ScheduledPrefillConfigFactory.SupportsOperatingSystemSelection(Platform)
                    ? operatingSystems?.ToArray() ?? [] : [],
                CachedApps = cachedApps,
                MaxConcurrency = Math.Clamp(maxConcurrency ?? session.Capabilities.MaxConcurrentRequests,
                    1, session.Capabilities.MaxConcurrentRequests)
            };
            return await StartRunAsync(session, options, operationId ?? Guid.NewGuid(), scheduleId,
                scheduleName, notificationMode, parentOperationId, cancellationToken);
        }
        throw new DaemonCommandException("daemon-outdated");
    }

    /// <summary>
    /// Empty means there are no safe skip hints, including when the best-effort lookup fails.
    /// Keep it distinct from an omitted snapshot so a daemon cannot reuse stale local history.
    /// </summary>
    private async Task<List<CachedDepotInput>?> GetCachedDepotsAsync(bool force, CancellationToken cancellationToken)
    {
        if (force) return null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cached = await _cacheService.GetAllCachedDepotsAsync();
            return cached.Select(depot => new CachedDepotInput
            { AppId = depot.AppId, DepotId = depot.DepotId, ManifestId = depot.ManifestId }).ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch cached depots, proceeding without cache details");
            return [];
        }
    }

    public IReadOnlyList<DaemonRunStatus> GetRuns(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        return session.Runs.Values.OrderBy(run => run.Snapshot.StartedAt).ThenBy(run => run.PrefillRunId)
            .Select(DaemonSessionDto.FromRun).ToArray();
    }

    public DaemonRun? GetRun(string sessionId, Guid runId)
        => _sessions.TryGetValue(sessionId, out var session) && session.Runs.TryGetValue(runId, out var run) ? run : null;

    /// <summary>Records a cancel on the run and sends it to the daemon.</summary>
    /// <returns>True when the daemon answered the cancel; false when none was sent or its answer was lost.</returns>
    public async Task<bool> CancelPrefillRunAsync(string sessionId, Guid runId, bool waitForReconcile,
        CancellationToken cancellationToken = default, string? reason = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            // A session being torn down ends its runs as stopped after its container stop and removal,
            // whether or not the removal succeeds, so a cancel landing during that teardown has nothing
            // left to send.
            lock (_terminationSync)
            {
                if (_terminations.ContainsKey(sessionId))
                    return false;
            }
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }
        if (!session.Runs.TryGetValue(runId, out var run))
            throw new DaemonCommandException("operation-not-found");
        lock (session.PrefillLock)
        {
            // Start holds the run's work until the daemon answers its admission (up to its 30 s command limit), so a
            // cancel landing then is sent by Start as soon as admission ends.
            if (run.AdmissionPending)
            {
                if (!run.CancelAfterAdmission)
                {
                    run.CancelAfterAdmission = true;
                    run.CancelReason = reason;
                }
                return false;
            }
        }
        var sent = false;
        var threw = false;
        await run.PrefillWork.WaitAsync(cancellationToken);
        try
        {
            if (run.TerminalCompletedFlag != 0) return false;
            // The first cancel owns the reason: a sign-out or time limit landing during a user's cancel must not
            // turn that cancel into a failure.
            if (!run.CancelRequested)
            {
                await _sessionService.SetRunCancellationAsync(runId, cancellationToken, reason);
                run.CancelReason = reason;
                run.CancelDeadlineUtc = DateTime.UtcNow + GetRunStallTimeout(run);
            }
            run.CancelRequested = true;
            if (session.Recovering) return false;
            try
            {
                await session.Client.CancelPrefillAsync(runId, run.DaemonInstanceId, cancellationToken);
                sent = true;
            }
            // A caller that stops waiting has not lost the daemon: the recorded cancel is sent again by the next
            // recovery pass, and marking the session recovering would refuse every Start until then.
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException
                || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                run.Recovering = true;
                session.Recovering = true;
                _logger.LogWarning(ex, "Cancellation awaits reconnection for prefill {RunId}", runId);
            }
        }
        // A throw skips the reconcile below, so a request a daemon event left is read once the work is free.
        catch { threw = true; throw; }
        finally
        {
            run.PrefillWork.Release();
            if (threw) ReconcileIfRequested(session, run);
        }
        if (!session.Recovering)
        {
            if (waitForReconcile)
                await ReconcileRunAsync(session, run, cancellationToken);
            else
            {
                // A browser's Cancel answers once the daemon has the cancel; the reconcile runs on the session's
                // own token, read here so a teardown stops its daemon reads.
                CancellationToken sessionToken;
                try { sessionToken = session.CancellationTokenSource.Token; }
                catch (ObjectDisposedException)
                {
                    // The cancel is stored and sent, and a disposed source means the session's teardown owns the run.
                    return sent;
                }
                FireAndForgetAsync(() => ReconcileRunAsync(session, run, sessionToken), nameof(ReconcileRunAsync));
            }
        }
        return sent;
    }

    // Only the Steam text names its service; the others tell the person to log in again
    private string SignInLostStageKey => Platform == PrefillPlatform.Steam
        ? "errors.steam.signInLost" : "errors.prefill.loginRequired";

    private async Task<PrefillResult> StartRunAsync(DaemonSession session, DaemonRunOptions options, Guid runId,
        Guid? scheduleId, string? scheduleName, string? notificationMode, Guid? parentOperationId,
        CancellationToken cancellationToken)
    {
        DaemonRun run;
        if (notificationMode is not null and not "visible" and not "silent" and not "hidden")
            throw new ArgumentException("Notification mode must be visible, silent, or hidden.", nameof(notificationMode));
        lock (session.PrefillLock)
        {
            var status = session.Capabilities!;
            if (!IsSessionLive(session) || session.Status != DaemonSessionStatus.Active || _stopping
                || session.AdmissionClosed || session.Recovering || session.CancellationTokenSource.IsCancellationRequested)
                throw new DaemonCommandException("outcome-unknown");
            if (Platform.RequiresLogin() && session.AuthState != DaemonAuthState.Authenticated)
                throw new DaemonCommandException("auth-lost", true, SignInLostStageKey);
            if (session.Runs.ContainsKey(runId)) throw new DaemonCommandException("operation-conflict");
            if (session.Runs.Values.Count(active => active.TerminalCompletedFlag != 2) >= status.MaxConcurrentRuns
                || (scheduleId.HasValue && session.Runs.Values.Any(active => active.TerminalCompletedFlag != 2
                    && active.PrefillScheduleId == scheduleId)))
                throw new PrefillAlreadyRunningException("The prefill daemon has no available run slots.");
            var now = DateTimeOffset.UtcNow;
            run = new DaemonRun
            {
                PrefillRunId = runId,
                SessionId = session.Id,
                DaemonInstanceId = status.DaemonInstanceId!,
                PrefillScheduleId = scheduleId,
                ScheduleName = scheduleName,
                Options = options,
                AdmissionPending = true,
                NotificationMode = notificationMode,
                ParentOperationId = parentOperationId,
                PrefillAddresses = session.PrefillAddresses,
                Snapshot = new DaemonRunSnapshot
                {
                    OperationId = runId.ToString(),
                    DaemonInstanceId = status.DaemonInstanceId!,
                    StartedAt = now,
                    UpdatedAt = now,
                    State = "started",
                    TotalApps = options.AppIds?.Count ?? 0
                }
            };
            session.Runs.TryAdd(runId, run);
        }
        await run.PrefillWork.WaitAsync(CancellationToken.None);
        var persisted = false;
        var refused = false;
        try
        {
            await using var change = session.IsPersistent
                ? await PersistentChangeGate.EnterChangeAsync(cancellationToken) : null;
            // The sign-in may have been lost while this Start waited for the gate.
            if (Platform.RequiresLogin() && session.AuthState != DaemonAuthState.Authenticated)
                throw new DaemonCommandException("auth-lost", true, SignInLostStageKey);
            if (!IsSessionLive(session) || session.AdmissionClosed || session.Recovering
                || session.Capabilities?.DaemonInstanceId != run.DaemonInstanceId
                || session.LoginOperationId.HasValue)
                throw new DaemonCommandException("instance-changed");
            await _sessionService.CreateRunAsync(run, scheduleId.HasValue ? "schedule" : "manual", cancellationToken);
            persisted = true;
            bool canceledDuringAdmission;
            lock (session.PrefillLock) canceledDuringAdmission = run.CancelAfterAdmission;
            // A cancel that arrived while this Start waited for the gate or the save stops it before the daemon is asked.
            if (canceledDuringAdmission)
            {
                refused = true;
                await FinishUnsentRunAsync(session, run, "canceled", run.CancelReason);
                return new PrefillResult
                {
                    Success = true,
                    RunId = runId,
                    DaemonInstanceId = run.DaemonInstanceId,
                    State = "canceled"
                };
            }
            var cachedDepots = await GetCachedDepotsAsync(options.Force, session.CancellationTokenSource.Token);
            var result = await session.Client.PrefillAsync(runId, run.DaemonInstanceId, options,
                cachedDepots: cachedDepots,
                cancellationToken: session.CancellationTokenSource.Token);
            if (!result.Success)
            {
                // The daemon never registered a refused run, so a cancel sent for it would only be refused too.
                refused = true;
                var failure = new DaemonCommandException(result.ErrorCode, result.RequiresLogin, SignInLostStageKey);
                // A daemon that is not signed in refuses with only the sign-in flag and no code. A daemon that
                // refuses with only an error text (the Epic Start refusal) stores the typed refusal code, which
                // the browser shows as "request failed".
                await FinishUnsentRunAsync(session, run, "failed", failure.RequiresLogin ? "auth-lost"
                    : string.IsNullOrEmpty(failure.ErrorCode) ? "prefill-failed" : failure.ErrorCode);
                if (result.ErrorCode == "run-limit")
                    throw new PrefillAlreadyRunningException(failure.Message);
                throw failure;
            }
            return result;
        }
        // The client refuses before sending (a changed daemon instance), so the daemon never had the run.
        catch (DaemonCommandException ex) when (persisted && !refused && run.TerminalCompletedFlag == 0)
        {
            refused = true;
            await FinishUnsentRunAsync(session, run, "failed", ex.ErrorCode);
            throw;
        }
        catch (Exception ex) when (persisted && run.TerminalCompletedFlag == 0
            && ex is not PrefillAlreadyRunningException && ex is not DaemonCommandException)
        {
            // A lost acknowledgment is not evidence that the process-owned run stopped.
            run.Recovering = true;
            session.Recovering = true;
            _logger.LogWarning(ex, "Prefill {RunId} awaits operation recovery", runId);
            return new PrefillResult
            {
                Success = true,
                RunId = runId,
                DaemonInstanceId = run.DaemonInstanceId,
                State = "recovering"
            };
        }
        finally
        {
            bool cancelAfterAdmission;
            DaemonSessionDto? ended = null;
            // The same send lock NotifyHubAsync takes, held from before the last update of a Start that ends before
            // saving its run is built until it is sent, so no update built after it is sent before it.
            if (!persisted) await session.SessionUpdateSend.WaitAsync();
            lock (session.PrefillLock)
            {
                run.AdmissionPending = false;
                cancelAfterAdmission = run.CancelAfterAdmission;
                // A cancel during admission answered at once; a Start that ends before saving its run tells the page
                // the run is gone, or the page keeps showing it as canceling.
                if (!persisted && cancelAfterAdmission)
                {
                    run.Snapshot = run.Snapshot with
                    {
                        State = "canceled",
                        Sequence = run.Snapshot.Sequence + 1,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    ended = DaemonSessionDto.FromSession(session);
                    run.Completion.TrySetResult(DaemonSessionDto.FromRun(run));
                }
            }
            if (!persisted) session.Runs.TryRemove(runId, out _);
            run.PrefillWork.Release();
            if (persisted && run.TerminalCompletedFlag == 0)
                FireAndForgetAsync(() => RefreshRunsAsync(session.Id), nameof(RefreshRunsAsync));
            if (persisted && !refused && cancelAfterAdmission && run.TerminalCompletedFlag == 0)
            {
                FireAndForgetAsync(async () =>
                {
                    // A session already torn down ended its runs; there is nothing left to cancel.
                    if (!_sessions.ContainsKey(session.Id)) return;
                    try
                    {
                        await CancelPrefillRunAsync(session.Id, runId, false, CancellationToken.None, run.CancelReason);
                    }
                    catch (Exception ex) when (ex is not DaemonCommandException)
                    {
                        // The user's Cancel was answered when it arrived during admission, so a failed record must
                        // still stop the run: mark it in memory, which the overdue pass and the recovery pass both
                        // read, and send it.
                        _logger.LogWarning(ex, "Could not record the cancel of prefill {RunId}; sending it anyway", runId);
                        await run.PrefillWork.WaitAsync();
                        try
                        {
                            if (run.TerminalCompletedFlag != 0 || run.CancelRequested) return;
                            run.CancelRequested = true;
                            run.CancelDeadlineUtc = DateTime.UtcNow + GetRunStallTimeout(run);
                            if (!session.Recovering)
                                await session.Client.CancelPrefillAsync(runId, run.DaemonInstanceId, CancellationToken.None);
                        }
                        finally
                        {
                            run.PrefillWork.Release();
                            ReconcileIfRequested(session, run);
                        }
                    }
                }, nameof(CancelPrefillRunAsync));
            }
            if (ended is not null)
            {
                FireAndForgetAsync(async () =>
                {
                    // Sent as built: a rebuild would no longer list the removed run.
                    try
                    {
                        await BroadcastToSubscribersAsync(session, EventSessionUpdated, ended);
                        await _notifications.NotifyAdminAsync(EventSessionUpdated, ended);
                    }
                    finally { session.SessionUpdateSend.Release(); }
                }, nameof(NotifyHubAsync));
            }
            else if (!persisted) session.SessionUpdateSend.Release();
        }
    }

    /// <summary>
    /// Ends a run the daemon was never sent. When the save fails, the run keeps that end in memory and the read
    /// that follows this Start, which finds no such run on the daemon, saves it.
    /// </summary>
    private async Task FinishUnsentRunAsync(DaemonSession session, DaemonRun run, string state, string? reason)
    {
        var end = run.Snapshot with
        {
            State = state,
            Reason = reason,
            UpdatedAt = DateTimeOffset.UtcNow,
            Sequence = run.Snapshot.Sequence + 1
        };
        try { await FinishRunAsync(session, run, end, [], CancellationToken.None); }
        catch (Exception saveError) when (saveError is not OperationCanceledException)
        {
            _logger.LogWarning(saveError, "Could not save the refused start of prefill {RunId}", run.PrefillRunId);
            run.Snapshot = end;
        }
    }

    /// <summary>
    /// Clears the temporary cache
    /// </summary>
    public async Task<ClearCacheResult> ClearCacheAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        return await session.Client.ClearCacheAsync(cancellationToken);
    }

    /// <summary>
    /// Gets cache info
    /// </summary>
    public async Task<ClearCacheResult> GetCacheInfoAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        return await session.Client.GetCacheInfoAsync(cancellationToken);
    }

    /// <summary>
    /// Gets selected apps status with download sizes
    /// </summary>
    public async Task<SelectedAppsStatus> GetSelectedAppsStatusAsync(string sessionId, List<string>? operatingSystems = null, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        return await session.Client.GetSelectedAppsStatusAsync(operatingSystems, cancellationToken);
    }

    /// <summary>
    /// Adds a SignalR connection as a subscriber to session events. A connection leaves the set when it
    /// disconnects or a send to it fails, never to make room: a tab still connected would otherwise keep
    /// drawing a run or session that ended.
    /// </summary>
    public void AddSubscriber(string sessionId, string connectionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            lock (session.PrefillLock)
            {
                session.SubscribedConnections.Add(connectionId);
                session.LastSubscribeCounts[connectionId] = ++session.SubscribeCount;
                _logger.LogDebug("Added subscriber {ConnectionId} to session {SessionId} (total: {Count})",
                    connectionId, sessionId, session.SubscribedConnections.Count);
            }
        }
    }

    /// <summary>
    /// Replays the retained live progress snapshot to a SINGLE just-(re)subscribed connection,
    /// so a client that connects/refreshes/reconnects mid-prefill binds its bar immediately
    /// without waiting for the next periodic daemon tick. Reuses the existing
    /// <c>EventPrefillProgress</c> event (no new event) and sends ONLY to the caller's connection
    /// (no double-broadcast to already-subscribed clients). No-op when the session is not
    /// prefilling or has no retained snapshot yet.
    /// </summary>
    public async Task ReplayProgressAsync(string sessionId, string connectionId)
    {
        if (!_sessions.ContainsKey(sessionId)) return;
        foreach (var run in GetRuns(sessionId))
        {
            if (run.Progress is not null)
                await SendToClientAsync(connectionId, EventPrefillProgress,
                    new { sessionId, progress = run.Progress }).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// Removes a SignalR connection from all session subscriptions
    /// </summary>
    public void RemoveSubscriber(string connectionId)
    {
        foreach (var session in _sessions.Values)
        {
            lock (session.PrefillLock)
            {
                session.SubscribedConnections.Remove(connectionId);
                session.LastSubscribeCounts.Remove(connectionId);
            }
        }
    }
}
