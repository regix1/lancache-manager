using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Coverage for <see cref="GameCacheDetectionDataService.RefreshDiskSummaryAsync"/>, which
/// re-aggregates the persisted summary without reading the cache directory. Detection measures
/// each row's size once and resolves which row owns a file shared by two rows; the refresh only
/// zeroes rows the eviction flow has marked evicted and sums what is left. Parameterized cases use
/// named (Blizzard/Riot/Xbox) rows per this file's precedent
/// (<see cref="XboxNamedDetectionEvictionTests"/>) - the named-game machinery has no per-service
/// branching, so xbox must behave identically to its siblings.
/// </summary>
public class GamesOnDiskCalculatorTests
{
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

    private static DbContextOptions<AppDbContext> NewInMemoryOptions()
        => new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"games_on_disk_{Guid.NewGuid():N}")
            .Options;

    private static GameCacheDetectionDataService NewDataService(DbContextOptions<AppDbContext> options)
        => new GameCacheDetectionDataService(
            new InMemoryDbContextFactory(options),
            NullLogger<GameCacheDetectionDataService>.Instance);

    private static CachedGameDetection NamedDetectionRow(
        string service,
        string gameName,
        int cacheFilesFound,
        ulong totalSizeBytes,
        bool isEvicted = false)
        => new CachedGameDetection
        {
            GameAppId = 0,
            EpicAppId = null,
            Service = service,
            GameName = gameName,
            CacheFilesFound = cacheFilesFound,
            TotalSizeBytes = totalSizeBytes,
            IsEvicted = isEvicted,
            LastDetectedUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        };

    // -----------------------------------------------------------------------------------------
    // The refresh contract: every non-evicted row keeps the size detection measured for it, an
    // evicted row drops to zero, and the summary is the sum of what remains. Games and services
    // are separate buckets whose sum is the identified total the dashboard reads.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task RefreshDiskSummaryAsync_SumsPersistedSizesAndZeroesEvictedRows()
    {
        var options = NewInMemoryOptions();

        await using (var seedContext = new AppDbContext(options))
        {
            seedContext.CachedGameDetections.Add(NamedDetectionRow(
                "xbox", "Halo Infinite",
                cacheFilesFound: 4,
                totalSizeBytes: 5_000UL));
            seedContext.CachedGameDetections.Add(NamedDetectionRow(
                "blizzard", "Overwatch",
                cacheFilesFound: 2,
                totalSizeBytes: 999UL));
            seedContext.CachedGameDetections.Add(NamedDetectionRow(
                "riot", "Valorant",
                cacheFilesFound: 5,
                totalSizeBytes: 500_000UL,
                isEvicted: true));
            seedContext.CachedServiceDetections.Add(new CachedServiceDetection
            {
                ServiceName = "wsus",
                CacheFilesFound = 1,
                TotalSizeBytes = 300UL,
                LastDetectedUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow
            });
            await seedContext.SaveChangesAsync();
        }

        var dataService = NewDataService(options);
        await dataService.RefreshDiskSummaryAsync();

        await using var verifyContext = new AppDbContext(options);
        var evictedRow = await verifyContext.CachedGameDetections.SingleAsync(g => g.IsEvicted);
        Assert.Equal(0UL, evictedRow.TotalSizeBytes);

        var summary = await verifyContext.CachedDetectionSummaries.SingleAsync();
        Assert.Equal(5_999UL, summary.GamesOnDiskBytes);
        Assert.Equal(2, summary.GamesOnDiskCount);
        Assert.Equal(300UL, summary.IdentifiedServiceBytes);
        Assert.Equal(1, summary.IdentifiedServiceCount);
        Assert.Equal(6_299UL, summary.IdentifiedCacheBytes);
    }

    // -----------------------------------------------------------------------------------------
    // A row that matched cache files but was sized at zero bytes claimed nothing of its own -
    // another row already owned every slice it matched. It stays in the list with its count, and
    // it must not inflate the active-game count the dashboard shows.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task RefreshDiskSummaryAsync_ZeroByteNonEvictedGame_IsNotCountedAsActive()
    {
        var options = NewInMemoryOptions();

        await using (var seedContext = new AppDbContext(options))
        {
            seedContext.CachedGameDetections.Add(NamedDetectionRow(
                "xbox", "Owning Launcher Payload",
                cacheFilesFound: 1,
                totalSizeBytes: 4_096UL));
            seedContext.CachedGameDetections.Add(NamedDetectionRow(
                "xbox", "Title Sharing The Payload",
                cacheFilesFound: 1,
                totalSizeBytes: 0UL));
            await seedContext.SaveChangesAsync();
        }

        var dataService = NewDataService(options);
        await dataService.RefreshDiskSummaryAsync();

        await using var verifyContext = new AppDbContext(options);
        var summary = await verifyContext.CachedDetectionSummaries.SingleAsync();

        Assert.Equal(4_096UL, summary.GamesOnDiskBytes);
        Assert.Equal(1, summary.GamesOnDiskCount);
    }

    [Fact]
    public async Task LoadDetectionAsync_MissingSummaryWithDetectionRows_RebuildsBeforeResponse()
    {
        var options = NewInMemoryOptions();
        const ulong persistedBytes = 4_096;

        await using (var seedContext = new AppDbContext(options))
        {
            seedContext.CachedGameDetections.Add(NamedDetectionRow(
                "riot",
                "Existing Detection",
                cacheFilesFound: 1,
                totalSizeBytes: persistedBytes));
            await seedContext.SaveChangesAsync();
        }

        var dataService = NewDataService(options);
        var response = await dataService.LoadDetectionAsync();

        Assert.NotNull(response);
        Assert.NotNull(response.DiskSummary);
        Assert.Equal(persistedBytes, response.DiskSummary.Value.GameBytes);
        Assert.Equal(1, response.DiskSummary.Value.ActiveGameCount);
        Assert.NotNull(response.SummaryComputedAtUtc);

        await using var verifyContext = new AppDbContext(options);
        var summary = await verifyContext.CachedDetectionSummaries.SingleAsync();
        Assert.Equal(persistedBytes, summary.GamesOnDiskBytes);
    }

    // -----------------------------------------------------------------------------------------
    // Regression lock. Evicted rows force-zero regardless of persisted size.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task RefreshDiskSummaryAsync_EvictedNamedGame_StillZeroed()
    {
        var options = NewInMemoryOptions();

        await using (var seedContext = new AppDbContext(options))
        {
            seedContext.CachedGameDetections.Add(NamedDetectionRow(
                "xbox", "Halo Infinite",
                cacheFilesFound: 5,
                totalSizeBytes: 500_000UL,
                isEvicted: true));
            await seedContext.SaveChangesAsync();
        }

        var dataService = NewDataService(options);
        await dataService.RefreshDiskSummaryAsync();

        await using var verifyContext = new AppDbContext(options);
        var row = await verifyContext.CachedGameDetections.SingleAsync();

        Assert.True(row.IsEvicted);
        Assert.Equal(0UL, row.TotalSizeBytes);
    }
}
