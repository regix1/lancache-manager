using System.Collections.Concurrent;
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
/// Two of the Game Detection schedule's own runs whose waiting cards named different scans, because the scan
/// mode changed between them. They are one run: one card, and one scan when a Full run stands in for an
/// Incremental one.
/// </summary>
[Collection(nameof(DownloadsEndedEventCollection))]
public sealed class OwnDetectionRunJoinTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public enum FullRunMeets
    {
        // An owed Full run is taken while an own Run Now waits on a card that named Incremental.
        AWaitingIncrementalRequest,
        // A Run Now that resolves Full is starting when a held Incremental run is released.
        AReleasedIncrementalHold
    }

    // An own run whose waiting card already named Incremental (it waited behind a busy run) is held for a download
    // after an owed own run that was held as Full: it joins that hold rather than raising a second card.
    [Fact]
    public async Task AnOwnRunWhoseCardNamedItsScan_JoinsTheOwnRunHeldUnderAnotherScan()
    {
        var mode = GameDetectionScanMode.Full;
        var state = CacheScanGateHarness.CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.GetGameDetectionScanMode) => mode,
            _ => NullReturningProxy.DefaultFor(method.ReturnType)
        });
        using var loop = new GameDetectionService(
            detectionService: null!,
            stateService: state,
            pathResolver: null!,
            scopeFactory: null!,
            cacheReconciliationService: null!,
            operationQueue: DispatchProxy.Create<IOperationWaitlist, NullReturningProxy>(),
            logger: NullLogger<GameDetectionService>.Instance,
            configuration: new ConfigurationBuilder().Build());
        var cards = new List<DetectionScanType?>();

        var owed = await loop.HoldRunAsync(new RunNotice(NotificationMode.All, RunTrigger.Scheduled),
            notice => cards.Add(notice.HeldScanType));
        mode = GameDetectionScanMode.Incremental;
        var waited = new RunNotice(NotificationMode.All, RunTrigger.Manual) { HeldScanType = DetectionScanType.Incremental };
        var held = await loop.HoldRunAsync(waited, notice => cards.Add(notice.HeldScanType));

        Assert.Same(owed, held);
        Assert.True(cards.Count == 1, $"Expected one waiting card, found {cards.Count}: {string.Join(", ", cards)}");
    }

    // The schedule's own Full run does the work of its own Incremental run, so one scan runs, not two.
    [Theory]
    [InlineData(FullRunMeets.AWaitingIncrementalRequest)]
    [InlineData(FullRunMeets.AReleasedIncrementalHold)]
    public async Task AnOwnFullRunMeetingAnOwnIncrementalRun_RunsOneScan(FullRunMeets order)
    {
        var mode = GameDetectionScanMode.Incremental;
        var state = CacheScanGateHarness.CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.GetGameDetectionScanMode) => mode,
            nameof(IStateService.GetHiddenClientIps) => new List<string>(),
            nameof(IStateService.GetEvictedDataMode) => "show",
            nameof(IStateService.GetGlobalNotificationDisplayMode) => NotificationDisplayMode.Condensed,
            nameof(IStateService.GetHiddenPrefillRules) => new List<ClientExclusionRule>(),
            nameof(IStateService.GetPrefillRules) => new List<ClientExclusionRule>(),
            _ => NullReturningProxy.DefaultFor(method.ReturnType)
        });
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance), NullLogger<UnifiedOperationTracker>.Instance);

        // The scan each run asked the operation queue for, in order. The first run stays inside the queue until
        // the test lets it go.
        var requested = new ConcurrentQueue<DetectionScanType>();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<OperationStartResponse> EnqueueAsync(object?[] args)
        {
            requested.Enqueue(((GameDetectionRequestKey)args[5]!).ScanType);
            if (requested.Count == 1)
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task;
            }
            return new OperationStartResponse { OperationId = Guid.NewGuid(), Status = OperationStartStatus.Started };
        }

        using var loop = new GameDetectionService(
            detectionService: null!,
            stateService: state,
            pathResolver: null!,
            scopeFactory: null!,
            cacheReconciliationService: null!,
            operationQueue: CacheScanGateHarness.CreateProxy<IOperationWaitlist>((method, args) =>
                method.Name == nameof(IOperationWaitlist.EnqueueAsync) ? EnqueueAsync(args!) : NullReturningProxy.DefaultFor(method.ReturnType)),
            logger: NullLogger<GameDetectionService>.Instance,
            configuration: new ConfigurationBuilder().Build());

        var previousGate = ScheduledServiceBase.ScheduleRunGate;
        var previousWait = ScheduledServiceBase.WaitForDownloadAnswer;
        try
        {
            var schedules = new ServiceScheduleRegistry(
                [loop],
                state,
                DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
                ScheduleExecutionTestService.Create(),
                tracker,
                activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.Idle());
            ScheduledServiceBase.WaitForDownloadAnswer = null;
            // A run that reaches the start gate waits there until the test opens it.
            var gate = ScheduledServiceBase.ScheduleRunGate!;
            var atGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var openGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ScheduledServiceBase.ScheduleRunGate = async (key, trigger) =>
            {
                atGate.TrySetResult();
                await openGate.Task;
                return await gate(key, trigger);
            };
            await loop.StartAsync(CancellationToken.None);
            try
            {
                if (order == FullRunMeets.AWaitingIncrementalRequest)
                {
                    openGate.TrySetResult();
                    // A Games-page Incremental scan keeps the schedule busy, and a Run Now waits behind it on a card
                    // that names Incremental.
                    await loop.TryTriggerImmediateRunAsync(
                        new RunNotice(NotificationMode.All, RunTrigger.Manual) { RequestedScanType = DetectionScanType.Incremental });
                    await firstEntered.Task.WaitAsync(Timeout);
                    await schedules.TriggerRunAsync("gameDetection");
                    // A scheduled run held for a download once the mode is Full, released while only the
                    // Games-page scan runs, is owed.
                    mode = GameDetectionScanMode.Full;
                    await loop.HoldRunAsync(new RunNotice(NotificationMode.All, RunTrigger.Scheduled), _ => { });
                    loop.ReleaseHeldRun(_ => null);
                    releaseFirst.TrySetResult();
                    await WaitForScansAsync(requested, 2);
                    Assert.Equal([DetectionScanType.Incremental, DetectionScanType.Full], requested.ToArray());
                }
                else
                {
                    releaseFirst.TrySetResult();
                    // A scheduled run held for a download while the mode is Incremental.
                    await loop.HoldRunAsync(new RunNotice(NotificationMode.All, RunTrigger.Scheduled), _ => { });
                    mode = GameDetectionScanMode.Full;
                    // A Run Now taken while nothing runs resolves Full, and the hold is released before it starts.
                    await loop.TryTriggerImmediateRunAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual));
                    await atGate.Task.WaitAsync(Timeout);
                    loop.ReleaseHeldRun(_ => null);
                    openGate.TrySetResult();
                    await WaitForScansAsync(requested, 1);
                    Assert.Equal([DetectionScanType.Full], requested.ToArray());
                }
            }
            finally
            {
                releaseFirst.TrySetResult();
                openGate.TrySetResult();
                await loop.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previousGate;
            ScheduledServiceBase.WaitForDownloadAnswer = previousWait;
        }
    }

    // Waits for the expected number of scans, then for a short while longer, so a second scan queued right
    // behind them has the time to reach the operation queue and show in the list.
    private static async Task WaitForScansAsync(ConcurrentQueue<DetectionScanType> requested, int count)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (requested.Count < count)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Only {requested.Count} of {count} scans reached the operation queue");
            await Task.Delay(10);
        }
        await Task.Delay(500);
    }
}
