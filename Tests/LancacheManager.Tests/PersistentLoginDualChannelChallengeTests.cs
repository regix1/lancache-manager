using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// Proves the fix for Bug #3 - the manager
/// serving the same login <c>password</c> challenge to the frontend TWICE.
///
/// The daemon delivers each credential challenge over TWO channels: the return value of
/// <c>WaitForChallengeAsync</c> AND the <c>OnCredentialChallenge</c> event
/// (<c>OnCredentialChallengeAsync</c>). Once the caller answers the challenge,
/// <c>ProvideCredentialAsync</c> clears <see cref="DaemonSession.PendingLoginChallenge"/> and records the
/// answered id in <see cref="DaemonSession.LastConsumedLoginChallengeId"/>. Before the fix, a late
/// duplicate of that SAME challenge arriving over the event channel re-cached the now-consumed challenge,
/// so the next <c>WaitForChallengeAsync</c> replayed it - the doubled <c>challenge:password</c> that stalls
/// the login before device-confirmation.
///
/// PRE-FIX BEHAVIOR (verified by temporarily removing the LastConsumedLoginChallengeId guard in
/// <c>OnCredentialChallengeAsync</c> and re-running
/// <see cref="OnCredentialChallengeAsync_StaleReDeliveryOfConsumedChallenge_NotReCachedOrBroadcast"/>): the
/// stale event unconditionally set <c>PendingLoginChallenge = challenge</c> and broadcast it, so the
/// assertions that the cache stays null and nothing is broadcast both fail.
/// </summary>
public class PersistentLoginDualChannelChallengeTests
{
    private const string PasswordChallengeId = "chal-password";
    private const string DeviceConfirmationChallengeId = "chal-device-confirmation";

    /// <summary>
    /// The race repro: after the password credential is provided (cache cleared,
    /// LastConsumedLoginChallengeId = password's id), the daemon's OTHER delivery channel fires
    /// OnCredentialChallenge for the SAME password challenge. It must be dropped as a stale re-delivery -
    /// NOT re-cached (so WaitForChallengeAsync cannot re-serve it) and NOT broadcast, and it must not
    /// regress AuthState back to the consumed step.
    /// </summary>
    [Fact]
    public async Task OnCredentialChallengeAsync_StaleReDeliveryOfConsumedChallenge_NotReCachedOrBroadcast()
    {
        var (daemon, session) = CreateSession();
        var recorder = (RecordingNotificationsProxy)daemon.Notifications;

        var passwordChallenge = new CredentialChallenge
        {
            ChallengeId = PasswordChallengeId,
            CredentialType = "password"
        };

        // The poller served this challenge from the cache; the caller now answers it.
        session.PendingLoginChallenge = passwordChallenge;
        session.AuthState = DaemonAuthState.PasswordRequired;

        await daemon.ProvideCredentialAsync(session.Id, passwordChallenge, "pass1", CancellationToken.None);

        // Consuming the credential clears the cache and records the just-consumed id.
        Assert.Null(session.PendingLoginChallenge);
        Assert.Equal(PasswordChallengeId, session.LastConsumedLoginChallengeId);

        // Simulate the daemon having advanced the auth state (the real, genuinely-new device-confirmation
        // challenge would arrive over the other channel first). The stale password re-delivery below must
        // not regress this.
        session.AuthState = DaemonAuthState.DeviceConfirmationRequired;

        // The SECOND delivery channel fires the SAME password challenge id AFTER it was consumed.
        await daemon.InvokeOnCredentialChallengeAsync(session, new CredentialChallenge
        {
            ChallengeId = PasswordChallengeId,
            CredentialType = "password"
        });

        // Dropped: cache stays empty (WaitForChallengeAsync would NOT re-serve the stale password),
        // nothing was broadcast, and AuthState was not regressed to PasswordRequired.
        Assert.Null(session.PendingLoginChallenge);
        Assert.Empty(recorder.SteamHubCalls);
        Assert.Empty(recorder.RawClientSends);
        Assert.Equal(DaemonAuthState.DeviceConfirmationRequired, session.AuthState);
    }

