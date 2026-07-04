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
/// Erase-on-stop backend: proves the two best-effort logout guards added to
/// <see cref="PrefillDaemonServiceBase"/> so a persistent container's login never outlives the
/// container per the user's "login exists ONLY while the container is alive" policy.
/// (1) <see cref="PrefillDaemonServiceBase.TerminateSessionAsync"/> sends a logout for a PERSISTENT
/// session BEFORE tearing its container down (covers explicit Stop, Error-state replacement, and
/// service shutdown - they all funnel through this one method), and never for a non-persistent
/// session. (2) <see cref="PrefillDaemonServiceBase.ApplyFreshPersistentLoginGuardAsync"/> is the
/// belt-and-braces guard called right after a persistent session's socket connects: it fires only for
/// a NEWLY CREATED persistent session (isReconnect:false), never for a re-adopted one
/// (isReconnect:true) or a non-persistent one - an adopted running container's login IS its current
/// life and must be preserved. Both failure paths (daemon reports failure / round-trip throws) must
/// be swallowed so a dead socket can never block teardown or startup.
/// Mirrors the exact fake-<see cref="IDaemonClient"/> + <c>TestableSteamDaemonService</c> harness in
/// the sibling PersistentLogoutTests.cs.
/// </summary>
public class PersistentEraseOnStopTests
{
    [Fact]
    public async Task TerminateSessionAsync_PersistentSession_SendsLogoutBeforeTeardown()
    {
        var client = new ScriptedLogoutDaemonClient(succeeds: true);
        var (daemon, session) = CreateSessionWithClient(client, isPersistent: true);

        await daemon.TerminateSessionAsync(session.Id, "test stop");

        Assert.Equal(1, client.LogoutCallCount);
        Assert.Equal(DaemonSessionStatus.Terminated, session.Status);
        Assert.Null(daemon.GetSession(session.Id));
    }

    [Fact]
    public async Task TerminateSessionAsync_NonPersistentSession_NeverSendsLogout()
    {
        var client = new ScriptedLogoutDaemonClient(succeeds: true);
        var (daemon, session) = CreateSessionWithClient(client, isPersistent: false);

        await daemon.TerminateSessionAsync(session.Id, "test stop");

        Assert.Equal(0, client.LogoutCallCount);
        Assert.Equal(DaemonSessionStatus.Terminated, session.Status);
    }

    [Fact]
    public async Task TerminateSessionAsync_PersistentSession_LogoutRoundTripThrows_TerminationStillCompletes()
    {
        var client = new ThrowingLogoutDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client, isPersistent: true);

        // Must not throw and must not block: a dead/hung socket cannot be allowed to prevent the stop.
        await daemon.TerminateSessionAsync(session.Id, "test stop");

