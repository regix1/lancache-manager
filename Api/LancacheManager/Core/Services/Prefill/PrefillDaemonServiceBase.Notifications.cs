using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Models;
using System.Text.Json;

namespace LancacheManager.Core.Services;

public abstract partial class PrefillDaemonServiceBase
{
    #region Socket Event Handlers

    /// <summary>
    /// Handles credential challenge events from socket communication.
    /// Protected (not private) so tests can invoke it directly via a subclass seam, mirroring
    /// FailLoginFastAsync's accessibility widening in PersistentLoginChallengeResumeTests.cs -
    /// production session creation wires this to <c>IDaemonClient.OnCredentialChallenge</c>, which
    /// a test harness that injects a session directly bypasses.
    /// </summary>
    protected async Task OnCredentialChallengeAsync(DaemonSession session, CredentialChallenge challenge)
    {
        try
        {
            // RC3: a challenge can still arrive from a daemon
            // client whose session was already terminated/replaced - the client read loop delivering a
            // queued "username" challenge right after Stop removed the session, just as a fresh
            // container becomes the service's active session. Writing it to PendingLoginChallenge or
            // broadcasting it would repopulate a dead session's state and let that stale challenge leak
            // toward the replacement (the "Received queued challenge for session <dead>" anomaly).
            // Ignore any challenge whose session is no longer the live, Active session under its id.
            if (!_sessions.TryGetValue(session.Id, out var liveSession)
                || !ReferenceEquals(liveSession, session)
                || session.Status != DaemonSessionStatus.Active)
            {
                _logger.LogWarning(
                    "Ignoring credential challenge {ChallengeId} ({CredentialType}) for session {SessionId}: " +
                    "session is no longer the active live session (status {Status})",
                    challenge.ChallengeId, challenge.CredentialType, session.Id, session.Status);
                return;
            }

            // A headless manager-initiated login currently owns this session's login flow (it holds
            // LoginLock and set this flag for the duration of its attempt). Its challenge is consumed
            // from the command return channel and silently cancelled, so publishing it here - the
            // auth-state rewrite, the resume-cache write, and the SignalR broadcasts below - would
            // hand the UI a challenge that is about to be revoked. Drop it entirely; interactive
            // logins never set the flag, so their challenge delivery is unchanged.
            if (session.SuppressLoginChallengePublication)
            {
                _logger.LogDebug(
                    "Suppressing credential challenge {ChallengeId} ({CredentialType}) for session {SessionId}: a headless login attempt owns this login flow",
                    challenge.ChallengeId, challenge.CredentialType, session.Id);
                return;
            }

            // Stale re-delivery guard (Bug #3): the daemon delivers each credential challenge over TWO
            // channels - the WaitForChallengeAsync return value AND this OnCredentialChallenge event. Once
            // the caller has answered a challenge, ProvideCredentialAsync clears the cache and records its
            // ChallengeId in LastConsumedLoginChallengeId. If the OTHER channel then delivers that SAME
            // already-consumed challenge here (a late duplicate, NOT a new step), re-caching it would replay
            // the answered challenge to the next WaitForChallengeAsync/GET poll - the "challenge:password
            // twice" race that stalls the login before device-confirmation - and would regress AuthState
            // back to the consumed step. Drop it before any state write. A genuine follow-on challenge (e.g.
            // device-confirmation after password) always carries a NEW ChallengeId and falls through to be
            // cached and broadcast below. Mirrors the frontend isRedelivery guard (persistentLoginStore.ts).
            if (!string.IsNullOrEmpty(session.LastConsumedLoginChallengeId)
                && string.Equals(challenge.ChallengeId, session.LastConsumedLoginChallengeId, StringComparison.Ordinal))
            {
                _logger.LogDebug(
                    "Ignoring stale re-delivery of already-consumed credential challenge {ChallengeId} " +
                    "({CredentialType}) for session {SessionId}",
                    challenge.ChallengeId, challenge.CredentialType, session.Id);
                return;
            }

            // Update auth state based on credential type
            session.AuthState = challenge.CredentialType switch
            {
                "username" => DaemonAuthState.UsernameRequired,
                "password" => DaemonAuthState.PasswordRequired,
                "2fa" => DaemonAuthState.TwoFactorRequired,
                "steamguard" => DaemonAuthState.SteamGuardRequired,
                "device-confirmation" => DaemonAuthState.DeviceConfirmationRequired,
                "authorization-url" => DaemonAuthState.AuthorizationUrlRequired,
                _ => session.AuthState
            };

            // Cache this as the session's current resumable challenge BEFORE the hub push below.
            // This is the ONLY place a follow-on challenge (password after username, 2FA after
            // password, etc.) reaches the cache - StartLoginCoreAsync only sets it for the FIRST
            // challenge of a fresh attempt. Without this, ProvideCredentialAsync consuming the
            // prior challenge plus this event delivering the next one would leave a stale earlier
            // challenge (or nothing) in the cache for any REST resume/poll (GET /challenge, the
            // reopen reconcile, or a SignalR-down poll fallback) to serve.
            session.PendingLoginChallenge = challenge;

            await NotifyCredentialChallengeAsync(session, challenge);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling socket credential challenge for session {SessionId}", session.Id);
        }
    }

    /// <summary>
    /// Handles status update events from socket communication.
    /// </summary>
    private async Task OnStatusChangeAsync(DaemonSession session, DaemonStatus status)
    {
        try
        {
            // Post-detach/replace/terminate guard: daemon event callbacks are dispatched fire-and-forget,
            // so a queued status event can arrive after this session was detached at shutdown, replaced
            // (error-state), or terminated. Ignore it before any DB write / broadcast if it is no longer
            // the live registered instance for its id. Re-checked after each await below so a callback that
            // escaped the bounded teardown drain (took longer than the timeout) cannot resurrect teardown
            // state (mirrors the OnCredentialChallengeAsync guard above).
            if (!IsSessionLive(session))
            {
                return;
            }

            var previousAuthState = session.AuthState;

            // Update auth state based on status
            var newAuthState = status.Status switch
            {
                "awaiting-login" => DaemonAuthState.NotAuthenticated,
                "not-logged-in" when status.SupportsConcurrentPrefill && Platform.RequiresLogin() => DaemonAuthState.NotAuthenticated,
                "logged-in" => DaemonAuthState.Authenticated,
                _ => session.AuthState
            };

            session.AuthState = newAuthState;

            // Capture the resolved account display name from either ingest field (GetStatus
            // AccountDisplayName or AuthState DisplayName). No platform string gate: any authenticated
            // session with a non-empty resolved name gets Username + AccountUsername set so admin UI and
            // username bans work. Anonymous Battle.net/Riot daemons report no name and ban via UserId.
            if (newAuthState == DaemonAuthState.Authenticated)
            {
                var accountName = status.ResolveAccountDisplayName();
                if (!string.IsNullOrEmpty(accountName)
                    && !string.Equals(session.Username, accountName, StringComparison.Ordinal))
                {
                    session.Username = accountName;
                    session.AccountUsername = accountName;
                    await _sessionService.SetUsernameAsync(session.Id, accountName);

                    // Late name capture while already Authenticated: KickBanned skips empty Username, so
                    // re-enforce now that a ban key exists. Awaited (not fire-and-forget) so the ban
                    // window is not lost.
                    if (!IsSessionLive(session))
                    {
                        return;
                    }

                    try
                    {
                        await KickBannedSessionsAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to enforce username bans after account name capture for session {SessionId}",
                            session.Id);
                    }

                    // Push SessionUpdated only while live so management UIs see the username without
                    // polling. Auth-state transitions also push via NotifyAuthStateChangeAsync; this
                    // covers the late-capture path where AuthState does not change.
                    if (!IsSessionLive(session))
                    {
                        return;
                    }

                    try
                    {
                        var dto = DaemonSessionDto.FromSession(session);
                        await NotifyHubAsync(EventSessionUpdated, dto);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to broadcast session update after account name capture for session {SessionId}",
                            session.Id);
                    }
                }
            }

