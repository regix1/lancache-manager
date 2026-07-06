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
/// Session 20260703-005211-295269021, Worker 5: proves <c>PrefillDaemonServiceBase.StartLoginAsync</c>
/// races the daemon's "Login failed: &lt;reason&gt;" status broadcast (<c>OnStatusUpdate</c>, the shape
/// every daemon repo uses: <c>BroadcastStatusAsync("awaiting-login", $"Login failed: {ex.Message}")</c>)
/// against the blind challenge waits (diagnostic.md census sites 42-45: the 30s controller wait plus the
/// 10s <c>WaitForChallengeAsync</c> fallback), instead of sitting through them.
///
/// PRE-FIX BEHAVIOR (verified by temporarily reverting the fix and re-running
/// <see cref="StartLoginAsync_DaemonBroadcastsLoginFailure_ReturnsFastWithRealErrorText"/>): the fake
/// daemon's <c>StartLoginAsync</c> blocks for <see cref="FailFastLoginDaemonClient.BlindWaitDelay"/>
/// (5s) before the manager's old code ever looks at the failure broadcast, so the test failed on the
/// elapsed-time assertion (observed ~5s, generic null result, no captured failure text) - the same
/// shape as the pre-fix "Login timeout - daemon may not be ready" symptom. With the fix, the race
/// short-circuits within ~50-100ms.
/// </summary>
public class PersistentLoginFailFastTests
{
    [Fact]
    public async Task StartLoginAsync_DaemonBroadcastsLoginFailure_ReturnsFastWithRealErrorText()
    {
        const string failureMessage = "Login failed: The computed authentication tag did not match the input tag.";
        var (daemon, session) = CreateSessionWithClient(new FailFastLoginDaemonClient(failureMessage));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var challenge = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        stopwatch.Stop();

        Assert.Null(challenge);
        Assert.Equal(failureMessage, session.LastLoginFailureMessage);
        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);

        // Proves early-completion via the race, not timeout-fallthrough: the fake's own blind-wait
        // task is 5 seconds; observing well under that (and under the 10s WaitForChallengeAsync this
        // scenario must never reach - the fake throws if it's called) proves the failure broadcast won.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Expected the fail-fast race to win in well under 2s, took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task StartLoginAsync_ChallengeArrivesNormally_NoRegressionFromRaceWiring()
    {
        var (daemon, session) = CreateSessionWithClient(new ChallengeArrivesDaemonClient());

        var challenge = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.NotNull(challenge);
        Assert.Equal(ChallengeArrivesDaemonClient.Challenge.ChallengeId, challenge!.ChallengeId);
        Assert.Null(session.LastLoginFailureMessage);
    }

    [Fact]
    public async Task StartLoginAsync_DaemonGenuinelySilent_PreservesNullResultAndNoFailureText()
    {
        var (daemon, session) = CreateSessionWithClient(new SilentLoginDaemonClient());

        var challenge = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Null(challenge);
        // No failure was ever observed, so the controller must fall back to the generic timeout text.
        Assert.Null(session.LastLoginFailureMessage);
    }

