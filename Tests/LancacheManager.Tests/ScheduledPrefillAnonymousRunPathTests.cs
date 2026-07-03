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
/// Lane 2 (session 20260703-001631-1548419933): proves the scheduled run path for the ANONYMOUS
/// services (Battle.net/Riot) with SelectedAppIds populated. The one real unknown from the plan was
/// whether <see cref="ScheduledPrefillService.RunServiceAsync"/>'s step-2b live-status poll
/// (<c>status?.Status == "logged-in"</c>) would wrongly skip anonymous daemons. Investigation found
/// BattleNetPrefill/RiotPrefill's <c>HandleStatus</c> always answers <c>{ isLoggedIn: true }</c>
/// (both daemons' <c>Api/SocketCommandInterface.cs:127-141</c>), which
/// <c>SocketDaemonClient.GetStatusAsync</c> (<c>SocketDaemonClient.cs:552</c>) turns into
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
        var daemonProvider = BuildProviderWithDaemon(platform, daemon);
        using var schedulerProvider = new ServiceCollection().BuildServiceProvider();
        var scheduledPrefillService = new ScheduledPrefillService(
            NullLogger<ScheduledPrefillService>.Instance,
            schedulerProvider.GetRequiredService<IServiceScopeFactory>(),
            (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>());

        var serviceConfig = new ScheduledPrefillServiceConfigDto
        {
            ServiceId = platform,
            Enabled = true,
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

        var attempted = await (Task<bool>)runServiceAsync.Invoke(
            scheduledPrefillService,
            new object?[] { serviceConfig, "op-1", daemonProvider, notifications, config, CancellationToken.None })!;

        scheduledPrefillService.Dispose();

        // The gate must not skip: an anonymous daemon reporting the REAL "logged-in" status must be
        // treated as runnable, exactly like an authenticated Steam/Epic/Xbox persistent container.
        Assert.True(attempted);
        Assert.DoesNotContain("needs-login", recorder.Stages);
        Assert.Contains("completed", recorder.Stages);

        // The selection must actually reach the daemon, and the preset must be forced off in favor
        // of it (ScheduledPrefillService.cs:338-348's hasSelectedApps branch).
        Assert.Equal(new List<string> { "wow", "d3" }, client.SelectedAppIdsSent);
        Assert.True(client.PrefillCalled);
        Assert.False(client.PrefillAllRequested);
        Assert.False(client.PrefillRecentRequested);
        Assert.Null(client.PrefillTopRequested);
    }

    private static (PrefillDaemonServiceBase Daemon, FakeAnonymousDaemonClient Client) CreateRunnablePersistentDaemon(
        PrefillPlatform platform)
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
        var client = new FakeAnonymousDaemonClient(session);
        session.Client = client;

        switch (daemon)
        {
            case TestableBattleNetDaemonService bnet:
                bnet.InjectSession(session);
                break;
            case TestableRiotDaemonService riot:
                riot.InjectSession(session);
                break;
        }

        return (daemon, client);
    }

    private static IServiceProvider BuildProviderWithDaemon(PrefillPlatform platform, PrefillDaemonServiceBase daemon)
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
            : base(logger, notifications, configuration, pathResolver, stateService, sessionService, cacheService, networkOptions)
        {
        }

        public void InjectSession(DaemonSession session) => _sessions[session.Id] = session;
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
            : base(logger, notifications, configuration, pathResolver, stateService, sessionService, cacheService, networkOptions)
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
    /// (<see cref="ScheduledPrefillService"/>'s <c>EmitProgressAsync</c> is the only caller that
    /// shapes payloads this way), so the test can assert the run never emitted "needs-login" and did
    /// reach "completed". Every other member returns its type default (mirrors NullReturningProxy).
    /// </summary>
    // Not sealed: DispatchProxy.Create derives a runtime subclass, which the compiler can only
    // allow casting an interface-typed reference back to when the class isn't sealed (mirrors
    // ScheduledPrefillServiceTests.CancellingTrackerProxy).
    private class RecordingNotificationsProxy : DispatchProxy
    {
        public List<string> Stages { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISignalRNotificationService.NotifyAllAsync) && args is { Length: >= 2 })
            {
                var stageProperty = args[1]?.GetType().GetProperty("stage");
                if (stageProperty?.GetValue(args[1]) is string stage)
                {
                    Stages.Add(stage);
                }
            }

            return DefaultReturnValue(targetMethod);
        }
    }

    /// <summary>
    /// Minimal do-nothing proxy for interfaces whose members are not exercised by this test's
    /// happy path (mirrors the pattern in ScheduledPrefillServiceTests.NullReturningProxy).
    /// </summary>
    // Not sealed: DispatchProxy.Create requires TProxy to be a non-sealed class at runtime.
    private class NullReturningProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => DefaultReturnValue(targetMethod);
    }

    /// <summary>
    /// Shared default-return logic for both proxies above: Task methods get a completed task,
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

        public FakeAnonymousDaemonClient(DaemonSession session)
        {
            _session = session;
        }

        public List<string>? SelectedAppIdsSent { get; private set; }
        public bool PrefillCalled { get; private set; }
        public bool PrefillAllRequested { get; private set; }
        public bool PrefillRecentRequested { get; private set; }
        public int? PrefillTopRequested { get; private set; }

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

        public Task<bool> ProvideXboxAutoLoginAsync(string sessionId, string refreshToken, string deviceKeyPkcs8, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CredentialChallenge?> WaitForChallengeAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CancelLoginAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Anonymous services are not exercised for logout in this test.");

        public Task CancelPrefillAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

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

            // The real terminal transition (IsPrefilling -> false) is driven by a later socket
            // event; this fake has no socket, so it flips the flag itself to simulate a run that
            // completes instantaneously, letting RunServiceAsync's poll loop exit immediately.
            _session.IsPrefilling = false;
            _session.TotalBytesTransferred = 1024;

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
