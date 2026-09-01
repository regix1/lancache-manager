using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Coverage for the incremental mode of GameCacheDetectionDataService.SaveServicesAsync.
/// A cancelled detection scan hands over only the services it reached before the stop, so a
/// service missing from that list says nothing about whether its files are still on disk.
/// The absence-to-evict pass is therefore skipped in incremental mode and still runs on a
/// full scan. These tests run against the EF Core InMemory provider so the production code
/// path executes.
///
/// Every seeded Download is evicted on purpose: that empties the servicesWithLiveDownloads
/// gate, which is the state a freshly cleared cache leaves behind and the state in which the
/// absence pass zeroes and badges a row it never looked at.
/// </summary>
public class IncrementalServiceSaveTests
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
            .UseInMemoryDatabase($"incremental_service_save_{Guid.NewGuid():N}")
            .Options;

    private static GameCacheDetectionDataService NewDataService(DbContextOptions<AppDbContext> options)
        => new GameCacheDetectionDataService(
            new InMemoryDbContextFactory(options),
            NullLogger<GameCacheDetectionDataService>.Instance);

    private static Download EvictedServiceDownload(string service)
        => new Download
        {
            Service = service,
            ClientIp = "10.0.0.1",
            StartTimeUtc = DateTime.UtcNow,
            EndTimeUtc = DateTime.UtcNow,
            CacheHitBytes = 1,
            CacheMissBytes = 1,
            IsActive = false,
            IsEvicted = true,
            GameAppId = null,
            EpicAppId = null,
            Datasource = "default"
        };

    private static ServiceCacheInfo Scanned(string serviceName, int cacheFilesFound, ulong totalSizeBytes)
        => new ServiceCacheInfo
        {
            ServiceName = serviceName,
            CacheFilesFound = cacheFilesFound,
            TotalSizeBytes = totalSizeBytes,
            Datasources = new List<string> { "default" }
        };

    /// <summary>
    /// Seeds one service the scan reached ("steam") and one it never got to ("wsus"), with the
    /// live-download gate wide open.
    /// </summary>
    private static async Task SeedReachedAndUnreachedAsync(DbContextOptions<AppDbContext> options)
    {
        await using var seed = new AppDbContext(options);
        seed.CachedServiceDetections.Add(new CachedServiceDetection
        {
            ServiceName = "wsus",
            CacheFilesFound = 42,
            TotalSizeBytes = 1000,
            IsEvicted = false,
            LastDetectedUtc = DateTime.UtcNow.AddHours(-1),
            CreatedAtUtc = DateTime.UtcNow.AddHours(-1)
        });
        seed.Downloads.Add(EvictedServiceDownload("wsus"));
        await seed.SaveChangesAsync();
    }

    /// <summary>
    /// A service absent from a partial list keeps its counts and its badge when the save is
    /// incremental: a list the run never finished building is not proof the files are gone.
    /// </summary>
    [Fact]
    public async Task SaveServices_IncrementalPartialList_LeavesUnreachedServiceUntouchedAsync()
    {
        var options = NewInMemoryOptions();
        await SeedReachedAndUnreachedAsync(options);

        var dataService = NewDataService(options);

        await dataService.SaveServicesAsync(
            new List<ServiceCacheInfo> { Scanned("steam", 7, 700) },
            incremental: true,
            CancellationToken.None);

        await using var assert = new AppDbContext(options);
        var row = await assert.CachedServiceDetections.SingleAsync(s => s.ServiceName == "wsus");
        Assert.Equal(42, row.CacheFilesFound);
        Assert.Equal(1000UL, row.TotalSizeBytes);
        Assert.False(row.IsEvicted);
    }

    /// <summary>
    /// The same partial list on a full scan still zeroes and badges the absent service, so the
    /// guard is proven in both directions and the success path is unchanged.
    /// </summary>
    [Fact]
    public async Task SaveServices_FullScanPartialList_StillEvictsAbsentServiceAsync()
    {
        var options = NewInMemoryOptions();
        await SeedReachedAndUnreachedAsync(options);

        var dataService = NewDataService(options);

        await dataService.SaveServicesAsync(
            new List<ServiceCacheInfo> { Scanned("steam", 7, 700) },
            incremental: false,
            CancellationToken.None);

        await using var assert = new AppDbContext(options);
        var row = await assert.CachedServiceDetections.SingleAsync(s => s.ServiceName == "wsus");
        Assert.Equal(0, row.CacheFilesFound);
        Assert.Equal(0UL, row.TotalSizeBytes);
        Assert.True(row.IsEvicted);
    }

    /// <summary>
    /// Skipping the absence pass must not also skip the refresh: a service the cancelled run DID
    /// reach still gets its counts written and its stale Evicted badge cleared.
    /// </summary>
    [Fact]
    public async Task SaveServices_IncrementalPartialList_RefreshesReachedServiceAsync()
    {
        var options = NewInMemoryOptions();
        await SeedReachedAndUnreachedAsync(options);

        await using (var seed = new AppDbContext(options))
        {
            seed.CachedServiceDetections.Add(new CachedServiceDetection
            {
                ServiceName = "steam",
                CacheFilesFound = 0,
                TotalSizeBytes = 0,
                IsEvicted = true,
                LastDetectedUtc = DateTime.UtcNow.AddHours(-1),
                CreatedAtUtc = DateTime.UtcNow.AddHours(-1)
            });
            await seed.SaveChangesAsync();
        }

        var dataService = NewDataService(options);

        await dataService.SaveServicesAsync(
            new List<ServiceCacheInfo> { Scanned("steam", 7, 700) },
            incremental: true,
            CancellationToken.None);

        await using var assert = new AppDbContext(options);
        var row = await assert.CachedServiceDetections.SingleAsync(s => s.ServiceName == "steam");
        Assert.Equal(7, row.CacheFilesFound);
        Assert.Equal(700UL, row.TotalSizeBytes);
        Assert.False(row.IsEvicted);
    }
}
