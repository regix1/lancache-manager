using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A detection save loads the rows it will update, then writes them. A row deleted in between
/// (the scheduled cleanup drops nameless rows without waiting for detection) must cost only that
/// row: the rest of the scan is still stored and the save does not throw, because a throw here
/// ends the whole detection run as failed with nothing stored.
/// </summary>
public class GameDetectionSaveDeletedRowTests
{
    /// <summary>Deletes one detection row from a second connection the first time a save begins.</summary>
    private sealed class DeleteRowBeforeFirstSave(TestDatabase database, long gameAppId) : SaveChangesInterceptor
    {
        private bool _deleted;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData saving,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_deleted)
            {
                _deleted = true;
                await using var context = database.Factory.CreateDbContext();
                await context.CachedGameDetections
                    .Where(g => g.GameAppId == gameAppId)
                    .ExecuteDeleteAsync(cancellationToken);
            }

            return result;
        }
    }

    /// <summary>Stores a row with the same unique key from a second connection the first time a save begins.</summary>
    private sealed class InsertClashingRowBeforeFirstSave(TestDatabase database, CachedGameDetection clashing) : SaveChangesInterceptor
    {
        private bool _inserted;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData saving,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_inserted)
            {
                _inserted = true;
                await using var context = database.Factory.CreateDbContext();
                context.CachedGameDetections.Add(clashing);
                await context.SaveChangesAsync(cancellationToken);
            }

            return result;
        }
    }

    [Fact]
    public async Task SaveGamesAsync_UniqueKeyClash_FailsInsteadOfDroppingTheScanAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        const long epicGameAppId = 2_147_483_700;
        var interceptedOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(new InsertClashingRowBeforeFirstSave(database, new CachedGameDetection
            {
                GameAppId = epicGameAppId,
                GameName = "Epic Game",
                Service = "epicgames",
                EpicAppId = "epic-1",
                CacheFilesFound = 1,
                TotalSizeBytes = 100
            }))
            .Options;
        var service = new GameCacheDetectionDataService(
            new TestDbContextFactory(interceptedOptions),
            NullLogger<GameCacheDetectionDataService>.Instance);

        var failure = await Record.ExceptionAsync(() => service.SaveGamesAsync(
            new List<GameCacheInfo>
            {
                new()
                {
                    GameAppId = epicGameAppId,
                    GameName = "Epic Game",
                    Service = "epicgames",
                    EpicAppId = "epic-1",
                    CacheFilesFound = 5,
                    TotalSizeBytes = 5000,
                    Datasources = new List<string> { "default" }
                },
                NewGame(30, "New Game")
            },
            incremental: true));

        // The save is one transaction, so nothing of the scan is stored; the detection has to fail
        // rather than report a scan whose rows were all rolled back.
        Assert.IsAssignableFrom<DbUpdateException>(failure);
        await using var check = database.Factory.CreateDbContext();
        Assert.False(await check.CachedGameDetections.AnyAsync(g => g.GameAppId == 30));
    }

    private static CachedGameDetection NewRow(long gameAppId, string gameName) => new()
    {
        GameAppId = gameAppId,
        GameName = gameName,
        Service = "steam",
        CacheFilesFound = 1,
        TotalSizeBytes = 100
    };

    private static GameCacheInfo NewGame(long gameAppId, string gameName) => new()
    {
        GameAppId = gameAppId,
        GameName = gameName,
        Service = "steam",
        CacheFilesFound = 5,
        TotalSizeBytes = 5000,
        Datasources = new List<string> { "default" }
    };

    [Fact]
    public async Task SaveGamesAsync_RowDeletedBeforeTheSave_StoresTheRestOfTheScanAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.CachedGameDetections.AddRange(NewRow(10, "Deleted Game"), NewRow(20, "Kept Game"));
            await seed.SaveChangesAsync();
        }

        var interceptedOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(new DeleteRowBeforeFirstSave(database, gameAppId: 10))
            .Options;
        var service = new GameCacheDetectionDataService(
            new TestDbContextFactory(interceptedOptions),
            NullLogger<GameCacheDetectionDataService>.Instance);

        var failure = await Record.ExceptionAsync(() => service.SaveGamesAsync(
            new List<GameCacheInfo>
            {
                NewGame(10, "Deleted Game"),
                NewGame(20, "Kept Game"),
                NewGame(30, "New Game")
            },
            incremental: true));

        Assert.Null(failure);

        await using var check = database.Factory.CreateDbContext();
        var rows = await check.CachedGameDetections.OrderBy(g => g.GameAppId).ToListAsync();
        Assert.Equal(new long[] { 20, 30 }, rows.Select(g => g.GameAppId).ToArray());
        Assert.All(rows, row => Assert.Equal(5000UL, row.TotalSizeBytes));
    }
}