            // The current auth state can change while the account-name write is pending.
            if (!IsSessionLive(session) || session.AuthState != newAuthState)
            {
                return;
            }

            if (session.AuthState != previousAuthState)
            {
                await NotifyAuthStateChangeAsync(session);

                if (!IsSessionLive(session) || session.AuthState != newAuthState)
                    return;

                // Notify derived class when a daemon becomes authenticated
                if (newAuthState == DaemonAuthState.Authenticated)
                {
                    FireAndForgetAsync(OnSessionAuthenticatedAsync, nameof(OnSessionAuthenticatedAsync));
                }
                // Notify when auth state changes FROM authenticated to non-authenticated
                else if (previousAuthState == DaemonAuthState.Authenticated && newAuthState != DaemonAuthState.Authenticated)
                {
                    await Task.WhenAll(session.Runs.Values.Where(run => run.TerminalCompletedFlag == 0).Select(async run =>
                    {
                        try { await CancelPrefillRunAsync(session.Id, run.PrefillRunId, reason: "auth-lost"); }
                        catch (Exception ex)
                        {
                            run.Recovering = true;
                            session.Recovering = true;
                            _logger.LogWarning(ex, "Authentication loss awaits reconciliation for prefill {RunId}", run.PrefillRunId);
                        }
                    }));
                    // Check if any other daemons are still authenticated
                    if (!IsAnyDaemonAuthenticated())
                    {
                        FireAndForgetAsync(OnAllSessionsLoggedOutAsync, nameof(OnAllSessionsLoggedOutAsync));
                    }
                }
            }

            if (!IsSessionLive(session))
            {
                return;
            }

