using System.Diagnostics;
using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;

namespace LancacheManager.Tests;

/// <summary>
/// A game or service removal reports one progress line for every few files it deletes, for its whole run. Every
/// browser on the page gets each message the removal sends, so the sends must be held to a few a second however
/// fast the lines arrive, and the last message before the removal ends must still carry the final file count.
/// </summary>
public sealed class RemovalProgressSendRateTests
{
    private const string Service = "steam";
    private const string DeletingStageKey = "signalr.serviceRemove.deleting";
    private const int ProgressLines = 4000;
    private const int FilesPerLine = 8;
    private const int FinalCount = ProgressLines * FilesPerLine;

    [Fact]
    public async Task ARemovalReportingThousandsOfLinesSendsAFewProgressMessagesAndStillSendsTheFinalCountAsync()
    {
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotifications>();
        var recorder = (RecordingNotifications)(object)notifications;
        // What the browsers got by the time the removal saves its ending, and whether it ended as a success.
        var sentBeforeFinish = new TaskCompletionSource<(List<CapturedEvent> Sent, bool Succeeded)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reporting = new Stopwatch();

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
                // What the removal's process reports: one line per eight files, as fast as it deletes them.
                reporting.Start();
                for (var line = 1; line <= ProgressLines; line++)
                {
                    await report(new RemovalProgressUpdate(
                        100.0 * line / ProgressLines, DeletingStageKey,
                        FilesDeleted: line * FilesPerLine, BytesFreed: line * FilesPerLine * 1024L));
                }

                reporting.Stop();
                return FinalCount;
            },
            BuildRepair: id => new OperationRepair
            {
                Id = id,
                Type = OperationType.ServiceRemoval,
                Name = $"Service removal: {Service}",
                StartedAt = DateTime.UtcNow
            },
            PrepareRepairAsync: (_, _) => Task.CompletedTask,
            FinishRepairAsync: (_, succeeded, _, _, _) =>
            {
                sentBeforeFinish.TrySetResult((recorder.Events.ToList(), succeeded));
                return Task.CompletedTask;
            }));

        var (sent, succeeded) = await sentBeforeFinish.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(succeeded, "The removal did not end as a success");

        var progress = sent
            .Where(message => message.EventName == SignalREvents.ServiceRemovalProgress)
            .Select(message => Assert.IsType<ServiceRemovalProgress>(message.Payload))
            .ToList();

        // The card shows the count of the last progress message until the removal's ending arrives.
        var last = Assert.IsType<ServiceRemovalProgress>(progress.LastOrDefault());
        Assert.True(last.FilesDeleted == FinalCount,
            $"The last progress message before the removal ended said {last.FilesDeleted} files ({last.StageKey}); " +
            $"it must carry the final count of {FinalCount}");

        // One send when the lines start, one for each full gate interval they took, and one more at a stage
        // change, the same allowance the eviction scan's gate gives.
        var deleting = progress.Count(message => message.StageKey == DeletingStageKey);
        var allowed = 2 + (int)(reporting.ElapsedMilliseconds / RustProcessHelper.ProgressEmitMinIntervalMs);
        Assert.True(deleting <= allowed,
            $"A removal that reported {ProgressLines} progress lines in {reporting.ElapsedMilliseconds} ms sent {deleting} " +
            $"progress messages to every browser; at most one per {RustProcessHelper.ProgressEmitMinIntervalMs} ms, " +
            $"{allowed} in that time, may go out");
    }
}
