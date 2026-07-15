using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// Proves <c>NotifyPrefillProgressAsync</c> only
/// drives a session to a terminal Failed/Cancelled state when that session is actually mid-prefill.
///
/// Root cause (context/diagnostic.md): the daemon reuses the prefill progress channel to report
/// LOGIN-phase failures - a Steam Guard/email code-getter throwing for a mobile-push account calls
/// <c>_progress.OnError(...)</c>, broadcast as <c>Progress{State="error"}</c>. The old terminal block
/// mapped ANY error/failed/cancelled progress to <c>TransitionToTerminalAsync(Failed)</c> with no
/// <c>IsPrefilling</c> guard, so a guest session still in its device-confirmation phase
/// (<c>IsPrefilling==false</c>, <c>PrefillStartedAt==null</c>) got killed with
/// "Prefill failed ..., duration: 0s", dropping the user out of the confirmation step.
///
/// PRE-FIX BEHAVIOR (verified by reverting only the guard and re-running): without the guard,
/// <see cref="ErrorProgress_SessionNotPrefilling_DoesNotTransitionToTerminal"/> fails - the session
/// flips to <c>PrefillState.Failed</c>, <c>TerminalCompletedFlag</c> becomes 1, <c>LastPrefillStatus</c>
/// becomes "failed", and exactly one <c>PrefillStateChanged</c> is broadcast (the "duration: 0s" kill).
/// The companion <see cref="ErrorProgress_SessionPrefilling_StillTransitionsToFailedExactlyOnce"/>
/// passes both before and after, proving the guard is additive and never suppresses a genuine prefill
/// failure. This path is shared by every prefill session type (guest + persistent, all 5 services), so
/// the guard is purely "is this session mid-prefill" - never narrowed to Steam-only or guest-only.
/// </summary>
public class PrefillProgressLoginPhaseGuardTests
{
    /// <summary>
    /// Reproducing test (acceptance criterion 1): a daemon error/failed/cancelled progress push arriving
    /// while the session has never started prefilling must NOT transition it to a terminal state.
    /// </summary>
    [Theory]
    [InlineData("error")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task ErrorProgress_SessionNotPrefilling_DoesNotTransitionToTerminal(string daemonState)
    {
        var (daemon, session, recorder) = CreateDaemonWithSession();

        // Login / device-confirmation phase: this session has never started a prefill.
        session.IsPrefilling = false;
        session.PrefillState = PrefillState.Idle;
        session.PrefillStartedAt = null;
        session.TerminalCompletedFlag = 0;

        await daemon.InvokeNotifyPrefillProgressAsync(session, new PrefillProgress { State = daemonState });

        // (b) the session was NOT flipped to a terminal Failed/Cancelled state, and (c) the terminal
        // funnel (TransitionToTerminalAsync, the sole emitter of "Prefill failed ..., duration: 0s")
        // never ran, so no login-killing transition occurred.
        Assert.False(session.IsPrefilling);
        Assert.Equal(PrefillState.Idle, session.PrefillState);
        Assert.Equal(0, session.TerminalCompletedFlag);
        Assert.Null(session.LastPrefillStatus);
        Assert.Null(session.LastPrefillCompletedAt);

        // (a) no terminal PrefillStateChanged broadcast fired. That broadcast is emitted exclusively
        // inside TransitionToTerminalAsync after its idempotency flag flips, so TerminalCompletedFlag==0
        // already proves it never fired; this asserts it directly on the SignalR surface too.
        Assert.DoesNotContain(recorder.Invocations, i =>
            i.Method == nameof(ISignalRNotificationService.NotifySteamHubAsync)
            && i.Args.Length > 0
            && (i.Args[0] as string) == SignalREvents.PrefillStateChanged);
    }

    /// <summary>
    /// Companion regression test (acceptance criterion 2): the SAME error progress arriving while the
    /// session genuinely IS prefilling must still produce exactly one terminal Failed transition,
    /// unchanged from current behavior. Passes both before and after the fix, proving the guard is
    /// additive rather than a removal of real failure handling.
    /// </summary>
    [Fact]
    public async Task ErrorProgress_SessionPrefilling_StillTransitionsToFailedExactlyOnce()
    {
        var (daemon, session, recorder) = CreateDaemonWithSession();

        // Genuinely mid-prefill: started, a download tick already observed.
        session.IsPrefilling = true;
        session.PrefillState = PrefillState.Downloading;
        session.PrefillStartedAt = DateTime.UtcNow.AddSeconds(-10);
        session.TerminalCompletedFlag = 0;

        await daemon.InvokeNotifyPrefillProgressAsync(session, new PrefillProgress { State = "error" });

        // Real failure handling is unchanged: exactly one terminal Failed transition fired.
        Assert.False(session.IsPrefilling);
        Assert.Equal(PrefillState.Failed, session.PrefillState);
        Assert.Equal(1, session.TerminalCompletedFlag);
        Assert.Equal(PrefillProgressState.Failed.ToWireString(), session.LastPrefillStatus);
        Assert.NotNull(session.LastPrefillCompletedAt);

        var terminalBroadcasts = recorder.Invocations.Count(i =>
            i.Method == nameof(ISignalRNotificationService.NotifySteamHubAsync)
            && i.Args.Length > 0
            && (i.Args[0] as string) == SignalREvents.PrefillStateChanged);
        Assert.Equal(1, terminalBroadcasts);
    }

    private static (TestableSteamDaemonService Daemon, DaemonSession Session, RecordingNotificationProxy Recorder)
        CreateDaemonWithSession()
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"prefill_progress_guard_{Guid.NewGuid():N}")
            .Options;
        var dbFactory = new InMemoryDbContextFactory(dbOptions);
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        var cacheService = new PrefillCacheService(dbFactory, NullLogger<PrefillCacheService>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        var recorder = (RecordingNotificationProxy)(object)notifications;
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
            UserId = Guid.NewGuid(),
            Status = DaemonSessionStatus.Active,
            AuthState = DaemonAuthState.DeviceConfirmationRequired,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        };
        daemon.InjectSession(session);

