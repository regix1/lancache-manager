using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // A person's Log in while the container start's automatic sign-in still waits on the daemon stops that
    // sign-in and starts the person's at once, instead of waiting behind it and being refused.
    [Fact]
    public async Task APersonsLogInTakesOverTheStartupSignInAsync()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, DateTime.UtcNow.AddDays(30));
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(RunningPersistentContainer(sessionId));
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: UsernameChallenge("persons-login")) { HoldStartLogin = true };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true)), gateway, () => client);

        await daemon.StartAsync(CancellationToken.None).WaitAsync(TestTimeout);
        await client.StartLoginEntered.Task.WaitAsync(TestTimeout);
        client.HoldStartLogin = false;
        var session = daemon.GetActivePersistentSession()!;

        var refusal = await Record.ExceptionAsync(async () =>
        {
            var challenge = await daemon.StartLoginAsync(session.Id).WaitAsync(TestTimeout);
            Assert.Equal("persons-login", challenge!.ChallengeId);
        });

        Assert.Null(refusal);
        Assert.Equal(1, client.CancelLoginCallCount);
        Assert.Equal(2, client.StartLoginCallCount);
        Assert.Equal(DaemonSessionStatus.Active, session.Status);
    }

    // A temporary session whose cancel the daemon never answers while its sign-in still runs is ended and its
    // container removed, so the next Start builds a daemon that can sign in; nothing reports it ready. The cancel
    // reports the failure, and the guest's tab is told the session ended because the cancel did not land.
    [Fact]
    public async Task AnUnansweredCancelEndsATemporarySessionStillSigningInAsync()
    {
        var notifications = DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
        var (gateway, daemon, session, client, container) = NewTemporarySessionSigningIn(notifications);
        using var ownedDaemon = daemon;
        daemon.AddSubscriber(session.Id, "connection-1");

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => daemon.CancelLoginAsync(session.Id).WaitAsync(TestTimeout));

        Assert.Equal("errors.prefill.loginCancelUnconfirmed", refusal.StageKey);
        Assert.Equal("signalr.prefillSession.loginCancelUnconfirmed",
            SessionEndedReasonKey((EventRecordingNotificationsProxy)(object)notifications));
        Assert.DoesNotContain(daemon.GetAllSessions(), s => s.Id == session.Id);
        Assert.False(gateway.ContainsContainer(container.Id));
        Assert.Null(session.PendingLoginChallenge);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => daemon.StartLoginAsync(session.Id).WaitAsync(TestTimeout));
        Assert.Equal(1, client.StartLoginCallCount);
    }

    // A container login whose cancel goes unanswered while it still signs in ends its run as a failed sign-in,
    // the way the refusal and the container card already report it, not as a canceled one.
    [Fact]
    public async Task AnUnansweredCancelOfAContainerLoginEndsItsRunAsFailedAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        client.StatusHandler = _ =>
        {
            var status = SteamStatus(s.Instance, false);
            status.ErrorCode = null;
            return Task.FromResult<DaemonStatus?>(status);
        };
        UseOperationTracker(s.Daemon);
        client.NeverAuthenticates = true;
        await s.Daemon.StartPersistentLoginAsync(s.Session.Id, TimeSpan.FromSeconds(30), () => { }, Guid.NewGuid());
        Assert.NotNull(s.Session.LoginOperationId);
        var attempt = s.Session.LoginAttempt;

        client.CancelAcknowledged = false;
        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => s.Daemon.CancelLoginAsync(s.Session.Id, loginAttempt: attempt).WaitAsync(TestTimeout));

        Assert.Equal("errors.prefill.loginCancelUnconfirmed", refusal.StageKey);
        Assert.Equal(DaemonSessionStatus.Error, s.Session.Status);
        Assert.Equal(OperationStatus.Failed, s.Session.LastLoginEnding!.Status);
        Assert.Equal("errors.prefill.loginCancelUnconfirmed", s.Session.LastLoginEnding.StageKey);
    }

    // On Xbox a person's Log in during the container start's saved-login sign-in waits for that sign-in instead of
    // canceling it: the Xbox daemon erases its saved login when a cancel meets a login it is still running.
    [Fact]
    public async Task APersonsLogInWaitsForTheXboxStartupSignInInsteadOfCancelingItAsync()
    {
        var (daemon, client, session) = await StartXboxSigningInAsync(new ScriptedLoginDaemonClient { HoldStartLogin = true });
        using var ownedDaemon = daemon;

        var personsLogin = daemon.StartLoginAsync(session.Id);
        client.ReleaseStartLogin.SetResult();
        await personsLogin.WaitAsync(TestTimeout);

        Assert.Equal(0, client.CancelLoginCallCount);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
        Assert.Equal(DaemonSessionStatus.Active, session.Status);
    }

    // A startup sign-in that fails fast leaves its cleanup holding the lock; a person's Log in that waits on that
    // sign-in is not then refused at the lock.
    [Fact]
    public async Task APersonsLogInAfterAFailedStartupSignInWaitsForItsCleanupAsync()
    {
        var (daemon, client, session) = await StartXboxSigningInAsync(
            new ScriptedLoginDaemonClient { HoldStartLogin = true, HoldCancelLogin = true });
        using var ownedDaemon = daemon;
        await client.RaiseStatusAsync(new DaemonStatus { Status = "awaiting-login", Message = "Login failed: stored token unreadable", ErrorCode = "login-failed" });
        await client.CancelLoginEntered.Task.WaitAsync(TestTimeout);

        var refusal = await Record.ExceptionAsync(async () =>
        {
            var personsLogin = daemon.StartLoginAsync(session.Id);
            // Longer than the 3 s a Log in waits on a held lock.
            await Task.Delay(TimeSpan.FromSeconds(4));
            client.ReleaseCancelLogin.SetResult();
            client.ReleaseStartLogin.SetResult();
            await personsLogin.WaitAsync(TestTimeout);
        });

        Assert.Null(refusal);
    }

    // A person's Log in that takes over during the startup try's first status read is a normal takeover; it logs
    // no warning.
    [Fact]
    public async Task APersonsLogInDuringTheStartupStatusReadLogsNoWarningAsync()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, DateTime.UtcNow.AddDays(30));
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(RunningPersistentContainer(sessionId));
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdRead = true;
        TestSteamDaemon? owner = null;
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: UsernameChallenge("persons-login"));
        // Only the startup try's first read is held; the adoption reads before it run as usual.
        client.StatusHandler = async token =>
        {
            if (holdRead && owner?.GetActivePersistentSession()?.StartupSignIn is { IsCompleted: false })
            {
                holdRead = false;
                readEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return new DaemonStatus { Type = "status", Status = "awaiting-login", Timestamp = DateTime.UtcNow };
        };
        var logger = new CapturingLogger<SteamDaemonService>();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true)), gateway, () => client, logger);
        owner = daemon;

        await daemon.StartAsync(CancellationToken.None).WaitAsync(TestTimeout);
        await readEntered.Task.WaitAsync(TestTimeout);
        var session = daemon.GetActivePersistentSession()!;

        var challenge = await daemon.StartLoginAsync(session.Id).WaitAsync(TestTimeout);
        await daemon.LastHeadlessSelfAuthAttempt!.WaitAsync(TestTimeout);

        Assert.Equal("persons-login", challenge!.ChallengeId);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains(nameof(PrefillDaemonServiceBase.AttemptHeadlessPersistentSelfAuthAsync)));
    }

    // A Steam startup sign-in that a person's Log in takes over as it ends with no response leaves no sign-in-again
    // retry behind; the person's sign-in is the one that runs.
    [Fact]
    public async Task ATakenOverStartupSignInThatGetsNoResponseStartsNoRetryAsync()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, DateTime.UtcNow.AddDays(30));
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(RunningPersistentContainer(sessionId));
        // The daemon takes the login but neither prompts nor signs in, so the try ends with no response and ends its login.
        var client = new ScriptedLoginDaemonClient { NeverAuthenticates = true, HoldCancelLogin = true };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true)), gateway, () => client);

        await daemon.StartAsync(CancellationToken.None).WaitAsync(TestTimeout);
        await client.CancelLoginEntered.Task.WaitAsync(TestTimeout);
        var session = daemon.GetActivePersistentSession()!;

        var personsLogin = daemon.StartLoginAsync(session.Id);
        client.ReleaseCancelLogin.SetResult();
        await personsLogin.WaitAsync(TestTimeout);
        await daemon.LastHeadlessSelfAuthAttempt!.WaitAsync(TestTimeout);

        Assert.Null(session.SignInAgainAfterDrop);
    }

    // A Log in that first ends an unfinished sign-in, and finds that cancel unanswered, sends no new login to
    // the daemon it just gave up on.
    [Fact]
    public async Task ALogInWhoseCancelOfTheUnfinishedSignInGoesUnansweredSendsNoLoginAsync()
    {
        var (_, daemon, session, client, _) = NewTemporarySessionSigningIn();
        using var ownedDaemon = daemon;
        // The prompt was answered and the sign-in never ended.
        session.PendingLoginChallenge = null;
        session.LoginOperationId = Guid.NewGuid();

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => daemon.StartLoginAsync(session.Id).WaitAsync(TestTimeout));

        Assert.Equal("errors.prefill.loginCancelUnconfirmed", refusal.StageKey);
        Assert.Equal(1, client.StartLoginCallCount);
    }

    // An Xbox saved-login sign-in that outlasts the challenge and status waits is left to finish: the daemon's own push
    // of the outcome ends it, and no cancel-login reaches the daemon, which would erase its saved login.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASlowXboxStartupSignInRunsToTheDaemonsOutcomeAsync(bool signsIn)
    {
        var client = new ScriptedLoginDaemonClient { NeverAuthenticates = true };
        var thirdRead = ThirdStatusReadAfterLogin(client);
        var (daemon, _, session) = await StartXboxSigningInAsync(client);
        using var ownedDaemon = daemon;
        await Task.WhenAny(thirdRead, client.CancelLoginEntered.Task).WaitAsync(TestTimeout);

        await client.RaiseStatusAsync(signsIn
            ? XboxLoggedIn()
            : new DaemonStatus { Type = "status", Status = "awaiting-login", Message = "Login failed: Microsoft did not answer", ErrorCode = "login-failed", Timestamp = DateTime.UtcNow });
        await daemon.LastHeadlessSelfAuthAttempt!.WaitAsync(TestTimeout);

        Assert.Equal(0, client.CancelLoginCallCount);
        Assert.Equal(signsIn ? DaemonAuthState.Authenticated : DaemonAuthState.NotAuthenticated, session.AuthState);
        Assert.True(session.LoginSettled);
        if (!signsIn)
        {
            Assert.Equal("Login failed: Microsoft did not answer", session.LastLoginFailureMessage);
        }
    }

    // A person's Log in that takes over while the slow Xbox sign-in reads the daemon does not stop that read: the
    // sent login still runs to its outcome and no cancel-login is sent.
    [Fact]
    public async Task APersonsLogInDuringTheSlowXboxSignInsStatusReadSendsNoCancelAsync()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedLoginDaemonClient { NeverAuthenticates = true };
        var thirdRead = ThirdStatusReadAfterLogin(client, release.Task);
        var (daemon, _, session) = await StartXboxSigningInAsync(client);
        using var ownedDaemon = daemon;
        await Task.WhenAny(thirdRead, client.CancelLoginEntered.Task).WaitAsync(TestTimeout);
        var takeOver = session.SignInAgainCancel;

        var personsLogin = daemon.StartLoginAsync(session.Id);
        await WaitUntilAsync(() => takeOver is null || takeOver.IsCancellationRequested);
        release.SetResult();
        await client.RaiseStatusAsync(XboxLoggedIn());
        await personsLogin.WaitAsync(TestTimeout);

        Assert.Equal(0, client.CancelLoginCallCount);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
    }

    // A person's Cancel of a Log in that waits behind the slow Xbox sign-in sends no cancel-login while the daemon still
    // signs in; once the daemon reports signed in, the mark keeps the container signed out.
    [Fact]
    public async Task APersonsCancelDuringTheSlowXboxSignInSendsNoCancelUntilItEndsAsync()
    {
        var client = new ScriptedLoginDaemonClient { NeverAuthenticates = true };
        var thirdRead = ThirdStatusReadAfterLogin(client);
        var (daemon, _, session) = await StartXboxSigningInAsync(client);
        using var ownedDaemon = daemon;
        await Task.WhenAny(thirdRead, client.CancelLoginEntered.Task).WaitAsync(TestTimeout);
        var loginId = Guid.NewGuid();

        var personsLogin = daemon.StartPersistentLoginAsync(session.Id, TimeSpan.FromSeconds(30), () => { }, Guid.NewGuid(), loginId: loginId);
        await WaitUntilAsync(() =>
        {
            lock (session.PrefillLock) return session.LoginRequests.ContainsKey(loginId);
        });
        await daemon.CancelLoginAsync(session.Id, loginId: loginId).WaitAsync(TestTimeout);
        var cancelsBeforeOutcome = client.CancelLoginCallCount;
        await client.RaiseStatusAsync(XboxLoggedIn());
        await Record.ExceptionAsync(() => personsLogin.WaitAsync(TestTimeout));
        await daemon.LastHeadlessSelfAuthAttempt!.WaitAsync(TestTimeout);

        Assert.Equal(0, cancelsBeforeOutcome);
        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);
        Assert.True(session.LoginCanceledByPerson);
    }

    // A temporary session whose daemon socket drops while it has no prefill is ended and its container removed: no
    // sweep ends an errored session, so it would otherwise keep running past the guest's expiry.
    [Fact]
    public async Task ADroppedSocketEndsATemporarySessionAsync()
    {
        var notifications = DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        var container = new FakeContainer { Id = Guid.NewGuid().ToString("N"), Name = "steam-daemon-temporary", Running = true };
        gateway.AddContainer(container);
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true), notifications: notifications), gateway);
        var session = InjectedTemporarySteamSession(daemon, new ScriptedLoginDaemonClient());
        session.ContainerId = container.Id;
        session.ContainerName = container.Name;
        daemon.AddSubscriber(session.Id, "connection-1");

        await DaemonTestMethods.InvokePrivateHandlerAsync(daemon, "OnDisconnectedAsync", session, session.Client);
        await WaitUntilAsync(() => session.Status == DaemonSessionStatus.Terminated);

        Assert.DoesNotContain(daemon.GetAllSessions(), s => s.Id == session.Id);
        Assert.False(gateway.ContainsContainer(container.Id));
        Assert.Equal("signalr.prefillSession.daemonDisconnected",
            SessionEndedReasonKey((EventRecordingNotificationsProxy)(object)notifications));
    }

    // An Xbox saved-login sign-in whose daemon socket drops, after which a new daemon instance answers (at once, or after
    // a read that failed while it still restarted), ends as a failed sign-in with no cancel-login and frees the login
    // lock; the session stays up.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task AnXboxSavedSignInEndsWhenItsDaemonIsGoneAfterADropAsync(bool dropBeforeTheWait, bool restarted)
    {
        var instance = Guid.NewGuid().ToString("D");
        var newInstance = Guid.NewGuid().ToString("D");
        var dropped = false;
        var readsAfterDrop = 0;
        var clock = new CacheStatusClock(DateTimeOffset.UtcNow);
        var client = new ScriptedLoginDaemonClient { NeverAuthenticates = true, HoldStartLogin = dropBeforeTheWait };
        var thirdRead = ThirdXboxStatusReadAfterLogin(client, instance, () => dropped
            ? (true, restarted || Interlocked.Increment(ref readsAfterDrop) > 1 ? XboxSignedOut(newInstance) : null)
            : (false, null));
        var (daemon, _, session) = await StartXboxSigningInAsync(client, clock: clock);
        using var ownedDaemon = daemon;
        if (!dropBeforeTheWait)
        {
            await thirdRead.WaitAsync(TestTimeout);
        }

        dropped = true;
        await client.RaiseDisconnectedAsync();
        if (dropBeforeTheWait)
        {
            client.ReleaseStartLogin.SetResult();
        }
        var attempt = daemon.LastHeadlessSelfAuthAttempt!;
        // Steps the clock through the pauses between status reads, well short of the 2-minute bound.
        await WaitUntilAsync(() =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            return attempt.IsCompleted;
        });

        Assert.True(attempt.IsCompleted, "The sign-in still waits after its daemon went away.");
        Assert.Equal(0, client.CancelLoginCallCount);
        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);
        Assert.True(session.LoginSettled);
        Assert.Equal(1, session.LoginLock.CurrentCount);
        Assert.Equal(DaemonSessionStatus.Active, session.Status);
        if (!restarted)
        {
            Assert.True(session.Recovering);
        }
    }

    // A drop after which the same daemon answers leaves its saved-login sign-in running: the try keeps the login lock and
    // ends on the daemon's own push, with no cancel-login. That holds for a daemon that says its sign-in still runs and
    // for an older one that does not say.
    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public async Task AnXboxSavedSignInKeepsWaitingWhenTheSameDaemonAnswersAfterADropAsync(bool? loggingIn)
    {
        var instance = Guid.NewGuid().ToString("D");
        var dropped = false;
        var readsAfterDrop = 0;
        var client = new ScriptedLoginDaemonClient { NeverAuthenticates = true };
        var thirdRead = ThirdXboxStatusReadAfterLogin(client, instance, () =>
        {
            if (!dropped) return (false, null);
            Interlocked.Increment(ref readsAfterDrop);
            return (true, XboxSignedOut(instance, loggingIn));
        });
        var (daemon, _, session) = await StartXboxSigningInAsync(client);
        using var ownedDaemon = daemon;
        await thirdRead.WaitAsync(TestTimeout);
        var attempt = daemon.LastHeadlessSelfAuthAttempt!;

        dropped = true;
        await client.RaiseDisconnectedAsync();
        // The recovery pass and the sign-in each read the daemon once after the drop.
        await WaitUntilAsync(() => Volatile.Read(ref readsAfterDrop) >= 1);
        await Task.Delay(300);
        Assert.False(attempt.IsCompleted);
        Assert.Equal(0, session.LoginLock.CurrentCount);

        var signedIn = XboxSignedOut(instance);
        signedIn.Status = "logged-in";
        await client.RaiseStatusAsync(signedIn);
        await attempt.WaitAsync(TestTimeout);

        Assert.Equal(0, client.CancelLoginCallCount);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
        Assert.Equal(DaemonSessionStatus.Active, session.Status);
    }

    // A daemon that answers after a drop and says no sign-in runs has already ended the sign-in, and its outcome push was
    // lost while the socket was down: the try ends signed out at once, with no cancel-login, instead of waiting out its
    // bound.
    [Fact]
    public async Task AnXboxSavedSignInEndsWhenItsDaemonSaysNoSignInRunsAfterADropAsync()
    {
        var instance = Guid.NewGuid().ToString("D");
        var dropped = false;
        var clock = new CacheStatusClock(DateTimeOffset.UtcNow);
        var client = new ScriptedLoginDaemonClient { NeverAuthenticates = true };
        var thirdRead = ThirdXboxStatusReadAfterLogin(client, instance, () => Volatile.Read(ref dropped)
            ? (true, XboxSignedOut(instance, loggingIn: false))
            : (false, null));
        var (daemon, _, session) = await StartXboxSigningInAsync(client, clock: clock);
        using var ownedDaemon = daemon;
        await thirdRead.WaitAsync(TestTimeout);
        var attempt = daemon.LastHeadlessSelfAuthAttempt!;

        Volatile.Write(ref dropped, true);
        await client.RaiseDisconnectedAsync();
        // Steps the clock past the 5 s the sign-in gives a daemon that says no sign-in runs to push its outcome.
        await WaitUntilAsync(() =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            return attempt.IsCompleted;
        });

        Assert.True(await Task.WhenAny(attempt, Task.Delay(TestTimeout)) == attempt,
            "The sign-in still waits after its daemon said no sign-in runs.");
        Assert.Equal(0, client.CancelLoginCallCount);
        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);
        Assert.True(session.LoginSettled);
        Assert.Equal(1, session.LoginLock.CurrentCount);
        Assert.Equal(DaemonSessionStatus.Active, session.Status);
    }

    // A typed Log in after an Xbox saved-login sign-in that a drop ended cancels that sign-in before it sends its own
    // login, and a failure the earlier sign-in pushes before that cancel is answered does not end the new one.
    [Fact]
    public async Task ATypedLogInAfterADroppedXboxSavedSignInCancelsItFirstAsync()
    {
        var instance = Guid.NewGuid().ToString("D");
        var dropped = false;
        var clock = new CacheStatusClock(DateTimeOffset.UtcNow);
        var client = new ScriptedLoginDaemonClient { NeverAuthenticates = true };
        var thirdRead = ThirdXboxStatusReadAfterLogin(client, instance, () => Volatile.Read(ref dropped)
            ? (true, XboxSignedOut(instance, loggingIn: false))
            : (false, null));
        var (daemon, _, session) = await StartXboxSigningInAsync(client, clock: clock);
        using var ownedDaemon = daemon;
        await thirdRead.WaitAsync(TestTimeout);
        var attempt = daemon.LastHeadlessSelfAuthAttempt!;
        Volatile.Write(ref dropped, true);
        await client.RaiseDisconnectedAsync();
        await WaitUntilAsync(() =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            return attempt.IsCompleted;
        });
        await attempt.WaitAsync(TestTimeout);
        Assert.Equal(0, client.CancelLoginCallCount);

        // The daemon signs the typed Log in in once its login arrives.
        client.NeverAuthenticates = false;
        client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(
            client.StartLoginCallCount > 1 ? XboxLoggedIn() : XboxSignedOut(instance, loggingIn: false));
        client.HoldCancelLogin = true;
        var logIn = daemon.StartLoginAsync(session.Id);
        await Task.WhenAny(client.CancelLoginEntered.Task, logIn).WaitAsync(TestTimeout);
        Assert.True(client.CancelLoginEntered.Task.IsCompleted, "The typed Log in sent no cancel-login for the dropped sign-in.");
        Assert.Equal(1, client.StartLoginCallCount);

        var failed = XboxSignedOut(instance, loggingIn: false);
        failed.Message = "Login failed: the saved token was refused.";
        failed.ErrorCode = "login-failed";
        await client.RaiseStatusAsync(failed);
        client.ReleaseCancelLogin.SetResult();
        await logIn.WaitAsync(TestTimeout);

        Assert.Equal(1, client.CancelLoginCallCount);
        Assert.Equal(2, client.StartLoginCallCount);
        Assert.Null(session.LastLoginFailureMessage);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
        Assert.Equal(DaemonSessionStatus.Active, session.Status);
    }

    // A failure the daemon pushes while the login's last status read is still out ends the Xbox saved-login sign-in with
    // the daemon's own text, instead of a wait that runs out its bound and then sends a cancel-login.
    [Fact]
    public async Task AnXboxSavedSignInEndsOnAFailurePushedDuringTheLastStatusReadAsync()
    {
        var instance = Guid.NewGuid().ToString("D");
        const string failure = "Login failed: the saved token was refused.";
        var reads = 0;
        var heldRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new CacheStatusClock(DateTimeOffset.UtcNow);
        var client = new ScriptedLoginDaemonClient { NeverAuthenticates = true };
        client.StatusHandler = async _ =>
        {
            if (client.StartLoginCallCount > 0 && Interlocked.Increment(ref reads) == 2)
            {
                heldRead.TrySetResult();
                await releaseRead.Task;
            }
            return XboxSignedOut(instance);
        };
        var (daemon, _, session) = await StartXboxSigningInAsync(client, clock: clock);
        using var ownedDaemon = daemon;
        await heldRead.Task.WaitAsync(TestTimeout);
        var attempt = daemon.LastHeadlessSelfAuthAttempt!;

        var failed = XboxSignedOut(instance);
        failed.Message = failure;
        failed.ErrorCode = "login-failed";
        await client.RaiseStatusAsync(failed);
        releaseRead.SetResult();

        Assert.True(await Task.WhenAny(attempt, Task.Delay(TestTimeout)) == attempt,
            "The sign-in still waits after the daemon pushed its failure.");
        Assert.Equal(failure, session.LastLoginFailureMessage);
        Assert.Equal(0, client.CancelLoginCallCount);
        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);
        Assert.Equal(1, session.LoginLock.CurrentCount);
    }

    // A person's Log in that waits behind the Xbox saved-login sign-in goes on once that sign-in's daemon is gone, instead
    // of waiting out the sign-in's bound.
    [Fact]
    public async Task APersonsLogInBehindTheXboxSavedSignInGoesOnWhenItsDaemonIsGoneAsync()
    {
        var instance = Guid.NewGuid().ToString("D");
        var dropped = false;
        var client = new ScriptedLoginDaemonClient { NeverAuthenticates = true };
        var thirdRead = ThirdXboxStatusReadAfterLogin(client, instance, () => dropped
            ? (true, XboxSignedOut(Guid.NewGuid().ToString("D")))
            : (false, null));
        var (daemon, _, session) = await StartXboxSigningInAsync(client);
        using var ownedDaemon = daemon;
        await thirdRead.WaitAsync(TestTimeout);
        var personsLogin = daemon.StartLoginAsync(session.Id);

        dropped = true;
        await client.RaiseDisconnectedAsync();

        Assert.True(await Task.WhenAny(personsLogin, Task.Delay(TestTimeout)) == personsLogin,
            "The Log in still waits behind a sign-in whose daemon went away.");
    }

    // While a person's Xbox Log in waits for the saved-login sign-in, the session tells the page what it waits for by
    // key, and stops once the Log in goes on.
    [Fact]
    public async Task APersonsXboxLogInBehindTheSavedSignInSendsWhatItWaitsForAsync()
    {
        var notifications = DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
        var recorder = (EventRecordingNotificationsProxy)(object)notifications;
        var (daemon, client, session) = await StartXboxSigningInAsync(
            new ScriptedLoginDaemonClient { HoldStartLogin = true }, notifications);
        using var ownedDaemon = daemon;
        const string waitKey = "\"LoginWaitKey\":\"prefill.persistent.finishingSavedSignIn\"";

        var personsLogin = daemon.StartLoginAsync(session.Id);
        await WaitUntilAsync(() => XboxSessionUpdates(recorder).Any(update => update.Contains(waitKey)));
        client.ReleaseStartLogin.SetResult();
        await personsLogin.WaitAsync(TestTimeout);

        Assert.Contains("\"LoginWaitKey\":null", XboxSessionUpdates(recorder).Last());
    }

    // A Log in that leaves while the saved-login sign-in still runs (its page closed) leaves the wait line up for the next
    // Log in behind the same sign-in; the line goes once that sign-in ends.
    [Fact]
    public async Task ALeavingXboxLogInKeepsTheWaitLineUntilTheSavedSignInEndsAsync()
    {
        var notifications = DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
        var recorder = (EventRecordingNotificationsProxy)(object)notifications;
        var (daemon, client, session) = await StartXboxSigningInAsync(
            new ScriptedLoginDaemonClient { HoldStartLogin = true }, notifications);
        using var ownedDaemon = daemon;
        const string waitKey = "\"LoginWaitKey\":\"prefill.persistent.finishingSavedSignIn\"";
        const string noWaitKey = "\"LoginWaitKey\":null";
        using var leaving = new CancellationTokenSource();

        var leavingLogin = daemon.StartLoginAsync(session.Id, cancellationToken: leaving.Token);
        await WaitUntilAsync(() => XboxSessionUpdates(recorder).Any(update => update.Contains(waitKey)));
        var firstWait = Array.FindIndex(XboxSessionUpdates(recorder), update => update.Contains(waitKey));
        leaving.Cancel();
        await Record.ExceptionAsync(() => leavingLogin.WaitAsync(TestTimeout));
        var personsLogin = daemon.StartLoginAsync(session.Id);
        await WaitUntilAsync(() => XboxSessionUpdates(recorder).Count(update => update.Contains(waitKey)) >= 2);

        Assert.DoesNotContain(XboxSessionUpdates(recorder).Skip(firstWait), update => update.Contains(noWaitKey));
        Assert.Equal("prefill.persistent.finishingSavedSignIn", session.LoginWaitKey);
        // The admin session list, the page's other reader of the session, carries the same key.
        var (_, adminDatabase) = NewDatabase();
        var adminDeps = MakeDeps(adminDatabase, new PrefillSessionService(adminDatabase, NullLogger<PrefillSessionService>.Instance),
            Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true));
        using var steam = new TestSteamDaemon(adminDeps, new RecordingContainerGateway());
        using var epic = new TestEpicDaemon(adminDeps, new RecordingContainerGateway());
        using var battle = new TestBattleNetDaemon(adminDeps, new RecordingContainerGateway());
        using var riot = new TestRiotDaemon(adminDeps, new RecordingContainerGateway());
        var controller = new PrefillAdminController(adminDeps.SessionService, steam, epic, battle, riot, (XboxPrefillDaemonService)daemon,
            adminDeps.CacheService, adminDeps.Notifications, NullLogger<PrefillAdminController>.Instance);
        var http = new DefaultHttpContext();
        http.Items["Session"] = new UserSession { Id = Guid.NewGuid(), SessionType = SessionType.Admin };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        async Task<string?> ListedWaitKeyAsync()
        {
            var listed = Assert.IsType<OkObjectResult>((await controller.GetActiveSessionsAsync()).Result);
            return Assert.Single(Assert.IsType<List<DaemonSessionDto>>(listed.Value), item => item.Id == session.Id).LoginWaitKey;
        }
        Assert.Equal("prefill.persistent.finishingSavedSignIn", await ListedWaitKeyAsync());

        client.ReleaseStartLogin.SetResult();
        await personsLogin.WaitAsync(TestTimeout);
        await WaitUntilAsync(() => session.LoginWaitKey is null && XboxSessionUpdates(recorder).Last().Contains(noWaitKey));
        Assert.Null(await ListedWaitKeyAsync());
    }

    // The persistent page reads the live session back through its Start, which carries the same wait key as the push
    // while a Log in waits on the saved sign-in, and none once that sign-in ends.
    [Fact]
    public async Task ThePersistentStartReadCarriesTheXboxWaitKeyUntilTheSavedSignInEndsAsync()
    {
        var notifications = DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
        var recorder = (EventRecordingNotificationsProxy)(object)notifications;
        var (daemon, client, session) = await StartXboxSigningInAsync(
            new ScriptedLoginDaemonClient { HoldStartLogin = true }, notifications);
        using var ownedDaemon = daemon;
        const string waitKey = "\"LoginWaitKey\":\"prefill.persistent.finishingSavedSignIn\"";
        const string noWaitKey = "\"LoginWaitKey\":null";
        using var daemons = new ServiceCollection().AddSingleton((XboxPrefillDaemonService)daemon).BuildServiceProvider();
        var controller = new PersistentPrefillController(daemons, null!, null!, NullLogger<PersistentPrefillController>.Instance);
        async Task<DaemonSessionDto> StartedSessionAsync()
        {
            var started = Assert.IsType<OkObjectResult>(
                (await controller.StartAsync(new StartPersistentSessionRequest { Service = PrefillPlatform.Xbox }, CancellationToken.None)).Result);
            return Assert.IsType<DaemonSessionDto>(started.Value);
        }

        var personsLogin = daemon.StartLoginAsync(session.Id);
        await WaitUntilAsync(() => XboxSessionUpdates(recorder).Any(update => update.Contains(waitKey)));
        var waiting = await StartedSessionAsync();

        Assert.Equal(session.Id, waiting.Id);
        Assert.Equal("prefill.persistent.finishingSavedSignIn", waiting.LoginWaitKey);

        client.ReleaseStartLogin.SetResult();
        await personsLogin.WaitAsync(TestTimeout);
        await WaitUntilAsync(() => session.LoginWaitKey is null && XboxSessionUpdates(recorder).Last().Contains(noWaitKey));
        var ended = await StartedSessionAsync();

        Assert.Equal(session.Id, ended.Id);
        Assert.Null(ended.LoginWaitKey);
    }

    // Two Log ins wait on two saved sign-ins, the second on a sign-in that started after the first one's: the wait
    // line stays until the second Log in's sign-in ends, not only the first one's.
    [Fact]
    public async Task AnXboxWaitLineStaysUntilTheLastWaitingLogInsSignInEndsAsync()
    {
        var notifications = DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
        var recorder = (EventRecordingNotificationsProxy)(object)notifications;
        var (daemon, client, session) = await StartXboxSigningInAsync(
            new ScriptedLoginDaemonClient { HoldStartLogin = true }, notifications);
        using var ownedDaemon = daemon;
        const string waitKey = "\"LoginWaitKey\":\"prefill.persistent.finishingSavedSignIn\"";
        var firstLogin = daemon.StartLoginAsync(session.Id);
        await WaitUntilAsync(() => XboxSessionUpdates(recorder).Any(update => update.Contains(waitKey)));
        // A sign-in again after a drop, started while the startup sign-in still runs.
        var signInAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (session.PrefillLock) session.SignInAgainAfterDrop = signInAgain.Task;
        var secondLogin = daemon.StartLoginAsync(session.Id);
        await WaitUntilAsync(() => XboxSessionUpdates(recorder).Count(update => update.Contains(waitKey)) >= 2);

        client.ReleaseStartLogin.SetResult();
        await firstLogin.WaitAsync(TestTimeout);

        Assert.Equal("prefill.persistent.finishingSavedSignIn", session.LoginWaitKey);

        signInAgain.SetResult();
        await secondLogin.WaitAsync(TestTimeout);
        await WaitUntilAsync(() => session.LoginWaitKey is null);
        Assert.Equal(0, session.LoginWaitCount);
    }

    // A sign-in again that no Log in waits on does not hold the wait line: it clears when the one waiting Log in's
    // sign-in ends, while that later sign-in still runs.
    [Fact]
    public async Task AnXboxWaitLineClearsWhileASignInNoLogInWaitsOnStillRunsAsync()
    {
        var (daemon, client, session) = await StartXboxSigningInAsync(new ScriptedLoginDaemonClient { HoldStartLogin = true });
        using var ownedDaemon = daemon;
        var personsLogin = daemon.StartLoginAsync(session.Id);
        await WaitUntilAsync(() => session.LoginWaitKey is not null);
        var signInAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (session.PrefillLock) session.SignInAgainAfterDrop = signInAgain.Task;

        client.ReleaseStartLogin.SetResult();
        await personsLogin.WaitAsync(TestTimeout);
        await WaitUntilAsync(() => session.LoginWaitKey is null);

        Assert.False(signInAgain.Task.IsCompleted);
        Assert.Equal(0, session.LoginWaitCount);
    }

    // After a drop, an Xbox saved sign-in whose status reads each hang for 30 s still ends inside its 2-minute bound:
    // signed out, with no cancel-login, which would erase a running sign-in's saved login.
    [Fact]
    public async Task AnXboxSavedSignInWhoseReadsHangAfterADropEndsAtTheBoundAsync()
    {
        var instance = Guid.NewGuid().ToString("D");
        var dropped = false;
        var reads = 0;
        var sentRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new CacheStatusClock(DateTimeOffset.UtcNow);
        var started = clock.GetUtcNow();
        var client = new ScriptedLoginDaemonClient { NeverAuthenticates = true };
        client.StatusHandler = async token =>
        {
            if (Volatile.Read(ref dropped))
            {
                await Task.Delay(TimeSpan.FromSeconds(30), clock, token);
                return null;
            }
            if (client.StartLoginCallCount > 0 && Interlocked.Increment(ref reads) == 3)
            {
                sentRead.TrySetResult();
            }
            return XboxSignedOut(instance);
        };
        var (daemon, _, session) = await StartXboxSigningInAsync(client, clock: clock);
        using var ownedDaemon = daemon;
        await sentRead.Task.WaitAsync(TestTimeout);
        var attempt = daemon.LastHeadlessSelfAuthAttempt!;

        Volatile.Write(ref dropped, true);
        await client.RaiseDisconnectedAsync();
        // Steps the clock up to the bound and no further: a try still reading then is past its bound.
        while (clock.GetUtcNow() - started < TimeSpan.FromMinutes(2) && !attempt.IsCompleted)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20);
        }

        Assert.True(await Task.WhenAny(attempt, Task.Delay(TestTimeout)) == attempt,
            "The sign-in still reads its daemon after the bound.");
        Assert.Equal("The daemon disconnected during the saved sign-in.", session.LastLoginFailureMessage);
        Assert.Equal(0, client.CancelLoginCallCount);
        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);
    }

    // A daemon whose read after the login says no sign-in runs gets 5 s to push the outcome, not the 2-minute bound.
    [Fact]
    public async Task AnXboxSavedSignInWhoseDaemonSaysNoSignInRunsEndsAfterAShortGraceAsync()
    {
        var instance = Guid.NewGuid().ToString("D");
        var reads = 0;
        var sentRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new CacheStatusClock(DateTimeOffset.UtcNow);
        var client = new ScriptedLoginDaemonClient { NeverAuthenticates = true };
        client.StatusHandler = _ =>
        {
            if (client.StartLoginCallCount > 0 && Interlocked.Increment(ref reads) == 3)
            {
                sentRead.TrySetResult();
            }
            return Task.FromResult<DaemonStatus?>(XboxSignedOut(instance, loggingIn: false));
        };
        var (daemon, _, session) = await StartXboxSigningInAsync(client, clock: clock);
        using var ownedDaemon = daemon;
        await sentRead.Task.WaitAsync(TestTimeout);
        var attempt = daemon.LastHeadlessSelfAuthAttempt!;

        // Steps the clock 10 s, well short of the 2-minute bound.
        for (var step = 0; step < 20 && !attempt.IsCompleted; step++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(20);
        }

        Assert.True(await Task.WhenAny(attempt, Task.Delay(TestTimeout)) == attempt,
            "The sign-in waited out its bound after its daemon said no sign-in runs.");
        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);
    }

    // A failure the daemon pushes just after a read made after the drop said no sign-in runs (an older daemon clears
    // its flag first) reaches the session as the daemon's own text.
    [Fact]
    public async Task AnXboxSavedSignInShowsTheFailurePushedJustAfterANoSignInReadAsync()
    {
        var instance = Guid.NewGuid().ToString("D");
        const string failure = "Login failed: the saved token was refused.";
        var dropped = false;
        var readsAfterDrop = 0;
        var clock = new CacheStatusClock(DateTimeOffset.UtcNow);
        var client = new ScriptedLoginDaemonClient { NeverAuthenticates = true };
        var thirdRead = ThirdXboxStatusReadAfterLogin(client, instance, () =>
        {
            if (!Volatile.Read(ref dropped)) return (false, null);
            Interlocked.Increment(ref readsAfterDrop);
            return (true, XboxSignedOut(instance, loggingIn: false));
        });
        var (daemon, _, session) = await StartXboxSigningInAsync(client, clock: clock);
        using var ownedDaemon = daemon;
        await thirdRead.WaitAsync(TestTimeout);
        var attempt = daemon.LastHeadlessSelfAuthAttempt!;

        Volatile.Write(ref dropped, true);
        await client.RaiseDisconnectedAsync();
        // The recovery pass and the sign-in each read the daemon once after the drop.
        await WaitUntilAsync(() => Volatile.Read(ref readsAfterDrop) >= 2);
        await Task.Delay(300);
        Assert.False(attempt.IsCompleted, "The sign-in ended before the daemon could push its failure.");
        var failed = XboxSignedOut(instance);
        failed.Message = failure;
        failed.ErrorCode = "login-failed";
        await client.RaiseStatusAsync(failed);
        await WaitUntilAsync(() =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            return attempt.IsCompleted;
        });

        Assert.Equal(failure, session.LastLoginFailureMessage);
        Assert.Equal(0, client.CancelLoginCallCount);
    }

    // The manager reads a refused sign-in from the daemon's login-failed code: the same text without the code does
    // not end the Log in as a failure.
    [Theory]
    [InlineData("login-failed", true)]
    [InlineData(null, false)]
    public async Task ARefusedSignInIsReadFromItsErrorCodeAsync(string? errorCode, bool fails)
    {
        var (_, daemon, session, _, _) = NewTemporarySessionSigningIn();
        using var ownedDaemon = daemon;

        await DaemonTestMethods.InvokePrivateHandlerAsync(daemon, "ApplyStatusAsync", session,
            new DaemonStatus { Status = "awaiting-login", Message = "Login failed: refused", ErrorCode = errorCode }, null);

        Assert.Equal(fails ? "Login failed: refused" : null, session.LastLoginFailureMessage);
    }

    // A status read that fails after the drop says nothing about the daemon the login went to: the Xbox saved-login
    // sign-in keeps the login lock and reads again, and ends on that daemon's push with no cancel-login.
    [Fact]
    public async Task AnXboxSavedSignInKeepsWaitingAfterADropWhenAStatusReadFailsAsync()
    {
        var instance = Guid.NewGuid().ToString("D");
        var dropped = false;
        var answers = false;
        var readsAfterDrop = 0;
        var clock = new CacheStatusClock(DateTimeOffset.UtcNow);
        var client = new ScriptedLoginDaemonClient { NeverAuthenticates = true };
        var thirdRead = ThirdXboxStatusReadAfterLogin(client, instance, () =>
        {
            if (!dropped) return (false, null);
            Interlocked.Increment(ref readsAfterDrop);
            return Volatile.Read(ref answers) ? (false, null) : (true, null);
        });
        var (daemon, _, session) = await StartXboxSigningInAsync(client, clock: clock);
        using var ownedDaemon = daemon;
        await thirdRead.WaitAsync(TestTimeout);
        var attempt = daemon.LastHeadlessSelfAuthAttempt!;

        dropped = true;
        await client.RaiseDisconnectedAsync();
        // The recovery pass and the sign-in each read the daemon once after the drop, and both reads fail.
        await WaitUntilAsync(() => Volatile.Read(ref readsAfterDrop) >= 2);
        await Task.Delay(300);
        Assert.False(attempt.IsCompleted, "One failed status read ended the sign-in.");
        Assert.Equal(0, session.LoginLock.CurrentCount);

        Volatile.Write(ref answers, true);
        var readsBeforeAnswer = Volatile.Read(ref readsAfterDrop);
        await WaitUntilAsync(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            return Volatile.Read(ref readsAfterDrop) > readsBeforeAnswer;
        });
        await Task.Delay(300);
        Assert.False(attempt.IsCompleted);

        var signedIn = XboxSignedOut(instance);
        signedIn.Status = "logged-in";
        await client.RaiseStatusAsync(signedIn);
        await attempt.WaitAsync(TestTimeout);

        Assert.Equal(0, client.CancelLoginCallCount);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
        Assert.Equal(DaemonSessionStatus.Active, session.Status);
    }

    // When neither the preflight nor the read after the login was sent named the daemon, an answer after the drop
    // cannot show the daemon is new, so the Xbox saved-login sign-in waits for its push.
    [Fact]
    public async Task AnXboxSavedSignInWithAFailedPreflightKeepsWaitingAfterADropAsync()
    {
        var instance = Guid.NewGuid().ToString("D");
        var dropped = false;
        var readsAfterLogin = 0;
        var readsAfterDrop = 0;
        var sentRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedLoginDaemonClient { NeverAuthenticates = true };
        client.StatusHandler = _ =>
        {
            if (Volatile.Read(ref dropped))
            {
                Interlocked.Increment(ref readsAfterDrop);
                return Task.FromResult<DaemonStatus?>(XboxSignedOut(instance));
            }
            if (client.StartLoginCallCount == 0)
            {
                return Task.FromResult<DaemonStatus?>(null);
            }
            if (Interlocked.Increment(ref readsAfterLogin) == 3)
            {
                sentRead.TrySetResult();
                return Task.FromResult<DaemonStatus?>(null);
            }
            return Task.FromResult<DaemonStatus?>(XboxSignedOut(instance));
        };
        var (daemon, _, session) = await StartXboxSigningInAsync(client);
        using var ownedDaemon = daemon;
        await sentRead.Task.WaitAsync(TestTimeout);
        var attempt = daemon.LastHeadlessSelfAuthAttempt!;

        Volatile.Write(ref dropped, true);
        await client.RaiseDisconnectedAsync();
        await WaitUntilAsync(() => Volatile.Read(ref readsAfterDrop) >= 1);
        await Task.Delay(300);
        Assert.False(attempt.IsCompleted, "A failed preflight made the same daemon look gone.");
        Assert.Equal(0, session.LoginLock.CurrentCount);

        var signedIn = XboxSignedOut(instance);
        signedIn.Status = "logged-in";
        await client.RaiseStatusAsync(signedIn);
        await attempt.WaitAsync(TestTimeout);

        Assert.Equal(0, client.CancelLoginCallCount);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
    }

    // A failed preflight leaves the read made after the login was sent to name the daemon, so a new instance that answers
    // after the drop still ends the Xbox saved-login sign-in at once.
    [Fact]
    public async Task AnXboxSavedSignInWithAFailedPreflightEndsWhenANewDaemonAnswersAfterADropAsync()
    {
        var instance = Guid.NewGuid().ToString("D");
        var dropped = false;
        var client = new ScriptedLoginDaemonClient { NeverAuthenticates = true };
        var thirdRead = ThirdXboxStatusReadAfterLogin(client, instance, () =>
        {
            if (Volatile.Read(ref dropped)) return (true, XboxSignedOut(Guid.NewGuid().ToString("D")));
            return client.StartLoginCallCount == 0 ? (true, null) : (false, null);
        });
        var (daemon, _, session) = await StartXboxSigningInAsync(client);
        using var ownedDaemon = daemon;
        await thirdRead.WaitAsync(TestTimeout);
        var attempt = daemon.LastHeadlessSelfAuthAttempt!;

        Volatile.Write(ref dropped, true);
        await client.RaiseDisconnectedAsync();

        Assert.True(await Task.WhenAny(attempt, Task.Delay(TestTimeout)) == attempt, "The sign-in still waits after a new daemon answered.");
        Assert.Equal(0, client.CancelLoginCallCount);
        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);
        Assert.Equal(1, session.LoginLock.CurrentCount);
    }

    // An Xbox saved-login sign-in whose daemon never answers after the drop ends signed out at the bound, with no
    // cancel-login to the unreachable daemon, and the session is not marked errored.
    [Fact]
    public async Task AnXboxSavedSignInWhoseDaemonNeverAnswersAfterADropEndsSignedOutAtTheBoundAsync()
    {
        var instance = Guid.NewGuid().ToString("D");
        var dropped = false;
        var clock = new CacheStatusClock(DateTimeOffset.UtcNow);
        var client = new ScriptedLoginDaemonClient { NeverAuthenticates = true };
        var thirdRead = ThirdXboxStatusReadAfterLogin(client, instance, () => Volatile.Read(ref dropped) ? (true, null) : (false, null));
        var (daemon, _, session) = await StartXboxSigningInAsync(client, clock: clock);
        using var ownedDaemon = daemon;
        await thirdRead.WaitAsync(TestTimeout);
        var attempt = daemon.LastHeadlessSelfAuthAttempt!;

        Volatile.Write(ref dropped, true);
        await client.RaiseDisconnectedAsync();
        // Steps the clock through the pauses between status reads until the 2-minute bound has passed.
        await WaitUntilAsync(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            return attempt.IsCompleted;
        });

        Assert.Equal(0, client.CancelLoginCallCount);
        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);
        Assert.Equal(DaemonSessionStatus.Active, session.Status);
        Assert.Equal(1, session.LoginLock.CurrentCount);
    }

    // A saved login its daemon accepted, whose socket then drops, reads the daemon again: a daemon that says it is
    // logged in ends the sign-in signed in, with no cancel-login.
    [Fact]
    public async Task AnAcceptedSavedLoginWhoseSocketDropsEndsSignedInWhenItsDaemonSaysSoAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);

        var (login, cancelsAtDispatch) = await StartAcceptedSavedLoginAsync(s, () => LoggedInRead(s.Instance));
        await login.WaitAsync(TestTimeout);

        Assert.Equal(DaemonAuthState.Authenticated, s.Session.AuthState);
        Assert.Null(s.Session.LastLoginFailureMessage);
        Assert.Equal(cancelsAtDispatch, client.CancelLoginCallCount);
    }

    // After the drop, a failed read is tried again 5 s later, and a daemon that still signs in is waited on until it
    // pushes its outcome. No cancel-login is sent: on Xbox one erases the saved login.
    [Fact]
    public async Task AnAcceptedSavedLoginWaitsAfterADropWhileItsDaemonStillSignsInAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        var answers = false;
        var failedReads = 0;
        var signingInReads = 0;

        var (login, cancelsAtDispatch) = await StartAcceptedSavedLoginAsync(s, () =>
        {
            if (!Volatile.Read(ref answers))
            {
                Interlocked.Increment(ref failedReads);
                return null;
            }
            Interlocked.Increment(ref signingInReads);
            return SigningInRead(s.Instance);
        });
        await WaitUntilAsync(() => Volatile.Read(ref failedReads) >= 1 || login.IsCompleted);
        await Task.Delay(300);
        Assert.False(login.IsCompleted, "The sign-in ended at the drop.");

        Volatile.Write(ref answers, true);
        // Steps the clock through the 5 s pause before the next read.
        await WaitUntilAsync(() =>
        {
            s.Clock.Advance(TimeSpan.FromSeconds(1));
            return Volatile.Read(ref signingInReads) >= 1;
        });
        await Task.Delay(300);
        Assert.False(login.IsCompleted, "The sign-in ended while its daemon still signed in.");

        await client.RaiseStatusAsync(CommitPush(s.Instance));
        await login.WaitAsync(TestTimeout);

        Assert.Equal(DaemonAuthState.Authenticated, s.Session.AuthState);
        Assert.Equal(cancelsAtDispatch, client.CancelLoginCallCount);
    }

    // A daemon that never answers after the drop ends the sign-in as timed out at its 2-minute bound, with no
    // cancel-login.
    [Fact]
    public async Task AnAcceptedSavedLoginWhoseDaemonNeverAnswersAfterADropTimesOutAtTheBoundAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        var started = s.Clock.GetUtcNow();
        var failedReads = 0;

        var (login, cancelsAtDispatch) = await StartAcceptedSavedLoginAsync(s, () =>
        {
            Interlocked.Increment(ref failedReads);
            return null;
        });
        await WaitUntilAsync(() => Volatile.Read(ref failedReads) >= 1 || login.IsCompleted);
        await Task.Delay(300);
        // Steps the clock up to the bound and no further: a sign-in still waiting then is past its bound.
        while (s.Clock.GetUtcNow() - started < TimeSpan.FromMinutes(2) && !login.IsCompleted)
        {
            s.Clock.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(20);
        }

        Assert.True(await Task.WhenAny(login, Task.Delay(TestTimeout)) == login, "The sign-in still waits after its bound.");
        // The socket drop ended it, as a saved sign-in whose daemon drops reports.
        Assert.Equal("The daemon disconnected during the saved sign-in.", s.Session.LastLoginFailureMessage);
        Assert.Equal("prefill.persistent.loginDisconnected", s.Session.LastLoginFailureKey);
        Assert.Equal(cancelsAtDispatch, client.CancelLoginCallCount);
    }

    // On a daemon without concurrent prefill the drop itself ends the sign-in and keeps its ending, so the Log in
    // answer that the bound gives later names the same key as that kept ending.
    [Fact]
    public async Task ASavedLoginEndedByAnOlderDaemonsDropAnswersWithItsKeptEndingAsync()
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client, operationTracker: tracker);
        var started = s.Clock.GetUtcNow();

        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        s.Daemon.HeldSavedLogin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Daemon.SavedLoginAccepted = true;
        var login = s.Daemon.ReusePersistentIntegrationLoginAsync(
            s.Session.Id, null, () => { }, Guid.NewGuid(), lease: new IntegrationLease(s.Daemon, 0, null, () => { }, () => { }));
        await s.Daemon.SavedLoginEntered.Task.WaitAsync(TestTimeout);
        var dropped = false;
        var failedReads = 0;
        var readAfterLogin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Client.StatusHandler = _ =>
        {
            if (Volatile.Read(ref dropped))
            {
                Interlocked.Increment(ref failedReads);
                return Task.FromResult<DaemonStatus?>(null);
            }
            readAfterLogin.TrySetResult();
            return Task.FromResult<DaemonStatus?>(SigningInRead(s.Instance));
        };
        s.Daemon.HeldSavedLogin.SetResult();
        await readAfterLogin.Task.WaitAsync(TestTimeout);
        Assert.NotNull(s.Session.LoginOperationId);
        // A status with no protocol version is an older daemon.
        s.Session.Capabilities = new DaemonStatus { Status = "not-logged-in" };
        Volatile.Write(ref dropped, true);
        await s.Client.RaiseDisconnectedAsync();

        await WaitUntilAsync(() => Volatile.Read(ref failedReads) >= 1 || login.IsCompleted);
        await Task.Delay(300);
        while (s.Clock.GetUtcNow() - started < TimeSpan.FromMinutes(3) && !login.IsCompleted)
        {
            s.Clock.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(20);
        }

        Assert.True(await Task.WhenAny(login, Task.Delay(TestTimeout)) == login, "The sign-in still waits after its bound.");
        var ending = Assert.IsType<PrefillLoginEnding>(s.Session.LastLoginEnding);
        Assert.Equal(OperationStatus.Failed, ending.Status);
        Assert.Equal("prefill.persistent.loginDisconnected", ending.StageKey);
        Assert.Equal(ending.StageKey, s.Session.LastLoginFailureKey);
    }

    // A logged-in push that lands as the 2-minute bound ends the read after a drop signs the session in.
    [Fact]
    public async Task AnAcceptedSavedLoginWhosePushLandsAtTheBoundSignsInAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        var (login, _) = await StartAcceptedSavedLoginAsync(s, () => null);
        using var readHeld = new ManualResetEventSlim(false);
        var reads = 0;
        // Each read after the drop waits for the test, then fails if the bound ended it meanwhile.
        s.Client.StatusHandler = token =>
        {
            Interlocked.Increment(ref reads);
            readHeld.Wait(TestTimeout);
            token.ThrowIfCancellationRequested();
            return Task.FromResult<DaemonStatus?>(null);
        };
        // Steps the clock through the 5 s pause before the next read.
        await WaitUntilAsync(() =>
        {
            s.Clock.Advance(TimeSpan.FromSeconds(1));
            return Volatile.Read(ref reads) >= 1;
        });

        await client.RaiseStatusAsync(CommitPush(s.Instance));
        s.Clock.Advance(TimeSpan.FromMinutes(2));
        readHeld.Set();
        await login.WaitAsync(TestTimeout);

        Assert.Equal(DaemonAuthState.Authenticated, s.Session.AuthState);
        Assert.Null(s.Session.LastLoginFailureMessage);
    }

    // On Xbox, a logged-in push that lands as the 2-minute bound ends the read after a drop signs the session in.
    [Fact]
    public async Task AnXboxSavedSignInWhosePushLandsAtTheBoundSignsInAsync()
    {
        var instance = Guid.NewGuid().ToString("D");
        var dropped = false;
        var reads = 0;
        var heldReads = 0;
        var sentRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var readHeld = new ManualResetEventSlim(false);
        var clock = new CacheStatusClock(DateTimeOffset.UtcNow);
        var client = new ScriptedLoginDaemonClient { NeverAuthenticates = true };
        client.StatusHandler = token =>
        {
            if (Volatile.Read(ref dropped))
            {
                // Each read after the drop waits for the test, then fails if the bound ended it meanwhile.
                Interlocked.Increment(ref heldReads);
                readHeld.Wait(TestTimeout);
                token.ThrowIfCancellationRequested();
                return Task.FromResult<DaemonStatus?>(null);
            }
            if (client.StartLoginCallCount > 0 && Interlocked.Increment(ref reads) == 3)
            {
                sentRead.TrySetResult();
            }
            return Task.FromResult<DaemonStatus?>(XboxSignedOut(instance));
        };
        var (daemon, _, session) = await StartXboxSigningInAsync(client, clock: clock);
        using var ownedDaemon = daemon;
        await sentRead.Task.WaitAsync(TestTimeout);
        var attempt = daemon.LastHeadlessSelfAuthAttempt!;

        Volatile.Write(ref dropped, true);
        await client.RaiseDisconnectedAsync();
        await WaitUntilAsync(() => Volatile.Read(ref heldReads) >= 1);
        await client.RaiseStatusAsync(XboxLoggedIn());
        clock.Advance(TimeSpan.FromMinutes(2));
        readHeld.Set();
        await attempt.WaitAsync(TestTimeout);

        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
        Assert.Equal(0, client.CancelLoginCallCount);
    }

    // On Xbox, a logged-in push that lands as the 2-minute wait times out, with no drop, signs the session in.
    [Fact]
    public async Task AnXboxSavedSignInWhosePushLandsAsTheWaitTimesOutSignsInAsync()
    {
        var instance = Guid.NewGuid().ToString("D");
        var reads = 0;
        var clock = new CacheStatusClock(DateTimeOffset.UtcNow);
        var watch = new BoundTimerWatch(clock);
        var client = new ScriptedLoginDaemonClient { NeverAuthenticates = true };
        client.StatusHandler = _ =>
        {
            // The read made after the sign-in subscribed to the daemon's pushes.
            if (client.StartLoginCallCount > 0 && Interlocked.Increment(ref reads) == 3)
            {
                watch.Arm();
            }
            return Task.FromResult<DaemonStatus?>(XboxSignedOut(instance));
        };
        var (daemon, _, session) = await StartXboxSigningInAsync(client, clock: watch);
        using var ownedDaemon = daemon;
        await watch.BoundTimerCreated.Task.WaitAsync(TestTimeout);
        var attempt = daemon.LastHeadlessSelfAuthAttempt!;
        // The sign-in awaits right after creating that timer; this leaves it time to get there.
        await Task.Delay(200);

        // The wait times out first; its continuation is held, the push lands, and only then does the sign-in go on.
        clock.Advance(TimeSpan.FromMinutes(2));
        await watch.Held.Posted.Task.WaitAsync(TestTimeout);
        await client.RaiseStatusAsync(XboxLoggedIn());
        watch.Held.Release();
        await attempt.WaitAsync(TestTimeout);

        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
        Assert.Equal(0, client.CancelLoginCallCount);
    }

    // The test clock as the daemon sees it. The first 2-minute timer created once armed is the sign-in's wait for its
    // outcome, created on the sign-in's own thread just before it awaits that wait; installing `Held` there makes the
    // await resume through it.
    private sealed class BoundTimerWatch(CacheStatusClock clock) : TimeProvider
    {
        private int _armed;

        public TaskCompletionSource BoundTimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HeldContinuations Held { get; } = new();

        public void Arm() => Volatile.Write(ref _armed, 1);

        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = clock.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromMinutes(2) && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                SynchronizationContext.SetSynchronizationContext(Held);
                BoundTimerCreated.TrySetResult();
            }
            return timer;
        }
    }

    // Holds what is posted to it until released; after that it runs posts on the thread pool.
    private sealed class HeldContinuations : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _posted = new();
        private int _released;

        public TaskCompletionSource Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Post(SendOrPostCallback d, object? state)
        {
            if (Volatile.Read(ref _released) == 1)
            {
                ThreadPool.QueueUserWorkItem(_ => d(state));
                return;
            }
            _posted.Enqueue((d, state));
            Posted.TrySetResult();
        }

        public void Release()
        {
            Volatile.Write(ref _released, 1);
            while (_posted.TryDequeue(out var posted))
                ThreadPool.QueueUserWorkItem(_ => posted.Callback(posted.State));
        }
    }

    // Starts "Use my saved login" on the signed-out Steam session with a saved login its daemon accepts. Each status read
    // after the login is sent says the daemon still signs in; once the first has come back the socket drops, and
    // `afterDrop` answers every later read. Returns the attempt and the cancel-logins sent before the login (the attempt
    // cancels any earlier sign-in first).
    private static async Task<(Task<CredentialChallenge?> Login, int CancelsAtDispatch)> StartAcceptedSavedLoginAsync(
        SignInScenario s, Func<DaemonStatus?> afterDrop)
    {
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        s.Daemon.HeldSavedLogin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Daemon.SavedLoginAccepted = true;
        var login = s.Daemon.ReusePersistentIntegrationLoginAsync(
            s.Session.Id, null, () => { }, Guid.NewGuid(), lease: new IntegrationLease(s.Daemon, 0, null, () => { }, () => { }));
        await s.Daemon.SavedLoginEntered.Task.WaitAsync(TestTimeout);
        var cancelsAtDispatch = s.Client.CancelLoginCallCount;

        var dropped = false;
        var readAfterLogin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Client.StatusHandler = _ =>
        {
            if (Volatile.Read(ref dropped)) return Task.FromResult(afterDrop());
            readAfterLogin.TrySetResult();
            return Task.FromResult<DaemonStatus?>(SigningInRead(s.Instance));
        };
        s.Daemon.HeldSavedLogin.SetResult();
        await readAfterLogin.Task.WaitAsync(TestTimeout);
        Volatile.Write(ref dropped, true);
        await s.Client.RaiseDisconnectedAsync();
        return (login, cancelsAtDispatch);
    }

    // A status read of a daemon that is still signing in, as the Xbox daemon reports it.
    private static DaemonStatus SigningInRead(string instanceId)
    {
        var status = XboxSignedOut(instanceId, loggingIn: true);
        status.Status = "not-logged-in";
        return status;
    }

    private static string[] XboxSessionUpdates(EventRecordingNotificationsProxy recorder)
        => recorder.Arguments
            .Where(call => call is [SignalREvents.XboxDaemonSessionUpdated, not null])
            .Select(call => JsonSerializer.Serialize(call[1]))
            .ToArray();

    // A signed-out protocol-2 Xbox status, so a drop takes the recovering path the current daemons take.
    private static DaemonStatus XboxSignedOut(string instanceId)
    {
        var status = IdleStatus(instanceId);
        status.Type = "status";
        status.Status = "awaiting-login";
        return status;
    }

    // The same status read from the daemon's JSON with its login-running field, which a null leaves out as an older
    // daemon does.
    private static DaemonStatus XboxSignedOut(string instanceId, bool? loggingIn)
    {
        var status = JsonSerializer.SerializeToNode(XboxSignedOut(instanceId))!.AsObject();
        status.Remove("isLoggingIn");
        if (loggingIn is { } running)
        {
            status["isLoggingIn"] = running;
        }
        return status.Deserialize<DaemonStatus>()!;
    }

    // Answers every status read as the signed-out daemon `instanceId` unless `afterDrop` says it answers instead, and
    // reports the third read after the login was sent: the two reads that end the bounded waits come first.
    private static Task ThirdXboxStatusReadAfterLogin(
        ScriptedLoginDaemonClient client, string instanceId, Func<(bool Answers, DaemonStatus? Status)> afterDrop)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        client.StatusHandler = _ =>
        {
            var (answers, status) = afterDrop();
            if (answers)
            {
                return Task.FromResult(status);
            }
            if (client.StartLoginCallCount > 0 && ++reads == 3)
            {
                reached.TrySetResult();
            }
            return Task.FromResult<DaemonStatus?>(XboxSignedOut(instanceId));
        };
        return reached.Task;
    }

    private static DaemonStatus XboxLoggedIn()
        => new() { Type = "status", Status = "logged-in", Timestamp = DateTime.UtcNow };

    // Answers every status read as signed out and reports the third read after the login was sent: the two reads that
    // end the bounded waits come first. A given release holds that third read until it completes.
    private static Task ThirdStatusReadAfterLogin(ScriptedLoginDaemonClient client, Task? release = null)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        client.StatusHandler = async token =>
        {
            if (client.StartLoginCallCount > 0 && ++reads == 3)
            {
                reached.TrySetResult();
                if (release is not null)
                {
                    await release.WaitAsync(token);
                }
            }
            return new DaemonStatus { Type = "status", Status = "awaiting-login", Timestamp = DateTime.UtcNow };
        };
        return reached.Task;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not hold in time.");
            await Task.Delay(20);
        }
    }

    // Starts an Xbox daemon that re-adopts its persistent container, whose saved-login sign-in has sent its login.
    private async Task<(PrefillDaemonServiceBase Daemon, ScriptedLoginDaemonClient Client, DaemonSession Session)>
        StartXboxSigningInAsync(
            ScriptedLoginDaemonClient client, ISignalRNotificationService? notifications = null, TimeProvider? clock = null)
    {
        const string containerName = "xbox-daemon-persistent";
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(
            sessionService, DateTime.UtcNow.AddDays(30), PrefillPlatform.Xbox, containerName);
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(RunningPersistentContainer(sessionId, containerName, PrefillPlatform.Xbox));
        var daemon = BuildDaemon(
            PrefillPlatform.Xbox,
            MakeDeps(database, sessionService, Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true),
                notifications: notifications, cacheStatusClock: clock),
            gateway, client);

        await daemon.StartAsync(CancellationToken.None).WaitAsync(TestTimeout);
        await client.StartLoginEntered.Task.WaitAsync(TestTimeout);
        return (daemon, client, daemon.GetActivePersistentSession()!);
    }

    private (RecordingContainerGateway Gateway, TestSteamDaemon Daemon, DaemonSession Session, ScriptedLoginDaemonClient Client, FakeContainer Container)
        NewTemporarySessionSigningIn(ISignalRNotificationService? notifications = null)
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        var container = new FakeContainer { Id = Guid.NewGuid().ToString("N"), Name = "steam-daemon-temporary", Running = true };
        gateway.AddContainer(container);
        var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true), notifications: notifications), gateway);
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: UsernameChallenge("temporary-login")) { CancelAcknowledged = false };
        var session = InjectedTemporarySteamSession(daemon, client);
        session.AuthState = DaemonAuthState.NotAuthenticated;
        session.ContainerId = container.Id;
        session.ContainerName = container.Name;
        var challenge = daemon.StartLoginAsync(session.Id).WaitAsync(TestTimeout).GetAwaiter().GetResult();
        Assert.Equal("temporary-login", challenge!.ChallengeId);
        return (gateway, daemon, session, client, container);
    }
}
