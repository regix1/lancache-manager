using System.Diagnostics;
using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A schedule's held run, its requests and the waiting cards that stand for them, driven one event
/// at a time in the order each case names: a hold, the downloads-ended edge, Run Now, Run All, a card
/// dismissal, the loop taking a run, its gate turning one away, and the run's end. Every case posts
/// its events from the test thread, so none depends on how threads interleave.
/// </summary>
[Collection(nameof(DownloadsEndedEventCollection))]
public sealed class ScheduleRunQueueTests
{
    private const string EvictionKey = "cacheReconciliation";
    private const string CacheSizeKey = "cacheSizeScan";
    private const string LogRotationKey = "logRotation";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // A Game Detection Run Now the gate let through meets a download that began before its start: the
    // queue records the decline, and the terminal handler holds the run. Whichever reaches the run queue
    // first, the run's end or the hold, the person sees the hold card and no failed card beside it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADetectionRunNowRefusedAfterTheGate_ShowsOnlyTheHoldCard(bool runEndsFirst)
    {
        var snapshot = new DownloadSpeedSnapshot();
        var tracker = CreateTracker();
        Action<OperationInfo>? handle = null;
        var declined = new TaskCompletionSource<OperationInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The queue's immediate path as it runs on a refusal: a declined row for the notice, then the throw.
        async Task<QueuedOperationResponse> DeclineAsync(RunNotice notice)
        {
            CacheScanGateHarness.MakeBusy(snapshot);
            var id = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource(),
                metadata: new Dictionary<string, object?> { [DeclinedRunMetadata.Key] = true }, notice: notice);
            tracker.CompleteOperation(id, success: true, skipped: true);
            var operation = tracker.GetOperation(id)!;
            if (!runEndsFirst)
            {
                handle!(operation);
                await WaitForAsync(() => tracker.GetWaitingOperations().Any());
            }
            declined.TrySetResult(operation);
            throw new DownloadInProgressException("A download is in progress");
        }

