using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The singleton detection summary row carries UnmappedServicesJson, the bucket the last FULL
/// scan measured. A cache holding only unrecognized content has zero detected games and zero
/// services while that bucket is non-empty, and the disk-summary refresh runs on every scan,
/// incremental included. Deleting the row on the zero-rows path therefore wiped the Unmapped
/// section on a quick scan. This pins the preserve: aggregates zero, bucket survives.
/// </summary>
public class UnmappedBucketSurvivesEmptyRefreshTests
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

    [Fact]
    public async Task Refresh_with_zero_games_and_services_keeps_the_unmapped_bucket()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"unmapped_survives_{Guid.NewGuid():N}")
            .Options;

        const string bucketJson = """[{"service":"wsus","file_count":628,"total_bytes":657520412,"sample_urls":[]}]""";
        await using (var seed = new AppDbContext(options))
        {
            seed.CachedDetectionSummaries.Add(new CachedDetectionSummary
            {
                Id = CachedDetectionSummary.SingletonId,
                GamesOnDiskBytes = 123,
                GamesOnDiskCount = 4,
                IdentifiedCacheBytes = 456,
                IdentifiedServiceBytes = 333,
                IdentifiedServiceCount = 2,
                ComputedAtUtc = DateTime.UtcNow.AddHours(-1),
                UnmappedServicesJson = bucketJson
            });
            await seed.SaveChangesAsync();
        }

        var service = new GameCacheDetectionDataService(
            new InMemoryDbContextFactory(options),
            NullLogger<GameCacheDetectionDataService>.Instance);

        // No CachedGameDetections and no CachedServiceDetections rows exist: the zero-rows path.
        await service.RefreshDiskSummaryAsync();

        await using var verify = new AppDbContext(options);
        var summary = await verify.CachedDetectionSummaries
            .SingleAsync(s => s.Id == CachedDetectionSummary.SingletonId);

        Assert.Equal(bucketJson, summary.UnmappedServicesJson);
        Assert.Equal(0u, summary.GamesOnDiskBytes);
        Assert.Equal(0, summary.GamesOnDiskCount);
        Assert.Equal(0u, summary.IdentifiedCacheBytes);
        Assert.Equal(0u, summary.IdentifiedServiceBytes);
        Assert.Equal(0, summary.IdentifiedServiceCount);
    }
}
