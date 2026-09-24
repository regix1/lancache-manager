using System.Runtime.CompilerServices;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Tests.CacheScanGateHarness;

namespace LancacheManager.Tests;

/// <summary>
/// The cache file scan schedule hands its run's notice all the way to the tracked scan, so the run is
/// drawn in the schedule's mode for its trigger. A Run Now with the scan tool missing ends as one red
/// card the person closes; an automatic run with the tool missing only logs.
/// </summary>
// The registry wires the schedule's run-completed hook and the process-wide schedule gate is reset
// here, so this class shares the collection of the other tests that touch those.
[Collection(nameof(DownloadsEndedEventCollection))]
public class CacheSizeScanNotificationFlagTests
{
    [Theory]
    [InlineData(NotificationMode.Manual, RunTrigger.Manual, RunVisibility.Card)]
    [InlineData(NotificationMode.Manual, RunTrigger.Scheduled, RunVisibility.Background)]
    [InlineData(NotificationMode.All, RunTrigger.RunAll, RunVisibility.Card)]
    [InlineData(NotificationMode.Silent, RunTrigger.Startup, RunVisibility.Background)]
    [InlineData(NotificationMode.Hidden, RunTrigger.Manual, RunVisibility.Hidden)]
    public async Task TheScheduledScanRegistersWithItsSchedulesNotice(NotificationMode mode, RunTrigger trigger, RunVisibility expected)
    {
        var tool = Path.GetTempFileName();
        try
        {
            var tracker = NewTracker();
            var paths = CreateProxy<IPathResolver>((method, _) => method.Name switch
            {
                nameof(IPathResolver.GetRustCacheSizePath) => tool,
                nameof(IPathResolver.GetCacheDirectory) => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                _ => null
            });
            Guid? started = null;
            var queue = CreateProxy<IOperationQueue>((method, args) =>
            {
                var start = (Func<Task<Guid?>>)args![3]!;
                return RunStartAsync();

                async Task<QueuedOperationResponse> RunStartAsync()
                {
                    started = await start();
                    return new QueuedOperationResponse { OperationId = started!.Value };
                }
            });
            var service = new ScanProbe(CacheServiceFor(tracker, paths), paths, queue, tracker);
            var notice = new RunNotice(mode, trigger);

            await service.RunAsync(notice);

            var operation = tracker.GetOperation(Assert.IsType<Guid>(started))!;
            Assert.Equal(OperationType.CacheSizeScan, operation.Type);
            Assert.Same(notice, operation.Notice);
            Assert.Equal(expected, Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == operation.Id).Visibility);
        }
        finally
        {
            File.Delete(tool);
        }
    }

    [Theory]
    [InlineData(NotificationMode.All, RunTrigger.Manual, true)]
    [InlineData(NotificationMode.Silent, RunTrigger.Manual, true)]
    [InlineData(NotificationMode.Hidden, RunTrigger.Manual, true)]
    [InlineData(NotificationMode.All, RunTrigger.Scheduled, false)]
    [InlineData(NotificationMode.All, RunTrigger.Startup, false)]
    [InlineData(NotificationMode.All, RunTrigger.RunAll, false)]
    public async Task AMissingScanToolFailsARunNowAsAKeptCardAndOnlyLogsOtherwise(NotificationMode mode, RunTrigger trigger, bool kept)
    {
        var tracker = NewTracker();
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "cache_size");
        var paths = CreateProxy<IPathResolver>((method, _) =>
            method.Name == nameof(IPathResolver.GetRustCacheSizePath) ? missing : null);
        var queue = CreateProxy<IOperationQueue>((_, _) => throw new InvalidOperationException("A run without its tool never reaches the queue"));
        var service = new ScanProbe(cacheService: null!, paths, queue, tracker);
        var notice = new RunNotice(mode, trigger);

        await service.RunAsync(notice);

        var runs = tracker.GetRuns().Runs;
        if (!kept)
        {
            Assert.Empty(runs);
            return;
        }

        var row = Assert.Single(runs);
        Assert.Equal(OperationType.CacheSizeScan.ToWireString(), row.OperationType);
        Assert.Equal(OperationStatus.Failed.ToWireString(), row.Status);
        Assert.True(row.Retained);
        Assert.Same(notice, tracker.GetOperation(row.OperationId)!.Notice);
    }

    private static UnifiedOperationTracker NewTracker() =>
        new(new ProcessManager(NullLogger<ProcessManager>.Instance), NullLogger<UnifiedOperationTracker>.Instance);

    /// <summary>
    /// The real scan service with only the fields its tracked full scan reads. The scan itself ends
    /// early here, which does not matter: the registration happens before any directory is walked.
    /// </summary>
    private static CacheManagementService CacheServiceFor(UnifiedOperationTracker tracker, IPathResolver paths)
    {
        var service = (CacheManagementService)RuntimeHelpers.GetUninitializedObject(typeof(CacheManagementService));
        var sources = (DatasourceService)RuntimeHelpers.GetUninitializedObject(typeof(DatasourceService));
        SetField(sources, "_datasources", new List<ResolvedDatasource>());
        SetField(service, "_logger", NullLogger<CacheManagementService>.Instance);
        SetField(service, "_scanCacheLock", new SemaphoreSlim(1, 1));
        SetField(service, "_operationTracker", tracker);
        SetField(service, "_notifications", CreateProxy<ISignalRNotificationService>((method, _) =>
            method.ReturnType == typeof(Task) ? Task.CompletedTask : null));
        SetField(service, "_conflictChecker", new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance));
        SetField(service, "_cacheScanGate", Idle());
        SetField(service, "_datasourceService", sources);
        SetField(service, "_pathResolver", paths);
        return service;
    }

    /// <summary>
    /// Drives one run of the real schedule through the base loop's run step, the way a tick or a
    /// Run Now does, with the registry listening for the run's end.
    /// </summary>
    private sealed class ScanProbe : CacheSizeScanScheduledService
    {
        public ScanProbe(CacheManagementService cacheService, IPathResolver paths, IOperationQueue queue, UnifiedOperationTracker tracker)
            : base(
                cacheService,
                paths,
                queue,
                VisibleClientsStateService(),
                new RustProcessHelper(NullLogger<RustProcessHelper>.Instance, new ProcessManager(NullLogger<ProcessManager>.Instance), paths, tracker),
                NullLogger<CacheSizeScanScheduledService>.Instance,
                new ConfigurationBuilder().Build())
        {
            _ = new ServiceScheduleRegistry([this], VisibleClientsStateService(),
                CreateProxy<ISignalRNotificationService>((method, _) => method.ReturnType == typeof(Task) ? Task.CompletedTask : null),
                tracker);
        }

        public async Task RunAsync(RunNotice notice)
        {
            var gate = ScheduleRunGate;
            ScheduleRunGate = null;
            try
            {
                await RunScheduledWorkAsync(ServiceKey, notice.Trigger, ExecuteWorkAsync, CancellationToken.None,
                    "{ServiceName} run failed", () => { }, notice);
            }
            finally
            {
                ScheduleRunGate = gate;
            }
        }
    }
}
