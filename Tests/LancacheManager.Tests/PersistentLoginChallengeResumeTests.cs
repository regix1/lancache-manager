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
/// Session 20260703-085455-996528703, Worker 1: proves the manager-side pending-login-challenge cache
/// on <see cref="DaemonSession.PendingLoginChallenge"/> that fixes the double-start bug from
/// diagnostic.md §5 - a second <c>StartLoginAsync</c> call arriving while a challenge is still pending
/// (e.g. the frontend closed/reopened the login modal) now resumes the SAME challenge instead of
/// issuing a second daemon <c>login</c> command, which the daemon answers "already in progress" without
/// re-emitting a challenge while the client destroys its own queued copy - the exact "No challenge
/// received" / 400 chain the diagnostic reproduced.
///
/// PRE-FIX BEHAVIOR (verified by temporarily reverting the resume check in
/// <c>PrefillDaemonServiceBase.StartLoginCoreAsync</c> and re-running
/// <see cref="StartLoginAsync_SecondCallWhilePending_ResumesSameChallengeWithoutDaemonCall"/>): the
/// second call falls through to the normal flow and invokes the daemon client's
/// <c>StartLoginAsync</c> a second time, which this test's fake client treats as a protocol violation
/// (throws) - mirroring the daemon's real "already in progress" reply racing against the client's own
/// queue-clearing.
/// </summary>
public class PersistentLoginChallengeResumeTests
{
    [Fact]
    public async Task StartLoginAsync_SecondCallWhilePending_ResumesSameChallengeWithoutDaemonCall()
    {
        var client = new SingleChallengeDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);

        var first = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal(SingleChallengeDaemonClient.Challenge.ChallengeId, first!.ChallengeId);
        Assert.Equal(1, client.StartLoginCallCount);

