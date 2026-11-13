using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using LancacheManager.Configuration;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;

namespace LancacheManager.Tests;

/// <summary>
/// Removals report progress for every few files they delete, for their whole run, and every browser on the page
/// gets each message sent. The run's state takes every report, so a reloaded card is current, while the live
/// messages go out on a stage change or at most once per gate interval.
/// </summary>
public sealed class RemovalProgressGateTests
{
    private const string Service = "steam";
    private const string DeletingStageKey = "signalr.serviceRemove.deleting";
    private const string RemovingFileStageKey = "signalr.corruptionRemove.removingCacheFile";

    [Fact]
    public async Task ARemovalKeepsEveryReportInItsStateAndSendsASameStageReportOnceTheIntervalHasPassedAsync()
    {
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotifications>();
        var recorder = (RecordingNotifications)(object)notifications;
        var metrics = new RemovalMetrics();
        var applied = 0;
        // Taken on the run's own thread once its last progress is sent, so no later send races the read.
        var sentBeforeFinish = new TaskCompletionSource<List<CapturedEvent>>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Taken before the run's ending sends the held report, so it holds only what went out live.
        var sentLive = new List<CapturedEvent>();

        await TrackedRemovalOperationRunner.StartAsync(tracker, notifications, new RemovalOperationConfig<int>(
            OperationType: OperationType.ServiceRemoval,
            OperationLabel: $"Service removal: {Service}",
            Target: Service,
            Metrics: metrics,
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
                await Task.Delay(RustProcessHelper.ProgressEmitMinIntervalMs + 50);
                await report(new RemovalProgressUpdate(20, DeletingStageKey, FilesDeleted: 2));
                await report(new RemovalProgressUpdate(30, DeletingStageKey, FilesDeleted: 3));
                sentLive.AddRange(recorder.Events);
                return 3;
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
                sentBeforeFinish.TrySetResult(recorder.Events.ToList());
                return Task.CompletedTask;
            },
            ApplyProgressMetrics: (target, update) =>
            {
                applied++;
                target.FilesDeleted = update.FilesDeleted;
            }));

        var sent = await sentBeforeFinish.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The run's state, which a reloaded card reads, took every report.
        Assert.Equal(3, applied);
        Assert.Equal(3, metrics.FilesDeleted);

        // A report on the same stage after the interval goes out live, not only at the run's ending.
        Assert.Contains(sentLive, message => message.Payload is ServiceRemovalProgress
        {
            StageKey: DeletingStageKey,
            FilesDeleted: 2
        });

        // The report held by the interval is sent before the run finishes.
        var deleting = sent
            .Where(message => message.EventName == SignalREvents.ServiceRemovalProgress)
            .Select(message => Assert.IsType<ServiceRemovalProgress>(message.Payload))
            .Where(message => message.StageKey == DeletingStageKey)
            .ToList();
        Assert.Contains(deleting, message => message.FilesDeleted == 3);
    }

    [Fact]
    public async Task ACorruptionRemovalWritingACheckpointPerFileSendsAFewProgressMessagesAsync()
    {
        const int checkpoints = 300;
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

        // What the removal's process writes: one checkpoint for each file it removes, as fast as it removes them.
        var reporting = Stopwatch.StartNew();
        for (var file = 1; file <= checkpoints; file++)
        {
            var checkpoint = JsonSerializer.Serialize(new
            {
                status = "running",
                stageKey = RemovingFileStageKey,
                percentComplete = 10.0 + 85.0 * file / checkpoints,
                filesProcessed = file,
                totalFiles = checkpoints
            });
            await (Task)send.Invoke(pipe, [checkpoint, null])!;
        }

        await (Task)send.Invoke(pipe, [
            """{"status":"completed","percentComplete":100,"context":{"detectionMethod":"structural","count":1,"files":1,"alreadyMissing":0,"healed":0,"keyVerificationSkipped":0,"bytesFreed":1024}}""",
            0])!;
        var complete = await messages.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        reporting.Stop();
        messages.Resume.TrySetResult();
        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(complete.Success, "The corruption removal did not end as a success");

        var removing = messages.Progress.Count(message => message.StageKey == RemovingFileStageKey);
        Assert.True(removing >= 1, "The corruption removal sent no progress for its file stage");
        // One send when the checkpoints start, one for each full gate interval the run took, and one more at a
        // stage change.
        var allowed = 2 + (int)(reporting.ElapsedMilliseconds / RustProcessHelper.ProgressEmitMinIntervalMs);
        Assert.True(removing <= allowed,
            $"A corruption removal that wrote {checkpoints} checkpoints in {reporting.ElapsedMilliseconds} ms sent " +
            $"{removing} progress messages to every browser; at most one per " +
            $"{RustProcessHelper.ProgressEmitMinIntervalMs} ms, {allowed} in that time, may go out");
    }
}
