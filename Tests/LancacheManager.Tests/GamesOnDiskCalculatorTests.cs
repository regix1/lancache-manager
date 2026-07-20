using System.Text.Json;
using LancacheManager.Core;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Coverage for the "0 B despite nonzero CacheFilesFound" bug: <see cref="GamesOnDiskCalculator"/>
/// drops a game's key from <c>GameBytesByKey</c> whenever none of its persisted cache paths
/// resolve on disk at refresh time (contributedBytes == 0), and
/// <see cref="GameCacheDetectionDataService.RefreshDiskSummaryAsync"/> used to read that absence
/// as "0 bytes" and clobber the last Rust-computed <c>TotalSizeBytes</c>. Parameterized over
/// xbox/blizzard/riot per this test file's precedent (<see cref="XboxNamedDetectionEvictionTests"/>)
/// - the named-game machinery has no per-service branching, so xbox must behave identically to
/// its siblings.
/// </summary>
public class GamesOnDiskCalculatorTests : IDisposable
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

    private readonly List<string> _tempFiles = new();

    private string CreateTempCacheFile(int byteCount)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gamesondisk_test_{Guid.NewGuid():N}.cache");
        File.WriteAllBytes(path, new byte[byteCount]);
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    private static CachedGameDetection NamedDetectionRow(
        string service,
        string gameName,
        int cacheFilesFound,
        ulong totalSizeBytes,
        List<string> cacheFilePaths,
        bool isEvicted = false)
        => new CachedGameDetection
        {
            GameAppId = 0,
            EpicAppId = null,
            Service = service,
            GameName = gameName,
            CacheFilesFound = cacheFilesFound,
            TotalSizeBytes = totalSizeBytes,
            CacheFilePathsJson = JsonSerializer.Serialize(cacheFilePaths),
            IsEvicted = isEvicted,
            LastDetectedUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        };

    // -----------------------------------------------------------------------------------------
    // Criterion 6a: attribution against real on-disk files yields a present, positive keyed entry
    // for the named branch, across all three named services (no per-service hardcoding).
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("xbox")]
    [InlineData("blizzard")]
    [InlineData("riot")]
    public void ComputeAttributedCacheFromDisk_NamedGame_RealFiles_YieldsPositiveKeyedBytes(string service)
    {
        var path1 = CreateTempCacheFile(1024);
        var path2 = CreateTempCacheFile(2048);
        const string gameName = "Some Named Title";

        var game = new GameCacheInfo
        {
            GameAppId = 0,
            Service = service,
            GameName = gameName,
            EpicAppId = null,
            CacheFilePaths = new List<string> { path1, path2 }
        };

        var attributed = GamesOnDiskCalculator.ComputeAttributedCacheFromDisk(
            new List<GameCacheInfo> { game },
            new List<ServiceCacheInfo>());

        var key = $"named:{service.ToLowerInvariant()}\x01{gameName}";
        Assert.True(attributed.GameBytesByKey.TryGetValue(key, out var bytes));
        Assert.Equal(3072UL, bytes);
    }

    // -----------------------------------------------------------------------------------------
    // Criterion 6b: reproducing round-trip test. Against the unfixed
    // RefreshDiskSummaryAsync (bare TryGetValue(...) ?? 0), this FAILS because TotalSizeBytes
    // is clobbered to 0. After the fix, a non-evicted game with CacheFilesFound > 0 whose paths
    // don't resolve on disk right now retains its persisted (Rust-computed) size.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task RefreshDiskSummaryAsync_NamedGame_PathsMissingFromDisk_RetainsPersistedSize()
    {
        var options = NewInMemoryOptions();
        var missingPath = Path.Combine(Path.GetTempPath(), $"gamesondisk_missing_{Guid.NewGuid():N}.cache");
        // Deliberately never created on disk - simulates a cache file no longer present
        // (e.g. reclaimed) by the time this refresh runs, even though the game was detected
        // with real files moments earlier and is not itself evicted.

        await using (var seedContext = new AppDbContext(options))
        {
            seedContext.CachedGameDetections.Add(NamedDetectionRow(
                "xbox", "Minecraft Launcher",
                cacheFilesFound: 11,
                totalSizeBytes: 10_378_000UL,
                cacheFilePaths: new List<string> { missingPath }));
            await seedContext.SaveChangesAsync();
        }

        var dataService = NewDataService(options);
        await dataService.RefreshDiskSummaryAsync();

        await using var verifyContext = new AppDbContext(options);
        var row = await verifyContext.CachedGameDetections.SingleAsync();

        Assert.False(row.IsEvicted);
        Assert.Equal(11, row.CacheFilesFound);
        Assert.Equal(10_378_000UL, row.TotalSizeBytes);

        // The persisted CachedDetectionSummary aggregate must include the retained bytes -
        // otherwise the dashboard total and the per-row list disagree.
        var summary = await verifyContext.CachedDetectionSummaries.SingleAsync();
        Assert.Equal(10_378_000UL, summary.GamesOnDiskBytes);
        Assert.Equal(1, summary.GamesOnDiskCount);
    }

    [Fact]
    public async Task LoadDetectionAsync_MissingSummaryWithDetectionRows_RebuildsBeforeResponse()
    {
        var options = NewInMemoryOptions();
        const ulong persistedBytes = 4_096;
        var missingPath = Path.Combine(Path.GetTempPath(), $"gamesondisk_backfill_{Guid.NewGuid():N}.cache");

        await using (var seedContext = new AppDbContext(options))
        {
            seedContext.CachedGameDetections.Add(NamedDetectionRow(
                "riot",
                "Existing Detection",
                cacheFilesFound: 1,
                totalSizeBytes: persistedBytes,
                cacheFilePaths: new List<string> { missingPath }));
            await seedContext.SaveChangesAsync();
        }

        var dataService = NewDataService(options);
        var response = await dataService.LoadDetectionAsync(includeCacheFilePaths: false);

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
    // Retention must not double-count when a game's paths were already claimed by an earlier
    // active game/service in the same refresh (e.g. two named games sharing a common launcher/
    // redistributable cache file). The claimant's bytes are already in the aggregate, so the
    // second game's stale persisted size must be zeroed, not added on top.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task RefreshDiskSummaryAsync_NamedGame_PathsClaimedByAnotherEntity_DoesNotDoubleCountAggregate()
    {
        var options = NewInMemoryOptions();
        var sharedPath = CreateTempCacheFile(5000);

        await using (var seedContext = new AppDbContext(options))
        {
            // Game A claims the shared path first (processed in insertion/Id order).
            seedContext.CachedGameDetections.Add(NamedDetectionRow(
                "xbox", "Shared Launcher Payload",
                cacheFilesFound: 1,
                totalSizeBytes: 1UL,
                cacheFilePaths: new List<string> { sharedPath }));

            // Game B references the exact same physical path. Its own attribution contributes
            // 0 bytes because A already claimed it - this must NOT fall back to retaining B's
            // stale (and much larger) persisted size.
            seedContext.CachedGameDetections.Add(NamedDetectionRow(
                "xbox", "Another Title Sharing The Payload",
                cacheFilesFound: 3,
                totalSizeBytes: 999_999UL,
                cacheFilePaths: new List<string> { sharedPath }));

            await seedContext.SaveChangesAsync();
        }

        var dataService = NewDataService(options);
        await dataService.RefreshDiskSummaryAsync();

        await using var verifyContext = new AppDbContext(options);
        var rows = await verifyContext.CachedGameDetections.OrderBy(g => g.Id).ToListAsync();

        Assert.Equal(5000UL, rows[0].TotalSizeBytes);
        Assert.Equal(0UL, rows[1].TotalSizeBytes);

        var summary = await verifyContext.CachedDetectionSummaries.SingleAsync();
        Assert.Equal(5000UL, summary.GamesOnDiskBytes);
        Assert.Equal(1, summary.GamesOnDiskCount);
    }

    // -----------------------------------------------------------------------------------------
    // Mixed case: one of the game's paths was already claimed by another entity, the other path
    // is new to this pass but missing from disk. Both contribute 0 bytes, so the whole entity
    // must still be treated as claimed-elsewhere (force-zeroed) and NOT fall into the retention
    // branch, which would otherwise re-add its stale persisted size on top of the claimant's
    // already-counted bytes and inflate the aggregate above actual disk usage.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task RefreshDiskSummaryAsync_NamedGame_MixedClaimedAndMissingPaths_DoesNotDoubleCountAggregate()
    {
        var options = NewInMemoryOptions();
        var sharedPath = CreateTempCacheFile(4096);
        var missingPath = Path.Combine(Path.GetTempPath(), $"gamesondisk_mixed_missing_{Guid.NewGuid():N}.cache");

        await using (var seedContext = new AppDbContext(options))
        {
            // Game A claims the shared path first (processed in Id order).
            seedContext.CachedGameDetections.Add(NamedDetectionRow(
                "xbox", "Owning Launcher Payload",
                cacheFilesFound: 1,
                totalSizeBytes: 1UL,
                cacheFilePaths: new List<string> { sharedPath }));

            // Game B references the same shared path (already claimed by A, contributes 0 bytes)
            // plus a second path that is new to this pass but absent from disk (also 0 bytes).
            seedContext.CachedGameDetections.Add(NamedDetectionRow(
                "xbox", "Mixed Claimed And Missing Title",
                cacheFilesFound: 2,
                totalSizeBytes: 777_777UL,
                cacheFilePaths: new List<string> { sharedPath, missingPath }));

            await seedContext.SaveChangesAsync();
        }

        var dataService = NewDataService(options);
        await dataService.RefreshDiskSummaryAsync();

        await using var verifyContext = new AppDbContext(options);
        var rows = await verifyContext.CachedGameDetections.OrderBy(g => g.Id).ToListAsync();

        Assert.Equal(4096UL, rows[0].TotalSizeBytes);
        Assert.Equal(0UL, rows[1].TotalSizeBytes);

        var summary = await verifyContext.CachedDetectionSummaries.SingleAsync();
        Assert.Equal(4096UL, summary.GamesOnDiskBytes);
        Assert.Equal(1, summary.GamesOnDiskCount);
    }

    // -----------------------------------------------------------------------------------------
    // Criterion 6c: regression lock. Evicted rows still force-zero regardless of persisted size,
    // untouched by the retention guard.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task RefreshDiskSummaryAsync_EvictedNamedGame_StillZeroed()
    {
        var options = NewInMemoryOptions();
        var missingPath = Path.Combine(Path.GetTempPath(), $"gamesondisk_evicted_{Guid.NewGuid():N}.cache");

        await using (var seedContext = new AppDbContext(options))
        {
            seedContext.CachedGameDetections.Add(NamedDetectionRow(
                "xbox", "Halo Infinite",
                cacheFilesFound: 5,
                totalSizeBytes: 500_000UL,
                cacheFilePaths: new List<string> { missingPath },
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
