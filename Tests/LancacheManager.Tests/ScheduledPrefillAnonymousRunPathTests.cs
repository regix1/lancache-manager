using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// Proves the scheduled run path for the ANONYMOUS
/// services (Battle.net/Riot) with SelectedAppIds populated. The one real unknown from the plan was
/// whether <see cref="ScheduledPrefillService.RunServiceAsync"/>'s step-2b live-status poll
/// (<c>status?.Status == "logged-in"</c>) would wrongly skip anonymous daemons. Investigation found
/// BattleNetPrefill/RiotPrefill's <c>HandleStatus</c> always answers <c>{ isLoggedIn: true }</c>
/// (both daemons' <c>Api/SocketCommandInterface.cs:127-141</c>), which
/// <c>DaemonClientBase.GetStatusAsync</c> (shared by both transports) turns into
/// <c>Status = "logged-in"</c> - so the gate already passes today; no production fix was needed.
/// This test exercises the full chain (gate pass -> SetSelectedAppsAsync -> PrefillAsync with the
/// preset forced off by SelectedAppIds) end-to-end with a fake <see cref="IDaemonClient"/> standing
/// in for the real socket, using that REAL "logged-in" string rather than an invented one.
/// </summary>
public class ScheduledPrefillAnonymousRunPathTests
{
    private static readonly Guid SystemUserId = ScheduledPrefillConstants.DeriveSystemUserId();

    public static IEnumerable<object[]> AnonymousServices()
    {
        yield return new object[] { PrefillPlatform.BattleNet };
        yield return new object[] { PrefillPlatform.Riot };
    }