    /// <summary>
    /// Companion proving the guard is specific to the immediate re-delivery, not a blanket block: after the
    /// password is consumed, a genuinely NEW follow-on challenge (device-confirmation, DIFFERENT id) must
    /// still be cached and broadcast so the login can advance.
    /// </summary>
    [Fact]
    public async Task OnCredentialChallengeAsync_NewFollowOnChallengeAfterConsumedPassword_CachedAndBroadcast()
    {
        var (daemon, session) = CreateSession();
        var recorder = (RecordingNotificationsProxy)daemon.Notifications;
        session.SubscribedConnections.Add("conn-1");

        var passwordChallenge = new CredentialChallenge
        {
            ChallengeId = PasswordChallengeId,
            CredentialType = "password"
        };
        session.PendingLoginChallenge = passwordChallenge;
        session.AuthState = DaemonAuthState.PasswordRequired;

        await daemon.ProvideCredentialAsync(session.Id, passwordChallenge, "pass1", CancellationToken.None);
        Assert.Null(session.PendingLoginChallenge);
        Assert.Equal(PasswordChallengeId, session.LastConsumedLoginChallengeId);

        var deviceConfirmation = new CredentialChallenge
        {
            ChallengeId = DeviceConfirmationChallengeId,
            CredentialType = "device-confirmation"
        };

        await daemon.InvokeOnCredentialChallengeAsync(session, deviceConfirmation);

        // A different id is a real follow-on: cached, auth state advanced, and broadcast over both paths.
        Assert.Same(deviceConfirmation, session.PendingLoginChallenge);
        Assert.Equal(DaemonAuthState.DeviceConfirmationRequired, session.AuthState);

        var pushed = Assert.Single(recorder.SteamHubCalls);
        Assert.Equal(SignalREvents.CredentialChallenge, pushed.EventName);
        Assert.Equal(DeviceConfirmationChallengeId, pushed.ChallengeId);

        var rawSend = Assert.Single(recorder.RawClientSends);
        Assert.Equal("conn-1", rawSend.ConnectionId);
        Assert.Equal(SignalREvents.CredentialChallenge, rawSend.EventName);
    }

