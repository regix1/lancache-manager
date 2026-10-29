using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using LancacheManager.Models;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Middleware;
using LancacheManager.Infrastructure.Services;

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

    // Sessions whose row missed the last write of the login state. Memory stays the truth: the state is
    // written again in the background and at manager stop, and the next sign-in or Log in writes it again
    // even when it changes no mark.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _loginStateRowsOutOfDate = new();

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
    /// needs-login state (an unconfirmed cancel marks the session errored when the login is still
    /// in flight - see <see cref="CancelHeadlessLoginAsync"/>). Never pushes a modal, notification, or challenge
    /// broadcast. Runs fire-and-forget in the background (never blocks or fails startup); any
    /// exception is observed and logged by the <see cref="InvokeSafeAsync"/> wrapper. Returns the
    /// outcome, or null when it did not try. <paramref name="takeOver"/> stops the attempt, and ends a
    /// login it already sent, when a person's sign-in takes over from the retry that runs it.
    /// </summary>
    internal async Task<LoginAttemptOutcome?> AttemptHeadlessPersistentSelfAuthAsync(
        DaemonSession session, CancellationToken takeOver = default)
    {
        // The session was torn down (shutdown/terminate) between the trigger and this task running.
        if (!IsSessionLive(session))
        {
            return null;
        }

        var cancellationToken = session.CancellationTokenSource.Token;
        // The retry's own token also stops this attempt, so a person's sign-in is not kept waiting behind it.
        using var attemptStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, takeOver);
        var loginStop = attemptStop.Token;

        // Try-acquire like the interactive entry point, but bail silently instead of throwing: a
        // held lock means an interactive login is already in flight - the user got there first and
        // this attempt is unnecessary.
        if (!await session.LoginLock.WaitAsync(0, cancellationToken))
        {
            return null;
        }

        var abandonedLoginCleanup = new AbandonedLoginCleanupHolder();
        try
        {
            EnsureCurrentSession(session);

            // A cached challenge means an interactive login is mid-flow (paused/reopened modal), and a
            // set login operation means a person's sign-in is in progress; never cancel or restart
            // either out from under the user.
            if (session.PendingLoginChallenge is not null || session.LoginOperationId is not null)
            {
                return null;
            }

            // One live-status preflight, applied through the same handler every socket status push
            // uses, so an already-authenticated daemon (anonymous services, or a login-required
            // daemon that authenticated earlier) reconciles to Authenticated and broadcasts through
            // the existing flows instead of being skipped silently.
            var signOutCount = session.SignOutCount;
            var preflight = await session.Client.GetStatusAsync(loginStop);
            // The mark is read after the status round trip: a person's Cancel during it has to win.
            // A logged-in report while that mark is set stays signed out.
            if (preflight?.Status == "logged-in")
            {
                await ApplyStatusAsync(session, preflight, signOutCount);
                lock (session.PrefillLock)
                {
                    if (session.LoginCanceledByPerson)
                    {
                        return null;
                    }
                }

                return LoginAttemptOutcome.Authenticated;
            }

            lock (session.PrefillLock)
            {
                // A person canceled the last sign-in, so the container stays signed out until someone signs in. Read
                // under the lock that marks this sign-in as automatic, so a person's cancel sees one or the other.
                // A sign-out the daemon reported without the drop code also leaves the saved login unused until a
                // person signs in.
                if (session.LoginCanceledByPerson || session.SignedOut)
                {
                    return null;
                }
                // Unsettled only once a sign-in is really sent: a try that returns before it leaves the session as it
                // found it, so a pending image update still applies. The login lock this try holds already keeps
                // the image update away until here.
                session.LoginSettled = false;
                session.SuppressLoginChallengePublication = true;
                session.SigningInAutomatically = true;
            }
            try
            {
                var result = await StartLoginCoreAsync(
                    session,
                    session.Id,
                    timeout: null,
                    abandonedLoginCleanup,
                    onCommandDispatched: null,
                    loginStop,
                    loginId: null);
                switch (result.Outcome)
                {
                    case LoginAttemptOutcome.Authenticated:
                        // The daemon self-authenticated from its stored volume login; the login flow
                        // already flipped auth state and broadcast through the existing flows. Its status
                        // push skipped the settle while this attempt suppressed the challenge, so the
                        // attempt settles the session; a persistent container's image update waits for it.
                        // A person's cancel that landed while the command was in flight stays signed out,
                        // and an unconfirmed daemon cancel still has to be read before the session is left up.
                        // The daemon is signed in under that mark, so the witness records it for the next drop and
                        // for a later Log in.
                        bool personCanceledDuringAttempt;
                        lock (session.PrefillLock)
                        {
                            personCanceledDuringAttempt = session.LoginCanceledByPerson;
                            if (!personCanceledDuringAttempt)
                            {
                                session.LoginSettled = true;
                            }
                            else
                            {
                                session.LoggedInWhilePersonCanceled = true;
                            }
                        }
                        if (personCanceledDuringAttempt)
                        {
                            await SaveLoginStateAsync(session);
                            await CancelHeadlessLoginAsync(session, result, cancellationToken);
                        }
                        return LoginAttemptOutcome.Authenticated;

                    case LoginAttemptOutcome.Failed:
                        // The daemon itself failed the attempt (fail-fast path): auth state was
                        // already reset to NotAuthenticated and notified, no login is left in
                        // flight, and the failure was already logged with the daemon's own text.
                        return LoginAttemptOutcome.Failed;

                    default:
                        // ChallengeIssued: the stored login is gone/invalid and the daemon wants
                        // credentials nobody is present to type. NoResponse: the daemon neither
                        // authenticated nor challenged within the bounded waits, and may still be
                        // processing the login command; it must not be left visibly LoggingIn.
                        // Both need a confirmed daemon-side cancellation.
                        await CancelHeadlessLoginAsync(session, result, cancellationToken);
                        return result.Outcome;
                }
            }
            catch (OperationCanceledException) when (takeOver.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // A person's sign-in takes over from the retry: end the login this attempt already sent,
                // so theirs starts clean, and free the lock for it.
                await CancelHeadlessLoginAsync(session, null, cancellationToken);
                return null;
            }
            finally
            {
                session.SuppressLoginChallengePublication = false;
                session.SigningInAutomatically = false;
            }
        }
        finally
        {
            ReleaseLoginLockAfterCleanup(session, abandonedLoginCleanup);
        }
    }

    // The first try waits 30 s so a short network blip, or a prefill run the drop cut off and that is
    // still stopping, clears first. The waits grow because the daemon reports a refused saved login the
    // same way as an unreachable Steam, and each try is a full sign-in.
    private static readonly TimeSpan[] _signInAgainDelays =
    [
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(30)
    ];

    private void StartSignInAgain(DaemonSession session)
    {
        lock (session.PrefillLock)
        {
            // An errored session is replaced at its next start; it gets no more sign-in commands.
            if (session.Status == DaemonSessionStatus.Error)
            {
                return;
            }
            if (session.SignInAgainAfterDrop is not null)
            {
                return;
            }
            // The daemon also writes the drop code for a sign-in a person canceled; that stays signed out. A
            // later connection drop clears the mark before it calls here. Read under the same lock that starts
            // the retry, so a cancel cannot land between the two.
            if (session.LoginCanceledByPerson)
            {
                return;
            }
            var signInCount = session.SignInCount;
            // A startup headless try already holds this token. Replacing it would leave that try
            // running after a person's Cancel, which only cancels the source stored here.
            var takeOver = session.SignInAgainCancel;
            if (takeOver is null || takeOver.IsCancellationRequested)
            {
                takeOver = new CancellationTokenSource();
                session.SignInAgainCancel = takeOver;
            }
            session.SignInAgainAfterDrop = InvokeSafeAsync(
                () => SignInAgainAfterDropAsync(session, signInCount, takeOver.Token),
                nameof(SignInAgainAfterDropAsync));
        }
    }

    // Called under the session lock. The mark is the keep, and any logged-in seen before this cancel does
    // not turn the cancel's own auth-lost into a later drop.
    private void RememberPersonCancel(DaemonSession session)
    {
        session.LoginCanceledByPerson = true;
        session.LoggedInWhilePersonCanceled = false;
    }

    // Stores the session's login state on its row: the cancel mark, the logged-in witness, the log-off mark,
    // the last sign-in time and the re-login date. Each write reads all five when it runs, one write at a
    // time, so the last write stores the latest state whatever order the callers arrive in. The write is
    // tried once more after a failure. If it still fails, memory stays the truth: the session is recorded
    // as out of date and the state is written again until a write lands.
    private async Task SaveLoginStateAsync(DaemonSession session, CancellationToken cancellationToken = default)
    {
        await session.LoginStateWrites.WaitAsync(cancellationToken);
        try
        {
            const int attempts = 2;
            for (var attempt = 1; ; attempt++)
            {
                var canceled = false;
                var loggedInWhileCanceled = false;
                var signedOut = false;
                try
                {
                    DateTime lastLoginAt;
                    DateTime expiresAt;
                    lock (session.PrefillLock)
                    {
                        canceled = session.LoginCanceledByPerson;
                        loggedInWhileCanceled = session.LoggedInWhilePersonCanceled;
                        signedOut = session.SignedOut;
                        lastLoginAt = session.LastLoginAt;
                        expiresAt = session.ExpiresAt;
                    }

                    await _sessionService.SetLoginStateAsync(session.Id, canceled, loggedInWhileCanceled, signedOut,
                        lastLoginAt, expiresAt, cancellationToken);
                    _loginStateRowsOutOfDate.TryRemove(session.Id, out _);
                    return;
                }
                catch (Exception ex) when (attempt < attempts && ex is not OperationCanceledException)
                {
                    _logger.LogError(ex,
                        "Could not store the login state (canceled {Canceled}, logged in while canceled {LoggedInWhileCanceled}, signed out {SignedOut}) for session {SessionId}; retrying the write",
                        canceled, loggedInWhileCanceled, signedOut, session.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var firstMiss = _loginStateRowsOutOfDate.TryAdd(session.Id, 0);
                    _logger.LogError(ex,
                        "Could not store the login state (canceled {Canceled}, logged in while canceled {LoggedInWhileCanceled}, signed out {SignedOut}) for session {SessionId}; it is written again until a write lands",
                        canceled, loggedInWhileCanceled, signedOut, session.Id);
                    if (firstMiss)
                    {
                        FireAndForgetAsync(() => RewriteLoginStateAsync(session), nameof(RewriteLoginStateAsync));
                    }
                    return;
                }
            }
        }
        finally
        {
            session.LoginStateWrites.Release();
        }
    }

    // A row that missed its login state would let a manager restart sign the container back in or keep an old
    // re-login date, so it is written again every 30 s while the session lives, until one write lands.
    private async Task RewriteLoginStateAsync(DaemonSession session)
    {
        var sessionEnded = session.CancellationTokenSource.Token;
        try
        {
            while (_loginStateRowsOutOfDate.ContainsKey(session.Id))
            {
                await Task.Delay(TimeSpan.FromSeconds(30), CacheStatusClock, sessionEnded);
                await SaveLoginStateAsync(session);
            }
        }
        catch (OperationCanceledException) when (sessionEnded.IsCancellationRequested)
        {
            // The session ended. A container that replaces it copies the marks from this session's memory.
        }
    }

    // Empties the retry's place when it still holds this retry's; a newer retry has its own token source.
    private static void EmptyRetryPlace(DaemonSession session, CancellationToken takeOver)
    {
        lock (session.PrefillLock)
        {
            if (session.SignInAgainCancel is { } current && current.Token == takeOver)
            {
                session.SignInAgainAfterDrop = null;
            }
        }
    }

    private async Task SignInAgainAfterDropAsync(DaemonSession session, int signInCount, CancellationToken takeOver)
    {
        _logger.LogInformation(
            "Steam sign-in for persistent session {SessionId} was lost; signing in again with the saved login",
            session.Id);
        for (var attempt = 0; ; attempt++)
        {
            var delay = _signInAgainDelays[Math.Min(attempt, _signInAgainDelays.Length - 1)];
            LoginAttemptOutcome? outcome;
            try
            {
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(session.CancellationTokenSource.Token, takeOver);
                await Task.Delay(delay, CacheStatusClock, stop.Token);
                bool signedInMeanwhile;
                lock (session.PrefillLock)
                {
                    // An errored session gets no more sign-in commands, even from a retry it started before it errored.
                    if (session.Status == DaemonSessionStatus.Error)
                    {
                        return;
                    }
                    signedInMeanwhile = session.SignInCount != signInCount;
                }
                if (signedInMeanwhile)
                {
                    // Signed in some other way while this waited. A reply the daemon sent before a drop can be
                    // applied after it and look like that, so read the daemon once: if it is still signed out
                    // this read applies the drop and starts the next retry in this one's place.
                    await GetSessionStatusAsync(session.Id, stop.Token);
                    return;
                }
                outcome = await AttemptHeadlessPersistentSelfAuthAsync(session, takeOver);
                if (outcome == LoginAttemptOutcome.Authenticated)
                {
                    lock (session.PrefillLock)
                    {
                        // No sign-in was applied since this retry started, so this try only confirmed a
                        // signed-in state the session may hold from an old reply. Free this retry's place
                        // so the read below can start a new one.
                        if (session.SignInCount == signInCount)
                        {
                            EmptyRetryPlace(session, takeOver);
                        }
                    }
                    // A drop pushed while this sign-in's own status reply was in flight can be applied
                    // before that reply. One more read applies the daemon's current state, so that drop
                    // starts a new retry.
                    await GetSessionStatusAsync(session.Id, stop.Token);
                    return;
                }
            }
            catch (ConflictException)
            {
                // A person's cancel of an earlier sign-in is still finishing.
                continue;
            }
            catch (OperationCanceledException) when (session.CancellationTokenSource.IsCancellationRequested || takeOver.IsCancellationRequested)
            {
                // The session was stopped, or a person's sign-in took over, while this waited or tried.
                if (takeOver.IsCancellationRequested)
                {
                    EmptyRetryPlace(session, takeOver);
                }
                return;
            }
            catch (OperationCanceledException)
            {
                // Something else canceled the sign-in this try was making, not the retry itself; try again
                // at the next wait.
                continue;
            }
            catch (ObjectDisposedException)
            {
                // The service was disposed at shutdown while this waited.
                return;
            }

            if (takeOver.IsCancellationRequested)
            {
                EmptyRetryPlace(session, takeOver);
                return;
            }
            if (outcome == LoginAttemptOutcome.ChallengeIssued
                || !IsSessionLive(session)
                || session.Status == DaemonSessionStatus.Error)
            {
                return;
            }
            _logger.LogInformation(
                "Persistent session {SessionId} is not signed in to Steam yet; trying again after {Delay}",
                session.Id, _signInAgainDelays[Math.Min(attempt + 1, _signInAgainDelays.Length - 1)]);
        }
    }

    /// <summary>
    /// Silently cancels a headless login attempt that ended in a challenge or in no response, while
    /// the caller still owns <see cref="DaemonSession.LoginLock"/>. Only a cancel the daemon
    /// ACKNOWLEDGED may present the ordinary needs-login state - the daemon answers a repeat
    /// <c>login</c> command for an attempt it still considers in flight with "already in progress"
    /// WITHOUT re-emitting a challenge, so claiming needs-login after an unconfirmed cancel would
    /// wedge the admin's next interactive attempt. An unconfirmed cancel marks the session errored
    /// so the next start replaces it, unless a person's cancel finds the daemon logged in (the mark
    /// rule leaves it signed out) or gets a later cancel through and finds the daemon idle.
    /// </summary>
    private async Task CancelHeadlessLoginAsync(DaemonSession session, LoginAttemptResult? result, CancellationToken cancellationToken)
    {
        // Publication was suppressed, so nothing should be cached - clear defensively anyway, plus
        // the client's own queued copy, before the daemon round-trip.
        ClearPendingLoginChallenge(session);
        session.Client.ClearPendingChallenges();

        var cancelConfirmed = await session.Client.CancelLoginWithOutcomeAsync(cancellationToken);

        if (cancelConfirmed)
        {
            session.AuthState = DaemonAuthState.NotAuthenticated;
            session.LoginSettled = true;
            await NotifyAuthStateChangeAsync(session);

            // A person's sign-in took over before the attempt had an outcome, so there is nothing to report.
            if (result is null)
            {
                return;
            }

            if (result.Outcome == LoginAttemptOutcome.ChallengeIssued)
            {
                _logger.LogWarning(
                    "Headless self-auth for persistent {ServiceName} session {SessionId} found no usable stored login (daemon answered with a {CredentialType} challenge); attempt cancelled - awaiting interactive login",
                    ServiceName, session.Id, result.Challenge!.CredentialType);
            }
            else if (result.Outcome == LoginAttemptOutcome.Authenticated)
            {
                _logger.LogInformation(
                    "Headless self-auth for persistent {ServiceName} session {SessionId} signed in after a person canceled; the container stays signed out until they log in",
                    ServiceName, session.Id);
            }
            else
            {
                _logger.LogWarning(
                    "Headless self-auth for persistent {ServiceName} session {SessionId} got no response from the daemon (neither authenticated nor challenged); attempt cancelled - awaiting interactive login",
                    ServiceName, session.Id);
            }

            return;
        }

        bool personCanceled;
        lock (session.PrefillLock)
        {
            personCanceled = session.LoginCanceledByPerson;
        }

        if (personCanceled)
        {
            // The person stopped this startup try. A logged-in daemon stays on the mark rule. A login
            // that is still in flight is cancelled again until the daemon is idle. Error is only the
            // replacement path when that login never becomes idle, not the cancel by itself.
            if (await TrySettleUnconfirmedPersonCancelAsync(session, cancellationToken))
            {
                return;
            }
        }

        _logger.LogError(
            "Headless self-auth for persistent {ServiceName} session {SessionId} could not confirm daemon-side login cancellation; marking the session errored so the next start replaces the daemon cleanly",
            ServiceName, session.Id);
        session.Status = DaemonSessionStatus.Error;
        session.LoginSettled = false;
        session.ErrorMessage = "Automatic sign-in could not be cancelled cleanly. Start the session again to recover.";
        try
        {
            await NotifyHubAsync(session, EventSessionUpdated, DaemonSessionDto.FromSession(session));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to broadcast session update after headless cancel recovery for session {SessionId}", session.Id);
        }
    }

    // True when the daemon is logged in, or a later cancel is acknowledged. False when three
    // reads were thrown or returned no status, and when three reads returned a signed-out status
    // the cancel never acknowledged. The caller replaces the daemon on either false.
    private async Task<bool> TrySettleUnconfirmedPersonCancelAsync(DaemonSession session, CancellationToken cancellationToken)
    {
        const int finishedReads = 3;
        var signedOutReads = 0;
        var unknownReads = 0;
        while (signedOutReads < finishedReads)
        {
            DaemonStatus? live = null;
            var readFailed = false;
            var signOutCount = session.SignOutCount;
            try
            {
                live = await session.Client.GetStatusAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not read the daemon after an unconfirmed sign-in cancel for session {SessionId}",
                    session.Id);
                readFailed = true;
            }

            if (!readFailed && live is { Status: "logged-in" })
            {
                await ApplyStatusAsync(session, live, signOutCount);
                await PresentPersonCancelSignedOutAsync(session);
                return true;
            }

            // A thrown read, or a call that returned no status, is not a finished signed-out attempt.
            // Three of them still leave the login unconfirmed, so the caller replaces the daemon.
            if (readFailed || live is null)
            {
                unknownReads++;
                if (unknownReads >= finishedReads)
                {
                    return false;
                }

                continue;
            }

            signedOutReads++;
            if (signedOutReads == finishedReads)
            {
                return false;
            }

            bool acknowledged;
            try
            {
                acknowledged = await session.Client.CancelLoginWithOutcomeAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Retrying cancel-login failed for session {SessionId}",
                    session.Id);
                acknowledged = false;
            }

            if (acknowledged)
            {
                await PresentPersonCancelSignedOutAsync(session);
                return true;
            }
        }

        return false;
    }

    private async Task PresentPersonCancelSignedOutAsync(DaemonSession session)
    {
        DaemonAuthState previous;
        lock (session.PrefillLock)
        {
            previous = session.AuthState;
            if (session.AuthState != DaemonAuthState.NotAuthenticated)
            {
                session.AuthState = DaemonAuthState.NotAuthenticated;
            }

            session.LoginSettled = true;
            // A logged-in daemon, or a cancel the daemon acknowledged, stays up and signed out.
            // Three signed-out reads never reach here, and neither do three thrown or empty reads.
            // Those paths replace the daemon.
            if (session.Status != DaemonSessionStatus.Terminated)
            {
                session.Status = DaemonSessionStatus.Active;
                session.ErrorMessage = null;
            }
        }

        if (session.AuthState != previous)
        {
            await NotifyAuthStateChangeAsync(session);
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

        var signInCount = session.SignInCount;
        var signOutCount = session.SignOutCount;
        var status = await session.Client.GetStatusAsync(cancellationToken);
        if (status is not null)
        {
            // A sign-in in flight reads the daemon's idle "awaiting-login" until the daemon itself reports
            // the result, so only a logged-in reply may change the session's sign-in state from here. A
            // signed-out reply read before a sign-in finished is older than that sign-in.
            if ((session.LoginOperationId is null || status.Status == "logged-in")
                && (status.Status == "logged-in" || session.SignInCount == signInCount))
                await ApplyStatusAsync(session, status, signOutCount);
            await RecoverRunsAsync(session, status, cancellationToken);
        }
        else if (!session.Runs.IsEmpty)
        {
            session.Recovering = true;
            await SendRecoveryUpdateAsync(session);
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
            ownerSessionId: null,
            cancellationToken);

    /// <summary>
    /// Login entry point of the persistent session's Log in route. Calls <paramref name="onCommandDispatched"/>
    /// once a fresh daemon login command has been sent, which picks the route's "Logged in" or "Already
    /// logged in" answer. Cached challenge resumes do not invoke the callback because they start no new
    /// daemon work.
    /// </summary>
    /// <param name="ownerSessionId">The auth session that started this sign-in; only its browser
    /// draws the sign-in's card. A persistent session belongs to the system owner, so its own
    /// <see cref="DaemonSession.UserId"/> cannot name the caller.</param>
    internal Task<CredentialChallenge?> StartPersistentLoginAsync(
        string sessionId,
        TimeSpan? timeout,
        Action onCommandDispatched,
        Guid ownerSessionId,
        CancellationToken cancellationToken = default,
        Guid? loginId = null)
    {
        return StartLoginEntryAsync(
            sessionId,
            timeout,
            accountId: null,
            onCommandDispatched,
            reuseIntegration: false,
            ownerSessionId,
            cancellationToken,
            loginId: loginId);
    }

    /// <summary>
    /// Imports the server-side integration login into the exact persistent daemon session, and calls
    /// <paramref name="onCommandDispatched"/> once the login command has been sent.
    /// </summary>
    /// <param name="ownerSessionId">See <see cref="StartPersistentLoginAsync"/>.</param>
    internal Task<CredentialChallenge?> ReusePersistentIntegrationLoginAsync(
        string sessionId,
        Guid? accountId,
        Action onCommandDispatched,
        Guid ownerSessionId,
        CancellationToken cancellationToken = default,
        IntegrationLease? lease = null,
        Guid? loginId = null)
    {
        return StartLoginEntryAsync(
            sessionId,
            timeout: null,
            accountId,
            onCommandDispatched,
            reuseIntegration: true,
            ownerSessionId,
            cancellationToken,
            lease,
            loginId);
    }

    /// <summary>
    /// Reports whether this platform can currently prepare a server-side integration login.
    /// Returned values are safe for the browser and never contain credentials.
    /// </summary>
    public virtual IntegrationLoginAvailability GetIntegrationLoginAvailability(Guid? accountId, IntegrationCaller? caller = null)
        => new(false, null, "not-supported");

    public virtual Task<IntegrationLease> AcquireIntegrationLoginAsync(
        IntegrationCaller caller, CancellationToken cancellationToken = default)
    {
        IntegrationLease.Refuse("not-supported");
        throw new InvalidOperationException("Unsupported integration login.");
    }

    /// <summary>
    /// Imports this platform's integration login into the exact session client.
    /// </summary>
    protected virtual Task<bool> ReuseIntegrationLoginAsync(
        DaemonSession session,
        Guid? accountId,
        Action onCommandDispatched,
        CancellationToken cancellationToken,
        IntegrationLease? lease = null)
        => Task.FromResult(false);

    /// <summary>
    /// Rejects a login handoff when the session was replaced while credentials were being prepared.
    /// </summary>
    protected void EnsureCurrentSession(DaemonSession session)
    {
        lock (session.PrefillLock)
        {
            if (!_sessions.TryGetValue(session.Id, out var current)
                || !ReferenceEquals(current, session)
                || session.AdmissionClosed)
            {
                throw new ConflictException($"Persistent session {session.Id} was replaced.")
                {
                    StageKey = "errors.prefill.sessionReplaced",
                    Context = new() { ["sessionId"] = session.Id }
                };
            }
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
    private void RegisterLoginOperation(DaemonSession session, Guid ownerSessionId)
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
            loginCancellation,
            metadata: Platform,
            notice: new RunNotice(NotificationMode.All, RunTrigger.Manual),
            ownerSessionId: ownerSessionId);
        session.LoginExpiresAtUtc = DateTime.UtcNow.AddSeconds(GetAbandonedLoginTimeoutSeconds());
        // A new sign-in starts with no stop reason, so one a sweep left on an ended sign-in never colors this one.
        session.LoginStopReason = null;

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
    /// is a success, a fail-fast carries the daemon's own failure text, a sign-in the app ended (the sweep
    /// giving up on it, an expired or shutting-down session) is a failure that names why, and every other
    /// ending (the user cancelling, a logout) ends as a canceled run, which shows a gray card that leaves
    /// on its own unless Keep Notifications Visible holds it; the login modal shows the refusal itself.
    /// Idempotent: the handle is cleared before the tracker is called, so two terminal paths racing on
    /// one session cannot both hand the tracker the same id. It also keeps that ending on the session by attempt number.
    /// </summary>
    private void CompleteLoginOperation(DaemonSession session)
    {
        if (_operationTracker is null || session.LoginOperationId is not { } operationId)
        {
            return;
        }

        session.LoginOperationId = null;
        session.LoginExpiresAtUtc = null;

        var authenticated = session.AuthState == DaemonAuthState.Authenticated;
        var stoppedByApp = !authenticated && session.LoginStopReason is not null;
        // Kept by attempt for a browser that missed this ending: a guest reads it on its next subscribe, and a
        // persistent login reads it from the session's pushed update or its one read after a reconnect.
        var (endingStatus, endingKey) = authenticated ? (OperationStatus.Completed, "prefill.persistent.status.loggedIn")
            : stoppedByApp ? (OperationStatus.Failed, session.LoginStopReason!)
            : session.LastLoginFailureKey is { } failureKey ? (OperationStatus.Failed, failureKey)
            : session.LastLoginFailureMessage is not null ? (OperationStatus.Failed, "prefill.auth.signInRefused")
            : (OperationStatus.Cancelled, "errors.integration.attemptExpired");
        // A run refused before it took an attempt number of its own never replaces the ending of the attempt before it.
        if (session.LastLoginEnding?.LoginAttempt != session.LoginAttempt)
        {
            session.LastLoginEnding = new PrefillLoginEnding(session.LoginAttempt, endingStatus, endingKey);
        }
        if (stoppedByApp)
        {
            _operationTracker.SetWarning(operationId, new RunWarning(session.LoginStopReason!, new Dictionary<string, object?>()));
        }
        session.LoginStopReason = null;
        _operationTracker.CompleteOperation(
            operationId,
            success: authenticated,
            error: authenticated ? null : session.LastLoginFailureMessage,
            cancelled: !authenticated && !stoppedByApp && session.LastLoginFailureMessage is null);

        // A guest's browser has no notification bar and cannot reach the close route, so a kept ending
        // would stay until restart with nobody able to see or close it. A persistent container's failed
        // sign-in already shows as the red line on its container card, so a kept run card would repeat it.
        if (session.IsTemporary || (session.IsPersistent && !authenticated && session.LastLoginFailureMessage is not null))
            _operationTracker.CloseRun(operationId);
    }

    private async Task<CredentialChallenge?> StartLoginEntryAsync(
        string sessionId,
        TimeSpan? timeout,
        Guid? accountId,
        Action? onCommandDispatched,
        bool reuseIntegration,
        Guid? ownerSessionId,
        CancellationToken cancellationToken,
        IntegrationLease? lease = null,
        Guid? loginId = null)
    {
        if (reuseIntegration)
        {
            if (lease is null) IntegrationLease.Refuse("attempt-required");
            lease!.Validate();
        }
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        // Refused before its request is recorded, so a later cancel of it finds nothing it could stop.
        if (session.Runs.Values.Any(run => run.TerminalCompletedFlag != 2))
            throw new ConflictException("Wait for active prefill runs to finish before signing in.")
            { StageKey = "errors.prefill.prefillRunning" };

        if (loginId is { } id)
        {
            lock (session.PrefillLock)
            {
                if (session.LoginRequests.TryGetValue(id, out var request))
                {
                    if (request.Canceled || request.Attempt is not null)
                        throw new ConflictException("This login request is no longer active.")
                        { StageKey = "errors.prefill.loginInProgress" };
                }
                else
                {
                    session.LoginRequests.Add(id, new DaemonSession.LoginRequest());
                }
            }
        }

        // A person's sign-in takes over from the automatic retry: stop it, and wait until an attempt it has
        // in flight has ended its daemon-side login and freed the lock.
        Task? signInAgain;
        CancellationTokenSource? takeOver;
        lock (session.PrefillLock)
        {
            signInAgain = session.SignInAgainAfterDrop;
            takeOver = session.SignInAgainCancel;
        }
        // Both waits sit before the try that ends a request whose sign-in never began. An abort here has to
        // end the request too, or a later cancel still reads it as a Log in on its way to a sign-in.
        var loginWaitsFinished = false;
        try
        {
            if (signInAgain is not null)
            {
                try
                {
                    if (!signInAgain.IsCompleted)
                    {
                        takeOver!.Cancel();
                        await signInAgain.WaitAsync(cancellationToken);
                    }
                }
                finally
                {
                    // However this request ends, a finished retry (it stopped on a challenge, or the wait ended) must
                    // not stay in its place: it would block every later retry, while an empty place lets the daemon's
                    // next drop code start a new one. A retry still ending its login keeps its place, so a second
                    // Log in waits for it. Some of the retry's own exits leave the place set; an applied sign-in, or a
                    // later Log in that finds it finished, empties it.
                    lock (session.PrefillLock)
                    {
                        if (signInAgain.IsCompleted && ReferenceEquals(session.SignInAgainAfterDrop, signInAgain))
                        {
                            session.SignInAgainAfterDrop = null;
                        }
                    }
                }
            }

            // Serializes login attempts on this session: overlapping calls would race the
            // daemon's single challenge/status stream and clobber each other's failure text/auth state.
            // A short bounded wait absorbs the common case where another attempt holds the lock only
            // briefly. An attempt still blocked after that is rejected rather than queued, so the caller
            // gets a bounded, unambiguous error instead of silently waiting behind one it doesn't know
            // about.
            if (!await session.LoginLock.WaitAsync(_loginLockWaitTimeout, cancellationToken))
            {
                // A refused Log in ends its request, as the finally below does for every later exit.
                lock (session.PrefillLock)
                {
                    if (loginId is { } refusedId) session.LoginRequests[refusedId].Canceled = true;
                }
                throw new ConflictException($"A login attempt is already in progress for session {sessionId}.")
                {
                    StageKey = "errors.prefill.loginInProgress",
                    Context = new() { ["sessionId"] = sessionId }
                };
            }

            loginWaitsFinished = true;
        }
        finally
        {
            if (!loginWaitsFinished && loginId is { } abortedId)
            {
                lock (session.PrefillLock)
                {
                    if (session.LoginRequests.TryGetValue(abortedId, out var aborted) && aborted.Attempt is null)
                        aborted.Canceled = true;
                }
            }
        }

        var abandonedLoginCleanup = new AbandonedLoginCleanupHolder();
        var loginRegistered = false;
        try
        {
            EnsureCurrentSession(session);
            lock (session.PrefillLock)
            {
                // An unconfirmed cancel marked this daemon for replacement; it gets no more sign-in commands.
                if (session.Status == DaemonSessionStatus.Error)
                {
                    throw new ConflictException($"Persistent session {session.Id} needs to be started again.")
                    {
                        StageKey = "errors.prefill.sessionReplaced",
                        Context = new() { ["sessionId"] = session.Id }
                    };
                }
                if (session.LoginCanceling)
                {
                    throw new ConflictException("Wait for login cancellation to finish.")
                    {
                        StageKey = "errors.prefill.loginInProgress"
                    };
                }
                ThrowIfLoginRevoked(session, loginId);
            }
            // A sign-in whose prompt was answered but which never ended (its browser's cancel was lost) still runs on the
            // daemon, and the daemon names no attempt when it refuses one. Its owner's new start ends it here, before this
            // start takes a new attempt number, so a late refusal ends that sign-in and never the one starting now. Its ending
            // is announced under its own attempt number, which the dialog that started this sign-in does not take as its own.
            bool unfinished;
            long unfinishedAttempt;
            Guid? unfinishedRun;
            lock (session.PrefillLock)
            {
                unfinished = session.LoginOperationId is not null
                    && session.PendingLoginChallenge is null
                    && session.AuthState is not (DaemonAuthState.Authenticated or DaemonAuthState.NotAuthenticated);
                unfinishedAttempt = session.LoginAttempt;
                unfinishedRun = session.LoginOperationId;
            }
            if (unfinished)
            {
                // Another person's sign-in on a shared container is theirs to finish or cancel. A run the tracker no longer
                // holds was ended on its card, so no holder is left whose sign-in this start would take.
                if (_operationTracker?.GetOperation(unfinishedRun!.Value) is { } existingRun
                    && existingRun.OwnerSessionId != (ownerSessionId ?? session.UserId))
                {
                    throw new ConflictException($"A login attempt is already in progress for session {sessionId}.")
                    {
                        StageKey = "errors.prefill.loginInProgress",
                        Context = new() { ["sessionId"] = sessionId }
                    };
                }
                bool markedBeforeCancel;
                lock (session.PrefillLock)
                {
                    markedBeforeCancel = session.LoginCanceledByPerson;
                }
                try
                {
                    await CancelLoginAsync(sessionId, cancellationToken, loginAttempt: unfinishedAttempt);
                }
                catch when (!markedBeforeCancel)
                {
                    // This Log in made the cancel, not a press of Cancel. One the daemon did not answer leaves the earlier
                    // sign-in running, and a mark left behind would keep it signed out once it finishes. A person who
                    // canceled this Log in meanwhile did press Cancel, so their mark stays.
                    var cleared = false;
                    lock (session.PrefillLock)
                    {
                        if (loginId is not { } ownId || !session.LoginRequests[ownId].Canceled)
                        {
                            session.LoginCanceledByPerson = false;
                            session.LoggedInWhilePersonCanceled = false;
                            cleared = true;
                        }
                    }
                    if (cleared) await SaveLoginStateAsync(session);
                    throw;
                }
            }
            // A person's cancel can keep a logged-in daemon shown as signed out; the witness records it. This Log in
            // signs in the account the person chooses now, so the account the daemon still holds is logged out while
            // the mark still keeps its logged-in reports from signing the container in.
            bool keptLoggedIn;
            lock (session.PrefillLock)
            {
                keptLoggedIn = session.LoginCanceledByPerson && session.LoggedInWhilePersonCanceled;
            }
            // A logout that did not land leaves that account on the daemon, so the mark and the witness stay and this
            // Log in is refused instead of letting the held account sign the container in.
            if (keptLoggedIn
                && !await TryBestEffortLogoutAsync(session, "Log in while a person's cancel kept the daemon signed out"))
            {
                throw new ConflictException($"The account held on session {sessionId} could not be logged out.")
                {
                    StageKey = "errors.prefill.heldAccountNotLoggedOut",
                    Context = new() { ["sessionId"] = sessionId }
                };
            }
            // A sign-in whose cleanup cancel the daemon did not acknowledge may still be running there, and the daemon
            // would refuse this one or answer it with that sign-in's prompt. It is ended first, before any operation is
            // registered; the saved login cancels its own way.
            if (!reuseIntegration && session.LoginCancelUnconfirmed)
            {
                if (!await session.Client.CancelLoginWithOutcomeAsync(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new ConflictException("A login attempt is already in progress for this session.")
                    {
                        StageKey = "errors.prefill.loginInProgress"
                    };
                }
                session.LoginCancelUnconfirmed = false;
            }
            // This sign-in is the person's new attempt: an earlier cancel no longer speaks for it.
            bool marksCleared;
            lock (session.PrefillLock)
            {
                // A cancel of this Log in that landed while the unfinished sign-in above ended set the mark; it stays.
                ThrowIfLoginRevoked(session, loginId);
                marksCleared = session.LoginCanceledByPerson || session.LoggedInWhilePersonCanceled
                    || session.SignedOut || _loginStateRowsOutOfDate.ContainsKey(session.Id);
                session.LoginCanceledByPerson = false;
                session.LoggedInWhilePersonCanceled = false;
                session.SignedOut = false;
            }
            if (marksCleared)
            {
                await SaveLoginStateAsync(session);
            }
            session.PreserveLoginExpiry = false;
            session.LoginSettled = false;
            // A hub session is created for the caller's own auth session, so its UserId names the
            // caller; the persistent entry points pass the caller explicitly.
            RegisterLoginOperation(session, ownerSessionId ?? session.UserId);
            loginRegistered = true;

            CredentialChallenge? challenge;
            if (reuseIntegration)
            {
                challenge = await ReuseIntegrationLoginCoreAsync(
                    session,
                    accountId,
                    onCommandDispatched
                        ?? throw new ArgumentNullException(nameof(onCommandDispatched)),
                    cancellationToken,
                    lease!,
                    loginId);
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
                    cancellationToken,
                    loginId);
                challenge = result.Challenge;
            }

            // The challenge is the one object both interactive entry paths already hand the browser,
            // so it carries the login operation id out. Integration reuse never returns a challenge.
            if (challenge is not null)
            {
                challenge.OperationId = session.LoginOperationId?.ToString();
                challenge.LoginAttempt = session.LoginAttempt;
            }

            return challenge;
        }
        finally
        {
            lock (session.PrefillLock)
            {
                session.LoginDispatch?.TrySetResult(false);
                // A Log in that ends before its sign-in began (refused, failed, or with nothing to sign in) leaves
                // nothing running, so a later cancel of it is not a person's cancel of a sign-in.
                if (loginId is { } endedId && session.LoginRequests[endedId] is { Attempt: null } ended) ended.Canceled = true;
            }
            // Only a signed-in or signed-out session has finished its sign-in. LoggingIn and the prompt
            // states (a username, password, code or link the person still has to answer) are mid-flow, and
            // the auth-state funnel closes the card when they finish.
            if (loginRegistered && session.AuthState is (DaemonAuthState.Authenticated or DaemonAuthState.NotAuthenticated))
            {
                CompleteLoginOperation(session);
            }

            ReleaseLoginLockAfterCleanup(session, abandonedLoginCleanup);
        }
    }

    private async Task<CredentialChallenge?> ReuseIntegrationLoginCoreAsync(
        DaemonSession session,
        Guid? accountId,
        Action onCommandDispatched,
        CancellationToken cancellationToken,
        IntegrationLease lease,
        Guid? loginId)
    {
        lease.Validate();
        var availability = GetIntegrationLoginAvailability(accountId, lease.Caller);
        if (!availability.Available) IntegrationLease.Refuse(availability.Reason);
        var integrationDisplayName = availability.Account;
        lock (session.PrefillLock)
        {
            ThrowIfLoginRevoked(session, loginId);
            session.LastLoginFailureMessage = null;
            session.LastLoginFailureKey = null;
            session.LastConsumedLoginChallengeId = null;
            ClearPendingLoginChallenge(session);
        }

        var signOutCount = session.SignOutCount;
        var currentStatus = await session.Client.GetStatusAsync(cancellationToken);
        lock (session.PrefillLock)
        {
            ThrowIfLoginRevoked(session, loginId);
        }
        if (currentStatus?.Status == "logged-in")
        {
            await ApplyStatusAsync(session, currentStatus, signOutCount);
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

        var challengePublicationWasSuppressed = session.SuppressLoginChallengePublication;
        session.SuppressLoginChallengePublication = true;
        try
        {
            lease.Validate();
            lock (session.PrefillLock)
            {
                if (session.LoginCanceling)
                {
                    throw new ConflictException("Wait for login cancellation to finish.")
                    {
                        StageKey = "errors.prefill.loginInProgress"
                    };
                }
                ThrowIfLoginRevoked(session, loginId);
            }
            if (!await session.Client.CancelLoginWithOutcomeAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new ConflictException("A login attempt is already in progress for this session.")
                {
                    StageKey = "errors.prefill.loginInProgress"
                };
            }
            EnsureCurrentSession(session);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
        finally
        {
            session.SuppressLoginChallengePublication = challengePublicationWasSuppressed;
        }

        challengePublicationWasSuppressed = session.SuppressLoginChallengePublication;
        session.SuppressLoginChallengePublication = true;
        try
        {
            lock (session.PrefillLock)
            {
                if (session.LoginCanceling)
                {
                    throw new ConflictException("Wait for login cancellation to finish.")
                    {
                        StageKey = "errors.prefill.loginInProgress"
                    };
                }
                BindLoginRequest(session, loginId, session.LoginAttempt + 1);
                session.LoginAttempt++;
                session.LoginCancelTask = null;
                session.LoginDispatch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            session.AuthState = DaemonAuthState.LoggingIn;
            await NotifyAuthStateChangeAsync(session);
        }
        catch
        {
            session.SuppressLoginChallengePublication = challengePublicationWasSuppressed;
            lease.Dispose();
            throw;
        }

        var completion = new TaskCompletionSource<DaemonStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatched = false;
        var authenticated = false;
        Task OnLoginStatus(DaemonStatus status)
        {
            if (status.Status == "logged-in" || TryGetLoginFailureMessage(status, out _))
            {
                completion.TrySetResult(status);
            }
            return Task.CompletedTask;
        }
        Task OnLoginDisconnected()
        {
            completion.TrySetResult(new DaemonStatus
            {
                Status = "awaiting-login",
                Message = "Login failed: The daemon disconnected during saved login."
            });
            return Task.CompletedTask;
        }
        session.Client.OnStatusUpdate += OnLoginStatus;
        session.Client.OnDisconnected += OnLoginDisconnected;

        try
        {
            EnsureCurrentSession(session);
            bool accepted;
            try
            {
                lease.Validate();
                lock (session.PrefillLock)
                {
                    ThrowIfLoginRevoked(session, loginId);
                }
                accepted = await ReuseIntegrationLoginAsync(
                    session,
                    accountId,
                    () =>
                    {
                        dispatched = true;
                        lock (session.PrefillLock)
                        {
                            session.LoginDispatch?.TrySetResult(true);
                        }
                        onCommandDispatched();
                    },
                    cancellationToken,
                    lease);
            }
            finally
            {
                lease.Dispose();
            }
            lock (session.PrefillLock)
            {
                ThrowIfLoginRevoked(session, loginId);
            }
            if (!accepted)
            {
                await FailLoginFastAsync(session, session.Id, "Integration login was rejected by the daemon.");
                return null;
            }

            EnsureCurrentSession(session);
            signOutCount = session.SignOutCount;
            var finalStatus = await session.Client.GetStatusAsync(cancellationToken);
            // Epic and Xbox acknowledge admission before their background authentication finishes.
            if (finalStatus?.Status != "logged-in")
            {
                finalStatus = await completion.Task.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken);
            }
            lock (session.PrefillLock)
            {
                ThrowIfLoginRevoked(session, loginId);
            }
            EnsureCurrentSession(session);
            if (finalStatus?.Status == "logged-in")
            {
                authenticated = true;
                if (Platform == PrefillPlatform.Xbox
                    && string.IsNullOrWhiteSpace(finalStatus.AccountDisplayName)
                    && string.IsNullOrWhiteSpace(finalStatus.DisplayName)
                    && !string.IsNullOrWhiteSpace(integrationDisplayName))
                {
                    finalStatus.AccountDisplayName = integrationDisplayName;
                }
                await ApplyStatusAsync(session, finalStatus, signOutCount);
                return null;
            }

            await FailLoginFastAsync(
                session,
                session.Id,
                finalStatus is not null && TryGetLoginFailureMessage(finalStatus, out var message)
                    ? message
                    : "Integration login completed without authenticating the daemon.");
            return null;
        }
        catch (OperationCanceledException)
        {
            session.LastLoginFailureMessage = null;
            session.LastLoginFailureKey = null;
            session.AuthState = DaemonAuthState.NotAuthenticated;
            ClearPendingLoginChallenge(session);
            await NotifyAuthStateChangeAsync(session);
            throw;
        }
        catch (TimeoutException)
        {
            session.LastLoginFailureKey = "prefill.persistent.loginTimedOut";
            await FailLoginFastAsync(session, session.Id, "Saved login timed out before the daemon authenticated.");
            return null;
        }
        catch
        {
            await FailLoginFastAsync(session, session.Id, "Integration login failed.");
            throw;
        }
        finally
        {
            session.Client.OnStatusUpdate -= OnLoginStatus;
            session.Client.OnDisconnected -= OnLoginDisconnected;
            // A cancel the daemon does not acknowledge may leave this sign-in running, so the next Log in ends it first.
            if (dispatched && !authenticated && !await session.Client.CancelLoginWithOutcomeAsync(CancellationToken.None))
                session.LoginCancelUnconfirmed = true;
            session.SuppressLoginChallengePublication = challengePublicationWasSuppressed;
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
        CancellationToken cancellationToken,
        Guid? loginId)
    {
        long loginAttempt;
        lock (session.PrefillLock)
        {
            if (session.LoginCanceling)
            {
                throw new ConflictException("Wait for login cancellation to finish.")
                {
                    StageKey = "errors.prefill.loginInProgress"
                };
            }
            ThrowIfLoginRevoked(session, loginId);
        }
        // Resume path: a challenge from an earlier StartLoginAsync call on this session is still
        // pending (e.g. the frontend closed/reopened the login modal, or a second request raced in
        // before the first one's challenge was consumed). Answer with the SAME challenge and issue NO
        // daemon command at all - the daemon would only reply "already in progress" without
        // re-emitting it, and the client's own StartLoginAsync destroys its queued copy the moment
        // it's invoked again, so this check must run before anything touches session.Client.
        if (session.PendingLoginChallenge is { } pendingLoginChallenge && session.AuthState != DaemonAuthState.Authenticated)
        {
            lock (session.PrefillLock)
            {
                ThrowIfLoginRevoked(session, loginId);
                if (!ReferenceEquals(session.PendingLoginChallenge, pendingLoginChallenge))
                    throw new OperationCanceledException("Login challenge was cancelled.");
                BindLoginRequest(session, loginId, session.LoginAttempt);
            }
            _logger.LogInformation(
                "Session {SessionId} has a pending login challenge - resuming it instead of starting a new daemon login",
                sessionId);
            return LoginAttemptResult.ForChallenge(pendingLoginChallenge);
        }

        _logger.LogInformation("Starting login for session {SessionId}. ResponsesDir: {ResponsesDir}",
            sessionId, session.ResponsesDir);

        lock (session.PrefillLock)
        {
            if (session.LoginCanceling)
            {
                throw new ConflictException("Wait for login cancellation to finish.")
                {
                    StageKey = "errors.prefill.loginInProgress"
                };
            }
            ThrowIfLoginRevoked(session, loginId);
            BindLoginRequest(session, loginId, session.LoginAttempt + 1);
            session.LastLoginFailureMessage = null;
            session.LastLoginFailureKey = null;
            session.LastConsumedLoginChallengeId = null;
            session.LoginAttempt++;
            loginAttempt = session.LoginAttempt;
            session.LoginCancelTask = null;
            session.LoginDispatch = onCommandDispatched is null
                ? null
                : new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

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
                CredentialChallenge? existingChallenge;
                if (onCommandDispatched is null)
                {
                    existingChallenge = await AwaitChallengeOrLoginFailureAsync(
                        session, session.Client.StartLoginAsync(timeout, cancellationToken), loginFailureTcs, abandonedLoginCleanup);
                }
                else
                {
                    existingChallenge = await AwaitChallengeOrLoginFailureAsync(
                        session,
                        StartLoginWithDispatchTrackingAsync(
                            session, timeout, onCommandDispatched, cancellationToken, DaemonAuthState.Authenticated),
                        loginFailureTcs,
                        abandonedLoginCleanup);
                }
                lock (session.PrefillLock)
                {
                    ThrowIfLoginRevoked(session, loginId);
                }
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

            lock (session.PrefillLock)
            {
                ThrowIfLoginRevoked(session, loginId);
            }
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
            lock (session.PrefillLock)
            {
                ThrowIfLoginRevoked(session, loginId);
            }
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
                lock (session.PrefillLock)
                {
                    ThrowIfLoginRevoked(session, loginId);
                    if (session.LoginAttempt != loginAttempt || session.LoginCancelTask is not null)
                        throw new OperationCanceledException("Login request was cancelled.");
                    if (!session.SuppressLoginChallengePublication)
                        session.PendingLoginChallenge = challenge;
                }
                return LoginAttemptResult.ForChallenge(challenge);
            }

            // If login is already in progress, a challenge might already be queued.
            var pendingChallenge = await AwaitChallengeOrLoginFailureAsync(
                session, session.Client.WaitForChallengeAsync(TimeSpan.FromSeconds(10), cancellationToken), loginFailureTcs, abandonedLoginCleanup);
            lock (session.PrefillLock)
            {
                ThrowIfLoginRevoked(session, loginId);
            }
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
                lock (session.PrefillLock)
                {
                    ThrowIfLoginRevoked(session, loginId);
                    if (session.LoginAttempt != loginAttempt || session.LoginCancelTask is not null)
                        throw new OperationCanceledException("Login request was cancelled.");
                    if (!session.SuppressLoginChallengePublication)
                        session.PendingLoginChallenge = pendingChallenge;
                }
                return LoginAttemptResult.ForChallenge(pendingChallenge);
            }

            var signOutCount = session.SignOutCount;
            var status = await session.Client.GetStatusAsync(cancellationToken);
            if (status?.Status == "logged-in")
            {
                // Route through ApplyStatusAsync so username capture, ban re-enforcement, and
                // SessionUpdated run exactly once - do not also fire OnSessionAuthenticated here.
                await ApplyStatusAsync(session, status, signOutCount);

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
            signOutCount = session.SignOutCount;
            var finalStatus = await session.Client.GetStatusAsync(cancellationToken);
            if (finalStatus?.Status == "logged-in")
            {
                // Same single-path rule as the earlier already-logged-in branch.
                await ApplyStatusAsync(session, finalStatus, signOutCount);
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
        CancellationToken cancellationToken,
        DaemonAuthState restoreAuthState = DaemonAuthState.NotAuthenticated)
    {
        var commandDispatched = false;
        try
        {
            return await session.Client.StartLoginWithDispatchAsync(
                timeout,
                () =>
                {
                    commandDispatched = true;
                    lock (session.PrefillLock)
                    {
                        session.LoginDispatch?.TrySetResult(true);
                    }
                    onCommandDispatched();
                },
                cancellationToken);
        }
        catch
        {
            if (!commandDispatched)
            {
                // The state was moved to LoggingIn immediately before the transport call. If no
                // command reached the daemon, restore the pre-attempt state; the dispatch callback
                // never ran.
                session.AuthState = restoreAuthState;
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
        // A cancel the daemon does not acknowledge may leave this sign-in running, so the next Log in ends it first.
        if (!await session.Client.CancelLoginWithOutcomeAsync(CancellationToken.None))
            session.LoginCancelUnconfirmed = true;

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
        EnsureCurrentSession(session);
        session.LastLoginFailureMessage = failureMessage;
        session.AuthState = DaemonAuthState.NotAuthenticated;
        session.LoginSettled = true;
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

    private static void ThrowIfLoginRevoked(DaemonSession session, Guid? loginId)
    {
        if (session.LoginCanceling ||
            (loginId is { } id &&
             session.LoginRequests.TryGetValue(id, out var request) &&
             request.Canceled &&
             (request.Attempt is null || session.LoginCancelTask is not null)))
        {
            throw new OperationCanceledException("Login request was cancelled.");
        }
    }

    private static void BindLoginRequest(DaemonSession session, Guid? loginId, long attempt)
    {
        if (loginId is not { } id) return;
        var request = session.LoginRequests[id];
        if (request.Canceled || (request.Attempt is { } bound && bound != attempt))
            throw new OperationCanceledException("Login request was cancelled.");
        request.Attempt = attempt;
    }

    /// <summary>
    /// How the session's sign-in with this attempt number ended, once it ended. Null while it runs, and when a newer
    /// sign-in on the session has ended since.
    /// </summary>
    public PrefillLoginEnding? GetLoginEnding(DaemonSession session, long loginAttempt)
    {
        lock (session.PrefillLock)
        {
            return session.LastLoginEnding is { } ending && ending.LoginAttempt == loginAttempt ? ending : null;
        }
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

        EnsureCurrentSession(session);

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
            EnsureCurrentSession(session);
            session.AccountUsername = credential;
            session.Username = credential;

            // Update the database record with the username
            await _sessionService.SetUsernameAsync(sessionId, credential);

            // Do not log the username at Information level - it is PII. The value is
            // persisted via SetUsernameAsync above; keep only the session id in the log.
            _logger.LogDebug("Captured username for session {SessionId}", sessionId);

            // Send the session update to its own tabs and the account holders for real-time updates
            var updatedDto = DaemonSessionDto.FromSession(session);
            await NotifyHubAsync(session, EventSessionUpdated, updatedDto);
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

        EnsureCurrentSession(session);

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
            challenge.LoginAttempt = session.LoginAttempt;
        }

        return challenge;
    }

    /// <summary>
    /// Cancels a pending login attempt and resets auth state.
    /// Sends cancel-login command to the daemon to abort any pending credential waits.
    /// </summary>
    /// <param name="loginAttempt">
    /// The <see cref="DaemonSession.LoginAttempt"/> a browser cancel belongs to; null for a caller that ends
    /// whatever login is current. A cancel for an older attempt changes nothing.
    /// </param>
    /// <param name="stopReason">
    /// The reason an app-made cancel names. Set before the daemon round trip, because the daemon announces the
    /// ended sign-in before it answers; cleared when this call fails, never set by a call that joins another cancel,
    /// and ignored (nothing is canceled) when the sign-in already ended.
    /// </param>
    /// <returns>False when nothing was cancelled: <paramref name="loginAttempt"/> names an older attempt, an app-made cancel found the sign-in already ended, or the sign-in finished while the cancel was on its way.</returns>
    public async Task<bool> CancelLoginAsync(string sessionId, CancellationToken cancellationToken = default,
        long? loginAttempt = null, Guid? loginId = null, string? stopReason = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }
        if (loginId is { } startId)
        {
            // A person's cancel of a Log in that has not begun its sign-in. An automatic sign-in it waits behind
            // keeps running, and the mark keeps the container signed out if that finishes. The attempt token stays
            // so a login command already in flight still reports its status. A Log in the server has not seen, or
            // one already ended, starts nothing: its id is kept only to refuse a late start.
            bool startPending;
            var startMarked = false;
            lock (session.PrefillLock)
            {
                EnsureCurrentSession(session);
                startPending = !session.LoginRequests.TryGetValue(startId, out var startRequest) || startRequest.Attempt is null;
                if (startPending)
                {
                    if (startRequest is null)
                    {
                        session.LoginRequests.Add(startId, new DaemonSession.LoginRequest { Canceled = true });
                    }
                    else
                    {
                        // A named reason is the browser's sign-in clock (or the sweep), not the person's Cancel.
                        startMarked = stopReason is null
                            && !startRequest.Canceled
                            && session.AuthState != DaemonAuthState.Authenticated;
                        startRequest.Canceled = true;
                    }
                    if (startMarked)
                    {
                        RememberPersonCancel(session);
                    }
                }
            }
            if (startPending)
            {
                if (startMarked) await SaveLoginStateAsync(session);
                return false;
            }
        }

        var client = session.Client;
        long attempt;
        bool settledBeforeCancel;
        TaskCompletionSource<bool>? completion = null;
        Task<bool>? joined = null;
        Task<bool>? dispatch = null;
        bool personCanceled;
        CancellationTokenSource? retryStop = null;
        lock (session.PrefillLock)
        {
            EnsureCurrentSession(session);
            attempt = session.LoginAttempt;
            if (loginId is { } id)
            {
                // A request with no attempt yet was answered above, and a bound attempt is never unbound.
                var request = session.LoginRequests[id];
                if (request.Attempt != attempt)
                {
                    request.Canceled = true;
                    return false;
                }
                if (loginAttempt is not null && loginAttempt != request.Attempt)
                {
                    return false;
                }
            }
            if (loginAttempt is not null && loginAttempt != attempt)
            {
                _logger.LogInformation(
                    "Ignoring cancel-login for session {SessionId}: attempt {Expected} is no longer current (current {Actual})",
                    sessionId, loginAttempt, attempt);
                return false;
            }
            // An app-made cancel ends only a sign-in that still waits: one that ended between the sweep's check and this
            // lock (signed in, or refused) is left as it is, so the app neither signs it out nor leaves a reason behind.
            if (stopReason is not null &&
                session.AuthState is DaemonAuthState.Authenticated or DaemonAuthState.NotAuthenticated)
            {
                return false;
            }
            // The daemon writes the drop code for any cancel of a sign-in with no prompt open, so a person's cancel
            // (no reason) is marked: that code must not start the automatic sign-in, and a retry already waiting
            // stops. A sign-in that already finished keeps no mark, so its next drop still retries, and
            // a cancel the automatic sign-in meets is not a person's. A person's "Use my saved login" also suppresses
            // challenges, so the suppress flag cannot tell the two apart.
            personCanceled = stopReason is null
                && session.AuthState != DaemonAuthState.Authenticated
                && !session.SigningInAutomatically;
            if (personCanceled)
            {
                RememberPersonCancel(session);
                retryStop = session.SignInAgainCancel;
            }
            if (session.LoginCancelTask is { } running)
            {
                // A person's cancel that joins one the app started is still theirs.
                joined = running;
                settledBeforeCancel = session.LoginSettled;
            }
            else
            {
                foreach (var request in session.LoginRequests.Values)
                {
                    if (request.Attempt == attempt) request.Canceled = true;
                }
                completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                session.LoginCancelTask = completion.Task;
                session.LoginCanceling = true;
                // The daemon announces the ended sign-in before it answers the cancel, so the reason has to be
                // on the session before the round trip for that announcement to end the sign-in with it.
                if (stopReason is not null) session.LoginStopReason = stopReason;
                settledBeforeCancel = session.LoginSettled;
                session.LoginSettled = false;
                dispatch = session.LoginDispatch?.Task;
            }
        }

        retryStop?.Cancel();
        if (joined is not null)
        {
            if (personCanceled) await SaveLoginStateAsync(session);
            return await joined.WaitAsync(cancellationToken);
        }

        Exception? failure = null;
        var signedInMeanwhile = false;
        try
        {
            _logger.LogInformation("Cancelling login for session {SessionId}", sessionId);

            // Clear the resume cache and any queued daemon-side challenge FIRST, before the (possibly
            // slow) daemon cancel round-trip below. CancelLoginAsync takes no lock, so a concurrent
            // resume/poll landing during that await would otherwise be able to serve a challenge that is
            // being cancelled out from under it - a cancelled login must never be resumable, not even for
            // the few ms the daemon round-trip takes. Captured first so it can be restored if the daemon
            // round-trip itself fails below - a failed cancel must not be treated as a successful one.
            var capturedPendingChallenge = ClearPendingLoginChallenge(session);
            session.Client.ClearPendingChallenges();

            // The daemon did not confirm the cancel, so it may still believe this login is in progress.
            // Restore the cached challenge so a later StartLoginAsync resumes it instead of racing a
            // brand-new daemon login against the one that was never actually cancelled - the exact
            // duplicate-login race the resume cache exists to prevent.
            void RestorePendingChallenge()
            {
                lock (session.PrefillLock)
                {
                    if (session.LoginAttempt == attempt
                        && IsSessionLive(session)
                        && ReferenceEquals(session.Client, client)
                        && !session.AdmissionClosed)
                    {
                        // A sign-in that ended while the cancel went unanswered, signed in or not, owes no challenge and is
                        // settled.
                        if (session.AuthState is DaemonAuthState.Authenticated or DaemonAuthState.NotAuthenticated)
                        {
                            session.LoginSettled = true;
                        }
                        else
                        {
                            if (session.PendingLoginChallenge is null)
                            {
                                session.PendingLoginChallenge = capturedPendingChallenge;
                            }
                            session.LoginSettled = settledBeforeCancel;
                        }
                    }
                }
            }

            bool acknowledged;
            try
            {
                // Stored before the daemon writes its drop code for this cancel, so a manager restart in between
                // still finds the mark. A write that still fails after its retry leaves the mark in memory, and
                // the cancel still reaches the daemon.
                if (personCanceled)
                {
                    await SaveLoginStateAsync(session);
                }
                // Send cancel-login command to daemon - this will abort any pending credential waits
                acknowledged = dispatch is null || await dispatch.WaitAsync(cancellationToken)
                    ? await session.Client.CancelLoginWithOutcomeAsync(cancellationToken)
                    : true;
            }
            catch (Exception ex)
            {
                RestorePendingChallenge();
                _logger.LogWarning(ex, "Error sending cancel-login to daemon for session {SessionId}; login was not cancelled", sessionId);
                throw;
            }
            // A refused or timed-out cancel of a container login is a failed cancel, not a successful one;
            // the scheduled prefill dialog reports it. Guest sessions keep their best-effort cancel. A person's
            // cancel that met a sign-in the daemon already finished is done: the mark holds the container signed
            // out and the witness lets the next real drop sign in, so the daemon has nothing left to answer.
            bool finishedUnderMark;
            lock (session.PrefillLock)
            {
                finishedUnderMark = personCanceled && session.LoggedInWhilePersonCanceled;
            }
            if (!acknowledged && session.IsPersistent && !finishedUnderMark)
            {
                RestorePendingChallenge();
                _logger.LogWarning("Daemon did not acknowledge cancel-login for session {SessionId}; login was not cancelled", sessionId);
                throw new ConflictException($"Login for session {session.Id} was not cancelled.")
                {
                    StageKey = "prefill.persistent.loginNotCanceled",
                    Context = new() { ["sessionId"] = session.Id }
                };
            }

            // A newer attempt started while the daemon answered; it owns the auth state now.
            bool newerAttempt;
            lock (session.PrefillLock)
            {
                newerAttempt = session.LoginAttempt != attempt;
            }
            if (newerAttempt)
            {
                _logger.LogInformation(
                    "Login cancelled for session {SessionId}; a newer attempt started meanwhile, auth state left to it",
                    sessionId);
            }
            else
            {
                // Reset auth state to allow a new login attempt
                EnsureCurrentSession(session);
                if (!ReferenceEquals(session.Client, client))
                {
                    throw new ConflictException($"Persistent session {session.Id} was replaced.")
                    {
                        StageKey = "errors.prefill.sessionReplaced",
                        Context = new() { ["sessionId"] = session.Id }
                    };
                }
                // A daemon whose sign-in finished in the same instant can answer the cancel before its logged-in
                // announcement; its status already reads signed in by then, so a session not yet signed in asks once.
                bool signedInBeforeAnswer;
                lock (session.PrefillLock)
                {
                    signedInBeforeAnswer = session.AuthState == DaemonAuthState.Authenticated;
                }
                if (!signedInBeforeAnswer)
                {
                    DaemonStatus? daemonStatus = null;
                    var signOutCount = session.SignOutCount;
                    try
                    {
                        daemonStatus = await session.Client.GetStatusAsync(cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not read the daemon status after cancel-login for session {SessionId}", sessionId);
                    }

                    if (daemonStatus is { Status: "logged-in" })
                    {
                        await ApplyStatusAsync(session, daemonStatus, signOutCount);
                        // A cancel cannot undo a sign-in the daemon already finished, so no drop code for this cancel
                        // follows. The person's mark keeps the container signed out, and the next real drop signs in once.
                        var witnessArmed = false;
                        lock (session.PrefillLock)
                        {
                            if (session.LoginCanceledByPerson && !session.LoggedInWhilePersonCanceled)
                            {
                                session.LoggedInWhilePersonCanceled = true;
                                witnessArmed = true;
                            }
                        }
                        if (witnessArmed)
                        {
                            await SaveLoginStateAsync(session);
                        }
                    }
                }
                // A daemon whose sign-in finished during the round trip leaves a finished sign-in alone when the cancel
                // reaches it, so a cancel the app made leaves the session signed in instead of disagreeing with the
                // daemon, and a person's mark keeps it signed out. Either way the sign-in is over, so the session is
                // settled.
                lock (session.PrefillLock)
                {
                    signedInMeanwhile = session.AuthState == DaemonAuthState.Authenticated;
                    if (!signedInMeanwhile)
                    {
                        session.AuthState = DaemonAuthState.NotAuthenticated;
                    }
                    session.LoginSettled = true;
                }
                if (!signedInMeanwhile)
                {
                    await NotifyAuthStateChangeAsync(session);
                    _logger.LogInformation("Login cancelled for session {SessionId}, ready for new attempt", sessionId);
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            lock (session.PrefillLock)
            {
                session.LoginCanceling = false;
                // A cancel that failed did not end the sign-in, so its reason must not end a later one.
                if (failure is not null && stopReason is not null) session.LoginStopReason = null;
                // A person's cancel mark stays: they canceled a running sign-in, and only their accepted Log in or a
                // real connection drop after the daemon reached signed-in clears it.
                if (failure is not null && ReferenceEquals(session.LoginCancelTask, completion!.Task))
                    session.LoginCancelTask = null;
            }
            if (failure is null) completion!.TrySetResult(!signedInMeanwhile);
            else completion!.TrySetException(failure);
        }
        return await completion!.Task;
    }

    /// <summary>
    /// Attempts to log a RUNNING persistent session out in place via the daemon's <c>logout</c>
    /// command, without tearing the container down. IsPersistent-gated defense in depth, mirroring
    /// the other persistent-only operations in this class. On daemon success, the session is put
    /// through the SAME auth-state funnel every other login/logout transition uses
    /// (<see cref="NotifyAuthStateChangeAsync"/>) so subscribers see the account was forgotten, its
    /// cached resume challenge is cleared, and <see cref="DaemonSession.NeedsRelogin"/> is reset. On
    /// failure - the daemon reports failure, or the round-trip itself throws (socket error, timeout)
    /// - this performs no teardown and returns a not-forgotten result while the container keeps its
    /// last confirmed authentication state. A logout the daemon confirms stores the log-off mark
    /// (<see cref="DaemonSession.SignedOut"/>), so a manager restart or a recreate does not sign the account in
    /// again, also when an older steam/epic daemon image reported success while keeping its stored account file.
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

        var client = session.Client;
        lock (session.PrefillLock)
        {
            EnsureCurrentSession(session);
            session.LoginSettled = false;
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (IsSessionLive(session) && ReferenceEquals(session.Client, client))
                {
                    session.AdmissionClosed = false;
                    session.LoginSettled = true;
                }
                throw;
            }
            catch (Exception ex)
            {
                // A run whose end the daemon has not confirmed may still be downloading, so the logout is not sent.
                _logger.LogInformation(ex,
                    "Daemon did not confirm the prefill cancel before logout for persistent session {SessionId}; the container remains running",
                    sessionId);
                if (IsSessionLive(session) && ReferenceEquals(session.Client, client))
                {
                    session.AdmissionClosed = false;
                    session.LoginSettled = true;
                }
                return new PersistentLogoutResult(false);
            }
        }

        ClearPendingLoginChallenge(session);
        client.ClearPendingChallenges();

        LogoutOutcome outcome;
        try
        {
            outcome = await client.LogoutWithReasonAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (IsSessionLive(session) && ReferenceEquals(session.Client, client))
            {
                session.AdmissionClosed = false;
                session.LoginSettled = true;
            }
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex,
                "Daemon logout command failed for persistent session {SessionId}; the container remains running",
                sessionId);
            if (IsSessionLive(session) && ReferenceEquals(session.Client, client))
            {
                session.AdmissionClosed = false;
                session.LoginSettled = true;
            }
            return new PersistentLogoutResult(false);
        }

        if (!outcome.Success)
        {
            if (IsSessionLive(session) && ReferenceEquals(session.Client, client))
            {
                session.AdmissionClosed = false;
                session.LoginSettled = true;
            }
            if (outcome.RequiresLogin)
            {
                // Older daemon image's pre-login command gate rejected "logout" outright because this
                // session hasn't finished authenticating - not a genuine failure. Still returns
                // forgotten=false so the caller can report that the daemon did not confirm logout.
                _logger.LogInformation(
                    "Daemon declined logout for persistent session {SessionId} before authentication completed " +
                    "(older daemon image); nothing to log out",
                    sessionId);
            }
            else
            {
                _logger.LogInformation(
                    "Daemon reported logout failed for persistent session {SessionId}; the container remains running",
                    sessionId);
            }
            return new PersistentLogoutResult(false);
        }

        // Only this logout's own run cancel closed admission: an image replacement needs LoginSettled,
        // which this logout cleared first, so reopening it leaves EnsureCurrentSession testing replacement.
        lock (session.PrefillLock) session.AdmissionClosed = false;
        EnsureCurrentSession(session);
        if (!ReferenceEquals(session.Client, client))
        {
            return new PersistentLogoutResult(false);
        }
        lock (session.PrefillLock)
        {
            session.SignOutCount++;
            session.AuthState = DaemonAuthState.NotAuthenticated;
        }
        session.NeedsRelogin = false;
        lock (session.PrefillLock) session.SignedOut = true;
        await SaveLoginStateAsync(session);
        session.LoginSettled = true;
        await NotifyAuthStateChangeAsync(session);

        _logger.LogInformation("Session {SessionId} logged out in place; daemon forgot its stored account", sessionId);
        return new PersistentLogoutResult(true);
    }

    /// <summary>
    /// Removes this service's persistent auth volume when no session is currently running for it -
    /// used by the admin "clear all logins" endpoint (<c>POST .../persistent/clear-logins</c>) to
    /// forget a STOPPED service's stored login even though there is no live session/socket to send a
    /// <c>logout</c> command to. Returns a typed result when the volume is missing, still attached to a
    /// container, or Docker does not answer in time; throws on any other Docker error.
    /// </summary>
    // virtual: the escalation in ForgetRunningPersistentLoginAsync calls this, and unit tests override
    // it to exercise the hard-remove path without a live Docker daemon.
    public virtual async Task<PersistentVolumeClearResult> ClearPersistentAuthVolumeAsync(CancellationToken cancellationToken = default)
    {
        await ConnectDockerAsync(cancellationToken);
        if (!_containerGateway.IsAvailable)
        {
            return PersistentVolumeClearResult.DockerUnavailable;
        }

        // With deterministic naming a STOPPED
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
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Docker did not answer the removal of persistent {ServiceName} auth volume {Volume}", ServiceName, volumeName);
            return PersistentVolumeClearResult.DockerUnavailable;
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

        // A Start, adopt or image pass waits until the container is gone and its volume cleared, so it never
        // removes the container under this teardown or mounts the volume this is erasing. The caller runs
        // after its adopt released the lock, and neither call below takes it.
        await _persistentStartLock.WaitAsync(cancellationToken);
        try
        {
            try
            {
                await TerminateSessionAsync(
                    sessionId,
                    reason: "Clear stored logins: hard-remove stale persistent login",
                    force: true,
                    terminatedBy: "admin");
            }
            catch (Exception ex)
            {
                // The container still holds the volume, so this service's login survives; the caller reports it
                // and goes on to the next service.
                _logger.LogWarning(ex,
                    "Could not terminate persistent session {SessionId} while clearing its stored login", sessionId);
                return PersistentRunningLoginClearOutcome.HardRemoveFailed;
            }

            var volumeResult = await ClearPersistentAuthVolumeAsync(cancellationToken);
            return volumeResult is PersistentVolumeClearResult.Removed or PersistentVolumeClearResult.NotFound
                ? PersistentRunningLoginClearOutcome.HardRemoved
                : PersistentRunningLoginClearOutcome.HardRemoveFailed;
        }
        finally
        {
            _persistentStartLock.Release();
        }
    }

    /// <summary>
    /// Force-removes any STOPPED
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
            // A list Docker does not answer in time is logged below like any failed list.
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

            try
            {
                if (await RemoveContainerForceAsync(match.ID, cancellationToken) == ContainerRemovalOutcome.Removed)
                {
                    _logger.LogInformation(
                        "Removed stopped persistent {ServiceName} container {Id} so its auth volume can be cleared",
                        ServiceName, ShortContainerId(match.ID));
                }
            }
            catch (ServiceUnavailableException)
            {
                // Logged by the helper; the volume remove reports whether the container still holds the volume.
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
        // Every service's gateway talks to the same Docker, so after one connect times out the later services
        // report it instead of waiting for the same timeout again.
        var dockerUnavailable = false;

        foreach (var service in Enum.GetValues<PrefillPlatform>())
        {
            var daemon = ResolveDaemon(provider, service);
            if (daemon is null)
            {
                continue;
            }

            var session = daemon.GetActivePersistentSession();
            try
            {
                // A container kept running after a startup Docker timeout has no session until it is adopted,
                // so its login is cleared through the daemon like any running container's.
                if (session is null && !dockerUnavailable)
                {
                    try
                    {
                        session = await daemon.AdoptRunningPersistentSessionAsync(cancellationToken);
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Docker unreachable or slow, a refused removal, or a pending image update whose new
                        // container failed: the adopt's failure is logged and the volume clear below answers
                        // for this service. A Docker that never connected answers here instead, since the
                        // volume clear would wait for another connect.
                        daemon._logger.LogWarning(ex,
                            "Could not adopt the running {Service} container while clearing its stored login", service);
                        dockerUnavailable = !daemon._containerGateway.IsAvailable;
                    }
                }

                if (session is null && dockerUnavailable)
                {
                    results.Add(new ClearPersistentLoginServiceResultDto
                    {
                        Service = service,
                        WasRunning = false,
                        Success = false,
                        Detail = PersistentVolumeClearResult.DockerUnavailable.ToString()
                    });
                    continue;
                }

                if (session is not null)
                {
                    // The daemon's logout is tried first and checked against its live status. When the login
                    // survived (an older image that reports success anyway, or a failed logout), the container
                    // is removed with its auth volume. The result says which happened.
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

                // A person's Stop whose teardown failed is finished first, so its session ends with the
                // Stop's own reason and the card that kept that Stop's error hears it end.
                if (daemon.GetFailedStopSession() is { } failedStop)
                {
                    await daemon.TerminateSessionAsync(failedStop.Id);
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
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // One service's Docker failure must not stop the others from being cleared.
                daemon._logger.LogWarning(ex, "Could not clear the stored {Service} login", service);
                results.Add(new ClearPersistentLoginServiceResultDto
                {
                    Service = service,
                    WasRunning = session is not null,
                    Success = false,
                    Detail = "remove-failed"
                });
            }
        }

        return results;
    }
}
