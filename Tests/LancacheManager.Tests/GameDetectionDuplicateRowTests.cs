using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// One game keeps one detection row. The unique index cannot enforce that for Steam rows (two
/// (appId, NULL) rows are distinct to PostgreSQL), so the writers that check for a row and then insert
/// one must not interleave, and a pair that is already stored must not make every later detection fail.
/// </summary>
public class GameDetectionDuplicateRowTests
{
    /// <summary>Runs an evicted-row recovery the first time a save begins, waiting for it a bounded time.</summary>
    private sealed class RecoverBeforeFirstSave : SaveChangesInterceptor
    {
        private readonly Func<GameCacheDetectionDataService, Task<int>> _recover;
        private bool _started;

        public RecoverBeforeFirstSave(Func<GameCacheDetectionDataService, Task<int>> recover)
        {
            _recover = recover;
        }

        public GameCacheDetectionDataService? Service { get; set; }

        public Task<int>? Recovery { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData saving,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_started)
            {
                _started = true;
                Recovery = _recover(Service!);
                await Task.WhenAny(Recovery, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
            }

            return result;
        }
    }

    private static CachedGameDetection NewRow(long gameAppId, string gameName, string service, string? epicAppId, bool isEvicted) => new()
    {
        GameAppId = gameAppId,
        GameName = gameName,
        Service = service,
        EpicAppId = epicAppId,
        IsEvicted = isEvicted,
        CacheFilesFound = isEvicted ? 0 : 1,
        TotalSizeBytes = isEvicted ? 0UL : 100UL
    };

    private static GameCacheInfo NewGame(long gameAppId, string gameName, string service, string? epicAppId) => new()
    {
        GameAppId = gameAppId,
        GameName = gameName,
        Service = service,
        EpicAppId = epicAppId,
        CacheFilesFound = 5,
        TotalSizeBytes = 5000,
        Datasources = new List<string> { "default" }
    };

    private static Download NewDownload(long gameAppId, string gameName, long? depotId, bool isEvicted) => new()
    {
        Service = "steam",
        ClientIp = "10.0.0.5",
        StartTimeUtc = DateTime.UtcNow,
        EndTimeUtc = DateTime.UtcNow,
        CacheHitBytes = 1000,
        CacheMissBytes = 0,
        IsActive = false,
        IsEvicted = isEvicted,
        GameAppId = gameAppId,
        GameName = gameName,
        DepotId = depotId,
        Datasource = "default"
    };