        Assert.Equal(DaemonSessionStatus.Terminated, session.Status);
        Assert.Null(daemon.GetSession(session.Id));
    }

    [Fact]
    public async Task TerminateSessionAsync_PersistentSession_DaemonReportsLogoutFailure_TerminationStillCompletes()
    {
        var client = new ScriptedLogoutDaemonClient(succeeds: false);
        var (daemon, session) = CreateSessionWithClient(client, isPersistent: true);

        await daemon.TerminateSessionAsync(session.Id, "test stop");

        Assert.Equal(1, client.LogoutCallCount);
        Assert.Equal(DaemonSessionStatus.Terminated, session.Status);
    }

    [Fact]
    public async Task ApplyFreshPersistentLoginGuardAsync_FreshPersistentCreate_SendsLogout()
    {
        var client = new ScriptedLogoutDaemonClient(succeeds: true);
        var (daemon, session) = CreateSessionWithClient(client, isPersistent: true);

        await daemon.ApplyFreshPersistentLoginGuardAsync(session, isPersistent: true, isReconnect: false);

        Assert.Equal(1, client.LogoutCallCount);
    }

    [Fact]
    public async Task ApplyFreshPersistentLoginGuardAsync_ReAdoptedPersistentSession_NeverSendsLogout()
    {
        var client = new ScriptedLogoutDaemonClient(succeeds: true);
        var (daemon, session) = CreateSessionWithClient(client, isPersistent: true);

        await daemon.ApplyFreshPersistentLoginGuardAsync(session, isPersistent: true, isReconnect: true);

        Assert.Equal(0, client.LogoutCallCount);
    }

    [Fact]
    public async Task ApplyFreshPersistentLoginGuardAsync_NonPersistentCreate_NeverSendsLogout()
    {
        var client = new ScriptedLogoutDaemonClient(succeeds: true);
        var (daemon, session) = CreateSessionWithClient(client, isPersistent: false);

        await daemon.ApplyFreshPersistentLoginGuardAsync(session, isPersistent: false, isReconnect: false);

        Assert.Equal(0, client.LogoutCallCount);
    }

    [Fact]
    public async Task ApplyFreshPersistentLoginGuardAsync_LogoutThrows_NeverPropagates()
    {
        var client = new ThrowingLogoutDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client, isPersistent: true);

        // A dead socket on a fresh container must never block session creation from returning.
        await daemon.ApplyFreshPersistentLoginGuardAsync(session, isPersistent: true, isReconnect: false);
    }

    private static (PrefillDaemonServiceBase Daemon, DaemonSession Session) CreateSessionWithClient(
        IDaemonClient client, bool isPersistent)
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"persistent_erase_on_stop_{Guid.NewGuid():N}")
            .Options;
        var dbFactory = new InMemoryDbContextFactory(dbOptions);
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        var cacheService = new PrefillCacheService(dbFactory, NullLogger<PrefillCacheService>.Instance);
        var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
        var configuration = new ConfigurationBuilder().Build();
        var pathResolver = (IPathResolver)DispatchProxy.Create<IPathResolver, NullReturningProxy>();
        var stateService = (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>();
        var networkOptions = new StaticOptionsMonitor<PrefillNetworkOptions>(new PrefillNetworkOptions());

        var daemon = new TestableSteamDaemonService(
            NullLogger<SteamDaemonService>.Instance, notifications, configuration, pathResolver,
            stateService, sessionService, cacheService, networkOptions);

        var session = new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = ScheduledPrefillConstants.DeriveSystemUserId(),
            Status = DaemonSessionStatus.Active,
            IsPersistent = isPersistent,
            AuthState = DaemonAuthState.NotAuthenticated,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            Client = client
        };
        daemon.InjectSession(session);

        return (daemon, session);
    }

    // Test-only seam: _sessions is protected on PrefillDaemonServiceBase, so production code never
    // exposes a way to inject a session without a real daemon round-trip. Mirrors the InjectSession
    // seam in PersistentLogoutTests.cs / PersistentLoginChallengeResumeTests.cs.
    private sealed class TestableSteamDaemonService : SteamDaemonService
    {
        public TestableSteamDaemonService(
            Microsoft.Extensions.Logging.ILogger<SteamDaemonService> logger,
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
    /// Minimal do-nothing proxy for interfaces whose members are not exercised by these tests' happy
    /// paths (mirrors NullReturningProxy in PersistentLogoutTests.cs).
    /// </summary>
    private class NullReturningProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
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
    }

    /// <summary>
    /// Fake <see cref="IDaemonClient"/> exposing only the surface these scenarios touch; every other
    /// member throws <see cref="NotSupportedException"/> so an unexpected call fails loudly (mirrors
    /// TestDaemonClientBase in PersistentLogoutTests.cs).
    /// </summary>
    private abstract class TestDaemonClientBase : IDaemonClient
    {
        public event Func<CredentialChallenge, Task>? OnCredentialChallenge { add { } remove { } }
        public event Func<DaemonStatus, Task>? OnStatusUpdate { add { } remove { } }
        public event Func<SocketPrefillProgress, Task>? OnProgressUpdate { add { } remove { } }
        public event Func<string, Task>? OnError { add { } remove { } }
        public event Func<Task>? OnDisconnected { add { } remove { } }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<DaemonStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Unexpected GetStatusAsync in this scenario.");

        public Task<CommandResponse> SendCommandAsync(
            string type, Dictionary<string, string>? parameters = null, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException($"Unexpected SendCommandAsync({type}) in this test.");

        public Task<CredentialChallenge?> StartLoginAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Unexpected StartLoginAsync in an erase-on-stop scenario.");

        public Task ProvideCredentialAsync(CredentialChallenge challenge, string credential, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

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

        public abstract Task<bool> LogoutAsync(CancellationToken cancellationToken = default);

        public Task CancelPrefillAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<List<OwnedGame>> GetOwnedGamesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<List<CdnInfo>> GetCdnInfoAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SetSelectedAppsAsync(List<string> appIds, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PrefillResult> PrefillAsync(
            bool all = false, bool recent = false, bool recentlyPurchased = false, int? top = null,
            bool force = false, List<string>? operatingSystems = null, int? maxConcurrency = null,
            List<CachedDepotInput>? cachedDepots = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ClearCacheResult> ClearCacheAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ClearCacheResult> GetCacheInfoAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SelectedAppsStatus> GetSelectedAppsStatusAsync(List<string>? operatingSystems = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CacheStatusResult> CheckCacheStatusAsync(List<CachedDepotInput> cachedDepots, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        // TerminateSessionAsync calls ShutdownAsync in its graceful (non-force) branch, but only when
        // _dockerClient and session.ContainerId are both set - neither is true in this harness (no
        // Docker started, ContainerId left unset), so this is never actually invoked here. Completed
        // rather than throwing purely as a defensive default, matching TestDaemonClientBase's style.
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public virtual void ClearPendingChallenges() { }

        public void Dispose() { }
    }

    /// <summary>
    /// Scenario: the daemon responds to the <c>logout</c> command without a transport error, either
    /// succeeding or reporting failure - both must be swallowed by the best-effort guards under test.
    /// </summary>
    private sealed class ScriptedLogoutDaemonClient : TestDaemonClientBase
    {
        private readonly bool _succeeds;

        public ScriptedLogoutDaemonClient(bool succeeds)
        {
            _succeeds = succeeds;
        }

        public int LogoutCallCount { get; private set; }

        public override Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
        {
            LogoutCallCount++;
            return Task.FromResult(_succeeds);
        }
    }

    /// <summary>
    /// Scenario: the daemon round-trip itself throws (dead/hung socket) - must be swallowed, not
    /// propagated, by both best-effort guards under test.
    /// </summary>
    private sealed class ThrowingLogoutDaemonClient : TestDaemonClientBase
    {
        public override Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Simulated daemon logout round-trip failure.");
    }
}