        return (daemon, session, recorder);
    }

    // Test-only seam: _sessions is `protected` on PrefillDaemonServiceBase so production never exposes a
    // way to inject a session without real Docker container creation. Mirrors the TestableSteamDaemonService
    // pattern in PersistentLoginFailFastTests.cs.
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
            : base(logger, notifications, configuration, pathResolver, stateService, sessionService, cacheService, networkOptions, new TestLancacheServerLocator(), new UnavailableContainerGatewayFactory())
        {
        }

        public void InjectSession(DaemonSession session) => _sessions[session.Id] = session;

        // NotifyPrefillProgressAsync is protected on PrefillDaemonServiceBase (widened from private for
        // exactly this seam); production drives it from the socket read loop via OnProgressChangeAsync.
        public Task InvokeNotifyPrefillProgressAsync(DaemonSession session, PrefillProgress progress)
            => NotifyPrefillProgressAsync(session, progress);
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
    /// Records every <see cref="ISignalRNotificationService"/> invocation (method name + args) so a test
    /// can assert which SignalR events were (or were not) broadcast, then returns the same harmless
    /// null/Task defaults as <see cref="NullReturningProxy"/> for members the tests don't exercise. Not
    /// sealed: <see cref="DispatchProxy.Create{T, TProxy}"/> generates a subtype of the proxy class.
    /// </summary>
    private class RecordingNotificationProxy : DispatchProxy
    {
        public List<(string Method, object?[] Args)> Invocations { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is not null)
            {
                Invocations.Add((targetMethod.Name, args ?? Array.Empty<object?>()));
            }

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
    /// Minimal do-nothing proxy for interfaces whose members are not exercised by these tests (mirrors
    /// NullReturningProxy in PersistentLoginFailFastTests.cs).
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
}