    /// <summary>
    /// Session 20260703-005211-295269021 verifier, Cursor #3: a second <c>StartLoginAsync</c> call on
    /// the same session while the first is still mid-flight must be rejected outright (try-acquire on
    /// <see cref="DaemonSession.LoginLock"/>), not silently queued behind it - overlapping calls would
    /// otherwise race the daemon's single challenge/status stream.
    /// </summary>
    [Fact]
    public async Task StartLoginAsync_ConcurrentCallOnSameSession_RejectedWhileFirstInFlight()
    {
        var blockingClient = new BlockingLoginDaemonClient();
        var (daemon, session) = CreateSessionWithClient(blockingClient);

        var firstCall = daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        await blockingClient.EnteredStartLogin.Task;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None));

        blockingClient.ReleaseGate.TrySetResult(null);
        await firstCall;
    }

    private static (PrefillDaemonServiceBase Daemon, DaemonSession Session) CreateSessionWithClient(IDaemonClient client)
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"login_fail_fast_{Guid.NewGuid():N}")
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
            IsPersistent = true,
            AuthState = DaemonAuthState.NotAuthenticated,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            Client = client
        };
        daemon.InjectSession(session);

        return (daemon, session);
    }

    // Test-only seam: _sessions is `protected` on PrefillDaemonServiceBase so production code never
    // exposes a way to inject a session without going through real Docker container creation. Mirrors
    // TestableBattleNetDaemonService/TestableRiotDaemonService in ScheduledPrefillAnonymousRunPathTests.cs.
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
            : base(logger, notifications, configuration, pathResolver, stateService, sessionService, cacheService, networkOptions, new TestLancacheServerLocator())
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
    /// paths (mirrors NullReturningProxy in ScheduledPrefillAnonymousRunPathTests.cs).
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
    /// Base fake <see cref="IDaemonClient"/> exposing the same event surface as the real transports;
    /// every member outside a scenario's login-flow scope throws <see cref="NotSupportedException"/> so
    /// an unexpected call fails loudly (mirrors FakeAnonymousDaemonClient in
    /// ScheduledPrefillAnonymousRunPathTests.cs). Scenario subclasses override only the members their
    /// happy path touches.
    /// </summary>
    private abstract class TestDaemonClientBase : IDaemonClient
    {
        public event Func<CredentialChallenge, Task>? OnCredentialChallenge { add { } remove { } }
        public event Func<DaemonStatus, Task>? OnStatusUpdate;
        public event Func<SocketPrefillProgress, Task>? OnProgressUpdate { add { } remove { } }
        public event Func<string, Task>? OnError { add { } remove { } }
        public event Func<Task>? OnDisconnected { add { } remove { } }

        /// <summary>The invoker the test scenarios use to raise the daemon's status broadcast.</summary>
        protected Task RaiseStatusUpdateAsync(DaemonStatus status)
            => OnStatusUpdate?.Invoke(status) ?? Task.CompletedTask;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public virtual Task<DaemonStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Unexpected GetStatusAsync in this scenario.");

        public Task<CommandResponse> SendCommandAsync(
            string type, Dictionary<string, string>? parameters = null, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException($"Unexpected SendCommandAsync({type}) in this test.");

        public abstract Task<CredentialChallenge?> StartLoginAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default);

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

        public virtual Task<CredentialChallenge?> WaitForChallengeAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Unexpected WaitForChallengeAsync in this scenario.");

        public Task CancelLoginAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Unexpected LogoutAsync in this scenario.");

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

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void ClearPendingChallenges() { }

        public void Dispose() { }
    }

    /// <summary>
    /// Scenario: the daemon broadcasts "Login failed: X" ~50ms after <c>StartLoginAsync</c> is called,
    /// while the ack task itself deliberately blocks for <see cref="BlindWaitDelay"/> (simulating the
    /// daemon giving no ack at all) - the exact pre-fix shape from diagnostic.md census sites 42-45.
    /// <c>WaitForChallengeAsync</c>/<c>GetStatusAsync</c> are left throwing (inherited) so this also
    /// proves the fail-fast path never reaches the second 10s wait or the status-fallback calls.
    /// </summary>
    private sealed class FailFastLoginDaemonClient : TestDaemonClientBase
    {
        public static readonly TimeSpan BlindWaitDelay = TimeSpan.FromSeconds(5);
        private readonly string _failureMessage;

        public FailFastLoginDaemonClient(string failureMessage)
        {
            _failureMessage = failureMessage;
        }

        public override async Task<CredentialChallenge?> StartLoginAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            _ = BroadcastLoginFailureShortlyAsync();
            await Task.Delay(BlindWaitDelay, CancellationToken.None);
            return null;
        }

        private async Task BroadcastLoginFailureShortlyAsync()
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            await RaiseStatusUpdateAsync(new DaemonStatus { Status = "awaiting-login", Message = _failureMessage });
        }
    }

    /// <summary>
    /// Scenario: the daemon returns a real challenge promptly and never broadcasts a failure - proves
    /// the fail-fast race wiring adds no regression to the ordinary success path.
    /// </summary>
    private sealed class ChallengeArrivesDaemonClient : TestDaemonClientBase
    {
        public static readonly CredentialChallenge Challenge = new()
        {
            ChallengeId = "chal-1",
            CredentialType = "username"
        };

        public override Task<CredentialChallenge?> StartLoginAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => Task.FromResult<CredentialChallenge?>(Challenge);
    }

    /// <summary>
    /// Scenario: the daemon never broadcasts anything and never produces a challenge (genuinely
    /// silent) - proves the generic "Login timeout" fallback (session.LastLoginFailureMessage staying
    /// null) is preserved for the case nothing was ever heard from the daemon.
    /// </summary>
    private sealed class SilentLoginDaemonClient : TestDaemonClientBase
    {
        public override async Task<CredentialChallenge?> StartLoginAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), CancellationToken.None);
            return null;
        }

        public override async Task<CredentialChallenge?> WaitForChallengeAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), CancellationToken.None);
            return null;
        }

        public override Task<DaemonStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<DaemonStatus?>(new DaemonStatus { Status = "awaiting-login" });
    }

    /// <summary>
    /// Scenario: <c>StartLoginAsync</c> blocks until the test releases it, letting a test hold one
    /// login call "in flight" while asserting a second concurrent call is rejected.
    /// </summary>
    private sealed class BlockingLoginDaemonClient : TestDaemonClientBase
    {
        public readonly TaskCompletionSource<object?> EnteredStartLogin = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<CredentialChallenge?> ReleaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<CredentialChallenge?> StartLoginAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            EnteredStartLogin.TrySetResult(null);
            return await ReleaseGate.Task;
        }

        // Once released, StartLoginAsync returns null (no challenge) and StartLoginCoreAsync falls
        // through to these - mirror SilentLoginDaemonClient's shape so the first call completes
        // normally instead of hitting the base class's NotSupportedException.
        public override Task<CredentialChallenge?> WaitForChallengeAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => Task.FromResult<CredentialChallenge?>(null);

        public override Task<DaemonStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<DaemonStatus?>(new DaemonStatus { Status = "awaiting-login" });
    }
}
