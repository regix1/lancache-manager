using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A full scan that matches nothing still measures the cache files no detection row claims, and
/// that is the run whose remainder is largest. <see cref="GameCacheDetectionDataService.LoadDetectionAsync"/>
/// returned null as soon as there were no game and no service rows, so the bucket the scan had
/// just written stayed in the database and never reached the panel.
/// </summary>
public class LoadDetectionUnmappedBucketTests
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
            .UseInMemoryDatabase($"unmapped_bucket_{Guid.NewGuid():N}")
            .Options;

    private static GameCacheDetectionDataService NewDataService(DbContextOptions<AppDbContext> options)
        => new GameCacheDetectionDataService(
            new InMemoryDbContextFactory(options),
            NullLogger<GameCacheDetectionDataService>.Instance);

    private static UnmappedService Wsus(long fileCount, ulong bytes)
        => new UnmappedService
        {
            Service = "wsus",
            FileCount = fileCount,
            TotalSizeBytes = bytes,
            SampleUrls = { "http://wsus.example/one.cab" }
        };

    [Fact]
    public async Task LoadDetectionAsync_NoDetectionRows_StillReturnsTheUnmappedBucket()
    {
        var options = NewInMemoryOptions();
        var dataService = NewDataService(options);

        // A full scan over a cache no matcher claims: no game rows, no service rows, no downloads
        // to synthesize evicted projections from, and a stored remainder covering every file.
        await dataService.SaveUnmappedServicesAsync([Wsus(18_575, 415_000_000_000)]);

        var response = await dataService.LoadDetectionAsync(includeCacheFilePaths: false);

        Assert.NotNull(response);
        var unmapped = Assert.Single(response!.UnmappedServices!);
        Assert.Equal("wsus", unmapped.Service);
        Assert.Equal(18_575, unmapped.FileCount);
    }

    [Fact]
    public async Task LoadDetectionAsync_NoDetectionRowsAndNoBucket_StillReturnsNull()
    {
        var options = NewInMemoryOptions();

        var response = await NewDataService(options).LoadDetectionAsync(includeCacheFilePaths: false);

        Assert.Null(response);
    }
}
