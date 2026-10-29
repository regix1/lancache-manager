using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    [Fact]
    public async Task AFailedClearAfterAFailedRemovePassKeepsTheFailureThatEndedItAsync()
    {
        await using var pass = await RemovePass.CreateAsync();
        pass.Rust.OnRun = async (process, _, _) =>
        {
            // The child reached its delete stage, then ended without a report.
            await RemovePass.WriteDeleteStageAsync(process);
            return new ProcessExecutionResult { ExitCode = 0 };
        };
        pass.Saves.FailuresLeft = 1;

        await Assert.ThrowsAsync<FileNotFoundException>(pass.RunAsync);
    }

    [Fact]
    public async Task AFailedClearAtTheDeleteStageStopsTheChildAndEndsThePassAsync()
    {
        await using var pass = await RemovePass.CreateAsync();
        var childStopped = false;
        pass.Rust.OnRun = async (process, token, onProgressEvent) =>
        {
            await RemovePass.WriteDeleteStageAsync(process);
            try
            {
                await onProgressEvent!(new RustProgressEvent
                {
                    Event = "progress",
                    StageKey = "signalr.unmappedRemove.running",
                    PercentComplete = 92
                });
            }
            catch (Exception)
            {
                // The helper logs a fault in the callback and keeps reading the child.
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
            catch (OperationCanceledException)
            {
                childStopped = true;
                throw;
            }

            await File.WriteAllTextAsync(RemovePass.QuotedArgument(process, 1), JsonSerializer.Serialize(RemovePass.Report));
            return new ProcessExecutionResult { ExitCode = 0 };
        };
        pass.Saves.FailuresLeft = 1;

        await Assert.ThrowsAsync<DbUpdateException>(pass.RunAsync);

        Assert.True(childStopped);
        // The pass's own cleanup cleared the list once the database answered again.
        Assert.Null(await pass.StoredBucketAsync());
    }

    /// <summary>
    /// A remove pass over one monolithic datasource, with a stand-in for the detector and a database
    /// whose next saves can be made to fail.
    /// </summary>
    private sealed class RemovePass : IAsyncDisposable
    {
        private readonly string _root;

        private RemovePass(string root)
        {
            _root = root;
            var paths = new AuthCredentialFormatTests.TempDirPathResolver(root);
            Rust = new ScriptedDetector(paths);
            Contexts = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase("unmapped-clear-failure-" + Guid.NewGuid().ToString("N"))
                .AddInterceptors(Saves)
                .Options);
            Store = new GameCacheDetectionDataService(Contexts, NullLogger<GameCacheDetectionDataService>.Instance);
            var datasources = new DatasourceService(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LanCache:LogPath"] = Path.Combine(root, "logs"),
                    ["LanCache:CachePath"] = Path.Combine(root, "cache")
                }).Build(),
                paths,
                NullLogger<DatasourceService>.Instance);
            var datasource = Assert.Single(datasources.GetDatasources());
            Directory.CreateDirectory(datasource.CachePath);
            Directory.CreateDirectory(datasource.LogPath);
            File.WriteAllText(Path.Combine(datasource.LogPath, "access.log"), string.Empty);
            var detector = paths.GetRustGameDetectorPath();
            Directory.CreateDirectory(Path.GetDirectoryName(detector)!);
            File.WriteAllText(detector, string.Empty);
            Directory.CreateDirectory(paths.GetOperationsDirectory());

            var tracker = CacheScanGateHarness.TrackerWith(new DownloadSpeedSnapshot(), []);
            // Stands in for a child that keeps publishing, so the download watch never reads silence.
            CacheScanGateHarness.SetField(tracker, "_reportDueUtc", DateTime.MaxValue);
            Detection = new GameCacheDetectionService(
                NullLogger<GameCacheDetectionService>.Instance,
                paths,
                operationStateService: null!,
                Contexts,
                Store,
                evictedDetectionPreservationService: null!,
                unknownGameResolutionService: null!,
                Rust,
                notifications: null!,
                datasources,
                new DatasourceCapabilityService(datasources),
                operationTracker: null!,
                CacheScanGateHarness.GateOver(tracker));
        }

        public static Dictionary<string, object?> Report { get; } = new()
        {
            ["total_games_detected"] = 0,
            ["total_services_detected"] = 0,
            ["indexed_cache_files"] = 10,
            ["games"] = Array.Empty<GameCacheInfo>(),
            ["services"] = Array.Empty<ServiceCacheInfo>(),
            ["unmapped_services"] = Array.Empty<UnmappedService>(),
            ["unmapped_removed_files"] = 3,
            ["unmapped_removed_bytes"] = 3000,
            ["unmapped_remove_failures"] = 0,
            ["unmapped_over_limit"] = false
        };

        public ScriptedDetector Rust { get; }
        public FailingSaves Saves { get; } = new();
        public TestDbContextFactory Contexts { get; }
        public GameCacheDetectionDataService Store { get; }
        public GameCacheDetectionService Detection { get; }

        public static async Task<RemovePass> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "lm-unmapped-clear-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var pass = new RemovePass(root);
            await pass.Store.SaveUnmappedServicesAsync(
                [new UnmappedService { Service = "wsus", FileCount = 7, TotalSizeBytes = 4096 }],
                CancellationToken.None);
            return pass;
        }

        public Task<UnmappedFilesPassResult> RunAsync() =>
            Detection.RunUnmappedFilesPassAsync(
                Guid.NewGuid(),
                new UnmappedFilesLimits(10, 1_000_000),
                0,
                (_, _, _) => Task.CompletedTask,
                CancellationToken.None);

        public async Task<string?> StoredBucketAsync()
        {
            await using var context = await Contexts.CreateDbContextAsync();
            return (await context.CachedDetectionSummaries.SingleOrDefaultAsync())?.UnmappedServicesJson;
        }

        /// <summary>The detector's quoted arguments are the cache root, the report and the progress file.</summary>
        public static string QuotedArgument(ProcessStartInfo process, int index) =>
            Regex.Matches(process.Arguments, "\"([^\"]*)\"")[index].Groups[1].Value;

        public static Task WriteDeleteStageAsync(ProcessStartInfo process) =>
            File.WriteAllTextAsync(
                QuotedArgument(process, 2),
                """{"status":"running","stageKey":"signalr.unmappedRemove.running","percentComplete":92}""");

        public ValueTask DisposeAsync()
        {
            Detection.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A file the process still holds must not fail the test.
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Fails the next <see cref="FailuresLeft"/> saves before they reach the database.</summary>
    private sealed class FailingSaves : SaveChangesInterceptor
    {
        public int FailuresLeft { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData saving,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (FailuresLeft > 0)
            {
                FailuresLeft--;
                throw new DbUpdateException("The database refused the save.");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class ScriptedDetector(IPathResolver paths) : RustProcessHelper(
        NullLogger<RustProcessHelper>.Instance,
        new ProcessManager(NullLogger<ProcessManager>.Instance),
        paths,
        operationTracker: null!)
    {
        public Func<ProcessStartInfo, CancellationToken, Func<RustProgressEvent, Task>?, Task<ProcessExecutionResult>>? OnRun { get; set; }

        public override Task<ProcessExecutionResult> ExecuteTrackedProcessWithProgressEventsAsync(
            ProcessStartInfo process,
            Guid? operationId,
            CancellationToken cancellationToken,
            Func<RustProgressEvent, Task>? onProgressEvent,
            string processLabel = "rust") =>
            OnRun!(process, cancellationToken, onProgressEvent);
    }
}