        var second = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.NotNull(second);
        Assert.Equal(SingleChallengeDaemonClient.Challenge.ChallengeId, second!.ChallengeId);
        Assert.Equal(1, client.StartLoginCallCount);
    }

    [Fact]
    public async Task CancelLoginAsync_ClearsPendingChallenge_NextStartIssuesFreshDaemonLogin()
    {
        var client = new MultiCallChallengeDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);

        var first = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal("chal-1", first!.ChallengeId);
        Assert.NotNull(session.PendingLoginChallenge);

        await daemon.CancelLoginAsync(session.Id, CancellationToken.None);

        Assert.Null(session.PendingLoginChallenge);

        var second = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal("chal-2", second!.ChallengeId);
        Assert.Equal(2, client.StartLoginCallCount);
    }

    /// <summary>
    /// Session 20260703-085455-996528703 post-review fix: a failed daemon-side cancel round-trip
    /// (socket error, unresponsive daemon) must NOT be treated as a successful cancel. Before this fix,
    /// <c>CancelLoginAsync</c> cleared <see cref="DaemonSession.PendingLoginChallenge"/> and flipped
    /// <see cref="DaemonSession.AuthState"/> to <see cref="DaemonAuthState.NotAuthenticated"/>
    /// unconditionally, even when the try/catch around the daemon round-trip caught an exception - so
    /// the next <c>StartLoginAsync</c> would issue a brand-new daemon login while the daemon might still
    /// believe the original attempt is in progress, reproducing the duplicate-login race the resume
    /// cache exists to prevent. The fix restores the captured challenge and rethrows on failure instead.
    /// </summary>
    [Fact]
    public async Task CancelLoginAsync_DaemonCancelFails_ChallengeStaysResumable_NextStartDoesNotIssueFreshDaemonLogin()
    {
        var client = new ThrowingCancelDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);

        var first = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal("chal-1", first!.ChallengeId);
        Assert.NotNull(session.PendingLoginChallenge);
        var authStateBeforeCancel = session.AuthState;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => daemon.CancelLoginAsync(session.Id, CancellationToken.None));

        // Failure must not be swallowed into a successful cancel: the cached challenge stays in place
        // and auth state is left untouched (not reset as if the cancel had actually gone through).
        Assert.NotNull(session.PendingLoginChallenge);
        Assert.Equal("chal-1", session.PendingLoginChallenge!.ChallengeId);
        Assert.Equal(authStateBeforeCancel, session.AuthState);

        var second = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);

        // Resumed the SAME cached challenge - no second daemon login command was issued.
        Assert.Equal("chal-1", second!.ChallengeId);
        Assert.Equal(1, client.StartLoginCallCount);
    }

    [Fact]
    public async Task NotifyAuthStateChangeAsync_TransitionToAuthenticated_ClearsPendingChallenge()
    {
        var client = new SingleChallengeDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);

        var challenge = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(challenge);
        Assert.NotNull(session.PendingLoginChallenge);

        session.AuthState = DaemonAuthState.Authenticated;
        await ((TestableSteamDaemonService)daemon).InvokeNotifyAuthStateChangeAsync(session);

        Assert.Null(session.PendingLoginChallenge);
    }

    [Fact]
    public async Task FailLoginFastAsync_ClearsPendingChallenge()
    {
        var client = new SingleChallengeDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);

        var challenge = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(challenge);
        Assert.NotNull(session.PendingLoginChallenge);

        var result = await ((TestableSteamDaemonService)daemon)
            .InvokeFailLoginFastAsync(session, session.Id, "Login failed: some daemon error.");

        Assert.Null(result);
        Assert.Null(session.PendingLoginChallenge);
        Assert.Equal("Login failed: some daemon error.", session.LastLoginFailureMessage);
        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);
    }

    /// <summary>
    /// Edge case: the daemon reports the session authenticated (e.g. via a live status broadcast) in
    /// the window between two StartLoginAsync calls, while a (now stale) pending challenge from before
    /// that transition is still sitting on the session. The resume check must defer to AuthState and
    /// NOT hand back the stale challenge - it must go through the "already authenticated" branch
    /// instead, which asks the daemon and returns null (confirmed authenticated).
    /// </summary>
    [Fact]
    public async Task StartLoginAsync_PendingChallengeButNowAuthenticated_DoesNotResumeStaleChallenge()
    {
        var client = new SingleChallengeDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);

        var challenge = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(challenge);

        // Simulate the daemon having authenticated out-of-band (e.g. self-auth from its own volume)
        // without going through the normal clearing funnel, leaving a stale challenge in place on
        // purpose so the resume guard itself (not the clearing logic) is what's under test here.
        session.AuthState = DaemonAuthState.Authenticated;
        client.NextStartLoginReturnsNull = true;

        var result = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(2, client.StartLoginCallCount);
    }

    [Fact]
    public async Task WaitForChallengeAsync_ServesCachedChallengeImmediately_WithoutCallingClient()
    {
        var client = new SingleChallengeDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);

        var challenge = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(challenge);

        // WaitForChallengeAsync on the fake client throws NotSupportedException if invoked (it isn't
        // overridden by SingleChallengeDaemonClient), so reaching a non-throwing result proves the
        // cached challenge was served instead of falling through to the daemon.
        var polled = await daemon.WaitForChallengeAsync(session.Id, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.NotNull(polled);
        Assert.Equal(SingleChallengeDaemonClient.Challenge.ChallengeId, polled!.ChallengeId);
    }

    /// <summary>
    /// Session 20260703-085455-996528703 verifier, Cursor #1 (the BLOCKER): a multi-step login
    /// (username -> password) must never resume the STALE first-step challenge after the caller has
    /// already submitted credentials for it. Before this fix, <c>ProvideCredentialAsync</c> never
    /// cleared <see cref="DaemonSession.PendingLoginChallenge"/> and the follow-on challenge dispatch
    /// (<c>OnCredentialChallengeAsync</c>) never updated it either, so a GET-challenge poll (or the
    /// reopen reconcile) kept returning the already-answered username challenge forever.
    /// </summary>
    [Fact]
    public async Task ProvideCredentialAsync_ClearsCache_FollowOnChallengeReplacesIt_NeverServesStaleChallenge()
    {
        var client = new MultiStepLoginDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);
        var testableDaemon = (TestableSteamDaemonService)daemon;
        client.AttachDaemon(testableDaemon, session);

        var usernameChallenge = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal("chal-username", usernameChallenge!.ChallengeId);
        Assert.Same(usernameChallenge, session.PendingLoginChallenge);

        await daemon.ProvideCredentialAsync(session.Id, usernameChallenge, "user1", CancellationToken.None);

        // The follow-on (password) challenge must be what a poll/GET now serves - never the
        // already-answered username challenge that was cached before submission.
        var polled = await daemon.WaitForChallengeAsync(session.Id, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.NotNull(polled);
        Assert.Equal("chal-password", polled!.ChallengeId);
        Assert.NotEqual(usernameChallenge.ChallengeId, polled.ChallengeId);
        Assert.Same(polled, session.PendingLoginChallenge);
    }

    /// <summary>
    /// Session 20260703-085455-996528703 verifier, Cursor #1: drives the full Steam-shaped multi-step
    /// flow (username -> password -> 2FA -> authenticated) purely through the REST-facing service
    /// methods (<c>StartLoginAsync</c>/<c>ProvideCredentialAsync</c>/<c>WaitForChallengeAsync</c>) with
    /// no SignalR hub assertions at all, proving the flow completes correctly on REST alone (i.e. it
    /// does not depend on the SignalR push mirror to advance) and that no step ever observes a stale
    /// challenge from an earlier step.
    /// </summary>
    [Fact]
    public async Task MultiStepLogin_CompletesViaRestAlone_NeverServesStaleChallengeAtAnyStep()
    {
        var client = new MultiStepLoginDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);
        var testableDaemon = (TestableSteamDaemonService)daemon;
        client.AttachDaemon(testableDaemon, session);

        var challenge = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal("username", challenge!.CredentialType);

        await daemon.ProvideCredentialAsync(session.Id, challenge, "user1", CancellationToken.None);
        challenge = await daemon.WaitForChallengeAsync(session.Id, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("password", challenge!.CredentialType);

        await daemon.ProvideCredentialAsync(session.Id, challenge, "pass1", CancellationToken.None);
        challenge = await daemon.WaitForChallengeAsync(session.Id, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("2fa", challenge!.CredentialType);

        await daemon.ProvideCredentialAsync(session.Id, challenge, "123456", CancellationToken.None);

        // Last step consumed with no further challenge queued: the cache must be empty (not the
        // stale 2fa challenge), matching the daemon having moved on to logged-in.
        Assert.Null(session.PendingLoginChallenge);
        Assert.True(client.LoggedIn);

        // The daemon's own "logged-in" status broadcast flips AuthState and clears the cache via the
        // existing auth-success funnel (proven independently by
        // NotifyAuthStateChangeAsync_TransitionToAuthenticated_ClearsPendingChallenge above) -
        // simulated here to close the loop end-to-end for this scenario.
        session.AuthState = DaemonAuthState.Authenticated;
        await testableDaemon.InvokeNotifyAuthStateChangeAsync(session);
        Assert.Null(session.PendingLoginChallenge);
    }

    [Fact]
    public async Task TerminateSessionAsync_ClearsPendingChallengeOnTheSessionObject()
    {
        var client = new SingleChallengeDaemonClient();
        var (daemon, session) = CreateSessionWithClient(client);

        var challenge = await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(challenge);
        Assert.NotNull(session.PendingLoginChallenge);

        await daemon.TerminateSessionAsync(session.Id, "test cleanup", force: true);

        Assert.Null(session.PendingLoginChallenge);
    }

    private static (PrefillDaemonServiceBase Daemon, DaemonSession Session) CreateSessionWithClient(IDaemonClient client)
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"login_challenge_resume_{Guid.NewGuid():N}")
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

    // Test-only seams: _sessions is `protected` and NotifyAuthStateChangeAsync/FailLoginFastAsync are
    // protected/private on PrefillDaemonServiceBase, so production code never exposes a way to inject a
    // session or drive these funnels without a real daemon round-trip. Mirrors the InjectSession seam
    // already established in PersistentLoginFailFastTests.cs; the two extra invokers here let the
    // clearing side effects at each documented clear-point (auth-success funnel, fail-fast failure) be
    // asserted directly instead of being fought around the resume short-circuit they're testing against.
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

        public Task InvokeNotifyAuthStateChangeAsync(DaemonSession session) => NotifyAuthStateChangeAsync(session);

        public Task<CredentialChallenge?> InvokeFailLoginFastAsync(DaemonSession session, string sessionId, string failureMessage)
            => FailLoginFastAsync(session, sessionId, failureMessage);

        // Test-only seam mirroring PersistentLoginChallengePushTests.cs: production wires
        // IDaemonClient.OnCredentialChallenge to this during real session creation, which
        // InjectSession bypasses. Lets MultiStepLoginDaemonClient simulate the daemon dispatching a
        // follow-on challenge exactly as the real socket event loop would.
        public Task InvokeOnCredentialChallengeAsync(DaemonSession session, CredentialChallenge challenge)
            => OnCredentialChallengeAsync(session, challenge);
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
    /// paths (mirrors NullReturningProxy in PersistentLoginFailFastTests.cs).
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
    /// an unexpected call fails loudly (mirrors TestDaemonClientBase in PersistentLoginFailFastTests.cs).
    /// </summary>
    private abstract class TestDaemonClientBase : IDaemonClient
    {
        public event Func<CredentialChallenge, Task>? OnCredentialChallenge { add { } remove { } }
        public event Func<DaemonStatus, Task>? OnStatusUpdate { add { } remove { } }
        public event Func<SocketPrefillProgress, Task>? OnProgressUpdate { add { } remove { } }
        public event Func<string, Task>? OnError { add { } remove { } }
        public event Func<Task>? OnDisconnected { add { } remove { } }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public virtual Task<DaemonStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Unexpected GetStatusAsync in this scenario.");

        public Task<CommandResponse> SendCommandAsync(
            string type, Dictionary<string, string>? parameters = null, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException($"Unexpected SendCommandAsync({type}) in this test.");

        public abstract Task<CredentialChallenge?> StartLoginAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default);

        public virtual Task ProvideCredentialAsync(CredentialChallenge challenge, string credential, CancellationToken cancellationToken = default)
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

        public virtual Task CancelLoginAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public virtual Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
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
    /// Scenario: returns a real challenge exactly once. A second <c>StartLoginAsync</c> call throws,
    /// mirroring the daemon's real "already in progress" reply combined with the client's own
    /// queue-clearing destroying the challenge - i.e. what happens today if a resume is NOT taken.
    /// <see cref="NextStartLoginReturnsNull"/> lets a later call simulate the daemon confirming
    /// already-authenticated (returns null) instead of throwing, for the stale-challenge edge case.
    /// </summary>
    private sealed class SingleChallengeDaemonClient : TestDaemonClientBase
    {
        public static readonly CredentialChallenge Challenge = new()
        {
            ChallengeId = "chal-pending",
            CredentialType = "username"
        };

        public int StartLoginCallCount { get; private set; }
        public bool NextStartLoginReturnsNull { get; set; }

        public override Task<CredentialChallenge?> StartLoginAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            StartLoginCallCount++;
            if (StartLoginCallCount == 1)
            {
                return Task.FromResult<CredentialChallenge?>(Challenge);
            }
            if (NextStartLoginReturnsNull)
            {
                return Task.FromResult<CredentialChallenge?>(null);
            }
            throw new InvalidOperationException(
                "StartLoginAsync should not be called again while a challenge is pending (resume expected).");
        }

        public override Task<DaemonStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<DaemonStatus?>(new DaemonStatus { Status = "logged-in" });
    }

    /// <summary>
    /// Scenario: returns a fresh, distinct challenge on every call - used to prove a NEW daemon login is
    /// issued once the pending-challenge cache has been cleared (cancel-login).
    /// </summary>
    private sealed class MultiCallChallengeDaemonClient : TestDaemonClientBase
    {
        public int StartLoginCallCount { get; private set; }

        public override Task<CredentialChallenge?> StartLoginAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            StartLoginCallCount++;
            return Task.FromResult<CredentialChallenge?>(new CredentialChallenge
            {
                ChallengeId = $"chal-{StartLoginCallCount}",
                CredentialType = "username"
            });
        }
    }

    /// <summary>
    /// Scenario: returns a single real challenge, but the daemon-side cancel command itself throws -
    /// simulating a socket error or unresponsive daemon during <c>CancelLoginAsync</c>'s round-trip.
    /// <see cref="MultiCallChallengeDaemonClient.StartLoginCallCount"/>-style counting distinguishes a
    /// resumed cached challenge (count stays 1) from a fresh daemon login being issued (count reaches 2).
    /// </summary>
    private sealed class ThrowingCancelDaemonClient : TestDaemonClientBase
    {
        public int StartLoginCallCount { get; private set; }

        public override Task<CredentialChallenge?> StartLoginAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            StartLoginCallCount++;
            return Task.FromResult<CredentialChallenge?>(new CredentialChallenge
            {
                ChallengeId = $"chal-{StartLoginCallCount}",
                CredentialType = "username"
            });
        }

        public override Task CancelLoginAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Simulated daemon cancel-login round-trip failure.");
    }

    /// <summary>
    /// Scenario: a Steam-shaped three-step login (username -> password -> 2fa -> logged-in).
    /// <see cref="AttachDaemon"/> wires this fake to the same
    /// <see cref="TestableSteamDaemonService.InvokeOnCredentialChallengeAsync"/> seam production uses
    /// (<c>daemonClient.OnCredentialChallenge += ...</c> at session-creation time), so
    /// <see cref="ProvideCredentialAsync"/> can dispatch each follow-on challenge exactly the way the
    /// real socket event loop would - proving the manager-side cache tracks whichever challenge the
    /// daemon most recently emitted, not just the first one of the attempt.
    /// </summary>
    private sealed class MultiStepLoginDaemonClient : TestDaemonClientBase
    {
        private static readonly string[] Steps = { "username", "password", "2fa" };
        private TestableSteamDaemonService? _daemon;
        private DaemonSession? _session;
        private int _stepIndex;

        public int StartLoginCallCount { get; private set; }
        public bool LoggedIn { get; private set; }

        public void AttachDaemon(TestableSteamDaemonService daemon, DaemonSession session)
        {
            _daemon = daemon;
            _session = session;
        }

        public override Task<CredentialChallenge?> StartLoginAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            StartLoginCallCount++;
            return Task.FromResult<CredentialChallenge?>(MakeChallenge(_stepIndex));
        }

        public override async Task ProvideCredentialAsync(CredentialChallenge challenge, string credential, CancellationToken cancellationToken = default)
        {
            _stepIndex++;
            if (_stepIndex < Steps.Length)
            {
                // Simulate the daemon's async follow-on challenge dispatch (the real
                // IDaemonClient.OnCredentialChallenge event) synchronously so the test can assert
                // immediately after ProvideCredentialAsync returns.
                await _daemon!.InvokeOnCredentialChallengeAsync(_session!, MakeChallenge(_stepIndex));
            }
            else
            {
                LoggedIn = true;
            }
        }

        public override Task<DaemonStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<DaemonStatus?>(new DaemonStatus { Status = LoggedIn ? "logged-in" : "awaiting-login" });

        private static CredentialChallenge MakeChallenge(int stepIndex) => new()
        {
            ChallengeId = $"chal-{Steps[stepIndex]}",
            CredentialType = Steps[stepIndex]
        };
    }
}
