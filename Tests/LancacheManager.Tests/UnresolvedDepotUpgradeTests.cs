using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace LancacheManager.Tests;

public sealed class UnresolvedDepotUpgradeTests
{
    // Rows written before the unresolved-depot flag existed carry only the placeholder name. Upgrading
    // sets the flag on them once, evicted rows included, so every reader can go by the flag alone.
    [Fact]
    public async Task UpgradeFlagsStoredPlaceholderRowsAndLaterSavesKeepOneRowEach()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await using var existing = new AppDbContext(fixture.Options);
        // A scratch database with a long command timeout: CREATE DATABASE waits for a checkpoint.
        var connection = new NpgsqlConnectionStringBuilder(existing.Database.GetConnectionString())
        {
            Database = $"lancache_migration_tests_{Guid.NewGuid():N}",
            SearchPath = "public",
            Pooling = false,
            CommandTimeout = 120
        };
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection.ConnectionString, npgsql =>
                npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null)).Options;
        await using var context = new AppDbContext(options);
        try
        {
            await context.GetService<IMigrator>().MigrateAsync("20261109200000_AddScheduleExecutionFailureReason");
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"CachedGameDetections\" (\"GameAppId\", \"GameName\", \"CacheFilesFound\", \"TotalSizeBytes\", " +
                "\"DepotIdsJson\", \"SampleUrlsJson\", \"DatasourcesJson\", \"Service\", \"IsEvicted\", \"LastDetectedUtc\", \"CreatedAtUtc\") VALUES " +
                "(9001, 'Unknown Game (Depot 9001)', 3, 300, '[9001]', '[]', '[\"default\"]', 'steam', false, now(), now()), " +
                "(9002, 'Unknown Game (Depot 9002)', 0, 0, '[9002]', '[]', '[\"default\"]', 'steam', true, now(), now()), " +
                "(9003, 'Real Game', 2, 200, '[9004]', '[]', '[\"default\"]', 'steam', false, now(), now())");

            await context.Database.MigrateAsync();

            var rows = await context.CachedGameDetections.AsNoTracking().OrderBy(g => g.GameAppId).ToListAsync();
            Assert.Equal(new long[] { 9001, 9002, 9003 }, rows.Select(g => g.GameAppId).ToArray());
            Assert.True(rows[0].IsUnresolvedDepot);
            Assert.True(rows[1].IsUnresolvedDepot);
            Assert.False(rows[2].IsUnresolvedDepot);
            Assert.Equal("NO", await context.Database.SqlQueryRaw<string>(
                "SELECT is_nullable AS \"Value\" FROM information_schema.columns " +
                "WHERE table_name = 'CachedGameDetections' AND column_name = 'IsUnresolvedDepot'").SingleAsync());

            var unknown = await GameCacheDetectionService.GetUnknownGamesCachedAsync(context);
            Assert.Equal(new long[] { 9001, 9002 }, unknown.Select(g => g.GameAppId).OrderBy(id => id).ToArray());

            // An incremental detection merges the stored rows back in; each must match its own row.
            var service = new GameCacheDetectionDataService(
                new TestDbContextFactory(options),
                NullLogger<GameCacheDetectionDataService>.Instance);
            await service.SaveGamesAsync(rows.Select(service.ToGameCacheInfo).ToList(), incremental: true);

            await using var check = new AppDbContext(options);
            var saved = await check.CachedGameDetections.AsNoTracking().OrderBy(g => g.GameAppId).ToListAsync();
            Assert.Equal(new long[] { 9001, 9002, 9003 }, saved.Select(g => g.GameAppId).ToArray());
            Assert.Equal(new[] { true, true, false }, saved.Select(g => g.IsUnresolvedDepot == true).ToArray());
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }
}
