using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Proves
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
        var dbFactory = new TestDbContextFactory(dbOptions);
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
}
