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
/// "Clear stored logins" hard-remove escalation for a RUNNING persistent container
/// (<see cref="PrefillDaemonServiceBase.ForgetRunningPersistentLoginAsync"/>).
///
/// Root cause this proves is fixed: a RUNNING persistent daemon self-authenticates from a real login
/// stored in its named auth volume. The old clear-logins running-branch only sent the daemon's
/// in-place <c>logout</c> command and trusted its success flag - but an un-updated steam/epic image
/// reports success WITHOUT deleting that volume token, so the login survived and the admin had to
/// <c>docker rm</c> the volume by hand. The fix VERIFIES the logout against the daemon's LIVE status
/// and, when it did not verifiably take, ESCALATES to terminate the container + delete its named auth
/// volume (the only image-version-independent hard remove).
///
/// Pre-fix behavior (what these tests would fail against): the running-branch called
/// <c>LogoutPersistentSessionAsync</c> and reported <c>Success = logoutResult.LoggedOut</c> with no
/// live-status verification and no fallback - so for the old-image-lies scenario below it would neither
/// terminate the container nor delete the volume, and would report a false success. The escalation
/// assertions (session removed + volume delete invoked + HardRemoved) therefore only hold post-fix.
///
/// Mirrors the fake-<see cref="IDaemonClient"/> + <c>TestableSteamDaemonService</c> harness in the
/// sibling PersistentEraseOnStopTests.cs / PersistentLogoutTests.cs (xUnit, hand-rolled fakes, no
/// mocking framework). <see cref="PrefillDaemonServiceBase.TerminateSessionAsync"/> runs for real
/// (Docker-unavailable in the harness, so it only removes the in-memory session), while
/// <see cref="PrefillDaemonServiceBase.ClearPersistentAuthVolumeAsync"/> is overridden to exercise the
/// volume-delete result without a live Docker daemon.
/// </summary>
public class PersistentClearLoginsEscalationTests
{
    [Fact]
    public async Task ForgetRunningPersistentLoginAsync_LogoutReportsSuccessButStatusStillLoggedIn_EscalatesToHardRemove()
    {
        // Old-image-lies: the daemon acknowledges the logout (LoggedOut=true) yet its live socket still
        // reports "logged-in" because the account token in the named volume was never deleted.
        var client = new ClearLoginsScenarioClient(logoutSucceeds: true, liveStatus: "logged-in");
        var (daemon, session) = CreateDaemonWithSession(client, PersistentVolumeClearResult.Removed);

        var outcome = await daemon.ForgetRunningPersistentLoginAsync(session.Id, CancellationToken.None);

        Assert.Equal(PersistentRunningLoginClearOutcome.HardRemoved, outcome);
        // The live status was actually consulted (the verification, not just the logout's own flag).
        Assert.True(client.StatusCallCount >= 1);
        // Escalation ran: the container was terminated (session removed) ...
        Assert.Null(daemon.GetSession(session.Id));
        Assert.Equal(DaemonSessionStatus.Terminated, session.Status);
        // ... and the named auth volume was deleted, AFTER the terminate detached it (no active session
        // remained when the delete ran, so it could not have hit an in-use volume).
        Assert.Equal(1, daemon.VolumeClearCallCount);
        Assert.False(daemon.ActiveSessionPresentWhenVolumeCleared);
    }

    [Fact]
    public async Task ForgetRunningPersistentLoginAsync_DaemonReportsLogoutFailure_EscalatesToHardRemove()
    {
        // Second escalation trigger: the daemon reports the logout failed outright (older image whose
        // pre-login gate rejects logout, or a genuine failure). Must also hard-remove, not give up.
        var client = new ClearLoginsScenarioClient(logoutSucceeds: false, liveStatus: "logged-in");
        var (daemon, session) = CreateDaemonWithSession(client, PersistentVolumeClearResult.Removed);

        var outcome = await daemon.ForgetRunningPersistentLoginAsync(session.Id, CancellationToken.None);

        Assert.Equal(PersistentRunningLoginClearOutcome.HardRemoved, outcome);
        Assert.Null(daemon.GetSession(session.Id));
        Assert.Equal(1, daemon.VolumeClearCallCount);
    }

