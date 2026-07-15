using System.Collections.Concurrent;
using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// End-to-end startup-reconcile orchestration for the persistent prefill container lifecycle, driven
/// against a <see cref="RecordingContainerGateway"/> (the docker seam) plus a fake reconnect client, so
/// the cleanup -&gt; re-adopt -&gt; recreate flow can be exercised without a live Docker daemon or daemon
/// socket. Complements the pure-helper coverage (<c>PersistentSingletonGateTests</c>,
/// <c>PersistentLoginValidityClockTests</c>) by proving the whole <see cref="PrefillDaemonServiceBase.StartAsync"/>
/// path wires those decisions to the right container operations and DB row transitions.
/// </summary>
public sealed class PrefillContainerOrchestrationTests : IDisposable
{
    private const string SteamPersistentContainerName = "steam-daemon-persistent";

    private readonly List<string> _tempRoots = new();

    // ============================ C-a ============================
    // Mode-2 (KeepAcrossRestart): a persistent container left running by a graceful shutdown detach is
    // re-adopted on the next start with its login intact - no stop/kill/remove/logout, DB row back to Active.
    [Fact]
    public async Task StartAsync_RunningPersistentContainer_ReadoptedRowActive_NoDestructiveDockerOps()
    {
        var (dbOptions, dbFactory) = NewDatabase();
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);

        // A persistent Steam session from the previous life: Active DB row + a still-running container that
        // the detach on shutdown left behind, carrying the labels/secret the re-adopt path recovers.
        var sessionId = await SeedActivePersistentRowAsync(sessionService, expiresAt: DateTime.UtcNow.AddDays(30));
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(RunningPersistentContainer(sessionId));

