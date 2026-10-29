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
            // A challenge can still arrive from a daemon
            // client whose session was already terminated/replaced - the client read loop delivering a
            // queued "username" challenge right after Stop removed the session, just as a fresh
            // container becomes the service's active session. Writing it to PendingLoginChallenge or
            // broadcasting it would repopulate a dead session's state and let that stale challenge leak
            // toward the replacement (the "Received queued challenge for session <dead>" anomaly).
            // Ignore any challenge whose session is no longer the live, Active session under its id.
            if (!_sessions.TryGetValue(session.Id, out var liveSession)
                || !ReferenceEquals(liveSession, session)
                || session.Status != DaemonSessionStatus.Active
                || session.AdmissionClosed)
            {
                _logger.LogWarning(
                    "Ignoring credential challenge {ChallengeId} ({CredentialType}) for session {SessionId}: " +
                    "session is no longer the active live session (status {Status})",
                    challenge.ChallengeId, challenge.CredentialType, session.Id, session.Status);
                return;
            }

            lock (session.PrefillLock)
            {
                if (!IsSessionLive(session) || session.Status != DaemonSessionStatus.Active ||
                    session.AdmissionClosed || session.LoginCanceling ||
                    session.LoginCancelTask is not null ||
                    session.SuppressLoginChallengePublication ||
                    (challenge.LoginAttempt is { } attempt && attempt != session.LoginAttempt) ||
                    (!string.IsNullOrEmpty(session.LastConsumedLoginChallengeId) &&
                     string.Equals(challenge.ChallengeId, session.LastConsumedLoginChallengeId, StringComparison.Ordinal)))
                {
                    return;
                }
                session.LoginSettled = false;

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

                if (challenge.ExpiresAt != default)
                {
                    session.LoginExpiresAtUtc = challenge.ExpiresAt;
                }

                session.PendingLoginChallenge = challenge;
                challenge.OperationId = session.LoginOperationId?.ToString();
                challenge.LoginAttempt = session.LoginAttempt;
            }

            await NotifyCredentialChallengeAsync(session, challenge);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling socket credential challenge for session {SessionId}", session.Id);
        }
    }

    /// <summary>
    /// Applies a daemon status. A status read passes <paramref name="signOutCount"/>, the session's
    /// <see cref="DaemonSession.SignOutCount"/> taken before the read; a pushed status passes null.
    /// </summary>
    private async Task ApplyStatusAsync(DaemonSession session, DaemonStatus status, int? signOutCount)
    {
        try
        {
            // Post-detach/replace/terminate guard: daemon event callbacks are dispatched fire-and-forget,
            // so a queued status event can arrive after this session was detached at shutdown, replaced
            // (error-state), or terminated. Ignore it before any DB write / broadcast if it is no longer
            // the live registered instance for its id. Re-checked after each await below so a callback that
            // escaped the bounded teardown drain (took longer than the timeout) cannot resurrect teardown
            // state (mirrors the OnCredentialChallengeAsync guard above).
            if (!IsSessionLive(session) || session.AdmissionClosed)
            {
                return;
            }

            DaemonAuthState previousAuthState;
            DaemonAuthState newAuthState;
            var cancelMarkCleared = false;
            var witnessArmed = false;
            lock (session.PrefillLock)
            {
                if (!IsSessionLive(session) || session.AdmissionClosed)
                {
                    return;
                }
                // A signed-in answer read before a sign-out was applied is older than that sign-out.
                if (signOutCount is { } readFrom && status.Status == "logged-in" && session.SignOutCount != readFrom)
                {
                    return;
                }

                previousAuthState = session.AuthState;
                var reportedAuthState = status.Status switch
                {
                    "awaiting-login" => DaemonAuthState.NotAuthenticated,
                    "not-logged-in" when Platform.RequiresLogin() => DaemonAuthState.NotAuthenticated,
                    "logged-in" => DaemonAuthState.Authenticated,
                    _ => session.AuthState
                };

                // The stored mark is the keep, including before the in-memory set is filled. A logged-in
                // report must not sign the container in, count a sign-in, end the drop retry, settle the
                // login, or clear the mark. A report after this cancel has settled remembers that the
                // daemon reached signed-in, so a later connection drop can clear the mark and sign in once.
                // Message "Initialized" is the daemon's startup snapshot (GetStatus maps isInitialized to
                // that text). It is not a finished sign-in, so it never arms the drop for this cancel.
                // A sign-in the daemon finishes while this cancel is still running arms it too: a cancel
                // cannot undo a finished sign-in, so no drop code for this cancel follows it.
                var keepPersonCancel = session.LoginCanceledByPerson
                    && reportedAuthState == DaemonAuthState.Authenticated;
                if (keepPersonCancel)
                {
                    var initializedSnapshot = string.Equals(status.Message, "Initialized", StringComparison.Ordinal);
                    if (!session.LoggedInWhilePersonCanceled && !initializedSnapshot)
                    {
                        session.LoggedInWhilePersonCanceled = true;
                        witnessArmed = true;
                    }
                    newAuthState = DaemonAuthState.NotAuthenticated;
                    session.AuthState = newAuthState;
                }
                else
                {
                    newAuthState = reportedAuthState;
                    session.AuthState = newAuthState;
                    // Any sign-in ends the retry for the drop before it. A retry still waiting returns at its
                    // next wake, and the next drop starts its own retry with the first wait. Only a change into
                    // signed in is a new sign-in: a status read or a repeated report of a container already
                    // signed in is the same one.
                    if (newAuthState == DaemonAuthState.Authenticated)
                    {
                        if (previousAuthState != DaemonAuthState.Authenticated) session.SignInCount++;
                        session.SignInAgainAfterDrop = null;
                        cancelMarkCleared = session.LoginCanceledByPerson || session.LoggedInWhilePersonCanceled
                            || _loginStateRowsOutOfDate.ContainsKey(session.Id);
                        session.LoginCanceledByPerson = false;
                        session.LoggedInWhilePersonCanceled = false;
                    }
                    else if (previousAuthState == DaemonAuthState.Authenticated)
                    {
                        session.SignOutCount++;
                    }

                    // The daemon reports a refused sign-in as "awaiting-login" with a "Login failed:" message.
                    // Record it before the auth-state change below ends the tracked sign-in, so that sign-in
                    // ends as a failure rather than a cancel.
                    if (newAuthState == DaemonAuthState.NotAuthenticated && TryGetLoginFailureMessage(status, out var failureMessage))
                    {
                        session.LastLoginFailureMessage = failureMessage;
                        // A refused sign-in answers no prompt it left open, so the next start begins a new sign-in
                        // instead of resuming this one.
                        ClearPendingLoginChallenge(session);
                    }

                    if (newAuthState == DaemonAuthState.Authenticated
                        && session.LoginOperationId is null
                        && !session.SuppressLoginChallengePublication)
                    {
                        session.LoginSettled = true;
                    }
                    else if (newAuthState == DaemonAuthState.NotAuthenticated
                        && session.LoginOperationId is null
                        && session.PendingLoginChallenge is null
                        && !session.SuppressLoginChallengePublication)
                    {
                        session.LoginSettled = true;
                    }
                }

                // A drop after the daemon had reached signed-in is not the drop already in flight for this
                // cancel. Clear the mark so the one retry below can start.
                if (!keepPersonCancel
                    && newAuthState == DaemonAuthState.NotAuthenticated
                    && status.ErrorCode == "auth-lost"
                    && session.IsPersistent
                    && session.LoginCanceledByPerson
                    && session.LoggedInWhilePersonCanceled)
                {
                    session.LoggedInWhilePersonCanceled = false;
                    session.LoginCanceledByPerson = false;
                    cancelMarkCleared = true;
                }
            }

            // A write that fails keeps memory as the truth: the daemon did reach logged-in, and a drop still
            // starts its retry below.
            if (cancelMarkCleared || witnessArmed)
            {
                await SaveLoginStateAsync(session);
            }

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
                        await NotifyHubAsync(session, EventSessionUpdated, dto);
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

            // Status reads carry the daemon's drop code too, so whichever report lands first starts
            // the one retry for this drop, including the first status read after a manager restart.
            if (newAuthState == DaemonAuthState.NotAuthenticated && status.ErrorCode == "auth-lost" && session.IsPersistent)
            {
                StartSignInAgain(session);
            }

            // A log-off reports a signed-in container signed out without the drop code while the daemon keeps the
            // saved login (Steam as awaiting-login, Epic and Xbox through the status read as not logged in), so the
            // stored mark keeps a restart or a recreate from signing it in again. A drop code, also one that follows
            // that report, says the connection dropped instead.
            if (newAuthState == DaemonAuthState.NotAuthenticated && session.IsPersistent)
            {
                bool signedOut;
                bool signedOutChanged;
                lock (session.PrefillLock)
                {
                    signedOut = status.ErrorCode != "auth-lost"
                        && (session.SignedOut
                            || (previousAuthState == DaemonAuthState.Authenticated && status.Status != "logged-in"));
                    signedOutChanged = signedOut != session.SignedOut;
                    session.SignedOut = signedOut;
                }
                if (signedOutChanged)
                {
                    await SaveLoginStateAsync(session);
                }
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
            if (IsSessionLive(session) && Guid.TryParse(socketProgress.OperationId, out var id)
                && session.Runs.TryGetValue(id, out var run)
                && run.DaemonInstanceId == socketProgress.DaemonInstanceId
                && socketProgress.Sequence > run.Snapshot.Sequence && run.TerminalCompletedFlag == 0)
                await ReconcileRunAsync(session, run, session.CancellationTokenSource.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling socket progress for session {SessionId}", session.Id);
        }
    }

    /// <summary>
    /// Handles the daemon's socket or TCP connection dropping.
    /// </summary>
    private async Task OnDisconnectedAsync(DaemonSession session, IDaemonClient daemonClient)
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
            await NotifyHubAsync(session, EventSessionUpdated, DaemonSessionDto.FromSession(session));
            FireAndForgetAsync(() => RefreshRunsAsync(session.Id), nameof(RefreshRunsAsync));
            return;
        }
        lock (session.PrefillLock)
        {
            if (!IsSessionLive(session) || !ReferenceEquals(session.Client, daemonClient)
                || session.Status is DaemonSessionStatus.Error or DaemonSessionStatus.Terminated
                || session.CancellationTokenSource.IsCancellationRequested)
                return;
            session.Status = DaemonSessionStatus.Error;
        }
        _logger.LogWarning("Socket disconnected for session {SessionId}", session.Id);
        try
        {
            var failure = new DaemonCommandException();
            // A sign-in waiting at a prompt has no daemon left to answer it, and neither expiry sweep looks
            // at an Error session, so the disconnect ends it here as a failure.
            if (session.LoginOperationId is not null)
            {
                session.LastLoginFailureMessage = failure.Message;
                session.LastLoginFailureKey = failure.StageKey;
                CompleteLoginOperation(session);
            }
            DaemonSessionDto snapshot;
            lock (session.PrefillLock)
            {
                if (!IsSessionLive(session) || !ReferenceEquals(session.Client, daemonClient))
                    return;
                snapshot = DaemonSessionDto.FromSession(session);
            }
            await NotifyHubAsync(session, EventSessionUpdated, snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify the socket disconnect for session {SessionId}", session.Id);
        }
    }

    #endregion

    /// <summary>
    /// Broadcasts a payload to all subscribed connections for a session. A send that fails or outlasts its 5-second bound
    /// removes that connection from the session's subscriptions: a browser that is slow or gone would otherwise hold every
    /// later update and the run's ending behind it. Its dialog reads how the sign-in ended when it subscribes again.
    /// </summary>
    private async Task BroadcastToSubscribersAsync(DaemonSession session, string eventName, object payload)
    {
        string[] connections;
        long subscribeCount;
        lock (session.PrefillLock)
        {
            connections = session.SubscribedConnections.ToArray();
            subscribeCount = session.SubscribeCount;
        }
        await Task.WhenAll(connections.Select(async connectionId =>
        {
            try
            {
                await SendToClientAsync(connectionId, eventName, payload).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                lock (session.PrefillLock)
                {
                    // This connection subscribed again after the snapshot, so its tab is live and keeps its subscription.
                    if (session.LastSubscribeCounts.TryGetValue(connectionId, out var lastSubscribe) && lastSubscribe > subscribeCount)
                        return;
                    session.SubscribedConnections.Remove(connectionId);
                    session.LastSubscribeCounts.Remove(connectionId);
                }
                _logger.LogWarning(ex, "Failed to notify {EventName} to {ConnectionId}, removing subscription", eventName, connectionId);
            }
        }));
    }

    protected async Task NotifyAuthStateChangeAsync(DaemonSession session)
    {
        if (!IsSessionLive(session) || session.AdmissionClosed)
        {
            return;
        }

        var authState = session.AuthState;
        // Read with the state, before any await: the attempt this state belongs to, which a new start may move on.
        var loginAttempt = session.LoginAttempt;
        // Only a person's Log in registers a login operation; the automatic sign-ins never do.
        var personLogin = session.LoginOperationId is not null;
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
            if (!session.PreserveLoginExpiry)
            {
                session.NeedsRelogin = false;
            }
            ClearPendingLoginChallenge(session);
            // A tracked sign-in that ended signed in is settled the same way an untracked status push settles one;
            // nothing else marks it, and a persistent container's image update waits for it.
            lock (session.PrefillLock)
            {
                if (!session.SuppressLoginChallengePublication)
                {
                    session.LoginSettled = true;
                }
            }

            // A person's finished Log in starts a new re-login window from now, stored so a restart reads it.
            // The automatic sign-ins keep the stored sign-in time and date.
            if (personLogin && session.IsPersistent)
            {
                var lastLoginAt = DateTime.UtcNow;
                session.LastLoginAt = lastLoginAt;
                session.ExpiresAt = ComputePersistentExpiry(lastLoginAt, _stateService.GetAdminPersistentLoginValidityDays());
                session.NeedsRelogin = false;
                await SaveLoginStateAsync(session);
            }
        }

        // The auth transition changes this platform's persistent-container/integration aggregate (and, for
        // an anonymous daemon, its connected state), so refresh the session's activity presence here.
        await ReportSessionActivityAsync(session, present: true);

        if (!IsSessionLive(session) || session.AuthState != authState)
            return;

        var payload = new { sessionId = session.Id, authState = authState.ToString(), loginAttempt };
        await BroadcastToSubscribersAsync(session, EventAuthStateChanged, payload);

        // Liveness fence: the subscriber fan-out above can outlast a teardown that won the bounded drain;
        // stop before mirroring later auth/session updates to the account holders for a session no longer live.
        if (!IsSessionLive(session) || session.AuthState != authState)
        {
            return;
        }

        // Mirror to the account holders so management UIs (persistent container list, prefill sessions)
        // update via SignalR instead of polling when a daemon self-authenticates or logs in.
        await _notifications.NotifyAdminAsync(EventAuthStateChanged, payload);

        // Re-check after the hub push before the session-update broadcast: teardown may have won during it.
        if (!IsSessionLive(session) || session.AuthState != authState)
        {
            return;
        }

        try
        {
            var dto = DaemonSessionDto.FromSession(session);
            await NotifyHubAsync(session, EventSessionUpdated, dto);
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
        lock (session.PrefillLock)
        {
            if (!IsSessionLive(session) || session.Status != DaemonSessionStatus.Active ||
                session.AdmissionClosed || session.LoginCanceling ||
                session.LoginCancelTask is not null ||
                session.SuppressLoginChallengePublication ||
                challenge.LoginAttempt != session.LoginAttempt ||
                !ReferenceEquals(session.PendingLoginChallenge, challenge))
                return;
        }

        await BroadcastToSubscribersAsync(session, EventCredentialChallenge,
            new { sessionId = session.Id, challenge });

        // Liveness fence: the subscriber fan-out above can outlast a teardown that won the bounded drain;
        // don't mirror the challenge to the account holders for a session that is no longer the live instance.
        lock (session.PrefillLock)
        {
            if (!IsSessionLive(session) || session.Status != DaemonSessionStatus.Active ||
                session.AdmissionClosed || session.LoginCanceling ||
                session.LoginCancelTask is not null ||
                session.SuppressLoginChallengePublication ||
                challenge.LoginAttempt != session.LoginAttempt ||
                !ReferenceEquals(session.PendingLoginChallenge, challenge))
                return;
        }

        // Mirror to the account holders (matches NotifyAuthStateChangeAsync's AuthStateChanged mirror
        // above) so the persistent-container config modal - which never calls SubscribeToSessionAsync,
        // unlike the mapping-flow live login - receives the challenge the instant the daemon emits it
        // instead of waiting on the REST challenge poll. Only account holders get this copy: a challenge
        // carries sign-in codes, so no other browser may receive it.
        await _notifications.NotifyAdminAsync(EventCredentialChallenge, new { sessionId = session.Id, challenge });
    }

    private async Task NotifyStatusChangeAsync(DaemonSession session, DaemonStatus status)
    {
        await BroadcastToSubscribersAsync(session, EventStatusChanged,
            new { sessionId = session.Id, status });
    }

    private async Task BroadcastHistoryUpdatedAsync(string sessionId)
    {
        var historyEvent = new { sessionId };
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
                CancelReason = row.CancelRequested && row.Reason is "stalled" or "auth-lost" or "runtime-exceeded"
                    ? row.Reason : null,
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
        // This read comes after every read a pass skipped before it, so it answers their request. A read that
        // gets no answer puts the request back, and the request is written before the recovering flag so a
        // holder that clears the flag afterwards still finds the request when it releases the lease.
        var requested = session.RecoveryRequested;
        session.RecoveryRequested = false;
        try
        {
            var signInCount = session.SignInCount;
            var signOutCount = session.SignOutCount;
            var status = await session.Client.GetStatusAsync(cancellationToken);
            if (status is null)
            {
                if (requested) session.RecoveryRequested = true;
                // The capabilities a skipped pass did not apply may name the old daemon, so no run is sent until
                // a read answers.
                if (!session.Runs.IsEmpty || requested) session.Recovering = true;
                session.NextRecoveryAtUtc = DateTime.UtcNow.AddSeconds(10);
                await SendRecoveryUpdateAsync(session);
                return;
            }
            // A drop pushed while the socket to the daemon was down reaches the app only through a read.
            // Only an always-on Steam session acts on it; applying it to other sessions would send them a
            // status message every sweep. A running automatic sign-in applies its own outcome when it ends,
            // and a signed-out answer read before a sign-in finished is older than that sign-in.
            if (session.IsPersistent
                && Platform == PrefillPlatform.Steam
                && (session.LoginOperationId is null || status.Status == "logged-in")
                && !session.SigningInAutomatically
                && (status.Status == "logged-in" || session.SignInCount == signInCount))
            {
                await ApplyStatusAsync(session, status, signOutCount);
            }
            await RecoverRunsAsync(session, status, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (requested) session.RecoveryRequested = true;
            throw;
        }
        catch (Exception ex)
        {
            if (requested) session.RecoveryRequested = true;
            session.Recovering = true;
            session.NextRecoveryAtUtc = DateTime.UtcNow.AddSeconds(10);
            _logger.LogWarning(ex, "Could not reconcile prefill runs for session {SessionId}", sessionId);
            await SendRecoveryUpdateAsync(session);
        }
    }

    /// <summary>
    /// Drops the runs whose rows the Prefill Sessions reset deleted from every session, so the card stops
    /// listing runs whose history is gone. A run that finished after the delete kept its row and stays. The
    /// ids decide, not the finished flag: a run deleted while it was still publishing its finish is gone too.
    /// Holding the recovery lease keeps a sweep that read the rows before the delete from putting them back.
    /// </summary>
    internal async Task ForgetFinishedRunsAsync(IReadOnlyCollection<Guid> deletedRunIds)
    {
        foreach (var session in _sessions.Values)
        {
            await session.RecoveryWork.WaitAsync();
            try
            {
                var removed = false;
                foreach (var runId in deletedRunIds)
                    removed |= session.Runs.TryRemove(runId, out _);
                // An idle container sends no update of its own, so open pages learn of the removal only here.
                if (removed)
                    await NotifyHubAsync(session, EventSessionUpdated, DaemonSessionDto.FromSession(session));
            }
            finally { session.RecoveryWork.Release(); }
        }
    }

    internal async Task RecoverRunsAsync(DaemonSession session, DaemonStatus status, CancellationToken cancellationToken)
    {
        if (!await session.RecoveryWork.WaitAsync(0, cancellationToken))
        {
            // The holder may have read the daemon before this read did, as when the container restarted in
            // place meanwhile, so a pass with a fresh read follows it. A holder that released before the request
            // was written may already have checked for one, so this pass tries the lease once more and applies its
            // own read. A recovery pass still holding it finds the request when it releases; a Prefill Sessions reset,
            // an image replace, or the first Start's wait for the lease (PrefillAsync) holds it without a pass and
            // leaves the request to the sweep (SessionExpiry.cs).
            session.RecoveryRequested = true;
            if (!await session.RecoveryWork.WaitAsync(0, cancellationToken)) return;
        }
        try
        {
            if (!IsSessionLive(session)) return;
            // A pass that waited for the lease may hold a read taken before the one the last holder applied, as
            // when the container restarted in place meanwhile; applying it would point the session back at the old
            // daemon instance. The newer read already ran this pass's work.
            if (session.Capabilities is { } applied && status.ReadStamp < applied.ReadStamp) return;
            if (!status.SupportsConcurrentPrefill)
            {
                session.Capabilities = status;
                lock (session.PrefillLock)
                {
                    // A drop after this pass's status read already marked the session recovering. A client that
                    // has reconnected since lets this older read end it; the drop's own pass, which found this
                    // pass holding the lease, then runs again after it with a fresh read.
                    if (session.Client is not DaemonClientBase { IsConnected: false })
                        session.Recovering = session.Runs.Values.Any(run => run.TerminalCompletedFlag != 2);
                }
                if (!session.Recovering) session.Runs.Clear();
                return;
            }
            lock (session.PrefillLock)
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
                // A run's row is saved before the run is sent, so a finished operation with no row is one whose
                // row was deleted, and adopting it would write back history a reset removed.
                if (snapshot.State is "completed" or "failed" or "cancelled") continue;
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
                // A bad answer about one run leaves the others to this pass. A lost connection or an unanswered
                // read still ends the pass, which keeps the session recovering.
                try
                {
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
                catch (Exception ex) when (ex is JsonException or DaemonCommandException)
                {
                    _logger.LogWarning(ex, "Could not reconcile prefill run {RunId} for session {SessionId}", run.PrefillRunId, session.Id);
                }
            }
            lock (session.PrefillLock)
            {
                // A drop after this pass's status read already marked the session recovering.
                if (session.Client is not DaemonClientBase { IsConnected: false })
                    session.Recovering = session.Runs.Values.Any(run => run.Recovering && run.TerminalCompletedFlag == 0);
            }
            session.NextRecoveryAtUtc = DateTime.UtcNow.AddSeconds(10);
            await SendRecoveryUpdateAsync(session);
        }
        finally
        {
            session.RecoveryWork.Release();
            if (session.RecoveryRequested)
                FireAndForgetAsync(() => RefreshRunsAsync(session.Id), nameof(RefreshRunsAsync));
        }
    }

    /// <summary>
    /// A container list read runs a recovery pass and the page reads the list again on this update, so the pass
    /// sends only what differs from the last update any path sent. A change another path made without
    /// sending its own update still differs from that, and a recovering flag written and cleared again
    /// without one leaves no record, so pages that read it meanwhile get this update.
    /// </summary>
    private async Task SendRecoveryUpdateAsync(DaemonSession session)
    {
        var update = DaemonSessionDto.FromSession(session);
        if (JsonSerializer.Serialize(update) != session.LastRecoveryUpdate)
            await NotifyHubAsync(session, EventSessionUpdated, update);
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
        // The lists count a run until it gives up its slot, so an update sent during the publish still counted it.
        if (IsSessionLive(session))
            await SendRunSessionUpdateAsync(session, run.PrefillRunId);
    }

    private void ApplyRunSnapshot(DaemonRun run, DaemonRunSnapshot snapshot, IReadOnlyList<DaemonRunItem> items)
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
        var failure = snapshot.State == "failed" ? new DaemonCommandException(snapshot.Reason, loginStageKey: SignInLostStageKey) : null;
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

    private async Task PublishRunAsync(DaemonSession session, DaemonRun run, List<DaemonRunItem> changed, bool terminal)
    {
        foreach (var item in changed)
        {
            if (item.Result == "success")
            {
                try
                {
                    var recorded = await RecordCacheAsync(item.AppId, item.Name, item.TotalBytes ?? 0,
                        session.AccountUsername, item.CacheRevision, item.Depots);
                    if (recorded) await _notifications.NotifyAllAsync(SignalREvents.PrefillCacheChanged);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to record cached app {AppId} for run {RunId}", item.AppId, run.PrefillRunId);
                }
            }
        }
        if (terminal && run.Items.Values.Any(item => item.Result == "success"))
            FireAndForgetAsync(OnRunDownloadedAsync, nameof(OnRunDownloadedAsync));
        var progressEvent = new { sessionId = session.Id, progress = run.LastProgress };
        if (!IsSessionLive(session))
        {
            // A Stop ends its runs after the session left the live list, and a Stop whose container removal fails
            // sends no SessionEnded, so the open pages learn each run's end here. A failed send keeps its
            // subscription: the SessionEnded of a retried Stop still has to reach that page.
            if (!terminal) return;
            string[] subscribers;
            lock (session.PrefillLock) subscribers = session.SubscribedConnections.ToArray();
            foreach (var connectionId in subscribers)
            {
                try
                {
                    await SendToClientAsync(connectionId, EventPrefillProgress, progressEvent).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send the end of prefill {RunId} to {ConnectionId}", run.PrefillRunId, connectionId);
                }
            }
            return;
        }
        await RaisePrefillProgressAsync(session, run.LastProgress!, run.Snapshot.Sequence);
        await BroadcastToSubscribersAsync(session, EventPrefillProgress, progressEvent);
        // The admin Prefill Sessions list re-reads this session's games only when a save changed one of them.
        if (terminal || changed.Count > 0)
            await BroadcastHistoryUpdatedAsync(session.Id);
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
            // The run is already saved, so a notice that outlasts its bound is logged and does not fail the run.
            try
            {
                await _notifications.NotifyAdminAsync(EventPrefillStateChanged, terminalEvent).WaitAsync(TimeSpan.FromSeconds(5), CacheStatusClock);
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning(ex, "The ending of prefill {RunId} is still being sent to the account holders", run.PrefillRunId);
            }
        }
        // A run's end sends its session update from FinishRunAsync, once the run no longer holds its slot.
        if (!terminal)
            await SendRunSessionUpdateAsync(session, run.PrefillRunId);
        await ReportSessionActivityAsync(session, present: true);
    }

    private async Task SendRunSessionUpdateAsync(DaemonSession session, Guid runId)
    {
        try
        {
            await NotifyHubAsync(session, EventSessionUpdated, DaemonSessionDto.FromSession(session)).WaitAsync(TimeSpan.FromSeconds(5), CacheStatusClock);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "The session update for prefill {RunId} is still waiting to be sent", runId);
        }
    }

    private Task<bool> RecordCacheAsync(
        string appId,
        string? appName,
        long totalBytes,
        string? cachedBy,
        string? cacheRevision,
        IReadOnlyList<DepotManifestProgressInfo>? depots)
    {
        if (Platform != PrefillPlatform.Steam)
            return _cacheService.RecordCachedAppAsync(Platform, appId, appName, totalBytes, cachedBy, cacheRevision);
        return long.TryParse(appId, out var numericAppId)
            ? _cacheService.RecordSteamCacheAsync(numericAppId, appName, totalBytes, cachedBy, depots)
            : Task.FromResult(false);
    }

}