    [Fact]
    public async Task SaveGamesAsync_StoredDuplicateRows_KeepsOneRowPerGameAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        const long epicGameAppId = 2_147_483_800;
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.CachedGameDetections.AddRange(
                NewRow(600, "Steam Game", "steam", null, isEvicted: true),
                NewRow(600, "Steam Game", "steam", null, isEvicted: false),
                NewRow(0, "Epic Game", "epicgames", "epic-dup", isEvicted: true),
                NewRow(epicGameAppId, "Epic Game", "epicgames", "epic-dup", isEvicted: false),
                NewRow(0, "Named Game", "blizzard", null, isEvicted: true),
                NewRow(0, "Named Game", "blizzard", null, isEvicted: false));
            await seed.SaveChangesAsync();
        }

        var service = new GameCacheDetectionDataService(
            database.Factory,
            NullLogger<GameCacheDetectionDataService>.Instance);

        var failure = await Record.ExceptionAsync(() => service.SaveGamesAsync(
            new List<GameCacheInfo>
            {
                NewGame(600, "Steam Game", "steam", null),
                NewGame(epicGameAppId, "Epic Game", "epicgames", "epic-dup"),
                NewGame(0, "Named Game", "blizzard", null)
            },
            incremental: true));

        Assert.Null(failure);

        await using var check = database.Factory.CreateDbContext();
        var steamRows = await check.CachedGameDetections.Where(g => g.GameAppId == 600).ToListAsync();
        var epicRows = await check.CachedGameDetections.Where(g => g.EpicAppId == "epic-dup").ToListAsync();
        var namedRows = await check.CachedGameDetections
            .Where(g => g.GameAppId == 0 && g.EpicAppId == null && g.GameName == "Named Game")
            .ToListAsync();
        Assert.All(new[] { steamRows, epicRows, namedRows }, rows =>
        {
            var row = Assert.Single(rows);
            Assert.False(row.IsEvicted);
            Assert.Equal(5000UL, row.TotalSizeBytes);
        });
    }

    [Fact]
    public async Task SaveGamesAsync_RecoveryDuringTheSave_StoresOneRowAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.Downloads.Add(NewDownload(500, "Recovered Game", depotId: null, isEvicted: true));
            await seed.SaveChangesAsync();
        }

        var interceptor = new RecoverBeforeFirstSave(
            detection => detection.RecoverEvictedGamesAsync(CancellationToken.None));
        var interceptedOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;
        var service = new GameCacheDetectionDataService(
            new TestDbContextFactory(interceptedOptions),
            NullLogger<GameCacheDetectionDataService>.Instance);
        interceptor.Service = service;

        await service.SaveGamesAsync(
            new List<GameCacheInfo> { NewGame(500, "Recovered Game", "steam", null) },
            incremental: true);
        Assert.NotNull(interceptor.Recovery);
        await interceptor.Recovery!.WaitAsync(TimeSpan.FromSeconds(30));

        await using var check = database.Factory.CreateDbContext();
        var row = Assert.Single(await check.CachedGameDetections.Where(g => g.GameAppId == 500).ToListAsync());
        Assert.False(row.IsEvicted);
    }

    [Fact]
    public async Task ResolveUnknownGamesAsync_StoredEvictedDuplicatePair_ResolvesAndKeepsOneRowAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.CachedGameDetections.AddRange(
                NewRow(700, "Resolved Game", "steam", null, isEvicted: true),
                NewRow(700, "Resolved Game", "steam", null, isEvicted: true),
                new CachedGameDetection
                {
                    GameAppId = 7_001_001,
                    GameName = "Unknown Game (Depot 7001001)",
                    Service = "steam",
                    IsUnresolvedDepot = true,
                    CacheFilesFound = 3,
                    TotalSizeBytes = 300
                });
            seed.Downloads.Add(NewDownload(700, "Resolved Game", depotId: 7_001_001, isEvicted: false));
            await seed.SaveChangesAsync();
        }

        var service = new UnknownGameResolutionService(
            database.Factory,
            NullLogger<UnknownGameResolutionService>.Instance);

        var resolved = await service.ResolveUnknownGamesAsync();

        Assert.Equal(1, resolved);
        await using var check = database.Factory.CreateDbContext();
        var row = Assert.Single(await check.CachedGameDetections.Where(g => g.GameAppId == 700).ToListAsync());
        Assert.Equal(300UL, row.TotalSizeBytes);
        Assert.False(await check.CachedGameDetections.AnyAsync(g => g.GameAppId == 7_001_001));
    }

    private static CachedGameDetection NewUnknownDepotRow(long depotId, ulong totalSizeBytes) => new()
    {
        GameAppId = depotId,
        GameName = $"Unknown Game (Depot {depotId})",
        Service = "steam",
        IsUnresolvedDepot = true,
        CacheFilesFound = 3,
        TotalSizeBytes = totalSizeBytes
    };

    [Fact]
    public async Task ResolveUnknownGamesAsync_EvictedLowerIdBesideLiveRow_MergesIntoTheLiveRowAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.CachedGameDetections.Add(NewRow(710, "Live Game", "steam", null, isEvicted: true));
            await seed.SaveChangesAsync();
            seed.CachedGameDetections.Add(NewRow(710, "Live Game", "steam", null, isEvicted: false));
            seed.CachedGameDetections.Add(NewUnknownDepotRow(7_101_001, 300));
            seed.Downloads.Add(NewDownload(710, "Live Game", depotId: 7_101_001, isEvicted: false));
            await seed.SaveChangesAsync();
        }

        var service = new UnknownGameResolutionService(
            database.Factory,
            NullLogger<UnknownGameResolutionService>.Instance);

        Assert.Equal(1, await service.ResolveUnknownGamesAsync());

        await using var check = database.Factory.CreateDbContext();
        var row = Assert.Single(await check.CachedGameDetections.Where(g => g.GameAppId == 710).ToListAsync());
        Assert.False(row.IsEvicted);
        Assert.Equal(400UL, row.TotalSizeBytes);
    }

    [Fact]
    public async Task ResolveUnknownGamesAsync_UnknownDepotNumberedLikeAnApp_KeepsBothRowsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.CachedGameDetections.Add(NewUnknownDepotRow(800, 50));
            await seed.SaveChangesAsync();
            seed.CachedGameDetections.Add(NewRow(800, "App Eight Hundred", "steam", null, isEvicted: false));
            seed.CachedGameDetections.Add(NewUnknownDepotRow(8_001_001, 300));
            seed.Downloads.Add(NewDownload(800, "App Eight Hundred", depotId: 8_001_001, isEvicted: false));
            await seed.SaveChangesAsync();
        }

        var service = new UnknownGameResolutionService(
            database.Factory,
            NullLogger<UnknownGameResolutionService>.Instance);

        await service.ResolveUnknownGamesAsync();

        await using var check = database.Factory.CreateDbContext();
        var rows = await check.CachedGameDetections.Where(g => g.GameAppId == 800).ToListAsync();
        var app = Assert.Single(rows, g => g.IsUnresolvedDepot != true);
        Assert.Equal("App Eight Hundred", app.GameName);
        Assert.Equal(400UL, app.TotalSizeBytes);
        var depot = Assert.Single(rows, g => g.IsUnresolvedDepot == true);
        Assert.Equal(50UL, depot.TotalSizeBytes);
    }

    [Fact]
    public async Task SaveGamesAsync_UnknownDepotNumberedLikeAnApp_KeepsBothRowsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.CachedGameDetections.Add(NewUnknownDepotRow(810, 50));
            await seed.SaveChangesAsync();
            seed.CachedGameDetections.Add(NewRow(810, "App Eight Ten", "steam", null, isEvicted: false));
            await seed.SaveChangesAsync();
        }

        var service = new GameCacheDetectionDataService(
            database.Factory,
            NullLogger<GameCacheDetectionDataService>.Instance);

        var unknownDepot = NewGame(810, "Unknown Game (Depot 810)", "steam", null);
        unknownDepot.IsUnresolvedDepot = true;
        unknownDepot.TotalSizeBytes = 60;
        await service.SaveGamesAsync(
            new List<GameCacheInfo> { NewGame(810, "App Eight Ten", "steam", null), unknownDepot },
            incremental: false);

        await using var check = database.Factory.CreateDbContext();
        var rows = await check.CachedGameDetections.Where(g => g.GameAppId == 810).ToListAsync();
        var app = Assert.Single(rows, g => g.IsUnresolvedDepot != true);
        Assert.Equal("App Eight Ten", app.GameName);
        Assert.Equal(5000UL, app.TotalSizeBytes);
        var depot = Assert.Single(rows, g => g.IsUnresolvedDepot == true);
        Assert.Equal(60UL, depot.TotalSizeBytes);
    }

    [Fact]
    public async Task SaveGamesAsync_FullScanWithoutTheUnknownDepot_DeletesOnlyThatRowAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.CachedGameDetections.Add(NewUnknownDepotRow(830, 50));
            seed.CachedGameDetections.Add(NewUnknownDepotRow(831, 70));
            await seed.SaveChangesAsync();
            seed.CachedGameDetections.Add(NewRow(830, "App Eight Thirty", "steam", null, isEvicted: false));
            seed.CachedGameDetections.Add(NewRow(831, "App Eight Thirty One", "steam", null, isEvicted: false));
            await seed.SaveChangesAsync();
        }

        var service = new GameCacheDetectionDataService(
            database.Factory,
            NullLogger<GameCacheDetectionDataService>.Instance);

        await service.SaveGamesAsync(
            new List<GameCacheInfo>
            {
                NewGame(830, "App Eight Thirty", "steam", null),
                NewGame(831, "App Eight Thirty One", "steam", null)
            },
            incremental: false);

        await using var check = database.Factory.CreateDbContext();
        var rows = await check.CachedGameDetections
            .Where(g => g.GameAppId == 830 || g.GameAppId == 831)
            .OrderBy(g => g.GameAppId)
            .ToListAsync();
        Assert.Equal(
            new[] { "App Eight Thirty", "App Eight Thirty One" },
            rows.Select(g => g.GameName).ToArray());
    }

    [Fact]
    public async Task SaveServicesAsync_RecoveryDuringTheSave_StoresOneRowAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.Downloads.Add(new Download
            {
                Service = "wsus",
                ClientIp = "10.0.0.5",
                StartTimeUtc = DateTime.UtcNow,
                EndTimeUtc = DateTime.UtcNow,
                CacheHitBytes = 1000,
                CacheMissBytes = 0,
                IsActive = false,
                IsEvicted = true,
                Datasource = "default"
            });
            await seed.SaveChangesAsync();
        }

        var interceptor = new RecoverBeforeFirstSave(
            detection => detection.RecoverEvictedServicesAsync(CancellationToken.None));
        var interceptedOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;
        var service = new GameCacheDetectionDataService(
            new TestDbContextFactory(interceptedOptions),
            NullLogger<GameCacheDetectionDataService>.Instance);
        interceptor.Service = service;

        var failure = await Record.ExceptionAsync(() => service.SaveServicesAsync(
            new List<ServiceCacheInfo>
            {
                new() { ServiceName = "wsus", CacheFilesFound = 4, TotalSizeBytes = 4000, Datasources = new List<string> { "default" } }
            },
            incremental: true));

        Assert.Null(failure);
        Assert.NotNull(interceptor.Recovery);
        await interceptor.Recovery!.WaitAsync(TimeSpan.FromSeconds(30));

        await using var check = database.Factory.CreateDbContext();
        var row = Assert.Single(await check.CachedServiceDetections.Where(s => s.ServiceName == "wsus").ToListAsync());
        Assert.False(row.IsEvicted);
        Assert.Equal(4000UL, row.TotalSizeBytes);
    }
}
