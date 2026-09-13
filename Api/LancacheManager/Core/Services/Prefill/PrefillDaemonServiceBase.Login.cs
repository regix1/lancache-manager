using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using LancacheManager.Models;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Middleware;

namespace LancacheManager.Core.Services;

public abstract partial class PrefillDaemonServiceBase
{
    /// <summary>
    /// The most recent background headless self-auth attempt fired after an involuntary
    /// FullPersistence recreate, or null when none has been triggered this service lifetime. The
    /// task never faults (it is wrapped by <see cref="InvokeSafeAsync"/>). Internal so tests can
    /// await the fire-and-forget attempt deterministically instead of polling.
    /// </summary>
    internal Task? LastHeadlessSelfAuthAttempt { get; private set; }

    /// <summary>
    /// Headless self-auth for a fresh persistent session whose named auth volume still holds a
    /// previous life's login (the fresh-login guard preserved it): the daemon only reads that stored
    /// login when it receives a <c>login</c> command, so issue one on the admin's behalf. The whole
    /// transaction - the live-status preflight, the login, and any silent cancellation - runs while
    /// OWNING <see cref="DaemonSession.LoginLock"/>, so a concurrent interactive login either wins
    /// the try-acquire before this attempt begins or starts only after this attempt's cleanup
    /// finished; it can never observe or inherit this attempt's challenge. While the login runs,
    /// <see cref="DaemonSession.SuppressLoginChallengePublication"/> keeps a daemon challenge from
    /// being published (state write + broadcast) by the transport event handler - this attempt
    /// consumes it from the command return channel and silently cancels it. Outcomes: an
    /// authenticated daemon flips through the existing auth-state flows; a challenge or a
    /// no-response is cancelled daemon-side, and only a CONFIRMED cancel presents the ordinary
    /// needs-login state (an unconfirmed cancel marks the session errored - see
    /// <see cref="CancelHeadlessLoginAsync"/>). Never pushes a modal, notification, or challenge
    /// broadcast. Runs fire-and-forget in the background (never blocks or fails startup); any
    /// exception is observed and logged by the <see cref="InvokeSafeAsync"/> wrapper.
    /// </summary>
    internal async Task AttemptHeadlessPersistentSelfAuthAsync(DaemonSession session)
    {
        // The session was torn down (shutdown/terminate) between the trigger and this task running.
        if (!IsSessionLive(session))
        {
            return;
        }

        var cancellationToken = session.CancellationTokenSource.Token;

        // Try-acquire like the interactive entry point, but bail silently instead of throwing: a
        // held lock means an interactive login is already in flight - the user got there first and
        // this attempt is unnecessary.
        if (!await session.LoginLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        var abandonedLoginCleanup = new AbandonedLoginCleanupHolder();
        try
        {
            // A cached challenge means an interactive login is mid-flow (paused/reopened modal);
            // never cancel it out from under the user.
            if (session.PendingLoginChallenge is not null)
            {
                return;
            }

            // One live-status preflight, applied through the same handler every socket status push
            // uses, so an already-authenticated daemon (anonymous services, or a login-required
            // daemon that authenticated earlier) reconciles to Authenticated and broadcasts through
            // the existing flows instead of being skipped silently.
            var preflight = await session.Client.GetStatusAsync(cancellationToken);
            if (preflight?.Status == "logged-in")
            {
                await OnStatusChangeAsync(session, preflight);
                return;
            }

            session.SuppressLoginChallengePublication = true;
            try
            {
                var result = await StartLoginCoreAsync(
                    session,
                    session.Id,
                    timeout: null,
                    abandonedLoginCleanup,
                    onCommandDispatched: null,
                    cancellationToken);
                switch (result.Outcome)
                {
                    case LoginAttemptOutcome.Authenticated:
                        // The daemon self-authenticated from its stored volume login; the login flow
                        // already flipped auth state and broadcast through the existing flows.
                        return;

                    case LoginAttemptOutcome.Failed:
                        // The daemon itself failed the attempt (fail-fast path): auth state was
                        // already reset to NotAuthenticated and notified, no login is left in
                        // flight, and the failure was already logged with the daemon's own text.
                        return;

                    default:
                        // ChallengeIssued: the stored login is gone/invalid and the daemon wants
                        // credentials nobody is present to type. NoResponse: the daemon neither
                        // authenticated nor challenged within the bounded waits, and may still be
                        // processing the login command; it must not be left visibly LoggingIn.
                        // Both need a confirmed daemon-side cancellation.
                        await CancelHeadlessLoginAsync(session, result, cancellationToken);
                        return;
                }
            }
            finally
            {
                session.SuppressLoginChallengePublication = false;
            }
        }
        finally
        {
            ReleaseLoginLockAfterCleanup(session, abandonedLoginCleanup);
        }
    }

    /// <summary>
    /// Silently cancels a headless login attempt that ended in a challenge or in no response, while
    /// the caller still owns <see cref="DaemonSession.LoginLock"/>. Only a cancel the daemon
    /// ACKNOWLEDGED may present the ordinary needs-login state - the daemon answers a repeat
    /// <c>login</c> command for an attempt it still considers in flight with "already in progress"
    /// WITHOUT re-emitting a challenge, so claiming needs-login after an unconfirmed cancel would
    /// wedge the admin's next interactive attempt. An unconfirmed cancel instead marks the session
    /// errored (the honest state: this daemon needs replacing) so the next start replaces it through
    /// the existing errored-session replacement path, which preserves a FullPersistence volume login.
    /// </summary>
    private async Task CancelHeadlessLoginAsync(DaemonSession session, LoginAttemptResult result, CancellationToken cancellationToken)
    {
        // Publication was suppressed, so nothing should be cached - clear defensively anyway, plus
        // the client's own queued copy, before the daemon round-trip.
        ClearPendingLoginChallenge(session);
        session.Client.ClearPendingChallenges();

        bool cancelConfirmed;
        try
        {
            cancelConfirmed = await session.Client.CancelLoginWithOutcomeAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "cancel-login round-trip failed while cleaning up a headless login attempt for session {SessionId}",
                session.Id);
            cancelConfirmed = false;
        }

        if (cancelConfirmed)
        {
            session.AuthState = DaemonAuthState.NotAuthenticated;
            await NotifyAuthStateChangeAsync(session);

            if (result.Outcome == LoginAttemptOutcome.ChallengeIssued)
            {
                _logger.LogWarning(
                    "Headless self-auth for persistent {ServiceName} session {SessionId} found no usable stored login (daemon answered with a {CredentialType} challenge); attempt cancelled - awaiting interactive login",
                    ServiceName, session.Id, result.Challenge!.CredentialType);
            }
            else
            {
                _logger.LogWarning(
                    "Headless self-auth for persistent {ServiceName} session {SessionId} got no response from the daemon (neither authenticated nor challenged); attempt cancelled - awaiting interactive login",
                    ServiceName, session.Id);
            }

            return;
        }

        _logger.LogError(
            "Headless self-auth for persistent {ServiceName} session {SessionId} could not confirm daemon-side login cancellation; marking the session errored so the next start replaces the daemon cleanly",
            ServiceName, session.Id);
        session.Status = DaemonSessionStatus.Error;
        session.ErrorMessage = "Automatic sign-in could not be cancelled cleanly. Start the session again to recover.";
        try
        {
            await NotifyHubAsync(EventSessionUpdated, DaemonSessionDto.FromSession(session));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to broadcast session update after headless cancel recovery for session {SessionId}", session.Id);
        }
    }

