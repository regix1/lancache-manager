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
        if (!session.Runs.IsEmpty)
        {
            var active = session.Runs.Values.Where(run => run.TerminalCompletedFlag != 2).ToArray();
            if (active.Length > 1) throw new DaemonCommandException("ambiguous-operation");
            if (active.Length == 1)
                await CancelPrefillRunAsync(sessionId, active[0].PrefillRunId, cancellationToken);
            return;
        }
        Guid? runId;
        lock (session.PrefillLock)
            runId = session.PrefillRunId;
        try
        {
            await TransitionToTerminalAsync(session, PrefillState.Cancelled, runId,
                cancelDaemon: true, cancelBeforeClaim: true, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Cancel-prefill request was cancelled for session {SessionId}", sessionId);
            throw;
        }
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
    public virtual async Task<CacheStatusResult> GetCacheStatusAsync(
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
            var status = await session.Client.GetStatusAsync(cancellationToken);
            if (status?.SupportsConcurrentPrefill == true)
                await RecoverRunsAsync(session, status, cancellationToken);
        }
        if (session.Capabilities?.SupportsConcurrentPrefill == true)
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
        if (session.Recovering || !session.Runs.IsEmpty)
            throw new DaemonCommandException("outcome-unknown");
        lock (session.PrefillLock)
        {
            if (session.AdmissionClosed || session.IsPrefilling || session.TerminalCompletedFlag == 1)
                throw new PrefillAlreadyRunningException($"A prefill is already in progress for session {sessionId}");
        }

        var cachedDepots = await GetCachedDepotsAsync(force, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ScheduledPrefillConfigFactory.SupportsOperatingSystemSelection(Platform))
            operatingSystems = null;

        Guid? runId = null;
        Task<PrefillResult>? commandTask = null;
        try
        {
            if (!await session.PrefillWork.WaitAsync(0, cancellationToken))
                throw new PrefillAlreadyRunningException($"A prefill is already in progress for session {sessionId}");
            try
            {
                lock (session.PrefillLock)
                {
                    if (!IsSessionLive(session) || session.Status != DaemonSessionStatus.Active
                        || session.AdmissionClosed
                        || session.CancellationTokenSource.IsCancellationRequested)
                        throw new DaemonCommandException();
                    if (session.IsPrefilling || session.TerminalCompletedFlag == 1)
                        throw new PrefillAlreadyRunningException($"A prefill is already in progress for session {sessionId}");
                    runId = Guid.NewGuid();
                    session.PrefillRunId = runId;
                    session.PrefillScheduleId = scheduleId;
                    session.IsPrefilling = true;
                    session.LastProgress = null;
                    session.PreviousAppId = null;
                    session.PreviousAppName = null;
                    session.CurrentAppId = null;
                    session.CurrentAppName = null;
                    session.CurrentBytesDownloaded = 0;
                    session.CurrentTotalBytes = 0;
                    session.CompletedBytesTransferred = 0;
                    session.TotalBytesTransferred = 0;
                    session.PrefillStartedAt = DateTime.UtcNow;
                    session.TerminalCompletedFlag = 0;
                    session.PrefillState = PrefillState.Started;
                    session.LastPrefillCompletedAt = null;
                    session.LastPrefillDurationSeconds = null;
                    session.LastPrefillStatus = null;
                    session.ErrorMessage = null;
                    session.ErrorStageKey = null;
                    Volatile.Write(ref session.LastProgressTicksUtc, DateTime.UtcNow.Ticks);
                    session.LastProgressBytes = 0;
                }
                await NotifyPrefillStartedAsync(session);
                if (!IsSessionLive(session) || session.AdmissionClosed) throw new DaemonCommandException();
                await NotifyHubAsync(EventSessionUpdated, DaemonSessionDto.FromSession(session));
                if (!IsSessionLive(session) || session.Status != DaemonSessionStatus.Active || session.AdmissionClosed)
                    throw new DaemonCommandException();

                var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                commandTask = session.Client.PrefillAsync(all, recent, recentlyPurchased, top, force,
                    operatingSystems, maxConcurrency, cachedDepots, cancellationToken, runId,
                    () => dispatched.TrySetResult());
                await Task.WhenAny(dispatched.Task, commandTask);
            }
            finally { session.PrefillWork.Release(); }

            var result = await commandTask;
            result.RunId = runId;
            if (!result.Success || result.RequiresLogin)
            {
                var failure = new DaemonCommandException(result.ErrorCode, result.RequiresLogin);
                result.Success = false;
                result.ErrorMessage = failure.Message;
                result.ErrorCode = failure.ErrorCode;
                result.RequiresLogin = failure.RequiresLogin;
                result.StageKey = failure.StageKey;
                await TransitionToTerminalAsync(session, PrefillState.Failed, runId, failure.Message, failure.StageKey);
            }
            return result;
        }
        catch (PrefillAlreadyRunningException) { throw; }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already in progress", StringComparison.OrdinalIgnoreCase))
        {
            throw new PrefillAlreadyRunningException("A prefill is already in progress.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (runId is not null)
                await TransitionToTerminalAsync(session, PrefillState.Cancelled, runId,
                    cancelDaemon: commandTask is not null);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prefill command failed for session {SessionId}, run {RunId}", sessionId, runId);
            var failure = ex as DaemonCommandException ?? new DaemonCommandException();
            if (runId is not null)
                await TransitionToTerminalAsync(session, PrefillState.Failed, runId, failure.Message, failure.StageKey);
            throw failure;
        }
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

    public async Task CancelPrefillRunAsync(string sessionId, Guid runId, CancellationToken cancellationToken = default,
        string? reason = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        if (!session.Runs.TryGetValue(runId, out var run))
        {
            if (session.Runs.IsEmpty && session.PrefillRunId == runId)
            {
                await CancelPrefillAsync(sessionId, cancellationToken);
                return;
            }
            throw new DaemonCommandException("operation-not-found");
        }
        await run.PrefillWork.WaitAsync(cancellationToken);
        try
        {
            if (run.TerminalCompletedFlag != 0) return;
            if (!run.CancelRequested || (reason is not null && run.CancelReason is null))
                await _sessionService.SetRunCancellationAsync(runId, cancellationToken, reason);
            run.CancelRequested = true;
            run.CancelReason ??= reason;
            if (session.Recovering) return;
            try
            {
                await session.Client.CancelPrefillAsync(runId, run.DaemonInstanceId, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Net.Sockets.SocketException)
            {
                run.Recovering = true;
                session.Recovering = true;
                _logger.LogWarning(ex, "Cancellation awaits reconnection for prefill {RunId}", runId);
            }
        }
        finally { run.PrefillWork.Release(); }
        if (!session.Recovering)
            await ReconcileRunAsync(session, run, cancellationToken);
    }

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
                throw new DaemonCommandException("auth-lost", true);
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
        try
        {
            await using var mutation = session.IsPersistent
                ? await PersistentEditSessionGate.EnterMutationAsync(cancellationToken) : null;
            if (!IsSessionLive(session) || session.AdmissionClosed || session.Recovering
                || session.Capabilities?.DaemonInstanceId != run.DaemonInstanceId
                || session.LoginOperationId.HasValue || (session.IsPersistent && PersistentEditSessionGate.HasPendingStart()))
                throw new DaemonCommandException("instance-changed");
            await _sessionService.CreateRunAsync(run, scheduleId.HasValue ? "schedule" : "manual", cancellationToken);
            persisted = true;
            var cachedDepots = await GetCachedDepotsAsync(options.Force, session.CancellationTokenSource.Token);
            var result = await session.Client.PrefillAsync(runId, run.DaemonInstanceId, options,
                cachedDepots: cachedDepots,
                cancellationToken: session.CancellationTokenSource.Token);
            if (!result.Success)
            {
                var failure = new DaemonCommandException(result.ErrorCode, result.RequiresLogin);
                await FinishRunAsync(session, run, run.Snapshot with
                {
                    State = "failed",
                    Reason = failure.ErrorCode,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Sequence = run.Snapshot.Sequence + 1
                }, [], CancellationToken.None);
                if (result.ErrorCode == "run-limit")
                    throw new PrefillAlreadyRunningException(failure.Message);
                throw failure;
            }
            return result;
        }
        catch (Exception ex) when (persisted && run.TerminalCompletedFlag == 0
            && ex is not PrefillAlreadyRunningException && ex is not DaemonCommandException)
        {
            // A lost acknowledgement is not evidence that the process-owned run stopped.
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
            run.AdmissionPending = false;
            if (!persisted) session.Runs.TryRemove(runId, out _);
            run.PrefillWork.Release();
            if (persisted && run.TerminalCompletedFlag == 0)
                FireAndForgetAsync(() => RefreshRunsAsync(session.Id), nameof(RefreshRunsAsync));
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
    /// Adds a SignalR connection as a subscriber to session events.
    /// Limits connections per session to prevent duplicate event broadcasts
    /// from stale connections accumulating during page navigations.
    /// </summary>
    public void AddSubscriber(string sessionId, string connectionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            // If we're at the limit, remove oldest connections to make room
            // This prevents stale connections from accumulating during page navigations
            while (session.SubscribedConnections.Count >= MaxConnectionsPerSession)
            {
                var oldest = session.SubscribedConnections.First();
                session.SubscribedConnections.Remove(oldest);
                _logger.LogDebug("Removed stale subscriber {ConnectionId} from session {SessionId} (limit reached)",
                    oldest, sessionId);
            }

            session.SubscribedConnections.Add(connectionId);
            _logger.LogDebug("Added subscriber {ConnectionId} to session {SessionId} (total: {Count})",
                connectionId, sessionId, session.SubscribedConnections.Count);
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
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        if (!session.Runs.IsEmpty)
        {
            foreach (var run in GetRuns(sessionId))
            {
                if (run.Progress is not null)
                    await SendToClientAsync(connectionId, EventPrefillProgress,
                        new { sessionId, progress = run.Progress }).WaitAsync(TimeSpan.FromSeconds(5));
            }
            return;
        }
        Guid? runId;
        lock (session.PrefillLock)
            runId = session.PrefillRunId;
        await session.PrefillWork.WaitAsync();
        try
        {
            PrefillProgress? snapshot;
            lock (session.PrefillLock)
            {
                if (!IsSessionLive(session) || runId is null || session.PrefillRunId != runId
                    || !session.IsPrefilling || session.TerminalCompletedFlag != 0)
                    return;
                snapshot = session.LastProgress;
            }
            if (snapshot is null) return;
            try
            {
                await SendToClientAsync(connectionId, EventPrefillProgress,
                    new { sessionId = session.Id, progress = snapshot });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to replay prefill progress to connection {ConnectionId} for session {SessionId}",
                    connectionId, sessionId);
            }
        }
        finally { session.PrefillWork.Release(); }
    }

    /// <summary>
    /// Removes a SignalR connection from all session subscriptions
    /// </summary>
    public void RemoveSubscriber(string connectionId)
    {
        foreach (var session in _sessions.Values)
        {
            session.SubscribedConnections.Remove(connectionId);
        }
    }
}
