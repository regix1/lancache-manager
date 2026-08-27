using System.Globalization;
using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// <c>PrefillCachedDepots</c> is one shared table behind every browser's game picker, so a change one
/// client makes has to reach the rest. The per-app delete route reaches
/// <see cref="PrefillCacheService.ClearAppCacheAsync"/>, takes only its own app's rows, and says how
/// many it took so an app that owns none is not reported as removed. The
/// download choke point announces a change only when a depot was actually written: an
/// <c>AlreadyUpToDate</c> app re-records depots the table already holds, so prefilling 200
/// already-cached games walks that line 200 times with nothing new to say. Two containers writing
/// the same depot lose one insert to the unique index, and the batch has to carry on past it.
/// </summary>
public sealed class PrefillCacheChangeTests
{
    private const long CachedAppId = 667970;
    private const long OtherAppId = 730;
    private const long CachedDepotId = 1770481;
    private const ulong CachedManifestId = 4625979481897414804;

    [Fact]
    public async Task ClearAppCache_LeavesEveryOtherAppCached()
    {
        var options = NewOptions();
        await using (var seed = new AppDbContext(options))
        {
            seed.PrefillCachedDepots.AddRange(
                NewCachedDepot(CachedAppId, CachedDepotId),
                NewCachedDepot(CachedAppId, CachedDepotId + 1),
                NewCachedDepot(OtherAppId, CachedDepotId + 2));
            await seed.SaveChangesAsync();
        }

        var (controller, recorder) = NewController(options);

        var response = await controller.ClearAppCacheAsync(CachedAppId);

        Assert.IsType<OkObjectResult>(response.Result);

        await using var verification = new AppDbContext(options);
        var remaining = await verification.PrefillCachedDepots
            .Select(depot => depot.AppId)
            .ToListAsync();

        Assert.Equal(OtherAppId, Assert.Single(remaining));
        Assert.Single(CacheChangeBroadcasts(recorder));
    }