        var daemon = new TestSteamDaemon(MakeDeps(dbFactory, sessionService, Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true)), gateway);

        await daemon.StartAsync(CancellationToken.None);

        // Re-adopted: registered in memory and reactivated in the DB.
        var session = SessionsOf(daemon).Values.SingleOrDefault(s => s.IsPersistent);
        Assert.NotNull(session);
        Assert.Equal(sessionId, session!.Id);
        Assert.Equal(PrefillSessionStatus.Active, (await GetRowAsync(dbOptions, sessionId))!.Status);

        // Login intact: the container is still present + running, and nothing tore it down or logged it out.
        Assert.True(gateway.ContainsContainer(session.ContainerId));
        Assert.Equal(0, gateway.DestructiveCallCount);
        Assert.Equal(0, ((FakeReconnectDaemonClient)session.Client).LogoutCount);

        daemon.Dispose();
    }

    // ============================ C-e ============================
    // Five services, ONE shared DB: each daemon re-adopts its own running container, and no daemon's
    // platform-scoped orphan sweep re-orphans a row another daemon already reactivated. Every row ends Active.
    [Fact]
    public async Task StartAsync_FiveServicesSharedDatabase_EveryReadoptedRowActive()
    {
        var (dbOptions, dbFactory) = NewDatabase();
        var config = Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true);

        var specs = new (PrefillPlatform Platform, string Prefix)[]
        {
            (PrefillPlatform.Steam, "steam-daemon-"),
            (PrefillPlatform.Epic, "epic-daemon-"),
            (PrefillPlatform.Xbox, "xbox-daemon-"),
            (PrefillPlatform.BattleNet, "battlenet-prefill-"),
            (PrefillPlatform.Riot, "riot-prefill-")
        };

        var sessionIds = new Dictionary<PrefillPlatform, string>();
        var daemons = new List<PrefillDaemonServiceBase>();

        foreach (var (platform, prefix) in specs)
        {
            var svc = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
            var containerName = $"{prefix}persistent";
            var sessionId = await SeedActivePersistentRowAsync(svc, expiresAt: DateTime.UtcNow.AddDays(30), platform: platform, containerName: containerName);
            sessionIds[platform] = sessionId;

            var gateway = new RecordingContainerGateway();
            gateway.AddContainer(RunningPersistentContainer(sessionId, containerName, platform));
            daemons.Add(BuildDaemon(platform, MakeDeps(dbFactory, svc, config), gateway));
        }

        // Start them in order against the shared DB - the order a real host boots the hosted services.
        foreach (var daemon in daemons)
        {
            await daemon.StartAsync(CancellationToken.None);
        }

        // Every platform's row is Active at the end: no cross-platform re-orphaning.
        foreach (var (platform, _) in specs)
        {
            var row = await GetRowAsync(dbOptions, sessionIds[platform]);
            Assert.Equal(PrefillSessionStatus.Active, row!.Status);
        }

        foreach (var daemon in daemons)
        {
            daemon.Dispose();
        }
    }

    // ============================ C-b ============================
    // Mode-3 (FullPersistence) recreate after an outage: the vanished container is recreated and its new
    // session inherits the prior life's still-future validity window (does not silently extend it).
    [Fact]
    public async Task StartAsync_FullPersistenceRecreate_InheritsFutureExpiryAnchor()
    {
        var priorExpiry = DateTime.UtcNow.AddDays(45);
        var (dbOptions, daemon, gateway, priorSessionId) = SetupRecreateScenario(priorExpiry);

        await daemon.StartAsync(CancellationToken.None);

        var active = await GetActivePersistentRowAsync(dbOptions, PrefillPlatform.Steam);
        Assert.NotNull(active);
        Assert.NotEqual(priorSessionId, active!.SessionId);                       // a FRESH row, not the reactivated old one
        Assert.Equal(priorExpiry, active.ExpiresAtUtc, TimeSpan.FromSeconds(5));   // inherited the future window
        Assert.Contains(gateway.Calls, c => c.StartsWith("Create:", StringComparison.Ordinal));

        daemon.Dispose();
    }

    [Fact]
    public async Task StartAsync_FullPersistenceRecreate_UsesNowPlusValidityWhenPriorExpiryPast()
    {
        var priorExpiry = DateTime.UtcNow.AddDays(-5); // already elapsed
        const int validityDays = 90;
        var (dbOptions, daemon, _, _) = SetupRecreateScenario(priorExpiry, validityDays);

        await daemon.StartAsync(CancellationToken.None);

        var active = await GetActivePersistentRowAsync(dbOptions, PrefillPlatform.Steam);
        Assert.NotNull(active);
        Assert.Equal(DateTime.UtcNow.AddDays(validityDays), active!.ExpiresAtUtc, TimeSpan.FromMinutes(2));

        daemon.Dispose();
    }

    // C-b (gate leg): driving the re-adopt directly with a STOPPED container present exercises the gate's
    // Recreate action (remove the dead target, then create), the branch cleanup would otherwise pre-empt.
    [Fact]
    public async Task ReadoptPersistentContainers_StoppedContainerPresent_RemovesTargetThenRecreates()
    {
        var (dbOptions, daemon, gateway, priorSessionId) = SetupRecreateScenario(DateTime.UtcNow.AddDays(30));
        var stopped = gateway.AddContainer(StoppedPersistentContainer(priorSessionId));

        // The prior row was seeded Active; the gate's shouldRecreate reads it as a non-Terminated life.
        await InvokePrivateAsync(daemon, "ReadoptPersistentContainersAsync", CancellationToken.None);

        Assert.Contains(gateway.Calls, c => c == $"Remove:{stopped.Id}");                     // dead target removed first
        Assert.Contains(gateway.Calls, c => c.StartsWith("Create:", StringComparison.Ordinal)); // then recreated
        Assert.NotNull(await GetActivePersistentRowAsync(dbOptions, PrefillPlatform.Steam));

        daemon.Dispose();
    }

    // ============================ C-c ============================
    // Negatives: startup must NOT fabricate a persistent container.
    [Fact]
    public async Task StartAsync_DisabledService_DoesNotRecreate()
    {
        await AssertNoRecreateAsync(Config(PersistenceMode.FullPersistence, steamEnabled: false), priorStatus: PrefillSessionStatus.Orphaned);
    }

    [Fact]
    public async Task StartAsync_TerminatedPriorRow_DoesNotRecreate()
    {
        await AssertNoRecreateAsync(Config(PersistenceMode.FullPersistence, steamEnabled: true), priorStatus: PrefillSessionStatus.Terminated);
    }

    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    public async Task StartAsync_NonFullPersistenceMode_DoesNotRecreateVanishedContainer(PersistenceMode mode)
    {
        await AssertNoRecreateAsync(Config(mode, steamEnabled: true), priorStatus: PrefillSessionStatus.Orphaned);
    }

    private async Task AssertNoRecreateAsync(ScheduledPrefillConfigDto config, PrefillSessionStatus priorStatus)
    {
        var (dbOptions, dbFactory) = NewDatabase();
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        await SeedPriorPersistentRowAsync(sessionService, priorStatus, expiresAt: DateTime.UtcNow.AddDays(30));

        var gateway = new RecordingContainerGateway(); // empty inventory: the container vanished during the outage
        var daemon = new TestSteamDaemon(MakeDeps(dbFactory, sessionService, config), gateway);

        await daemon.StartAsync(CancellationToken.None);

        Assert.DoesNotContain(gateway.Calls, c => c.StartsWith("Create:", StringComparison.Ordinal));
        Assert.Null(await GetActivePersistentRowAsync(dbOptions, PrefillPlatform.Steam));

        daemon.Dispose();
    }

    // ============================ C-d ============================
    // A docker create failure mid-recreate is swallowed (startup never aborts) and the dead session's DB
    // row stays non-Terminated, so the NEXT startup's zero-container retry arm recreates successfully.
    [Fact]
    public async Task StartAsync_RecreateDockerCreateFailure_RetriesAndSucceedsOnNextStartup()
    {
        var (dbOptions, dbFactory) = NewDatabase();
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        await SeedPriorPersistentRowAsync(sessionService, PrefillSessionStatus.Orphaned, expiresAt: DateTime.UtcNow.AddDays(30));

        var gateway = new RecordingContainerGateway();
        var config = Config(PersistenceMode.FullPersistence, steamEnabled: true);

        // First startup: the create fails; recreate swallows it, no Active row appears.
        var first = new TestSteamDaemon(MakeDeps(dbFactory, sessionService, config), gateway);
        gateway.FailNextCreateContainer(new InvalidOperationException("simulated docker create failure"));
        await first.StartAsync(CancellationToken.None);
        first.Dispose();

        Assert.Contains(gateway.Calls, c => c.StartsWith("Create:", StringComparison.Ordinal));
        Assert.Null(await GetActivePersistentRowAsync(dbOptions, PrefillPlatform.Steam));

        // Second startup (retry arm): create now succeeds and the recreate produces an Active row.
        var second = new TestSteamDaemon(MakeDeps(dbFactory, sessionService, config), gateway);
        await second.StartAsync(CancellationToken.None);

        Assert.NotNull(await GetActivePersistentRowAsync(dbOptions, PrefillPlatform.Steam));

        second.Dispose();
    }

    public void Dispose()
    {
        foreach (var root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }

    // ==================================================================================================
    // Helpers
    // ==================================================================================================

    private static (DbContextOptions<AppDbContext> Options, IDbContextFactory<AppDbContext> Factory) NewDatabase()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"prefill_orchestration_{Guid.NewGuid():N}")
            .Options;
        return (options, new PooledDbFactory(options));
    }

    private static async Task<PrefillSession?> GetRowAsync(DbContextOptions<AppDbContext> options, string sessionId)
    {
        await using var context = new AppDbContext(options);
        return await context.PrefillSessions.AsNoTracking().FirstOrDefaultAsync(s => s.SessionId == sessionId);
    }

    private static async Task<PrefillSession?> GetActivePersistentRowAsync(DbContextOptions<AppDbContext> options, PrefillPlatform platform)
    {
        await using var context = new AppDbContext(options);
        return await context.PrefillSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Platform == platform && s.IsPersistent && s.Status == PrefillSessionStatus.Active);
    }

    private static async Task<string> SeedActivePersistentRowAsync(
        PrefillSessionService sessionService, DateTime expiresAt, PrefillPlatform platform = PrefillPlatform.Steam, string containerName = SteamPersistentContainerName)
    {
        var sessionId = Guid.NewGuid().ToString("N")[..16];
        await sessionService.CreateSessionAsync(
            sessionId, ScheduledPrefillConstants.DeriveSystemUserId(), $"container-{sessionId}", containerName, expiresAt, platform.ToString());
        return sessionId;
    }

    private static async Task SeedPriorPersistentRowAsync(PrefillSessionService sessionService, PrefillSessionStatus status, DateTime expiresAt)
    {
        var priorId = await SeedActivePersistentRowAsync(sessionService, expiresAt);
        switch (status)
        {
            case PrefillSessionStatus.Terminated:
                await sessionService.TerminateSessionAsync(priorId, "admin stop");
                break;
            case PrefillSessionStatus.Orphaned:
                await sessionService.MarkOrphansAsync(PrefillPlatform.Steam);
                break;
        }
    }

    private static FakeContainer RunningPersistentContainer(string sessionId, string containerName = SteamPersistentContainerName, PrefillPlatform platform = PrefillPlatform.Steam)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = containerName,
            Running = true,
            Labels =
            {
                ["lancache.prefill.persistent"] = "true",
                ["lancache.prefill.service"] = platform.ToString(),
                ["lancache.prefill.sessionId"] = sessionId,
                ["lancache.prefill.userId"] = ScheduledPrefillConstants.DeriveSystemUserId().ToString()
            },
            Env = { "PREFILL_SOCKET_SECRET=deadbeefsecret", "PREFILL_USE_SOCKET=true" }
        };

    private static FakeContainer StoppedPersistentContainer(string sessionId)
    {
        var c = RunningPersistentContainer(sessionId);
        c.Running = false;
        return c;
    }

    // Builds the shared per-recreate-test scenario: FullPersistence + Steam enabled, one prior persistent
    // Active row (with the given expiry) that cleanup will orphan, and an empty container inventory.
    private (DbContextOptions<AppDbContext> Options, TestSteamDaemon Daemon, RecordingContainerGateway Gateway, string PriorSessionId)
        SetupRecreateScenario(DateTime priorExpiry, int validityDays = 90)
    {
        var (options, dbFactory) = NewDatabase();
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        var priorSessionId = SeedActivePersistentRowAsync(sessionService, priorExpiry).GetAwaiter().GetResult();

        var gateway = new RecordingContainerGateway();
        var config = Config(PersistenceMode.FullPersistence, steamEnabled: true);
        var daemon = new TestSteamDaemon(MakeDeps(dbFactory, sessionService, config, validityDays), gateway);
        return (options, daemon, gateway, priorSessionId);
    }

    private static PrefillDaemonServiceBase BuildDaemon(PrefillPlatform platform, DaemonDeps deps, IPrefillContainerGateway gateway)
        => platform switch
        {
            PrefillPlatform.Steam => new TestSteamDaemon(deps, gateway),
            PrefillPlatform.Epic => new TestEpicDaemon(deps, gateway),
            PrefillPlatform.Xbox => new TestXboxDaemon(deps, gateway),
            PrefillPlatform.BattleNet => new TestBattleNetDaemon(deps, gateway),
            PrefillPlatform.Riot => new TestRiotDaemon(deps, gateway),
            _ => throw new ArgumentOutOfRangeException(nameof(platform))
        };

    private DaemonDeps MakeDeps(
        IDbContextFactory<AppDbContext> dbFactory, PrefillSessionService sessionService, ScheduledPrefillConfigDto config, int validityDays = 90)
    {
        var notifications = FakeInterface<ISignalRNotificationService>();
        var stateService = OrchestrationStateService.New(config, validityDays);
        var pathResolver = PathResolverProxy.New(NewTempRoot());
        var cacheService = new PrefillCacheService(dbFactory, NullLogger<PrefillCacheService>.Instance);
        // A non-"auto" NetworkMode keeps the create path from probing Docker for lancache-dns.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Prefill:NetworkMode"] = "bridge" })
            .Build();
        var networkOptions = new StaticOptionsMonitor(new PrefillNetworkOptions { NetworkMode = "bridge" });
        return new DaemonDeps(notifications, configuration, pathResolver, stateService, sessionService, cacheService, networkOptions, new TestLancacheServerLocator());
    }

    private static ScheduledPrefillConfigDto Config(PersistenceMode global, bool steamEnabled)
    {
        var b = ScheduledPrefillConfigFactory.CreateDefault();
        var steam = new ScheduledPrefillServiceConfigDto
        {
            ServiceId = b.Steam.ServiceId,
            Enabled = steamEnabled,
            ShowNotification = b.Steam.ShowNotification,
            IntervalHours = b.Steam.IntervalHours,
            Preset = b.Steam.Preset,
            TopCount = b.Steam.TopCount,
            SelectedAppIds = b.Steam.SelectedAppIds,
            OperatingSystems = b.Steam.OperatingSystems,
            Force = b.Steam.Force,
            MaxConcurrency = b.Steam.MaxConcurrency,
            PersistenceMode = null
        };
        return new ScheduledPrefillConfigDto
        {
            Version = b.Version,
            MaxServiceRuntime = b.MaxServiceRuntime,
            StallTimeout = b.StallTimeout,
            Steam = steam,
            Epic = b.Epic,
            Xbox = b.Xbox,
            BattleNet = b.BattleNet,
            Riot = b.Riot,
            PersistenceMode = global
        };
    }

    private static ConcurrentDictionary<string, DaemonSession> SessionsOf(PrefillDaemonServiceBase daemon)
        => (ConcurrentDictionary<string, DaemonSession>)typeof(PrefillDaemonServiceBase)
            .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(daemon)!;

    private static async Task InvokePrivateAsync(PrefillDaemonServiceBase daemon, string methodName, params object[] args)
    {
        var method = typeof(PrefillDaemonServiceBase).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{methodName} not found on {nameof(PrefillDaemonServiceBase)}");
        await (Task)method.Invoke(daemon, args)!;
    }

    private static T FakeInterface<T>() where T : class
        => (T)DispatchProxy.Create<T, NullReturningProxy>();

    private string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "lancache-orch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempRoots.Add(root);
        return root;
    }

    // ---------- Test doubles ----------

    private sealed record DaemonDeps(
        ISignalRNotificationService Notifications,
        IConfiguration Configuration,
        IPathResolver PathResolver,
        IStateService StateService,
        PrefillSessionService SessionService,
        PrefillCacheService CacheService,
        IOptionsMonitor<PrefillNetworkOptions> NetworkOptions,
        ILancacheServerLocator Locator);

    private sealed class TestSteamDaemon : SteamDaemonService
    {
        public TestSteamDaemon(DaemonDeps d, IPrefillContainerGateway g)
            : base(NullLogger<SteamDaemonService>.Instance, d.Notifications, d.Configuration, d.PathResolver, d.StateService, d.SessionService, d.CacheService, d.NetworkOptions, d.Locator, new SingleContainerGatewayFactory(g)) { }
        protected override IDaemonClient CreateDaemonClient(bool useTcpMode, int? tcpHostPort, string socketPath, string socketSecret)
            => new FakeReconnectDaemonClient();
    }

    // The Epic/Xbox mapping service is a game-catalog dependency the startup re-adopt path never touches
    // (the fake reconnect client reports null live status, so no auth hooks that would use it ever fire),
    // so it is passed null here rather than standing up its heavy dependency graph.
    private sealed class TestEpicDaemon : EpicPrefillDaemonService
    {
        public TestEpicDaemon(DaemonDeps d, IPrefillContainerGateway g)
            : base(NullLogger<EpicPrefillDaemonService>.Instance, d.Notifications, d.Configuration, d.PathResolver, d.StateService, d.SessionService, d.CacheService, null!, d.NetworkOptions, d.Locator, new SingleContainerGatewayFactory(g)) { }
        protected override IDaemonClient CreateDaemonClient(bool useTcpMode, int? tcpHostPort, string socketPath, string socketSecret)
            => new FakeReconnectDaemonClient();
    }

    private sealed class TestXboxDaemon : XboxPrefillDaemonService
    {
        public TestXboxDaemon(DaemonDeps d, IPrefillContainerGateway g)
            : base(NullLogger<XboxPrefillDaemonService>.Instance, d.Notifications, d.Configuration, d.PathResolver, d.StateService, d.SessionService, d.CacheService, null!, d.NetworkOptions, d.Locator, new SingleContainerGatewayFactory(g)) { }
        protected override IDaemonClient CreateDaemonClient(bool useTcpMode, int? tcpHostPort, string socketPath, string socketSecret)
            => new FakeReconnectDaemonClient();
    }

    private sealed class TestBattleNetDaemon : BattleNetDaemonService
    {
        public TestBattleNetDaemon(DaemonDeps d, IPrefillContainerGateway g)
            : base(NullLogger<BattleNetDaemonService>.Instance, d.Notifications, d.Configuration, d.PathResolver, d.StateService, d.SessionService, d.CacheService, d.NetworkOptions, d.Locator, new SingleContainerGatewayFactory(g)) { }
        protected override IDaemonClient CreateDaemonClient(bool useTcpMode, int? tcpHostPort, string socketPath, string socketSecret)
            => new FakeReconnectDaemonClient();
    }

    private sealed class TestRiotDaemon : RiotDaemonService
    {
        public TestRiotDaemon(DaemonDeps d, IPrefillContainerGateway g)
            : base(NullLogger<RiotDaemonService>.Instance, d.Notifications, d.Configuration, d.PathResolver, d.StateService, d.SessionService, d.CacheService, d.NetworkOptions, d.Locator, new SingleContainerGatewayFactory(g)) { }
        protected override IDaemonClient CreateDaemonClient(bool useTcpMode, int? tcpHostPort, string socketPath, string socketSecret)
            => new FakeReconnectDaemonClient();
    }

    private sealed class PooledDbFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public PooledDbFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppDbContext(_options));
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<PrefillNetworkOptions>
    {
        public StaticOptionsMonitor(PrefillNetworkOptions value) => CurrentValue = value;
        public PrefillNetworkOptions CurrentValue { get; }
        public PrefillNetworkOptions Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<PrefillNetworkOptions, string?> listener) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    // IStateService stub returning a configurable scheduled-prefill config + persistent validity window,
    // and default/null for every other member (mirrors the DispatchProxy pattern in the sibling tests).
    private class OrchestrationStateService : DispatchProxy
    {
        private ScheduledPrefillConfigDto? _config;
        private int _validityDays;

        public static IStateService New(ScheduledPrefillConfigDto config, int validityDays)
        {
            var proxy = (IStateService)Create<IStateService, OrchestrationStateService>();
            var self = (OrchestrationStateService)(object)proxy;
            self._config = config;
            self._validityDays = validityDays;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IStateService.GetScheduledPrefillConfig))
            {
                return _config;
            }

            if (targetMethod?.Name == nameof(IStateService.GetAdminPersistentLoginValidityDays))
            {
                return _validityDays;
            }

            return DefaultReturn(targetMethod?.ReturnType);
        }

        internal static object? DefaultReturn(Type? returnType)
        {
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
    }

    private class NullReturningProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => OrchestrationStateService.DefaultReturn(targetMethod?.ReturnType);
    }

    // IPathResolver stub returning temp-dir-rooted paths, so the create path can make real bind-mount dirs.
    private class PathResolverProxy : DispatchProxy
    {
        private string _root = string.Empty;

        public static IPathResolver New(string root)
        {
            var proxy = (IPathResolver)Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)proxy)._root = root;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IPathResolver.ResolvePath):
                {
                    var relative = (string)args![0]!;
                    return Path.IsPathRooted(relative) ? relative : Path.Combine(_root, relative);
                }
                case nameof(IPathResolver.NormalizePath):
                    return (string)args![0]!;
            }

            if (targetMethod?.ReturnType == typeof(string))
            {
                return _root;
            }

            return OrchestrationStateService.DefaultReturn(targetMethod?.ReturnType);
        }
    }
}
