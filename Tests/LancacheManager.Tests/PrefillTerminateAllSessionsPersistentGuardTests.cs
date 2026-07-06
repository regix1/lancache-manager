using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// Verifier fix (Cursor #2.5, PRE-EXISTING HIGH, session 20260703-024834-181826369): proves
/// <c>PrefillDaemonServiceBase.TerminateAllSessionsAsync</c> - reachable from
/// <c>SteamKit2Service.Authentication.LogoutAsync</c> on a Steam PICS credential logout, not just
/// service shutdown - never tears down a persistent (system-owned) session unless a caller explicitly
/// opts in via <c>includePersistent: true</c>. Mirrors the harness in
/// <c>PersistentLoginFailFastTests.CreateSessionWithClient</c> (TestableSteamDaemonService +
/// InMemoryDbContextFactory + DispatchProxy null fakes).
/// </summary>
public class PrefillTerminateAllSessionsPersistentGuardTests
{
    [Fact]
    public async Task TerminateAllSessionsAsync_DefaultCall_SkipsPersistentSession()
    {
        var daemon = NewDaemon();
        var persistent = InjectSession(daemon, "persist-1", isPersistent: true);
        var guest = InjectSession(daemon, "guest-1", isPersistent: false);

        await daemon.TerminateAllSessionsAsync("Steam PICS authentication logged out");

        Assert.Same(persistent, daemon.GetAllSessions().FirstOrDefault(s => s.Id == "persist-1"));
        Assert.DoesNotContain(daemon.GetAllSessions(), s => s.Id == "guest-1");
        Assert.Equal(DaemonSessionStatus.Active, persistent.Status);
        Assert.Equal(DaemonSessionStatus.Terminated, guest.Status);
    }

    [Fact]
    public async Task TerminateAllSessionsAsync_IncludePersistentTrue_AlsoTerminatesPersistentSession()
    {
        var daemon = NewDaemon();
        var persistent = InjectSession(daemon, "persist-2", isPersistent: true);

        await daemon.TerminateAllSessionsAsync("Full shutdown", includePersistent: true);

        Assert.DoesNotContain(daemon.GetAllSessions(), s => s.Id == "persist-2");
        Assert.Equal(DaemonSessionStatus.Terminated, persistent.Status);
    }

    [Fact]
    public async Task TerminateAllSessionsAsync_DefaultCall_SkipsSessionWithPersistentContainerNameEvenIfFlagUnset()
    {
        // Defense-in-depth case: the IsPersistent flag wasn't set correctly, but the container name
        // still matches the deterministic persistent-container convention. The old inline
        // `!s.IsPersistent` filter would have terminated this session; the shared
        // PrefillSessionService.IsTerminatableByAdmin predicate must now skip it too.
        var daemon = NewDaemon();
        var persistent = InjectSession(daemon, "persist-3", isPersistent: false, containerName: "steam-daemon-persistent");

        await daemon.TerminateAllSessionsAsync("Steam PICS authentication logged out");

        Assert.Same(persistent, daemon.GetAllSessions().FirstOrDefault(s => s.Id == "persist-3"));
        Assert.Equal(DaemonSessionStatus.Active, persistent.Status);
    }

    private static TestableSteamDaemonService NewDaemon()
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"terminate_all_persistent_guard_{Guid.NewGuid():N}")
            .Options;
        var dbFactory = new InMemoryDbContextFactory(dbOptions);
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        var cacheService = new PrefillCacheService(dbFactory, NullLogger<PrefillCacheService>.Instance);
        var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
        var configuration = new ConfigurationBuilder().Build();
        var pathResolver = (IPathResolver)DispatchProxy.Create<IPathResolver, NullReturningProxy>();
        var stateService = (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>();
        var networkOptions = new StaticOptionsMonitor<PrefillNetworkOptions>(new PrefillNetworkOptions());

        return new TestableSteamDaemonService(
            NullLogger<SteamDaemonService>.Instance, notifications, configuration, pathResolver,
            stateService, sessionService, cacheService, networkOptions);
    }

    private static DaemonSession InjectSession(
        TestableSteamDaemonService daemon, string id, bool isPersistent, string? containerName = null)
    {
        var session = new DaemonSession
        {
            Id = id,
            UserId = Guid.NewGuid(),
            ContainerName = containerName ?? (isPersistent ? "steam-daemon-persistent" : $"steam-daemon-{id}"),
            Status = DaemonSessionStatus.Active,
            IsPersistent = isPersistent,
            AuthState = DaemonAuthState.Authenticated,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            Client = (IDaemonClient)DispatchProxy.Create<IDaemonClient, NullReturningProxy>()
        };
        daemon.InjectSession(session);
        return session;
    }

    // Test-only seam: _sessions is `protected` on PrefillDaemonServiceBase so production code never
    // exposes a way to inject a session without going through real Docker container creation. Mirrors
    // TestableSteamDaemonService in PersistentLoginFailFastTests.cs.
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
}
