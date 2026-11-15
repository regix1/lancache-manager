using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Run Now on the Game Detection schedule card is refused when a datasource's cache-key scheme cannot
/// be determined (no log source on disk says which recipe the cache uses). The run that ends there
/// tells the user why with the typed key the cache features already use for that refusal,
/// <c>errors.corruption.keySchemeUnavailable</c>, never the generic "Operation failed" key. The real
/// schedule loop, schedule registry, tracker and detection service run; the operation queue starts the
/// run at once, as the real queue does when nothing conflicts, and passes the refusal on unchanged.
/// </summary>
[Collection(nameof(DownloadsEndedEventCollection))]
public sealed class GameDetectionFailureKeyTests : IAsyncDisposable
{
    private const string KeySchemeUnavailable = "errors.corruption.keySchemeUnavailable";
    private const string GenericFailureKey = "signalr.generic.failed";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lm-detect-refusal-key-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ARunNowRefusedForAnUnknownKeyScheme_ReportsTheKeySchemeKeyAsync()
    {
        Directory.CreateDirectory(_root);
        await using var repairs = await OperationRepairTests.RepairHarness.CreateAsync(Path.Combine(_root, "state"));
        var paths = new AuthCredentialFormatTests.TempDirPathResolver(_root);
        // The folders live under this test's own root: a relative path would land in the test process's
        // working folder, where another class's access log would give the cache a known key scheme.
        var datasourceConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanCache:LogPath"] = Path.Combine(_root, "logs"),
                ["LanCache:CachePath"] = Path.Combine(_root, "cache")
            })
            .Build();
        var datasources = new DatasourceService(datasourceConfig, paths, NullLogger<DatasourceService>.Instance);
        var datasource = Assert.Single(datasources.GetDatasources());
        // A log folder with no log source in it: nothing says which cache-key recipe the cache uses.
        Directory.CreateDirectory(datasource.LogPath);
        Directory.CreateDirectory(datasource.CachePath);
        var capabilities = new DatasourceCapabilityService(datasources);
        Assert.NotNull(capabilities.CheckAllCanMapLogicalObjects());

        var tracker = repairs.Tracker;
        using var detection = new GameCacheDetectionService(
            NullLogger<GameCacheDetectionService>.Instance,
            paths,
            repairs.Owner,
            dbContextFactory: null!,
            detectionDataService: null!,
            evictedDetectionPreservationService: null!,
            unknownGameResolutionService: null!,
            rustProcessHelper: null!,
            DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
            datasources,
            capabilities,
            tracker,
            CacheScanGateHarness.Idle(),
            repairs.StateService,
            new OperationConflictChecker(tracker, repairs.Owner, NullLogger<OperationConflictChecker>.Instance));

        var state = DispatchProxy.Create<IStateService, HybridDetectionHoldTests.HybridModeState>();
        ((HybridDetectionHoldTests.HybridModeState)(object)state).LastFullScanUtc = DateTime.UtcNow.AddDays(-1);
        using var loop = new GameDetectionService(
            detectionService: detection,
            stateService: state,
            pathResolver: null!,
            scopeFactory: null!,
            cacheReconciliationService: null!,
            operationQueue: CacheScanGateHarness.CreateProxy<IOperationWaitlist>((_, args) => StartNowAsync((Func<Task<Guid?>>)args![3]!)),
            logger: NullLogger<GameDetectionService>.Instance,
            configuration: new ConfigurationBuilder().Build());

        var notifications = (RecordingNotificationProxy)(object)DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        var previousGate = ScheduledServiceBase.ScheduleRunGate;
        var previousWait = ScheduledServiceBase.WaitForDownloadAnswer;
        OperationInfo? ended = null;
        try
        {
            var schedules = new ServiceScheduleRegistry(
                [loop],
                CacheScanGateHarness.VisibleClientsStateService(),
                (ISignalRNotificationService)(object)notifications,
                ScheduleExecutionTestService.Create(),
                tracker,
                activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.Idle());
            ScheduledServiceBase.WaitForDownloadAnswer = null;
            await loop.StartAsync(CancellationToken.None);
            try
            {
                // The admin clicks Run Now on the Game Detection card.
                await schedules.TriggerRunAsync("gameDetection");
                var deadline = DateTime.UtcNow + Timeout;
                while (ended is null && DateTime.UtcNow < deadline)
                {
                    ended = tracker.GetRuns().Runs
                        .Select(run => tracker.GetOperation(run.OperationId))
                        .FirstOrDefault(operation => operation is { Type: OperationType.GameDetection, Status: OperationStatus.Failed });
                    if (ended is null)
                        await Task.Delay(25);
                }
            }
            finally
            {
                await loop.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previousGate;
            ScheduledServiceBase.WaitForDownloadAnswer = previousWait;
        }

        Assert.True(ended is not null, "the refused Run Now left no failed Game Detection run on the card");
        var shownKeys = notifications.Invocations
            .Where(call => call.Method == nameof(ISignalRNotificationService.NotifyOperationFailedAsync))
            .Select(call => call.Args.Length > 1 ? call.Args[1]?.GetType().GetProperty("Error")?.GetValue(call.Args[1]) as string : null)
            .Prepend(ended!.Message)
            .ToList();
        Assert.True(
            ended.Message == KeySchemeUnavailable && !shownKeys.Contains(GenericFailureKey),
            $"a Run Now refused for an unknown cache-key scheme ends on {ended.Message} (failure event keys: {string.Join(", ", shownKeys)}), "
            + $"so the card says only \"Operation failed\" instead of {KeySchemeUnavailable}");
    }

    /// <summary>The queue's immediate door: nothing conflicts, so the run starts now and a refusal passes through.</summary>
    private static async Task<OperationStartResponse> StartNowAsync(Func<Task<Guid?>> start)
    {
        var started = await start();
        return new OperationStartResponse
        {
            OperationId = started ?? Guid.Empty,
            Status = OperationStartStatus.Started
        };
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
