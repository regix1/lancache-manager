using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Verifies the retained-repair contract for the stored unmapped-services bucket. An interrupted
/// full scan that began database writes invalidates the prior full-scan remainder. Pre-write,
/// completed, and incremental repairs preserve the prior measurement.
/// </summary>
public sealed class CancelledDetectionUnmappedBucketTests
{
    [Theory]
    [InlineData(OperationStatus.Cancelled)]
    [InlineData(OperationStatus.Failed)]
    public async Task EveryExitThatDidNotFinishTheWalk_ClearsTheStoredBucket(
        OperationStatus outcome)
    {
        var (original, stored) = await ResumeAndReadBucketAsync(
            DetectionScanType.Full,
            outcome,
            writeStarted: true);

        Assert.NotEmpty(original);
        Assert.Null(stored);
    }

    [Theory]
    [InlineData(DetectionScanType.Full, OperationStatus.Cancelled, false)]
    [InlineData(DetectionScanType.Full, OperationStatus.Failed, false)]
    [InlineData(DetectionScanType.Full, OperationStatus.Completed, true)]
    [InlineData(DetectionScanType.Incremental, OperationStatus.Cancelled, true)]
    [InlineData(DetectionScanType.Incremental, OperationStatus.Failed, true)]
    public async Task RepairsWithoutAnInterruptedFullWrite_PreserveTheStoredBucket(
        DetectionScanType scan,
        OperationStatus outcome,
        bool writeStarted)
    {
        var (original, stored) = await ResumeAndReadBucketAsync(scan, outcome, writeStarted);

        Assert.NotEmpty(original);
        Assert.Equal(original, stored);
    }

    private static async Task<(string Original, string? Stored)> ResumeAndReadBucketAsync(
        DetectionScanType scan,
        OperationStatus outcome,
        bool writeStarted)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("cancelled-detection-bucket-" + Guid.NewGuid().ToString("N"))
            .Options;
        var contexts = new TestDbContextFactory(options);
        var store = new GameCacheDetectionDataService(
            contexts,
            NullLogger<GameCacheDetectionDataService>.Instance);
        await store.SaveUnmappedServicesAsync(
            [
                new UnmappedService
                {
                    Service = "wsus",
                    FileCount = 7,
                    TotalSizeBytes = 4096,
                    SampleUrls = ["http://wsus.example/update.cab"]
                }
            ],
            CancellationToken.None);

        string original;
        await using (var before = await contexts.CreateDbContextAsync())
        {
            original = Assert.IsType<string>(
                (await before.CachedDetectionSummaries.SingleAsync()).UnmappedServicesJson);
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanCache:DataSources:0:Name"] = "disabled",
                ["LanCache:DataSources:0:CachePath"] = "unused-cache",
                ["LanCache:DataSources:0:LogPath"] = "unused-logs",
                ["LanCache:DataSources:0:Enabled"] = "false"
            })
            .Build();
        var datasources = new DatasourceService(
            configuration,
            pathResolver: null!,
            logger: NullLogger<DatasourceService>.Instance);
        using var detection = new GameCacheDetectionService(
            logger: NullLogger<GameCacheDetectionService>.Instance,
            pathResolver: null!,
            operationStateService: null!,
            dbContextFactory: contexts,
            detectionDataService: store,
            evictedDetectionPreservationService: null!,
            unknownGameResolutionService: null!,
            rustProcessHelper: null!,
            notifications: null!,
            datasourceService: datasources,
            capabilityService: new DatasourceCapabilityService(datasources),
            operationTracker: null!,
            cacheScanGate: null!);
        var repair = new OperationRepair
        {
            Id = Guid.NewGuid(),
            Type = OperationType.GameDetection,
            Name = "Game Detection",
            StartedAt = DateTime.UtcNow,
            Phase = OperationRepairPhase.Repairing,
            Outcome = outcome,
            DatabaseWriteStarted = writeStarted,
            GameDetection = new GameDetectionMetrics
            {
                ScanType = scan,
                StartTime = DateTime.UtcNow
            }
        };

        await detection.ResumeRepairAsync(repair, CancellationToken.None);

        await using var after = await contexts.CreateDbContextAsync();
        var stored = (await after.CachedDetectionSummaries.SingleAsync()).UnmappedServicesJson;
        return (original, stored);
    }
}
