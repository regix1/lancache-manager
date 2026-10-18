using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    private static TimeSpan SignInAgainFirstWait => TimeSpan.FromSeconds(30);
    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    // ============================ Sign in again after a dropped Steam connection ============================
    // The daemon reports errorCode "auth-lost" while it stays signed out after a dropped connection. The
    // manager then signs the always-on container in again with its saved login on growing waits. Every
    // wait runs on the test clock, so each test moves time by hand.

    [Fact]
    public async Task ADroppedSteamSessionSignsInAgainWithTheSavedLoginAsync()
    {
        using var s = await StartSignedInSteamAsync(new ScriptedLoginDaemonClient());
        var timers = s.Clock.ActiveTimerCount;

        await s.RaiseDropAsync();
        var retry = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(retry);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);

        s.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(s.StartsBeforeDrop, s.Client.StartLoginCallCount);

        s.SteamAnswers = true;
        s.Clock.Advance(TimeSpan.FromSeconds(1));
        await retry!.WaitAsync(TestTimeout);

        Assert.Equal(s.StartsBeforeDrop + 1, s.Client.StartLoginCallCount);
        Assert.Equal(DaemonAuthState.Authenticated, s.Session.AuthState);
        Assert.Null(s.Session.SignInAgainAfterDrop);
    }

    [Fact]
    public async Task ADropReportedAfterTheSignOutStillStartsTheRetryAsync()
    {
        using var s = await StartSignedInSteamAsync(new ScriptedLoginDaemonClient());
        var timers = s.Clock.ActiveTimerCount;

        // A status read that lands first applies the sign-out without the code.
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        Assert.Equal(DaemonAuthState.NotAuthenticated, s.Session.AuthState);
        Assert.Null(s.Session.SignInAgainAfterDrop);

        await s.RaiseDropAsync();
        var retry = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(retry);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);

        s.SteamAnswers = true;
        s.Clock.Advance(SignInAgainFirstWait);
        await retry!.WaitAsync(TestTimeout);

        Assert.Equal(s.StartsBeforeDrop + 1, s.Client.StartLoginCallCount);
        Assert.Equal(DaemonAuthState.Authenticated, s.Session.AuthState);
    }

    [Fact]
    public async Task StartAsync_ReadoptedContainerSignedOutByADrop_SignsInAgainAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = new SignInScenario(client);
        s.UseSteamAnswers();

        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, expiresAt: DateTime.UtcNow.AddDays(30));
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(RunningPersistentContainer(sessionId));
        s.Daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true), cacheStatusClock: s.Clock),
            gateway,
            () => client);

        await s.Daemon.StartAsync(CancellationToken.None);
        s.Session = SessionsOf(s.Daemon).Values.Single(session => session.IsPersistent);

        var retry = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(retry);
        Assert.Null(s.Daemon.LastHeadlessSelfAuthAttempt);

        s.SteamAnswers = true;
        await s.Clock.WaitForActiveTimersAsync(1);
        s.Clock.Advance(SignInAgainFirstWait);
        await retry!.WaitAsync(TestTimeout);

        Assert.Equal(DaemonAuthState.Authenticated, s.Session.AuthState);
        Assert.Equal(1, client.StartLoginCallCount);
    }

    [Fact]
    public async Task ASignOutWithoutTheDropCodeStartsNoSignInAsync()
    {
        using var s = await StartSignedInSteamAsync(new ScriptedLoginDaemonClient());
        var timers = s.Clock.ActiveTimerCount;

        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        s.SteamAnswers = true;
        s.Clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(DaemonAuthState.NotAuthenticated, s.Session.AuthState);
        Assert.Null(s.Session.SignInAgainAfterDrop);
        Assert.Equal(timers, s.Clock.ActiveTimerCount);
        Assert.Equal(s.StartsBeforeDrop, s.Client.StartLoginCallCount);
    }

    [Fact]
    public async Task ARepeatedDropReportStartsOneRetryAsync()
    {
        using var s = await StartSignedInSteamAsync(new ScriptedLoginDaemonClient());
        var timers = s.Clock.ActiveTimerCount;

        await s.RaiseDropAsync();
        var first = s.Session.SignInAgainAfterDrop;
        await s.RaiseDropAsync();

        Assert.NotNull(first);
        Assert.Same(first, s.Session.SignInAgainAfterDrop);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);

        s.SteamAnswers = true;
        s.Clock.Advance(SignInAgainFirstWait);
        await first!.WaitAsync(TestTimeout);

        Assert.Equal(s.StartsBeforeDrop + 1, s.Client.StartLoginCallCount);
    }

    [Fact]
    public async Task ADroppedSessionKeepsTryingUntilSteamAnswersAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        client.NeverAuthenticates = true;
        var timers = s.Clock.ActiveTimerCount;

        await s.RaiseDropAsync();
        var retry = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(retry);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);

        // The first try gets no answer from Steam and cancels its sign-in.
        s.Clock.Advance(SignInAgainFirstWait);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);
        Assert.Equal(s.StartsBeforeDrop + 1, client.StartLoginCallCount);
        Assert.Equal(1, client.CancelLoginCallCount);
        Assert.False(retry!.IsCompleted);

        s.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(s.StartsBeforeDrop + 1, client.StartLoginCallCount);

        // The sign-in counts only a login sent from the moment Steam answers, so the second try must send its own.
        s.AnswersAfterStarts = client.StartLoginCallCount;
        s.SteamAnswers = true;
        client.NeverAuthenticates = false;
        s.Clock.Advance(TimeSpan.FromSeconds(1));
        await retry.WaitAsync(TestTimeout);

        Assert.Equal(s.StartsBeforeDrop + 2, client.StartLoginCallCount);
        Assert.Equal(DaemonAuthState.Authenticated, s.Session.AuthState);
    }

    [Fact]
    public async Task ADroppedSessionStopsWhenTheSavedLoginIsGoneAsync()
    {
        var challenge = new CredentialChallenge
        {
            ChallengeId = "gone-login-chal",
            CredentialType = "username",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
        var client = new ScriptedLoginDaemonClient(alreadyLoggedIn: true, challengeOnLogin: challenge);
        using var s = await StartSignedInSteamAsync(client);
        var timers = s.Clock.ActiveTimerCount;

        await s.RaiseDropAsync();
        var retry = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(retry);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);

        s.Clock.Advance(SignInAgainFirstWait);
        await retry!.WaitAsync(TestTimeout);
        Assert.Equal(1, client.StartLoginCallCount);
        Assert.Equal(1, client.CancelLoginCallCount);

        // A second report of the same drop finds the finished retry and starts nothing.
        await s.RaiseDropAsync();
        Assert.Same(retry, s.Session.SignInAgainAfterDrop);
        Assert.True(retry.IsCompleted);

        s.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(timers, s.Clock.ActiveTimerCount);
        Assert.Equal(1, client.StartLoginCallCount);
    }

    [Fact]
    public async Task ARetryKeepsGoingAfterAnotherAutomaticSignInFailsAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        client.NeverAuthenticates = true;
        var timers = s.Clock.ActiveTimerCount;

        await s.RaiseDropAsync();
        await s.Clock.WaitForActiveTimersAsync(timers + 1);
        s.Clock.Advance(SignInAgainFirstWait);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);
        Assert.Equal(s.StartsBeforeDrop + 1, client.StartLoginCallCount);

        // Stands for the scheduled run's sign-in failing while the retry waits.
        Assert.Equal(LoginAttemptOutcome.NoResponse, await s.Daemon.AttemptHeadlessPersistentSelfAuthAsync(s.Session));
        Assert.Equal(s.StartsBeforeDrop + 2, client.StartLoginCallCount);

        s.Clock.Advance(TimeSpan.FromMinutes(1));
        await s.Clock.WaitForActiveTimersAsync(timers + 1);
        Assert.Equal(s.StartsBeforeDrop + 3, client.StartLoginCallCount);
        Assert.False(s.Session.SignInAgainAfterDrop!.IsCompleted);
    }

    [Fact]
    public async Task ADroppedSessionStopsWhenItsCancelIsNotConfirmedAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        client.NeverAuthenticates = true;
        client.CancelAcknowledged = false;
        var timers = s.Clock.ActiveTimerCount;

        await s.RaiseDropAsync();
        var retry = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(retry);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);

        s.Clock.Advance(SignInAgainFirstWait);
        await retry!.WaitAsync(TestTimeout);
        Assert.Equal(DaemonSessionStatus.Error, s.Session.Status);

        s.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(timers, s.Clock.ActiveTimerCount);
        Assert.Equal(s.StartsBeforeDrop + 1, client.StartLoginCallCount);
    }

    [Fact]
    public async Task AnAutomaticSignInLeavesAPersonsSignInAloneAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var daemon = NewSteamDaemon();
        var session = InjectedPersistentSession(daemon, client);
        session.LoginOperationId = Guid.NewGuid();

        Assert.Null(await daemon.AttemptHeadlessPersistentSelfAuthAsync(session));
        Assert.Equal(0, client.StartLoginCallCount);
    }

    [Fact]
    public async Task ARetryOutlastsASignInCancelThatIsStillFinishingAsync()
    {
        using var s = await StartSignedInSteamAsync(new ScriptedLoginDaemonClient());
        var timers = s.Clock.ActiveTimerCount;

        await s.RaiseDropAsync();
        var retry = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(retry);
        s.Session.LoginCanceling = true;
        await s.Clock.WaitForActiveTimersAsync(timers + 1);

        // The refused try keeps the retry alive for the next wait.
        s.Clock.Advance(SignInAgainFirstWait);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);
        Assert.False(retry!.IsCompleted);
        Assert.Equal(s.StartsBeforeDrop, s.Client.StartLoginCallCount);

        s.Session.LoginCanceling = false;
        s.SteamAnswers = true;
        s.Clock.Advance(TimeSpan.FromMinutes(1));
        await retry.WaitAsync(TestTimeout);

        Assert.Equal(DaemonAuthState.Authenticated, s.Session.AuthState);
    }

    [Fact]
    public async Task StoppingTheSessionEndsTheRetryQuietlyAsync()
    {
        var logger = new CapturingLogger<SteamDaemonService>();
        using var s = await StartSignedInSteamAsync(new ScriptedLoginDaemonClient(), logger);
        var timers = s.Clock.ActiveTimerCount;

        await s.RaiseDropAsync();
        var retry = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(retry);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);

        await s.Session.CancellationTokenSource.CancelAsync();
        await retry!.WaitAsync(TestTimeout);

        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("SignInAgainAfterDropAsync"));
    }

    [Fact]
    public async Task ADroppedTemporarySessionStartsNoSignInAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var daemon = NewSteamDaemon();
        var session = InjectedTemporarySteamSession(daemon, client);

        await InvokePrivateAsync(daemon, "OnStatusChangeAsync", session, SteamStatus(Guid.NewGuid().ToString(), signedIn: false));

        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);
        Assert.Null(session.SignInAgainAfterDrop);
    }

    [Fact]
    public async Task AnAuthStatePushCarriesItsErrorCode()
    {
        using var client = new SocketDaemonClient("/nonexistent.sock", "secret", null);
        var received = new TaskCompletionSource<DaemonStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnStatusUpdate += status =>
        {
            received.TrySetResult(status);
            return Task.CompletedTask;
        };

        client.ProcessMessage("""{"type":"auth-state","data":{"state":"awaiting-login","message":"m","errorCode":"auth-lost"}}""");

        var pushed = await received.Task.WaitAsync(TestTimeout);
        Assert.Equal("awaiting-login", pushed.Status);
        Assert.Equal("auth-lost", pushed.ErrorCode);
    }

    [Fact]
    public async Task ASignInByAnotherPathEndsAWaitingRetryAndTheNextDropStartsItsOwnAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        client.NeverAuthenticates = true;
        var timers = s.Clock.ActiveTimerCount;

        await s.RaiseDropAsync();
        var first = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(first);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);
        s.Clock.Advance(SignInAgainFirstWait);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);
        Assert.Equal(s.StartsBeforeDrop + 1, client.StartLoginCallCount);

        // Stands for a scheduled run or a person signing the container in while the retry waits.
        await s.Client.RaiseStatusAsync(SteamStatus(s.Instance, signedIn: true));
        Assert.Null(s.Session.SignInAgainAfterDrop);
        Assert.False(first!.IsCompleted);

        await s.RaiseDropAsync();
        var second = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        await s.Clock.WaitForActiveTimersAsync(timers + 2);

        s.Clock.Advance(SignInAgainFirstWait);
        await s.Clock.WaitForActiveTimersAsync(timers + 2);
        Assert.Equal(s.StartsBeforeDrop + 2, client.StartLoginCallCount);

        // The first retry wakes, sees the sign-in it did not make, and sends nothing.
        s.Clock.Advance(SignInAgainFirstWait);
        await first.WaitAsync(TestTimeout);
        Assert.Equal(s.StartsBeforeDrop + 2, client.StartLoginCallCount);
    }

    [Fact]
    public async Task ADropOvertakingTheRetrysOwnSignInStartsANewRetryAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        var overtaken = false;
        client.StatusHandler = async _ =>
        {
            if (!overtaken && client.StartLoginCallCount > s.StartsBeforeDrop)
            {
                // The drop push is applied before the sign-in's own logged-in reply.
                overtaken = true;
                await client.RaiseStatusAsync(SteamStatus(s.Instance, signedIn: false));
                return SteamStatus(s.Instance, signedIn: true);
            }
            return SteamStatus(s.Instance, signedIn: false);
        };
        var timers = s.Clock.ActiveTimerCount;

        await s.RaiseDropAsync();
        var first = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(first);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);

        s.Clock.Advance(SignInAgainFirstWait);
        await first!.WaitAsync(TestTimeout);

        var second = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Equal(DaemonAuthState.NotAuthenticated, s.Session.AuthState);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);

        s.Clock.Advance(SignInAgainFirstWait);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);
        Assert.Equal(s.StartsBeforeDrop + 2, client.StartLoginCallCount);
    }

    [Fact]
    public async Task AStatusReadAfterTheDaemonSocketDropsStartsTheRetryAsync()
    {
        using var s = await StartSignedInSteamAsync(new ScriptedLoginDaemonClient());

        await s.Daemon.RefreshRunsAsync(s.Session.Id);

        Assert.NotNull(s.Session.SignInAgainAfterDrop);
        Assert.Equal(DaemonAuthState.NotAuthenticated, s.Session.AuthState);
    }

    [Fact]
    public async Task ARecreatedContainerWhoseSignInGetsNoAnswerKeepsTryingAsync()
    {
        var client = new ScriptedLoginDaemonClient { NeverAuthenticates = true };
        var clock = new CacheStatusClock(DateTimeOffset.UtcNow);
        var (_, daemon, _, _) = SetupRecreateScenario(DateTime.UtcNow.AddDays(30), clientFactory: () => client, cacheStatusClock: clock);
        var timers = clock.ActiveTimerCount;

        await daemon.StartAsync(CancellationToken.None);
        await daemon.LastHeadlessSelfAuthAttempt!;

        var session = SessionsOf(daemon).Values.Single(candidate => candidate.IsPersistent);
        var startsAfterSetup = client.StartLoginCallCount;
        Assert.NotNull(session.SignInAgainAfterDrop);
        await clock.WaitForActiveTimersAsync(timers + 1);

        clock.Advance(SignInAgainFirstWait);
        await clock.WaitForActiveTimersAsync(timers + 1);

        Assert.Equal(startsAfterSetup + 1, client.StartLoginCallCount);

        daemon.Dispose();
    }

    [Fact]
    public async Task AnImageUpdateDuringARetrySignsTheNewContainerInAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var instance = Guid.NewGuid().ToString();
        var signedOut = IdleStatus(instance);
        signedOut.Status = "awaiting-login";
        var client = new ScriptedLoginDaemonClient();
        client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(client.StartLoginCallCount > 0 ? IdleStatus(instance) : signedOut);
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)),
            gateway,
            () => client);
        var session = InjectedPersistentSession(daemon, client);
        session.ContainerId = "container-a";
        session.ContainerName = SteamPersistentContainerName;
        session.ConfiguredImage = "ghcr.io/regix1/steam-prefill-daemon:latest";
        session.ImageId = "sha256:image-a";
        session.Capabilities = signedOut;
        session.LoginSettled = true;
        session.AuthState = DaemonAuthState.NotAuthenticated;
        // A retry that is still waiting.
        session.SignInAgainAfterDrop = new TaskCompletionSource().Task;
        gateway.AddContainer(new FakeContainer
        {
            Id = session.ContainerId,
            Name = session.ContainerName,
            Running = true,
            ImageId = session.ImageId
        });

        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        Assert.NotNull(daemon.LastHeadlessSelfAuthAttempt);
        await daemon.LastHeadlessSelfAuthAttempt!;
        var successor = Assert.Single(SessionsOf(daemon).Values, current => current.IsPersistent);
        Assert.Equal(1, client.StartLoginCallCount);
        Assert.Equal(DaemonAuthState.Authenticated, successor.AuthState);
    }

    [Fact]
    public async Task ARetrysSignInThatNoSignInConfirmedStartsANewRetryAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        client.NeverAuthenticates = true;
        var timers = s.Clock.ActiveTimerCount;

        await s.RaiseDropAsync();
        var first = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(first);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);

        // A signed-in state the session holds without any sign-in having been applied.
        s.Session.AuthState = DaemonAuthState.Authenticated;
        s.Clock.Advance(SignInAgainFirstWait);
        await first!.WaitAsync(TestTimeout);

        var second = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Equal(DaemonAuthState.NotAuthenticated, s.Session.AuthState);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);
        var startsAfterFirstTry = client.StartLoginCallCount;

        s.Clock.Advance(SignInAgainFirstWait);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);
        Assert.Equal(startsAfterFirstTry + 1, client.StartLoginCallCount);
    }

    [Fact]
    public async Task ARunRefreshLeavesATemporarySessionsSignInStateAloneAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        var instance = Guid.NewGuid().ToString();
        client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(SteamStatus(instance, signedIn: false));
        using var daemon = NewSteamDaemon();
        var session = InjectedTemporarySteamSession(daemon, client);

        await daemon.RefreshRunsAsync(session.Id);

        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
        Assert.Null(session.SignInAgainAfterDrop);
    }

    [Fact]
    public async Task ARecreatedContainerWhoseCancelIsNotConfirmedStartsNoRetryAsync()
    {
        // The daemon's own push for the unfinished sign-in carries the drop code and starts a retry before
        // the cancel fails and the session is marked errored.
        var client = new ScriptedLoginDaemonClient { NeverAuthenticates = true, NoAnswerErrorCode = "auth-lost", CancelAcknowledged = false };
        var clock = new CacheStatusClock(DateTimeOffset.UtcNow);
        var (_, daemon, _, _) = SetupRecreateScenario(DateTime.UtcNow.AddDays(30), clientFactory: () => client, cacheStatusClock: clock);
        var timers = clock.ActiveTimerCount;

        await daemon.StartAsync(CancellationToken.None);
        await daemon.LastHeadlessSelfAuthAttempt!;

        var session = SessionsOf(daemon).Values.Single(candidate => candidate.IsPersistent);
        Assert.Equal(DaemonSessionStatus.Error, session.Status);
        var retry = session.SignInAgainAfterDrop;
        var startsAfterSetup = client.StartLoginCallCount;
        Assert.NotNull(retry);
        await clock.WaitForActiveTimersAsync(timers + 1);

        clock.Advance(SignInAgainFirstWait);
        await retry!.WaitAsync(TestTimeout);

        Assert.Equal(startsAfterSetup, client.StartLoginCallCount);
        Assert.Equal(timers, clock.ActiveTimerCount);

        daemon.Dispose();
    }

    [Fact]
    public async Task APersonsSignInStopsTheRetryThatIsWaitingAsync()
    {
        var client = new ScriptedLoginDaemonClient(alreadyLoggedIn: true, challengeOnLogin: UsernameChallenge("takeover-waiting"));
        var logger = new CapturingLogger<SteamDaemonService>();
        using var s = await StartSignedInSteamAsync(client, logger);
        var timers = s.Clock.ActiveTimerCount;

        await s.RaiseDropAsync();
        var retry = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(retry);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);

        var challenge = await s.Daemon.StartLoginAsync(s.Session.Id).WaitAsync(TestTimeout);

        // The person's own sign-in ran and its prompt reached them; the retry ended without a try.
        Assert.Equal("takeover-waiting", challenge!.ChallengeId);
        await retry!.WaitAsync(TestTimeout);
        Assert.Equal(timers, s.Clock.ActiveTimerCount);
        s.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(1, client.StartLoginCallCount);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("SignInAgainAfterDropAsync"));
    }

    [Fact]
    public async Task APersonsSignInTakesOverFromARetryThatIsSigningInAsync()
    {
        var client = new ScriptedLoginDaemonClient(alreadyLoggedIn: true, challengeOnLogin: UsernameChallenge("takeover-in-flight"));
        var logger = new CapturingLogger<SteamDaemonService>();
        using var s = await StartSignedInSteamAsync(client, logger);
        client.HoldStartLogin = true;
        var timers = s.Clock.ActiveTimerCount;

        await s.RaiseDropAsync();
        var retry = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(retry);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);
        s.Clock.Advance(SignInAgainFirstWait);
        await client.StartLoginEntered.Task.WaitAsync(TestTimeout);

        // The retry's login is on the daemon and holds the lock; the person's sign-in ends it, then runs.
        var personsLogin = s.Daemon.StartLoginAsync(s.Session.Id);
        await client.CancelLoginEntered.Task.WaitAsync(TestTimeout);
        client.ReleaseStartLogin.SetResult();
        var challenge = await personsLogin.WaitAsync(TestTimeout);

        Assert.Equal("takeover-in-flight", challenge!.ChallengeId);
        Assert.Equal(2, client.StartLoginCallCount);
        Assert.Equal(1, client.CancelLoginCallCount);
        await retry!.WaitAsync(TestTimeout);
        Assert.Equal(timers, s.Clock.ActiveTimerCount);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("trying again"));
    }

    [Fact]
    public async Task APersonsSignInThatDoesNotFinishLeavesRoomForANewRetryAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        var timers = s.Clock.ActiveTimerCount;

        await s.RaiseDropAsync();
        var first = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(first);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);

        // The person's Log in takes over, and Steam does not answer it: the daemon ends it with the drop code.
        client.NeverAuthenticates = true;
        client.NoAnswerErrorCode = "auth-lost";
        await s.Daemon.StartLoginAsync(s.Session.Id).WaitAsync(TestTimeout);
        await first!.WaitAsync(TestTimeout);

        var second = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(second);
        Assert.NotSame(first, second);

        // Steam answers again and the new retry signs the container in.
        client.NeverAuthenticates = false;
        s.SteamAnswers = true;
        await s.Clock.WaitForActiveTimersAsync(timers + 1);
        s.Clock.Advance(SignInAgainFirstWait);
        await second!.WaitAsync(TestTimeout);

        Assert.Equal(DaemonAuthState.Authenticated, s.Session.AuthState);
    }

    [Fact]
    public async Task APersonsLogInAbortedDuringTheTakeOverLeavesRoomForANewRetryAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        client.HoldStartLogin = true;
        client.HoldCancelLogin = true;
        var timers = s.Clock.ActiveTimerCount;

        await s.RaiseDropAsync();
        var first = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(first);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);
        s.Clock.Advance(SignInAgainFirstWait);
        await client.StartLoginEntered.Task.WaitAsync(TestTimeout);

        // The person's request is aborted (a page reload) while the retry is still ending its login.
        using var abort = new CancellationTokenSource();
        var personsLogin = s.Daemon.StartLoginAsync(s.Session.Id, cancellationToken: abort.Token);
        await client.CancelLoginEntered.Task.WaitAsync(TestTimeout);
        await abort.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => personsLogin);
        client.ReleaseCancelLogin.SetResult();
        await first!.WaitAsync(TestTimeout);

        // The next drop code starts a new retry; the finished one does not block it.
        await s.RaiseDropAsync();
        var second = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task APersonsLogInAfterTheRetryStoppedOnAChallengeLeavesRoomForANewRetryAsync()
    {
        var client = new ScriptedLoginDaemonClient(alreadyLoggedIn: true, challengeOnLogin: UsernameChallenge("after-challenge-stop"));
        using var s = await StartSignedInSteamAsync(client);
        var timers = s.Clock.ActiveTimerCount;

        await s.RaiseDropAsync();
        var first = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(first);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);

        // The saved login is gone: the retry meets a Steam challenge and stops.
        s.Clock.Advance(SignInAgainFirstWait);
        await first!.WaitAsync(TestTimeout);

        // A person signs in with a new login, and Steam drops it later.
        await s.Daemon.StartLoginAsync(s.Session.Id).WaitAsync(TestTimeout);
        await s.RaiseDropAsync();

        var second = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task APersonsLogInIsRefusedOnAnErroredSessionAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        // An unconfirmed cancel marked this daemon for replacement.
        s.Session.Status = DaemonSessionStatus.Error;

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => s.Daemon.StartLoginAsync(s.Session.Id));

        Assert.Equal("errors.prefill.sessionReplaced", refusal.StageKey);
        Assert.Equal(s.StartsBeforeDrop, client.StartLoginCallCount);
    }

    [Fact]
    public async Task AFinishingRetryLeavesANewerRetrysPlaceAloneAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        client.NeverAuthenticates = true;
        client.HoldStartLogin = true;
        var timers = s.Clock.ActiveTimerCount;

        await s.RaiseDropAsync();
        var first = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(first);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);

        // A signed-in state the session holds with no sign-in counted: the first retry's try will only confirm it.
        s.Session.AuthState = DaemonAuthState.Authenticated;
        s.Clock.Advance(SignInAgainFirstWait);
        await client.StartLoginEntered.Task.WaitAsync(TestTimeout);

        // A newer retry took the place meanwhile.
        var newer = new TaskCompletionSource().Task;
        s.Session.SignInAgainAfterDrop = newer;
        s.Session.SignInAgainCancel = new CancellationTokenSource();
        client.ReleaseStartLogin.SetResult();
        await first!.WaitAsync(TestTimeout);

        Assert.Same(newer, s.Session.SignInAgainAfterDrop);
    }

    [Fact]
    public async Task ASecondLogInDuringAnAbortedTakeOverWaitsForTheEndingRetryAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        client.HoldStartLogin = true;
        client.HoldCancelLogin = true;
        var timers = s.Clock.ActiveTimerCount;

        await s.RaiseDropAsync();
        Assert.NotNull(s.Session.SignInAgainAfterDrop);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);
        s.Clock.Advance(SignInAgainFirstWait);
        await client.StartLoginEntered.Task.WaitAsync(TestTimeout);

        using var abort = new CancellationTokenSource();
        var firstLogin = s.Daemon.StartLoginAsync(s.Session.Id, cancellationToken: abort.Token);
        await client.CancelLoginEntered.Task.WaitAsync(TestTimeout);
        await abort.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstLogin);

        // The retry is still ending its daemon login. A second Log in waits for it; it is not refused after the
        // 3 s lock wait.
        client.HoldStartLogin = false;
        var secondLogin = s.Daemon.StartLoginAsync(s.Session.Id);
        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.False(secondLogin.IsCompleted);

        client.ReleaseCancelLogin.SetResult();
        await secondLogin.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task APersonsCancelOfASignInDoesNotStartTheRetryAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        await CancelAPersonsSignInAsync(s, client);

        Assert.Null(s.Session.SignInAgainAfterDrop);

        // A later status read still carries the daemon's code for that canceled sign-in.
        await s.Daemon.GetSessionStatusAsync(s.Session.Id);
        Assert.Null(s.Session.SignInAgainAfterDrop);
    }

    [Fact]
    public async Task ADropAfterAPersonsCanceledSignInStartsTheRetryAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        await CancelAPersonsSignInAsync(s, client);

        var signIns = s.Session.SignInCount;

        // The daemon reaches logged-in after the cancel. That report stays signed out and keeps the mark.
        await s.Client.RaiseStatusAsync(SteamStatus(s.Instance, signedIn: true));
        Assert.Equal(DaemonAuthState.NotAuthenticated, s.Session.AuthState);
        Assert.Equal(signIns, s.Session.SignInCount);
        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.True(s.Session.LoggedInWhilePersonCanceled);
        Assert.True(await s.StoredWitnessAsync());
        Assert.Null(s.Session.SignInAgainAfterDrop);

        // A later connection drop, not the cancel's own auth-lost, clears the mark and signs in once.
        await s.RaiseDropAsync();

        Assert.NotNull(s.Session.SignInAgainAfterDrop);
        Assert.False(s.Session.LoginCanceledByPerson);
        Assert.False(s.Session.LoggedInWhilePersonCanceled);
        Assert.False(await s.StoredCancelAsync());
        Assert.False(await s.StoredWitnessAsync());
    }

    [Fact]
    public async Task AnInitializedStatusReadDuringAPersonsCancelDoesNotStartTheRetryAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        client.HoldStartLogin = true;
        client.CancelErrorCode = "auth-lost";
        client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(new DaemonStatus
        {
            Type = "status",
            Status = "logged-in",
            Message = "Initialized",
            Timestamp = DateTime.UtcNow
        });

        var personsLogin = s.Daemon.StartLoginAsync(s.Session.Id);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (client.StartLoginCallCount == s.StartsBeforeDrop && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(await s.Daemon.CancelLoginAsync(s.Session.Id));
        client.ReleaseStartLogin.SetResult();
        await Record.ExceptionAsync(() => personsLogin);

        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.False(s.Session.LoggedInWhilePersonCanceled);
        Assert.False(await s.StoredWitnessAsync());
        Assert.Equal(DaemonAuthState.NotAuthenticated, s.Session.AuthState);
        Assert.Null(s.Session.SignInAgainAfterDrop);

        await s.RaiseDropAsync();
        Assert.Null(s.Session.SignInAgainAfterDrop);
        Assert.True(s.Session.LoginCanceledByPerson);

        await s.Client.RaiseStatusAsync(SteamStatus(s.Instance, signedIn: true));
        Assert.True(s.Session.LoggedInWhilePersonCanceled);
        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.Null(s.Session.SignInAgainAfterDrop);

        await s.RaiseDropAsync();
        Assert.NotNull(s.Session.SignInAgainAfterDrop);
        Assert.False(s.Session.LoginCanceledByPerson);
        Assert.False(s.Session.LoggedInWhilePersonCanceled);
    }

    [Fact]
    public async Task APersonsLogInClearsTheCancelAndTheLoggedInWitnessAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        await CancelAPersonsSignInAsync(s, client);
        await s.Client.RaiseStatusAsync(SteamStatus(s.Instance, signedIn: true));
        Assert.True(s.Session.LoggedInWhilePersonCanceled);
        Assert.True(await s.StoredWitnessAsync());

        client.HoldStartLogin = false;
        s.SteamAnswers = true;
        await s.Daemon.StartLoginAsync(s.Session.Id).WaitAsync(TestTimeout);

        Assert.Equal(DaemonAuthState.Authenticated, s.Session.AuthState);
        Assert.False(s.Session.LoginCanceledByPerson);
        Assert.False(s.Session.LoggedInWhilePersonCanceled);
        Assert.False(await s.StoredCancelAsync());
        Assert.False(await s.StoredWitnessAsync());
    }

    [Fact]
    public async Task ARetryOutlastsATryThatADiscardedEditCancelsAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        client.HoldStartLogin = true;
        var timers = s.Clock.ActiveTimerCount;

        await s.RaiseDropAsync();
        var retry = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(retry);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);
        s.Clock.Advance(SignInAgainFirstWait);
        await client.StartLoginEntered.Task.WaitAsync(TestTimeout);

        // Discarding a schedule edit cancels whatever sign-in is current, with no attempt named, while the try waits.
        s.Session.LoginCanceling = true;
        client.ReleaseStartLogin.SetResult();
        await s.Clock.WaitForActiveTimersAsync(timers + 1);
        Assert.False(retry!.IsCompleted);

        s.Session.LoginCanceling = false;
        s.SteamAnswers = true;
        s.Clock.Advance(TimeSpan.FromMinutes(1));
        await retry.WaitAsync(TestTimeout);

        Assert.Equal(DaemonAuthState.Authenticated, s.Session.AuthState);
    }

    [Fact]
    public async Task AStatusReplyAppliedAfterADropDoesNotEndTheNewRetryAsync()
    {
        using var s = await StartSignedInSteamAsync(new ScriptedLoginDaemonClient());
        var timers = s.Clock.ActiveTimerCount;

        await s.RaiseDropAsync();
        var first = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(first);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);

        // A reply the daemon sent before the drop is applied after it.
        await s.Client.RaiseStatusAsync(SteamStatus(s.Instance, signedIn: true));
        Assert.Equal(DaemonAuthState.Authenticated, s.Session.AuthState);

        // The retry wakes, finds the daemon still signed out, and goes on under a new retry.
        s.Clock.Advance(SignInAgainFirstWait);
        await first!.WaitAsync(TestTimeout);
        var second = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Equal(DaemonAuthState.NotAuthenticated, s.Session.AuthState);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);

        s.SteamAnswers = true;
        s.Clock.Advance(SignInAgainFirstWait);
        await second!.WaitAsync(TestTimeout);

        Assert.Equal(s.StartsBeforeDrop + 1, s.Client.StartLoginCallCount);
        Assert.Equal(DaemonAuthState.Authenticated, s.Session.AuthState);
    }

    // ============================ A person's Cancel keeps the container signed out ============================

    [Fact]
    public async Task APersonsCancelIsStoredOnTheSessionRowAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);

        await CancelAPersonsSignInAsync(s, client);

        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.True(await s.StoredCancelAsync());
    }

    [Fact]
    public async Task StartAsync_ReadoptedContainerAPersonSignedOut_StaysSignedOutAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = new SignInScenario(client);
        s.UseSteamAnswers();

        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, expiresAt: DateTime.UtcNow.AddDays(30));
        // Before the restart a person canceled a sign-in; the daemon still reports the drop code for it.
        await sessionService.SetLoginCanceledByPersonAsync(sessionId, true, false);
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(RunningPersistentContainer(sessionId));
        s.Daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true), cacheStatusClock: s.Clock),
            gateway,
            () => client);

        await s.Daemon.StartAsync(CancellationToken.None);
        s.Session = SessionsOf(s.Daemon).Values.Single(session => session.IsPersistent);

        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.False(s.Session.LoggedInWhilePersonCanceled);
        Assert.True(CanceledSignInKept(s.Daemon, s.Session.Id));
        Assert.Null(s.Session.SignInAgainAfterDrop);
        s.SteamAnswers = true;
        s.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(0, client.StartLoginCallCount);
        Assert.Equal(DaemonAuthState.NotAuthenticated, s.Session.AuthState);
    }

    [Fact]
    public async Task StartAsync_ReadoptedLoggedInContainerAPersonCanceled_StaysSignedOutUntilTheNextDropAsync()
    {
        var client = new ScriptedLoginDaemonClient(alreadyLoggedIn: true);
        using var s = new SignInScenario(client);
        var (options, database) = NewDatabase();
        s.Database = options;
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, expiresAt: DateTime.UtcNow.AddDays(30));
        await sessionService.SetLoginCanceledByPersonAsync(sessionId, true, false);
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(RunningPersistentContainer(sessionId));
        s.Daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true), cacheStatusClock: s.Clock),
            gateway,
            () => client);

        await s.Daemon.StartAsync(CancellationToken.None);
        s.Session = SessionsOf(s.Daemon).Values.Single(session => session.IsPersistent);

        Assert.True(CanceledSignInKept(s.Daemon, s.Session.Id));
        Assert.Equal(DaemonAuthState.NotAuthenticated, s.Session.AuthState);
        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.True(s.Session.LoggedInWhilePersonCanceled);
        Assert.True(await s.StoredWitnessAsync());
        Assert.Equal(0, s.Session.SignInCount);
        Assert.Null(s.Session.SignInAgainAfterDrop);

        await s.RaiseDropAsync();

        Assert.False(s.Session.LoginCanceledByPerson);
        Assert.False(s.Session.LoggedInWhilePersonCanceled);
        Assert.False(await s.StoredCancelAsync());
        Assert.False(await s.StoredWitnessAsync());
        Assert.NotNull(s.Session.SignInAgainAfterDrop);
        Assert.Equal(DaemonAuthState.NotAuthenticated, s.Session.AuthState);
    }

    [Fact]
    public async Task StartAsync_ReadoptedDropAfterTheDaemonHadReachedLoggedIn_SignsInOnceAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = new SignInScenario(client);
        s.UseSteamAnswers();

        var (options, database) = NewDatabase();
        s.Database = options;
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, expiresAt: DateTime.UtcNow.AddDays(30));
        // The restart is after the daemon had reached logged-in and then dropped. The witness is on the row.
        await sessionService.SetLoginCanceledByPersonAsync(sessionId, true, true);
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(RunningPersistentContainer(sessionId));
        s.Daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true), cacheStatusClock: s.Clock),
            gateway,
            () => client);

        await s.Daemon.StartAsync(CancellationToken.None);
        s.Session = SessionsOf(s.Daemon).Values.Single(session => session.IsPersistent);

        Assert.False(s.Session.LoginCanceledByPerson);
        Assert.False(s.Session.LoggedInWhilePersonCanceled);
        Assert.False(CanceledSignInKept(s.Daemon, s.Session.Id));
        Assert.NotNull(s.Session.SignInAgainAfterDrop);
        Assert.Equal(0, client.StartLoginCallCount);
        Assert.Equal(DaemonAuthState.NotAuthenticated, s.Session.AuthState);
        Assert.False(await s.StoredCancelAsync());
        Assert.False(await s.StoredWitnessAsync());
    }

    [Fact]
    public void APersonsCancelLeavesAScheduledRunSignedOut()
    {
        var session = new DaemonSession { Id = "scheduled", LoginCanceledByPerson = true };

        Assert.True(ScheduledPrefillRunGates.PersonCanceledSignIn(session));

        session.LoginCanceledByPerson = false;
        Assert.False(ScheduledPrefillRunGates.PersonCanceledSignIn(session));
    }

    [Fact]
    public async Task AnAutomaticSignInSkipsAContainerAPersonSignedOutAsync()
    {
        // The scheduled run signs in with this call when the daemon reports the drop code.
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        await CancelAPersonsSignInAsync(s, client);
        var starts = client.StartLoginCallCount;

        Assert.Null(await s.Daemon.AttemptHeadlessPersistentSelfAuthAsync(s.Session));

        Assert.Equal(starts, client.StartLoginCallCount);
        Assert.Equal(DaemonAuthState.NotAuthenticated, s.Session.AuthState);
        Assert.False(s.Session.SuppressLoginChallengePublication);
    }

    [Fact]
    public async Task ARecreatedContainerStaysSignedOutAfterAPersonsCancelAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        var (options, daemon, _, priorSessionId) = SetupRecreateScenario(DateTime.UtcNow.AddDays(30), clientFactory: () => client);
        await using (var context = new AppDbContext(options))
        {
            var prior = await context.PrefillSessions.SingleAsync(row => row.SessionId == priorSessionId);
            prior.LoginCanceledByPerson = true;
            await context.SaveChangesAsync();
        }

        await daemon.StartAsync(CancellationToken.None);
        await daemon.LastHeadlessSelfAuthAttempt!;

        var session = SessionsOf(daemon).Values.Single(candidate => candidate.IsPersistent);
        Assert.Equal(0, client.StartLoginCallCount);
        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);
        Assert.True(session.LoginCanceledByPerson);
        // Stored on the new container's own row, so a manager restart keeps it.
        Assert.True((await GetRowAsync(options, session.Id))!.LoginCanceledByPerson);
        daemon.Dispose();
    }

    [Fact]
    public async Task APersonsCancelBeforeTheNewRowIsWrittenIsStoredOnItAsync()
    {
        var held = new HeldSave(entry => entry.State == EntityState.Added);
        var client = new ScriptedLoginDaemonClient();
        var (options, daemon, _, _) = SetupRecreateScenario(DateTime.UtcNow.AddDays(30), clientFactory: () => client, interceptors: [held]);
        using var owned = daemon;
        held.Armed = true;

        // The new container is registered and its row is not written yet when the person cancels.
        var start = daemon.StartAsync(CancellationToken.None);
        await held.Entered.Task.WaitAsync(TestTimeout);
        var session = SessionsOf(daemon).Values.Single(candidate => candidate.IsPersistent);
        await daemon.CancelLoginAsync(session.Id);
        Assert.True(session.LoginCanceledByPerson);

        held.Release.SetResult();
        await start.WaitAsync(TestTimeout);
        await daemon.LastHeadlessSelfAuthAttempt!;

        Assert.True((await GetRowAsync(options, session.Id))!.LoginCanceledByPerson);
    }

    [Fact]
    public async Task AnImageUpdateAfterAPersonsCancelLeavesTheNewContainerSignedOutAsync()
    {
        var image = SignedOutContainerWithANewerImage();
        using var daemon = image.Daemon;
        var (session, client, sessions) = (image.Session, image.Client, image.Sessions);
        // A person's cancel just stopped the retry, which has not finished ending.
        session.SignInAgainAfterDrop = new TaskCompletionSource().Task;

        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        await daemon.LastHeadlessSelfAuthAttempt!;
        var successor = Assert.Single(SessionsOf(daemon).Values, current => current.IsPersistent);
        Assert.NotSame(session, successor);
        Assert.Equal(0, client.StartLoginCallCount);
        Assert.True(successor.LoginCanceledByPerson);
        // Stored on the new container's own row, so a manager restart keeps it.
        Assert.True((await sessions.GetSessionAsync(successor.Id))!.LoginCanceledByPerson);
    }

    [Fact]
    public async Task AnAutomaticSignInSkippedAfterAPersonsCancelLetsAnImageUpdateApplyAsync()
    {
        var image = SignedOutContainerWithANewerImage();
        using var daemon = image.Daemon;
        var (session, client) = (image.Session, image.Client);

        // The scheduled run tries the automatic sign-in, which the person's cancel turns away.
        Assert.Null(await daemon.AttemptHeadlessPersistentSelfAuthAsync(session));
        Assert.True(session.LoginSettled);

        await daemon.ReconcilePersistentImageAsync(CancellationToken.None);

        var successor = Assert.Single(SessionsOf(daemon).Values, current => current.IsPersistent);
        Assert.NotSame(session, successor);
        Assert.Equal(0, client.StartLoginCallCount);
    }

    [Fact]
    public async Task AnAutomaticSignInThatFindsAPersonsPromptOpenLeavesTheSessionSettledAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        // Set by hand: a cached prompt on a settled session.
        s.Session.PendingLoginChallenge = UsernameChallenge("prompt-1");
        Assert.True(s.Session.LoginSettled);

        Assert.Null(await s.Daemon.AttemptHeadlessPersistentSelfAuthAsync(s.Session));

        Assert.True(s.Session.LoginSettled);
    }

    [Fact]
    public async Task AnAutomaticSignInWhoseStatusReadFailsLeavesTheSessionSettledAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        Assert.True(s.Session.LoginSettled);
        client.StatusHandler = _ => Task.FromException<DaemonStatus?>(new IOException("The daemon closed the connection."));

        await Assert.ThrowsAsync<IOException>(() => s.Daemon.AttemptHeadlessPersistentSelfAuthAsync(s.Session));

        Assert.True(s.Session.LoginSettled);
    }

    [Fact]
    public async Task APersonsCancelOfAFinishedSignInLeavesTheNextDropToTheRetryAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        // The sign-in finished before the person's cancel reached the server; the daemon announces nothing.
        client.LoginFinished = true;

        await s.Daemon.CancelLoginAsync(s.Session.Id, loginAttempt: s.Session.LoginAttempt);

        Assert.Equal(DaemonAuthState.Authenticated, s.Session.AuthState);
        Assert.False(s.Session.LoginCanceledByPerson);
        Assert.False(await s.StoredCancelAsync());
        await s.RaiseDropAsync();
        Assert.NotNull(s.Session.SignInAgainAfterDrop);
    }

    [Fact]
    public async Task APersonsCancelAfterTheSignInEndedWithTheDropCodeStopsTheWaitingRetryAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        var timers = s.Clock.ActiveTimerCount;
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));

        // The person's Log in gets no answer from Steam; the daemon ends it with the drop code, which starts a retry.
        client.NeverAuthenticates = true;
        client.NoAnswerErrorCode = "auth-lost";
        await s.Daemon.StartLoginAsync(s.Session.Id).WaitAsync(TestTimeout);
        var retry = s.Session.SignInAgainAfterDrop;
        Assert.NotNull(retry);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);
        var starts = client.StartLoginCallCount;

        // The person's Cancel of that sign-in arrives afterwards.
        await s.Daemon.CancelLoginAsync(s.Session.Id, loginAttempt: s.Session.LoginAttempt);
        await retry!.WaitAsync(TestTimeout);

        client.NeverAuthenticates = false;
        s.SteamAnswers = true;
        s.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(starts, client.StartLoginCallCount);
        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.True(await s.StoredCancelAsync());
        Assert.Equal(DaemonAuthState.NotAuthenticated, s.Session.AuthState);
    }

    [Fact]
    public async Task APersonsCancelThatJoinsTheExpiryCancelIsStillTheirsAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        var (personsLogin, attempt) = await StartAHeldPersonsSignInAsync(s, client);
        client.HoldCancelLogin = true;
        client.CancelErrorCode = "auth-lost";

        var expiry = s.Daemon.CancelLoginAsync(
            s.Session.Id, loginAttempt: attempt, stopReason: "common.notifications.warnings.signInExpired");
        await client.CancelLoginEntered.Task.WaitAsync(TestTimeout);
        var persons = s.Daemon.CancelLoginAsync(s.Session.Id);
        Assert.True(s.Session.LoginCanceledByPerson);

        client.ReleaseCancelLogin.SetResult();
        await expiry.WaitAsync(TestTimeout);
        await persons.WaitAsync(TestTimeout);
        client.ReleaseStartLogin.SetResult();
        await Record.ExceptionAsync(() => personsLogin);

        Assert.Null(s.Session.SignInAgainAfterDrop);
        Assert.True(await s.StoredCancelAsync());
    }

    // A cancel the daemon did not acknowledge is still the person's cancel of a running sign-in: only their
    // accepted Log in or a completed sign-in clears the mark.
    [Fact]
    public async Task AFailedCancelKeepsThePersonsMarkAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        client.CancelAcknowledged = false;

        await Assert.ThrowsAsync<ConflictException>(() => s.Daemon.CancelLoginAsync(s.Session.Id));

        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.True(await s.StoredCancelAsync());
        await s.RaiseDropAsync();
        Assert.Null(s.Session.SignInAgainAfterDrop);
    }

    [Fact]
    public async Task APersonsCancelJoiningACancelThatFailsKeepsTheMarkAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        var (personsLogin, attempt) = await StartAHeldPersonsSignInAsync(s, client);
        client.HoldCancelLogin = true;
        client.CancelAcknowledged = false;

        var expiry = s.Daemon.CancelLoginAsync(
            s.Session.Id, loginAttempt: attempt, stopReason: "common.notifications.warnings.signInExpired");
        await client.CancelLoginEntered.Task.WaitAsync(TestTimeout);
        var persons = s.Daemon.CancelLoginAsync(s.Session.Id);
        Assert.True(s.Session.LoginCanceledByPerson);

        client.ReleaseCancelLogin.SetResult();
        await Assert.ThrowsAsync<ConflictException>(() => expiry);
        await Assert.ThrowsAsync<ConflictException>(() => persons);
        client.ReleaseStartLogin.SetResult();
        await Record.ExceptionAsync(() => personsLogin);

        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.True(await s.StoredCancelAsync());
    }

    [Fact]
    public async Task APersonsLogInClearsTheStoredCancelAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        await CancelAPersonsSignInAsync(s, client);
        Assert.True(await s.StoredCancelAsync());

        // A Log in that Steam does not answer, so no applied sign-in clears the mark instead.
        client.NeverAuthenticates = true;
        await s.Daemon.StartLoginAsync(s.Session.Id).WaitAsync(TestTimeout);

        Assert.False(s.Session.LoginCanceledByPerson);
        Assert.False(await s.StoredCancelAsync());
    }

    [Fact]
    public async Task ALoggedInStatusKeepsAPersonsCancelAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        await CancelAPersonsSignInAsync(s, client);
        var signIns = s.Session.SignInCount;

        await s.Client.RaiseStatusAsync(SteamStatus(s.Instance, signedIn: true));

        Assert.Equal(DaemonAuthState.NotAuthenticated, s.Session.AuthState);
        Assert.Equal(signIns, s.Session.SignInCount);
        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.True(s.Session.LoggedInWhilePersonCanceled);
        Assert.True(await s.StoredCancelAsync());
        Assert.True(await s.StoredWitnessAsync());
        Assert.Null(s.Session.SignInAgainAfterDrop);
    }

    [Fact]
    public async Task ALoggedInStatusDuringACancelWriteKeepsTheStoredMarkAsync()
    {
        var held = new HeldSave(entry => entry.Property(row => row.LoginCanceledByPerson).IsModified);
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client, interceptors: [held]);
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));

        // The cancel's write of its mark is slow. A logged-in report while that cancel is still
        // running does not arm the later-drop witness, and the mark stays on the row.
        held.Armed = true;
        var cancel = s.Daemon.CancelLoginAsync(s.Session.Id);
        try
        {
            await held.Entered.Task.WaitAsync(TestTimeout);
            await s.Client.RaiseStatusAsync(SteamStatus(s.Instance, signedIn: true)).WaitAsync(TestTimeout);

            Assert.False(s.Session.LoggedInWhilePersonCanceled);
            Assert.True(s.Session.LoginCanceledByPerson);
            Assert.Equal(DaemonAuthState.NotAuthenticated, s.Session.AuthState);
            Assert.Null(s.Session.SignInAgainAfterDrop);
        }
        finally
        {
            held.Release.TrySetResult();
        }

        await cancel.WaitAsync(TestTimeout);

        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.False(s.Session.LoggedInWhilePersonCanceled);
        Assert.True(await s.StoredCancelAsync());
        Assert.False(await s.StoredWitnessAsync());
        Assert.Null(s.Session.SignInAgainAfterDrop);

        await s.Client.RaiseStatusAsync(SteamStatus(s.Instance, signedIn: true));
        Assert.True(s.Session.LoggedInWhilePersonCanceled);
        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.True(await s.StoredWitnessAsync());
        Assert.Null(s.Session.SignInAgainAfterDrop);
    }

    [Fact]
    public async Task ACancelThatMeetsTheRetrysOwnSignInIsNotAPersonsAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        client.HoldStartLogin = true;
        var timers = s.Clock.ActiveTimerCount;

        await s.RaiseDropAsync();
        Assert.NotNull(s.Session.SignInAgainAfterDrop);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);
        s.Clock.Advance(SignInAgainFirstWait);
        await WaitForStartsAsync(client, s.StartsBeforeDrop + 1);

        // A discarded schedule edit cancels whatever sign-in is current, here the retry's own.
        var discard = s.Daemon.CancelLoginAsync(s.Session.Id);
        Assert.False(s.Session.LoginCanceledByPerson);

        client.ReleaseStartLogin.SetResult();
        await Record.ExceptionAsync(() => discard.WaitAsync(TestTimeout));
        Assert.False(s.Session.LoginCanceledByPerson);
        Assert.False(await s.StoredCancelAsync());
    }

    [Fact]
    public async Task APersonsCancelOfALogInWaitingOnTheAutomaticSignInKeepsItStoppedAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        client.HoldStartLogin = true;
        var timers = s.Clock.ActiveTimerCount;
        await s.RaiseDropAsync();
        await s.Clock.WaitForActiveTimersAsync(timers + 1);
        s.Clock.Advance(SignInAgainFirstWait);
        await WaitForStartsAsync(client, s.StartsBeforeDrop + 1);

        // The person presses Log in while the automatic sign-in runs; their Log in waits for it to end.
        client.HoldCancelLogin = true;
        var loginId = Guid.NewGuid();
        var login = s.Daemon.StartLoginForEditAsync(s.Session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId);
        await client.CancelLoginEntered.Task.WaitAsync(TestTimeout);

        Assert.False(await s.Daemon.CancelLoginAsync(s.Session.Id, loginId: loginId));
        Assert.True(s.Session.LoginCanceledByPerson);

        // The automatic sign-in ends with the drop code, as the daemon writes it for a cancel with no prompt open.
        client.CancelErrorCode = "auth-lost";
        client.ReleaseCancelLogin.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        var starts = client.StartLoginCallCount;
        await s.RaiseDropAsync();

        Assert.Null(s.Session.SignInAgainAfterDrop);
        Assert.True(await s.StoredCancelAsync());
        s.SteamAnswers = true;
        s.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(starts, client.StartLoginCallCount);
    }

    // The server never accepted that Log in, so its cancel stops nothing; the id is kept only to refuse it.
    [Fact]
    public async Task APersonsCancelThatReachesTheServerBeforeItsLogInLeavesTheRetryRunningAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        var timers = s.Clock.ActiveTimerCount;
        await s.RaiseDropAsync();
        Assert.NotNull(s.Session.SignInAgainAfterDrop);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);

        var loginId = Guid.NewGuid();
        Assert.False(await s.Daemon.CancelLoginAsync(s.Session.Id, loginId: loginId));
        await Assert.ThrowsAsync<ConflictException>(
            () => s.Daemon.StartLoginForEditAsync(s.Session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId));

        Assert.False(s.Session.LoginCanceledByPerson);
        Assert.False(await s.StoredCancelAsync());
        s.Clock.Advance(SignInAgainFirstWait);
        await WaitForStartsAsync(client, s.StartsBeforeDrop + 1);
    }

    [Fact]
    public async Task APersonsCancelOfTheirSavedLoginStopsTheAutomaticSignInAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        s.Daemon.HeldSavedLogin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // "Use my saved login" also hides the daemon's prompts while it runs.
        var loginId = Guid.NewGuid();
        var login = s.Daemon.ReuseIntegrationLoginForEditAsync(
            s.Session.Id, null, () => { }, Guid.NewGuid(), lease: new IntegrationLease(s.Daemon, 0, null, () => { }, () => { }), loginId: loginId);
        await s.Daemon.SavedLoginEntered.Task.WaitAsync(TestTimeout);
        Assert.True(s.Session.SuppressLoginChallengePublication);

        client.CancelErrorCode = "auth-lost";
        await s.Daemon.CancelLoginAsync(s.Session.Id, loginId: loginId);
        s.Daemon.HeldSavedLogin.SetResult();
        await Record.ExceptionAsync(() => login);

        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.True(await s.StoredCancelAsync());
        Assert.Null(s.Session.SignInAgainAfterDrop);
    }

    [Fact]
    public async Task APersonsCancelWhileTheirLogInEndsAnUnfinishedSignInKeepsTheMarkAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        // Set by hand: a sign-in whose prompt was answered and which never ended, with its card still up.
        s.Session.AuthState = DaemonAuthState.LoggingIn;
        s.Session.LoginOperationId = Guid.NewGuid();
        s.Session.LoginDispatch = null;
        client.HoldCancelLogin = true;

        var loginId = Guid.NewGuid();
        var login = s.Daemon.StartLoginForEditAsync(s.Session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId);
        await client.CancelLoginEntered.Task.WaitAsync(TestTimeout);
        await s.Daemon.CancelLoginAsync(s.Session.Id, loginId: loginId);

        client.ReleaseCancelLogin.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);

        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.True(await s.StoredCancelAsync());
    }

    [Fact]
    public async Task APersonsCancelOfALogInWhoseEarlierSignInCannotBeEndedKeepsTheMarkAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        // Set by hand: a sign-in whose prompt was answered and which never ended, with its card still up.
        s.Session.AuthState = DaemonAuthState.LoggingIn;
        s.Session.LoginOperationId = Guid.NewGuid();
        s.Session.LoginDispatch = null;
        client.HoldCancelLogin = true;
        client.CancelAcknowledged = false;

        var loginId = Guid.NewGuid();
        var login = s.Daemon.StartLoginForEditAsync(s.Session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId);
        await client.CancelLoginEntered.Task.WaitAsync(TestTimeout);
        Assert.False(await s.Daemon.CancelLoginAsync(s.Session.Id, loginId: loginId));

        client.ReleaseCancelLogin.SetResult();
        var refusal = await Assert.ThrowsAsync<ConflictException>(() => login.WaitAsync(TestTimeout));
        Assert.Equal("prefill.persistent.loginNotCanceled", refusal.StageKey);

        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.True(await s.StoredCancelAsync());
        await s.RaiseDropAsync();
        Assert.Null(s.Session.SignInAgainAfterDrop);
    }

    [Fact]
    public async Task ACancelOfALogInRefusedAsBusyLeavesTheRetryRunningAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        var timers = s.Clock.ActiveTimerCount;
        await s.RaiseDropAsync();
        Assert.NotNull(s.Session.SignInAgainAfterDrop);
        await s.Clock.WaitForActiveTimersAsync(timers + 1);
        // A prefill run the drop cut off is still stopping.
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        s.Session.Runs[runId] = new DaemonRun
        {
            PrefillRunId = runId,
            SessionId = s.Session.Id,
            DaemonInstanceId = "instance",
            Options = new DaemonRunOptions(),
            Snapshot = new DaemonRunSnapshot
            {
                OperationId = runId.ToString(),
                DaemonInstanceId = "instance",
                StartedAt = now,
                UpdatedAt = now,
                State = "completed"
            },
            TerminalCompletedFlag = 2,
            CompletedAtUtc = DateTime.UtcNow
        };
        s.Session.Recovering = true;

        var loginId = Guid.NewGuid();
        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => s.Daemon.StartLoginForEditAsync(s.Session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId));
        Assert.Equal("errors.prefill.containerBusy", refusal.StageKey);
        Assert.False(await s.Daemon.CancelLoginAsync(s.Session.Id, loginId: loginId));

        Assert.False(s.Session.LoginCanceledByPerson);
        Assert.False(await s.StoredCancelAsync());
        s.Session.Runs.Clear();
        s.Session.Recovering = false;
        s.Clock.Advance(SignInAgainFirstWait);
        await WaitForStartsAsync(client, s.StartsBeforeDrop + 1);
    }

    [Fact]
    public async Task ACancelOfALogInRefusedOnAnErroredSessionSetsNoMarkAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        s.Session.Status = DaemonSessionStatus.Error;

        var loginId = Guid.NewGuid();
        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => s.Daemon.StartLoginForEditAsync(s.Session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId));
        Assert.Equal("errors.prefill.sessionReplaced", refusal.StageKey);
        Assert.False(await s.Daemon.CancelLoginAsync(s.Session.Id, loginId: loginId));

        Assert.False(s.Session.LoginCanceledByPerson);
        Assert.False(await s.StoredCancelAsync());
    }

    [Fact]
    public async Task ACancelOfALogInRefusedWhileAnotherSignInHoldsTheLockSetsNoMarkAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        // Stands for another sign-in that keeps the login lock past the Log in's wait.
        await s.Session.LoginLock.WaitAsync(TestTimeout);
        try
        {
            var loginId = Guid.NewGuid();
            var refusal = await Assert.ThrowsAsync<ConflictException>(
                () => s.Daemon.StartLoginForEditAsync(s.Session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId));
            Assert.Equal("errors.prefill.loginInProgress", refusal.StageKey);
            Assert.False(await s.Daemon.CancelLoginAsync(s.Session.Id, loginId: loginId));
        }
        finally
        {
            s.Session.LoginLock.Release();
        }

        Assert.False(s.Session.LoginCanceledByPerson);
        Assert.False(await s.StoredCancelAsync());
    }

    [Fact]
    public async Task APersonsCancelStoresTheMarkAfterTheWriteIsRetriedAsync()
    {
        var failing = new FailingCancelMarkSave { FailuresLeft = 1 };
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client, interceptors: [failing]);

        await CancelAPersonsSignInAsync(s, client);

        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.True(await s.StoredCancelAsync());
        Assert.Equal(0, failing.FailuresLeft);
    }

    [Fact]
    public async Task APersonsCancelStillReachesTheDaemonWhenItsMarkCannotBeStoredAsync()
    {
        var failing = new FailingCancelMarkSave();
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client, interceptors: [failing]);
        var (personsLogin, _) = await StartAHeldPersonsSignInAsync(s, client);
        client.CancelErrorCode = "auth-lost";
        failing.Armed = true;

        await Assert.ThrowsAsync<DbUpdateException>(() => s.Daemon.CancelLoginAsync(s.Session.Id));
        client.ReleaseStartLogin.SetResult();
        await Record.ExceptionAsync(() => personsLogin);

        Assert.Equal(1, client.CancelLoginCallCount);
        Assert.Equal(DaemonAuthState.NotAuthenticated, s.Session.AuthState);
        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.False(await s.StoredCancelAsync());
        Assert.Null(s.Session.SignInAgainAfterDrop);
    }

    [Fact]
    public async Task APersonsCancelJoiningAnotherEndsWhenItsMarkCannotBeStoredAsync()
    {
        var failing = new FailingCancelMarkSave();
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client, interceptors: [failing]);
        var (personsLogin, attempt) = await StartAHeldPersonsSignInAsync(s, client);
        client.HoldCancelLogin = true;
        failing.Armed = true;

        var expiry = s.Daemon.CancelLoginAsync(
            s.Session.Id, loginAttempt: attempt, stopReason: "common.notifications.warnings.signInExpired");
        await client.CancelLoginEntered.Task.WaitAsync(TestTimeout);
        var persons = s.Daemon.CancelLoginAsync(s.Session.Id);

        client.ReleaseCancelLogin.SetResult();
        await expiry.WaitAsync(TestTimeout);
        Assert.IsType<DbUpdateException>(await Record.ExceptionAsync(() => persons.WaitAsync(TestTimeout)));
        client.ReleaseStartLogin.SetResult();
        await Record.ExceptionAsync(() => personsLogin);

        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.False(await s.StoredCancelAsync());
    }

    [Fact]
    public async Task AFailedCancelReportsItsOwnFailureWhenTheMarkCannotBeStoredAsync()
    {
        var failing = new FailingCancelMarkSave();
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client, interceptors: [failing]);
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        client.CancelAcknowledged = false;
        failing.Armed = true;

        await Assert.ThrowsAsync<ConflictException>(() => s.Daemon.CancelLoginAsync(s.Session.Id));

        Assert.Equal(1, client.CancelLoginCallCount);
        Assert.True(s.Session.LoginCanceledByPerson);
    }

    [Fact]
    public async Task ALoggedInStatusAfterAPersonsCancelIsNotAnnouncedAsync()
    {
        var failing = new FailingCancelMarkSave();
        var recorder = DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client, interceptors: [failing], notifications: recorder);
        await CancelAPersonsSignInAsync(s, client);
        failing.Armed = true;
        var announced = ((EventRecordingNotificationsProxy)(object)recorder).EventNames.Count(name => name == SignalREvents.AuthStateChanged);

        await s.Client.RaiseStatusAsync(SteamStatus(s.Instance, signedIn: true));

        Assert.Equal(DaemonAuthState.NotAuthenticated, s.Session.AuthState);
        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.Equal(
            announced,
            ((EventRecordingNotificationsProxy)(object)recorder).EventNames.Count(name => name == SignalREvents.AuthStateChanged));
    }

    [Fact]
    public async Task AnOpenTabSignInClockDoesNotStoreAPersonsCancelAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        var (personsLogin, attempt) = await StartAHeldPersonsSignInAsync(s, client);

        await s.Daemon.CancelLoginAsync(
            s.Session.Id, loginAttempt: attempt, stopReason: "common.notifications.warnings.signInExpired");
        client.ReleaseStartLogin.SetResult();
        await Record.ExceptionAsync(() => personsLogin);

        Assert.False(s.Session.LoginCanceledByPerson);
        Assert.False(await s.StoredCancelAsync());
        await s.RaiseDropAsync();
        Assert.NotNull(s.Session.SignInAgainAfterDrop);
    }

    [Fact]
    public async Task AnOpenTabClockForALogInThatHasNotStartedStoresNoPersonsCancelAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        await s.Session.LoginLock.WaitAsync(TestTimeout);
        var released = false;
        try
        {
            var loginId = Guid.NewGuid();
            var login = s.Daemon.StartLoginForEditAsync(s.Session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId);
            await WaitForRecordedLoginAsync(s.Session, loginId);

            Assert.False(await s.Daemon.CancelLoginAsync(
                s.Session.Id, loginId: loginId, stopReason: "common.notifications.warnings.signInExpired"));
            Assert.False(s.Session.LoginCanceledByPerson);

            s.Session.LoginLock.Release();
            released = true;
            await Record.ExceptionAsync(() => login);
        }
        finally
        {
            if (!released) s.Session.LoginLock.Release();
        }

        Assert.False(s.Session.LoginCanceledByPerson);
        Assert.False(await s.StoredCancelAsync());
    }

    [Fact]
    public async Task APersonsCancelDuringStartupHeadlessStopsThatTryAsync()
    {
        var client = new ScriptedLoginDaemonClient { HoldStartLogin = true };
        var (_, daemon, _, _) = SetupRecreateScenario(DateTime.UtcNow.AddDays(30), clientFactory: () => client);
        try
        {
            await daemon.StartAsync(CancellationToken.None).WaitAsync(TestTimeout);
            await client.StartLoginEntered.Task.WaitAsync(TestTimeout);
            var session = SessionsOf(daemon).Values.Single(candidate => candidate.IsPersistent);
            var loginId = Guid.NewGuid();
            var persons = daemon.StartLoginForEditAsync(session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId);
            await WaitForRecordedLoginAsync(session, loginId);

            Assert.False(await daemon.CancelLoginAsync(session.Id, loginId: loginId));
            Assert.True(session.LoginCanceledByPerson);

            client.ReleaseStartLogin.TrySetResult();
            await daemon.LastHeadlessSelfAuthAttempt!.WaitAsync(TestTimeout);
            await Record.ExceptionAsync(() => persons);

            Assert.True(session.LoginCanceledByPerson);
            Assert.NotEqual(DaemonAuthState.Authenticated, session.AuthState);
        }
        finally
        {
            client.ReleaseStartLogin.TrySetResult();
            daemon.Dispose();
        }
    }

    [Fact]
    public async Task AnUnconfirmedStartupCancelOfALoggedInDaemonStaysSignedOutAsync()
    {
        var loggedIn = 0;
        var client = new ScriptedLoginDaemonClient { HoldStartLogin = true, CancelAcknowledged = false };
        client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(new DaemonStatus
        {
            Type = "status",
            Status = Interlocked.CompareExchange(ref loggedIn, 0, 0) == 1 ? "logged-in" : "awaiting-login",
            Timestamp = DateTime.UtcNow
        });
        var (_, daemon, _, _) = SetupRecreateScenario(DateTime.UtcNow.AddDays(30), clientFactory: () => client);
        try
        {
            await daemon.StartAsync(CancellationToken.None).WaitAsync(TestTimeout);
            await client.StartLoginEntered.Task.WaitAsync(TestTimeout);
            var session = SessionsOf(daemon).Values.Single(candidate => candidate.IsPersistent);
            var loginId = Guid.NewGuid();
            var persons = daemon.StartLoginForEditAsync(session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId);
            await WaitForRecordedLoginAsync(session, loginId);
            Interlocked.Exchange(ref loggedIn, 1);

            Assert.False(await daemon.CancelLoginAsync(session.Id, loginId: loginId));
            client.ReleaseStartLogin.TrySetResult();
            await daemon.LastHeadlessSelfAuthAttempt!.WaitAsync(TestTimeout);
            await Record.ExceptionAsync(() => persons);

            Assert.Equal(DaemonSessionStatus.Active, session.Status);
            Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);
            Assert.True(session.LoginCanceledByPerson);
            Assert.Equal(1, client.CancelLoginCallCount);
        }
        finally
        {
            client.ReleaseStartLogin.TrySetResult();
            daemon.Dispose();
        }
    }

    [Fact]
    public async Task AnUnconfirmedStartupCancelThatLeavesTheLoginInFlightReplacesTheDaemonAsync()
    {
        var client = new ScriptedLoginDaemonClient
        {
            HoldStartLogin = true,
            CancelAcknowledged = false
        };
        client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(new DaemonStatus
        {
            Type = "status",
            Status = "awaiting-login",
            Timestamp = DateTime.UtcNow
        });
        var (_, daemon, _, _) = SetupRecreateScenario(DateTime.UtcNow.AddDays(30), clientFactory: () => client);
        try
        {
            await daemon.StartAsync(CancellationToken.None).WaitAsync(TestTimeout);
            await client.StartLoginEntered.Task.WaitAsync(TestTimeout);
            var session = SessionsOf(daemon).Values.Single(candidate => candidate.IsPersistent);
            var loginId = Guid.NewGuid();
            var persons = daemon.StartLoginForEditAsync(session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId);
            await WaitForRecordedLoginAsync(session, loginId);

            Assert.False(await daemon.CancelLoginAsync(session.Id, loginId: loginId));
            client.ReleaseStartLogin.TrySetResult();
            await daemon.LastHeadlessSelfAuthAttempt!.WaitAsync(TestTimeout);
            await Record.ExceptionAsync(() => persons);

            Assert.Equal(DaemonSessionStatus.Error, session.Status);
            Assert.NotEqual(DaemonAuthState.Authenticated, session.AuthState);
            Assert.True(session.LoginCanceledByPerson);
            Assert.Equal(3, client.CancelLoginCallCount);
        }
        finally
        {
            client.ReleaseStartLogin.TrySetResult();
            daemon.Dispose();
        }
    }

    [Fact]
    public async Task AnUnconfirmedStartupCancelWhoseStatusReadsThrowReplacesTheDaemonAsync()
    {
        var readsAfterRelease = 0;
        var settleReadsFail = 0;
        var client = new ScriptedLoginDaemonClient { HoldStartLogin = true, CancelAcknowledged = false };
        var signedOut = new DaemonStatus
        {
            Type = "status",
            Status = "awaiting-login",
            Timestamp = DateTime.UtcNow
        };
        client.StatusHandler = _ =>
        {
            if (Volatile.Read(ref settleReadsFail) == 0)
            {
                return Task.FromResult<DaemonStatus?>(signedOut);
            }

            if (Interlocked.Increment(ref readsAfterRelease) <= 2)
            {
                return Task.FromResult<DaemonStatus?>(signedOut);
            }

            throw new IOException("The daemon status could not be read.");
        };
        var (_, daemon, _, _) = SetupRecreateScenario(DateTime.UtcNow.AddDays(30), clientFactory: () => client);
        try
        {
            await daemon.StartAsync(CancellationToken.None).WaitAsync(TestTimeout);
            await client.StartLoginEntered.Task.WaitAsync(TestTimeout);
            var session = SessionsOf(daemon).Values.Single(candidate => candidate.IsPersistent);
            var loginId = Guid.NewGuid();
            var persons = daemon.StartLoginForEditAsync(session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId);
            await WaitForRecordedLoginAsync(session, loginId);

            Assert.False(await daemon.CancelLoginAsync(session.Id, loginId: loginId));
            Interlocked.Exchange(ref settleReadsFail, 1);
            client.ReleaseStartLogin.TrySetResult();
            await daemon.LastHeadlessSelfAuthAttempt!.WaitAsync(TestTimeout);
            await Record.ExceptionAsync(() => persons);

            Assert.Equal(DaemonSessionStatus.Error, session.Status);
            Assert.NotEqual(DaemonSessionStatus.Active, session.Status);
            Assert.Equal(
                "Automatic sign-in could not be cancelled cleanly. Start the session again to recover.",
                session.ErrorMessage);
            Assert.False(session.LoginSettled);
            Assert.True(session.LoginCanceledByPerson);
            Assert.Equal(DaemonAuthState.LoggingIn, session.AuthState);
        }
        finally
        {
            client.ReleaseStartLogin.TrySetResult();
            daemon.Dispose();
        }
    }

    [Fact]
    public async Task AnAbortedLogInDoesNotLeaveARequestALaterCancelCanMarkAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        await s.Session.LoginLock.WaitAsync(TestTimeout);
        try
        {
            var loginId = Guid.NewGuid();
            using var abort = new CancellationTokenSource();
            var login = s.Daemon.StartLoginForEditAsync(
                s.Session.Id, null, () => { }, Guid.NewGuid(), abort.Token, loginId);
            await WaitForRecordedLoginAsync(s.Session, loginId);

            await abort.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);

            Assert.True(s.Session.LoginRequests[loginId].Canceled);
            Assert.False(await s.Daemon.CancelLoginAsync(s.Session.Id, loginId: loginId));
        }
        finally
        {
            s.Session.LoginLock.Release();
        }

        Assert.False(s.Session.LoginCanceledByPerson);
        Assert.False(await s.StoredCancelAsync());
    }

    [Fact]
    public async Task ADeclinedLogoutLeavesTheSessionSettledAsync()
    {
        var client = new ScriptedLogoutDaemonClient(succeeds: false);
        using var daemon = NewSteamDaemon();
        var session = InjectedPersistentSession(daemon, client);
        session.AuthState = DaemonAuthState.Authenticated;
        session.LoginSettled = true;

        var result = await daemon.LogoutPersistentSessionAsync(session.Id);

        Assert.False(result.LoggedOut);
        Assert.True(session.LoginSettled);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
    }

    [Fact]
    public async Task ALogoutWhoseRoundTripThrowsLeavesTheSessionSettledAsync()
    {
        var client = new ThrowingLogoutDaemonClient();
        using var daemon = NewSteamDaemon();
        var session = InjectedPersistentSession(daemon, client);
        session.AuthState = DaemonAuthState.Authenticated;
        session.LoginSettled = true;

        var result = await daemon.LogoutPersistentSessionAsync(session.Id);

        Assert.False(result.LoggedOut);
        Assert.True(session.LoginSettled);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
    }

    [Fact]
    public async Task ALogoutTheCallerCancelsLeavesTheSessionSettledAsync()
    {
        var client = new TokenCanceledLogoutClient();
        using var daemon = NewSteamDaemon();
        var session = InjectedPersistentSession(daemon, client);
        session.AuthState = DaemonAuthState.Authenticated;
        session.LoginSettled = true;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => daemon.LogoutPersistentSessionAsync(session.Id, cancellation.Token));

        Assert.True(session.LoginSettled);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
    }

    // ---------- Helpers ----------

    private static bool CanceledSignInKept(PrefillDaemonServiceBase daemon, string sessionId)
    {
        var field = typeof(PrefillDaemonServiceBase).GetField(
            "_canceledAutomaticSignIn",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var kept = Assert.IsType<System.Collections.Concurrent.ConcurrentDictionary<string, byte>>(field.GetValue(daemon));
        return kept.ContainsKey(sessionId);
    }

    private static async Task WaitForRecordedLoginAsync(DaemonSession session, Guid loginId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var recorded = false;
        while (!recorded && DateTime.UtcNow < deadline)
        {
            lock (session.PrefillLock)
            {
                recorded = session.LoginRequests.ContainsKey(loginId);
            }
            if (!recorded) await Task.Delay(10);
        }
        Assert.True(recorded);
    }

    // Steam logged the account off and a person's Log in waits on the daemon with no prompt open.
    private static async Task<(Task<CredentialChallenge?> Login, long Attempt)> StartAHeldPersonsSignInAsync(
        SignInScenario s, ScriptedLoginDaemonClient client)
    {
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        client.HoldStartLogin = true;
        var login = s.Daemon.StartLoginAsync(s.Session.Id);
        await WaitForStartsAsync(client, s.StartsBeforeDrop + 1);
        return (login, s.Session.LoginAttempt);
    }

    private static async Task WaitForStartsAsync(ScriptedLoginDaemonClient client, int starts)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (client.StartLoginCallCount < starts && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.Equal(starts, client.StartLoginCallCount);
    }

    // An always-on Steam container a person's cancel left signed out, on image a while image b is ready.
    private (TestSteamDaemon Daemon, DaemonSession Session, ScriptedLoginDaemonClient Client, PrefillSessionService Sessions)
        SignedOutContainerWithANewerImage()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var instance = Guid.NewGuid().ToString();
        var signedOut = IdleStatus(instance);
        signedOut.Status = "awaiting-login";
        var client = new ScriptedLoginDaemonClient();
        client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(client.StartLoginCallCount > 0 ? IdleStatus(instance) : signedOut);
        var gateway = new RecordingContainerGateway { ResolvedImageId = "sha256:image-b" };
        var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)),
            gateway,
            () => client);
        var session = InjectedPersistentSession(daemon, client);
        session.ContainerId = "container-a";
        session.ContainerName = SteamPersistentContainerName;
        session.ConfiguredImage = "ghcr.io/regix1/steam-prefill-daemon:latest";
        session.ImageId = "sha256:image-a";
        session.Capabilities = signedOut;
        session.LoginSettled = true;
        session.AuthState = DaemonAuthState.NotAuthenticated;
        session.LoginCanceledByPerson = true;
        gateway.AddContainer(new FakeContainer
        {
            Id = session.ContainerId,
            Name = session.ContainerName,
            Running = true,
            ImageId = session.ImageId
        });
        return (daemon, session, client, sessions);
    }

    private sealed class TokenCanceledLogoutClient : TestDaemonClientBase
    {
        public override Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }

    // Fails writes of a session's cancel mark. Armed fails every one. FailuresLeft fails that many, then lets the write through.
    private sealed class FailingCancelMarkSave : SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public int FailuresLeft { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData saving, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (saving.Context!.ChangeTracker.Entries<PrefillSession>()
                    .Any(entry => entry.Property(row => row.LoginCanceledByPerson).IsModified
                        || entry.Property(row => row.LoggedInWhilePersonCanceled).IsModified)
                && (Armed || FailuresLeft > 0))
            {
                if (FailuresLeft > 0)
                {
                    FailuresLeft--;
                }

                throw new DbUpdateException("The database is unavailable.");
            }

            return ValueTask.FromResult(result);
        }
    }

    // Holds the first save of a session row that matches after it is armed until the test releases it.
    private sealed class HeldSave(Func<EntityEntry<PrefillSession>, bool> holds) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData saving, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && saving.Context!.ChangeTracker.Entries<PrefillSession>().Any(holds))
            {
                Armed = false;
                Entered.TrySetResult();
                await Release.Task;
            }
            return result;
        }
    }

    // Steam logged the account off, a person presses Log in, and cancels before Steam answers. The daemon
    // writes the drop code for that cancel, as it does for any cancel of a sign-in with no prompt open.
    private static async Task CancelAPersonsSignInAsync(SignInScenario s, ScriptedLoginDaemonClient client)
    {
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        Assert.Null(s.Session.SignInAgainAfterDrop);
        client.HoldStartLogin = true;
        client.CancelErrorCode = "auth-lost";

        var personsLogin = s.Daemon.StartLoginAsync(s.Session.Id);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (client.StartLoginCallCount == s.StartsBeforeDrop && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        await s.Daemon.CancelLoginAsync(s.Session.Id);

        client.ReleaseStartLogin.SetResult();
        await Record.ExceptionAsync(() => personsLogin);
    }

    private static CredentialChallenge UsernameChallenge(string challengeId) => new()
    {
        ChallengeId = challengeId,
        CredentialType = "username",
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddMinutes(5)
    };

    private static DaemonStatus SteamStatus(string instanceId, bool signedIn)
    {
        var status = IdleStatus(instanceId);
        status.Status = signedIn ? "logged-in" : "awaiting-login";
        status.ErrorCode = signedIn ? null : "auth-lost";
        return status;
    }

    // A signed-out status that carries no drop code: Steam logged the account off, or a person logged out.
    private static DaemonStatus LogOffStatus(string instanceId)
    {
        var status = SteamStatus(instanceId, signedIn: false);
        status.ErrorCode = null;
        return status;
    }

    private TestSteamDaemon NewSteamDaemon()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        return new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)),
            new RecordingContainerGateway());
    }

    private static DaemonSession InjectedTemporarySteamSession(TestSteamDaemon daemon, IDaemonClient client)
    {
        var session = new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = Guid.NewGuid(),
            Status = DaemonSessionStatus.Active,
            IsPersistent = false,
            AuthState = DaemonAuthState.Authenticated,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            Client = client
        };
        daemon.InjectSession(session);
        return session;
    }

    // Recreates the persistent Steam container with its saved login and waits for the create-time
    // sign-in, so the session starts signed in. The daemon stand-in then reports signed in only after a
    // login sent while Steam answers, as the real daemon does.
    private async Task<SignInScenario> StartSignedInSteamAsync(
        ScriptedLoginDaemonClient client, ILogger<SteamDaemonService>? logger = null, IInterceptor[]? interceptors = null,
        ISignalRNotificationService? notifications = null)
    {
        var s = new SignInScenario(client);
        var (database, daemon, _, _) = SetupRecreateScenario(
            DateTime.UtcNow.AddDays(30), clientFactory: () => client, notifications: notifications, logger: logger, cacheStatusClock: s.Clock,
            interceptors: interceptors);
        s.Daemon = daemon;
        s.Database = database;
        await daemon.StartAsync(CancellationToken.None);
        await daemon.LastHeadlessSelfAuthAttempt!;
        s.Session = SessionsOf(daemon).Values.Single(session => session.IsPersistent);
        Assert.Equal(DaemonAuthState.Authenticated, s.Session.AuthState);
        s.StartsBeforeDrop = client.StartLoginCallCount;
        s.AnswersAfterStarts = client.StartLoginCallCount;
        s.UseSteamAnswers();
        return s;
    }

    private sealed class SignInScenario(ScriptedLoginDaemonClient client) : IDisposable
    {
        public ScriptedLoginDaemonClient Client { get; } = client;
        public CacheStatusClock Clock { get; } = new(DateTimeOffset.UtcNow);
        public string Instance { get; } = Guid.NewGuid().ToString();
        public TestSteamDaemon Daemon { get; set; } = null!;
        public DaemonSession Session { get; set; } = null!;
        public DbContextOptions<AppDbContext> Database { get; set; } = null!;

        /// <summary>Whether the session's row holds a person's cancel of its last sign-in.</summary>
        public async Task<bool> StoredCancelAsync()
            => (await GetRowAsync(Database, Session.Id))!.LoginCanceledByPerson;

        /// <summary>Whether the session's row records that the daemon reached logged-in while that cancel held.</summary>
        public async Task<bool> StoredWitnessAsync()
            => (await GetRowAsync(Database, Session.Id))!.LoggedInWhilePersonCanceled;

        /// <summary>Logins the daemon had received before the drop.</summary>
        public int StartsBeforeDrop { get; set; }

        /// <summary>The daemon reads signed in only once Steam answers and it has received a login after this count.</summary>
        public int AnswersAfterStarts { get; set; }

        public bool SteamAnswers { get; set; }

        public void UseSteamAnswers()
            => Client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(
                SteamStatus(Instance, SteamAnswers && Client.StartLoginCallCount > AnswersAfterStarts));

        public Task RaiseDropAsync() => Client.RaiseStatusAsync(SteamStatus(Instance, signedIn: false));

        public void Dispose() => Daemon.Dispose();
    }
}