    [Theory]
    [MemberData(nameof(AnonymousServices))]
    public async Task RunServiceAsync_AnonymousServiceWithSelectedApps_PassesLiveStatusGate_AndSendsSelection(
        PrefillPlatform platform)
    {
        var (daemon, client) = CreateRunnablePersistentDaemon(platform);
        using var daemonProvider = BuildProviderWithDaemon(platform, daemon);
        using var schedulerProvider = new ServiceCollection().BuildServiceProvider();
        var scheduledPrefillService = new ScheduledPrefillService(
            NullLogger<ScheduledPrefillService>.Instance,
            schedulerProvider.GetRequiredService<IServiceScopeFactory>(),
            (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>());

        var serviceConfig = new ScheduledPrefillServiceConfigDto
        {
            ServiceId = platform,
            ScheduleId = ScheduledPrefillConfigFactory.GetDefaultScheduleId(platform),
            ScheduleName = "Default",
            Enabled = true,
            NotificationMode = NotificationMode.Silent,
            IntervalHours = 24,
            Preset = ScheduledPrefillPreset.All,
            TopCount = null,
            SelectedAppIds = new List<string> { "wow", "d3" },
            OperatingSystems = new List<ScheduledPrefillOperatingSystem> { ScheduledPrefillOperatingSystem.Windows },
            Force = false,
            MaxConcurrency = new ScheduledPrefillMaxConcurrencyDto { Mode = ScheduledPrefillMaxConcurrencyMode.Auto }
        };
        var config = ScheduledPrefillConfigFactory.CreateDefault();

        var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationsProxy>();
        var recorder = (RecordingNotificationsProxy)notifications;

        var runServiceAsync = typeof(ScheduledPrefillService).GetMethod(
            "RunServiceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // The run-level visibility flag is false here: the single due platform is Silent, so the OR
        // across due platforms is false, and every relayed event must carry showNotification=false.
        var result = await (Task<ScheduledPrefillServiceRunResult>)runServiceAsync.Invoke(
            scheduledPrefillService,
            new object?[] { MakeServiceRun(serviceConfig), daemonProvider, notifications, config, false })!;

        scheduledPrefillService.Dispose();

        // The gate must not skip: an anonymous daemon reporting the REAL "logged-in" status must be
        // treated as runnable, exactly like an authenticated Steam/Epic/Xbox persistent container.
        Assert.Equal(ScheduledPrefillServiceRunResult.Ran, result);
        Assert.DoesNotContain("needs-login", recorder.Stages);
        Assert.Contains("completed", recorder.Stages);
        Assert.NotEmpty(recorder.ShowNotificationValues);
        Assert.All(recorder.ShowNotificationValues, Assert.False);

        // The selection must actually reach the daemon, and the preset must be forced off in favor
        // of it (ScheduledPrefillService.cs:338-348's hasSelectedApps branch).
        Assert.Equal(new List<string> { "wow", "d3" }, client.SelectedAppIdsSent);
        Assert.True(client.PrefillCalled);
        Assert.False(client.PrefillAllRequested);
        Assert.False(client.PrefillRecentRequested);
        Assert.Null(client.PrefillTopRequested);
    }

    [Fact]
    public async Task RunServiceAsync_EmptySelectionClearsDaemonSelectionBeforePresetRun()
    {
        var (daemon, client) = CreateRunnablePersistentDaemon(PrefillPlatform.BattleNet);

        var (result, _) = await RunSingleServiceAsync(PrefillPlatform.BattleNet, daemon);

        Assert.Equal(ScheduledPrefillServiceRunResult.Ran, result);
        Assert.NotNull(client.SelectedAppIdsSent);
        Assert.Empty(client.SelectedAppIdsSent);
        Assert.True(client.PrefillAllRequested);
    }

    /// <summary>
    /// INVERTED on purpose. This used to assert that a live guest/manual session deferred the run,
    /// and that is the behavior being removed: a temporary or guest container is a separate entity
    /// with its own container and its own download, so it must never hold up a scheduled run. Driven
    /// through the real run path rather than the pure gate because the whole point is that the run no
    /// longer even shows the gate the other sessions on the daemon.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunServiceAsync_ManualSessionActive_StillRuns(bool manualSessionIsPrefilling)
    {
        var (daemon, _) = CreateRunnablePersistentDaemon(PrefillPlatform.BattleNet);

        var manualSession = new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = Guid.NewGuid(),
            Status = DaemonSessionStatus.Active,
            IsPersistent = false,
            IsPrefilling = manualSessionIsPrefilling,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        };
        manualSession.Client = new FakeAnonymousDaemonClient(manualSession);
        InjectSession(daemon, manualSession);

        var (result, recorder) = await RunSingleServiceAsync(PrefillPlatform.BattleNet, daemon);

        Assert.Equal(ScheduledPrefillServiceRunResult.Ran, result);
        Assert.Contains("completed", recorder.Stages);
        Assert.DoesNotContain("skipped", recorder.Stages);
        Assert.DoesNotContain("signalr.scheduledPrefill.skippedManualActive", recorder.StageKeys);
    }

    /// <summary>
    /// The second busy branch: a prior scheduled run is still going on the persistent container. It
    /// shares the system user id with the scheduler, so only <c>IsPrefilling</c> can defer it, and it
    /// must reach the card as a different key than the manual-session skip above.
    /// </summary>
    [Fact]
    public async Task RunServiceAsync_PersistentSessionAlreadyPrefilling_SkipsWithAlreadyRunningStageKey()
    {
        var (daemon, _) = CreateRunnablePersistentDaemon(PrefillPlatform.BattleNet);
        daemon.GetActivePersistentSession()!.IsPrefilling = true;

        var (result, recorder) = await RunSingleServiceAsync(PrefillPlatform.BattleNet, daemon);

        Assert.Equal(ScheduledPrefillServiceRunResult.Skipped, result);
        Assert.Equal(new List<string> { "skipped" }, recorder.Stages);
        Assert.Equal(new List<string?> { "signalr.scheduledPrefill.skippedAlreadyRunning" }, recorder.StageKeys);
    }

    /// <summary>
    /// A run the daemon ends with an error transfers no bytes, which the completion message reads as
    /// "everything was already cached" - so before this was fixed a total prefill outage was
    /// announced as a success and stamped a successful last run.
    /// </summary>
    [Fact]
    public async Task RunServiceAsync_DaemonEndedTheRunFailed_ReportsFailedWithTheDaemonReason()
    {
        const string daemonReason = "Prefill failed: Steam connection was lost before metadata could be loaded";
        var (daemon, _) = CreateRunnablePersistentDaemon(
            PrefillPlatform.BattleNet, terminalFailureMessage: daemonReason, transferredBytes: 0);

        var (result, recorder) = await RunSingleServiceAsync(PrefillPlatform.BattleNet, daemon);

        Assert.Equal(ScheduledPrefillServiceRunResult.Failed, result);
        Assert.Equal("failed", recorder.Stages[^1]);
        Assert.Equal(daemonReason, recorder.Messages[^1]);
        Assert.Null(recorder.StageKeys[^1]);
        Assert.DoesNotContain("completed", recorder.Stages);
    }

    /// <summary>
    /// The daemon finishes a run whose every app failed by reporting success: nothing threw, so its
    /// result carries Success=true and the session never reaches a Failed state. Only its per-app
    /// failure count says the run downloaded none of the games it was asked for.
    /// </summary>
    [Fact]
    public async Task RunServiceAsync_EveryAppFailedButDaemonReportedSuccess_ReportsFailedWithTheCount()
    {
        var (daemon, _) = CreateRunnablePersistentDaemon(
            PrefillPlatform.BattleNet, failedApps: 2, totalApps: 2, transferredBytes: 0);

        var (result, recorder) = await RunSingleServiceAsync(PrefillPlatform.BattleNet, daemon);

        Assert.Equal(ScheduledPrefillServiceRunResult.Failed, result);
        Assert.Equal("failed", recorder.Stages[^1]);
        Assert.Equal("2 of 2 games failed to download", recorder.Messages[^1]);
        Assert.Equal("signalr.scheduledPrefill.failedApps", recorder.StageKeys[^1]);
        Assert.DoesNotContain("completed", recorder.Stages);
    }

    /// <summary>
    /// A run that lost some games but downloaded others is still a failed run, and the count has to
    /// say which it was rather than implying everything failed.
    /// </summary>
    [Fact]
    public async Task RunServiceAsync_SomeAppsFailed_ReportsFailedNamingHowMany()
    {
        var (daemon, _) = CreateRunnablePersistentDaemon(
            PrefillPlatform.BattleNet, failedApps: 1, totalApps: 3, transferredBytes: 1024);

        var (result, recorder) = await RunSingleServiceAsync(PrefillPlatform.BattleNet, daemon);

        Assert.Equal(ScheduledPrefillServiceRunResult.Failed, result);
        Assert.Equal("1 of 3 games failed to download", recorder.Messages[^1]);
    }

    /// <summary>
    /// The daemon's own reported reason names the actual cause, so it must not be replaced by the
    /// generic count when a run both failed outright and lost individual games.
    /// </summary>
    [Fact]
    public async Task RunServiceAsync_DaemonErrorAndFailedApps_KeepsTheDaemonReason()
    {
        const string daemonReason = "Prefill failed: Steam connection was lost";
        var (daemon, _) = CreateRunnablePersistentDaemon(
            PrefillPlatform.BattleNet,
            terminalFailureMessage: daemonReason,
            failedApps: 2,
            totalApps: 2,
            transferredBytes: 0);

        var (result, recorder) = await RunSingleServiceAsync(PrefillPlatform.BattleNet, daemon);

        Assert.Equal(ScheduledPrefillServiceRunResult.Failed, result);
        Assert.Equal(daemonReason, recorder.Messages[^1]);
        Assert.Null(recorder.StageKeys[^1]);
    }

    /// <summary>
    /// The counterpart the failure branch must not swallow: a run that really did finish with
    /// everything already cached still transfers zero bytes, and still has to say it completed.
    /// </summary>
    [Fact]
    public async Task RunServiceAsync_RunFinishedWithZeroBytes_StillReportsCompleted()
    {
        var (daemon, _) = CreateRunnablePersistentDaemon(PrefillPlatform.BattleNet, transferredBytes: 0);

        var (result, recorder) = await RunSingleServiceAsync(PrefillPlatform.BattleNet, daemon);

        Assert.Equal(ScheduledPrefillServiceRunResult.Ran, result);
        Assert.Equal("completed", recorder.Stages[^1]);
        Assert.Equal("signalr.scheduledPrefill.completeNoBytes", recorder.StageKeys[^1]);
    }

    [Fact]
    public async Task NotifyPrefillProgressAsync_SmallerSecondGame_KeepsAggregateAndHeartbeatMoving()
    {
        var (daemon, _) = CreateRunnablePersistentDaemon(PrefillPlatform.BattleNet);
        var testableDaemon = Assert.IsType<TestableBattleNetDaemonService>(daemon);
        var session = Assert.IsType<DaemonSession>(daemon.GetActivePersistentSession());
        session.IsPrefilling = true;
        session.PrefillState = PrefillState.Downloading;

        await testableDaemon.PublishProgressAsync(session, new PrefillProgress
        {
            State = "downloading",
            CurrentAppId = "1",
            CurrentAppName = "Large game",
            BytesDownloaded = 1_000,
            TotalBytes = 1_000,
            TotalApps = 2
        });
        await testableDaemon.PublishProgressAsync(session, new PrefillProgress
        {
            State = "app_completed",
            CurrentAppId = "1",
            CurrentAppName = "Large game",
            BytesDownloaded = 1_000,
            TotalBytes = 1_000,
            TotalApps = 2,
            UpdatedApps = 1,
            Result = "Success"
        });
        await testableDaemon.PublishProgressAsync(session, new PrefillProgress
        {
            State = "downloading",
            CurrentAppId = "2",
            CurrentAppName = "Smaller game",
            BytesDownloaded = 0,
            TotalBytes = 500,
            TotalApps = 2,
            UpdatedApps = 1
        });

        var staleHeartbeat = DateTime.UtcNow.AddMinutes(-10).Ticks;
        Volatile.Write(ref session.LastProgressTicksUtc, staleHeartbeat);

        await testableDaemon.PublishProgressAsync(session, new PrefillProgress
        {
            State = "downloading",
            CurrentAppId = "2",
            CurrentAppName = "Smaller game",
            BytesDownloaded = 100,
            TotalBytes = 500,
            TotalApps = 2,
            UpdatedApps = 1
        });

        Assert.Equal(1_100, session.TotalBytesTransferred);
        Assert.Equal(1_000, session.CompletedBytesTransferred);
        Assert.True(Volatile.Read(ref session.LastProgressTicksUtc) > staleHeartbeat);
        Assert.False(PrefillDaemonServiceBase.IsPrefillStalled(
            session,
            DateTime.UtcNow,
            TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public async Task ScheduledRelay_UnknownTotal_RecordsCurrentGameAndBytesWithoutPercent()
    {
        using var schedulerProvider = new ServiceCollection().BuildServiceProvider();
        using var scheduledPrefillService = new ScheduledPrefillService(
            NullLogger<ScheduledPrefillService>.Instance,
            schedulerProvider.GetRequiredService<IServiceScopeFactory>(),
            (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>());
        var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationsProxy>();
        var recorder = (RecordingNotificationsProxy)notifications;
        var serviceRun = MakeServiceRun(new ScheduledPrefillServiceConfigDto
        {
            ServiceId = PrefillPlatform.BattleNet,
            ScheduleId = ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.BattleNet),
            ScheduleName = "Default",
            Enabled = true,
            NotificationMode = NotificationMode.Silent,
            IntervalHours = 24,
            Preset = ScheduledPrefillPreset.Recent,
            SelectedAppIds = new List<string>(),
            OperatingSystems = new List<ScheduledPrefillOperatingSystem> { ScheduledPrefillOperatingSystem.Windows },
            Force = false,
            MaxConcurrency = new ScheduledPrefillMaxConcurrencyDto { Mode = ScheduledPrefillMaxConcurrencyMode.Auto }
        });
        serviceRun.State.Record("running", "Prefill in progress", "signalr.scheduledPrefill.running", 1d);
        var session = new DaemonSession
        {
            Id = "unknown-total",
            IsPrefilling = true,
            PrefillState = PrefillState.Downloading
        };

        var relayType = typeof(ScheduledPrefillService).GetNestedType(
            "ScheduledPrefillProgressRelay",
            BindingFlags.NonPublic)!;
        var relay = Activator.CreateInstance(
            relayType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { scheduledPrefillService, notifications, session, serviceRun, session.Id, true },
            culture: null)!;
        relayType.GetMethod("Arm", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(relay, null);
        var onProgress = relayType.GetMethod("OnProgressAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await (Task)onProgress.Invoke(relay, new object[]
        {
            session,
            new PrefillProgress
            {
                State = "downloading",
                CurrentAppId = "123",
                CurrentAppName = "Battlefield 1",
                BytesDownloaded = 33_200,
                TotalBytes = 100_000,
                TotalApps = 0
            },
            1L
        })!;

        Assert.Equal("Downloading Battlefield 1", serviceRun.State.Message);
        object? recoveredPercent = serviceRun.State.PercentComplete;
        Assert.Null(recoveredPercent);
        Assert.Equal("signalr.scheduledPrefill.downloadingGameUnknownTotal", recorder.StageKeys.Single());
        Assert.Equal(33_200, recorder.BytesDownloaded.Single());
        Assert.Equal(100_000, recorder.TotalBytes.Single());
        Assert.Null(recorder.PercentCompleteValues.Single());
    }

    [Fact]
    public async Task RunServiceAsync_Stall_CancelsAndFailsTheExactSession()
    {
        var (daemon, client) = CreateRunnablePersistentDaemon(
            PrefillPlatform.BattleNet,
            leavePrefilling: true);
        var session = Assert.IsType<DaemonSession>(daemon.GetActivePersistentSession());

        var (result, recorder) = await RunSingleServiceAsync(PrefillPlatform.BattleNet, daemon);

        Assert.Equal(ScheduledPrefillServiceRunResult.Failed, result);
        Assert.Equal(1, client.CancelPrefillCalls);
        Assert.False(session.IsPrefilling);
        Assert.Equal(PrefillState.Failed, session.PrefillState);
        Assert.Equal("Prefill stalled (no progress)", recorder.Messages[^1]);
    }

    /// <summary>
    /// Drives the real <c>RunServiceAsync</c> against an already-prepared daemon and returns the run
    /// result with the notifications it emitted. The preset and selection fields are left empty
    /// because the busy gate answers at step 3, before step 4 ever reads them.
    /// </summary>
    private static async Task<(ScheduledPrefillServiceRunResult Result, RecordingNotificationsProxy Recorder)> RunSingleServiceAsync(
        PrefillPlatform platform,
        PrefillDaemonServiceBase daemon)
    {
        using var daemonProvider = BuildProviderWithDaemon(platform, daemon);
        using var schedulerProvider = new ServiceCollection().BuildServiceProvider();
        var scheduledPrefillService = new ScheduledPrefillService(
            NullLogger<ScheduledPrefillService>.Instance,
            schedulerProvider.GetRequiredService<IServiceScopeFactory>(),
            (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>());

        var serviceConfig = new ScheduledPrefillServiceConfigDto
        {
            ServiceId = platform,
            ScheduleId = ScheduledPrefillConfigFactory.GetDefaultScheduleId(platform),
            ScheduleName = "Default",
            Enabled = true,
            NotificationMode = NotificationMode.Silent,
            IntervalHours = 24,
            Preset = ScheduledPrefillPreset.All,
            TopCount = null,
            SelectedAppIds = new List<string>(),
            OperatingSystems = new List<ScheduledPrefillOperatingSystem> { ScheduledPrefillOperatingSystem.Windows },
            Force = false,
            MaxConcurrency = new ScheduledPrefillMaxConcurrencyDto { Mode = ScheduledPrefillMaxConcurrencyMode.Auto }
        };

        var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationsProxy>();
        var recorder = (RecordingNotificationsProxy)notifications;

        var runServiceAsync = typeof(ScheduledPrefillService).GetMethod(
            "RunServiceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var result = await (Task<ScheduledPrefillServiceRunResult>)runServiceAsync.Invoke(
            scheduledPrefillService,
            new object?[]
            {
                MakeServiceRun(serviceConfig), daemonProvider, notifications,
                ScheduledPrefillConfigFactory.CreateDefault(), false
            })!;

        scheduledPrefillService.Dispose();

        return (result, recorder);
    }

    /// <summary>
    /// The one platform's slice of a run that <c>RunServiceAsync</c> now takes instead of a bare
    /// operation id: a real tracked run also carries its own cancellation token, but nothing in these
    /// tests cancels, so <see cref="CancellationToken.None"/> stands in for it.
    /// </summary>
    private static ScheduledPrefillServiceRun MakeServiceRun(ScheduledPrefillServiceConfigDto serviceConfig)
        => new(
            serviceConfig,
            Guid.Empty,
            "op-1",
            "run-1",
            new ScheduledPrefillServiceRunState(
                serviceConfig.ServiceId,
                serviceConfig.ScheduleId,
                serviceConfig.ScheduleName),
            CancellationToken.None);

    private static (PrefillDaemonServiceBase Daemon, FakeAnonymousDaemonClient Client) CreateRunnablePersistentDaemon(
        PrefillPlatform platform,
        string? terminalFailureMessage = null,
        long transferredBytes = 1024,
        int failedApps = 0,
        int totalApps = 0,
        bool leavePrefilling = false)
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"anon_run_path_{Guid.NewGuid():N}")
            .Options;
        var dbFactory = new InMemoryDbContextFactory(dbOptions);
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        var cacheService = new PrefillCacheService(dbFactory, NullLogger<PrefillCacheService>.Instance);
        var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
        var configuration = new ConfigurationBuilder().Build();
        var pathResolver = (IPathResolver)DispatchProxy.Create<IPathResolver, NullReturningProxy>();
        var stateService = (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>();
        var networkOptions = new StaticOptionsMonitor<PrefillNetworkOptions>(new PrefillNetworkOptions());

        PrefillDaemonServiceBase daemon = platform switch
        {
            PrefillPlatform.BattleNet => new TestableBattleNetDaemonService(
                NullLogger<BattleNetDaemonService>.Instance, notifications, configuration, pathResolver,
                stateService, sessionService, cacheService, networkOptions),
            PrefillPlatform.Riot => new TestableRiotDaemonService(
                NullLogger<RiotDaemonService>.Instance, notifications, configuration, pathResolver,
                stateService, sessionService, cacheService, networkOptions),
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Only anonymous services are covered here")
        };

        var session = new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = SystemUserId,
            Status = DaemonSessionStatus.Active,
            IsPersistent = true,
            IsPrefilling = false,
            AuthState = DaemonAuthState.Authenticated,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };
        var client = new FakeAnonymousDaemonClient(
            session, terminalFailureMessage, transferredBytes, failedApps, totalApps, leavePrefilling);
        session.Client = client;

        InjectSession(daemon, session);

        return (daemon, client);
    }

    private static void InjectSession(PrefillDaemonServiceBase daemon, DaemonSession session)
    {
        switch (daemon)
        {
            case TestableBattleNetDaemonService bnet:
                bnet.InjectSession(session);
                break;
            case TestableRiotDaemonService riot:
                riot.InjectSession(session);
                break;
        }
    }

    private static ServiceProvider BuildProviderWithDaemon(PrefillPlatform platform, PrefillDaemonServiceBase daemon)
    {
        var services = new ServiceCollection();
        switch (platform)
        {
            case PrefillPlatform.BattleNet:
                services.AddSingleton((BattleNetDaemonService)daemon);
                break;
            case PrefillPlatform.Riot:
                services.AddSingleton((RiotDaemonService)daemon);
                break;
        }

        return services.BuildServiceProvider();
    }

    // Test-only seam: _sessions is `protected` on PrefillDaemonServiceBase so production code never
    // exposes a way to inject a session without going through real Docker container creation.
    private sealed class TestableBattleNetDaemonService : BattleNetDaemonService
    {
        public TestableBattleNetDaemonService(
            Microsoft.Extensions.Logging.ILogger<BattleNetDaemonService> logger,
            ISignalRNotificationService notifications,
            IConfiguration configuration,
            IPathResolver pathResolver,
            IStateService stateService,
            PrefillSessionService sessionService,
            PrefillCacheService cacheService,
            IOptionsMonitor<PrefillNetworkOptions> networkOptions)
            : base(logger, notifications, configuration, pathResolver, stateService, sessionService, cacheService, networkOptions, new TestLancacheServerLocator(), new UnavailableContainerGatewayFactory())
        {
        }

        public void InjectSession(DaemonSession session) => _sessions[session.Id] = session;

        public Task PublishProgressAsync(DaemonSession session, PrefillProgress progress)
            => NotifyPrefillProgressAsync(session, progress);
    }

    private sealed class TestableRiotDaemonService : RiotDaemonService
    {
        public TestableRiotDaemonService(
            Microsoft.Extensions.Logging.ILogger<RiotDaemonService> logger,
            ISignalRNotificationService notifications,
            IConfiguration configuration,
            IPathResolver pathResolver,
            IStateService stateService,
            PrefillSessionService sessionService,
            PrefillCacheService cacheService,
            IOptionsMonitor<PrefillNetworkOptions> networkOptions)
            : base(logger, notifications, configuration, pathResolver, stateService, sessionService, cacheService, networkOptions, new TestLancacheServerLocator(), new UnavailableContainerGatewayFactory())
        {
        }

        public void InjectSession(DaemonSession session) => _sessions[session.Id] = session;
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public InMemoryDbContextFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public AppDbContext CreateDbContext() => new AppDbContext(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext(_options));
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value)
        {
            CurrentValue = value;
        }

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>
    /// Records the <c>stage</c> field of every <c>NotifyAllAsync</c> payload
    /// (<see cref="ScheduledPrefillService"/>'s <c>ReportProgressAsync</c> is the only caller that
    /// shapes payloads this way), so the test can assert the run never emitted "needs-login" and did
    /// reach "completed". Every other member returns its type default (mirrors NullReturningProxy).
    /// </summary>
    // Not sealed: DispatchProxy.Create derives a runtime subclass, which the compiler can only
    // allow casting an interface-typed reference back to when the class isn't sealed (mirrors
    // ScheduledPrefillServiceTests.CancellingTrackerProxy).
    private class RecordingNotificationsProxy : DispatchProxy
    {
        public List<string> Stages { get; } = new();
        public List<bool> ShowNotificationValues { get; } = new();

        /// <summary>
        /// The <c>stageKey</c> alongside each recorded stage. Nullable entries are the point: the
        /// property is always present on the payload, so a caller that stops passing a key records a
        /// null here rather than dropping out of the list.
        /// </summary>
        public List<string?> StageKeys { get; } = new();

        public List<double?> PercentCompleteValues { get; } = new();
        public List<long?> BytesDownloaded { get; } = new();
        public List<long?> TotalBytes { get; } = new();

        /// <summary>
        /// The one sentence each recorded stage put on the card. It is what the user reads, so a
        /// terminal line that names the daemon's own reason can only be asserted from here.
        /// </summary>
        public List<string> Messages { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISignalRNotificationService.NotifyAllAsync) && args is { Length: >= 2 })
            {
                var stageProperty = args[1]?.GetType().GetProperty("stage");
                if (stageProperty?.GetValue(args[1]) is string stage)
                {
                    Stages.Add(stage);
                }

                var messageProperty = args[1]?.GetType().GetProperty("message");
                if (messageProperty?.GetValue(args[1]) is string message)
                {
                    Messages.Add(message);
                }

                var stageKeyProperty = args[1]?.GetType().GetProperty("stageKey");
                if (stageKeyProperty is not null)
                {
                    StageKeys.Add(stageKeyProperty.GetValue(args[1]) as string);
                }

                var percentProperty = args[1]?.GetType().GetProperty("percentComplete");
                if (percentProperty is not null)
                {
                    PercentCompleteValues.Add((double?)percentProperty.GetValue(args[1]));
                }

                var bytesProperty = args[1]?.GetType().GetProperty("bytesDownloaded");
                if (bytesProperty is not null)
                {
                    BytesDownloaded.Add((long?)bytesProperty.GetValue(args[1]));
                }

                var totalBytesProperty = args[1]?.GetType().GetProperty("totalBytes");
                if (totalBytesProperty is not null)
                {
                    TotalBytes.Add((long?)totalBytesProperty.GetValue(args[1]));
                }

                var showNotificationProperty = args[1]?.GetType().GetProperty("showNotification");
                if (showNotificationProperty?.GetValue(args[1]) is bool showNotification)
                {
                    ShowNotificationValues.Add(showNotification);
                }
            }

            return DefaultReturnValue(targetMethod);
        }
    }

    /// <summary>
    /// Default-return logic for the recording proxy above: Task methods get a completed task,
    /// non-nullable value types get their default instance, everything else (including void) is null.
    /// </summary>
    private static object? DefaultReturnValue(MethodInfo? targetMethod)
    {
        var returnType = targetMethod?.ReturnType;

        if (returnType is null || returnType == typeof(void))
        {
            return null;
        }

        if (returnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (returnType.IsValueType && Nullable.GetUnderlyingType(returnType) is null)
        {
            return Activator.CreateInstance(returnType);
        }

        return null;
    }

    /// <summary>
    /// Stands in for the real socket-based <see cref="IDaemonClient"/>. <see cref="GetStatusAsync"/>
    /// returns the REAL status string BattleNet/Riot daemons report ("logged-in" via isLoggedIn:true
    /// on every status poll, per the daemon-side investigation cited on this class). Members outside
    /// this test's happy path throw, so an unexpected call fails loudly instead of returning a
    /// silently-wrong default.
    /// </summary>
    private sealed class FakeAnonymousDaemonClient : IDaemonClient
    {
        private readonly DaemonSession _session;
        private readonly string? _terminalFailureMessage;
        private readonly long _transferredBytes;
        private readonly int _failedApps;
        private readonly int _totalApps;
        private readonly bool _leavePrefilling;

        public FakeAnonymousDaemonClient(
            DaemonSession session,
            string? terminalFailureMessage = null,
            long transferredBytes = 1024,
            int failedApps = 0,
            int totalApps = 0,
            bool leavePrefilling = false)
        {
            _session = session;
            _terminalFailureMessage = terminalFailureMessage;
            _transferredBytes = transferredBytes;
            _failedApps = failedApps;
            _totalApps = totalApps;
            _leavePrefilling = leavePrefilling;
        }

        public List<string>? SelectedAppIdsSent { get; private set; }
        public bool PrefillCalled { get; private set; }
        public bool PrefillAllRequested { get; private set; }
        public bool PrefillRecentRequested { get; private set; }
        public int? PrefillTopRequested { get; private set; }
        public int CancelPrefillCalls { get; private set; }

        public event Func<CredentialChallenge, Task>? OnCredentialChallenge { add { } remove { } }
        public event Func<DaemonStatus, Task>? OnStatusUpdate { add { } remove { } }
        public event Func<SocketPrefillProgress, Task>? OnProgressUpdate { add { } remove { } }
        public event Func<string, Task>? OnError { add { } remove { } }
        public event Func<Task>? OnDisconnected { add { } remove { } }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<DaemonStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<DaemonStatus?>(new DaemonStatus { Status = "logged-in" });

        public Task<CommandResponse> SendCommandAsync(
            string type, Dictionary<string, string>? parameters = null, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException($"Unexpected SendCommandAsync({type}) in this test.");

        public Task<CredentialChallenge?> StartLoginAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Anonymous services never start a login.");

        public Task ProvideCredentialAsync(CredentialChallenge challenge, string credential, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Anonymous services never provide a credential.");

        public Task<CredentialChallenge?> GetAutoLoginChallengeAsync(string sessionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> ProvideAutoLoginAsync(string sessionId, string username, string refreshToken, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> ProvideEpicAutoLoginAsync(string sessionId, string refreshToken, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> ProvideXboxAutoLoginAsync(string sessionId, string refreshToken, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CredentialChallenge?> WaitForChallengeAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CancelLoginAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Anonymous services are not exercised for logout in this test.");

        public Task CancelPrefillAsync(CancellationToken cancellationToken = default)
        {
            CancelPrefillCalls++;
            return Task.CompletedTask;
        }

        public Task<List<OwnedGame>> GetOwnedGamesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new List<OwnedGame>());

        public Task<List<CdnInfo>> GetCdnInfoAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new List<CdnInfo>());

        public Task SetSelectedAppsAsync(List<string> appIds, CancellationToken cancellationToken = default)
        {
            SelectedAppIdsSent = appIds;
            return Task.CompletedTask;
        }

        public Task<PrefillResult> PrefillAsync(
            bool all = false,
            bool recent = false,
            bool recentlyPurchased = false,
            int? top = null,
            bool force = false,
            List<string>? operatingSystems = null,
            int? maxConcurrency = null,
            List<CachedDepotInput>? cachedDepots = null,
            CancellationToken cancellationToken = default)
        {
            PrefillCalled = true;
            PrefillAllRequested = all;
            PrefillRecentRequested = recent;
            PrefillTopRequested = top;

            if (_leavePrefilling)
            {
                Volatile.Write(ref _session.LastProgressTicksUtc, DateTime.UtcNow.AddDays(-1).Ticks);
                return Task.FromResult(new PrefillResult { Success = true, TotalTime = TimeSpan.FromSeconds(1) });
            }

            // The real terminal transition (IsPrefilling -> false) is driven by a later socket
            // event; this fake has no socket, so it flips the flag itself to simulate a run that
            // completes instantaneously, letting RunServiceAsync's poll loop exit immediately.
            //
            // The daemon reports a failed run the same way it reports a finished one - the socket
            // stops prefilling and the terminal funnel stamps the state - so the failure shape here
            // differs only in the state and the reason it leaves behind.
            // The per-app counters ride on the daemon's progress ticks, and the run picks the last
            // one up through the relay's catch-up replay. Leaving the snapshot here is what a real
            // run's final app_completed tick leaves behind.
            if (_failedApps > 0 || _totalApps > 0)
            {
                _session.LastProgress = new PrefillProgress
                {
                    State = "app_completed",
                    FailedApps = _failedApps,
                    TotalApps = _totalApps
                };
                Interlocked.Increment(ref _session.ProgressSequence);
            }

            _session.IsPrefilling = false;
            _session.TotalBytesTransferred = _transferredBytes;
            if (_terminalFailureMessage is not null)
            {
                _session.PrefillState = PrefillState.Failed;
                _session.ErrorMessage = _terminalFailureMessage;
            }

            return Task.FromResult(new PrefillResult { Success = true, TotalTime = TimeSpan.FromSeconds(1) });
        }

        public Task<ClearCacheResult> ClearCacheAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ClearCacheResult> GetCacheInfoAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SelectedAppsStatus> GetSelectedAppsStatusAsync(List<string>? operatingSystems = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CacheStatusResult> CheckCacheStatusAsync(List<CachedDepotInput> cachedDepots, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void ClearPendingChallenges() { }

        public void Dispose() { }
    }
}
