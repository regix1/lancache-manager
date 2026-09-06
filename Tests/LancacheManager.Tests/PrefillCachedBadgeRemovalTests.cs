using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

/// <summary>
/// The prefill game picker's "Cached" badge is a stored row in <c>PrefillCachedDepots</c>, not a
/// live look at the disk, so a removal that deletes the files behind it has to delete the row too.
/// A game removed from Game Cache Detection kept reading "Cached" on the prefill page, and the
/// daemon skips a game it believes is cached, so the user sees a prefill run that appears to do
/// nothing. The cache clear already did this; the game, service and eviction removals now match it.
/// </summary>
public sealed class PrefillCachedBadgeRemovalTests
{
    private const long RemovedAppId = 251570;
    private const long KeptAppId = 730;

    /// <summary>
    /// The predicate the single-entity eviction removal runs for <c>EvictionScope.Steam</c>.
    /// </summary>
    [Fact]
    public async Task SteamScopedRemoval_TakesOnlyTheRemovedAppsBadgeRowsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();

        await using (var setup = new AppDbContext(database.Options))
        {
            setup.PrefillCachedDepots.AddRange(
                NewCachedDepot(RemovedAppId, depotId: 251571),
                NewCachedDepot(RemovedAppId, depotId: 251572),
                NewCachedDepot(KeptAppId, depotId: 731));
            await setup.SaveChangesAsync();
        }

        await using (var context = new AppDbContext(database.Options))
        {
            var deleted = await context.PrefillCachedDepots
                .Where(depot => depot.AppId == RemovedAppId)
                .ExecuteDeleteAsync();

            Assert.Equal(2, deleted);
        }

        await using (var assertContext = new AppDbContext(database.Options))
        {
            var remaining = await assertContext.PrefillCachedDepots
                .Select(depot => depot.AppId)
                .ToListAsync();

            Assert.Equal(KeptAppId, Assert.Single(remaining));
        }
    }

    /// <summary>
    /// The predicate the bulk eviction removal runs: badge rows go for every app whose downloads
    /// are being deleted because their cache files are gone, and a game still holding files keeps
    /// its badge.
    /// </summary>
    [Fact]
    public async Task BulkEvictionRemoval_TakesTheBadgeRowsOfEvictedGamesOnlyAsync()
    {
        await using var database = await TestDatabase.CreateAsync();

        await using (var setup = new AppDbContext(database.Options))
        {
            setup.Downloads.AddRange(
                NewDownload(RemovedAppId, isEvicted: true),
                NewDownload(KeptAppId, isEvicted: false));
            setup.PrefillCachedDepots.AddRange(
                NewCachedDepot(RemovedAppId, depotId: 251571),
                NewCachedDepot(KeptAppId, depotId: 731));
            await setup.SaveChangesAsync();
        }

        await using (var context = new AppDbContext(database.Options))
        {
            var evictedGameAppIds = await context.Downloads
                .Where(d => d.IsEvicted && d.GameAppId != null && d.GameAppId > 0)
                .Select(d => d.GameAppId!.Value)
                .Distinct()
                .ToListAsync();

            var deleted = await context.PrefillCachedDepots
                .Where(depot => evictedGameAppIds.Contains(depot.AppId))
                .ExecuteDeleteAsync();

            Assert.Equal(1, deleted);
        }

        await using (var assertContext = new AppDbContext(database.Options))
        {
            var remaining = await assertContext.PrefillCachedDepots
                .Select(depot => depot.AppId)
                .ToListAsync();

            Assert.Equal(KeptAppId, Assert.Single(remaining));
        }
    }

    /// <summary>
    /// The removals above run inside methods that drive Rust binaries and a full operation
    /// lifecycle, so the queries are exercised directly. These read the sources instead, to catch
    /// the badge cleanup being dropped out of a removal path again.
    /// </summary>
    [Theory]
    [InlineData("CacheManagementService.SteamRemoval.cs")]
    [InlineData("CacheManagementService.ServiceRemoval.cs")]
    [InlineData("CacheReconciliationService.cs")]
    [InlineData("CacheClearingService.cs")]
    public void RemovalPath_ClearsTheBadgeRowsAndAnnouncesTheChange(string fileName)
    {
        var source = ReadCacheServiceSource(fileName);

        Assert.Contains("PrefillCachedDepots", source, StringComparison.Ordinal);
        Assert.Contains("SignalREvents.PrefillCacheChanged", source, StringComparison.Ordinal);
    }

    private static Download NewDownload(long gameAppId, bool isEvicted) => new()
    {
        Service = "steam",
        ClientIp = gameAppId.ToString(),
        Datasource = "Default",
        GameAppId = gameAppId,
        StartTimeUtc = DateTime.UtcNow.AddMinutes(-1),
        EndTimeUtc = DateTime.UtcNow,
        CacheHitBytes = 1024,
        IsEvicted = isEvicted
    };

    private static PrefillCachedDepot NewCachedDepot(long appId, long depotId) => new()
    {
        AppId = appId,
        DepotId = depotId,
        ManifestId = (ulong)depotId,
        AppName = "7 Days to Die",
        TotalBytes = 1024,
        CachedAtUtc = DateTime.UtcNow
    };

    private static string ReadCacheServiceSource(string fileName) =>
        File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Api",
            "LancacheManager",
            "Core",
            "Services",
            "Cache",
            fileName));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Web")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
    }
}