    [Fact]
    public async Task ForgetRunningPersistentLoginAsync_StatusVerificationThrows_EscalatesRatherThanReportFalseSuccess()
    {
        // The logout reports success but the live status query itself fails (socket dropped). We cannot
        // stand behind an unverified success, so we escalate to the hard remove instead of celebrating.
        var client = new ClearLoginsScenarioClient(logoutSucceeds: true, liveStatus: null /* throws */);
        var (daemon, session) = CreateDaemonWithSession(client, PersistentVolumeClearResult.Removed);

        var outcome = await daemon.ForgetRunningPersistentLoginAsync(session.Id, CancellationToken.None);

        Assert.Equal(PersistentRunningLoginClearOutcome.HardRemoved, outcome);
        Assert.Null(daemon.GetSession(session.Id));
        Assert.Equal(1, daemon.VolumeClearCallCount);
    }

    [Fact]
    public async Task ForgetRunningPersistentLoginAsync_SoftLogoutVerifiedClean_DoesNotTerminate_ReportsLoggedOut()
    {
        // Happy path (updated image): the daemon acknowledges the logout AND its live status confirms it
        // is no longer logged in. The escalation must be CONDITIONAL - the container stays running and
        // its volume is never touched.
        var client = new ClearLoginsScenarioClient(logoutSucceeds: true, liveStatus: "logged-out");
        var (daemon, session) = CreateDaemonWithSession(client, PersistentVolumeClearResult.Removed);

        var outcome = await daemon.ForgetRunningPersistentLoginAsync(session.Id, CancellationToken.None);

        Assert.Equal(PersistentRunningLoginClearOutcome.LoggedOut, outcome);
        // No escalation: session still present + Active, and the volume delete was never invoked.
        Assert.NotNull(daemon.GetSession(session.Id));
        Assert.Equal(DaemonSessionStatus.Active, session.Status);
        Assert.Equal(0, daemon.VolumeClearCallCount);
        // The in-place logout did clear the manager-side auth state.
        Assert.Equal(DaemonAuthState.NotAuthenticated, session.AuthState);
    }

    [Fact]
    public async Task ForgetRunningPersistentLoginAsync_EscalationVolumeStillInUse_ReportsHardRemoveFailed_NotFalseSuccess()
    {
        // Honest failure: escalation ran but the volume delete could not complete (e.g. still attached /
        // Docker unavailable). Must report HardRemoveFailed so the UI never celebrates a login that
        // survived - Success is false only when BOTH the soft logout AND the hard remove failed.
        var client = new ClearLoginsScenarioClient(logoutSucceeds: true, liveStatus: "logged-in");
        var (daemon, session) = CreateDaemonWithSession(client, PersistentVolumeClearResult.InUse);

        var outcome = await daemon.ForgetRunningPersistentLoginAsync(session.Id, CancellationToken.None);

        Assert.Equal(PersistentRunningLoginClearOutcome.HardRemoveFailed, outcome);
        Assert.Equal(1, daemon.VolumeClearCallCount);
    }

    // ---- Fixtures -------------------------------------------------------------------------------