    /// <summary>
    /// A fresh login attempt clears the just-consumed marker so it can never suppress a legitimate future
    /// challenge that (in a later attempt) could reuse an id - proving the reset side of the invariant.
    /// </summary>
    [Fact]
    public async Task ProvideCredentialAsync_ThenFreshStartLogin_ClearsLastConsumedMarker()
    {
        var client = new SingleChallengeDaemonClient();
        var (daemon, session) = CreateSession(client);

        var passwordChallenge = new CredentialChallenge
        {
            ChallengeId = PasswordChallengeId,
            CredentialType = "password"
        };
        session.PendingLoginChallenge = passwordChallenge;

        await daemon.ProvideCredentialAsync(session.Id, passwordChallenge, "pass1", CancellationToken.None);
        Assert.Equal(PasswordChallengeId, session.LastConsumedLoginChallengeId);

        // A brand-new attempt (no pending challenge, not authenticated) must reset the marker.
        session.AuthState = DaemonAuthState.NotAuthenticated;
        await daemon.StartLoginAsync(session.Id, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Null(session.LastConsumedLoginChallengeId);
    }

    private static (TestableSteamDaemonService Daemon, DaemonSession Session) CreateSession(IDaemonClient? client = null)
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"login_dual_channel_{Guid.NewGuid():N}")
            .Options;
        var dbFactory = new InMemoryDbContextFactory(dbOptions);
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        var cacheService = new PrefillCacheService(dbFactory, NullLogger<PrefillCacheService>.Instance);
        var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationsProxy>();
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
            Client = client ?? (IDaemonClient)DispatchProxy.Create<IDaemonClient, NullReturningProxy>()
        };
        daemon.InjectSession(session);

        return (daemon, session);
    }

    // Test-only seam mirroring PersistentLoginChallengePushTests.cs: OnCredentialChallengeAsync and the
    // session registry are protected on the base service, so production never exposes a way to inject a
    // session or drive the follow-on-challenge event without a real socket round-trip. Also exposes the
    // notifications proxy so tests can read recorded broadcasts.
    private sealed class TestableSteamDaemonService : SteamDaemonService
    {
        public ISignalRNotificationService Notifications { get; }

        public TestableSteamDaemonService(
            Microsoft.Extensions.Logging.ILogger<SteamDaemonService> logger,
            ISignalRNotificationService notifications,
            IConfiguration configuration,
            IPathResolver pathResolver,
            IStateService stateService,
            PrefillSessionService sessionService,
            PrefillCacheService cacheService,
            IOptionsMonitor<PrefillNetworkOptions> networkOptions)
            : base(logger, notifications, configuration, pathResolver, stateService, sessionService, cacheService, networkOptions, new TestLancacheServerLocator(), new UnavailableContainerGatewayFactory())
        {
            Notifications = notifications;
        }

        public void InjectSession(DaemonSession session) => _sessions[session.Id] = session;

        public Task InvokeOnCredentialChallengeAsync(DaemonSession session, CredentialChallenge challenge)
            => OnCredentialChallengeAsync(session, challenge);
    }

    /// <summary>
    /// Returns a real challenge exactly once from StartLoginAsync - lets the reset test issue a fresh
    /// daemon login after the earlier challenge was consumed.
    /// </summary>
    private sealed class SingleChallengeDaemonClient : TestDaemonClientBase
    {
        public override Task<CredentialChallenge?> StartLoginAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => Task.FromResult<CredentialChallenge?>(new CredentialChallenge
            {
                ChallengeId = "chal-fresh-username",
                CredentialType = "username"
            });

        public override Task ProvideCredentialAsync(CredentialChallenge challenge, string credential, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Base fake <see cref="IDaemonClient"/> whose members throw unless a scenario overrides them (mirrors
    /// TestDaemonClientBase in PersistentLoginChallengeResumeTests.cs).
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
            => throw new NotSupportedException();

        public Task<CommandResponse> SendCommandAsync(
            string type, Dictionary<string, string>? parameters = null, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

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
            => throw new NotSupportedException();

        public virtual Task CancelLoginAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public virtual Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

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

    private sealed record HubCall(string EventName, string SessionId, string ChallengeId);

    private sealed record RawClientSend(string ConnectionId, string EventName);

    /// <summary>
    /// Records NotifySteamHubAsync (the hub mirror) and SendToPrefillClientRawAsync (the per-connection
    /// broadcast) so a test can assert whether a challenge was broadcast (mirrors the recorder in
    /// PersistentLoginChallengePushTests.cs).
    /// </summary>
    private class RecordingNotificationsProxy : DispatchProxy
    {
        public List<HubCall> SteamHubCalls { get; } = new();
        public List<RawClientSend> RawClientSends { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISignalRNotificationService.NotifySteamHubAsync) && args is { Length: >= 2 })
            {
                var eventName = args[0] as string ?? string.Empty;
                var data = args[1];
                var sessionId = data?.GetType().GetProperty("sessionId")?.GetValue(data) as string ?? string.Empty;
                var challenge = data?.GetType().GetProperty("challenge")?.GetValue(data) as CredentialChallenge;
                SteamHubCalls.Add(new HubCall(eventName, sessionId, challenge?.ChallengeId ?? string.Empty));
            }
            else if (targetMethod?.Name == nameof(ISignalRNotificationService.SendToPrefillClientRawAsync) && args is { Length: >= 2 })
            {
                var connectionId = args[0] as string ?? string.Empty;
                var eventName = args[1] as string ?? string.Empty;
                RawClientSends.Add(new RawClientSend(connectionId, eventName));
            }

            return DefaultReturnValue(targetMethod);
        }
    }

    private class NullReturningProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => DefaultReturnValue(targetMethod);
    }

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
}