    /// <summary>
    /// Gets the current status of a daemon session
    /// </summary>
    public async Task<DaemonStatus?> GetSessionStatusAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return null;
        }

        var status = await session.Client.GetStatusAsync(cancellationToken);
        if (status?.SupportsConcurrentPrefill == true || !session.Runs.IsEmpty)
        {
            if (status is not null)
            {
                await OnStatusChangeAsync(session, status);
                await RecoverRunsAsync(session, status, cancellationToken);
            }
            else session.Recovering = true;
        }
        return status;
    }

    /// <summary>
    /// Starts the login process for a daemon session.
    /// Returns a credential challenge if credentials are needed.
    /// </summary>
    public Task<CredentialChallenge?> StartLoginAsync(
        string sessionId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => StartLoginEntryAsync(
            sessionId,
            timeout,
            accountId: null,
            onCommandDispatched: null,
            reuseIntegration: false,
            cancellationToken);

    /// <summary>
    /// Edit-session login entry point that reports the exact point a fresh daemon login command has
    /// been dispatched. Cached challenge resumes do not invoke the callback because they start no new
    /// daemon work.
    /// </summary>
    internal Task<CredentialChallenge?> StartLoginForEditAsync(
        string sessionId,
        TimeSpan? timeout,
        Action onCommandDispatched,
        CancellationToken cancellationToken = default)
    {
        return StartLoginEntryAsync(
            sessionId,
            timeout,
            accountId: null,
            onCommandDispatched,
            reuseIntegration: false,
            cancellationToken);
    }

    /// <summary>
    /// Edit-session entry point for importing the server-side integration login into the exact
    /// persistent daemon session.
    /// </summary>
    internal Task<CredentialChallenge?> ReuseIntegrationLoginForEditAsync(
        string sessionId,
        Guid accountId,
        Action onCommandDispatched,
        CancellationToken cancellationToken = default)
    {
        return StartLoginEntryAsync(
            sessionId,
            timeout: null,
            accountId,
            onCommandDispatched,
            reuseIntegration: true,
            cancellationToken);
    }

    /// <summary>
    /// Reports whether this platform can currently prepare a server-side integration login.
    /// Returned values are safe for the browser and never contain credentials.
    /// </summary>
    public virtual IntegrationLoginAvailability GetIntegrationLoginAvailability(Guid? accountId)
        => new(false, null, "not-supported");

    /// <summary>
    /// Imports this platform's integration login into the exact session client.
    /// </summary>
    protected virtual Task<bool> ReuseIntegrationLoginAsync(
        DaemonSession session,
        Guid accountId,
        Action onCommandDispatched,
        CancellationToken cancellationToken)
        => Task.FromResult(false);

    /// <summary>
    /// Rejects a login handoff when the session was replaced while credentials were being prepared.
    /// </summary>
    protected void EnsureCurrentSession(DaemonSession session)
    {
        if (!_sessions.TryGetValue(session.Id, out var current) || !ReferenceEquals(current, session))
        {
            throw new ConflictException($"Persistent session {session.Id} was replaced.")
            {
                StageKey = "errors.prefill.sessionReplaced",
                Context = new() { ["sessionId"] = session.Id }
            };
        }
    }

    /// <summary>
    /// Raises the tracked operation behind a login's notification card, so the card carries a real id
    /// and its X ends up in <see cref="CancelLoginAsync"/> rather than in a second cancel of its own.
    /// Lives here, above <see cref="StartLoginCoreAsync"/>, because the headless self-auth path enters
    /// the core directly: it runs with nobody present, so it must raise no card. The suppression flag is
    /// checked as well, since it is what marks a headless attempt while it owns this session's login.
    /// A resumed login (the modal closed and reopened, so the core answers with the cached challenge)
    /// keeps the card it already has instead of raising a second one for the same attempt.
    /// </summary>
    private void RegisterLoginOperation(DaemonSession session)
    {
        if (_operationTracker is null ||
            session.LoginOperationId is not null ||
            session.SuppressLoginChallengePublication)
        {
            return;
        }

        // The tracker takes ownership of this source and is its single disposer, so nothing here keeps
        // a second handle on it: a cancel arrives through the token callback below, not through the
        // session. Deliberately NOT the session's own CancellationTokenSource - that one is cancelled
        // and disposed by teardown, and a source is one-shot, so a session that survives a cancelled
        // login could never start a second one.
        var loginCancellation = new CancellationTokenSource();
        session.LoginOperationId = _operationTracker.RegisterOperation(
            OperationType.PrefillLogin,
            $"{ServiceName} Prefill Sign-In",
            loginCancellation);
        session.LoginStartedAtUtc = DateTime.UtcNow;

        // Cancelling the operation only cancels a token; ending the login is real work, and it has one
        // tested implementation whose clear-before-round-trip ordering must not be bypassed by a second
        // cancel route. Detached, so the tracker's synchronous cancel is not held behind the daemon
        // round-trip that CancelLoginAsync awaits.
        loginCancellation.Token.Register(() =>
            FireAndForgetAsync(() => CancelLoginAsync(session.Id), nameof(CancelLoginAsync)));
    }

    /// <summary>
    /// Ends the tracked operation raised for this session's login, if there is one, and clears the
    /// session's handle on it. The terminal mirrors the auth state the attempt landed on: authenticated
    /// is a success, a fail-fast carries the daemon's own failure text, and every other ending (the user
    /// cancelling, a logout, a login the sweep gave up on) is reported as cancelled rather than as an
    /// error the user has to dismiss - the login modal already shows those failures itself.
    /// Idempotent: the handle is cleared before the tracker is called, so two terminal paths racing on
    /// one session cannot both hand the tracker the same id.
    /// </summary>
    private void CompleteLoginOperation(DaemonSession session)
    {
        if (_operationTracker is null || session.LoginOperationId is not { } operationId)
        {
            return;
        }

        session.LoginOperationId = null;
        session.LoginStartedAtUtc = null;

        var authenticated = session.AuthState == DaemonAuthState.Authenticated;
        _operationTracker.CompleteOperation(
            operationId,
            success: authenticated,
            error: authenticated ? null : session.LastLoginFailureMessage,
            cancelled: !authenticated && session.LastLoginFailureMessage is null);
    }

    private async Task<CredentialChallenge?> StartLoginEntryAsync(
        string sessionId,
        TimeSpan? timeout,
        Guid? accountId,
        Action? onCommandDispatched,
        bool reuseIntegration,
        CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        if (!session.Runs.IsEmpty && (session.IsPrefilling || session.Recovering))
            throw new ConflictException("Wait for active prefill runs to finish before signing in.")
            { StageKey = "errors.prefill.containerBusy" };

        // Serializes login attempts on this session: overlapping calls would race the
        // daemon's single challenge/status stream and clobber each other's failure text/auth state.
        // A short bounded wait absorbs the common case where another attempt holds the lock only
        // briefly. An attempt still blocked after that is rejected rather than queued, so the caller
        // gets a bounded, unambiguous error instead of silently waiting behind one it doesn't know
        // about.
        if (!await session.LoginLock.WaitAsync(_loginLockWaitTimeout, cancellationToken))
        {
            throw new ConflictException($"A login attempt is already in progress for session {sessionId}.")
            {
                StageKey = "errors.prefill.loginInProgress",
                Context = new() { ["sessionId"] = sessionId }
            };
        }

        var abandonedLoginCleanup = new AbandonedLoginCleanupHolder();
        try
        {
            RegisterLoginOperation(session);

            CredentialChallenge? challenge;
            if (reuseIntegration)
            {
                challenge = await ReuseIntegrationLoginCoreAsync(
                    session,
                    accountId ?? throw new UnauthorizedAccessException("A stable account is required to use a saved login."),
                    onCommandDispatched
                        ?? throw new ArgumentNullException(nameof(onCommandDispatched)),
                    cancellationToken);
            }
            else
            {
                // Public interactive behavior remains unchanged: callers receive the challenge
                // (or null for authenticated / failed-fast / no-response).
                var result = await StartLoginCoreAsync(
                    session,
                    sessionId,
                    timeout,
                    abandonedLoginCleanup,
                    onCommandDispatched,
                    cancellationToken);
                challenge = result.Challenge;
            }

            // The challenge is the one object both interactive entry paths already hand the browser,
            // so it carries the login operation id out. Integration reuse never returns a challenge.
            if (challenge is not null)
            {
                challenge.OperationId = session.LoginOperationId?.ToString();
            }

            return challenge;
        }
        finally
        {
            // A session still LoggingIn is genuinely mid-flow: the person is answering an interactive
            // challenge, and the auth-state funnel closes the card when they finish.
            if (session.AuthState != DaemonAuthState.LoggingIn)
            {
                CompleteLoginOperation(session);
            }

            ReleaseLoginLockAfterCleanup(session, abandonedLoginCleanup);
        }
    }

    private async Task<CredentialChallenge?> ReuseIntegrationLoginCoreAsync(
        DaemonSession session,
        Guid accountId,
        Action onCommandDispatched,
        CancellationToken cancellationToken)
    {
        session.LastLoginFailureMessage = null;
        session.LastConsumedLoginChallengeId = null;
        ClearPendingLoginChallenge(session);

        var currentStatus = await session.Client.GetStatusAsync(cancellationToken);
        if (currentStatus?.Status == "logged-in")
        {
            await OnStatusChangeAsync(session, currentStatus);
            throw new ConflictException("Log out the current account before using a saved login.")
            {
                StageKey = "errors.prefill.logoutBeforeSavedLogin"
            };
        }

        if (session.AuthState != DaemonAuthState.NotAuthenticated
            || session.PendingLoginChallenge is not null
            || string.Equals(currentStatus?.Status, "logging-in", StringComparison.OrdinalIgnoreCase)
            || string.Equals(currentStatus?.Status, "authenticating", StringComparison.OrdinalIgnoreCase)
            || string.Equals(currentStatus?.Status, "login-in-progress", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConflictException("A login attempt is already in progress for this session.")
            {
                StageKey = "errors.prefill.loginInProgress"
            };
        }

        var availability = GetIntegrationLoginAvailability(accountId);
        if (!availability.Available)
        {
            await FailLoginFastAsync(
                session,
                session.Id,
                $"Integration login is unavailable ({availability.Reason ?? "not-authenticated"}).");
            return null;
        }

        session.AuthState = DaemonAuthState.LoggingIn;
        await NotifyAuthStateChangeAsync(session);

        try
        {
            EnsureCurrentSession(session);
            var accepted = await ReuseIntegrationLoginAsync(
                session,
                accountId,
                onCommandDispatched,
                cancellationToken);
            if (!accepted)
            {
                await FailLoginFastAsync(session, session.Id, "Integration login was rejected by the daemon.");
                return null;
            }

            EnsureCurrentSession(session);
            var finalStatus = await session.Client.GetStatusAsync(cancellationToken);
            if (finalStatus?.Status == "logged-in")
            {
                await OnStatusChangeAsync(session, finalStatus);
                return null;
            }

            await FailLoginFastAsync(
                session,
                session.Id,
                "Integration login completed without authenticating the daemon.");
            return null;
        }
        catch (OperationCanceledException)
        {
            session.LastLoginFailureMessage = null;
            session.AuthState = DaemonAuthState.NotAuthenticated;
            ClearPendingLoginChallenge(session);
            await NotifyAuthStateChangeAsync(session);
            throw;
        }
        catch
        {
            await FailLoginFastAsync(session, session.Id, "Integration login failed.");
            throw;
        }
    }

    /// <summary>
    /// Releases a session's <see cref="DaemonSession.LoginLock"/> after a login attempt. Normally
    /// releases immediately. But when a fail-fast completion abandoned a still-running daemon task,
    /// <see cref="AwaitChallengeOrLoginFailureAsync"/> stashed its cleanup task in the holder instead
    /// of awaiting it inline - that cleanup's CancelLoginAsync clears the pending challenge wait
    /// synchronously but then also awaits its own "cancel-login" command round-trip to the daemon (up
    /// to that command's own timeout), so it must never be awaited on the hot fail-fast return path.
    /// The lock is released only once that cleanup finishes, so a later login attempt on this session
    /// can never overlap with the abandoned one. Shared by the interactive entry point and the
    /// headless coordinator (both own the lock for their whole attempt).
    /// </summary>
    private static void ReleaseLoginLockAfterCleanup(DaemonSession session, AbandonedLoginCleanupHolder abandonedLoginCleanup)
    {
        if (abandonedLoginCleanup.Task is { } cleanupTask)
        {
            _ = cleanupTask.ContinueWith(_ => session.LoginLock.Release(), TaskScheduler.Default);
        }
        else
        {
            session.LoginLock.Release();
        }
    }

    /// <summary>
    /// Mutable single-slot holder used to pass an abandoned-login-task cleanup <see cref="Task"/> out of
    /// <see cref="AwaitChallengeOrLoginFailureAsync"/> to <see cref="StartLoginAsync"/>'s <c>finally</c>
    /// without adding permanent state to <see cref="DaemonSession"/> - it only exists for the lifetime
    /// of one <see cref="StartLoginAsync"/> call.
    /// </summary>
    private sealed class AbandonedLoginCleanupHolder
    {
        public Task? Task;
    }

    private async Task<LoginAttemptResult> StartLoginCoreAsync(
        DaemonSession session,
        string sessionId,
        TimeSpan? timeout,
        AbandonedLoginCleanupHolder abandonedLoginCleanup,
        Action? onCommandDispatched,
        CancellationToken cancellationToken)
    {
        // Resume path: a challenge from an earlier StartLoginAsync call on this session is still
        // pending (e.g. the frontend closed/reopened the login modal, or a second request raced in
        // before the first one's challenge was consumed). Answer with the SAME challenge and issue NO
        // daemon command at all - the daemon would only reply "already in progress" without
        // re-emitting it, and the client's own StartLoginAsync destroys its queued copy the moment
        // it's invoked again, so this check must run before anything touches session.Client.
        if (session.PendingLoginChallenge is { } pendingLoginChallenge && session.AuthState != DaemonAuthState.Authenticated)
        {
            _logger.LogInformation(
                "Session {SessionId} has a pending login challenge - resuming it instead of starting a new daemon login",
                sessionId);
            return LoginAttemptResult.ForChallenge(pendingLoginChallenge);
        }

        _logger.LogInformation("Starting login for session {SessionId}. ResponsesDir: {ResponsesDir}",
            sessionId, session.ResponsesDir);

        // Reset any stale failure text from a previous attempt before racing this one.
        session.LastLoginFailureMessage = null;

        // A fresh attempt gets brand-new challenge ids from the daemon, so clear the just-consumed
        // marker left by any earlier attempt/step - it must only ever name the single most-recently
        // consumed challenge of the CURRENT flow, so it can never suppress a legitimate future
        // challenge. (The resume path above returns before here, so a mid-flow resume keeps it.)
        session.LastConsumedLoginChallengeId = null;

        // The daemon broadcasts "Login failed: <reason>" (status "awaiting-login") within
        // milliseconds when it can't proceed (e.g. an undecryptable stored token). Racing that
        // broadcast against the blind challenge waits below lets a doomed login fail in seconds
        // with the daemon's real error text instead of burning the full 30s/10s wait chain.
        var loginFailureTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<DaemonStatus, Task> onDaemonStatusUpdate = status =>
        {
            if (status != null && TryGetLoginFailureMessage(status, out var failureMessage))
            {
                loginFailureTcs.TrySetResult(failureMessage);
            }
            return Task.CompletedTask;
        };

        session.Client.OnStatusUpdate += onDaemonStatusUpdate;
        try
        {
            // If already authenticated, don't change state - just check with daemon
            if (session.AuthState == DaemonAuthState.Authenticated)
            {
                _logger.LogInformation("Session {SessionId} is already authenticated, checking daemon status", sessionId);
                var existingChallenge = await AwaitChallengeOrLoginFailureAsync(
                    session, session.Client.StartLoginAsync(timeout, cancellationToken), loginFailureTcs, abandonedLoginCleanup);
                if (loginFailureTcs.Task.IsCompleted)
                {
                    await FailLoginFastAsync(session, sessionId, loginFailureTcs.Task.Result);
                    return LoginAttemptResult.LoginFailed;
                }
                if (existingChallenge == null)
                {
                    // Daemon confirms we're still logged in
                    _logger.LogInformation("Session {SessionId} confirmed authenticated by daemon", sessionId);
                    return LoginAttemptResult.Authenticated;
                }
                // Daemon needs re-authentication - fall through to normal flow
                _logger.LogInformation("Session {SessionId} requires re-authentication", sessionId);
            }

            // Log what files exist in the responses directory
            if (Directory.Exists(session.ResponsesDir))
            {
                var files = Directory.GetFiles(session.ResponsesDir);
                _logger.LogInformation("Files in responses dir before login: {Files}",
                    files.Length > 0 ? string.Join(", ", files.Select(Path.GetFileName)) : "(empty)");
            }
            else
            {
                _logger.LogWarning("Responses directory does not exist: {ResponsesDir}", session.ResponsesDir);
            }

            session.AuthState = DaemonAuthState.LoggingIn;
            await NotifyAuthStateChangeAsync(session);

            var loginTask = onCommandDispatched is null
                ? session.Client.StartLoginAsync(timeout, cancellationToken)
                : StartLoginWithDispatchTrackingAsync(
                    session,
                    timeout,
                    onCommandDispatched,
                    cancellationToken);
            var challenge = await AwaitChallengeOrLoginFailureAsync(
                session,
                loginTask,
                loginFailureTcs,
                abandonedLoginCleanup);
            if (loginFailureTcs.Task.IsCompleted)
            {
                await FailLoginFastAsync(session, sessionId, loginFailureTcs.Task.Result);
                return LoginAttemptResult.LoginFailed;
            }

            // Log result
            if (challenge != null)
            {
                _logger.LogInformation("Received challenge for session {SessionId}: Type={Type}, Id={ChallengeId}",
                    sessionId, challenge.CredentialType, challenge.ChallengeId);
                // A headless attempt consumes and cancels its challenge without publishing it, so the
                // resume cache must not hold a challenge that is about to be revoked (an unlocked REST
                // challenge poll could otherwise serve it mid-attempt).
                if (!session.SuppressLoginChallengePublication)
                {
                    session.PendingLoginChallenge = challenge;
                }
                return LoginAttemptResult.ForChallenge(challenge);
            }

            // If login is already in progress, a challenge might already be queued.
            var pendingChallenge = await AwaitChallengeOrLoginFailureAsync(
                session, session.Client.WaitForChallengeAsync(TimeSpan.FromSeconds(10), cancellationToken), loginFailureTcs, abandonedLoginCleanup);
            if (loginFailureTcs.Task.IsCompleted)
            {
                await FailLoginFastAsync(session, sessionId, loginFailureTcs.Task.Result);
                return LoginAttemptResult.LoginFailed;
            }
            if (pendingChallenge != null)
            {
                _logger.LogInformation("Received queued challenge for session {SessionId}: Type={Type}, Id={ChallengeId}",
                    sessionId, pendingChallenge.CredentialType, pendingChallenge.ChallengeId);
                // Same suppression rule as the first-challenge cache write above.
                if (!session.SuppressLoginChallengePublication)
                {
                    session.PendingLoginChallenge = pendingChallenge;
                }
                return LoginAttemptResult.ForChallenge(pendingChallenge);
            }

            var status = await session.Client.GetStatusAsync(cancellationToken);
            if (status?.Status == "logged-in")
            {
                // Route through OnStatusChangeAsync so username capture, ban re-enforcement, and
                // SessionUpdated run exactly once - do not also fire OnSessionAuthenticated here.
                await OnStatusChangeAsync(session, status);

                _logger.LogInformation("Session {SessionId} already authenticated - no challenge needed", sessionId);
                return LoginAttemptResult.Authenticated;
            }

            if (Directory.Exists(session.ResponsesDir))
            {
                var files = Directory.GetFiles(session.ResponsesDir);
                _logger.LogWarning("No challenge received. Files in responses dir: {Files}",
                    files.Length > 0 ? string.Join(", ", files.Select(Path.GetFileName)) : "(empty)");
            }

            // Defensive guard: a persistent container may already be authenticated from its own named auth
            // volume (the daemon self-authenticates; the manager never injects a token). In that case the
            // daemon emits no challenge. Re-check the live daemon status before failing - if it reports
            // logged-in, treat this as an already-authenticated result (no challenge needed) rather than
            // throwing, so a stray login attempt on an already-logged-in container does not error.
            var finalStatus = await session.Client.GetStatusAsync(cancellationToken);
            if (finalStatus?.Status == "logged-in")
            {
                // Same single-path rule as the earlier already-logged-in branch.
                await OnStatusChangeAsync(session, finalStatus);
                _logger.LogInformation(
                    "Session {SessionId} already authenticated per daemon status - no challenge needed", sessionId);
                return LoginAttemptResult.Authenticated;
            }

            _logger.LogWarning(
                "Session {SessionId} login started but no challenge was received from the daemon",
                sessionId);
            return LoginAttemptResult.NoResponse;
        }
        finally
        {
            session.Client.OnStatusUpdate -= onDaemonStatusUpdate;
        }
    }

    private async Task<CredentialChallenge?> StartLoginWithDispatchTrackingAsync(
        DaemonSession session,
        TimeSpan? timeout,
        Action onCommandDispatched,
        CancellationToken cancellationToken)
    {
        var commandDispatched = false;
        try
        {
            return await session.Client.StartLoginWithDispatchAsync(
                timeout,
                () =>
                {
                    commandDispatched = true;
                    onCommandDispatched();
                },
                cancellationToken);
        }
        catch
        {
            if (!commandDispatched)
            {
                // The state was moved to LoggingIn immediately before the transport call. If no
                // command reached the daemon, restore the pre-attempt state and leave no edit-owned
                // work for cleanup to claim.
                session.AuthState = DaemonAuthState.NotAuthenticated;
                await NotifyAuthStateChangeAsync(session);
            }

            throw;
        }
    }

    /// <summary>
    /// Races a daemon challenge-producing call against <paramref name="loginFailureTcs"/>. Returns the
    /// daemon's result when it wins the race; returns null when the failure broadcast wins - the
    /// caller checks <c>loginFailureTcs.Task.IsCompleted</c> to distinguish "daemon said no challenge
    /// yet" from "daemon reported a login failure".
    /// </summary>
    /// <remarks>
    /// When the failure broadcast wins, <paramref name="daemonTask"/> (the daemon client's
    /// StartLoginAsync/WaitForChallengeAsync call) is still running. Returning immediately here -
    /// rather than awaiting its cleanup inline - preserves the fail-fast latency guarantee: the real
    /// <see cref="IDaemonClient.CancelLoginAsync"/> clears the pending challenge wait synchronously but
    /// then also awaits its own "cancel-login" command round-trip to the daemon, which can itself take
    /// up to that command's own timeout - awaiting it here would reintroduce the exact blind-wait
    /// latency this fix eliminates. Instead the cancel+observe work is stashed on
    /// <paramref name="abandonedLoginCleanup"/> for <see cref="StartLoginAsync"/> to await before
    /// releasing <see cref="DaemonSession.LoginLock"/>: a later login attempt
    /// on this session is rejected by the lock until the abandoned task is fully resolved, so it can
    /// never overlap with it.
    /// </remarks>
    private async Task<CredentialChallenge?> AwaitChallengeOrLoginFailureAsync(
        DaemonSession session,
        Task<CredentialChallenge?> daemonTask,
        TaskCompletionSource<string> loginFailureTcs,
        AbandonedLoginCleanupHolder abandonedLoginCleanup)
    {
        var completed = await Task.WhenAny(daemonTask, loginFailureTcs.Task);
        if (completed != loginFailureTcs.Task)
        {
            return await daemonTask;
        }

        abandonedLoginCleanup.Task = ObserveAbandonedLoginTaskAsync(session, daemonTask);
        return null;
    }

    /// <summary>
    /// Best-effort cancels the daemon's in-flight login wait and then awaits it to completion,
    /// swallowing any resulting exception so it is never left unobserved. Always uses
    /// <see cref="CancellationToken.None"/> for the cancel command itself - this cleanup must run to
    /// completion regardless of whether the original caller's request/token has since been cancelled
    /// or disposed.
    /// </summary>
    private async Task ObserveAbandonedLoginTaskAsync(DaemonSession session, Task<CredentialChallenge?> daemonTask)
    {
        try
        {
            await session.Client.CancelLoginAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "CancelLoginAsync failed while abandoning a fail-fast-superseded login task for session {SessionId}",
                session.Id);
        }

        try
        {
            await daemonTask;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Abandoned login task for session {SessionId} completed with an exception after fail-fast cancellation (expected)",
                session.Id);
        }
    }

    /// <summary>
    /// Matches the daemon's uniform failure broadcast shape (steam/epic/xbox all call
    /// <c>BroadcastStatusAsync("awaiting-login", $"Login failed: {ex.Message}")</c> on a login exception).
    /// </summary>
    private static bool TryGetLoginFailureMessage(DaemonStatus status, out string failureMessage)
    {
        if (!string.IsNullOrEmpty(status.Message) &&
            status.Message.Contains("Login failed:", StringComparison.OrdinalIgnoreCase))
        {
            failureMessage = status.Message;
            return true;
        }
        failureMessage = string.Empty;
        return false;
    }

    /// <summary>
    /// Terminal handler for a fail-fast login: records the daemon's real error text on the session
    /// (read directly by <c>PersistentPrefillController.StartLoginAsync</c>), resets auth state since the
    /// login definitively failed, and broadcasts the state change so the "authenticating" UI clears.
    /// </summary>
    protected async Task<CredentialChallenge?> FailLoginFastAsync(DaemonSession session, string sessionId, string failureMessage)
    {
        session.LastLoginFailureMessage = failureMessage;
        session.AuthState = DaemonAuthState.NotAuthenticated;
        // A stale pending challenge must never be handed to a resume after the daemon has reported a
        // failure for this attempt.
        ClearPendingLoginChallenge(session);
        _logger.LogWarning("Session {SessionId} login failed fast: {FailureMessage}", sessionId, failureMessage);
        await NotifyAuthStateChangeAsync(session);
        return null;
    }

    /// <summary>
    /// Clears a session's cached resumable login challenge (<see cref="DaemonSession.PendingLoginChallenge"/>),
    /// returning whatever it held. A stale challenge must never be served to a later resume, so every
    /// transition that invalidates it - a fail-fast failure, a credential being consumed, a cancelled
    /// login, an auth-state move to Authenticated, or session termination - goes through this single
    /// place rather than a direct <c>PendingLoginChallenge = null</c> write at each call site.
    /// </summary>
    private static CredentialChallenge? ClearPendingLoginChallenge(DaemonSession session)
    {
        var previous = session.PendingLoginChallenge;
        session.PendingLoginChallenge = null;
        return previous;
    }

    /// <summary>
    /// Provides an encrypted credential in response to a challenge.
    /// Override in derived class to add service-specific credential handling (e.g., ban checking).
    /// </summary>
    public virtual async Task ProvideCredentialAsync(
        string sessionId,
        CredentialChallenge challenge,
        string credential,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        // The caller is answering the session's current pending challenge - drop the cache here so
        // no concurrent resume/poll (GET /challenge, the reopen reconcile, a SignalR-down poll
        // fallback) can serve this now-consumed challenge as if it were still live. If the daemon
        // needs another step, OnCredentialChallengeAsync re-populates the cache with that follow-on
        // challenge; if not, WaitForChallengeAsync correctly falls through to a live daemon wait
        // instead of replaying stale data.
        ClearPendingLoginChallenge(session);

        // Record the id of the challenge we are answering so a concurrent stale re-delivery of this
        // SAME challenge over the daemon's OTHER delivery channel (the OnCredentialChallenge event,
        // handled by OnCredentialChallengeAsync) cannot re-cache the now-consumed challenge after this
        // clear - that late duplicate would otherwise be replayed to the next WaitForChallengeAsync/GET
        // poll (the "challenge:password twice" race). Only a genuinely NEW follow-on challenge (a
        // different ChallengeId) is allowed to repopulate the cache; a matching id is dropped as the
        // stale second copy of the challenge just answered. Reset on the next fresh login attempt.
        session.LastConsumedLoginChallengeId = challenge.ChallengeId;

        // If this is the username credential, capture it
        if (challenge.CredentialType.Equals("username", StringComparison.OrdinalIgnoreCase))
        {
            session.AccountUsername = credential;
            session.Username = credential;

            // Update the database record with the username
            await _sessionService.SetUsernameAsync(sessionId, credential);

            // Do not log the username at Information level - it is PII. The value is
            // persisted via SetUsernameAsync above; keep only the session id in the log.
            _logger.LogDebug("Captured username for session {SessionId}", sessionId);

            // Broadcast session update to all clients for real-time updates (both hubs)
            var updatedDto = DaemonSessionDto.FromSession(session);
            await NotifyHubAsync(EventSessionUpdated, updatedDto);
        }

        _logger.LogInformation("Providing encrypted {CredentialType} for session {SessionId}",
            challenge.CredentialType, sessionId);

        await session.Client.ProvideCredentialAsync(challenge, credential, cancellationToken);
    }

    /// <summary>
    /// Waits for the next credential challenge
    /// </summary>
    public async Task<CredentialChallenge?> WaitForChallengeAsync(
        string sessionId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        // Serve a cached resume challenge immediately - no need to make the poller wait on the
        // daemon when we already know the answer.
        if (session.PendingLoginChallenge is { } pendingLoginChallenge && session.AuthState != DaemonAuthState.Authenticated)
        {
            return pendingLoginChallenge;
        }

        // A headless self-auth attempt currently owns this session's login flow: whatever challenge
        // the daemon emits belongs to that attempt (which consumes and silently cancels it), never to
        // a UI/reconcile poll - and reaching the transport below would let this poll take over the
        // shared challenge waiter the attempt's login command installed, stealing the challenge the
        // attempt is about to revoke. There is no user-facing challenge while the flag is set, by
        // definition; report "none" without touching the client. (A poll that was ALREADY waiting
        // inside the transport when the attempt began is superseded by the login command's own waiter
        // install and starves harmlessly to its timeout; a poll that slips past this check before the
        // flag is set is refused by the transports' login-owned-waiter guard.)
        if (session.SuppressLoginChallengePublication)
        {
            return null;
        }

        // A cached challenge served above was already stamped where it was returned or broadcast, but one
        // that comes straight off the transport here has not been, and the persistent login poll hands
        // exactly this one back to the browser (PersistentPrefillController.cs:709).
        var challenge = await session.Client.WaitForChallengeAsync(timeout, cancellationToken);
        if (challenge is not null)
        {
            challenge.OperationId = session.LoginOperationId?.ToString();
        }

        return challenge;
    }

    /// <summary>
    /// Cancels a pending login attempt and resets auth state.
    /// Sends cancel-login command to the daemon to abort any pending credential waits.
    /// </summary>
    public async Task CancelLoginAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        _logger.LogInformation("Cancelling login for session {SessionId}", sessionId);

        // Clear the resume cache and any queued daemon-side challenge FIRST, before the (possibly
        // slow) daemon cancel round-trip below. CancelLoginAsync takes no lock, so a concurrent
        // resume/poll landing during that await would otherwise be able to serve a challenge that is
        // being cancelled out from under it - a cancelled login must never be resumable, not even for
        // the few ms the daemon round-trip takes. Captured first so it can be restored if the daemon
        // round-trip itself fails below - a failed cancel must not be treated as a successful one.
        var capturedPendingChallenge = ClearPendingLoginChallenge(session);
        session.Client.ClearPendingChallenges();

        try
        {
            // Send cancel-login command to daemon - this will abort any pending credential waits
            await session.Client.CancelLoginAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // The daemon round-trip failed, so the daemon may still believe this login is in
            // progress. Restore the cached challenge so a later StartLoginAsync resumes it instead of
            // racing a brand-new daemon login against the one that was never actually cancelled - the
            // exact duplicate-login race the resume cache exists to prevent - and surface the failure
            // to the caller instead of silently proceeding as if the cancel had succeeded.
            session.PendingLoginChallenge = capturedPendingChallenge;
            _logger.LogWarning(ex, "Error sending cancel-login to daemon for session {SessionId}; login was not cancelled", sessionId);
            throw;
        }

        // Reset auth state to allow a new login attempt
        session.AuthState = DaemonAuthState.NotAuthenticated;
        await NotifyAuthStateChangeAsync(session);

        _logger.LogInformation("Login cancelled for session {SessionId}, ready for new attempt", sessionId);
    }

    /// <summary>
    /// Attempts to log a RUNNING persistent session out in place via the daemon's <c>logout</c>
    /// command, without tearing the container down. IsPersistent-gated defense in depth, mirroring
    /// the other persistent-only operations in this class. On daemon success, the session is put
    /// through the SAME auth-state funnel every other login/logout transition uses
    /// (<see cref="NotifyAuthStateChangeAsync"/>) so subscribers see the account was forgotten, its
    /// cached resume challenge is cleared, and <see cref="DaemonSession.NeedsRelogin"/> is reset. On
    /// failure - the daemon reports failure, or the round-trip itself throws (socket error, timeout)
    /// - this performs NO teardown and returns a not-forgotten result; the caller
    /// (<see cref="Controllers.PersistentPrefillController"/> / the frontend) decides whether to fall
    /// back to a stop+restart. NOTE: an un-updated steam/epic daemon image reports SUCCESS here while
    /// only tearing down the live session, without deleting the stored account file - that case is
    /// in-band indistinguishable from a true success and is not detected by this method; it
    /// self-resolves once the daemon image is rebuilt with the account-file-delete fix.
    /// The pending login challenge / resume wait is cleared BEFORE the daemon round-trip, same
    /// ordering as <see cref="CancelLoginAsync"/> - a logout must not leave a stale challenge
    /// resumable while the (possibly slow) daemon call is in flight. Unlike
    /// <see cref="CancelLoginAsync"/>, that clear is NOT restored if the round-trip fails: logout
    /// intent is terminal, so there is nothing to resume even on failure.
    /// </summary>
    public async Task<PersistentLogoutResult> LogoutPersistentSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        if (!session.IsPersistent)
        {
            _logger.LogWarning("LogoutPersistentSessionAsync called for non-persistent session {SessionId}", sessionId);
            return new PersistentLogoutResult(false);
        }

        if (!session.Runs.IsEmpty)
        {
            lock (session.PrefillLock) session.AdmissionClosed = true;
            try
            {
                foreach (var run in session.Runs.Values.Where(run => run.TerminalCompletedFlag != 2))
                    await CancelPrefillRunAsync(session.Id, run.PrefillRunId, cancellationToken);
                await Task.WhenAll(session.Runs.Values.Where(run => run.TerminalCompletedFlag != 2)
                    .Select(run => run.Completion.Task)).WaitAsync(cancellationToken);
            }
            catch
            {
                session.AdmissionClosed = false;
                throw;
            }
        }

        ClearPendingLoginChallenge(session);
        session.Client.ClearPendingChallenges();

        LogoutOutcome outcome;
        try
        {
            outcome = await session.Client.LogoutWithReasonAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex,
                "Daemon logout command failed for persistent session {SessionId}; caller should fall back to a stop+restart",
                sessionId);
            session.AdmissionClosed = false;
            return new PersistentLogoutResult(false);
        }

        if (!outcome.Success)
        {
            session.AdmissionClosed = false;
            if (outcome.RequiresLogin)
            {
                // Older daemon image's pre-login command gate rejected "logout" outright because this
                // session hasn't finished authenticating - not a genuine failure. Still returns
                // forgotten=false; the caller (frontend) routes this case to cancelling the in-flight
                // login instead of falling back to a stop+restart.
                _logger.LogInformation(
                    "Daemon declined logout for persistent session {SessionId} before authentication completed " +
                    "(older daemon image); nothing to log out",
                    sessionId);
            }
            else
            {
                _logger.LogInformation(
                    "Daemon reported logout failed for persistent session {SessionId}; caller should fall back to a stop+restart",
                    sessionId);
            }
            return new PersistentLogoutResult(false);
        }

        session.AuthState = DaemonAuthState.NotAuthenticated;
        session.AdmissionClosed = false;
        session.NeedsRelogin = false;
        await NotifyAuthStateChangeAsync(session);

        _logger.LogInformation("Session {SessionId} logged out in place; daemon forgot its stored account", sessionId);
        return new PersistentLogoutResult(true);
    }

    /// <summary>
    /// Removes this service's persistent auth volume when no session is currently running for it -
    /// used by the admin "clear all logins" endpoint (<c>POST .../persistent/clear-logins</c>) to
    /// forget a STOPPED service's stored login even though there is no live session/socket to send a
    /// <c>logout</c> command to. Never throws: Docker's "no such volume" (already gone) and "volume in
    /// use" (a stopped-but-still-attached container still references it) are both reported as typed
    /// results rather than propagated - the end state the caller cares about (no lingering login) is
    /// either already true, or requires the admin to remove the attached container first, which this
    /// method cannot force.
    /// </summary>
    // virtual: the escalation in ForgetRunningPersistentLoginAsync calls this, and unit tests override
    // it to exercise the hard-remove path without a live Docker daemon.
    public virtual async Task<PersistentVolumeClearResult> ClearPersistentAuthVolumeAsync(CancellationToken cancellationToken = default)
    {
        if (!_containerGateway.IsAvailable)
        {
            return PersistentVolumeClearResult.DockerUnavailable;
        }

        // RC5: with deterministic naming a STOPPED
        // {ContainerPrefix}persistent container keeps this service's named auth volume ATTACHED, so the
        // force:false remove below 409s -> InUse and "clear stored logins" silently no-ops. Reap the
        // lingering stopped container first so the volume can actually be removed. A still-RUNNING
        // container is left untouched: the remove below then reports InUse, preserving the existing
        // refuse-while-running behavior instead of tearing down a live login.
        await RemoveStoppedPersistentContainersAsync(cancellationToken);

        var volumeName = GetPersistentConfigVolumeName();
        try
        {
            await _containerGateway.RemoveVolumeAsync(volumeName, force: false, cancellationToken);
            _logger.LogInformation("Cleared persistent {ServiceName} auth volume {Volume}", ServiceName, volumeName);
            return PersistentVolumeClearResult.Removed;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogInformation("Persistent {ServiceName} auth volume {Volume} does not exist; nothing to clear", ServiceName, volumeName);
            return PersistentVolumeClearResult.NotFound;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            _logger.LogWarning("Persistent {ServiceName} auth volume {Volume} is still attached to a container; not removed", ServiceName, volumeName);
            return PersistentVolumeClearResult.InUse;
        }
    }

    /// <summary>
    /// Clears the login of the RUNNING persistent session for this service during the admin "clear
    /// stored logins" sweep, with a HARD guarantee that holds even against an un-updated daemon image.
    ///
    /// WHY the verify-then-escalate: the daemon's in-place <c>logout</c> command is not trustworthy on
    /// an old steam/epic image - it reports <c>Success=true</c> WITHOUT deleting the account file that
    /// lives in the container's named auth volume (documented on <see cref="LogoutPersistentSessionAsync"/>
    /// and the LogoutAsync endpoint), so the container keeps self-authenticating from that volume and
    /// the next login click just dismisses again. A soft logout alone therefore cannot make
    /// "clear stored logins" deterministic. This method:
    ///   1. tries the least-disruptive in-place logout first (on an UPDATED image this genuinely forgets
    ///      the account and the container keeps running);
    ///   2. VERIFIES it against the daemon's LIVE status - trusting the socket, not the logout's own
    ///      success flag - so the old-image "lied about success" case is caught;
    ///   3. only when the logout did not verifiably take (reported failure, still "logged-in", or the
    ///      status could not be read), ESCALATES to <see cref="TerminateSessionAsync"/> (which detaches
    ///      the named volume by removing the container) followed by <see cref="ClearPersistentAuthVolumeAsync"/>
    ///      (which deletes that volume outright, after reaping any lingering stopped container) - the
    ///      only image-version-independent hard remove.
    ///
    /// Terminating a persistent container is permitted here because this runs ONLY from an explicit
    /// admin action (clear-logins); background reapers still never terminate a persistent container.
    /// The escalated container is left stopped/removed with no stored login - exactly the clean state
    /// the admin is asking for. The soft-logout-verified path leaves the container running and merely
    /// logged out, unchanged from before.
    /// </summary>
    public async Task<PersistentRunningLoginClearOutcome> ForgetRunningPersistentLoginAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        // (1) Least-disruptive first: in-place logout.
        var logoutResult = await LogoutPersistentSessionAsync(sessionId, cancellationToken);

        // (2) Verify against the LIVE socket. Do NOT trust logoutResult.LoggedOut on its own: an old
        // image reports success while the volume token survives. Verified-clean == the daemon
        // acknowledged the logout AND no longer reports "logged-in".
        bool verifiedLoggedOut;
        try
        {
            var status = await GetSessionStatusAsync(sessionId, cancellationToken);
            verifiedLoggedOut = logoutResult.LoggedOut && status?.Status != "logged-in";
        }
        catch (Exception ex)
        {
            // Could not confirm the logout took effect - refuse to stand behind an unverified success;
            // escalate to the hard remove instead (mirrors the list endpoint's resilient status catch).
            _logger.LogInformation(ex,
                "Could not verify in-place logout for persistent session {SessionId}; escalating to hard remove",
                sessionId);
            verifiedLoggedOut = false;
        }

        if (verifiedLoggedOut)
        {
            _logger.LogInformation(
                "Persistent session {SessionId} logged out in place and verified clear; container left running",
                sessionId);
            return PersistentRunningLoginClearOutcome.LoggedOut;
        }

        // (3) Escalate: terminate so the named auth volume detaches, then delete the volume.
        // ClearPersistentAuthVolumeAsync also reaps a lingering stopped container first, so the delete
        // succeeds instead of returning InUse.
        _logger.LogWarning(
            "In-place logout did not verifiably clear persistent session {SessionId}; terminating the container " +
            "and deleting its named auth volume so the stored login cannot survive",
            sessionId);

        await TerminateSessionAsync(
            sessionId,
            reason: "Clear stored logins: hard-remove stale persistent login",
            force: true,
            terminatedBy: "admin");

        var volumeResult = await ClearPersistentAuthVolumeAsync(cancellationToken);
        return volumeResult is PersistentVolumeClearResult.Removed or PersistentVolumeClearResult.NotFound
            ? PersistentRunningLoginClearOutcome.HardRemoved
            : PersistentRunningLoginClearOutcome.HardRemoveFailed;
    }

    /// <summary>
    /// RC5: force-removes any STOPPED
    /// <c>{ContainerPrefix}persistent</c> container that still holds this service's named auth volume
    /// attached, so <see cref="ClearPersistentAuthVolumeAsync"/> can actually delete the volume. A
    /// RUNNING container is deliberately skipped (the caller's <c>GetActivePersistentSession()==null</c>
    /// precondition means there is no live session, but a zombie could still be running - leave it so
    /// the volume remove reports InUse rather than killing it). Never throws: a failed list/remove is
    /// logged and swallowed, leaving the volume remove to report its own honest result.
    /// <paramref name="cancellationToken"/> bounds the docker calls. RemoveVolumes stays false - the
    /// named volume is removed separately by the caller, and this reaps only the container shell.
    /// </summary>
    private async Task RemoveStoppedPersistentContainersAsync(CancellationToken cancellationToken)
    {
        if (!_containerGateway.IsAvailable)
        {
            return;
        }

        var containerName = $"{ContainerPrefix}persistent";
        IList<ContainerListResponse> matches;
        try
        {
            matches = await _containerGateway.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["name"] = new Dictionary<string, bool> { [containerName] = true }
                    }
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to list persistent {ServiceName} containers while clearing auth volume; leaving the volume remove to report its own result",
                ServiceName);
            return;
        }

        // Docker's name filter is a substring match, so re-assert an EXACT name match (same shape as
        // ForceRemoveContainersByExactNameAsync) to avoid touching an unrelated container.
        foreach (var match in matches.Where(c => (c.Names ?? new List<string>()).Any(n => n.TrimStart('/') == containerName)))
        {
            if (string.Equals(match.State, "running", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Persistent {ServiceName} container {Name} is still running; not removing it before clearing its auth volume",
                    ServiceName, containerName);
                continue;
            }

            if (await RemoveContainerForceAsync(match.ID, cancellationToken) == ContainerRemovalOutcome.Removed)
            {
                _logger.LogInformation(
                    "Removed stopped persistent {ServiceName} container {Id} so its auth volume can be cleared",
                    ServiceName, ShortContainerId(match.ID));
            }
        }
    }

    /// <summary>
    /// Forgets every registered service's persistent login with a HARD guarantee, one result row per
    /// resolvable daemon. A RUNNING session goes through <see cref="ForgetRunningPersistentLoginAsync"/>,
    /// which logs out in place, verifies that against the daemon's live status, and escalates to
    /// terminating the container + deleting its named auth volume when the logout did not verifiably
    /// take. With no session running, the service's persistent auth volume is removed outright so a
    /// STOPPED service's stored login is forgotten too. Never throws for a failed logout: the honest
    /// per-service outcome rides in <see cref="ClearPersistentLoginServiceResultDto.Success"/> and
    /// <see cref="ClearPersistentLoginServiceResultDto.Detail"/> so a caller can report what survived.
    /// </summary>
    public static async Task<List<ClearPersistentLoginServiceResultDto>> ClearAllPersistentLoginsAsync(
        IServiceProvider provider,
        CancellationToken cancellationToken)
    {
        var results = new List<ClearPersistentLoginServiceResultDto>();

        foreach (var service in Enum.GetValues<PrefillPlatform>())
        {
            var daemon = ResolveDaemon(provider, service);
            if (daemon is null)
            {
                continue;
            }

            var session = daemon.GetActivePersistentSession();
            if (session is not null)
            {
                // Hard guarantee for a RUNNING container (see ForgetRunningPersistentLoginAsync): the
                // daemon's in-place logout is tried first, verified against its LIVE status, and - only
                // when it did not verifiably forget the login (old image that lies about success, or a
                // reported failure) - escalated to terminate the container + delete its named auth
                // volume. Report the honest outcome so the UI never celebrates a login that survived.
                var outcome = await daemon.ForgetRunningPersistentLoginAsync(session.Id, cancellationToken);
                results.Add(new ClearPersistentLoginServiceResultDto
                {
                    Service = service,
                    WasRunning = true,
                    Success = outcome is PersistentRunningLoginClearOutcome.LoggedOut
                        or PersistentRunningLoginClearOutcome.HardRemoved,
                    Detail = outcome switch
                    {
                        PersistentRunningLoginClearOutcome.LoggedOut => "logged-out",
                        PersistentRunningLoginClearOutcome.HardRemoved => "hard-removed",
                        _ => "hard-remove-failed"
                    }
                });
                continue;
            }

            var volumeResult = await daemon.ClearPersistentAuthVolumeAsync(cancellationToken);
            results.Add(new ClearPersistentLoginServiceResultDto
            {
                Service = service,
                WasRunning = false,
                Success = volumeResult is PersistentVolumeClearResult.Removed or PersistentVolumeClearResult.NotFound,
                Detail = volumeResult.ToString()
            });
        }

        return results;
    }
}