    [Fact]
    public async Task ClearAppCache_AppOwnsNoRows_ReportsNothingRemoved()
    {
        var options = NewOptions();
        await using (var seed = new AppDbContext(options))
        {
            seed.PrefillCachedDepots.Add(NewCachedDepot(OtherAppId, CachedDepotId));
            await seed.SaveChangesAsync();
        }

        var (controller, _) = NewController(options);

        var response = await controller.ClearAppCacheAsync(CachedAppId);

        var body = Assert.IsType<PrefillCacheRemovalResponse>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, body.RemovedDepots);
    }

    [Fact]
    public async Task RecordCachedDepots_DepotTakenByAnotherWriter_StillRecordsTheRest()
    {
        var options = NewOptions();
        var service = new PrefillCacheService(
            new ConflictingSaveContextFactory(options, CachedDepotId),
            NullLogger<PrefillCacheService>.Instance);

        var recorded = await service.RecordCachedDepotsAsync(
            CachedAppId,
            "VTOL VR",
            [(CachedDepotId, CachedManifestId, 1L), (CachedDepotId + 1, CachedManifestId, 1L)],
            cachedBy: null);

        Assert.True(recorded);

        await using var verification = new AppDbContext(options);
        var depotIds = await verification.PrefillCachedDepots
            .Select(depot => depot.DepotId)
            .ToListAsync();

        Assert.Equal(CachedDepotId + 1, Assert.Single(depotIds));
    }

    [Fact]
    public async Task CompletedApp_NewDepot_AnnouncesTheCacheChange()
    {
        var (daemon, session, recorder) = NewDaemon(NewOptions());

        await InvokeCompletedAppAsync(daemon, session, "Success");

        Assert.Single(CacheChangeBroadcasts(recorder));
    }

    [Fact]
    public async Task CompletedApp_DepotAlreadyRecorded_AnnouncesNothing()
    {
        var options = NewOptions();
        await using (var seed = new AppDbContext(options))
        {
            seed.PrefillCachedDepots.Add(NewCachedDepot(CachedAppId, CachedDepotId));
            await seed.SaveChangesAsync();
        }

        var (daemon, session, recorder) = NewDaemon(options);

        await InvokeCompletedAppAsync(daemon, session, "AlreadyUpToDate");

        Assert.Empty(CacheChangeBroadcasts(recorder));
    }

    private static Task InvokeCompletedAppAsync(
        TestableSteamDaemonService daemon,
        DaemonSession session,
        string result)
        => DaemonTestMethods.InvokePrivateHandlerAsync(
            daemon,
            "NotifyPrefillProgressAsync",
            session,
            new PrefillProgress
            {
                State = "app_completed",
                Result = result,
                CurrentAppId = CachedAppId.ToString(CultureInfo.InvariantCulture),
                CurrentAppName = "VTOL VR",
                Depots =
                [
                    new DepotManifestProgressInfo
                    {
                        DepotId = CachedDepotId,
                        ManifestId = CachedManifestId,
                        TotalBytes = 1
                    }
                ]
            });

    private static List<(string Method, object?[] Args)> CacheChangeBroadcasts(
        RecordingNotificationProxy recorder)
        => recorder.Invocations
            .Where(invocation => invocation.Method == nameof(ISignalRNotificationService.NotifyAllAsync)
                && invocation.Args.Length > 0
                && (invocation.Args[0] as string) == SignalREvents.PrefillCacheChanged)
            .ToList();

    private static (PrefillAdminController Controller, RecordingNotificationProxy Recorder) NewController(
        DbContextOptions<AppDbContext> options)
    {
        var factory = new TestDbContextFactory(options);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();

        var controller = new PrefillAdminController(
            new PrefillSessionService(factory, NullLogger<PrefillSessionService>.Instance),
            steamDaemonService: null!,
            epicDaemonService: null!,
            battleNetDaemonService: null!,
            riotDaemonService: null!,
            xboxDaemonService: null!,
            cacheService: new PrefillCacheService(factory, NullLogger<PrefillCacheService>.Instance),
            notifications: notifications,
            NullLogger<PrefillAdminController>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["Session"] = new UserSession { Id = Guid.NewGuid() };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return (controller, (RecordingNotificationProxy)(object)notifications);
    }

    private static (TestableSteamDaemonService Daemon, DaemonSession Session, RecordingNotificationProxy Recorder)
        NewDaemon(DbContextOptions<AppDbContext> options)
    {
        var factory = new TestDbContextFactory(options);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();

        var daemon = new TestableSteamDaemonService(
            NullLogger<SteamDaemonService>.Instance,
            notifications,
            new ConfigurationBuilder().Build(),
            (IPathResolver)DispatchProxy.Create<IPathResolver, NullReturningProxy>(),
            (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>(),
            new PrefillSessionService(factory, NullLogger<PrefillSessionService>.Instance),
            new PrefillCacheService(factory, NullLogger<PrefillCacheService>.Instance),
            new StaticOptionsMonitor<PrefillNetworkOptions>(new PrefillNetworkOptions()));

        var session = new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = Guid.NewGuid(),
            Status = DaemonSessionStatus.Active,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        };
        daemon.InjectSession(session);

        return (daemon, session, (RecordingNotificationProxy)(object)notifications);
    }

    private static PrefillCachedDepot NewCachedDepot(long appId, long depotId) => new()
    {
        AppId = appId,
        DepotId = depotId,
        ManifestId = CachedManifestId,
        AppName = "VTOL VR",
        TotalBytes = 1,
        CachedAtUtc = DateTime.UtcNow
    };

    /// <summary>
    /// The in-memory provider does not enforce the unique index on <c>(DepotId, ManifestId)</c>, so
    /// the losing insert is raised here instead, on the one depot another writer already holds.
    /// </summary>
    private sealed class ConflictingSaveContextFactory(
        DbContextOptions<AppDbContext> options,
        long conflictDepotId) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new ConflictingSaveContext(options, conflictDepotId);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class ConflictingSaveContext(
        DbContextOptions<AppDbContext> options,
        long conflictDepotId) : AppDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (ChangeTracker.Entries<PrefillCachedDepot>()
                .Any(entry => entry.State == EntityState.Added && entry.Entity.DepotId == conflictDepotId))
            {
                throw new DbUpdateException("depot manifest already recorded");
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }

    private static DbContextOptions<AppDbContext> NewOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"prefill_cache_change_{Guid.NewGuid():N}")
            .Options;
}
