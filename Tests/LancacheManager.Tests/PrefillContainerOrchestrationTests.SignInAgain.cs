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
using Microsoft.Extensions.DependencyInjection;
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

    // Steam, Epic or Xbox logs a signed-in account off without the drop code and the daemon keeps the saved login,
    // so the next manager, and a recreate after the container is gone, must not sign it in again without a person.
    // Steam reports it as awaiting-login; Epic and Xbox report it through the status read as not logged in.
    [Theory]
    [InlineData(PrefillPlatform.Steam, PersistenceMode.KillOnRestart)]
    [InlineData(PrefillPlatform.Steam, PersistenceMode.KeepAcrossRestart)]
    [InlineData(PrefillPlatform.Steam, PersistenceMode.FullPersistence)]
    [InlineData(PrefillPlatform.Epic, PersistenceMode.KillOnRestart)]
    [InlineData(PrefillPlatform.Epic, PersistenceMode.KeepAcrossRestart)]
    [InlineData(PrefillPlatform.Epic, PersistenceMode.FullPersistence)]
    [InlineData(PrefillPlatform.Xbox, PersistenceMode.KillOnRestart)]
    [InlineData(PrefillPlatform.Xbox, PersistenceMode.KeepAcrossRestart)]
    [InlineData(PrefillPlatform.Xbox, PersistenceMode.FullPersistence)]
    public async Task ARestartAfterALogOffSignsNothingInAsync(PrefillPlatform platform, PersistenceMode mode)
    {
        var c = await AdoptSignedInAsync(platform, mode, DateTime.UtcNow, []);

        await LogOffAsync(c);

        Assert.Equal(DaemonAuthState.NotAuthenticated, c.Session.AuthState);
        Assert.True((await GetRowAsync(c.Options, c.Session.Id))!.SignedOut);
        c.Daemon.Dispose();

        // The restart finds the same container, which still holds the saved login and reports no drop code.
        var container = RunningPersistentContainer(c.Session.Id, PersistentContainerName(platform), platform);
        var (restarted, restartClient, restartGateway) = await RestartAsync(c, container);

        Assert.Equal(c.Session.Id, SessionsOf(restarted).Values.Single(s => s.IsPersistent).Id);
        Assert.Empty(restartGateway.CreatedParameters);
        Assert.Equal(0, restartClient.StartLoginCallCount);
        restarted.Dispose();

        if (mode != PersistenceMode.FullPersistence)
        {
            return;
        }

        // The container is gone at the next start, so FullPersistence recreates it on the same saved login. Only an
        // enabled service is recreated.
        var (recreated, recreateClient, recreateGateway) = await RestartAsync(c, null);

        Assert.Single(recreateGateway.CreatedParameters);
        Assert.Equal(0, recreateClient.StartLoginCallCount);
        recreated.Dispose();
    }

    // A scheduled run that comes due while the startup sign-in still waits on Steam waits for that sign-in
    // and then sends its prefill signed in, with and without concurrent runs. KillOnRestart starts no sign-in.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart, false)]
    [InlineData(PersistenceMode.KeepAcrossRestart, true)]
    [InlineData(PersistenceMode.FullPersistence, true)]
    public async Task AScheduledRunDuringTheStartupSignInWaitsForItAsync(PersistenceMode mode, bool concurrentRuns)
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, DateTime.UtcNow.AddDays(30));
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(RunningPersistentContainer(sessionId));
        var instance = Guid.NewGuid().ToString();
        var signedIn = false;
        var client = new ScriptedLoginDaemonClient { HoldStartLogin = true };
        client.StatusHandler = _ =>
        {
            var status = concurrentRuns ? IdleStatus(instance) : new DaemonStatus { Type = "status", Timestamp = DateTime.UtcNow };
            status.Status = signedIn ? "logged-in" : "awaiting-login";
            return Task.FromResult<DaemonStatus?>(status);
        };
        var deps = MakeDeps(database, sessionService, Config(mode, steamEnabled: true));
        using var daemon = new TestSteamDaemon(deps, gateway, () => client);
        var prefillSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prefillCount = 0;
        DaemonAuthState? authAtPrefill = null;
        // The test reads the state the prefill command was sent in; the daemon then refuses it.
        client.PrefillHandler = _ =>
        {
            prefillCount++;
            authAtPrefill = daemon.GetActivePersistentSession()?.AuthState;
            prefillSent.TrySetResult();
            return Task.FromResult(new PrefillResult { Success = false, ErrorCode = "refused" });
        };
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TestTimeout);
        if (mode != PersistenceMode.KillOnRestart)
        {
            await client.StartLoginEntered.Task.WaitAsync(TestTimeout);
        }

        var serviceConfig = new ScheduledPrefillServiceConfigDto
        {
            ServiceId = PrefillPlatform.Steam,
            ScheduleId = ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam),
            ScheduleName = "Default",
            Enabled = true,
            NotificationMode = NotificationMode.Silent,
            IntervalHours = 24,
            Preset = ScheduledPrefillPreset.All,
            SelectedAppIds = new List<string>(),
            OperatingSystems = new List<ScheduledPrefillOperatingSystem> { ScheduledPrefillOperatingSystem.Windows },
            MaxConcurrency = new ScheduledPrefillMaxConcurrencyDto { Mode = ScheduledPrefillMaxConcurrencyMode.Auto }
        };
        var serviceRun = new ScheduledPrefillServiceRun(
            serviceConfig, Guid.Empty, "op-1", "run-1",
            new ScheduledPrefillServiceRunState(
                serviceConfig.ServiceId, serviceConfig.ScheduleId, serviceConfig.ScheduleName,
                new RunNotice(NotificationMode.Silent, RunTrigger.Scheduled)),
            CancellationToken.None);
        using var schedulerServices = new ServiceCollection().BuildServiceProvider();
        using var scheduler = new ScheduledPrefillService(NullLogger<ScheduledPrefillService>.Instance,
            schedulerServices.GetRequiredService<IServiceScopeFactory>(), deps.StateService);
        var runService = typeof(ScheduledPrefillService).GetMethod("RunServiceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var run = (Task<ScheduledPrefillServiceRunResult>)runService.Invoke(scheduler,
            [serviceRun, new SingleDaemonServiceProvider(daemon), FakeInterface<ISignalRNotificationService>(), Config(mode, steamEnabled: true), null])!;

        if (mode == PersistenceMode.KillOnRestart)
        {
            await run.WaitAsync(TestTimeout);
            Assert.Equal(0, client.StartLoginCallCount);
            return;
        }

        Assert.NotSame(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(1))));
        signedIn = true;
        client.ReleaseStartLogin.TrySetResult();
        await prefillSent.Task.WaitAsync(TestTimeout);

        Assert.Equal(1, client.StartLoginCallCount);
        Assert.Equal(1, prefillCount);
        Assert.Equal(DaemonAuthState.Authenticated, authAtPrefill);
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

        await DaemonTestMethods.InvokePrivateHandlerAsync(daemon, "ApplyStatusAsync", session, SteamStatus(Guid.NewGuid().ToString(), signedIn: false), null);

        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);
        Assert.Null(session.SignInAgainAfterDrop);
    }

    [Fact]
    public async Task AnAuthStatePushCarriesItsErrorCode()
    {
        using var client = new SocketDaemonClient("/nonexistent.sock", "secret", null)
        { SignInLostStageKey = "errors.steam.signInLost" };
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
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("got no response from the daemon"));
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
    public async Task ADropWhileATakenOverRetryEndsItsLoginWaitsForThatRetryAsync()
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
        using var abort = new CancellationTokenSource();
        var personsLogin = s.Daemon.StartLoginAsync(s.Session.Id, cancellationToken: abort.Token);
        await client.CancelLoginEntered.Task.WaitAsync(TestTimeout);
        await abort.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => personsLogin);
        var logins = client.StartLoginCallCount;

        // A second drop arrives while the retry is still ending the login it sent. It starts no retry beside it.
        client.HoldStartLogin = false;
        await s.RaiseDropAsync();
        Assert.Same(first, s.Session.SignInAgainAfterDrop);

        client.ReleaseCancelLogin.SetResult();
        await first!.WaitAsync(TestTimeout);
        Assert.Equal(logins, client.StartLoginCallCount);
    }

    // The minute sweep reads the daemon's status while the automatic sign-in waits on its login; that read does
    // not end the sign-in's state before the sign-in itself does.
    [Fact]
    public async Task ASweepDuringTheAutomaticSignInLeavesItsStateAloneAsync()
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
        var during = s.Session.AuthState;

        await s.Daemon.RefreshRunsAsync(s.Session.Id);

        Assert.Equal(during, s.Session.AuthState);
        await s.Session.CancellationTokenSource.CancelAsync();
        client.ReleaseStartLogin.SetResult();
        await retry!.WaitAsync(TestTimeout);
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
        // A cancel that ends the sign-in leaves the daemon signed out, so its reads say so from then on.
        client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(new DaemonStatus
        {
            Type = "status",
            Status = client.CancelLoginCallCount == 0 ? "logged-in" : "awaiting-login",
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
        await sessionService.SetLoginStateAsync(sessionId, true, false, false, DateTime.UtcNow, DateTime.UtcNow.AddDays(30));
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
        await sessionService.SetLoginStateAsync(sessionId, true, false, false, DateTime.UtcNow, DateTime.UtcNow.AddDays(30));
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(RunningPersistentContainer(sessionId));
        s.Daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true), cacheStatusClock: s.Clock),
            gateway,
            () => client);

        await s.Daemon.StartAsync(CancellationToken.None);
        s.Session = SessionsOf(s.Daemon).Values.Single(session => session.IsPersistent);

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
        await sessionService.SetLoginStateAsync(sessionId, true, true, false, DateTime.UtcNow, DateTime.UtcNow.AddDays(30));
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
    // accepted Log in or a real connection drop after the daemon reached signed-in clears the mark.
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

        // The cancel's write of its mark is slow. A logged-in report while that cancel is still running
        // is a sign-in the cancel cannot undo: it arms the later-drop witness, and the mark stays on the row.
        held.Armed = true;
        var cancel = s.Daemon.CancelLoginAsync(s.Session.Id);
        Task push;
        try
        {
            await held.Entered.Task.WaitAsync(TestTimeout);
            push = s.Client.RaiseStatusAsync(SteamStatus(s.Instance, signedIn: true));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!s.Session.LoggedInWhilePersonCanceled && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.True(s.Session.LoggedInWhilePersonCanceled);
            Assert.True(s.Session.LoginCanceledByPerson);
            Assert.Equal(DaemonAuthState.NotAuthenticated, s.Session.AuthState);
            Assert.Null(s.Session.SignInAgainAfterDrop);
        }
        finally
        {
            held.Release.TrySetResult();
        }

        await push.WaitAsync(TestTimeout);
        await cancel.WaitAsync(TestTimeout);

        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.True(s.Session.LoggedInWhilePersonCanceled);
        Assert.True(await s.StoredCancelAsync());
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
        var login = s.Daemon.StartPersistentLoginAsync(s.Session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId);
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
            () => s.Daemon.StartPersistentLoginAsync(s.Session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId));

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
        var login = s.Daemon.ReusePersistentIntegrationLoginAsync(
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
        var login = s.Daemon.StartPersistentLoginAsync(s.Session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId);
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
        var login = s.Daemon.StartPersistentLoginAsync(s.Session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId);
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
                State = "running"
            },
            TerminalCompletedFlag = 0
        };
        s.Session.Recovering = true;

        var loginId = Guid.NewGuid();
        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => s.Daemon.StartPersistentLoginAsync(s.Session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId));
        Assert.Equal("errors.prefill.prefillRunning", refusal.StageKey);
        Assert.False(await s.Daemon.CancelLoginAsync(s.Session.Id, loginId: loginId));

        Assert.False(s.Session.LoginCanceledByPerson);
        Assert.False(await s.StoredCancelAsync());
        s.Session.Runs.Clear();
        s.Session.Recovering = false;
        s.Clock.Advance(SignInAgainFirstWait);
        await WaitForStartsAsync(client, s.StartsBeforeDrop + 1);
    }

    // Runs stay in Runs for a day after they finish, so a recovering container whose runs all finished must
    // still take a Log in.
    [Fact]
    public async Task ALogInOnARecoveringContainerWhoseRunsAllFinishedIsNotRefusedAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        client.NeverAuthenticates = true;
        UseOperationTracker(s.Daemon);
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
        var startsBefore = client.StartLoginCallCount;

        var error = await Record.ExceptionAsync(() => s.Daemon.StartPersistentLoginAsync(
            s.Session.Id, TimeSpan.FromSeconds(30), () => { }, Guid.NewGuid(), loginId: Guid.NewGuid()).WaitAsync(TestTimeout));

        Assert.Null(error);
        Assert.Equal(startsBefore + 1, client.StartLoginCallCount);
        Assert.NotNull(s.Session.LoginOperationId);
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
            () => s.Daemon.StartPersistentLoginAsync(s.Session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId));
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
                () => s.Daemon.StartPersistentLoginAsync(s.Session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId));
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
        var failing = new FailingRowSave(entry => entry.Property(row => row.LoginCanceledByPerson).IsModified || entry.Property(row => row.LoggedInWhilePersonCanceled).IsModified) { FailuresLeft = 1 };
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
        var failing = new FailingRowSave(entry => entry.Property(row => row.LoginCanceledByPerson).IsModified || entry.Property(row => row.LoggedInWhilePersonCanceled).IsModified);
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client, interceptors: [failing]);
        var (personsLogin, _) = await StartAHeldPersonsSignInAsync(s, client);
        client.CancelErrorCode = "auth-lost";
        failing.Armed = true;

        Assert.True(await s.Daemon.CancelLoginAsync(s.Session.Id));
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
        var failing = new FailingRowSave(entry => entry.Property(row => row.LoginCanceledByPerson).IsModified || entry.Property(row => row.LoggedInWhilePersonCanceled).IsModified);
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
        Assert.Null(await Record.ExceptionAsync(() => persons.WaitAsync(TestTimeout)));
        client.ReleaseStartLogin.SetResult();
        await Record.ExceptionAsync(() => personsLogin);

        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.False(await s.StoredCancelAsync());
    }

    [Fact]
    public async Task AFailedCancelReportsItsOwnFailureWhenTheMarkCannotBeStoredAsync()
    {
        var failing = new FailingRowSave(entry => entry.Property(row => row.LoginCanceledByPerson).IsModified || entry.Property(row => row.LoggedInWhilePersonCanceled).IsModified);
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
        var failing = new FailingRowSave(entry => entry.Property(row => row.LoginCanceledByPerson).IsModified || entry.Property(row => row.LoggedInWhilePersonCanceled).IsModified);
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
            var login = s.Daemon.StartPersistentLoginAsync(s.Session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId);
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
    public async Task ADropWhoseMarkClearCannotBeStoredStillStartsTheRetryAsync()
    {
        var failing = new FailingRowSave(entry => entry.Property(row => row.LoginCanceledByPerson).IsModified || entry.Property(row => row.LoggedInWhilePersonCanceled).IsModified);
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client, interceptors: [failing]);
        await CancelAPersonsSignInAsync(s, client);
        await s.Client.RaiseStatusAsync(SteamStatus(s.Instance, signedIn: true));
        Assert.True(await s.StoredWitnessAsync());
        failing.Armed = true;

        await s.RaiseDropAsync();

        Assert.False(s.Session.LoginCanceledByPerson);
        Assert.NotNull(s.Session.SignInAgainAfterDrop);
        Assert.True(await s.StoredCancelAsync());

        // The sign-in after the drop changes neither flag, and still stores the cleared pair.
        failing.Armed = false;
        await s.Client.RaiseStatusAsync(SteamStatus(s.Instance, signedIn: true));
        Assert.Equal(DaemonAuthState.Authenticated, s.Session.AuthState);
        Assert.False(await s.StoredCancelAsync());
        Assert.False(await s.StoredWitnessAsync());
    }

    [Fact]
    public async Task ALogInWhoseMarkClearCannotBeStoredIsStoredByItsSignInAsync()
    {
        var failing = new FailingRowSave(entry => entry.Property(row => row.LoginCanceledByPerson).IsModified || entry.Property(row => row.LoggedInWhilePersonCanceled).IsModified);
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client, interceptors: [failing]);
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        await s.Daemon.CancelLoginAsync(s.Session.Id);
        Assert.True(await s.StoredCancelAsync());
        failing.Armed = true;

        client.HoldStartLogin = true;
        var personsLogin = s.Daemon.StartLoginAsync(s.Session.Id);
        await WaitForStartsAsync(client, s.StartsBeforeDrop + 1);
        Assert.False(s.Session.LoginCanceledByPerson);
        Assert.True(await s.StoredCancelAsync());

        failing.Armed = false;
        await s.Client.RaiseStatusAsync(SteamStatus(s.Instance, signedIn: true));
        client.ReleaseStartLogin.SetResult();
        await Record.ExceptionAsync(() => personsLogin);

        Assert.Equal(DaemonAuthState.Authenticated, s.Session.AuthState);
        Assert.False(await s.StoredCancelAsync());
    }

    [Fact]
    public async Task AWitnessThatCannotBeStoredStillLetsTheNextDropSignInAsync()
    {
        var failing = new FailingRowSave(entry => entry.Property(row => row.LoginCanceledByPerson).IsModified || entry.Property(row => row.LoggedInWhilePersonCanceled).IsModified);
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client, interceptors: [failing]);
        await CancelAPersonsSignInAsync(s, client);
        failing.Armed = true;

        await s.Client.RaiseStatusAsync(SteamStatus(s.Instance, signedIn: true));

        Assert.True(s.Session.LoggedInWhilePersonCanceled);
        Assert.False(await s.StoredWitnessAsync());

        failing.Armed = false;
        await s.RaiseDropAsync();

        Assert.NotNull(s.Session.SignInAgainAfterDrop);
        Assert.False(s.Session.LoginCanceledByPerson);
        Assert.False(await s.StoredCancelAsync());
    }

    [Fact]
    public async Task ASignInTheDaemonFinishesDuringAPersonsCancelLetsTheNextDropSignInAsync()
    {
        var held = new HeldSave(entry => entry.Property(row => row.LoginCanceledByPerson).IsModified);
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client, interceptors: [held]);
        var (personsLogin, _) = await StartAHeldPersonsSignInAsync(s, client);

        // Steam finishes the sign-in after the cancel stored nothing yet and before cancel-login reaches the daemon.
        client.LoginFinished = true;
        client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(LoggedInRead(s.Instance));
        held.Armed = true;
        var cancel = s.Daemon.CancelLoginAsync(s.Session.Id);
        await held.Entered.Task.WaitAsync(TestTimeout);
        var push = s.Client.RaiseStatusAsync(CommitPush(s.Instance));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!s.Session.LoggedInWhilePersonCanceled && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        held.Release.TrySetResult();
        await push.WaitAsync(TestTimeout);
        await cancel.WaitAsync(TestTimeout);
        client.ReleaseStartLogin.TrySetResult();
        await Record.ExceptionAsync(() => personsLogin);

        Assert.Equal(DaemonAuthState.NotAuthenticated, s.Session.AuthState);
        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.True(s.Session.LoggedInWhilePersonCanceled);
        Assert.True(await s.StoredWitnessAsync());

        await s.RaiseDropAsync();

        Assert.NotNull(s.Session.SignInAgainAfterDrop);
        Assert.False(s.Session.LoginCanceledByPerson);
    }

    [Fact]
    public async Task ALogInAndCancelOverAKeptLoggedInDaemonLetsTheNextDropSignInAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        await s.Daemon.CancelLoginAsync(s.Session.Id);
        await s.Client.RaiseStatusAsync(CommitPush(s.Instance));
        Assert.True(s.Session.LoggedInWhilePersonCanceled);

        // The card reads signed out. The daemon still reads logged-in after the Log in's logout, as an older
        // image does, so the Log in waits on it and the person presses Cancel while it waits.
        client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(LoggedInRead(s.Instance));
        client.LoginFinished = true;
        client.HoldStartLogin = true;
        var starts = client.StartLoginCallCount;
        var personsLogin = s.Daemon.StartLoginAsync(s.Session.Id);
        await WaitForStartsAsync(client, starts + 1);
        await s.Daemon.CancelLoginAsync(s.Session.Id).WaitAsync(TestTimeout);
        client.ReleaseStartLogin.TrySetResult();
        await Record.ExceptionAsync(() => personsLogin);

        Assert.Equal(1, client.LogoutCount);
        Assert.Equal(DaemonAuthState.NotAuthenticated, s.Session.AuthState);
        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.True(s.Session.LoggedInWhilePersonCanceled);
        Assert.True(await s.StoredWitnessAsync());

        await s.RaiseDropAsync();

        Assert.NotNull(s.Session.SignInAgainAfterDrop);
        Assert.False(s.Session.LoginCanceledByPerson);
    }

    [Fact]
    public async Task UseMySavedLoginOverAKeptLoggedInDaemonLogsTheHeldAccountOutFirstAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        await s.Daemon.CancelLoginAsync(s.Session.Id);
        await s.Client.RaiseStatusAsync(CommitPush(s.Instance));
        Assert.Equal(DaemonAuthState.NotAuthenticated, s.Session.AuthState);
        client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(
            client.LogoutCount == 0 ? LoggedInRead(s.Instance) : LogOffStatus(s.Instance));
        s.Daemon.HeldSavedLogin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var login = s.Daemon.ReusePersistentIntegrationLoginAsync(
            s.Session.Id, null, () => { }, Guid.NewGuid(),
            lease: new IntegrationLease(s.Daemon, 0, null, () => { }, () => { }), loginId: Guid.NewGuid());
        await s.Daemon.SavedLoginEntered.Task.WaitAsync(TestTimeout);

        Assert.Equal(1, client.LogoutCount);
        Assert.NotEqual(DaemonAuthState.Authenticated, s.Session.AuthState);
        s.Daemon.HeldSavedLogin.SetResult();
        var ended = await Record.ExceptionAsync(() => login);
        Assert.False(ended is ConflictException { StageKey: "errors.prefill.logoutBeforeSavedLogin" });
    }

    [Fact]
    public async Task APersonsCancelDuringStartupHeadlessKeepsTheContainerSignedOutAsync()
    {
        var client = new ScriptedLoginDaemonClient { HoldStartLogin = true };
        var (_, daemon, _, _) = SetupRecreateScenario(DateTime.UtcNow.AddDays(30), clientFactory: () => client);
        try
        {
            await daemon.StartAsync(CancellationToken.None).WaitAsync(TestTimeout);
            await client.StartLoginEntered.Task.WaitAsync(TestTimeout);
            var session = SessionsOf(daemon).Values.Single(candidate => candidate.IsPersistent);
            var loginId = Guid.NewGuid();
            var persons = daemon.StartPersistentLoginAsync(session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId);
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
            var persons = daemon.StartPersistentLoginAsync(session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId);
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
            var persons = daemon.StartPersistentLoginAsync(session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId);
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
            var persons = daemon.StartPersistentLoginAsync(session.Id, null, () => { }, Guid.NewGuid(), loginId: loginId);
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
            var login = s.Daemon.StartPersistentLoginAsync(
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

    [Fact]
    public async Task ACancelMarkTheDatabaseRefusedIsStoredOnceItIsBackAndKeptByTheNextManagerAsync()
    {
        var failing = new FailingRowSave(entry => entry.Property(row => row.LoginCanceledByPerson).IsModified || entry.Property(row => row.LoggedInWhilePersonCanceled).IsModified);
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client, interceptors: [failing]);
        failing.Armed = true;
        await CancelAPersonsSignInAsync(s, client);
        Assert.False(await s.StoredCancelAsync());

        failing.Armed = false;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await s.StoredCancelAsync() && DateTime.UtcNow < deadline)
        {
            s.Clock.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(10);
        }

        // A new manager process adopts the same running container over the same database.
        var restarted = new ScriptedLoginDaemonClient();
        using var r = new SignInScenario(restarted) { Database = s.Database };
        r.UseSteamAnswers();
        var contexts = new PooledDbFactory(s.Database);
        var sessions = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(RunningPersistentContainer(s.Session.Id));
        r.Daemon = new TestSteamDaemon(
            MakeDeps(contexts, sessions, Config(PersistenceMode.FullPersistence, steamEnabled: true), cacheStatusClock: r.Clock),
            gateway,
            () => restarted);
        await r.Daemon.StartAsync(CancellationToken.None);
        if (r.Daemon.LastHeadlessSelfAuthAttempt is { } attempt) await attempt.WaitAsync(TestTimeout);
        r.Session = SessionsOf(r.Daemon).Values.Single(session => session.IsPersistent);

        Assert.Equal(s.Session.Id, r.Session.Id);
        Assert.True(r.Session.LoginCanceledByPerson);
        Assert.Null(r.Session.SignInAgainAfterDrop);
        Assert.Equal(0, restarted.StartLoginCallCount);
    }

    [Fact]
    public async Task StoppingTheManagerStoresACancelMarkTheDatabaseRefusedAsync()
    {
        var failing = new FailingRowSave(entry => entry.Property(row => row.LoginCanceledByPerson).IsModified || entry.Property(row => row.LoggedInWhilePersonCanceled).IsModified);
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client, interceptors: [failing]);
        failing.Armed = true;
        await CancelAPersonsSignInAsync(s, client);
        Assert.False(await s.StoredCancelAsync());
        failing.Armed = false;

        await s.Daemon.StopAsync(CancellationToken.None);

        Assert.True(await s.StoredCancelAsync());
    }

    // A database that does not answer at shutdown must not keep the containers from being let go before
    // the host's stop time runs out.
    [Fact]
    public async Task StoppingTheManagerLetsTheContainersGoBeforeItRewritesACancelMarkAsync()
    {
        var failing = new FailingRowSave(entry => entry.Property(row => row.LoginCanceledByPerson).IsModified || entry.Property(row => row.LoggedInWhilePersonCanceled).IsModified);
        var held = new HeldSave(entry => entry.Property(row => row.LoginCanceledByPerson).IsModified);
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client, interceptors: [failing, held]);
        failing.Armed = true;
        await CancelAPersonsSignInAsync(s, client);
        Assert.False(await s.StoredCancelAsync());
        failing.Armed = false;
        var guestClient = new ScriptedLoginDaemonClient();
        InjectedTemporarySteamSession(s.Daemon, guestClient);
        held.Armed = true;

        using var hostStop = new CancellationTokenSource();
        var stopping = s.Daemon.StopAsync(hostStop.Token);
        await held.Entered.Task.WaitAsync(TestTimeout);

        Assert.True(client.Disposed);
        Assert.True(guestClient.Disposed);
        hostStop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping.WaitAsync(TestTimeout));
    }

    // A person's second Log in ends their first sign-in before it starts its own, and the daemon leaves that
    // cancel unanswered. The cancel was the Log in's, not the person's, so it leaves no mark behind.
    [Fact]
    public async Task ALogInWhoseCancelOfTheEarlierSignInGoesUnansweredLeavesNoCancelMarkAsync()
    {
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client);
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        client.StatusHandler = _ =>
        {
            var status = SteamStatus(s.Instance, s.SteamAnswers && client.StartLoginCallCount > s.AnswersAfterStarts);
            if (status.Status != "logged-in") status.ErrorCode = null;
            return Task.FromResult<DaemonStatus?>(status);
        };
        // The sign-in run the first Log in registers is what makes the second one end that sign-in first.
        UseOperationTracker(s.Daemon);
        // The daemon takes the first Log in but neither prompts nor answers, so that sign-in is still running.
        client.NeverAuthenticates = true;
        var owner = Guid.NewGuid();
        await s.Daemon.StartPersistentLoginAsync(s.Session.Id, TimeSpan.FromSeconds(30), () => { }, owner);
        Assert.NotNull(s.Session.LoginOperationId);

        client.CancelAcknowledged = false;
        var refused = await Assert.ThrowsAsync<ConflictException>(
            () => s.Daemon.StartPersistentLoginAsync(s.Session.Id, TimeSpan.FromSeconds(30), () => { }, owner));

        Assert.Equal("prefill.persistent.loginNotCanceled", refused.StageKey);
        Assert.False(s.Session.LoginCanceledByPerson);
        Assert.False(await s.StoredCancelAsync());
        Assert.False(ScheduledPrefillRunGates.PersonCanceledSignIn(s.Session));

        // The first sign-in now finishes on the daemon.
        s.SteamAnswers = true;
        s.AnswersAfterStarts = 0;
        await client.RaiseStatusAsync(SteamStatus(s.Instance, signedIn: true));

        Assert.Equal(DaemonAuthState.Authenticated, s.Session.AuthState);
    }

    [Fact]
    public async Task ReplacingAnErroredSessionKeepsACancelMarkTheDatabaseRefusedAsync()
    {
        var failing = new FailingRowSave(entry => entry.Property(row => row.LoginCanceledByPerson).IsModified || entry.Property(row => row.LoggedInWhilePersonCanceled).IsModified);
        var client = new ScriptedLoginDaemonClient();
        using var s = await StartSignedInSteamAsync(client, interceptors: [failing]);
        failing.Armed = true;
        await CancelAPersonsSignInAsync(s, client);
        Assert.False(await s.StoredCancelAsync());
        failing.Armed = false;
        var oldId = s.Session.Id;
        s.Session.Status = DaemonSessionStatus.Error;
        s.SteamAnswers = true;
        var startsBefore = client.StartLoginCallCount;

        var created = await s.Daemon.CreateSessionAsync(ScheduledPrefillConstants.DeriveSystemUserId(), isPersistent: true);
        if (s.Daemon.LastHeadlessSelfAuthAttempt is { } attempt) await attempt.WaitAsync(TestTimeout);

        Assert.NotEqual(oldId, created.Id);
        Assert.True(created.LoginCanceledByPerson);
        Assert.NotEqual(DaemonAuthState.Authenticated, created.AuthState);
        Assert.Equal(startsBefore, client.StartLoginCallCount);
        Assert.True((await GetRowAsync(s.Database, created.Id))!.LoginCanceledByPerson);
    }

    [Fact]
    public async Task UseMySavedLoginWhoseHeldAccountCannotBeLoggedOutKeepsTheCancelAsync()
    {
        var inner = new ScriptedLoginDaemonClient();
        var proxy = DispatchProxy.Create<IDaemonClient, TimedOutLogoutClient>();
        ((TimedOutLogoutClient)(object)proxy).Inner = inner;
        using var s = new SignInScenario(inner);
        var (database, daemon, _, _) = SetupRecreateScenario(
            DateTime.UtcNow.AddDays(30), clientFactory: () => proxy, cacheStatusClock: s.Clock);
        s.Daemon = daemon;
        s.Database = database;
        await daemon.StartAsync(CancellationToken.None);
        await daemon.LastHeadlessSelfAuthAttempt!;
        s.Session = SessionsOf(daemon).Values.Single(session => session.IsPersistent);
        s.StartsBeforeDrop = inner.StartLoginCallCount;
        s.AnswersAfterStarts = inner.StartLoginCallCount;
        s.UseSteamAnswers();
        await s.Client.RaiseStatusAsync(LogOffStatus(s.Instance));
        await s.Daemon.CancelLoginAsync(s.Session.Id);
        await s.Client.RaiseStatusAsync(CommitPush(s.Instance));
        inner.StatusHandler = _ => Task.FromResult<DaemonStatus?>(LoggedInRead(s.Instance));
        s.Daemon.HeldSavedLogin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var refused = await Record.ExceptionAsync(() => s.Daemon.ReusePersistentIntegrationLoginAsync(
            s.Session.Id, null, () => { }, Guid.NewGuid(),
            lease: new IntegrationLease(s.Daemon, 0, null, () => { }, () => { }), loginId: Guid.NewGuid()).WaitAsync(TestTimeout));
        s.Daemon.HeldSavedLogin.TrySetResult();

        Assert.Equal("errors.prefill.heldAccountNotLoggedOut", Assert.IsType<ConflictException>(refused).StageKey);
        Assert.True(s.Session.LoginCanceledByPerson);
        Assert.True(s.Session.LoggedInWhilePersonCanceled);
        Assert.NotEqual(DaemonAuthState.Authenticated, s.Session.AuthState);
    }

    // ============================ The login row: log-off mark, sign-in time and their writes ============================

    // A log-off whose mark write fails once is still announced, and the retried write keeps the next manager from
    // signing the account back in.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task ALogOffWhoseMarkWriteFailsOnceIsAnnouncedAndStoredAsync(PersistenceMode mode)
    {
        var failing = new FailingRowSave(entry => entry.Property(row => row.SignedOut).IsModified);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
        var recorder = (EventRecordingNotificationsProxy)(object)notifications;
        var c = await AdoptSignedInAsync(PrefillPlatform.Steam, mode, DateTime.UtcNow, [failing], notifications);
        var sentBefore = recorder.EventNames.Count;
        failing.FailuresLeft = 1;

        await LogOffAsync(c);

        Assert.Contains(SignalREvents.AuthStateChanged, recorder.EventNames.Skip(sentBefore));
        Assert.True((await GetRowAsync(c.Options, c.Session.Id))!.SignedOut);
        var container = c.Gateway.FindByName(SteamPersistentContainerName)!;
        c.Daemon.Dispose();
        var (restarted, restartClient, _) = await RestartAsync(c, container);
        Assert.Equal(0, restartClient.StartLoginCallCount);
        restarted.Dispose();
    }

    // A log-off mark the database refuses twice is announced at once and written again until a write lands.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task ALogOffMarkTheDatabaseRefusesTwiceIsWrittenAgainAsync(PersistenceMode mode)
    {
        var failing = new FailingRowSave(entry => entry.Property(row => row.SignedOut).IsModified);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
        var recorder = (EventRecordingNotificationsProxy)(object)notifications;
        var clock = new CacheStatusClock(DateTimeOffset.UtcNow);
        var c = await AdoptSignedInAsync(PrefillPlatform.Steam, mode, DateTime.UtcNow, [failing], notifications, clock);
        var sentBefore = recorder.EventNames.Count;
        failing.FailuresLeft = 2;

        await LogOffAsync(c);

        Assert.Contains(SignalREvents.AuthStateChanged, recorder.EventNames.Skip(sentBefore));
        Assert.False((await GetRowAsync(c.Options, c.Session.Id))!.SignedOut);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!(await GetRowAsync(c.Options, c.Session.Id))!.SignedOut && DateTime.UtcNow < deadline)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(10);
        }
        Assert.True((await GetRowAsync(c.Options, c.Session.Id))!.SignedOut);
        c.Daemon.Dispose();
    }

    // A person's Log in whose clear of the log-off mark fails once still signs in, and the retried write lets the next
    // manager sign the saved login in again.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task ALogInWhoseLogOffClearFailsOnceStillSignsInAsync(PersistenceMode mode)
    {
        var failing = new FailingRowSave(entry => entry.Property(row => row.SignedOut).IsModified);
        var c = await AdoptSignedInAsync(PrefillPlatform.Steam, mode, DateTime.UtcNow, [failing]);
        UseOperationTracker(c.Daemon);
        await LogOffAsync(c);
        Assert.True((await GetRowAsync(c.Options, c.Session.Id))!.SignedOut);
        var starts = c.Client.StartLoginCallCount;
        c.Client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(
            c.Client.StartLoginCallCount > starts ? SteamStatus(c.Instance, signedIn: true) : LogOffStatus(c.Instance));
        failing.FailuresLeft = 1;

        var error = await Record.ExceptionAsync(() => c.Daemon.StartPersistentLoginAsync(
            c.Session.Id, TimeSpan.FromSeconds(30), () => { }, Guid.NewGuid(), loginId: Guid.NewGuid()).WaitAsync(TestTimeout));
        await c.Daemon.GetSessionStatusAsync(c.Session.Id);

        Assert.Null(error);
        Assert.Equal(DaemonAuthState.Authenticated, c.Session.AuthState);
        Assert.False((await GetRowAsync(c.Options, c.Session.Id))!.SignedOut);
        var container = c.Gateway.FindByName(SteamPersistentContainerName)!;
        c.Daemon.Dispose();
        var (restarted, restartClient, _) = await RestartAsync(c, container);
        Assert.Equal(mode == PersistenceMode.KillOnRestart ? 0 : 1, restartClient.StartLoginCallCount);
        restarted.Dispose();
    }

    // A person's Start over an Orphaned FullPersistence row with the log-off mark answers with the running session
    // although the mark's first write on the new row fails, and the mark lands on that row.
    [Fact]
    public async Task AStartOverAnOrphanedRowWhoseMarkWriteFailsOnceStillAnswersAsync()
    {
        var failing = new FailingRowSave(entry => entry.Property(row => row.SignedOut).IsModified);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
        var recorder = (EventRecordingNotificationsProxy)(object)notifications;
        var (options, database) = NewDatabase(failing);
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var priorId = await SeedActivePersistentRowAsync(sessions, DateTime.UtcNow.AddDays(30));
        await using (var context = new AppDbContext(options))
        {
            var prior = await context.PrefillSessions.SingleAsync(row => row.SessionId == priorId);
            prior.Status = PrefillSessionStatus.Orphaned;
            prior.SignedOut = true;
            await context.SaveChangesAsync();
        }
        var client = new ScriptedLoginDaemonClient();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, steamEnabled: true), notifications: notifications),
            new RecordingContainerGateway(),
            () => client);
        failing.FailuresLeft = 1;

        var session = await daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId());
        if (daemon.LastHeadlessSelfAuthAttempt is { } attempt) await attempt.WaitAsync(TestTimeout);

        Assert.True(session.SignedOut);
        Assert.True((await GetRowAsync(options, session.Id))!.SignedOut);
        Assert.Contains(SignalREvents.DaemonSessionCreated, recorder.EventNames);
        Assert.Equal(0, client.StartLoginCallCount);
    }

    // A person's Log in that clears the log-off mark while the log-off's own write of it is still running leaves the
    // row cleared: the later write stores the later value.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task ALogInDuringTheLogOffMarkWriteLeavesTheRowClearedAsync(PersistenceMode mode)
    {
        var held = new HeldSave(entry => entry.Property(row => row.SignedOut).IsModified && entry.Entity.SignedOut);
        var c = await AdoptSignedInAsync(PrefillPlatform.Steam, mode, DateTime.UtcNow, [held]);
        UseOperationTracker(c.Daemon);
        var starts = c.Client.StartLoginCallCount;
        held.Armed = true;
        var logOff = c.Client.RaiseStatusAsync(LogOffStatus(c.Instance));
        await held.Entered.Task.WaitAsync(TestTimeout);
        c.Client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(
            c.Client.StartLoginCallCount > starts ? SteamStatus(c.Instance, signedIn: true) : LogOffStatus(c.Instance));

        var login = c.Daemon.StartPersistentLoginAsync(c.Session.Id, TimeSpan.FromSeconds(30), () => { }, Guid.NewGuid(), loginId: Guid.NewGuid());
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (c.Session.SignedOut && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        held.Release.SetResult();
        await Task.WhenAll(logOff, login).WaitAsync(TestTimeout);

        Assert.False(c.Session.SignedOut);
        Assert.False((await GetRowAsync(c.Options, c.Session.Id))!.SignedOut);
        c.Daemon.Dispose();
    }

    // A person's Log in that the daemon's push finishes is announced and stored although the first write of its
    // sign-in time fails, so the dialog hears the sign-in and a restart does not show "Login expired" again.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task ALogInWhoseSignInTimeWriteFailsOnceIsAnnouncedAndStoredAsync(PersistenceMode mode)
    {
        var failing = new FailingRowSave(entry => entry.Property(row => row.LastLoginAtUtc).IsModified);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
        var recorder = (EventRecordingNotificationsProxy)(object)notifications;
        var (options, database) = NewDatabase(failing);
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var signedIn = DateTime.UtcNow.AddDays(-40);
        var sessionId = Guid.NewGuid().ToString("N")[..16];
        await sessions.CreateSessionAsync(sessionId, ScheduledPrefillConstants.DeriveSystemUserId(), $"container-{sessionId}",
            SteamPersistentContainerName, signedIn.AddDays(30), signedIn, PrefillPlatform.Steam.ToString());
        var container = RunningPersistentContainer(sessionId);
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(container);
        var instance = Guid.NewGuid().ToString();
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: UsernameChallenge("challenge-1"));
        var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(mode, steamEnabled: true), validityDays: 30, notifications), gateway, () => client);
        UseOperationTracker(daemon);
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TestTimeout);
        var session = daemon.GetActivePersistentSession()!;
        await daemon.ProcessSessionExpiryAsync(DateTime.UtcNow);
        Assert.True(session.NeedsRelogin);
        Assert.NotNull(await daemon.StartPersistentLoginAsync(
            session.Id, TimeSpan.FromSeconds(30), () => { }, Guid.NewGuid(), loginId: Guid.NewGuid()).WaitAsync(TestTimeout));
        var sentBefore = recorder.EventNames.Count;
        failing.FailuresLeft = 1;
        client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(CommitPush(instance));
        var loginFinished = DateTime.UtcNow;

        await client.RaiseStatusAsync(CommitPush(instance));

        var sent = recorder.EventNames.Skip(sentBefore).ToList();
        Assert.Contains(SignalREvents.AuthStateChanged, sent);
        Assert.Contains(SignalREvents.DaemonSessionUpdated, sent);
        var row = (await GetRowAsync(options, sessionId))!;
        Assert.InRange(row.LastLoginAtUtc, loginFinished, DateTime.UtcNow);
        Assert.Equal(row.LastLoginAtUtc.AddDays(30), row.ExpiresAtUtc);
        daemon.Dispose();

        var restartGateway = new RecordingContainerGateway();
        restartGateway.AddContainer(container);
        var restarted = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(mode, steamEnabled: true), validityDays: 30),
            restartGateway,
            () => new ScriptedLoginDaemonClient(alreadyLoggedIn: true));
        await restarted.StartAsync(CancellationToken.None).WaitAsync(TestTimeout);
        if (restarted.LastHeadlessSelfAuthAttempt is { } attempt) await attempt.WaitAsync(TestTimeout);
        await restarted.ProcessSessionExpiryAsync(DateTime.UtcNow);
        Assert.False(restarted.GetActivePersistentSession()!.NeedsRelogin);
        restarted.Dispose();
    }

    // A validity save that read the rows just before a person's Log in stored its sign-in, and writes just after,
    // leaves the row on the new sign-in time plus the saved days.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task AValiditySaveThatCrossesALogInKeepsItsSignInTimeAsync(PersistenceMode mode)
    {
        var held = new HeldSave(entry => entry.Property(row => row.ExpiresAtUtc).IsModified
            && !entry.Property(row => row.LastLoginAtUtc).IsModified);
        var (options, database) = NewDatabase(held);
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var signedIn = DateTime.UtcNow.AddDays(-40);
        var sessionId = Guid.NewGuid().ToString("N")[..16];
        await sessions.CreateSessionAsync(sessionId, ScheduledPrefillConstants.DeriveSystemUserId(), $"container-{sessionId}",
            SteamPersistentContainerName, signedIn.AddDays(30), signedIn, PrefillPlatform.Steam.ToString());
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(RunningPersistentContainer(sessionId));
        var instance = Guid.NewGuid().ToString();
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: UsernameChallenge("challenge-1"));
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(mode, steamEnabled: true), validityDays: 30), gateway, () => client);
        UseOperationTracker(daemon);
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TestTimeout);
        var session = daemon.GetActivePersistentSession()!;
        Assert.NotNull(await daemon.StartPersistentLoginAsync(
            session.Id, TimeSpan.FromSeconds(30), () => { }, Guid.NewGuid(), loginId: Guid.NewGuid()).WaitAsync(TestTimeout));
        held.Armed = true;
        StateOf(daemon).SetAdminPersistentLoginValidityDays(20);

        var save = daemon.UpdatePersistentSessionExpiryAsync();
        await held.Entered.Task.WaitAsync(TestTimeout);
        client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(CommitPush(instance));
        await client.RaiseStatusAsync(CommitPush(instance));
        held.Release.SetResult();
        await save.WaitAsync(TestTimeout);

        var row = (await GetRowAsync(options, sessionId))!;
        Assert.True(session.LastLoginAt > signedIn);
        Assert.Equal(session.LastLoginAt, row.LastLoginAtUtc);
        Assert.Equal(session.LastLoginAt.AddDays(20), row.ExpiresAtUtc);
        Assert.Equal(row.ExpiresAtUtc, session.ExpiresAt);
    }

    // An image update of a container whose account was logged off keeps the log-off mark, in memory and on the new
    // container's row, so neither the next manager nor a FullPersistence recreate signs the account back in.
    [Theory]
    [InlineData(PrefillPlatform.Steam, PersistenceMode.KillOnRestart)]
    [InlineData(PrefillPlatform.Steam, PersistenceMode.KeepAcrossRestart)]
    [InlineData(PrefillPlatform.Steam, PersistenceMode.FullPersistence)]
    [InlineData(PrefillPlatform.Epic, PersistenceMode.KillOnRestart)]
    [InlineData(PrefillPlatform.Epic, PersistenceMode.KeepAcrossRestart)]
    [InlineData(PrefillPlatform.Epic, PersistenceMode.FullPersistence)]
    [InlineData(PrefillPlatform.Xbox, PersistenceMode.KillOnRestart)]
    [InlineData(PrefillPlatform.Xbox, PersistenceMode.KeepAcrossRestart)]
    [InlineData(PrefillPlatform.Xbox, PersistenceMode.FullPersistence)]
    public async Task AnImageUpdateAfterALogOffKeepsTheNewContainerSignedOutAsync(PrefillPlatform platform, PersistenceMode mode)
    {
        var c = await AdoptSignedInAsync(platform, mode, DateTime.UtcNow, []);
        await LogOffAsync(c);
        Assert.True((await GetRowAsync(c.Options, c.Session.Id))!.SignedOut);
        c.Gateway.ResolvedImageId = "sha256:image-b";

        await c.Daemon.ReconcilePersistentImageAsync(CancellationToken.None);
        if (c.Daemon.LastHeadlessSelfAuthAttempt is { } attempt) await attempt.WaitAsync(TestTimeout);

        var successor = c.Daemon.GetActivePersistentSession()!;
        Assert.NotEqual(c.Session.Id, successor.Id);
        Assert.True(successor.SignedOut);
        Assert.True((await GetRowAsync(c.Options, successor.Id))!.SignedOut);
        var container = c.Gateway.FindByName(PersistentContainerName(platform))!;
        c.Daemon.Dispose();
        var (restarted, restartClient, _) = await RestartAsync(c, container);
        Assert.Equal(successor.Id, restarted.GetActivePersistentSession()!.Id);
        Assert.Equal(0, restartClient.StartLoginCallCount);
        restarted.Dispose();
        if (mode != PersistenceMode.FullPersistence)
        {
            return;
        }

        // The container is gone at the next start, so FullPersistence recreates it from the latest row.
        var (recreated, recreateClient, recreateGateway) = await RestartAsync(c, container: null);
        Assert.Single(recreateGateway.CreatedParameters);
        Assert.Equal(0, recreateClient.StartLoginCallCount);
        recreated.Dispose();
    }

    // A person's Start that replaces an errored session after a log-off keeps the mark, in memory and on the new row,
    // so FullPersistence does not sign the account back in with the kept login.
    [Theory]
    [InlineData(PrefillPlatform.Steam, PersistenceMode.KillOnRestart)]
    [InlineData(PrefillPlatform.Steam, PersistenceMode.KeepAcrossRestart)]
    [InlineData(PrefillPlatform.Steam, PersistenceMode.FullPersistence)]
    [InlineData(PrefillPlatform.Epic, PersistenceMode.KillOnRestart)]
    [InlineData(PrefillPlatform.Epic, PersistenceMode.KeepAcrossRestart)]
    [InlineData(PrefillPlatform.Epic, PersistenceMode.FullPersistence)]
    [InlineData(PrefillPlatform.Xbox, PersistenceMode.KillOnRestart)]
    [InlineData(PrefillPlatform.Xbox, PersistenceMode.KeepAcrossRestart)]
    [InlineData(PrefillPlatform.Xbox, PersistenceMode.FullPersistence)]
    public async Task ReplacingAnErroredSessionAfterALogOffKeepsItSignedOutAsync(PrefillPlatform platform, PersistenceMode mode)
    {
        var c = await AdoptSignedInAsync(platform, mode, DateTime.UtcNow, []);
        await LogOffAsync(c);
        c.Session.Status = DaemonSessionStatus.Error;
        var starts = c.Client.StartLoginCallCount;

        var created = await c.Daemon.StartPersistentSessionAsync(platform, ScheduledPrefillConstants.DeriveSystemUserId());
        if (c.Daemon.LastHeadlessSelfAuthAttempt is { } attempt) await attempt.WaitAsync(TestTimeout);

        Assert.NotEqual(c.Session.Id, created.Id);
        Assert.True(created.SignedOut);
        Assert.True((await GetRowAsync(c.Options, created.Id))!.SignedOut);
        Assert.Equal(starts, c.Client.StartLoginCallCount);
        c.Daemon.Dispose();
    }

    // A daemon image that keeps the account file still answers a Log out as done, so the log-off mark keeps the next
    // manager from signing the account back in. A person's Log in clears the mark again.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public async Task ALogOutKeepsTheNextManagerFromSigningTheAccountBackInAsync(PersistenceMode mode)
    {
        var c = await AdoptSignedInAsync(PrefillPlatform.Steam, mode, DateTime.UtcNow, []);
        // The daemon ends the live sign-in and reads signed out, and keeps its saved login.
        c.Client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(LogOffStatus(c.Instance));

        Assert.True((await c.Daemon.LogoutPersistentSessionAsync(c.Session.Id)).LoggedOut);

        Assert.True(c.Session.SignedOut);
        Assert.True((await GetRowAsync(c.Options, c.Session.Id))!.SignedOut);
        var container = c.Gateway.FindByName(SteamPersistentContainerName)!;
        c.Daemon.Dispose();
        var (restarted, restartClient, _) = await RestartAsync(c, container);
        Assert.Equal(0, restartClient.StartLoginCallCount);

        UseOperationTracker(restarted);
        await restarted.StartPersistentLoginAsync(
            c.Session.Id, TimeSpan.FromSeconds(30), () => { }, Guid.NewGuid(), loginId: Guid.NewGuid()).WaitAsync(TestTimeout);
        Assert.False((await GetRowAsync(c.Options, c.Session.Id))!.SignedOut);
        restarted.Dispose();
        var (signedIn, signedInClient, _) = await RestartAsync(c, container);
        Assert.Equal(mode == PersistenceMode.KillOnRestart ? 0 : 1, signedInClient.StartLoginCallCount);
        signedIn.Dispose();
    }

    // A person's Start that replaces an errored persistent session keeps the sign-in time of the life it replaces and
    // dates the new session from it with the current days, so the window does not restart with no one signing in.
    // Past that date FullPersistence keeps the login and does not use it.
    [Theory]
    [InlineData(PersistenceMode.KillOnRestart, 25)]
    [InlineData(PersistenceMode.KeepAcrossRestart, 25)]
    [InlineData(PersistenceMode.FullPersistence, 25)]
    [InlineData(PersistenceMode.FullPersistence, 31)]
    public async Task ReplacingAnErroredSessionKeepsItsSignInTimeAsync(PersistenceMode mode, int daysSinceSignIn)
    {
        var signedIn = DateTime.UtcNow.AddDays(-daysSinceSignIn);
        var c = await AdoptSignedInAsync(PrefillPlatform.Steam, mode, signedIn, []);
        // The new container's daemon reads its saved login only when it receives a login.
        var starts = c.Client.StartLoginCallCount;
        c.Client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(
            c.Client.StartLoginCallCount > starts ? SteamStatus(c.Instance, signedIn: true) : LogOffStatus(c.Instance));
        c.Session.Status = DaemonSessionStatus.Error;

        var created = await c.Daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId());
        if (c.Daemon.LastHeadlessSelfAuthAttempt is { } attempt) await attempt.WaitAsync(TestTimeout);
        await c.Daemon.ProcessSessionExpiryAsync(DateTime.UtcNow);

        var row = (await GetRowAsync(c.Options, created.Id))!;
        Assert.Equal(signedIn, created.LastLoginAt);
        Assert.Equal(signedIn.AddDays(30), created.ExpiresAt);
        Assert.Equal(signedIn, row.LastLoginAtUtc);
        Assert.Equal(signedIn.AddDays(30), row.ExpiresAtUtc);
        var pastTheDate = daysSinceSignIn > 30;
        Assert.Equal(pastTheDate, created.NeedsRelogin);
        Assert.Equal(mode == PersistenceMode.FullPersistence && !pastTheDate ? 1 : 0, c.Client.StartLoginCallCount - starts);
        c.Daemon.Dispose();
    }

    // A person's Start under FullPersistence that finds the last container's row Orphaned keeps that sign-in time and its
    // date, and signs in with the kept login while the date is ahead.
    [Fact]
    public async Task AStartOverAnOrphanedRowKeepsItsSignInTimeAsync()
    {
        var (options, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var signedIn = DateTime.UtcNow.AddDays(-25);
        var priorId = Guid.NewGuid().ToString("N")[..16];
        await sessions.CreateSessionAsync(priorId, ScheduledPrefillConstants.DeriveSystemUserId(), $"container-{priorId}",
            SteamPersistentContainerName, signedIn.AddDays(30), signedIn, PrefillPlatform.Steam.ToString());
        await sessions.MarkOrphansAsync(PrefillPlatform.Steam);
        var client = new ScriptedLoginDaemonClient();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, steamEnabled: true), validityDays: 30),
            new RecordingContainerGateway(),
            () => client);

        var created = await daemon.StartPersistentSessionAsync(PrefillPlatform.Steam, ScheduledPrefillConstants.DeriveSystemUserId());
        if (daemon.LastHeadlessSelfAuthAttempt is { } attempt) await attempt.WaitAsync(TestTimeout);

        var row = (await GetRowAsync(options, created.Id))!;
        Assert.Equal(signedIn, created.LastLoginAt);
        Assert.Equal(signedIn.AddDays(30), created.ExpiresAt);
        Assert.Equal(signedIn, row.LastLoginAtUtc);
        Assert.Equal(signedIn.AddDays(30), row.ExpiresAtUtc);
        Assert.Equal(1, client.StartLoginCallCount);
        Assert.Equal(DaemonAuthState.Authenticated, created.AuthState);
    }

    // ---------- Helpers ----------

    // A persistent container a manager in Mode adopted signed in, with the database and the daemon stand-in it uses.
    private sealed class AdoptedContainer
    {
        public required PrefillPlatform Platform { get; init; }
        public required PersistenceMode Mode { get; init; }
        public required PrefillDaemonServiceBase Daemon { get; init; }
        public required DaemonSession Session { get; init; }
        public required ScriptedLoginDaemonClient Client { get; init; }
        public required RecordingContainerGateway Gateway { get; init; }
        public required DbContextOptions<AppDbContext> Options { get; init; }
        public required IDbContextFactory<AppDbContext> Database { get; init; }
        public required PrefillSessionService Sessions { get; init; }
        public required string Instance { get; init; }
    }

    private static string PersistentContainerName(PrefillPlatform platform) => $"{platform.ToString().ToLowerInvariant()}-daemon-persistent";

    // The platform's service is enabled, so a manager start recreates a vanished FullPersistence container.
    private static ScheduledPrefillConfigDto EnabledConfig(PrefillPlatform platform, PersistenceMode mode)
    {
        var config = Config(mode, steamEnabled: true);
        var schedules = config.GetServicesInRunOrder().Single(service => service.ServiceId == platform).Schedules;
        schedules[0] = new ScheduledPrefillSchedule { Id = schedules[0].Id, Name = schedules[0].Name, Enabled = true };
        return config;
    }

    // A manager in mode starts over a database holding one running persistent container of platform, last signed in at
    // lastLoginAt with 30 days of validity, whose daemon reports signed in with its saved login.
    private async Task<AdoptedContainer> AdoptSignedInAsync(
        PrefillPlatform platform, PersistenceMode mode, DateTime lastLoginAt, IInterceptor[] interceptors,
        ISignalRNotificationService? notifications = null, TimeProvider? clock = null)
    {
        var (options, database) = NewDatabase(interceptors);
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var sessionId = Guid.NewGuid().ToString("N")[..16];
        var containerName = PersistentContainerName(platform);
        await sessions.CreateSessionAsync(sessionId, ScheduledPrefillConstants.DeriveSystemUserId(), $"container-{sessionId}",
            containerName, lastLoginAt.AddDays(30), lastLoginAt, platform.ToString());
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(RunningPersistentContainer(sessionId, containerName, platform));
        var instance = Guid.NewGuid().ToString();
        var client = new ScriptedLoginDaemonClient();
        client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(SteamStatus(instance, signedIn: true));
        var daemon = BuildDaemon(
            platform,
            MakeDeps(database, sessions, EnabledConfig(platform, mode), validityDays: 30, notifications, cacheStatusClock: clock),
            gateway,
            client);
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TestTimeout);
        if (daemon.LastHeadlessSelfAuthAttempt is { } attempt) await attempt.WaitAsync(TestTimeout);
        var session = SessionsOf(daemon).Values.Single(candidate => candidate.IsPersistent);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
        return new AdoptedContainer
        {
            Platform = platform,
            Mode = mode,
            Daemon = daemon,
            Session = session,
            Client = client,
            Gateway = gateway,
            Options = options,
            Database = database,
            Sessions = sessions,
            Instance = instance
        };
    }

    // The account is logged off: Steam pushes it, and Epic and Xbox report it through the status read, as their daemons do.
    private static async Task LogOffAsync(AdoptedContainer c)
    {
        var loggedOff = LogOffStatus(c.Instance, c.Platform);
        c.Client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(loggedOff);
        if (c.Platform == PrefillPlatform.Steam)
        {
            await c.Client.RaiseStatusAsync(loggedOff);
        }
        else
        {
            await c.Daemon.GetSessionStatusAsync(c.Session.Id);
        }
    }

    // A new manager over the same database finds the container, or none, whose daemon still holds the saved login and
    // reads signed out until it receives a login.
    private async Task<(PrefillDaemonServiceBase Daemon, ScriptedLoginDaemonClient Client, RecordingContainerGateway Gateway)> RestartAsync(
        AdoptedContainer c, FakeContainer? container)
    {
        var gateway = new RecordingContainerGateway();
        if (container is not null)
        {
            gateway.ResolvedImageId = container.ImageId;
            gateway.AddContainer(container);
        }
        var client = new ScriptedLoginDaemonClient();
        client.StatusHandler = _ => Task.FromResult<DaemonStatus?>(
            client.StartLoginCallCount == 0 ? LogOffStatus(c.Instance, c.Platform) : SteamStatus(c.Instance, signedIn: true));
        var daemon = BuildDaemon(
            c.Platform, MakeDeps(c.Database, c.Sessions, EnabledConfig(c.Platform, c.Mode), validityDays: 30), gateway, client);
        await daemon.StartAsync(CancellationToken.None).WaitAsync(TestTimeout);
        if (daemon.LastHeadlessSelfAuthAttempt is { } attempt) await attempt.WaitAsync(TestTimeout);
        return (daemon, client, gateway);
    }

    // Fails writes of a session row that match while it is armed or while failures remain, then lets the writes through.
    private sealed class FailingRowSave(Func<EntityEntry<PrefillSession>, bool> fails) : SaveChangesInterceptor
    {
        public volatile bool Armed;

        public int FailuresLeft { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData saving, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if ((Armed || FailuresLeft > 0) && saving.Context!.ChangeTracker.Entries<PrefillSession>()
                    .Any(entry => fails(entry)))
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

    // A daemon whose logout command never answers in time; every other command goes to the scripted daemon.
    private class TimedOutLogoutClient : DispatchProxy
    {
        public ScriptedLoginDaemonClient Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IDaemonClient.LogoutWithReasonAsync))
            {
                return Task.FromException<LogoutOutcome>(new TimeoutException("logout timed out"));
            }
            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
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

    // Holds the first save of a session row that matches after it is armed until the test releases it or the save's token is canceled.
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
                await Release.Task.WaitAsync(cancellationToken);
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
    // Steam reads awaiting-login; Epic and Xbox read not logged in.
    private static DaemonStatus LogOffStatus(string instanceId, PrefillPlatform platform = PrefillPlatform.Steam)
    {
        var status = SteamStatus(instanceId, signedIn: false);
        status.ErrorCode = null;
        if (platform != PrefillPlatform.Steam) status.Status = "not-logged-in";
        return status;
    }

    // The daemon's own push once it finishes a sign-in.
    private static DaemonStatus CommitPush(string instanceId)
    {
        var status = SteamStatus(instanceId, signedIn: true);
        status.Message = "Authenticated and ready for commands";
        return status;
    }

    // A status read of a logged-in daemon carries its startup snapshot text.
    private static DaemonStatus LoggedInRead(string instanceId)
    {
        var status = SteamStatus(instanceId, signedIn: true);
        status.Message = "Initialized";
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