    private static (TestableSteamDaemonService Daemon, DaemonSession Session) CreateDaemonWithSession(
        IDaemonClient client, PersistentVolumeClearResult volumeClearResult)
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"clear_logins_escalation_{Guid.NewGuid():N}")
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
            stateService, sessionService, cacheService, networkOptions, volumeClearResult);

        var session = new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = ScheduledPrefillConstants.DeriveSystemUserId(),
            Status = DaemonSessionStatus.Active,
            IsPersistent = true,
            AuthState = DaemonAuthState.Authenticated,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            Client = client
        };
        daemon.InjectSession(session);

        return (daemon, session);
    }

    /// <summary>
    /// Test-only seam: injects a session without a real daemon round-trip (mirrors the InjectSession
    /// seam in the sibling persistent-session test files) and overrides
    /// <see cref="PrefillDaemonServiceBase.ClearPersistentAuthVolumeAsync"/> so the hard-remove path is
    /// exercised deterministically without a live Docker daemon (which would otherwise return
    /// <see cref="PersistentVolumeClearResult.DockerUnavailable"/>).
    /// </summary>
    private sealed class TestableSteamDaemonService : SteamDaemonService
    {
        private readonly PersistentVolumeClearResult _volumeClearResult;

        public TestableSteamDaemonService(
            Microsoft.Extensions.Logging.ILogger<SteamDaemonService> logger,
            ISignalRNotificationService notifications,
            IConfiguration configuration,
            IPathResolver pathResolver,
            IStateService stateService,
            PrefillSessionService sessionService,
            PrefillCacheService cacheService,
            IOptionsMonitor<PrefillNetworkOptions> networkOptions,
            PersistentVolumeClearResult volumeClearResult)
            : base(logger, notifications, configuration, pathResolver, stateService, sessionService, cacheService, networkOptions)
        {
            _volumeClearResult = volumeClearResult;
        }

        public int VolumeClearCallCount { get; private set; }

        /// <summary>
        /// Snapshot of whether an ACTIVE persistent session still existed at the moment the volume
        /// delete ran - proves the escalation terminated the container FIRST (so the volume was
        /// detached), rather than trying to delete a still-attached volume.
        /// </summary>
        public bool ActiveSessionPresentWhenVolumeCleared { get; private set; }

        public void InjectSession(DaemonSession session) => _sessions[session.Id] = session;

        public override Task<PersistentVolumeClearResult> ClearPersistentAuthVolumeAsync(CancellationToken cancellationToken = default)
        {
            VolumeClearCallCount++;
            ActiveSessionPresentWhenVolumeCleared = GetActivePersistentSession() is not null;
            return Task.FromResult(_volumeClearResult);
        }
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
    /// Minimal do-nothing proxy for interfaces whose members are not exercised (mirrors
    /// NullReturningProxy in the sibling persistent-session test files).
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
    /// Fake <see cref="IDaemonClient"/> exposing only the surface a clear-logins escalation touches
    /// (logout + live status); every other member throws <see cref="NotSupportedException"/> so an
    /// unexpected call fails loudly (mirrors TestDaemonClientBase in PersistentEraseOnStopTests.cs).
    /// <see cref="GetStatusAsync"/> is virtual here so a scenario can script the live status the
    /// verification step reads (or make it throw).
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

        public Task<CredentialChallenge?> StartLoginAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Unexpected StartLoginAsync in a clear-logins scenario.");

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

        // Explicit (virtual, not DIM-inherited) so a subclass can override it to simulate the daemon's
        // RequiresLogin signal - mirrors TestDaemonClientBase in the sibling files. Fakes that don't
        // override this just adapt whatever LogoutAsync returns, matching IDaemonClient's own default.
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

        // TerminateSessionAsync only calls ShutdownAsync in its graceful (non-force) branch, and only
        // when _dockerClient and session.ContainerId are both set - neither is true in this harness -
        // so this is never actually invoked. Completed rather than throwing as a defensive default.
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public virtual void ClearPendingChallenges() { }

        public void Dispose() { }
    }

    /// <summary>
    /// Scenario client: scripts the daemon's <c>logout</c> result and the live status the verification
    /// step subsequently reads. A null <paramref name="liveStatus"/> makes the status query throw, to
    /// exercise the "could not verify -> escalate" branch.
    /// </summary>
    private sealed class ClearLoginsScenarioClient : TestDaemonClientBase
    {
        private readonly bool _logoutSucceeds;
        private readonly string? _liveStatus;

        public ClearLoginsScenarioClient(bool logoutSucceeds, string? liveStatus)
        {
            _logoutSucceeds = logoutSucceeds;
            _liveStatus = liveStatus;
        }

        public int LogoutCallCount { get; private set; }
        public int StatusCallCount { get; private set; }

        public override Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
        {
            LogoutCallCount++;
            return Task.FromResult(_logoutSucceeds);
        }

        public override Task<DaemonStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            StatusCallCount++;
            if (_liveStatus is null)
            {
                throw new InvalidOperationException("Simulated live-status socket failure.");
            }

            return Task.FromResult<DaemonStatus?>(new DaemonStatus { Status = _liveStatus });
        }
    }
}
