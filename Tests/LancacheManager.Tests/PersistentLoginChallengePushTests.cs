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
/// Session 20260703-085455-996528703, Worker 4: proves that a credential challenge received for a
/// session (the same event that populates <see cref="DaemonSession.PendingLoginChallenge"/> - see
/// PersistentLoginChallengeResumeTests.cs / Worker 1) is also pushed to the DownloadHub via
/// <c>NotifyHubAsync</c>, mirroring the existing AuthStateChanged/SessionUpdated mirror in
/// <c>NotifyAuthStateChangeAsync</c>. This is what lets the persistent-container config modal -
/// which never calls <c>SubscribeToSessionAsync</c>, unlike the mapping-flow live login - receive
/// the challenge instantly instead of waiting on the REST challenge poll.
/// </summary>
public class PersistentLoginChallengePushTests
{
    [Fact]
    public async Task OnCredentialChallengeAsync_PushesChallengeToDownloadHub_ViaNotifyHubAsync()
    {
        var (daemon, session) = CreateSession();
        var recorder = (RecordingNotificationsProxy)daemon.Notifications;

        var challenge = new CredentialChallenge
        {
            ChallengeId = "chal-push-1",
            CredentialType = "username"
        };

        await daemon.InvokeOnCredentialChallengeAsync(session, challenge);

        var pushed = Assert.Single(recorder.SteamHubCalls);
        Assert.Equal(SignalREvents.CredentialChallenge, pushed.EventName);
        Assert.Equal(session.Id, pushed.SessionId);
        Assert.Equal("chal-push-1", pushed.ChallengeId);
    }

    [Fact]
    public async Task OnCredentialChallengeAsync_StillBroadcastsToSubscribedConnections()
    {
        // The pre-existing per-connection broadcast (used by the mapping-flow live login, which
        // DOES call SubscribeToSessionAsync) must keep working unchanged alongside the new hub
        // mirror - this is an additive push, not a replacement.
        var (daemon, session) = CreateSession();
        var recorder = (RecordingNotificationsProxy)daemon.Notifications;
        session.SubscribedConnections.Add("conn-1");

        var challenge = new CredentialChallenge { ChallengeId = "chal-push-2", CredentialType = "password" };

        await daemon.InvokeOnCredentialChallengeAsync(session, challenge);

        var rawSend = Assert.Single(recorder.RawClientSends);
        Assert.Equal("conn-1", rawSend.ConnectionId);
        Assert.Equal(SignalREvents.CredentialChallenge, rawSend.EventName);
        // Both delivery paths fire for the same challenge.
        Assert.Single(recorder.SteamHubCalls);
    }

    private static (TestableSteamDaemonService Daemon, DaemonSession Session) CreateSession()
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"login_challenge_push_{Guid.NewGuid():N}")
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
            Client = (IDaemonClient)DispatchProxy.Create<IDaemonClient, NullReturningProxy>()
        };
        daemon.InjectSession(session);

        return (daemon, session);
    }

    // Test-only seam: OnCredentialChallengeAsync is `protected` (widened from `private` this
    // session so tests can drive it directly) since production wiring hooks it to
    // IDaemonClient.OnCredentialChallenge during real session creation, which InjectSession
    // bypasses. Also exposes the notifications proxy so tests can read recorded calls.
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
            : base(logger, notifications, configuration, pathResolver, stateService, sessionService, cacheService, networkOptions)
        {
            Notifications = notifications;
        }

        public void InjectSession(DaemonSession session) => _sessions[session.Id] = session;

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

    private sealed record HubCall(string EventName, string SessionId, string ChallengeId);

    private sealed record RawClientSend(string ConnectionId, string EventName);

    /// <summary>
    /// Records calls to NotifySteamHubAsync (the mirror this test proves fires) and
    /// SendToPrefillClientRawAsync (the pre-existing per-connection broadcast), reading the
    /// sessionId/challenge fields off the anonymous payload by reflection (mirrors the `stage`
    /// property pattern in ScheduledPrefillAnonymousRunPathTests.RecordingNotificationsProxy).
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
