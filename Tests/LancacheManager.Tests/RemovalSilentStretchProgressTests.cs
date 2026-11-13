using System.Diagnostics;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;

namespace LancacheManager.Tests;

/// <summary>
/// A removal reports two checkpoints in quick succession and then goes quiet, as it does while a slow disk or a NAS
/// stalls between files. The second checkpoint lands inside the send interval, so it is held back; once the interval
/// has passed, the card must show it, not stay one checkpoint behind until the removal reports again.
/// </summary>
public sealed class RemovalSilentStretchProgressTests
{
    private const string Service = "steam";
    private const string DeletingStageKey = "signalr.serviceRemove.deleting";

    [Fact]
    public async Task ACheckpointHeldBackByTheIntervalReachesTheCardWhileTheRemovalIsQuietAsync()
    {
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var notifications = new ScheduledRunReporterTests.CapturingNotificationService();
        var secondReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quietEnds = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await TrackedRemovalOperationRunner.StartAsync(tracker, notifications, new RemovalOperationConfig<int>(
            OperationType: OperationType.ServiceRemoval,
            OperationLabel: $"Service removal: {Service}",
            Target: Service,
            Metrics: new RemovalMetrics(),
            StartedEventName: SignalREvents.ServiceRemovalStarted,
            BuildStarted: id => new ServiceRemovalStarted(Service, id, "signalr.serviceRemove.starting.byName", DateTime.UtcNow),
            ProgressEventName: SignalREvents.ServiceRemovalProgress,
            InitialStageKey: "signalr.serviceRemove.starting.byName",
            BuildInitialProgress: id => new ServiceRemovalProgress(Service, id, "signalr.serviceRemove.starting.byName"),
            BuildProgress: (id, update) => new ServiceRemovalProgress(
                Service, id, update.StageKey, update.PercentComplete, update.FilesDeleted, update.BytesFreed),
            CompleteEventName: SignalREvents.ServiceRemovalComplete,
            FinalizingStageKey: "signalr.serviceRemove.finalizing",
            BuildFinalizingProgress: (id, filesDeleted) => new ServiceRemovalProgress(
                Service, id, "signalr.serviceRemove.finalizing", 100.0, filesDeleted),
            BuildSuccess: (id, filesDeleted) => new ServiceRemovalComplete(
                true, Service, id, "signalr.serviceRemove.success", filesDeleted),
            BuildCanceled: id => new ServiceRemovalComplete(
                false, Service, id, "signalr.serviceRemove.canceled", Canceled: true),
            BuildErrorProgress: (id, _) => new ServiceRemovalProgress(Service, id, "signalr.serviceRemove.error.default"),
            BuildErrorComplete: (id, _) => new ServiceRemovalComplete(
                false, Service, id, "signalr.serviceRemove.error.default"),
            ExecuteAsync: async (_, _, report) =>
            {
                await report(new RemovalProgressUpdate(10, DeletingStageKey, FilesDeleted: 1));
                await report(new RemovalProgressUpdate(20, DeletingStageKey, FilesDeleted: 2));
                secondReported.TrySetResult();
                // The quiet stretch: the removal reports nothing more until the test lets it go on.
                await quietEnds.Task;
                return 2;
            },
            BuildRepair: id => new OperationRepair
            {
                Id = id,
                Type = OperationType.ServiceRemoval,
                Name = $"Service removal: {Service}",
                StartedAt = DateTime.UtcNow
            },
            PrepareRepairAsync: (_, _) => Task.CompletedTask,
            FinishRepairAsync: (_, _, _, _, _) =>
            {
                finished.TrySetResult();
                return Task.CompletedTask;
            },
            ApplyProgressMetrics: (target, update) => target.FilesDeleted = update.FilesDeleted));

        await secondReported.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Twenty send intervals of quiet: far more than the one interval after which the held checkpoint is due.
        var quiet = Stopwatch.StartNew();
        var quietLimit = TimeSpan.FromMilliseconds(20 * RustProcessHelper.ProgressEmitMinIntervalMs);
        int? shown = null;
        while (quiet.Elapsed < quietLimit)
        {
            shown = LatestFilesDeleted(notifications);
            if (shown == 2) break;
            await Task.Delay(10);
        }

        quiet.Stop();
        quietEnds.TrySetResult();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(shown == 2,
            $"After {quiet.ElapsedMilliseconds} ms of quiet following the removal's second checkpoint (2 files " +
            $"deleted), the card still showed {(shown is null ? "no count" : $"{shown} file(s) deleted")}: the " +
            $"checkpoint held back by the {RustProcessHelper.ProgressEmitMinIntervalMs} ms interval was never sent " +
            "once the interval passed");
    }

    private static int? LatestFilesDeleted(ScheduledRunReporterTests.CapturingNotificationService notifications) =>
        notifications.PayloadsFor<ServiceRemovalProgress>(SignalREvents.ServiceRemovalProgress)
            .Where(message => message.StageKey == DeletingStageKey)
            .Select(message => message.FilesDeleted)
            .LastOrDefault();
}