            if (session.AuthState == newAuthState)
                await NotifyStatusChangeAsync(session, status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling socket status change for session {SessionId}", session.Id);
        }
    }

    /// <summary>
    /// True when <paramref name="session"/> is still the live, registered in-memory instance for its id -
    /// i.e. teardown (detach / terminate / error-replacement) has not de-registered or replaced it. The
    /// event handlers re-check this after every await before a durable DB write or broadcast, so a callback
    /// that escaped the bounded teardown drain cannot resurrect teardown state.
    /// </summary>
    internal bool IsSessionLive(DaemonSession session)
        => _sessions.TryGetValue(session.Id, out var live) && ReferenceEquals(live, session);

    /// <summary>
    /// Handles progress update events from socket communication.
    /// </summary>
    private async Task OnProgressChangeAsync(DaemonSession session, SocketPrefillProgress socketProgress)
    {
        try
        {
            if (!session.Runs.IsEmpty || session.Capabilities?.SupportsConcurrentPrefill == true)
            {
                if (IsSessionLive(session) && Guid.TryParse(socketProgress.OperationId, out var id)
                    && session.Runs.TryGetValue(id, out var run)
                    && run.DaemonInstanceId == socketProgress.DaemonInstanceId
                    && socketProgress.Sequence > run.Snapshot.Sequence && run.TerminalCompletedFlag == 0)
                    await ReconcileRunAsync(session, run, session.CancellationTokenSource.Token);
                return;
            }
            Guid? runId;
            lock (session.PrefillLock)
            {
                runId = session.PrefillRunId;
                if (socketProgress.OperationId is not null
                    && (!Guid.TryParse(socketProgress.OperationId, out var operationId) || operationId != runId))
                    return;
                if (runId is null || !session.IsPrefilling || session.TerminalCompletedFlag != 0)
                    return;
            }
            // Post-detach/replace/terminate guard (see OnStatusChangeAsync): a fire-and-forget progress
            // event can arrive after this session was torn down; ignore it before any history/cache write
            // or broadcast if it is no longer the live registered instance. NotifyPrefillProgressAsync
            // re-checks after its awaits before durable writes/broadcasts.
            if (!IsSessionLive(session))
            {
                return;
            }

            // Convert socket progress to internal PrefillProgress format
            // Property names match daemon's PrefillProgressUpdate class
            var progress = new PrefillProgress
            {
                OperationId = runId.ToString(),
                ErrorCode = socketProgress.ErrorCode,
                RequiresLogin = socketProgress.RequiresLogin,
                State = socketProgress.State ?? "downloading",
                // The daemon sends currentAppId as a number and uses 0 for "no app in flight",
                // which the flexible string converter turns into "0" rather than null. Every
                // downstream check is a null/empty test, so an un-normalized "0" reads as a real
                // app: it opened a history entry after the last game finished that nothing could
                // ever complete, and session teardown then recorded it as a cancelled "App 0".
                CurrentAppId = socketProgress.CurrentAppId == "0" ? null : socketProgress.CurrentAppId,
                CurrentAppName = socketProgress.CurrentAppName,
                TotalBytes = socketProgress.TotalBytes,
                BytesDownloaded = socketProgress.BytesDownloaded,
                PercentComplete = socketProgress.PercentComplete,
                BytesPerSecond = (long)socketProgress.BytesPerSecond,
                ElapsedSeconds = socketProgress.ElapsedSeconds,
                TotalApps = socketProgress.TotalApps,
                UpdatedApps = socketProgress.UpdatedApps,
                AlreadyUpToDate = socketProgress.AlreadyUpToDate,
                FailedApps = socketProgress.FailedApps,
                TotalBytesTransferred = socketProgress.TotalBytesTransferred,
                TotalTimeSeconds = socketProgress.TotalTimeSeconds,
                UpdatedAt = socketProgress.UpdatedAt,
                Result = socketProgress.Result,
                ErrorMessage = string.IsNullOrEmpty(socketProgress.ErrorMessage) ? null
                    : new DaemonCommandException(socketProgress.ErrorCode, socketProgress.RequiresLogin == true).Message,
                // Map depot info for cache tracking
                Depots = socketProgress.Depots?.Select(d => new DepotManifestProgressInfo
                {
                    DepotId = d.DepotId,
                    ManifestId = d.ManifestId,
                    TotalBytes = d.TotalBytes
                }).ToList()
            };

            _logger.LogDebug("Socket Progress: {AppName} ({AppId}) - {State}, {Bytes}/{Total} bytes",
                progress.CurrentAppName, progress.CurrentAppId, progress.State,
                progress.BytesDownloaded, progress.TotalBytes);

            await NotifyPrefillProgressAsync(session, progress);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling socket progress for session {SessionId}", session.Id);
        }
    }

    #endregion

    /// <summary>
    /// Broadcasts a payload to all subscribed connections for a session.
    /// On error, removes the failing connectionId from the session's subscriptions unless removeOnError is false.
    /// </summary>
    private async Task BroadcastToSubscribersAsync(DaemonSession session, string eventName, object payload)
    {
        string[] connections;
        lock (session.PrefillLock) connections = session.SubscribedConnections.ToArray();
        await Task.WhenAll(connections.Select(async connectionId =>
        {
            try
            {
                await SendToClientAsync(connectionId, eventName, payload).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify {EventName} to {ConnectionId}, removing subscription", eventName, connectionId);
                lock (session.PrefillLock) session.SubscribedConnections.Remove(connectionId);
            }
        }));
    }

    protected async Task NotifyAuthStateChangeAsync(DaemonSession session)
    {
        var authState = session.AuthState;
        // Every ending of a login comes through here - the daemon's own success broadcast, a fail-fast,
        // the user cancelling, a logout, a login command that never reached the daemon, and the headless
        // abandon - so this is the single place a login's card gets closed. LoggingIn is the one state
        // that is not an ending: it is the broadcast that opens the flow. Done first so a broadcast
        // failure further down cannot leave a card running for a login that is already over.
        if (session.AuthState != DaemonAuthState.LoggingIn)
        {
            CompleteLoginOperation(session);
        }

        // Single robust point covering every login path (interactive + auto-login): once a
        // session transitions to Authenticated, it no longer needs a re-login. Clear the flag
        // here so a previously-flagged persistent container stops reporting needs-relogin.
        // Also drop any cached resume challenge - once the daemon has moved on to Authenticated,
        // that challenge is stale and must never be served to a later resume.
        if (session.AuthState == DaemonAuthState.Authenticated)
        {
            session.NeedsRelogin = false;
            ClearPendingLoginChallenge(session);
        }

        // The auth transition changes this platform's persistent-container/integration aggregate (and, for
        // an anonymous daemon, its connected state), so refresh the session's activity presence here.
        await ReportSessionActivityAsync(session, present: true);

        if (!IsSessionLive(session) || session.AuthState != authState)
            return;

        var payload = new { sessionId = session.Id, authState = authState.ToString() };
        await BroadcastToSubscribersAsync(session, EventAuthStateChanged, payload);

        // Liveness fence: the subscriber fan-out above can outlast a teardown that won the bounded drain;
        // stop before mirroring later auth/session updates to the hubs for a session no longer live.
        if (!IsSessionLive(session) || session.AuthState != authState)
        {
            return;
        }

        // Mirror to DownloadHub so management UIs (persistent container list, prefill sessions)
        // update via SignalR instead of polling when a daemon self-authenticates or logs in.
        await NotifyHubAsync(EventAuthStateChanged, payload);

        // Re-check after the hub push before the session-update broadcast: teardown may have won during it.
        if (!IsSessionLive(session) || session.AuthState != authState)
        {
            return;
        }

        try
        {
            var dto = DaemonSessionDto.FromSession(session);
            await NotifyHubAsync(EventSessionUpdated, dto);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to broadcast session update after auth state change for session {SessionId}",
                session.Id);
        }
    }

    private async Task NotifyCredentialChallengeAsync(DaemonSession session, CredentialChallenge challenge)
    {
        // Stamped once here so both broadcasts below carry the login's operation id, matching what the
        // login call's own return value carries.
        challenge.OperationId = session.LoginOperationId?.ToString();

        await BroadcastToSubscribersAsync(session, EventCredentialChallenge,
            new { sessionId = session.Id, challenge });

        // Liveness fence: the subscriber fan-out above can outlast a teardown that won the bounded drain;
        // don't mirror the challenge to the DownloadHub for a session that is no longer the live instance.
        if (!IsSessionLive(session))
        {
            return;
        }

        // Mirror to DownloadHub (matches NotifyAuthStateChangeAsync's AuthStateChanged/SessionUpdated
        // mirror above) so the persistent-container config modal - which never calls
        // SubscribeToSessionAsync, unlike the mapping-flow live login - receives the challenge the
        // instant the daemon emits it instead of waiting on the REST challenge poll. Same Clients.All
        // scoping as every other event this funnel already broadcasts; not narrowed to an admin-only
        // group since no such group plumbing exists for this funnel today (see W4 results for the
        // scoping rationale).
        await NotifyHubAsync(EventCredentialChallenge, new { sessionId = session.Id, challenge });
    }

    private async Task NotifyStatusChangeAsync(DaemonSession session, DaemonStatus status)
    {
        await BroadcastToSubscribersAsync(session, EventStatusChanged,
            new { sessionId = session.Id, status });
    }

    /// <summary>
    /// Emits the non-terminal <c>started</c> state transition: resets the per-run terminal
    /// idempotency guard, records the start time, clears any previous completion result, and
    /// broadcasts exactly one <c>PrefillStateChanged</c>. Terminal transitions
    /// (completed/failed/cancelled) go exclusively through <see cref="TransitionToTerminalAsync"/>.
    /// </summary>
    private async Task NotifyPrefillStartedAsync(DaemonSession session)
    {
        var state = PrefillProgressState.Started.ToWireString();
        var started = new { sessionId = session.Id, state, durationSeconds = (int?)null };
        await BroadcastToSubscribersAsync(session, EventPrefillStateChanged, started);
        if (!IsSessionLive(session)) return;
        await NotifyHubAsync(EventPrefillStateChanged, started);
        if (!IsSessionLive(session)) return;
        await ReportSessionActivityAsync(session, present: true);
    }

    /// <summary>
    /// THE SINGLE terminal funnel for a prefill run. Idempotent via an
    /// <see cref="Interlocked.CompareExchange(ref int, int, int)"/> guard on
    /// <see cref="DaemonSession.TerminalCompletedFlag"/>, so a socket-death + a late daemon
    /// terminal event can never double-fire. This is the ONLY place that:
    /// sets <c>IsPrefilling=false</c>, records the <c>LastPrefill*</c> completion result,
    /// clears the <c>LastProgress</c> snapshot, and emits exactly one <c>PrefillStateChanged</c>.
    /// ALL terminal paths (completed / failed / cancelled / cancel / socket-disconnect) route here.
    /// </summary>
    private async Task<bool> TransitionToTerminalAsync(
        DaemonSession session, PrefillState terminalState, Guid? runId,
        string? reason = null, string? stageKey = null, PrefillProgress? progress = null,
        Func<bool>? canClaim = null, bool cancelDaemon = false, bool cancelBeforeClaim = false,
        CancellationToken cancellationToken = default)
    {
        await session.PrefillWork.WaitAsync(cancellationToken);
        try
        {
            IDaemonClient client;
            lock (session.PrefillLock)
            {
                if (runId is null || !IsSessionLive(session) || session.PrefillRunId != runId
                    || !session.IsPrefilling || session.TerminalCompletedFlag != 0
                    || session.CancellationTokenSource.IsCancellationRequested
                    || canClaim?.Invoke() == false)
                    return false;
                client = session.Client;
            }

            if (cancelDaemon && cancelBeforeClaim)
                await client.CancelPrefillAsync(cancellationToken);

            string? appId;
            long bytesDownloaded;
            long totalBytes;
            int? durationSeconds;
            string state;
            lock (session.PrefillLock)
            {
                if (!IsSessionLive(session) || session.PrefillRunId != runId
                    || !ReferenceEquals(client, session.Client) || !session.IsPrefilling
                    || session.TerminalCompletedFlag != 0 || session.CancellationTokenSource.IsCancellationRequested
                    || canClaim?.Invoke() == false)
                    return false;

                session.TerminalCompletedFlag = 1;
                appId = session.CurrentAppId;
                bytesDownloaded = session.CurrentBytesDownloaded;
                totalBytes = session.CurrentTotalBytes;
                durationSeconds = session.PrefillStartedAt.HasValue
                    ? (int)(DateTime.UtcNow - session.PrefillStartedAt.Value).TotalSeconds : null;
                state = terminalState switch
                {
                    PrefillState.Completed => PrefillProgressState.Completed.ToWireString(),
                    PrefillState.Cancelled => PrefillProgressState.Cancelled.ToWireString(),
                    _ => PrefillProgressState.Failed.ToWireString()
                };
                if (progress is not null)
                    UpdateTransferredBytes(session, progress.TotalBytesTransferred);
                session.ErrorMessage = reason;
                session.ErrorStageKey = stageKey;
                session.PrefillState = terminalState;
                session.LastProgress = null;
                Volatile.Write(ref session.LastProgressTicksUtc, 0L);
                session.CurrentAppId = null;
                session.CurrentAppName = null;
                session.PreviousAppId = null;
                session.PreviousAppName = null;
                session.CurrentBytesDownloaded = 0;
                session.CurrentTotalBytes = 0;
                session.LastPrefillCompletedAt = DateTime.UtcNow;
                session.LastPrefillDurationSeconds = durationSeconds;
                session.LastPrefillStatus = state;
                session.IsPrefilling = false;
            }

            try
            {
                try
                {
                    if (terminalState == PrefillState.Cancelled)
                    {
                        await _sessionService.CancelEntriesAsync(session.Id);
                        if (appId is not null && IsSessionLive(session))
                            await BroadcastHistoryUpdatedAsync(session.Id, appId, "Cancelled");
                    }
                    else if (appId is not null)
                    {
                        var status = terminalState == PrefillState.Failed ? "Failed"
                            : bytesDownloaded == 0 ? "Cached" : "Completed";
                        await _sessionService.CompleteEntryAsync(
                            session.Id, appId, status, bytesDownloaded, totalBytes, reason);
                        if (IsSessionLive(session))
                            await BroadcastHistoryUpdatedAsync(session.Id, appId, status);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to close prefill history for session {SessionId}, run {RunId}", session.Id, runId);
                }

                if (cancelDaemon && !cancelBeforeClaim)
                {
                    try
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await client.CancelPrefillAsync(cleanup.Token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not confirm prefill cancellation for session {SessionId}, run {RunId}", session.Id, runId);
                        lock (session.PrefillLock)
                        {
                            if (IsSessionLive(session) && session.PrefillRunId == runId
                                && ReferenceEquals(session.Client, client))
                                session.Status = DaemonSessionStatus.Error;
                        }
                    }
                }

                if (!IsSessionLive(session)) return true;
                _logger.LogInformation("Prefill {State} for session {SessionId}, run {RunId}, duration: {Duration}s",
                    state, session.Id, runId, durationSeconds ?? 0);
                var terminal = new { sessionId = session.Id, state, durationSeconds };
                try
                {
                    await BroadcastToSubscribersAsync(session, EventPrefillStateChanged, terminal);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to publish prefill terminal for session {SessionId}", session.Id);
                }
                if (!IsSessionLive(session)) return true;
                try
                {
                    await NotifyHubAsync(EventPrefillStateChanged, terminal);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to publish prefill terminal to the hub for session {SessionId}", session.Id);
                }
                if (!IsSessionLive(session)) return true;
                try
                {
                    var snapshot = DaemonSessionDto.FromSession(session);
                    await NotifyHubAsync(EventSessionUpdated, snapshot);
                    if (IsSessionLive(session))
                        await ReportSessionActivityAsync(session, present: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to publish the finished prefill session {SessionId}", session.Id);
                }
                return true;
            }
            finally
            {
                lock (session.PrefillLock)
                {
                    if (session.PrefillRunId == runId && session.TerminalCompletedFlag == 1)
                        session.TerminalCompletedFlag = 2;
                }
            }
        }
        finally
        {
            session.PrefillWork.Release();
        }
    }

    /// <summary>
    /// Handles a daemon progress push for a session. Protected (not private) so tests can invoke it
    /// directly via a subclass seam, mirroring <see cref="OnCredentialChallengeAsync"/>'s accessibility
    /// widening - production wires this via the socket read loop (<see cref="OnProgressChangeAsync"/>),
    /// which a test harness that injects a session directly bypasses.
    /// </summary>
    protected async Task NotifyPrefillProgressAsync(DaemonSession session, PrefillProgress progress)
    {
        if (!session.Runs.IsEmpty)
        {
            if (IsSessionLive(session) && Guid.TryParse(progress.OperationId, out var id)
                && session.Runs.TryGetValue(id, out var run) && run.DaemonInstanceId == progress.DaemonInstanceId
                && progress.Sequence > run.Snapshot.Sequence && run.TerminalCompletedFlag == 0)
                await ReconcileRunAsync(session, run, session.CancellationTokenSource.Token);
            return;
        }
        Guid? runId;
        lock (session.PrefillLock)
        {
            runId = session.PrefillRunId;
            if (progress.OperationId is not null
                && (!Guid.TryParse(progress.OperationId, out var operationId) || operationId != runId))
                return;
            if (runId is null || !IsSessionLive(session) || !session.IsPrefilling
                || session.TerminalCompletedFlag != 0)
                return;
        }

        var progressState = PrefillProgressStateExtensions.ParseOrUnknown(progress.State);
        if (progressState is PrefillProgressState.Completed or PrefillProgressState.Failed
            or PrefillProgressState.Error or PrefillProgressState.Cancelled)
        {
            var terminalState = progressState switch
            {
                PrefillProgressState.Completed => PrefillState.Completed,
                PrefillProgressState.Cancelled => PrefillState.Cancelled,
                _ => PrefillState.Failed
            };
            var failure = terminalState == PrefillState.Failed
                ? new DaemonCommandException(progress.ErrorCode, progress.RequiresLogin == true) : null;
            await TransitionToTerminalAsync(session, terminalState, runId,
                failure?.Message, failure?.StageKey, progress);
            return;
        }

        await session.PrefillWork.WaitAsync();
        try
        {
            long sequence;
            string? previousAppId;
            string? previousAppName;
            long previousBytes;
            long previousTotal;
            bool startingNewApp;
            long bytesDownloaded;
            long totalBytes;
            string? account;
            var appId = string.IsNullOrEmpty(progress.CurrentAppId) ? null : progress.CurrentAppId;
            var appName = progress.CurrentAppName;
            var appCompleted = progressState == PrefillProgressState.AppCompleted && appId is not null;
            lock (session.PrefillLock)
            {
                if (!IsSessionLive(session) || session.PrefillRunId != runId
                    || !session.IsPrefilling || session.TerminalCompletedFlag != 0)
                    return;
                if (appCompleted && session.CurrentAppId is null && session.PreviousAppId == appId
                    && session.LastProgress?.State is "app_completed" or "already_cached")
                    return;

                sequence = ++session.ProgressSequence;
                previousAppId = session.CurrentAppId;
                previousAppName = session.CurrentAppName;
                previousBytes = session.CurrentBytesDownloaded;
                previousTotal = session.CurrentTotalBytes;
                account = session.AccountUsername;
                startingNewApp = appId is not null
                    && (previousAppId != appId || previousAppName != appName);
                if (startingNewApp)
                {
                    session.PreviousAppId = previousAppId;
                    session.PreviousAppName = previousAppName;
                    session.CompletedBytesTransferred = Math.Max(session.CompletedBytesTransferred, session.TotalBytesTransferred);
                    session.CurrentBytesDownloaded = 0;
                    session.CurrentTotalBytes = 0;
                }
                session.CurrentAppId = appId;
                session.CurrentAppName = appName;
                if (appId is not null)
                {
                    if (progress.BytesDownloaded > 0) session.CurrentBytesDownloaded = progress.BytesDownloaded;
                    if (progress.TotalBytes > 0) session.CurrentTotalBytes = progress.TotalBytes;
                }
                bytesDownloaded = session.CurrentBytesDownloaded;
                totalBytes = session.CurrentTotalBytes;
                if (appCompleted)
                {
                    session.PreviousAppId = appId;
                    session.PreviousAppName = appName;
                    session.CurrentAppId = null;
                    session.CurrentAppName = null;
                    session.CurrentBytesDownloaded = 0;
                    session.CurrentTotalBytes = 0;
                    UpdateTransferredBytes(session, Math.Max(session.TotalBytesTransferred,
                        session.CompletedBytesTransferred + bytesDownloaded));
                }
                else
                {
                    UpdateTransferredBytes(session, progress.TotalBytesTransferred > 0
                        ? progress.TotalBytesTransferred : session.CompletedBytesTransferred + progress.BytesDownloaded);
                }
                progress.OperationId = runId.ToString();
                progress.TotalBytesTransferred = session.TotalBytesTransferred;
            }

            if (startingNewApp)
            {
                if (previousAppId is not null)
                {
                    try
                    {
                        var status = previousBytes == 0 ? "Cached" : "Completed";
                        await _sessionService.CompleteEntryAsync(session.Id, previousAppId, status, previousBytes, previousTotal);
                        if (!IsSessionLive(session)) return;
                        await BroadcastHistoryUpdatedAsync(session.Id, previousAppId, status);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to complete prefill history for app {AppId}", previousAppId);
                    }
                }
                if (!IsSessionLive(session)) return;
                try
                {
                    var entry = await _sessionService.StartEntryAsync(session.Id, appId!, appName);
                    if (!IsSessionLive(session)) return;
                    if (entry is not null)
                        await BroadcastHistoryUpdatedAsync(session.Id, appId!, "InProgress");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to start prefill history for app {AppId}", appId);
                }
            }
            if (!IsSessionLive(session)) return;

            if (appCompleted)
            {
                var isCached = progress.Result is "AlreadyUpToDate" or "Skipped" or "NoDepotsToDownload";
                var status = isCached ? "Cached" : progress.Result == "Failed" ? "Failed" : "Completed";
                try
                {
                    var failure = progress.Result == "Failed"
                        ? new DaemonCommandException(progress.ErrorCode, progress.RequiresLogin == true) : null;
                    var entry = await _sessionService.CompleteEntryAsync(
                        session.Id, appId!, status, bytesDownloaded, totalBytes, failure?.Message);
                    if (!IsSessionLive(session)) return;
                    await BroadcastHistoryUpdatedAsync(session.Id, appId!, status);
                    if (!IsSessionLive(session)) return;
                    if (entry is not null && progress.Result != "Failed" && !session.CancellationTokenSource.IsCancellationRequested)
                    {
                        try
                        {
                            var recorded = await _cacheService.RecordCachedAppAsync(
                                Platform, appId!, appName, totalBytes, account);
                            if (!IsSessionLive(session)) return;
                            if (!session.CancellationTokenSource.IsCancellationRequested
                                && Platform == PrefillPlatform.Steam
                                && progress.Result is "Success" or "AlreadyUpToDate"
                                && progress.Depots is { Count: > 0 }
                                && uint.TryParse(appId, out var numericAppId))
                            {
                                recorded |= await _cacheService.RecordCachedDepotsAsync(numericAppId, appName,
                                    progress.Depots.Select(d => (d.DepotId, d.ManifestId, d.TotalBytes)), account);
                            }
                            if (!IsSessionLive(session)) return;
                            if (recorded && !session.CancellationTokenSource.IsCancellationRequested)
                                await _notifications.NotifyAllAsync(SignalREvents.PrefillCacheChanged);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to record cached app {AppId}", appId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to complete prefill history for app {AppId}", appId);
                }

                progress.State = (isCached ? PrefillProgressState.AlreadyCached : PrefillProgressState.AppCompleted).ToWireString();
                progress.TotalBytes = totalBytes;
                progress.BytesDownloaded = bytesDownloaded;
                progress.PercentComplete = 100;
                progress.BytesPerSecond = 0;
            }

            lock (session.PrefillLock)
            {
                if (!IsSessionLive(session) || session.PrefillRunId != runId
                    || !session.IsPrefilling || session.TerminalCompletedFlag != 0)
                    return;
                session.LastProgress = progress;
                if (session.PrefillState == PrefillState.Started)
                    session.PrefillState = PrefillState.Downloading;
            }

            var sessionBroadcast = appCompleted ? Task.CompletedTask
                : NotifyHubAsync(EventSessionUpdated, DaemonSessionDto.FromSession(session));
            var subscriberBroadcast = BroadcastToSubscribersAsync(session, EventPrefillProgress,
                new { sessionId = session.Id, progress });
            try
            {
                await RaisePrefillProgressAsync(session, progress, sequence);
            }
            finally
            {
                await Task.WhenAll(sessionBroadcast, subscriberBroadcast);
            }
        }
        finally
        {
            session.PrefillWork.Release();
        }
    }

    internal static void UpdateTransferredBytes(DaemonSession session, long totalBytesTransferred)
    {
        session.TotalBytesTransferred = Math.Max(session.TotalBytesTransferred, totalBytesTransferred);

        // Advance the stall clock only when bytes increase. A zero-progress session must still be
        // detectable as stalled, including when repeated socket ticks carry the same byte count.
        if (session.TotalBytesTransferred > session.LastProgressBytes)
        {
            session.LastProgressBytes = session.TotalBytesTransferred;
            Volatile.Write(ref session.LastProgressTicksUtc, DateTime.UtcNow.Ticks);
        }
    }

    private async Task BroadcastHistoryUpdatedAsync(string sessionId, string appId, string status)
    {
        var historyEvent = new { sessionId, appId, status };
        // Narrowed from NotifyAllDownloadsAndServiceHubAsync → NotifyAllAsync (downloads hub only).
        // Only the admin Prefill Sessions page (which subscribes via the default downloads hub)
        // consumes this event. The session-owner clients connected to the service-specific daemon
        // hub (/hubs/steam-daemon, /hubs/epic-prefill-daemon) have no handler registered for it
        // and were logging `No client method with the name 'prefillhistoryupdated' found` on every fire.
        await _notifications.NotifyAllAsync(EventPrefillHistoryUpdated, historyEvent);
    }

    private async Task<List<PrefillRun>> LoadRunsAsync(DaemonSession session, CancellationToken cancellationToken)
    {
        var saved = await _sessionService.GetRunsAsync(session.Id, cancellationToken);
        foreach (var row in saved.Where(row => row.CompletedAtUtc is null
            || row.CompletedAtUtc > DateTime.UtcNow.AddHours(-24)).OrderByDescending(row => row.StartedAtUtc))
        {
            if (session.Runs.ContainsKey(row.Id)) continue;
            if (row.CompletedAtUtc.HasValue && session.Runs.Values.Count(run => run.TerminalCompletedFlag == 2) >= 256)
                continue;
            var snapshot = JsonSerializer.Deserialize<DaemonRunSnapshot>(row.SnapshotJson)
                ?? throw new JsonException("Persisted run snapshot is null.");
            var options = JsonSerializer.Deserialize<DaemonRunOptions>(row.OptionsJson)
                ?? throw new JsonException("Persisted run options are null.");
            var restored = new DaemonRun
            {
                PrefillRunId = row.Id,
                SessionId = session.Id,
                DaemonInstanceId = row.DaemonInstanceId,
                PrefillScheduleId = row.ScheduleId,
                ScheduleName = row.ScheduleName,
                NotificationMode = row.NotificationMode,
                ParentOperationId = row.ParentOperationId,
                Options = options,
                Snapshot = snapshot,
                CancelRequested = row.CancelRequested,
                CancelReason = row.CancelRequested && row.Reason is "stalled" or "auth-lost" ? row.Reason : null,
                HistoryIncomplete = row.HistoryIncomplete,
                CompletedAtUtc = row.CompletedAtUtc,
                Recovering = !row.CompletedAtUtc.HasValue,
                TerminalCompletedFlag = row.CompletedAtUtc.HasValue ? 2 : 0,
                LastProgressBytes = snapshot.BytesTransferred,
                LastProgressTicksUtc = snapshot.UpdatedAt.UtcTicks,
                PrefillState = row.State switch
                {
                    "completed" => PrefillState.Completed,
                    "cancelled" => PrefillState.Cancelled,
                    "failed" => PrefillState.Failed,
                    _ => PrefillState.Downloading
                }
            };
            session.Runs.TryAdd(row.Id, restored);
            if (restored.TerminalCompletedFlag == 2)
                restored.Completion.TrySetResult(DaemonSessionDto.FromRun(restored));
        }
        return saved;
    }

    public async Task RefreshRunsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session) || !IsSessionLive(session)) return;
        try
        {
            var status = await session.Client.GetStatusAsync(cancellationToken);
            if (status is null)
            {
                if (!session.Runs.IsEmpty) session.Recovering = true;
                return;
            }
            await RecoverRunsAsync(session, status, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            session.Recovering = true;
            session.NextRecoveryAtUtc = DateTime.UtcNow.AddSeconds(10);
            _logger.LogWarning(ex, "Could not reconcile prefill runs for session {SessionId}", sessionId);
        }
    }

    internal async Task RecoverRunsAsync(DaemonSession session, DaemonStatus status, CancellationToken cancellationToken)
    {
        if (!await session.RecoveryWork.WaitAsync(0, cancellationToken)) return;
        try
        {
            if (!IsSessionLive(session)) return;
            if (!status.SupportsConcurrentPrefill)
            {
                session.Capabilities = status;
                session.Recovering = session.Runs.Values.Any(run => run.TerminalCompletedFlag != 2);
                if (!session.Recovering) session.Runs.Clear();
                return;
            }
            session.Recovering = session.Recovering || session.Capabilities?.DaemonInstanceId != status.DaemonInstanceId;
            foreach (var expired in session.Runs.Values.Where(run => run.TerminalCompletedFlag == 2)
                .OrderByDescending(run => run.CompletedAtUtc).Select((run, index) => (run, index))
                .Where(entry => entry.index >= 256 || entry.run.CompletedAtUtc < DateTime.UtcNow.AddHours(-24)))
                session.Runs.TryRemove(expired.run.PrefillRunId, out _);
            var saved = await LoadRunsAsync(session, cancellationToken);
            session.Capabilities = status;
            var inventory = status.ActiveOperations!.Concat(status.RecentOperations!).ToArray();
            foreach (var snapshot in inventory)
            {
                if (snapshot.DaemonInstanceId != status.DaemonInstanceId
                    || !Guid.TryParse(snapshot.OperationId, out var id) || id == Guid.Empty)
                    throw new JsonException("Daemon operation inventory has an invalid identity.");
                if (session.Runs.ContainsKey(id) || saved.Any(row => row.Id == id)) continue;
                var page = await session.Client.GetOperationAsync(id, snapshot.DaemonInstanceId,
                    cancellationToken: cancellationToken);
                var adopted = new DaemonRun
                {
                    PrefillRunId = id,
                    SessionId = session.Id,
                    DaemonInstanceId = snapshot.DaemonInstanceId,
                    Options = page.Options,
                    Snapshot = snapshot with { Sequence = 0 },
                    Recovering = true
                };
                await _sessionService.CreateRunAsync(adopted, "recovered", cancellationToken);
                session.Runs.TryAdd(id, adopted);
            }
            foreach (var run in session.Runs.Values.Where(run => run.TerminalCompletedFlag == 0))
            {
                if (run.AdmissionPending) continue;
                if (run.DaemonInstanceId != status.DaemonInstanceId)
                {
                    await run.PrefillWork.WaitAsync(cancellationToken);
                    try
                    {
                        if (run.TerminalCompletedFlag != 0 || run.AdmissionPending) continue;
                        run.HistoryIncomplete = true;
                        await FinishRunAsync(session, run, run.Snapshot with
                        {
                            State = "failed",
                            Reason = "instance-changed",
                            Sequence = run.Snapshot.Sequence + 1,
                            UpdatedAt = DateTimeOffset.UtcNow
                        }, [], cancellationToken);
                    }
                    finally { run.PrefillWork.Release(); }
                    continue;
                }
                await ReconcileRunAsync(session, run, cancellationToken,
                    inventory.FirstOrDefault(snapshot => snapshot.OperationId == run.PrefillRunId.ToString()));
                if (run.CancelRequested && run.TerminalCompletedFlag == 0)
                    await session.Client.CancelPrefillAsync(run.PrefillRunId, run.DaemonInstanceId, cancellationToken);
            }
            session.Recovering = session.Runs.Values.Any(run => run.Recovering && run.TerminalCompletedFlag == 0);
            session.NextRecoveryAtUtc = DateTime.UtcNow.AddSeconds(10);
            await NotifyHubAsync(EventSessionUpdated, DaemonSessionDto.FromSession(session));
        }
        finally { session.RecoveryWork.Release(); }
    }

    private async Task ReconcileRunAsync(DaemonSession session, DaemonRun run, CancellationToken cancellationToken,
        DaemonRunSnapshot? retained = null)
    {
        // Busy callbacks are coalesced; the retained operation page supplies every item transition.
        if (!await run.PrefillWork.WaitAsync(0, cancellationToken)) return;
        try
        {
            if (!IsSessionLive(session) || run.TerminalCompletedFlag != 0) return;
            var items = new List<DaemonRunItem>();
            var offset = 0;
            DaemonRunSnapshot? snapshot = null;
            bool? firstTerminal = null;
            while (true)
            {
                var page = await session.Client.GetOperationAsync(run.PrefillRunId, run.DaemonInstanceId,
                    offset, cancellationToken: cancellationToken);
                if (page.Operation.OperationId != run.PrefillRunId.ToString()
                    || page.Operation.DaemonInstanceId != run.DaemonInstanceId || page.Items is null)
                    throw new JsonException("Operation page has a different run identity.");
                var terminal = page.Operation.State is "completed" or "failed" or "cancelled";
                firstTerminal ??= terminal;
                if (terminal && firstTerminal == false)
                {
                    items.Clear(); offset = 0; snapshot = null; firstTerminal = true;
                    continue;
                }
                if (snapshot is null || page.Operation.Sequence >= snapshot.Sequence) snapshot = page.Operation;
                items.AddRange(page.Items);
                if (page.NextOffset is null)
                {
                    if (terminal && items.Select(item => item.AppId).Distinct(StringComparer.Ordinal).Count() != page.TotalItems)
                        run.HistoryIncomplete = true;
                    break;
                }
                if (page.NextOffset <= offset || page.NextOffset > page.TotalItems)
                    throw new JsonException("Operation page does not advance its item offset.");
                offset = page.NextOffset.Value;
            }
            if (!IsSessionLive(session) || snapshot!.Sequence < run.Snapshot.Sequence) return;
            if (snapshot.State is "completed" or "failed" or "cancelled")
                await FinishRunAsync(session, run, snapshot, items, cancellationToken);
            else
            {
                var stored = await _sessionService.SaveRunAsync(run, snapshot, items, false, cancellationToken);
                if (!stored.Applied) return;
                ApplyRunSnapshot(run, snapshot, items);
                await PublishRunAsync(session, run, stored.Items, false);
            }
        }
        catch (DaemonCommandException ex) when (ex.ErrorCode == "operation-not-found")
        {
            run.HistoryIncomplete = true;
            if (retained is null)
            {
                var status = await session.Client.GetStatusAsync(cancellationToken);
                if (status?.DaemonInstanceId == run.DaemonInstanceId)
                    retained = (status.ActiveOperations ?? []).Concat(status.RecentOperations ?? [])
                        .FirstOrDefault(snapshot => snapshot.OperationId == run.PrefillRunId.ToString());
            }
            var outcome = retained is { State: "completed" or "failed" or "cancelled" } ? retained
                : run.Snapshot with
                {
                    State = "failed",
                    Reason = "outcome-unknown",
                    Sequence = run.Snapshot.Sequence + 1,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            await FinishRunAsync(session, run, outcome, [], cancellationToken);
        }
        finally { run.PrefillWork.Release(); }
    }

    private async Task FinishRunAsync(DaemonSession session, DaemonRun run, DaemonRunSnapshot snapshot,
        IReadOnlyList<DaemonRunItem> items, CancellationToken cancellationToken, bool containerStopped = false)
    {
        if (run.TerminalCompletedFlag != 0 || (!containerStopped && !IsSessionLive(session))) return;
        if (snapshot.State == "cancelled" && run.CancelReason is "stalled" or "auth-lost" or "runtime-exceeded")
            snapshot = snapshot with { State = "failed", Reason = run.CancelReason };
        if (snapshot.FailedApps > 0 && snapshot.State == "completed") snapshot = snapshot with { State = "failed" };
        var stored = await _sessionService.SaveRunAsync(run, snapshot, items, true, cancellationToken);
        if (!stored.Applied) return;
        run.TerminalCompletedFlag = 1;
        ApplyRunSnapshot(run, snapshot, items);
        run.CompletedAtUtc = snapshot.UpdatedAt.UtcDateTime;
        run.PrefillState = snapshot.State switch
        {
            "completed" => PrefillState.Completed,
            "cancelled" => PrefillState.Cancelled,
            _ => PrefillState.Failed
        };
        Volatile.Write(ref run.LastProgressTicksUtc, 0);
        try { await PublishRunAsync(session, run, stored.Items, true); }
        finally
        {
            run.TerminalCompletedFlag = 2;
            run.Completion.TrySetResult(DaemonSessionDto.FromRun(run));
        }
    }

    private static void ApplyRunSnapshot(DaemonRun run, DaemonRunSnapshot snapshot, IReadOnlyList<DaemonRunItem> items)
    {
        foreach (var item in items)
        {
            if (!run.Items.TryGetValue(item.AppId, out var previous) || item.Sequence > previous.Sequence)
                run.Items[item.AppId] = item;
        }
        var elapsed = (snapshot.UpdatedAt - run.Snapshot.UpdatedAt).TotalSeconds;
        var bytesPerSecond = elapsed > 0 ? Math.Max(0, snapshot.BytesTransferred - run.Snapshot.BytesTransferred) / elapsed : 0;
        run.Snapshot = snapshot;
        run.Recovering = false;
        if (snapshot.BytesTransferred > run.LastProgressBytes)
        {
            run.LastProgressBytes = snapshot.BytesTransferred;
            Volatile.Write(ref run.LastProgressTicksUtc, DateTime.UtcNow.Ticks);
        }
        var failure = snapshot.State == "failed" ? new DaemonCommandException(snapshot.Reason) : null;
        run.ErrorMessage = failure?.Message;
        run.ErrorStageKey = failure?.StageKey;
        if (snapshot.State == "failed" && snapshot.Reason == "stalled")
        {
            run.ErrorMessage = "The prefill stopped making progress.";
            run.ErrorStageKey = "signalr.scheduledPrefill.failedStalled";
        }
        if (snapshot.State == "failed" && snapshot.Reason == "runtime-exceeded")
        {
            run.ErrorMessage = "Exceeded maximum service runtime";
            run.ErrorStageKey = "signalr.scheduledPrefill.failedMaxRuntime";
        }
        run.LastProgress = new PrefillProgress
        {
            OperationId = snapshot.OperationId,
            DaemonInstanceId = snapshot.DaemonInstanceId,
            Sequence = snapshot.Sequence,
            State = snapshot.State,
            Reason = snapshot.Reason,
            CurrentAppId = snapshot.CurrentItem?.AppId,
            CurrentAppName = snapshot.CurrentItem?.Name,
            BytesDownloaded = snapshot.CurrentItem?.BytesTransferred ?? 0,
            PercentComplete = snapshot.CurrentItem?.TotalBytes is > 0
                ? Math.Min(100, 100d * snapshot.CurrentItem.BytesTransferred / snapshot.CurrentItem.TotalBytes.Value) : 0,
            BytesPerSecond = bytesPerSecond,
            TotalBytes = snapshot.CurrentItem?.TotalBytes ?? 0,
            TotalApps = snapshot.TotalApps,
            UpdatedApps = snapshot.CompletedApps,
            AlreadyUpToDate = snapshot.CachedApps,
            FailedApps = snapshot.FailedApps,
            CancelledApps = snapshot.CancelledApps,
            SkippedApps = snapshot.SkippedApps,
            TotalBytesTransferred = snapshot.BytesTransferred,
            UpdatedAt = snapshot.UpdatedAt.UtcDateTime,
            ElapsedSeconds = (snapshot.UpdatedAt - snapshot.StartedAt).TotalSeconds,
            TotalTimeSeconds = (snapshot.UpdatedAt - snapshot.StartedAt).TotalSeconds,
            ErrorMessage = run.ErrorMessage,
            ErrorCode = failure?.ErrorCode,
            RequiresLogin = failure?.RequiresLogin
        };
        if (run.TerminalCompletedFlag == 0) run.PrefillState = PrefillState.Downloading;
    }

    private async Task PublishRunAsync(DaemonSession session, DaemonRun run, IReadOnlyList<DaemonRunItem> changed, bool terminal)
    {
        foreach (var item in changed)
        {
            if (item.Result is "success" or "already_cached")
            {
                try
                {
                    var recorded = await _cacheService.RecordCachedAppAsync(Platform, item.AppId, item.Name,
                        item.TotalBytes ?? 0, session.AccountUsername);
                    if (Platform == PrefillPlatform.Steam && item.Depots is { Count: > 0 }
                        && uint.TryParse(item.AppId, out var appId))
                        recorded |= await _cacheService.RecordCachedDepotsAsync(appId, item.Name,
                            item.Depots.Select(depot => (depot.DepotId, depot.ManifestId, depot.TotalBytes)), session.AccountUsername);
                    if (recorded) await _notifications.NotifyAllAsync(SignalREvents.PrefillCacheChanged);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to record cached app {AppId} for run {RunId}", item.AppId, run.PrefillRunId);
                }
            }
        }
        if (!IsSessionLive(session)) return;
        await RaisePrefillProgressAsync(session, run.LastProgress!, run.Snapshot.Sequence);
        var progressEvent = new { sessionId = session.Id, progress = run.LastProgress };
        await BroadcastToSubscribersAsync(session, EventPrefillProgress, progressEvent);
        if (terminal)
        {
            var terminalEvent = new
            {
                sessionId = session.Id,
                operationId = run.PrefillRunId,
                daemonInstanceId = run.DaemonInstanceId,
                state = run.Snapshot.State,
                durationSeconds = (int)(run.Snapshot.UpdatedAt - run.Snapshot.StartedAt).TotalSeconds,
                run = DaemonSessionDto.FromRun(run)
            };
            await BroadcastToSubscribersAsync(session, EventPrefillStateChanged, terminalEvent);
            await NotifyHubAsync(EventPrefillStateChanged, terminalEvent).WaitAsync(TimeSpan.FromSeconds(5));
        }
        await NotifyHubAsync(EventSessionUpdated, DaemonSessionDto.FromSession(session)).WaitAsync(TimeSpan.FromSeconds(5));
        await ReportSessionActivityAsync(session, present: true);
    }

}
