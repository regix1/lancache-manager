using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using LancacheManager.Configuration;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;

namespace LancacheManager.Tests;

/// <summary>
/// A removal report that lands inside the send interval is held, not lost: it goes out once the interval has passed,
/// so a removal that goes quiet after it does not leave the card one report behind. A run that ended in the meantime
/// gets no late report after its ending.
/// </summary>
public sealed class RemovalHeldProgressTests
{
    private const string Service = "steam";
    private const string DeletingStageKey = "signalr.serviceRemove.deleting";
    private const string RemovingFileStageKey = "signalr.corruptionRemove.removingCacheFile";

    [Fact]
    public async Task ACorruptionCheckpointHeldBackByTheIntervalReachesTheCardWhileTheRemovalIsQuietAsync()
    {
        var runType = typeof(CorruptionRemovalContractTests).GetNestedType("RemovalRun", BindingFlags.NonPublic)!;
        var fixture = Activator.CreateInstance(
            runType,
            [CorruptionDetectionMethod.Structural, true, 1, DatasourceSchemeOverrideValues.Monolithic, null, false])!;
        await using var disposal = (IAsyncDisposable)fixture;
        var messages = (CorruptionRemovalContractTests.RemovalMessages)runType.GetProperty("Messages")!.GetValue(fixture)!;
        var pipe = runType.GetProperty("Pipe")!.GetValue(fixture)!;
        var connect = pipe.GetType().GetMethod("ConnectAsync")!;
        var send = pipe.GetType().GetMethod("SendAsync")!;

        var run = (Task<bool>)runType.GetMethod("RunAsync")!.Invoke(fixture, [Service, null])!;
        await (Task)connect.Invoke(pipe, null)!;

        // Two checkpoints back to back: the second lands inside the interval the first one's send started. The
        // removal reads its progress file on each wake-up, so the second is written only once the first was sent.
        for (var file = 1; file <= 2; file++)
        {
            var checkpoint = JsonSerializer.Serialize(new
            {
                status = "running",
                stageKey = RemovingFileStageKey,
                percentComplete = 10.0 * file,
                filesProcessed = file,
                totalFiles = 10
            });
            await (Task)send.Invoke(pipe, [checkpoint, null])!;
            if (file == 1)
            {
                var firstSent = Stopwatch.StartNew();
                while (!messages.Progress.Any(message => message.StageKey == RemovingFileStageKey))
                {
                    Assert.True(firstSent.Elapsed < TimeSpan.FromSeconds(10), "The first checkpoint was never sent");
                    await Task.Delay(5);
                }
            }
        }

        // The quiet stretch: twenty intervals, far more than the one after which the held checkpoint is due.
        var quiet = Stopwatch.StartNew();
        var quietLimit = TimeSpan.FromMilliseconds(20 * RustProcessHelper.ProgressEmitMinIntervalMs);
        int? shown = null;
        while (quiet.Elapsed < quietLimit)
        {
            shown = messages.Progress
                .Where(message => message.StageKey == RemovingFileStageKey)
                .Select(message => (int?)message.FilesProcessed)
                .LastOrDefault();
            if (shown == 2) break;
            await Task.Delay(10);
        }

        quiet.Stop();
        await (Task)send.Invoke(pipe, [
            """{"status":"completed","percentComplete":100,"context":{"detectionMethod":"structural","count":1,"files":1,"alreadyMissing":0,"healed":0,"keyVerificationSkipped":0,"bytesFreed":1024}}""",
            0])!;
        var complete = await messages.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        messages.Resume.TrySetResult();
        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(complete.Success, "The corruption removal did not end as a success");

        Assert.True(shown == 2,
            $"After {quiet.ElapsedMilliseconds} ms of quiet following the corruption removal's second checkpoint " +
            $"(2 files processed), the card still showed {(shown is null ? "no count" : $"{shown} file(s) processed")}: " +
            $"the checkpoint held back by the {RustProcessHelper.ProgressEmitMinIntervalMs} ms interval was never sent " +
            "once the interval passed");
    }

    [Fact]
    public async Task AReportHeldWhenTheRunIsEndedFromOutsideIsNeverSentAfterTheEndingAsync()
    {
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var notifications = new ScheduledRunReporterTests.CapturingNotificationService();
        var secondReported = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
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
            ExecuteAsync: async (id, _, report) =>
            {
                await report(new RemovalProgressUpdate(10, DeletingStageKey, FilesDeleted: 1));
                await report(new RemovalProgressUpdate(20, DeletingStageKey, FilesDeleted: 2));
                secondReported.TrySetResult(id);
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

        var operationId = await secondReported.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // A force stop ends the run from outside while the removal's own code is still running.
        tracker.CompleteOperation(operationId, success: false, canceled: true);

        // Four intervals: the held report would have been due after one.
        await Task.Delay(4 * RustProcessHelper.ProgressEmitMinIntervalMs);
        quietEnds.TrySetResult();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var events = notifications.Events;

        var ending = events.FindIndex(message => message.EventName == SignalREvents.ServiceRemovalComplete);
        Assert.True(ending >= 0, "The run ended from outside sent no ending");
        var late = events
            .Skip(ending + 1)
            .Where(message => message.EventName == SignalREvents.ServiceRemovalProgress)
            .Select(message => Assert.IsType<ServiceRemovalProgress>(message.Payload))
            .ToList();
        Assert.True(late.Count == 0,
            $"The run had already ended as canceled, yet {late.Count} progress message(s) went out after its ending " +
            $"(last: {late.LastOrDefault()?.FilesDeleted} files deleted)");
    }
}
