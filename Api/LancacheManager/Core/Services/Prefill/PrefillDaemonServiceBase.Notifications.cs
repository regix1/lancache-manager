using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Models;

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
    private async Task BroadcastToSubscribersAsync(DaemonSession session, string eventName, object payload, bool removeOnError = true)
    {
        foreach (var connectionId in session.SubscribedConnections.ToList())
        {
            try
            {
                await SendToClientAsync(connectionId, eventName, payload);
            }
            catch (Exception ex)
            {
                if (removeOnError)
                {
                    _logger.LogWarning(ex, "Failed to notify {EventName} to {ConnectionId}, removing subscription", eventName, connectionId);
                    session.SubscribedConnections.Remove(connectionId);
                }
                // When removeOnError is false, silently ignore the error
            }
        }
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

    private async Task NotifySessionEndedAsync(DaemonSession session, string reason)
    {
        // NotifySessionEndedAsync does NOT remove connectionId on error (session is ending anyway)
        await BroadcastToSubscribersAsync(session, EventSessionEnded,
            new { sessionId = session.Id, reason }, removeOnError: false);

        // The session has ended; clear its presence/downloading dots and recompute this platform's
        // aggregate (the persistent container may have gone with it).
        await ReportSessionActivityAsync(session, present: false);
    }
}
