using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// Session 20260703-105848-2820911866, Worker B: proves
/// <see cref="PrefillDaemonServiceBase.LogoutPersistentSessionAsync(string, CancellationToken)"/>,
/// which lets the "Log out" button forget a persistent container's stored account IN PLACE (no
/// container restart) when the daemon supports the <c>logout</c> command, instead of the previous
/// stop+restart flow that never actually cleared the account (its named auth volume survives a
/// restart). A daemon reporting failure (older image without the command) or an exception from the
/// round-trip must both be treated identically as "not supported", with NO auth-state teardown
/// performed here - the caller (<see cref="Controllers.PersistentPrefillController"/> / the frontend)
/// decides whether to fall back to the old stop+restart path. The pending login challenge IS cleared
/// unconditionally, before the daemon round-trip even starts (mirrors <c>CancelLoginAsync</c>'s
/// ordering) - logout intent is terminal, so unlike cancel, a failed round-trip does not restore it.
/// </summary>
public class PersistentLogoutTests
{
    [Fact]
    public async Task LogoutPersistentSessionAsync_DaemonSucceeds_ClearsAuthStateAndChallenge_ReturnsLoggedOutTrue()
    {
        var client = new ScriptedLogoutDaemonClient(succeeds: true);
        var (daemon, session) = CreateSessionWithClient(client);
        session.AuthState = DaemonAuthState.Authenticated;
        session.NeedsRelogin = true;
        session.PendingLoginChallenge = new CredentialChallenge { ChallengeId = "stale-chal", CredentialType = "username" };

        var result = await daemon.LogoutPersistentSessionAsync(session.Id, CancellationToken.None);

        Assert.True(result.LoggedOut);
        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);
        Assert.False(session.NeedsRelogin);
        Assert.Null(session.PendingLoginChallenge);
        Assert.Equal(1, client.LogoutCallCount);
    }

    [Fact]
    public async Task LogoutPersistentSessionAsync_DaemonReportsFailure_ReturnsLoggedOutFalse_NoTeardown()
    {
        var client = new ScriptedLogoutDaemonClient(succeeds: false);
        var (daemon, session) = CreateSessionWithClient(client);
        session.AuthState = DaemonAuthState.Authenticated;
        var pending = new CredentialChallenge { ChallengeId = "still-pending", CredentialType = "username" };
        session.PendingLoginChallenge = pending;

        var result = await daemon.LogoutPersistentSessionAsync(session.Id, CancellationToken.None);

        Assert.False(result.LoggedOut);
        // Not-supported must never touch auth state - the caller's stop+restart fallback (or a later
        // resume) needs to see it exactly as it was. The pending challenge, however, is cleared
        // unconditionally before the daemon round-trip even starts (mirrors CancelLoginAsync's
        // ordering) and is NOT restored on failure - logout intent is terminal.
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
        Assert.Null(session.PendingLoginChallenge);
    }

    [Fact]
    public async Task LogoutPersistentSessionAsync_DaemonThrows_TreatedSameAsNotSupported_NoTeardown()
    {
        var client = new ThrowingLogoutDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);
        session.AuthState = DaemonAuthState.Authenticated;
        var pending = new CredentialChallenge { ChallengeId = "still-pending", CredentialType = "username" };
        session.PendingLoginChallenge = pending;

        var result = await daemon.LogoutPersistentSessionAsync(session.Id, CancellationToken.None);

        Assert.False(result.LoggedOut);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
        Assert.Null(session.PendingLoginChallenge);
    }

    [Fact]
    public async Task LogoutPersistentSessionAsync_ClearsChallengeAndPendingWaitBeforeDaemonRoundTrip()
    {
        // Proves the CancelLoginAsync-style ordering: the challenge cache and the daemon client's own
        // pending-wait are both cleared BEFORE the (possibly slow) daemon round-trip, not after - a
        // concurrent resume/poll landing during that await must never be able to serve a challenge
        // that a logout is in the middle of tearing down.
        var client = new OrderRecordingLogoutDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);
        client.Session = session;
        session.AuthState = DaemonAuthState.Authenticated;
        session.PendingLoginChallenge = new CredentialChallenge { ChallengeId = "stale-chal", CredentialType = "username" };

        var result = await daemon.LogoutPersistentSessionAsync(session.Id, CancellationToken.None);

        Assert.True(result.LoggedOut);
        Assert.Equal(new[] { "ClearPendingChallenges", "LogoutAsync" }, client.CallOrder);
        Assert.True(client.ChallengeWasAlreadyNullDuringLogoutCall);
    }

    [Fact]
    public async Task LogoutPersistentSessionAsync_NonPersistentSession_ReturnsLoggedOutFalse_NeverCallsDaemon()
    {
        var client = new ScriptedLogoutDaemonClient(succeeds: true);
        var (daemon, session) = CreateSessionWithClient(client, isPersistent: false);
        session.AuthState = DaemonAuthState.Authenticated;

        var result = await daemon.LogoutPersistentSessionAsync(session.Id, CancellationToken.None);

        Assert.False(result.LoggedOut);
        Assert.Equal(0, client.LogoutCallCount);
        // Defense-in-depth guard must short-circuit before touching auth state at all.
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
    }

    [Fact]
    public async Task LogoutPersistentSessionAsync_UnknownSessionId_ThrowsKeyNotFound()
    {
        var client = new ScriptedLogoutDaemonClient(succeeds: true);
        var (daemon, _) = CreateSessionWithClient(client);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => daemon.LogoutPersistentSessionAsync("no-such-session", CancellationToken.None));
    }

    [Fact]
    public async Task LogoutPersistentSessionAsync_DaemonRejectsPreLoginLogout_ReturnsLoggedOutFalse_NoTeardown_DistinctLog()
    {
        // Older daemon image: its pre-login command gate rejects "logout" outright while the session
        // hasn't finished authenticating (erase-on-stop regression diagnosis). Must still return
        // forgotten=false (the frontend routes this to cancelling the in-flight login instead of a
        // stop+restart), but the log line must say so distinctly rather than reading as a genuine
        // daemon failure every single time an admin cancels a mid-challenge login.
        var client = new PreLoginRejectionDaemonClient();
        var logger = new CapturingLogger();
        var (daemon, session) = CreateSessionWithClient(client, logger: logger);
        session.AuthState = DaemonAuthState.Authenticated;
        var pending = new CredentialChallenge { ChallengeId = "still-pending", CredentialType = "username" };
        session.PendingLoginChallenge = pending;

        var result = await daemon.LogoutPersistentSessionAsync(session.Id, CancellationToken.None);

        Assert.False(result.LoggedOut);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);
        Assert.Null(session.PendingLoginChallenge);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Information &&
            entry.Message.Contains("before authentication completed"));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("Daemon reported logout failed"));
    }

    private static (PrefillDaemonServiceBase Daemon, DaemonSession Session) CreateSessionWithClient(
        IDaemonClient client, bool isPersistent = true, ILogger<SteamDaemonService>? logger = null)
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"persistent_logout_{Guid.NewGuid():N}")
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
            logger ?? NullLogger<SteamDaemonService>.Instance, notifications, configuration, pathResolver,
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
    // seam in PersistentLoginChallengeResumeTests.cs / PersistentLoginFailFastTests.cs.
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
    /// paths (mirrors NullReturningProxy in PersistentLoginChallengeResumeTests.cs /
    /// PersistentLoginFailFastTests.cs).
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
    /// Fake <see cref="IDaemonClient"/> exposing only the surface a persistent-logout scenario
    /// touches; every other member throws <see cref="NotSupportedException"/> so an unexpected call
    /// fails loudly (mirrors TestDaemonClientBase in the sibling persistent-login test files).
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
            => throw new NotSupportedException("Unexpected StartLoginAsync in a logout scenario.");

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

        // Explicit (virtual, not DIM-inherited) so a subclass can override it the normal OOP way to
        // simulate the daemon's RequiresLogin signal - mirrors TestDaemonClientBase in
        // PersistentEraseOnStopTests.cs. Fakes that don't override this just adapt whatever
        // LogoutAsync returns, matching IDaemonClient's own default implementation.
        public virtual async Task<LogoutOutcome> LogoutWithReasonAsync(CancellationToken cancellationToken = default)
        {
            var success = await LogoutAsync(cancellationToken);
            return new LogoutOutcome(success, RequiresLogin: false);
        }

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

        public virtual void ClearPendingChallenges() { }

        public void Dispose() { }
    }

    /// <summary>
    /// Scenario: the daemon responds to the <c>logout</c> command without a transport error, either
    /// succeeding (new image) or reporting failure (e.g. unknown-command handling that resolves to a
    /// failed <c>CommandResponse</c> instead of throwing).
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
    /// Scenario: the daemon round-trip itself fails (socket error / unresponsive daemon) rather than
    /// returning a normal failed response - must be treated identically to a scripted failure.
    /// </summary>
    private sealed class ThrowingLogoutDaemonClient : TestDaemonClientBase
    {
        public override Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Simulated daemon logout round-trip failure.");
    }

    /// <summary>
    /// Scenario: an older daemon image's pre-login command gate rejects "logout" outright because the
    /// session hasn't finished authenticating - a real daemon response (not a transport error), just
    /// one that carries <c>RequiresLogin: true</c> alongside <c>Success: false</c>.
    /// </summary>
    private sealed class PreLoginRejectionDaemonClient : TestDaemonClientBase
    {
        public override Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public override Task<LogoutOutcome> LogoutWithReasonAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new LogoutOutcome(false, RequiresLogin: true));
    }

    /// <summary>
    /// Records the order in which <see cref="ClearPendingChallenges"/> and <see cref="LogoutAsync"/>
    /// are invoked, and snapshots whether the session's cached <c>PendingLoginChallenge</c> was already
    /// cleared by the time the daemon round-trip starts - proves the clear-before-round-trip ordering
    /// rather than just its end state.
    /// </summary>
    private sealed class OrderRecordingLogoutDaemonClient : TestDaemonClientBase
    {
        public List<string> CallOrder { get; } = new();
        public DaemonSession? Session { get; set; }
        public bool ChallengeWasAlreadyNullDuringLogoutCall { get; private set; }

        public override void ClearPendingChallenges()
        {
            CallOrder.Add(nameof(ClearPendingChallenges));
        }

        public override Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
        {
            CallOrder.Add(nameof(LogoutAsync));
            ChallengeWasAlreadyNullDuringLogoutCall = Session?.PendingLoginChallenge is null;
            return Task.FromResult(true);
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    /// <summary>Captures log entries so a test can assert on the exact text a scenario produces -
    /// mirrors ScheduledPrefillServiceTests.cs's CapturingLogger.</summary>
    private sealed class CapturingLogger : ILogger<SteamDaemonService>
    {
        private readonly object _sync = new();
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get { lock (_sync) return _entries.ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_sync)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
            }
        }
    }
}