        using var loop = new GameDetectionService(
            detectionService: null!,
            stateService: DispatchProxy.Create<IStateService, NullReturningProxy>(),
            pathResolver: null!,
            scopeFactory: null!,
            cacheReconciliationService: null!,
            operationQueue: CacheScanGateHarness.CreateProxy<IOperationQueue>((_, args) => DeclineAsync((RunNotice)args![6]!)),
            logger: NullLogger<GameDetectionService>.Instance,
            configuration: new ConfigurationBuilder().Build());
        using var schedules = Install([loop], CacheScanGateHarness.With(snapshot), tracker);
        // Called by the test, so the order of the hold against the run's end is the test's.
        handle = (Action<OperationInfo>)Delegate.CreateDelegate(typeof(Action<OperationInfo>), schedules.Service,
            typeof(ServiceScheduleRegistry).GetMethod("OnTrackedOperationTerminal", BindingFlags.Instance | BindingFlags.NonPublic)!);
        tracker.OperationTerminal -= handle;
        await loop.StartAsync(CancellationToken.None);
        try
        {
            await schedules.Service.TriggerRunAsync("gameDetection");
            var operation = await declined.Task.WaitAsync(Timeout);
            // The run's end clears the executing flag on the run queue, after the end has been decided.
            await WaitForAsync(() => !loop.IsCurrentlyExecuting);
            if (runEndsFirst)
            {
                handle(operation);
                await WaitForAsync(() => tracker.GetWaitingOperations().Any());
            }

            var runs = tracker.GetRuns().Runs;
            Assert.Single(runs, run => run.Status == OperationStatus.Waiting.ToWireString());
            Assert.DoesNotContain(runs, run => run.Status == OperationStatus.Failed.ToWireString());
        }
        finally
        {
            await loop.StopAsync(CancellationToken.None);
        }
    }

    // An eviction scan a person started that a download turned away waits on a card and runs by itself once
    // downloads end, also when the download is already gone by the time the schedule looks.
    [Fact]
    public async Task APersonsEvictionScanTurnedAwayForADownload_WaitsAndThenRuns()
    {
        using var loop = new LoopProbe(EvictionKey);
        var snapshot = new DownloadSpeedSnapshot();
        CacheScanGateHarness.MakeBusy(snapshot);
        var tracker = CreateTracker();
        using var schedules = Install([loop], CacheScanGateHarness.With(snapshot), tracker);
        var notice = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        var skippedId = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            notice: notice);
        tracker.CompleteOperation(skippedId, success: true, skipped: true,
            onCompleting: operation => operation.SkippedForDownload = true);

        await WaitForAsync(() => tracker.GetWaitingOperations().Any());
        // The card says the run waits for downloads, from the row's own typed field.
        var heldCard = Assert.Single(tracker.GetWaitingOperations());
        Assert.True(Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == heldCard.Id).WaitingForDownload);
        // The download is still writing, so the run is held and nothing asks the loop to run it.
        await Task.Delay(100);
        Assert.False(await loop.HasRequestAsync());

        CacheScanGateHarness.MakeIdle(snapshot);
        RaiseDownloadsEnded();
        var deadline = DateTime.UtcNow + Timeout;
        while (!await loop.HasRequestAsync())
        {
            Assert.True(DateTime.UtcNow < deadline, "The held run was never requested");
            await Task.Delay(10);
        }

        var held = await loop.TakeRequestAsync();
        Assert.NotNull(held);
        Assert.Equal(RunTrigger.Manual, held!.Trigger);
        Assert.Equal(OperationStatus.Waiting, tracker.GetOperation(held.OperationId!.Value)!.Status);
    }

    // The detection schedule reports the scan type its Run Now would request (Full here, the default mode),
    // which hybrid mode resolves from the clock; no other schedule reports one.
    [Fact]
    public void OnlyTheDetectionSchedule_ReportsTheScanTypeItsRunNowRuns()
    {
        using var detection = new LoopProbe("gameDetection");
        using var eviction = new LoopProbe(EvictionKey);
        using var schedules = Install([detection, eviction], CacheScanGateHarness.Idle(), CreateTracker());

        var all = schedules.Service.GetAll();

        Assert.Equal(DetectionScanType.Full, all.Single(schedule => schedule.Key == "gameDetection").RunNowScanType);
        Assert.Null(all.Single(schedule => schedule.Key == EvictionKey).RunNowScanType);
        // The browser keeps only these two strings (readSchedules), so the wire form is part of the contract.
        var wire = System.Text.Json.JsonSerializer.Serialize(all,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.Contains("\"runNowScanType\":\"full\"", wire);
    }

    // A run held in the first seconds after the tracker starts, before it has reported, is released when the
    // tracker first reports no downloads; no downloads-ended edge follows a first quiet report.
    [Fact]
    public async Task ARunHeldBeforeTheTrackerReports_IsReleasedWhenItReportsNoDownloads()
    {
        using var loop = new LoopProbe(EvictionKey);
        var tracker = CreateTracker();
        var speedTracker = CacheScanGateHarness.TrackerWith(new DownloadSpeedSnapshot(), []);
        CacheScanGateHarness.SetField(speedTracker, "_unreportedSinceUtc", DateTime.UtcNow);
        using var schedules = Install([loop], CacheScanGateHarness.GateOver(speedTracker), tracker);
        var skippedId = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            notice: new RunNotice(NotificationMode.All, RunTrigger.Manual));
        tracker.CompleteOperation(skippedId, success: true, skipped: true,
            onCompleting: operation => operation.SkippedForDownload = true);

        await WaitForAsync(() => tracker.GetWaitingOperations().Any());
        Assert.False(await loop.HasRequestAsync());

        CacheScanGateHarness.SetField(speedTracker, "_unreportedSinceUtc", null);

        var deadline = DateTime.UtcNow + Timeout;
        while (!await loop.HasRequestAsync())
        {
            Assert.True(DateTime.UtcNow < deadline, "The held run was never released");
            await Task.Delay(10);
        }
    }

    // A running detection answers a request of the same scan type and makes the other type wait, in both
    // directions.
    [Theory]
    [InlineData(DetectionScanType.Full, DetectionScanType.Full, true)]
    [InlineData(DetectionScanType.Incremental, DetectionScanType.Incremental, true)]
    [InlineData(DetectionScanType.Incremental, DetectionScanType.Full, false)]
    [InlineData(DetectionScanType.Full, DetectionScanType.Incremental, false)]
    public async Task ARunningDetection_AnswersOnlyARequestOfTheSameScanType(
        DetectionScanType running, DetectionScanType requested, bool joins)
    {
        var tracker = CreateTracker();
        var queue = new OperationQueueService(
            tracker,
            OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationQueueService>.Instance);
        tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource(),
            detectionScanType: running);

        var response = await queue.EnqueueAsync(
            OperationType.GameDetection, ConflictScope.Bulk(), "Game Detection",
            () => Task.FromResult<Guid?>(null), CancellationToken.None, detectionScanType: requested);

        Assert.Equal(joins, response.AlreadyRunning);
        Assert.Equal(!joins, response.Queued);
    }

    // A Games-page scan held for a download runs as the scan type the person picked, even when the schedule's
    // own mode resolves to the other type (Full here, the default mode).
    [Fact]
    public async Task AHeldGamesPageScan_RunsTheScanTypeThePersonPicked()
    {
        var enqueuedType = new TaskCompletionSource<DetectionScanType?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var loop = new GameDetectionService(
            detectionService: null!,
            stateService: DispatchProxy.Create<IStateService, NullReturningProxy>(),
            pathResolver: null!,
            scopeFactory: null!,
            cacheReconciliationService: null!,
            operationQueue: CacheScanGateHarness.CreateProxy<IOperationQueue>((_, args) =>
            {
                enqueuedType.TrySetResult((DetectionScanType?)args![11]);
                return Task.FromResult(new QueuedOperationResponse { OperationId = Guid.NewGuid(), Status = "started" });
            }),
            logger: NullLogger<GameDetectionService>.Instance,
            configuration: new ConfigurationBuilder().Build());
        using var schedules = Install([loop], CacheScanGateHarness.Idle(), CreateTracker());
        await loop.StartAsync(CancellationToken.None);
        try
        {
            await loop.TryTriggerImmediateRunAsync(
                new RunNotice(NotificationMode.All, RunTrigger.Manual) { RequestedScanType = DetectionScanType.Incremental });

            Assert.Equal(DetectionScanType.Incremental, await enqueuedType.Task.WaitAsync(Timeout));
        }
        finally
        {
            await loop.StopAsync(CancellationToken.None);
        }
    }

    // A held run is answered by the run the loop took only when both asked for the same scan type; a run of
    // another type is another scan's work, so the held run is requested of its own.
    [Theory]
    [InlineData(DetectionScanType.Incremental, true)]
    [InlineData(null, false)]
    public async Task AHeldTypedRun_JoinsTheLoopsRunOnlyForTheSameScanType(DetectionScanType? takenType, bool joins)
    {
        using var loop = new LoopProbe("gameDetection");
        await loop.TryTriggerImmediateRunAsync(
            new RunNotice(NotificationMode.All, RunTrigger.Manual) { RequestedScanType = takenType });
        Assert.NotNull(await loop.TakeRequestAsync());
        var held = new RunNotice(NotificationMode.All, RunTrigger.Manual) { RequestedScanType = DetectionScanType.Incremental };
        await loop.HoldRunAsync(held, _ => { });

        loop.ReleaseHeldRun(_ => null);

        Assert.Equal(!joins, await loop.HasRequestAsync());
    }

    // A Quick and a Full request both held for a download each keep their own card and both run when downloads
    // end, the Full scan first, whichever was pressed first.
    [Theory]
    [InlineData(DetectionScanType.Incremental)]
    [InlineData(DetectionScanType.Full)]
    public async Task HeldQuickAndFullRequests_BothRunWithFullFirst(DetectionScanType pressedFirst)
    {
        using var loop = new LoopProbe("gameDetection");
        var quick = new RunNotice(NotificationMode.All, RunTrigger.Manual) { RequestedScanType = DetectionScanType.Incremental };
        var full = new RunNotice(NotificationMode.All, RunTrigger.Manual) { RequestedScanType = DetectionScanType.Full };
        var cards = 0;
        foreach (var notice in pressedFirst == DetectionScanType.Incremental ? new[] { quick, full } : new[] { full, quick })
        {
            Assert.Same(notice, await loop.HoldRunAsync(notice, _ => cards++));
        }
        Assert.Equal(2, cards);

        loop.ReleaseHeldRun(_ => null);

        Assert.Same(full, await loop.TakeRequestAsync());
        Assert.Same(quick, await loop.TakeRequestAsync());
        Assert.Null(await loop.TakeRequestAsync());
    }

    // The schedule's own held run and a person's held request for the other scan type both run, the Full scan
    // first, whichever was held first and whether the schedule's run is owed or was asked for.
    [Theory]
    [InlineData(RunTrigger.Scheduled, true)]
    [InlineData(RunTrigger.Scheduled, false)]
    [InlineData(RunTrigger.Manual, true)]
    [InlineData(RunTrigger.Manual, false)]
    public async Task AHeldFullRunOfTheSchedule_RunsBeforeAHeldQuickRequest(RunTrigger scheduleTrigger, bool scheduleHeldFirst)
    {
        using var loop = new LoopProbe("gameDetection") { OwnScanType = DetectionScanType.Full };
        var own = new RunNotice(NotificationMode.All, scheduleTrigger);
        var quick = new RunNotice(NotificationMode.All, RunTrigger.Manual) { RequestedScanType = DetectionScanType.Incremental };
        foreach (var notice in scheduleHeldFirst ? new[] { own, quick } : new[] { quick, own })
        {
            await loop.HoldRunAsync(notice, _ => { });
        }

        loop.ReleaseHeldRun(_ => null);

        var first = await loop.TakeRequestAsync() ?? await loop.TakeOwedAsync();
        Assert.Same(own, first);
        await loop.RunAsync(first!.Trigger, first);
        Assert.Same(quick, await loop.TakeRequestAsync());
    }

    // A run the schedule owes is not folded into the run the loop took for the other scan type: it waits for
    // the next pass and runs its own scan.
    [Fact]
    public async Task AnOwedRunOfAnotherScanType_IsNotFoldedIntoTheTakenRun()
    {
        using var loop = new LoopProbe("gameDetection") { OwnScanType = DetectionScanType.Incremental };
        var own = new RunNotice(NotificationMode.All, RunTrigger.Scheduled);
        var full = new RunNotice(NotificationMode.All, RunTrigger.Manual) { RequestedScanType = DetectionScanType.Full };
        await loop.HoldRunAsync(own, _ => { });
        await loop.HoldRunAsync(full, _ => { });

        loop.ReleaseHeldRun(_ => null);

        Assert.Same(full, await loop.TakeRequestAsync());
        Assert.Null(await loop.TakeOwedAsync());
        Assert.True(await loop.HasRequestAsync());
        await loop.RunAsync(RunTrigger.Manual, full);
        Assert.Same(own, await loop.TakeOwedAsync());
    }

    // A held request and a held Run Now that ask for the same scan type share one hold and one card.
    [Fact]
    public async Task AHeldRunNowAndAHeldRequestForTheSameScanType_ShareOneCard()
    {
        using var loop = new LoopProbe("gameDetection") { OwnScanType = DetectionScanType.Incremental };
        var cards = 0;
        var quick = new RunNotice(NotificationMode.All, RunTrigger.Manual) { RequestedScanType = DetectionScanType.Incremental };
        var runNow = new RunNotice(NotificationMode.All, RunTrigger.Manual);

        Assert.Same(quick, await loop.HoldRunAsync(quick, _ => cards++));
        Assert.Same(quick, await loop.HoldRunAsync(runNow, _ => cards++));

        Assert.Equal(1, cards);
    }

    // A detection held for a download is drawn on a card that names the scan type it will request, so Run All
    // counts it as the schedule's run and does not raise a second card.
    [Fact]
    public async Task AHeldDetectionsCard_NamesItsScanType_AndRunAllCountsIt()
    {
        using var loop = new LoopProbe("gameDetection") { OwnScanType = DetectionScanType.Full };
        var snapshot = new DownloadSpeedSnapshot();
        CacheScanGateHarness.MakeBusy(snapshot);
        var tracker = CreateTracker();
        using var schedules = Install([loop], CacheScanGateHarness.With(snapshot), tracker);

        await schedules.Service.TriggerRunAsync("gameDetection");

        Assert.Equal(DetectionScanType.Full, Assert.Single(tracker.GetWaitingOperations()).DetectionScanType);
        var result = await schedules.Service.TriggerAllAsync();
        Assert.Equal(1, result.AlreadyRunningCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Single(tracker.GetWaitingOperations());
    }

    // A Games-page scan a download turns away at promotion is held on a card that names the scan type the
    // person pressed, not the schedule's own.
    [Fact]
    public async Task AGamesPageScanTurnedAwayForADownload_IsHeldOnACardNamingItsScanType()
    {
        using var loop = new LoopProbe("gameDetection") { OwnScanType = DetectionScanType.Full };
        var snapshot = new DownloadSpeedSnapshot();
        CacheScanGateHarness.MakeBusy(snapshot);
        var tracker = CreateTracker();
        using var schedules = Install([loop], CacheScanGateHarness.With(snapshot), tracker);
        var notice = new RunNotice(NotificationMode.All, RunTrigger.Manual) { RequestedScanType = DetectionScanType.Incremental };
        var skippedId = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource(),
            notice: notice, detectionScanType: DetectionScanType.Incremental);
        tracker.CompleteOperation(skippedId, success: true, skipped: true,
            onCompleting: operation => operation.SkippedForDownload = true);

        await WaitForAsync(() => tracker.GetWaitingOperations().Any());

        Assert.Equal(DetectionScanType.Incremental, Assert.Single(tracker.GetWaitingOperations()).DetectionScanType);
    }

    // A Games-page scan that joins the schedule's waiting run keeps the type the person pressed, so the merged
    // run is still that scan when a download holds it.
    [Fact]
    public async Task AGamesPageScanJoiningTheSchedulesWaitingRun_KeepsItsScanType()
    {
        var tracker = CreateTracker();
        var queue = new OperationQueueService(
            tracker,
            OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationQueueService>.Instance);
        tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        var scheduled = new RunNotice(NotificationMode.All, RunTrigger.Scheduled);
        var person = new RunNotice(NotificationMode.All, RunTrigger.Manual) { RequestedScanType = DetectionScanType.Full };

        var parked = await queue.EnqueueAsync(
            OperationType.GameDetection, ConflictScope.Bulk(), "Game Detection",
            () => Task.FromResult<Guid?>(null), CancellationToken.None, notice: scheduled, detectionScanType: DetectionScanType.Full);
        var joined = await queue.EnqueueAsync(
            OperationType.GameDetection, ConflictScope.Bulk(), "Game Detection",
            () => Task.FromResult<Guid?>(null), CancellationToken.None, notice: person, detectionScanType: DetectionScanType.Full);

        Assert.True(parked.Queued);
        Assert.True(joined.AlreadyRunning);
        Assert.Equal(DetectionScanType.Full, scheduled.RequestedScanType);
    }

    // Hybrid mode counts its week from a finished full scan; the broadcast that follows the scan's end already
    // carries the scan type Run Now would request after that stamp.
    [Fact]
    public async Task AFinishedHybridFullScan_BroadcastsTheScanTypeRunNowNowRequests()
    {
        DateTime? lastFullScan = null;
        var state = CacheScanGateHarness.CreateProxy<IStateService>((method, args) => method.Name switch
        {
            nameof(IStateService.GetHiddenClientIps) => new List<string>(),
            nameof(IStateService.GetGlobalNotificationDisplayMode) => NotificationDisplayMode.Condensed,
            nameof(IStateService.GetGameDetectionScanMode) => GameDetectionScanMode.Hybrid,
            nameof(IStateService.GetGameDetectionLastFullScan) => lastFullScan,
            nameof(IStateService.SetGameDetectionLastFullScan) => lastFullScan = (DateTime)args![0]!,
            _ => null
        });
        var broadcasts = new List<IReadOnlyList<ServiceScheduleInfo>>();
        var notifications = CacheScanGateHarness.CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync) && args![1] is IReadOnlyList<ServiceScheduleInfo> sent)
            {
                lock (broadcasts) broadcasts.Add(sent);
            }
            return method.ReturnType == typeof(Task) ? Task.CompletedTask : null;
        });
        using var loop = new LoopProbe("gameDetection");
        var tracker = CreateTracker();
        using var schedules = Install([loop], CacheScanGateHarness.Idle(), tracker, state, notifications);
        var scanId = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource(),
            new GameDetectionMetrics { ScanType = DetectionScanType.Full }, detectionScanType: DetectionScanType.Full);

        tracker.CompleteOperation(scanId, success: true);

        await WaitForAsync(() => { lock (broadcasts) return broadcasts.Count > 0; });
        IReadOnlyList<ServiceScheduleInfo> first;
        lock (broadcasts) first = broadcasts[0];
        Assert.Equal(DetectionScanType.Incremental, first.Single(schedule => schedule.Key == "gameDetection").RunNowScanType);
    }

    // Hybrid mode's scan type flips when the last full scan turns a week old, and no run starts or ends then.
    // The schedules are sent again at that moment, carrying the new scan type, without any other event.
    [Fact]
    public async Task TheHybridWeekEnding_BroadcastsTheNewRunNowScanType()
    {
        var lastFullScan = DateTime.UtcNow - GameDetectionScanModeExtensions.HybridWeek + TimeSpan.FromSeconds(1);
        var state = CacheScanGateHarness.CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.GetHiddenClientIps) => new List<string>(),
            nameof(IStateService.GetGlobalNotificationDisplayMode) => NotificationDisplayMode.Condensed,
            nameof(IStateService.GetGameDetectionScanMode) => GameDetectionScanMode.Hybrid,
            nameof(IStateService.GetGameDetectionLastFullScan) => lastFullScan,
            _ => null
        });
        var broadcasts = new List<IReadOnlyList<ServiceScheduleInfo>>();
        var notifications = CacheScanGateHarness.CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync) && args![1] is IReadOnlyList<ServiceScheduleInfo> sent)
            {
                lock (broadcasts) broadcasts.Add(sent);
            }
            return method.ReturnType == typeof(Task) ? Task.CompletedTask : null;
        });
        using var loop = new LoopProbe("gameDetection");
        using var schedules = Install([loop], CacheScanGateHarness.Idle(), CreateTracker(), state, notifications);

        var first = schedules.Service.GetAll();

        Assert.Equal(DetectionScanType.Incremental, first.Single(schedule => schedule.Key == "gameDetection").RunNowScanType);
        await WaitForAsync(() =>
        {
            lock (broadcasts)
                return broadcasts.Any(sent => sent.Single(schedule => schedule.Key == "gameDetection").RunNowScanType == DetectionScanType.Full);
        });
    }

    // A Games-page scan of the other scan type is an extra run: while the schedule sleeps it runs at once and
    // leaves the countdown where it was, so the schedule's own run still comes when it was due.
    [Fact]
    public async Task ARequestOfTheOtherScanTypeTakenAtTheLoopTop_LeavesTheCountdownAlone()
    {
        using var loop = new LoopProbe("gameDetection") { OwnScanType = DetectionScanType.Full };
        await loop.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => loop.NextRunUtc is not null, "The loop did not start its first sleep");
            var due = loop.NextRunUtc;

            await loop.TryTriggerImmediateRunAsync(
                new RunNotice(NotificationMode.All, RunTrigger.Manual) { RequestedScanType = DetectionScanType.Incremental });

            await WaitUntilAsync(() => loop.WorkRuns == 1 && !loop.IsCurrentlyExecuting, "The extra scan did not run");
            await Task.Delay(200);
            Assert.Equal(due, loop.NextRunUtc);
            Assert.Equal(1, loop.WorkRuns);
        }
        finally
        {
            await loop.StopAsync(CancellationToken.None);
        }
    }

    // A request of the other scan type that arrives after the loop top looked, on a due tick, does not become
    // that tick's run: the tick runs the schedule's own scan and the request stays for its own turn.
    [Fact]
    public async Task ARequestOfTheOtherScanTypeArrivingAtADueTick_IsNotTheTicksRun()
    {
        using var loop = new LoopProbe("gameDetection") { OwnScanType = DetectionScanType.Full };
        var extra = new RunNotice(NotificationMode.All, RunTrigger.Manual) { RequestedScanType = DetectionScanType.Incremental };
        await loop.TryTriggerImmediateRunAsync(extra);
        RunNotice? tickRun = null;

        await loop.RunAsync(RunTrigger.Scheduled, null, () =>
        {
            tickRun = loop.CurrentRunNotice;
            return Task.CompletedTask;
        });

        Assert.NotSame(extra, tickRun);
        Assert.Null(tickRun!.RequestedScanType);
        Assert.Same(extra, await loop.TakeRequestAsync());
    }

    // Run All counts only a waiting request of the schedule's own scan type as the schedule's run; one of the
    // other type is its own scan and does not turn Run All away.
    [Theory]
    [InlineData(DetectionScanType.Incremental, true)]
    [InlineData(DetectionScanType.Full, false)]
    public async Task RunAll_BesideAWaitingRequest_IsRefusedOnlyForTheSchedulesOwnScanType(
        DetectionScanType waiting, bool admitted)
    {
        using var loop = new LoopProbe("gameDetection") { OwnScanType = DetectionScanType.Full };
        await loop.TryTriggerImmediateRunAsync(
            new RunNotice(NotificationMode.All, RunTrigger.Manual) { RequestedScanType = waiting });

        var admission = await loop.TryTriggerImmediateRunAsync(new RunNotice(NotificationMode.All, RunTrigger.RunAll));

        Assert.Equal(admitted, admission.Admitted);
    }

    // An extra scan the loop is running is not the schedule's run: the status shows no run and a Run All is admitted.
    [Fact]
    public async Task RunAll_WhileAnExtraScanRuns_IsAdmittedAndTheStatusShowsNoRun()
    {
        using var loop = new LoopProbe("gameDetection") { OwnScanType = DetectionScanType.Full };
        await loop.TryTriggerImmediateRunAsync(
            new RunNotice(NotificationMode.All, RunTrigger.Manual) { RequestedScanType = DetectionScanType.Incremental });
        Assert.NotNull(await loop.TakeRequestAsync());

        Assert.Null((await loop.ReadRunStatusAsync()).Run);
        var admission = await loop.TryTriggerImmediateRunAsync(new RunNotice(NotificationMode.All, RunTrigger.RunAll));

        Assert.True(admission.Admitted);
        Assert.True(admission.FollowUpQueued);
    }

    // An extra scan taken right after a run leaves the countdown that run just set where it was.
    [Fact]
    public async Task AnExtraScanTakenAfterARun_LeavesTheCountdownAlone()
    {
        using var loop = new LoopProbe("gameDetection") { OwnScanType = DetectionScanType.Full };
        var firstRunStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DateTime? countdownDuringExtra = null;
        loop.Work = async () =>
        {
            if (loop.WorkRuns == 1)
            {
                firstRunStarted.SetResult();
                await releaseFirstRun.Task;
            }
            else
            {
                countdownDuringExtra = loop.NextRunUtc;
            }
        };
        await loop.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => loop.NextRunUtc is not null, "The loop did not start its first sleep");
            await loop.TryTriggerImmediateRunAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual));
            await firstRunStarted.Task.WaitAsync(Timeout);
            await loop.TryTriggerImmediateRunAsync(
                new RunNotice(NotificationMode.All, RunTrigger.Manual) { RequestedScanType = DetectionScanType.Incremental });
            releaseFirstRun.SetResult();

            await WaitUntilAsync(() => loop.WorkRuns == 2 && !loop.IsCurrentlyExecuting, "The extra scan did not run");
            await Task.Delay(200);
            Assert.NotNull(countdownDuringExtra);
            Assert.Equal(countdownDuringExtra, loop.NextRunUtc);
        }
        finally
        {
            await loop.StopAsync(CancellationToken.None);
        }
    }

    // The Schedules page reads "Last run" from the schedule's own runs only.
    [Theory]
    [InlineData(null, true)]
    [InlineData(DetectionScanType.Incremental, false)]
    public async Task AnExtraScan_DoesNotStampTheSchedulesLastRun(DetectionScanType? requested, bool stamps)
    {
        using var loop = new LoopProbe("gameDetection") { OwnScanType = DetectionScanType.Full };
        var notice = new RunNotice(NotificationMode.All, RunTrigger.Manual) { RequestedScanType = requested };

        await loop.RunAsync(RunTrigger.Manual, notice);

        Assert.Equal(stamps, loop.LastRunUtc is not null);
    }

    // An extra scan released while a failed run backs off before its retry does not bring the retry forward.
    [Fact]
    public async Task AnExtraScanReleasedDuringAFailedRunsBackOff_DoesNotBringTheRetryForward()
    {
        using var loop = new LoopProbe("gameDetection")
        {
            OwnScanType = DetectionScanType.Full,
            RetryDelay = TimeSpan.FromMilliseconds(1500),
        };
        DateTime? failedAt = null;
        DateTime? retriedAt = null;
        loop.Work = () =>
        {
            switch (loop.WorkRuns)
            {
                case 1:
                    failedAt = DateTime.UtcNow;
                    throw new InvalidOperationException("The scan failed");
                case 3:
                    retriedAt = DateTime.UtcNow;
                    break;
            }
            return Task.CompletedTask;
        };
        await loop.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => loop.NextRunUtc is not null, "The loop did not start its first sleep");
            await loop.TryTriggerImmediateRunAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual));
            await WaitUntilAsync(() => failedAt is not null, "The first run did not fail");
            await loop.TryTriggerImmediateRunAsync(
                new RunNotice(NotificationMode.All, RunTrigger.Manual) { RequestedScanType = DetectionScanType.Incremental });

            await WaitUntilAsync(() => retriedAt is not null, "The retry did not run");
            Assert.True(retriedAt >= failedAt + TimeSpan.FromMilliseconds(1200), "The retry ran before its back-off ended");
        }
        finally
        {
            await loop.StopAsync(CancellationToken.None);
        }
    }

    // A second Run Now joins the first after a scan mode change gave the schedule's scan type another slot.
    [Fact]
    public async Task ASecondRunNowAfterAScanModeChange_JoinsTheWaitingOne()
    {
        using var loop = new LoopProbe("gameDetection") { OwnScanType = DetectionScanType.Incremental };
        var first = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        await loop.TryTriggerImmediateRunAsync(first);
        loop.OwnScanType = DetectionScanType.Full;

        var admission = await loop.TryTriggerImmediateRunAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual));

        Assert.Same(first, admission.Retained);
    }

    // The same holds for the run held for a download: one hold and one card.
    [Fact]
    public async Task ASecondHeldRunAfterAScanModeChange_JoinsTheHeldOne()
    {
        using var loop = new LoopProbe("gameDetection") { OwnScanType = DetectionScanType.Incremental };
        var cards = 0;
        var first = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        await loop.HoldRunAsync(first, _ => cards++);
        loop.OwnScanType = DetectionScanType.Full;

        var held = await loop.HoldRunAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual), _ => cards++);

        Assert.Same(first, held);
        Assert.Equal(1, cards);
    }

    // A Run Now waiting from before a scan mode change does not take in a Games-page scan of the type it was
    // asked under: the Run Now runs the schedule's type now, the Games-page scan runs its own, Full first.
    [Fact]
    public async Task AGamesPageScanAfterAScanModeChange_DoesNotJoinTheWaitingRunNow()
    {
        using var loop = new LoopProbe("gameDetection") { OwnScanType = DetectionScanType.Incremental };
        var runNow = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        await loop.TryTriggerImmediateRunAsync(runNow);
        loop.OwnScanType = DetectionScanType.Full;
        var quick = new RunNotice(NotificationMode.All, RunTrigger.Manual) { RequestedScanType = DetectionScanType.Incremental };

        var admission = await loop.TryTriggerImmediateRunAsync(quick);

        Assert.Same(quick, admission.Retained);
        Assert.Null(runNow.RequestedScanType);
        Assert.Same(runNow, await loop.TakeRequestAsync());
        Assert.Same(quick, await loop.TakeRequestAsync());
    }

    // The same holds for runs held for a download: two holds, two cards.
    [Fact]
    public async Task AHeldGamesPageScanAfterAScanModeChange_DoesNotJoinTheHeldRunNow()
    {
        using var loop = new LoopProbe("gameDetection") { OwnScanType = DetectionScanType.Incremental };
        var cards = 0;
        var runNow = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        await loop.HoldRunAsync(runNow, _ => cards++);
        loop.OwnScanType = DetectionScanType.Full;
        var quick = new RunNotice(NotificationMode.All, RunTrigger.Manual) { RequestedScanType = DetectionScanType.Incremental };

        Assert.Same(quick, await loop.HoldRunAsync(quick, _ => cards++));
        Assert.Equal(2, cards);
        Assert.Null(runNow.RequestedScanType);
    }

    // A stored last full scan far ahead of the clock puts the hybrid week's end past what a timer can wait for;
    // reading the schedules must still answer.
    [Fact]
    public void AStoredFullScanTimeFarAheadOfTheClock_DoesNotBreakTheScheduleRead()
    {
        var lastFullScan = DateTime.UtcNow + TimeSpan.FromDays(60);
        var state = CacheScanGateHarness.CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.GetHiddenClientIps) => new List<string>(),
            nameof(IStateService.GetGlobalNotificationDisplayMode) => NotificationDisplayMode.Condensed,
            nameof(IStateService.GetGameDetectionScanMode) => GameDetectionScanMode.Hybrid,
            nameof(IStateService.GetGameDetectionLastFullScan) => lastFullScan,
            _ => null
        });
        using var loop = new LoopProbe("gameDetection");
        using var schedules = Install([loop], CacheScanGateHarness.Idle(), CreateTracker(), state);

        Assert.Contains(schedules.Service.GetAll(), schedule => schedule.Key == "gameDetection");
    }

    // A wake recorded before the loop takes a request was for that request. A wake recorded after the take, or
    // an interval change the loop has not handled yet, still cuts the next sleep short.
    [Theory]
    [InlineData("beforeTake", false)]
    [InlineData("afterTake", true)]
    [InlineData("scheduleChange", true)]
    public async Task AWake_CutsTheNextSleepShortUnlessItWasForTheRunJustTaken(string wake, bool cutsShort)
    {
        using var loop = new LoopProbe("gameDetection");
        if (wake == "scheduleChange") loop.ChangeSchedule();
        await loop.TryTriggerImmediateRunAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual));
        Assert.NotNull(await loop.TakeRequestAsync());
        if (wake == "afterTake")
        {
            Assert.True((await loop.TryTriggerImmediateRunAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual))).FollowUpQueued);
        }

        Assert.Equal(cutsShort, await loop.SleepAsync(TimeSpan.FromMilliseconds(300)));
    }

    // Run All counts a waiting detection as the schedule's run only when it is the scan type the schedule would
    // run (Full here, the default mode); a waiting scan of the other type leaves the schedule's own scan to be
    // requested.
    [Theory]
    [InlineData(DetectionScanType.Full, 0, 1)]
    [InlineData(DetectionScanType.Incremental, 1, 0)]
    public async Task RunAll_CountsAWaitingDetectionOnlyOfTheSchedulesScanType(
        DetectionScanType waiting, int triggered, int alreadyRunning)
    {
        using var loop = new LoopProbe("gameDetection");
        var tracker = CreateTracker();
        using var schedules = Install([loop], CacheScanGateHarness.Idle(), tracker);
        tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource(),
            initialStatus: OperationStatus.Waiting, detectionScanType: waiting);

        var result = await schedules.Service.TriggerAllAsync();

        Assert.Equal(triggered, result.TriggeredCount);
        Assert.Equal(alreadyRunning, result.AlreadyRunningCount);
    }

    // A detection request that arrives while an eviction scan runs its own detection step waits for the scan
    // and never joins the step.
    [Fact]
    public async Task ADetectionRequestDuringAnEvictionScansDetectionStep_WaitsForTheScan()
    {
        var tracker = CreateTracker();
        var queue = new OperationQueueService(
            tracker,
            OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationQueueService>.Instance);
        var scan = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource(),
            new GameDetectionMetrics { ParentOperationId = scan }, parentOperationId: scan,
            detectionScanType: DetectionScanType.Full);

        var response = await queue.EnqueueAsync(
            OperationType.GameDetection, ConflictScope.Bulk(), "Game Detection",
            () => Task.FromResult<Guid?>(null), CancellationToken.None, detectionScanType: DetectionScanType.Full);

        Assert.False(response.AlreadyRunning);
        Assert.True(response.Queued);
        var waiting = tracker.GetOperation(response.OperationId)!;
        Assert.Equal(OperationType.EvictionScan, waiting.BlockedByType);
    }

    // Game Detection's running dot lights during an eviction scan's detection step, though the step is
    // not the detection schedule's run.
    [Fact]
    public void TheDetectionDot_LightsDuringAnEvictionScansDetectionStep()
    {
        using var loop = new LoopProbe("gameDetection");
        var tracker = CreateTracker();
        var state = CacheScanGateHarness.CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.GetGameDetectionScanMode) => GameDetectionScanMode.Full,
            nameof(IStateService.GetGlobalNotificationDisplayMode) => NotificationDisplayMode.Condensed,
            _ => null
        });
        var schedules = new ServiceScheduleRegistry(
            [loop], state,DispatchProxy.Create<ISignalRNotificationService, QuietNotifications>(),
            ScheduleExecutionTestService.Create(), tracker);
        var scan = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource(),
            new GameDetectionMetrics { ParentOperationId = scan }, parentOperationId: scan);

        Assert.True(schedules.GetAll().Single(schedule => schedule.Key == "gameDetection").IsRunning);
        Assert.False(schedules.GetRunStatus("gameDetection")!.IsRunning);
    }

    // A Run Now that waits behind a busy run and is then refused at its start by a download hands its
    // waiting card to the declined row. The run's end then finds no waiting card to end as skipped, and a
    // hold raised after it links to the declined row, so one click shows one card.
    [Fact]
    public async Task ARequestWithAWaitingCardRefusedAtItsStart_HandsItsCardToTheDeclinedRow()
    {
        var tracker = CreateTracker();
        var queue = new OperationQueueService(
            tracker,
            OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationQueueService>.Instance);
        var notice = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        var waitingCard = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource(),
            initialStatus: OperationStatus.Waiting, notice: notice);
        notice.Attach(tracker, waitingCard);
        Task<Guid?> RefuseAsync() => throw new DownloadInProgressException("A download is in progress");

        await Assert.ThrowsAsync<DownloadInProgressException>(() => queue.EnqueueAsync(
            OperationType.GameDetection,
            ConflictScope.Bulk(),
            "Game Detection",
            RefuseAsync,
            CancellationToken.None,
            reportRefusal: true,
            notice: notice));

        Assert.NotEqual(waitingCard, notice.OperationId);
        Assert.Equal(OperationStatus.Skipped, tracker.GetOperation(notice.OperationId!.Value)!.Status);
        Assert.Equal(OperationStatus.Completed, tracker.GetOperation(waitingCard)!.Status);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The awaited state never arrived");
            await Task.Delay(10);
        }
    }

    // A hold whose card a person is dismissing is not released into a run, even before the cancel
    // reaches its notice.
    [Fact]
    public async Task AHoldWhoseCardIsBeingDismissed_IsNotReleased()
    {
        using var loop = new LoopProbe(EvictionKey);
        var tracker = CreateTracker();
        loop.Tracker = tracker;
        var held = new RunNotice(NotificationMode.All, RunTrigger.Scheduled);
        var card = Guid.Empty;
        await loop.HoldRunAsync(held, notice =>
        {
            // The card's token carries no dismissal callback, so the cancel stops at the card: the
            // moment between the card turning Cancelling and its notice being canceled.
            card = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
                initialStatus: OperationStatus.Waiting, notice: notice);
            notice.Attach(tracker, card);
        });
        tracker.CancelOperation(card);
        Assert.False(held.Cancelled);

        loop.ReleaseHeldRun(_ => null);

        Assert.False(await loop.HasRequestAsync());
    }

    // A request that joins a waiting run whose card a person is dismissing would ride on a run that
    // never happens, so the join is refused.
    [Fact]
    public void ARunWhoseCardIsBeingDismissed_TakesNoNewRequest()
    {
        var tracker = CreateTracker();
        var waiting = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        var card = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            initialStatus: OperationStatus.Waiting, notice: waiting);
        waiting.Attach(tracker, card);
        tracker.CancelOperation(card);

        Assert.False(waiting.Adopt(tracker, new RunNotice(NotificationMode.All, RunTrigger.Manual)));
    }

    // Two Run Now presses on a running scan, the second during a download: the first press's
    // follow-up meets the second press's hold at its gate, and its card closes into the hold's.
    [Fact]
    public async Task FollowUpTurnedAwayIntoAHold_ClosesItsCardIntoTheHolds()
    {
        using var loop = new LoopProbe(EvictionKey);
        var snapshot = new DownloadSpeedSnapshot();
        var tracker = CreateTracker();
        using var schedules = Install([loop], CacheScanGateHarness.With(snapshot), tracker);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = loop.RunAsync(RunTrigger.Scheduled, null, () => { started.TrySetResult(); return release.Task; });
        await started.Task.WaitAsync(Timeout);

        Assert.True((await schedules.Service.TriggerRunAsync(EvictionKey)).FollowUpQueued);
        var followUpCard = Assert.Single(tracker.GetWaitingOperations()).Id;
        CacheScanGateHarness.MakeBusy(snapshot);
        Assert.NotNull((await schedules.Service.TriggerRunAsync(EvictionKey)).SkippedReason);
        release.TrySetResult();
        await running;

        var followUp = await loop.TakeRequestAsync();
        Assert.NotNull(followUp);
        await loop.RunAsync(RunTrigger.Manual, followUp);

        Assert.NotEqual(OperationStatus.Waiting, tracker.GetOperation(followUpCard)!.Status);
        var holdCard = Assert.Single(tracker.GetWaitingOperations()).Id;
        // The follow-up's card joined the hold's: dismissing it leaves the hold waiting.
        Assert.Equal(OperationCancelResult.AlreadyFinished, tracker.CancelOperation(followUpCard));
        Assert.Equal(OperationStatus.Waiting, tracker.GetOperation(holdCard)!.Status);
        Assert.False(tracker.GetOperation(holdCard)!.Notice!.Cancelled);
        CacheScanGateHarness.MakeIdle(snapshot);
        RaiseDownloadsEnded();
        var held = await loop.TakeRequestAsync();
        Assert.NotNull(held);
        Assert.Equal(holdCard, held!.OperationId);
        await loop.RunAsync(RunTrigger.Manual, held);
        Assert.Empty(tracker.GetWaitingOperations());
        Assert.Equal(1, (await schedules.Service.TriggerAllAsync()).TriggeredCount);
    }

    // A held run released while the schedule already has a request waiting joins that request, and
    // its card is the request's card, so it closes when the request runs.
    [Fact]
    public async Task HoldReleasedBesideAWaitingRequest_RunsOnceOnTheHoldsCard()
    {
        using var loop = new LoopProbe(EvictionKey);
        var snapshot = new DownloadSpeedSnapshot();
        var tracker = CreateTracker();
        using var schedules = Install([loop], CacheScanGateHarness.With(snapshot), tracker);
        Assert.Equal(1, (await schedules.Service.TriggerAllAsync()).TriggeredCount);
        CacheScanGateHarness.MakeBusy(snapshot);
        Assert.NotNull((await schedules.Service.TriggerRunAsync(EvictionKey)).SkippedReason);
        var holdCard = Assert.Single(tracker.GetWaitingOperations()).Id;

        CacheScanGateHarness.MakeIdle(snapshot);
        RaiseDownloadsEnded();
        var request = await loop.TakeRequestAsync();
        Assert.NotNull(request);
        Assert.Equal(RunTrigger.Manual, request!.Trigger);
        await loop.RunAsync(RunTrigger.Manual, request);

        Assert.False(await loop.HasRequestAsync());
        Assert.Equal(1, loop.WorkRuns);
        Assert.Empty(tracker.GetWaitingOperations());
        Assert.NotEqual(OperationStatus.Waiting, tracker.GetOperation(holdCard)!.Status);
    }

    // Holds left over from a downloads-ended edge the process missed: Run All gives every held
    // schedule the same answer, whichever it asks first.
    [Fact]
    public async Task RunAllAfterAMissedEdge_CountsEveryHeldScheduleTheSameWay()
    {
        using var eviction = new LoopProbe(EvictionKey);
        using var cacheSize = new LoopProbe(CacheSizeKey);
        var snapshot = new DownloadSpeedSnapshot();
        CacheScanGateHarness.MakeBusy(snapshot);
        var tracker = CreateTracker();
        using var schedules = Install([eviction, cacheSize], CacheScanGateHarness.With(snapshot), tracker);
        Assert.Equal(2, (await schedules.Service.TriggerAllAsync()).SkippedCount);

        CacheScanGateHarness.MakeIdle(snapshot);
        var (triggered, alreadyRunning, skipped, _) = await schedules.Service.TriggerAllAsync();

        Assert.Equal(0, triggered);
        Assert.Equal(2, alreadyRunning);
        Assert.Equal(0, skipped);
        Assert.True(await eviction.HasRequestAsync());
        Assert.True(await cacheSize.HasRequestAsync());
    }

    // A Run All run held for a download and released while its schedule is busy is the busy run's own
    // work: no second run is queued, and its card closes when that run ends.
    [Fact]
    public async Task RunAllHoldReleasedIntoABusyLoop_RunsOnceAndClosesItsCard()
    {
        using var loop = new LoopProbe(EvictionKey);
        var snapshot = new DownloadSpeedSnapshot();
        CacheScanGateHarness.MakeBusy(snapshot);
        var tracker = CreateTracker();
        using var schedules = Install([loop], CacheScanGateHarness.With(snapshot), tracker);
        await schedules.Service.TriggerAllAsync();
        var held = Assert.Single(tracker.GetWaitingOperations());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = WithGate(AllowEveryRun, () =>
            loop.RunAsync(RunTrigger.Scheduled, null, () => { started.TrySetResult(); return release.Task; }));
        await started.Task.WaitAsync(Timeout);

        CacheScanGateHarness.MakeIdle(snapshot);
        RaiseDownloadsEnded();
        Assert.False(await loop.HasRequestAsync());
        Assert.Equal(OperationStatus.Waiting, tracker.GetOperation(held.Id)!.Status);
        release.TrySetResult();
        await running;

        Assert.False(await loop.HasRequestAsync());
        Assert.Equal(1, loop.WorkRuns);
        Assert.Empty(tracker.GetWaitingOperations());
    }

    // A held Run All run released while a scan is already doing the work closes into that scan's
    // card as a join: dismissing the held card, the way the browser's cancel does, leaves the scan
    // running.
    [Fact]
    public async Task HoldReleasedIntoARunningScan_ItsDismissalLeavesTheScanRunning()
    {
        using var loop = new LoopProbe(EvictionKey);
        var snapshot = new DownloadSpeedSnapshot();
        CacheScanGateHarness.MakeBusy(snapshot);
        var tracker = CreateTracker();
        using var schedules = Install([loop], CacheScanGateHarness.With(snapshot), tracker);
        await schedules.Service.TriggerAllAsync();
        var held = Assert.Single(tracker.GetWaitingOperations());
        var scanCts = new CancellationTokenSource();
        var scan = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", scanCts);

        CacheScanGateHarness.MakeIdle(snapshot);
        RaiseDownloadsEnded();
        await loop.ReadRunStatusAsync();

        Assert.Equal(scan, tracker.GetOperation(held.Id)!.NextOperationId);
        Assert.Equal(held.Id, tracker.GetOperation(held.Id, followHandoff: true)!.Id);
        Assert.Equal(OperationCancelResult.AlreadyFinished, tracker.CancelOperation(held.Id));
        held.Notice!.Cancel(tracker, held.Id);
        Assert.Equal(OperationStatus.Running, tracker.GetOperation(scan)!.Status);
        Assert.False(scanCts.IsCancellationRequested);
    }

    // The person dismissed the held card, and its dismissal has not reached the schedule yet: a Run
    // Now after downloads end is a new request, not the dismissed one.
    [Fact]
    public async Task RunNowAfterADismissedHold_IsANewRequest()
    {
        using var loop = new LoopProbe(EvictionKey);
        var snapshot = new DownloadSpeedSnapshot();
        CacheScanGateHarness.MakeBusy(snapshot);
        var tracker = CreateTracker();
        using var schedules = Install([loop], CacheScanGateHarness.With(snapshot), tracker);
        await schedules.Service.TriggerRunAsync(EvictionKey);
        var held = Assert.Single(tracker.GetWaitingOperations());
        held.Notice!.Cancel(tracker, held.Id);

        CacheScanGateHarness.MakeIdle(snapshot);
        var (status, skippedReason, _) = await schedules.Service.TriggerRunAsync(EvictionKey);

        Assert.Null(skippedReason);
        Assert.False(status.IsRunning);
        var request = await loop.TakeRequestAsync();
        Assert.NotNull(request);
        Assert.False(request!.Cancelled);
    }

    // The same dismissal gap during the download: a Run Now is held on a card of its own instead of
    // being folded into the dismissed hold.
    [Fact]
    public async Task RunNowOnADismissedHold_IsHeldOnItsOwnCard()
    {
        using var loop = new LoopProbe(EvictionKey);
        var tracker = CreateTracker();
        using var schedules = Install([loop], CacheScanGateHarness.Downloading(), tracker);
        await schedules.Service.TriggerRunAsync(EvictionKey);
        var dismissed = Assert.Single(tracker.GetWaitingOperations());
        dismissed.Notice!.Cancel(tracker, dismissed.Id);

        await schedules.Service.TriggerRunAsync(EvictionKey);

        Assert.Contains(tracker.GetWaitingOperations(), operation => operation.Id != dismissed.Id
            && operation.Notice is { Cancelled: false });
    }

    // An owed run beside a Run Now whose card was just dismissed: the loop takes the owed run, not the
    // dismissed request.
    [Fact]
    public async Task OwedRunBesideADismissedRequest_IsTheRunTheLoopTakes()
    {
        using var loop = new LoopProbe(EvictionKey);
        var tracker = CreateTracker();
        var request = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        var owed = new RunNotice(NotificationMode.All, RunTrigger.Scheduled);
        await loop.TryTriggerImmediateRunAsync(request);
        await OweAsync(loop, owed);
        request.Cancel(tracker, Guid.Empty);

        Assert.Null(await loop.TakeRequestAsync());
        var taken = await loop.TakeOwedAsync();
        Assert.NotNull(taken);
        Assert.Same(owed, taken);
    }

    // A run owed while the loop is idle is seen by the loop's peek after a run, so the loop takes it
    // instead of sleeping.
    [Fact]
    public async Task OwedRunPostedAfterARun_IsSeenBeforeTheSleep()
    {
        using var loop = new LoopProbe(EvictionKey);

        await OweAsync(loop, new RunNotice(NotificationMode.All, RunTrigger.Scheduled));

        Assert.True(await loop.HasRequestAsync());
    }

    // A run turned away by its own start because a download began is held again on a new card; the
    // download ends before the run has finished ending, and the run's end leaves that card for the
    // owed run instead of closing it as "nothing to do".
    [Fact]
    public async Task RunHeldAgainByItsOwnStart_KeepsItsNewCardThroughItsOwnEnd()
    {
        using var loop = new LoopProbe(EvictionKey);
        var snapshot = new DownloadSpeedSnapshot();
        var tracker = CreateTracker();
        using var schedules = Install([loop], CacheScanGateHarness.With(snapshot), tracker);
        RunNotice? run = null;
        Guid holdCard = default;

        await loop.RunAsync(RunTrigger.Scheduled, null, async () =>
        {
            run = loop.CurrentRunNotice;
            var scan = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(), notice: run);
            run.Attach(tracker, scan);
            CacheScanGateHarness.MakeBusy(snapshot);
            tracker.CompleteOperation(scan, success: true, skipped: true);
            holdCard = await WaitForWaitingCardAsync(tracker, run);
            CacheScanGateHarness.MakeIdle(snapshot);
            RaiseDownloadsEnded();
        });

        Assert.Equal(OperationStatus.Waiting, tracker.GetOperation(holdCard)!.Status);
        var owed = await loop.TakeOwedAsync();
        Assert.NotNull(owed);
        Assert.Same(run, owed);
    }

    // The start delegate attached its notice to the scan, then the scan was held again on a new card
    // before the queue's own attach ran: the notice keeps the hold's card.
    [Fact]
    public async Task QueueAttach_LeavesANoticeHeldAgainDuringItsStart()
    {
        var tracker = CreateTracker();
        var queue = new OperationQueueService(tracker,
            OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationQueueService>.Instance);
        var notice = new RunNotice(NotificationMode.All, RunTrigger.Scheduled);
        Guid holdCard = default;

        await queue.EnqueueAsync(OperationType.EvictionScan, ConflictScope.Bulk(), "Eviction Scan", () =>
        {
            var scan = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(), notice: notice);
            notice.Attach(tracker, scan);
            tracker.CompleteOperation(scan, success: true, skipped: true);
            holdCard = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
                initialStatus: OperationStatus.Waiting, notice: notice);
            notice.Attach(tracker, holdCard);
            return Task.FromResult<Guid?>(scan);
        }, CancellationToken.None, notice: notice);

        Assert.Equal(holdCard, notice.OperationId);
        Assert.Equal(OperationStatus.Waiting, tracker.GetOperation(holdCard)!.Status);
    }

    // A scheduled run held for a download and released while its loop is running is that run's own
    // work: nothing is owed afterwards, and the card closes with the run.
    [Fact]
    public async Task ScheduledHoldReleasedWhileTheLoopRuns_IsNotOwedAgain()
    {
        using var loop = new LoopProbe(EvictionKey);
        var snapshot = new DownloadSpeedSnapshot();
        CacheScanGateHarness.MakeBusy(snapshot);
        var tracker = CreateTracker();
        using var schedules = Install([loop], CacheScanGateHarness.With(snapshot), tracker);
        await HoldScheduledRunAsync(loop);
        CacheScanGateHarness.MakeIdle(snapshot);

        await WithGate(AllowEveryRun, () => loop.RunAsync(RunTrigger.Scheduled, null, () =>
        {
            RaiseDownloadsEnded();
            return Task.CompletedTask;
        }));

        Assert.Null(await loop.TakeOwedAsync());
        Assert.Equal(1, loop.WorkRuns);
        Assert.Empty(tracker.GetWaitingOperations());
    }

    // A held run released after the loop woke for its own tick, before the tick took a run, is that
    // tick's run rather than a second run after it.
    [Fact]
    public async Task HoldReleasedAsTheTickWakes_IsTheTicksRun()
    {
        using var loop = new LoopProbe(EvictionKey);
        var snapshot = new DownloadSpeedSnapshot();
        CacheScanGateHarness.MakeBusy(snapshot);
        var tracker = CreateTracker();
        using var schedules = Install([loop], CacheScanGateHarness.With(snapshot), tracker);
        await HoldScheduledRunAsync(loop);
        CacheScanGateHarness.MakeIdle(snapshot);
        RaiseDownloadsEnded();

        await WithGate(AllowEveryRun, () => loop.RunAsync(RunTrigger.Scheduled, null));

        Assert.Null(await loop.TakeOwedAsync());
        Assert.Empty(tracker.GetWaitingOperations());
    }

    // Downloads end in the startup run's gap, after it took its run and before its gate: the held Run
    // All run is the startup run's work, so nothing waits to run after it.
    [Fact]
    public async Task HoldReleasedInTheStartupGap_IsTheStartupRunsWork()
    {
        using var loop = new LoopProbe(EvictionKey, runOnStartup: true);
        var snapshot = new DownloadSpeedSnapshot();
        CacheScanGateHarness.MakeBusy(snapshot);
        var tracker = CreateTracker();
        using var schedules = Install([loop], CacheScanGateHarness.With(snapshot), tracker);
        Assert.Equal(1, (await schedules.Service.TriggerAllAsync()).SkippedCount);
        CacheScanGateHarness.MakeIdle(snapshot);
        var requestWaiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.Startup = async _ => requestWaiting.TrySetResult(await loop.HasRequestAsync());
        var gate = ScheduledServiceBase.ScheduleRunGate!;
        ScheduledServiceBase.ScheduleRunGate = (key, trigger) =>
        {
            if (key == EvictionKey && trigger == RunTrigger.Startup) RaiseDownloadsEnded();
            return gate(key, trigger);
        };

        await loop.StartAsync(CancellationToken.None);
        try
        {
            Assert.False(await requestWaiting.Task.WaitAsync(Timeout));
        }
        finally
        {
            await loop.StopAsync(CancellationToken.None);
        }
    }

    // Run All while the loop has taken a run that has not reached its gate counts the schedule as
    // running, and leaves the held scheduled run for that run instead of queueing it behind it.
    [Fact]
    public async Task RunAllBesideATakenRun_LeavesTheHeldRunToThatRun()
    {
        using var loop = new LoopProbe(EvictionKey);
        var snapshot = new DownloadSpeedSnapshot();
        CacheScanGateHarness.MakeBusy(snapshot);
        var tracker = CreateTracker();
        using var schedules = Install([loop], CacheScanGateHarness.With(snapshot), tracker);
        await HoldScheduledRunAsync(loop);
        var holdCard = Assert.Single(tracker.GetWaitingOperations()).Id;
        CacheScanGateHarness.MakeIdle(snapshot);
        Assert.True((await loop.TryTriggerImmediateRunAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual))).Admitted);
        var taken = await loop.TakeRequestAsync();
        Assert.NotNull(taken);

        var (triggered, alreadyRunning, _, _) = await schedules.Service.TriggerAllAsync();

        Assert.Equal(0, triggered);
        Assert.Equal(1, alreadyRunning);
        Assert.False(await loop.HasRequestAsync());
        RaiseDownloadsEnded();
        Assert.False(await loop.HasRequestAsync());
        Assert.Null(await loop.TakeOwedAsync());
        Assert.Equal(holdCard, taken!.OperationId);
    }

    // A held run released while the schedule's scan is being canceled is not merged into the canceled
    // scan: its join onto that scan is refused, and it waits for the loop and runs.
    [Fact]
    public async Task HoldReleasedWhileTheScanIsCanceled_IsQueued()
    {
        using var loop = new LoopProbe(EvictionKey);
        var snapshot = new DownloadSpeedSnapshot();
        var tracker = CreateTracker();
        using var schedules = Install([loop], CacheScanGateHarness.With(snapshot), tracker);
        var scan = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        CacheScanGateHarness.MakeBusy(snapshot);
        Assert.NotNull((await schedules.Service.TriggerRunAsync(EvictionKey)).SkippedReason);
        var held = Assert.Single(tracker.GetWaitingOperations()).Id;
        Assert.Equal(OperationCancelResult.Requested, tracker.CancelOperation(scan));

        CacheScanGateHarness.MakeIdle(snapshot);
        loop.ReleaseHeldRun(_ => scan);
        await loop.ReadRunStatusAsync();

        Assert.Equal(held, tracker.GetOperation(held, followHandoff: true)!.Id);
        Assert.NotEqual(scan, tracker.GetOperation(held)!.NextOperationId);
        Assert.True(await loop.HasRequestAsync());
    }

    // The same release through its other branch: the run the loop took has a scan that is being
    // canceled, so the held run cannot join it and is queued on its own card.
    [Fact]
    public async Task HeldRunReleasedBesideAPassWhoseScanIsCanceled_Queues()
    {
        using var loop = new LoopProbe(EvictionKey);
        var tracker = CreateTracker();
        using var schedules = Install([loop], CacheScanGateHarness.Idle(), tracker);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<RunNotice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = loop.RunAsync(RunTrigger.Scheduled, null, () =>
        {
            started.TrySetResult(loop.CurrentRunNotice);
            return release.Task;
        });
        try
        {
            var pass = await started.Task.WaitAsync(Timeout);
            var scan = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
            pass.Attach(tracker, scan);
            var held = new RunNotice(NotificationMode.All, RunTrigger.RunAll);
            await loop.HoldRunAsync(held, notice =>
            {
                var card = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
                    initialStatus: OperationStatus.Waiting, notice: notice);
                notice.PendingId = card;
                notice.Attach(tracker, card);
            });
            var heldCard = held.OperationId!.Value;
            Assert.Equal(OperationCancelResult.Requested, tracker.CancelOperation(scan));

            loop.ReleaseHeldRun(_ => null);
            await loop.ReadRunStatusAsync();

            Assert.Equal(OperationStatus.Waiting, tracker.GetOperation(heldCard)!.Status);
            Assert.Null(tracker.GetOperation(heldCard)!.NextOperationId);
            Assert.True(await loop.HasRequestAsync());
        }
        finally
        {
            release.TrySetResult();
            await running;
        }
    }

    // A held card released after its download stops naming the prefill it waited for.
    [Fact]
    public async Task ReleasedHold_StopsNamingTheFinishedPrefill()
    {
        using var loop = new LoopProbe(EvictionKey);
        var snapshot = new DownloadSpeedSnapshot();
        CacheScanGateHarness.MakeBusy(snapshot);
        var tracker = CreateTracker();
        tracker.RegisterOperation(OperationType.ScheduledPrefill, "Scheduled Prefill", new CancellationTokenSource());
        using var schedules = Install([loop], CacheScanGateHarness.With(snapshot), tracker);
        await HoldScheduledRunAsync(loop);
        var held = Assert.Single(tracker.GetWaitingOperations());
        Assert.Equal("Scheduled Prefill", held.BlockedByName);

        CacheScanGateHarness.MakeIdle(snapshot);
        RaiseDownloadsEnded();
        await loop.ReadRunStatusAsync();

        Assert.Equal(OperationStatus.Waiting, tracker.GetOperation(held.Id)!.Status);
        Assert.Null(tracker.GetOperation(held.Id)!.BlockedByName);
    }

    // A run held for a download says so on its row, even when no blocker can be named, and stops
    // saying it once the download ends and the hold is released.
    [Fact]
    public async Task HeldRun_SaysItWaitsForDownloadsUntilReleased()
    {
        using var loop = new LoopProbe(EvictionKey);
        var snapshot = new DownloadSpeedSnapshot();
        CacheScanGateHarness.MakeBusy(snapshot);
        var tracker = CreateTracker();
        using var schedules = Install([loop], CacheScanGateHarness.With(snapshot), tracker);
        await HoldScheduledRunAsync(loop);
        var held = Assert.Single(tracker.GetWaitingOperations());
        Assert.Null(held.BlockedByName);
        Assert.True(Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == held.Id).WaitingForDownload);

        CacheScanGateHarness.MakeIdle(snapshot);
        RaiseDownloadsEnded();
        await loop.ReadRunStatusAsync();

        Assert.False(Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == held.Id).WaitingForDownload);
    }

    // With Run on startup on, a person cancels the startup eviction scan and presses Run All before it
    // stops: Run All counts the schedule as started, shows the queued run's own waiting card at once,
    // and the run starts once the canceled scan ends, closing that card into its own scan.
    [Fact]
    public async Task RunAllWhileTheStartupScanIsCanceled_RunsAfterIt()
    {
        using var loop = new LoopProbe(EvictionKey, runOnStartup: true);
        var tracker = CreateTracker();
        using var schedules = Install([loop], CacheScanGateHarness.Idle(), tracker);
        var scanRegistered = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextRun = new TaskCompletionSource<RunTrigger>(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid nextScan = default;
        loop.Startup = async _ =>
        {
            var startup = loop.CurrentRunNotice;
            var cts = new CancellationTokenSource();
            var scan = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", cts, notice: startup);
            cts.Token.Register(() => startup.Cancel(tracker, scan));
            startup.Attach(tracker, scan);
            scanRegistered.TrySetResult(scan);
            await scanEnded.Task;
        };
        loop.Work = () =>
        {
            nextScan = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
                notice: loop.CurrentRunNotice);
            loop.CurrentRunNotice.Attach(tracker, nextScan);
            nextRun.TrySetResult(loop.CurrentRunNotice.Trigger);
            return Task.CompletedTask;
        };

        await loop.StartAsync(CancellationToken.None);
        try
        {
            var scan = await scanRegistered.Task.WaitAsync(Timeout);
            Assert.Equal(OperationCancelResult.Requested, tracker.CancelOperation(scan));

            var (triggered, alreadyRunning, _, _) = await schedules.Service.TriggerAllAsync();

            Assert.Equal(1, triggered);
            Assert.Equal(0, alreadyRunning);
            var card = Assert.Single(tracker.GetWaitingOperations());
            Assert.Equal(RunTrigger.RunAll, card.Notice!.Trigger);
            tracker.CompleteOperation(scan, success: false, cancelled: true);
            scanEnded.TrySetResult();
            Assert.Equal(RunTrigger.RunAll, await nextRun.Task.WaitAsync(Timeout));
            Assert.Equal(nextScan, tracker.GetOperation(card.Id)!.NextOperationId);
        }
        finally
        {
            scanEnded.TrySetResult();
            await loop.StopAsync(CancellationToken.None);
        }
    }

    // A download that ends at the moment a hold is placed: every read of the download gate stays off
    // the run queue's reader, so the edge it raises never runs there and the queue keeps answering
    // (row 1); and an edge raised between the gate's answer and the hold releases the hold (row 2).
    [Theory]
    [InlineData("ReadRunMessagesAsync")]
    [InlineData("HoldRefusedRunAsync")]
    public async Task GateReadsStayOffTheReader_AndALateEdgeReleasesTheHold(string expiringFrame)
    {
        var start = DateTimeOffset.UtcNow;
        var clock = new StackClock(start.AddSeconds(1), start.AddSeconds(20), expiringFrame);
        var snapshot = new DownloadSpeedSnapshot();
        CacheScanGateHarness.MakeBusy(snapshot);
        var speed = CacheScanGateHarness.TrackerWith(snapshot, [], clock);
        CacheScanGateHarness.SetField(speed, "_previousHadUnfilteredActivity", true);
        CacheScanGateHarness.SetField(speed, "_previousHadActivity", true);
        CacheScanGateHarness.SetField(speed, "_edgeRevision", snapshot.Revision);
        var gate = CacheScanGateHarness.GateOver(speed);

        // Only this case's registry hears the edge, so registries other cases left subscribed stay quiet.
        var field = typeof(RustSpeedTrackerService).GetField(nameof(RustSpeedTrackerService.DownloadsEnded),
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        var otherHandlers = (Delegate?)field.GetValue(null);
        field.SetValue(null, null);
        var edges = 0;
        try
        {
            RustSpeedTrackerService.DownloadsEnded += () => Interlocked.Increment(ref edges);
            using var loop = new LoopProbe(EvictionKey);
            var tracker = CreateTracker();
            using var schedules = Install([loop], gate, tracker);

            var call = Task.Run(() => schedules.Service.TriggerRunAsync(EvictionKey));
            var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(3))) == call;

            if (expiringFrame == "ReadRunMessagesAsync")
            {
                Assert.True(finished, $"Run Now did not answer; edges={edges}");
                Assert.Equal(0, Interlocked.Read(ref clock.ReaderReads));
                var status = loop.ReadRunStatusAsync();
                Assert.Same(status, await Task.WhenAny(status, Task.Delay(TimeSpan.FromSeconds(3))));
            }
            else
            {
                Assert.True(finished, $"Run Now did not answer; edges={edges}");
                Assert.Equal(1, edges);
                Assert.Null((await loop.ReadRunStatusAsync()).HeldCard);
                Assert.True(await loop.HasRequestAsync());
            }
        }
        finally
        {
            field.SetValue(null, otherHandlers);
        }
    }

    // A downloads-ended edge raised from inside the run queue's own reader (here, from the run's end
    // report) posts its releases instead of waiting on the queue that is raising it.
    [Fact]
    public async Task DownloadsEndedRaisedOnTheReader_DoesNotHang()
    {
        using var loop = new LoopProbe(EvictionKey);
        var tracker = CreateTracker();
        using var schedules = Install([loop], CacheScanGateHarness.Idle(), tracker);
        var report = loop.RunCompleted!;
        loop.RunCompleted = (notice, key, error, canceled, kept) =>
        {
            RaiseDownloadsEnded();
            report(notice, key, error, canceled, kept);
        };

        await loop.RunAsync(RunTrigger.Scheduled, null).WaitAsync(Timeout);

        await loop.ReadRunStatusAsync().WaitAsync(Timeout);
    }

    // A pass of a schedule whose pass is mostly a poll (here, one that found nothing due) is not doing
    // the work, so Run Now during it starts a run; while the pass is doing the work, Run Now answers
    // "already running" and queues nothing.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunNowDuringAPollThatFindsNothingDue_Starts(bool passWorking)
    {
        using var loop = new LoopProbe(LogRotationKey, queueManualRuns: false) { PassWorking = passWorking };
        var tracker = CreateTracker();
        using var schedules = Install([loop], CacheScanGateHarness.Idle(), tracker);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = loop.RunAsync(RunTrigger.Scheduled, null, () => { started.TrySetResult(); return release.Task; });
        await started.Task.WaitAsync(Timeout);

        var (status, _, followUpQueued) = await schedules.Service.TriggerRunAsync(LogRotationKey);
        release.TrySetResult();
        await running;

        Assert.Equal(passWorking, status.IsRunning);
        Assert.False(followUpQueued);
        Assert.Equal(!passWorking, await loop.HasRequestAsync());
    }

    // The same poll seen by Run All: started while the pass is only polling, already running while it
    // does the work.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAllDuringAPollThatFindsNothingDue_Starts(bool passWorking)
    {
        using var loop = new LoopProbe(LogRotationKey, queueManualRuns: false) { PassWorking = passWorking };
        var tracker = CreateTracker();
        using var schedules = Install([loop], CacheScanGateHarness.Idle(), tracker);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = loop.RunAsync(RunTrigger.Scheduled, null, () => { started.TrySetResult(); return release.Task; });
        await started.Task.WaitAsync(Timeout);

        var (triggered, alreadyRunning, _, _) = await schedules.Service.TriggerAllAsync();
        release.TrySetResult();
        await running;

        Assert.Equal(passWorking ? 0 : 1, triggered);
        Assert.Equal(passWorking ? 1 : 0, alreadyRunning);
    }

    // A Run Now dismissed before the sleeping schedule takes it runs nothing: the wake it caused does
    // not start a scheduled pass in its place.
    [Fact]
    public async Task ARequestDismissedBeforeTheTake_RunsNothing()
    {
        using var loop = new LoopProbe(EvictionKey);
        await loop.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => loop.NextRunUtc is not null, "The loop did not start its first sleep");
            var notice = new RunNotice(loop.EffectiveNotificationMode, RunTrigger.Manual);
            notice.Cancel(null, Guid.Empty);

            await loop.TryTriggerImmediateRunAsync(notice);
            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Equal(0, loop.WorkRuns);
        }
        finally
        {
            await loop.StopAsync(CancellationToken.None);
        }
    }

    // A Run Now that woke the schedule and then failed is retried after the error back-off, the way a
    // failed scheduled run is.
    [Fact]
    public async Task AFailedRunAfterAWake_IsRetried()
    {
        using var loop = new LoopProbe(EvictionKey);
        loop.Work = () => loop.WorkRuns == 1
            ? Task.FromException(new InvalidOperationException("first run fails"))
            : Task.CompletedTask;
        await loop.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => loop.NextRunUtc is not null, "The loop did not start its first sleep");

            await loop.TryTriggerImmediateRunAsync(new RunNotice(loop.EffectiveNotificationMode, RunTrigger.Manual));

            await WaitUntilAsync(() => loop.WorkRuns == 2, "The failed run was not retried");
        }
        finally
        {
            await loop.StopAsync(CancellationToken.None);
        }
    }

    // A Run Now dismissed during a failed run's back-off wakes the loop but does not bring the retry
    // forward: the retry runs at the end of the back-off.
    [Fact]
    public async Task ADismissedRunNowDuringTheErrorBackOff_KeepsTheRetryDelay()
    {
        using var loop = new LoopProbe(EvictionKey) { RetryDelay = TimeSpan.FromSeconds(2) };
        loop.Work = () => loop.WorkRuns == 1
            ? Task.FromException(new InvalidOperationException("first run fails"))
            : Task.CompletedTask;
        await loop.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => loop.NextRunUtc is not null, "The loop did not start its first sleep");
            await loop.TryTriggerImmediateRunAsync(new RunNotice(loop.EffectiveNotificationMode, RunTrigger.Manual));
            await WaitUntilAsync(() => loop.WorkRuns == 1, "The first run did not start");
            await WaitUntilAsync(() => loop.NextRunUtc < DateTime.UtcNow + TimeSpan.FromMinutes(1),
                "The failed run did not start its back-off");
            var retryAt = loop.NextRunUtc!.Value;

            var dismissed = new RunNotice(loop.EffectiveNotificationMode, RunTrigger.Manual);
            dismissed.Cancel(null, Guid.Empty);
            await loop.TryTriggerImmediateRunAsync(dismissed);
            await Task.Delay(500);

            Assert.Equal(1, loop.WorkRuns);
            await WaitUntilAsync(() => loop.WorkRuns == 2, "The failed run was not retried");
            Assert.True(DateTime.UtcNow >= retryAt - TimeSpan.FromMilliseconds(50));
        }
        finally
        {
            await loop.StopAsync(CancellationToken.None);
        }
    }

    // A live Run Now during the back-off ends it, so the run starts at once instead of after the delay.
    [Fact]
    public async Task ALiveRunNowDuringTheErrorBackOff_RunsAtOnce()
    {
        using var loop = new LoopProbe(EvictionKey) { RetryDelay = TimeSpan.FromHours(1) };
        loop.Work = () => loop.WorkRuns == 1
            ? Task.FromException(new InvalidOperationException("first run fails"))
            : Task.CompletedTask;
        await loop.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => loop.NextRunUtc is not null, "The loop did not start its first sleep");
            var firstSleepEnds = loop.NextRunUtc;
            await loop.TryTriggerImmediateRunAsync(new RunNotice(loop.EffectiveNotificationMode, RunTrigger.Manual));
            await WaitUntilAsync(() => loop.WorkRuns == 1, "The first run did not start");
            // The failure points the countdown at the retry instead of the first sleep's end.
            await WaitUntilAsync(() => loop.NextRunUtc != firstSleepEnds, "The failed run did not start its back-off");
            await Task.Delay(100);

            await loop.TryTriggerImmediateRunAsync(new RunNotice(loop.EffectiveNotificationMode, RunTrigger.Manual));

            await WaitUntilAsync(() => loop.WorkRuns == 2, "The Run Now did not cut the back-off short");
        }
        finally
        {
            await loop.StopAsync(CancellationToken.None);
        }
    }

    // A scheduled run whose time arrives while the schedule handles a wake (here, from a dismissed Run
    // Now) still runs then, instead of moving to the next occurrence.
    [Fact]
    public async Task AWakeHandledAfterTheDueTime_RunsTheDueWork()
    {
        using var loop = new LoopProbe(EvictionKey) { ProbeInterval = TimeSpan.FromMilliseconds(600) };
        await loop.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => loop.NextRunUtc is not null, "The loop did not start its first sleep");
            var due = loop.NextRunUtc!.Value;
            loop.ProbeInterval = TimeSpan.FromHours(1);
            // The woken loop reads its interval only after the time its sleep was waiting for.
            loop.HoldIntervalReadUntil = due + TimeSpan.FromMilliseconds(100);
            var dismissed = new RunNotice(loop.EffectiveNotificationMode, RunTrigger.Manual);
            dismissed.Cancel(null, Guid.Empty);

            await loop.TryTriggerImmediateRunAsync(dismissed);

            await WaitUntilAsync(() => loop.WorkRuns == 1, "The due run did not run");
        }
        finally
        {
            await loop.StopAsync(CancellationToken.None);
        }
    }

    // Run Now wakes a sleeping schedule from the run queue's reader; the woken loop goes on off that
    // reader, so its next step never runs inside the reader's handling of the request. This guards the
    // ordering only: the test also passes without the explicit yield in the sleep, because a canceled
    // delay already resumes off the canceling thread on this runtime.
    [Fact]
    public async Task AWakeFromTheReader_ResumesTheLoopOffTheReader()
    {
        var logger = new CapturingLogger<LoopProbe>();
        var woke = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ScheduledServiceBase.Admission>? admission = null;
        var onReader = false;
        // The reader answers the Run Now only after its handling returns. A woken loop that runs inline
        // inside that handling waits here for an answer its own thread has not given yet, so the wait
        // runs out; a loop that resumed elsewhere sees the answer arrive.
        logger.OnLogged = entry =>
        {
            if (!entry.Message.Contains("sleep interrupted")) return;
            onReader = !SpinWait.SpinUntil(() => Volatile.Read(ref admission) is { IsCompleted: true }, TimeSpan.FromSeconds(2));
            woke.TrySetResult();
        };
        using var loop = new LoopProbe(EvictionKey, logger: logger);
        // Started off the test framework's synchronization context, as the host starts it; under that
        // context every continuation is posted, which would hide where the woken loop resumes.
        await Task.Run(() => loop.StartAsync(CancellationToken.None));
        try
        {
            await WaitUntilAsync(() => loop.NextRunUtc is not null, "The loop did not start its first sleep");
            await Task.Delay(200);

            Volatile.Write(ref admission, loop.TryTriggerImmediateRunAsync(new RunNotice(loop.EffectiveNotificationMode, RunTrigger.Manual)));
            await admission!.WaitAsync(Timeout);

            await woke.Task.WaitAsync(Timeout);
            Assert.False(onReader);
        }
        finally
        {
            await loop.StopAsync(CancellationToken.None);
        }
    }

    // Holds a scheduled run through the registry's own gate, the way the loop's tick is refused.
    private static async Task HoldScheduledRunAsync(LoopProbe loop)
    {
        loop.SelectRunNotice(new RunNotice(loop.EffectiveNotificationMode, RunTrigger.Scheduled));
        Assert.NotNull(await ScheduledServiceBase.ScheduleRunGate!(EvictionKey, RunTrigger.Scheduled));
    }

    // Owes a run the way production does: held for a download, then released with the loop idle.
    private static async Task OweAsync(LoopProbe loop, RunNotice notice)
    {
        await loop.HoldRunAsync(notice, _ => { });
        loop.ReleaseHeldRun(_ => null);
    }

    private static async Task<Guid> WaitForWaitingCardAsync(UnifiedOperationTracker tracker, RunNotice notice)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            var card = tracker.GetWaitingOperations().FirstOrDefault(operation => ReferenceEquals(operation.Notice, notice));
            if (card is not null) return card.Id;
            Assert.True(DateTime.UtcNow < deadline, "The run was not held again");
            await Task.Delay(10);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(10);
        }
    }

    private static Task<string?> AllowEveryRun(string key, RunTrigger trigger) => Task.FromResult<string?>(null);

    // The schedule gate is a process-wide static, so it is always put back.
    private static async Task<T> WithGate<T>(Func<string, RunTrigger, Task<string?>> gate, Func<Task<T>> body)
    {
        var previous = ScheduledServiceBase.ScheduleRunGate;
        ScheduledServiceBase.ScheduleRunGate = gate;
        try
        {
            return await body();
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previous;
        }
    }

    private static void RaiseDownloadsEnded()
    {
        var field = typeof(RustSpeedTrackerService).GetField(
            nameof(RustSpeedTrackerService.DownloadsEnded),
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        ((Action?)field!.GetValue(null))?.Invoke();
    }

    private static UnifiedOperationTracker CreateTracker()
        => new(new ProcessManager(NullLogger<ProcessManager>.Instance), NullLogger<UnifiedOperationTracker>.Instance);

    // Keeps the registry's gate installed for the test's loops and puts the previous hooks back after.
    private static InstalledSchedules Install(
        IReadOnlyList<ScheduledServiceBase> loops,
        CacheScanGate gate,
        UnifiedOperationTracker tracker,
        IStateService? state = null,
        ISignalRNotificationService? notifications = null)
    {
        var previousGate = ScheduledServiceBase.ScheduleRunGate;
        var previousWait = ScheduledServiceBase.WaitForDownloadAnswer;
        var service = new ServiceScheduleRegistry(
            loops,
            state ?? CacheScanGateHarness.VisibleClientsStateService(),
            notifications ?? DispatchProxy.Create<ISignalRNotificationService, QuietNotifications>(),
            ScheduleExecutionTestService.Create(),
            tracker,
            activityRegistry: null,
            cacheScanGate: gate);
        // The harness tracker has nothing to report, so a startup run asks at once.
        ScheduledServiceBase.WaitForDownloadAnswer = null;
        return new InstalledSchedules(service, previousGate, previousWait);
    }

    private sealed class InstalledSchedules(
        ServiceScheduleRegistry service,
        Func<string, RunTrigger, Task<string?>>? previousGate,
        Func<string, CancellationToken, Task>? previousWait) : IDisposable
    {
        public ServiceScheduleRegistry Service { get; } = service;

        public void Dispose()
        {
            ScheduledServiceBase.ScheduleRunGate = previousGate;
            ScheduledServiceBase.WaitForDownloadAnswer = previousWait;
        }
    }

    public class QuietNotifications : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod!.ReturnType == typeof(Task) ? Task.CompletedTask : null;
    }

    // Answers the later time to every read made while the named frame is on the stack, and counts the
    // reads made on a run queue's reader. Relies on the frame names a Debug build keeps.
    private sealed class StackClock(DateTimeOffset before, DateTimeOffset after, string expiringFrame) : TimeProvider
    {
        public long ReaderReads;

        public override DateTimeOffset GetUtcNow()
        {
            var stack = new StackTrace().ToString();
            if (stack.Contains("ReadRunMessagesAsync")) Interlocked.Increment(ref ReaderReads);
            return stack.Contains(expiringFrame) ? after : before;
        }
    }

    private sealed class LoopProbe(
        string serviceKey,
        bool runOnStartup = false,
        bool queueManualRuns = true,
        ILogger<LoopProbe>? logger = null)
        : ScheduledBackgroundService(logger ?? NullLogger<LoopProbe>.Instance, new ConfigurationBuilder().Build())
    {
        public override string ServiceKey => serviceKey;
        protected override string ServiceName => serviceKey;
        protected override TimeSpan StartupDelay => TimeSpan.Zero;
        public TimeSpan RetryDelay { get; set; } = TimeSpan.Zero;
        protected override TimeSpan ErrorRetryDelay => RetryDelay;
        protected override bool QueueManualRuns => queueManualRuns;
        public override bool DefaultRunOnStartup => runOnStartup;

        public TimeSpan ProbeInterval { get; set; } = TimeSpan.FromHours(1);
        // When set, the next interval read waits until this time, standing in for a loop that is slow
        // to reach its due check after a wake.
        public DateTime? HoldIntervalReadUntil { get; set; }

        protected override TimeSpan Interval
        {
            get
            {
                if (HoldIntervalReadUntil is { } until)
                {
                    HoldIntervalReadUntil = null;
                    var wait = until - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                }
                return ProbeInterval;
            }
        }

        // Whether the pass the loop took is doing the schedule's work; false stands for a pass that is
        // only polling.
        public bool PassWorking { get; set; } = true;

        protected internal override bool IsDoingWork(RunNotice? taken) => PassWorking && base.IsDoingWork(taken);

        // The scan type a run that named none resolves to, standing for a detection schedule's own mode.
        public DetectionScanType? OwnScanType { get; set; }

        protected internal override DetectionScanType? ScanTypeOf(RunNotice notice) => notice.RequestedScanType ?? OwnScanType;

        public Task<bool> SleepAsync(TimeSpan delay) => InterruptibleDelayAsync(delay, CancellationToken.None);

        public void ChangeSchedule() => WakeForScheduleChange();

        public Func<Task>? Work { get; set; }
        public Func<CancellationToken, Task>? Startup { get; set; }
        public int WorkRuns { get; private set; }
        public Task<bool> HasRequestAsync() => HasPendingRunAsync();

        public Task<RunNotice?> TakeRequestAsync() => ConsumePendingManualRunAsync();
        public Task<RunNotice?> TakeOwedAsync() => ConsumePendingDeferredRunAsync();

        public Task<(bool ShuttingDown, bool RunFailed)> RunAsync(RunTrigger trigger, RunNotice? notice, Func<Task>? work = null)
            => RunScheduledWorkAsync(ServiceKey, trigger, _ =>
            {
                WorkRuns++;
                return work?.Invoke() ?? Task.CompletedTask;
            }, CancellationToken.None, "{ServiceName} probe run failed", () => { }, notice);

        protected override Task OnStartupAsync(CancellationToken stoppingToken)
            => Startup?.Invoke(stoppingToken) ?? Task.CompletedTask;

        protected override Task ExecuteWorkAsync(CancellationToken stoppingToken)
        {
            WorkRuns++;
            return Work?.Invoke() ?? Task.CompletedTask;
        }
    }
}
