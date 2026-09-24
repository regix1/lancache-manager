using System.Reflection;
using System.Runtime.CompilerServices;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Platform;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using static LancacheManager.Tests.CacheScanGateHarness;

namespace LancacheManager.Tests;

/// <summary>
/// The first phase of every eviction scan is a full game detection run under the scan's own
/// operation: started with its card hidden, its percent forwarded onto the scan card, waited on
/// until its terminal, and cancelled along with the scan. The tracker here is a stand-in the test
/// drives, so the detection stays "running" exactly as long as each test wants it to.
/// </summary>
// A registry holding a run starts it when the process-wide downloads-ended event fires, so these
// tests share that event's collection and never see another class raise it mid-test.
[Collection(nameof(DownloadsEndedEventCollection))]
public sealed class CacheScanDetectionPhaseTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RemovalStartsUnderActiveScanAndCancellationKeepsCompetitorsWaiting(bool cancel, bool populated)
    {
        using var ctx = new PhaseContext();
        await using var database = await TestDatabase.CreateAsync();
        await using var context = new AppDbContext(database.Options);
        foreach (var platform in populated ? Enum.GetValues<PrefillPlatform>() : [])
        {
            context.PrefillCachedApps.Add(new PrefillCachedApp
            {
                Platform = platform, AppId = "123", AppName = "Removed", CachedAtUtc = DateTime.UtcNow
            });
            context.PrefillCachedApps.Add(new PrefillCachedApp
            {
                Platform = platform, AppId = "456", AppName = "Kept", CachedAtUtc = DateTime.UtcNow
            });
            context.Downloads.Add(new Download
            {
                Service = platform.ToService(), ClientIp = "127.0.0.1", Datasource = "Default",
                GameAppId = platform == PrefillPlatform.Steam ? 123 : null,
                EpicAppId = platform == PrefillPlatform.Epic ? "123" : null,
                XboxProductId = platform == PrefillPlatform.Xbox ? "123" : null,
                GameName = "Removed", IsEvicted = true, StartTimeUtc = DateTime.UtcNow, EndTimeUtc = DateTime.UtcNow
            });
        }
        await context.SaveChangesAsync();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        var scanId = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        var queue = new OperationQueueService(tracker,
            new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationQueueService>.Instance);
        var promoted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = await queue.EnqueueAsync(OperationType.CacheSizeScan, ConflictScope.Bulk(), "Cache File Scan",
            () => { promoted.TrySetResult(); return Task.FromResult<Guid?>(Guid.NewGuid()); }, CancellationToken.None);
        Assert.True(queued.Queued);
        var terminals = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        var childTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.OperationTerminal += operation =>
        {
            terminals.Add(operation.Id);
            if (operation.Type == OperationType.EvictionRemoval) childTerminal.TrySetResult();
            if (operation.Id == scanId) scanTerminal.TrySetResult();
        };
        Guid removalId = default;
        ctx.Notifications.OnSent = (eventName, value) =>
        {
            if (eventName != SignalREvents.EvictionRemovalStarted || value is not EvictionRemovalStarted started) return;
            removalId = started.OperationId;
            Assert.Equal(OperationStatus.Running, tracker.GetOperation(scanId)!.Status);
            Assert.Equal(OperationStatus.Running, tracker.GetOperation(removalId)!.Status);
            Assert.False(promoted.Task.IsCompleted);
            if (cancel) tracker.CancelOperation(removalId);
        };
        await ctx.Scan.RemoveEvictedRecordsAsync(context, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(Guid.Empty, removalId);
        // Without cancellation removal reaches the fixture's absent summary service after commit.
        Assert.Equal(cancel ? OperationStatus.Cancelled : OperationStatus.Failed, tracker.GetOperation(removalId)!.Status);
        Assert.Equal(populated ? cancel ? 10 : 5 : 0, await context.PrefillCachedApps.CountAsync());
        if (!cancel) Assert.All(await context.PrefillCachedApps.AsNoTracking().ToListAsync(), app => Assert.Equal("456", app.AppId));
        Assert.Equal(OperationStatus.Running, tracker.GetOperation(scanId)!.Status);
        Assert.False(promoted.Task.IsCompleted);
        Assert.Equal(queued.OperationId, Assert.Single(tracker.GetWaitingOperations()).Id);
        tracker.CompleteOperation(scanId, success: true);
        await promoted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(childTerminal.Task, scanTerminal.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, terminals.Count(id => id == removalId));
        Assert.Equal(1, terminals.Count(id => id == scanId));
    }

    [Fact]
    public async Task ThePhaseStartsAHiddenFullDetectionForwardsItsPercentAndWaitsForItsTerminal()
    {
        using var ctx = new PhaseContext();
        var scanId = ctx.RegisterScan();

        var phase = ctx.RunPhaseAsync(scanId, CancellationToken.None);

        var detection = await ctx.Tracker.WaitForOperationAsync(OperationType.GameDetection);
        var metrics = Assert.IsType<GameDetectionMetrics>(detection.Metadata);
        Assert.Equal(NotificationMode.Hidden, detection.Notice!.Mode);
        Assert.Equal(RunTrigger.Manual, detection.Notice.Trigger);
        Assert.Equal(DetectionScanType.Full, metrics.ScanType);
        Assert.Equal(scanId, detection.ParentOperationId);
        Assert.Equal(scanId, metrics.ParentOperationId);
        Assert.NotEqual(scanId, detection.Id);

        ctx.Tracker.SetPercent(detection.Id, 42);
        var forwarded = await ctx.Notifications.WaitForAsync(
            SignalREvents.EvictionScanProgress,
            payload => payload is EvictionScanProgress { StageKey: "signalr.evictionScan.detectingGames", PercentComplete: 42 });
        Assert.Equal(scanId, Assert.IsType<EvictionScanProgress>(forwarded).OperationId);
        Assert.False(phase.IsCompleted);

        ctx.Tracker.Complete(detection.Id);

        await phase.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(ctx.Tracker.Waiting);
    }

    [Fact]
    public async Task PromotedScanReusesTheCompletedFullDetectionThatBlockedIt()
    {
        using var ctx = new PhaseContext();
        var detectionId = Guid.NewGuid();
        ctx.Detection.RememberCompletedDetection(detectionId, DetectionScanType.Full, success: true, cancelled: false);
        var notice = new RunNotice(NotificationMode.Hidden, RunTrigger.Manual) { BlockedByOperationId = detectionId };
        var scanId = ctx.RegisterScan(notice: notice);

        await ctx.RunPhaseAsync(scanId, CancellationToken.None);

        Assert.Equal(0, ctx.Tracker.Count(OperationType.GameDetection));
    }

    [Fact]
    public async Task InvalidatedDetectionStillStartsAHiddenFullScan()
    {
        using var ctx = new PhaseContext();
        var detectionId = Guid.NewGuid();
        ctx.Detection.RememberCompletedDetection(detectionId, DetectionScanType.Full, success: true, cancelled: false);
        ctx.Detection.InvalidateDetectionCache();
        var notice = new RunNotice(NotificationMode.Hidden, RunTrigger.Manual) { BlockedByOperationId = detectionId };
        var scanId = ctx.RegisterScan(notice: notice);

        var phase = ctx.RunPhaseAsync(scanId, CancellationToken.None);
        var detection = await ctx.Tracker.WaitForOperationAsync(OperationType.GameDetection);
        Assert.NotEqual(detectionId, detection.Id);
        ctx.Tracker.Complete(detection.Id);
        await phase.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void OnlyASuccessfulFullDetectionStaysReusable()
    {
        using var ctx = new PhaseContext();
        var detectionId = Guid.NewGuid();
        ctx.Detection.RememberCompletedDetection(detectionId, DetectionScanType.Incremental, success: true, cancelled: false);
        ctx.Detection.RememberCompletedDetection(detectionId, DetectionScanType.Full, success: false, cancelled: false);
        ctx.Detection.RememberCompletedDetection(detectionId, DetectionScanType.Full, success: true, cancelled: true);
        Assert.False(ctx.Detection.HasReusableFullDetection(detectionId));

        ctx.Detection.RememberCompletedDetection(detectionId, DetectionScanType.Full, success: true, cancelled: false);
        Assert.True(ctx.Detection.HasReusableFullDetection(detectionId));
        ctx.Detection.InvalidateDetectionCache();
        Assert.False(ctx.Detection.HasReusableFullDetection(detectionId));
    }

    [Fact]
    public async Task PromotionRecordsTheDetectionThatBlockedTheScan()
    {
        using var ctx = new PhaseContext();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var detectionId = tracker.RegisterOperation(
            OperationType.GameDetection, "Game Detection", new CancellationTokenSource(),
            new GameDetectionMetrics { ScanType = DetectionScanType.Full });
        var queue = new OperationQueueService(tracker,
            new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationQueueService>.Instance);
        var notice = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        var started = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = await queue.EnqueueAsync(
            OperationType.EvictionScan, ConflictScope.Bulk(), "Eviction Scan",
            () =>
            {
                started.TrySetResult(notice.BlockedByOperationId);
                return Task.FromResult<Guid?>(Guid.NewGuid());
            },
            CancellationToken.None,
            notice: notice);

        Assert.True(queued.Queued);
        tracker.CompleteOperation(detectionId, success: true);
        Assert.Equal(detectionId, await started.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task PromotionLeavesTheQueuedScanRunningWhenThatScanKeepsItsId()
    {
        using var ctx = new PhaseContext();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var detectionId = tracker.RegisterOperation(
            OperationType.GameDetection, "Game Detection", new CancellationTokenSource());
        var queue = new OperationQueueService(tracker,
            new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationQueueService>.Instance);
        var notice = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        var continued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = await queue.EnqueueAsync(
            OperationType.EvictionScan, ConflictScope.Bulk(), "Eviction Scan",
            () =>
            {
                var parkedId = notice.OperationId!.Value;
                Assert.True(tracker.BeginQueuedOperation(parkedId, new Dictionary<string, object?>(), null, null));
                continued.TrySetResult();
                return Task.FromResult<Guid?>(parkedId);
            },
            CancellationToken.None,
            notice: notice);

        Assert.True(queued.Queued);
        tracker.CompleteOperation(detectionId, success: true);
        await continued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OperationStatus.Running, tracker.GetOperation(queued.OperationId)!.Status);
    }

    [Fact]
    public void ARunningScanKeepsItsTerminalWhenItsQueuedTokenIsCancelled()
    {
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var token = new CancellationTokenSource();
        var parkedId = tracker.RegisterOperation(
            OperationType.EvictionScan, "Eviction Scan", token,
            metadata: new Dictionary<string, object?> { ["waiting"] = true },
            initialStatus: OperationStatus.Waiting);

        // Still parked: the queue owns the terminal because no worker exists.
        Assert.True(tracker.BeginQueuedOperation(parkedId, new Dictionary<string, object?>(), null, null));
        Assert.False(tracker.CancelParkedOperation(parkedId));
        Assert.Equal(OperationStatus.Running, tracker.GetOperation(parkedId)!.Status);

        tracker.CompleteOperation(parkedId, success: true);
        Assert.Equal(OperationStatus.Completed, tracker.GetOperation(parkedId)!.Status);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task CancellingAHeldOrAcknowledgedScanLeavesARunningScansTerminalToItsWorker(bool acknowledged, bool begun)
    {
        const string evictionKey = "cacheReconciliation";
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var schedules = new ServiceScheduleRegistry([], VisibleClientsStateService(),
            DispatchProxy.Create<ISignalRNotificationService, RecordingNotifications>(), tracker);
        var notice = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        typeof(ServiceScheduleRegistry)
            .GetMethod(acknowledged ? "AcknowledgeRun" : "HoldRefusedRun", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(schedules, acknowledged
                ? [evictionKey, OperationType.EvictionScan, notice, false]
                : [evictionKey, OperationType.EvictionScan, notice]);
        var heldId = notice.PendingId!.Value;
        var holds = (Dictionary<string, (Guid Id, RunNotice Notice)>)typeof(ServiceScheduleRegistry)
            .GetField("_deferredRuns", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(schedules)!;
        var terminal = new TaskCompletionSource<OperationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.OperationTerminal += operation =>
        {
            if (operation.Id == heldId) terminal.TrySetResult(operation.Status);
        };

        // The scan's worker continues the held operation in place, the way RegisterEvictionScanOperation does.
        if (begun)
        {
            Assert.True(tracker.BeginQueuedOperation(heldId, new Dictionary<string, object?>(), null, null));
        }

        Assert.Equal(OperationCancelResult.Requested, tracker.CancelOperation(heldId));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            lock (holds)
            {
                if (!holds.ContainsKey(evictionKey)) break;
            }
            Assert.True(DateTime.UtcNow < deadline, "The cancel callback did not drop the hold");
            await Task.Delay(10);
        }

        if (!begun)
        {
            // No worker exists, so the callback is what ends it.
            Assert.Equal(OperationStatus.Cancelled, await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            return;
        }

        // The callback runs on the thread pool; an early terminal lands well inside this window.
        Assert.NotSame(terminal.Task, await Task.WhenAny(terminal.Task, Task.Delay(TimeSpan.FromMilliseconds(500))));
        Assert.Equal(OperationStatus.Cancelling, tracker.GetOperation(heldId)!.Status);
        // Still active, so the conflict check keeps every other heavy operation waiting.
        Assert.Contains(heldId, tracker.GetActiveOperations(OperationType.EvictionScan).Select(operation => operation.Id));

        tracker.CompleteOperation(heldId, success: false, cancelled: true);
        Assert.Equal(OperationStatus.Cancelled, await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void AParkedScanIsCancelledByTheQueueAndRefusesToStart()
    {
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var parkedId = tracker.RegisterOperation(
            OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            metadata: new Dictionary<string, object?> { ["waiting"] = true },
            initialStatus: OperationStatus.Waiting);

        Assert.Equal(OperationCancelResult.Requested, tracker.CancelOperation(parkedId));
        // The cancel moved it to Cancelling without giving it a worker, so it is still the queue's.
        Assert.Contains(parkedId, tracker.GetWaitingOperations().Select(operation => operation.Id));
        Assert.False(tracker.BeginQueuedOperation(parkedId, new Dictionary<string, object?>(), null, null));
        Assert.True(tracker.CancelParkedOperation(parkedId));
        Assert.Equal(OperationStatus.Cancelled, tracker.GetOperation(parkedId)!.Status);
        Assert.False(tracker.CancelParkedOperation(parkedId));
    }

    [Fact]
    public void AQueuedEvictionScanContinuesTheParkedOperation()
    {
        using var ctx = new PhaseContext();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        var parkedToken = new CancellationTokenSource();
        var parkedId = tracker.RegisterOperation(
            OperationType.EvictionScan, "Eviction Scan", parkedToken, initialStatus: OperationStatus.Waiting);
        var notice = new RunNotice(NotificationMode.Hidden, RunTrigger.Manual);
        notice.Attach(tracker, parkedId);

        var continued = (Guid)typeof(CacheReconciliationService).GetMethod(
            "RegisterEvictionScanOperation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(
            ctx.Scan, ["Eviction Scan", parkedToken, notice])!;

        Assert.Equal(parkedId, continued);
        Assert.Equal(OperationStatus.Running, tracker.GetOperation(parkedId)!.Status);
        Assert.Equal(parkedId, Assert.Single(tracker.GetActiveOperations(OperationType.EvictionScan)).Id);
    }

    [Fact]
    public async Task CancellingTheScanCancelsTheDetectionItIsWaitingOn()
    {
        using var ctx = new PhaseContext();
        using var cts = new CancellationTokenSource();

        var scanId = ctx.RegisterScan();
        var phase = ctx.RunPhaseAsync(scanId, cts.Token);
        var detection = await ctx.Tracker.WaitForOperationAsync(OperationType.GameDetection);

        cts.Cancel();

        await phase.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(detection.Id, ctx.Tracker.Cancelled);
        Assert.Empty(ctx.Tracker.Waiting);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetectionFailureRemainsOnTheRunningParentThroughLaterProgress(bool silent)
    {
        using var ctx = new PhaseContext();
        var scanId = ctx.RegisterScan(new RunNotice(silent ? NotificationMode.Silent : NotificationMode.All, RunTrigger.Scheduled));
        var phase = ctx.RunPhaseAsync(scanId, CancellationToken.None);
        var detection = await ctx.Tracker.WaitForOperationAsync(OperationType.GameDetection);
        ctx.Tracker.Fail(detection.Id, "Cache index could not be read");
        await phase.WaitAsync(TimeSpan.FromSeconds(5));
        await ctx.ReportProgressAsync(scanId);
        var progress = Assert.IsType<EvictionScanProgress>(await ctx.Notifications.WaitForAsync(
            SignalREvents.EvictionScanProgress, value => value is EvictionScanProgress { StageKey: "signalr.evictionScan.scanning" }));
        Assert.Equal("Cache index could not be read", progress.Context!["detectionError"]);
        Assert.Equal("Cache index could not be read", ctx.Scan.CurrentScanProgressContext!["detectionError"]);
        Assert.Equal(OperationStatus.Running, ctx.Tracker.Get(scanId)!.Status);
    }

    [Fact]
    public async Task SilentParentTerminalKeepsDetectionDetailAndRejectsLaterProgress()
    {
        using var ctx = new PhaseContext();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        var id = ctx.RegisterScan(new RunNotice(NotificationMode.Silent, RunTrigger.Scheduled));
        var holders = typeof(CacheReconciliationService).GetField("_evictionScanTerminalStates", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(ctx.Scan)!;
        var holder = holders.GetType().GetProperty("Item")!.GetValue(holders, [id])!;
        tracker.UpdateMetadata(id, values => holder.GetType().GetField("DetectionError")!.SetValue(holder, "Cache index could not be read"));
        await ctx.ReportProgressAsync(id);
        tracker.CompleteOperation(id, success: true);
        var terminal = Assert.IsType<EvictionScanComplete>(await ctx.Notifications.WaitForAsync(
            SignalREvents.EvictionScanComplete, value => value is EvictionScanComplete { OperationId: var operationId } && operationId == id));
        Assert.True(terminal.Success);
        Assert.Null(terminal.Error);
        Assert.Equal("Cache index could not be read", terminal.Context!["detectionError"]);
        // The silent run still ends as a kept warning, so the failed detection is not lost.
        var row = Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == id);
        Assert.Equal("Cache index could not be read", row.Warning);
        Assert.True(row.Retained);
        var percent = tracker.GetOperation(id)!.PercentComplete;
        await ctx.ReportProgressAsync(id);
        Assert.Equal(percent, tracker.GetOperation(id)!.PercentComplete);
        Assert.Null(ctx.Scan.CurrentScanProgressContext);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AScanWhoseDetectionFailedEndsAsAKeptWarningCarryingTheError(bool detectionFailed)
    {
        using var ctx = new PhaseContext();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        var id = ctx.RegisterScan();
        if (detectionFailed)
        {
            // What the detection phase records on the scan when its child ends Failed.
            var holders = typeof(CacheReconciliationService).GetField("_evictionScanTerminalStates", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(ctx.Scan)!;
            var holder = holders.GetType().GetProperty("Item")!.GetValue(holders, [id])!;
            tracker.UpdateMetadata(id, _ => holder.GetType().GetField("DetectionError")!.SetValue(holder, "Cache index could not be read"));
        }

        await ctx.ReportProgressAsync(id);
        tracker.CompleteOperation(id, success: true);

        var row = Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == id);
        Assert.Equal(detectionFailed ? "Cache index could not be read" : null, row.Warning);
        Assert.Equal(detectionFailed, row.Retained);
    }

    [Theory]
    [InlineData(NotificationMode.All, RunTrigger.Manual, RunVisibility.Card)]
    [InlineData(NotificationMode.All, RunTrigger.Scheduled, RunVisibility.Card)]
    [InlineData(NotificationMode.Manual, RunTrigger.Manual, RunVisibility.Card)]
    [InlineData(NotificationMode.Manual, RunTrigger.Scheduled, RunVisibility.Background)]
    [InlineData(NotificationMode.Manual, RunTrigger.RunAll, RunVisibility.Background)]
    [InlineData(NotificationMode.Silent, RunTrigger.Manual, RunVisibility.Background)]
    [InlineData(NotificationMode.Silent, RunTrigger.Scheduled, RunVisibility.Background)]
    [InlineData(NotificationMode.Hidden, RunTrigger.Manual, RunVisibility.Hidden)]
    [InlineData(NotificationMode.Hidden, RunTrigger.Scheduled, RunVisibility.Hidden)]
    public async Task TheScansOwnCleanupFollowsTheScansModeAndTrigger(
        NotificationMode mode, RunTrigger trigger, RunVisibility expected)
    {
        using var ctx = new PhaseContext();
        await using var database = await TestDatabase.CreateAsync();
        await using var context = new AppDbContext(database.Options);
        context.Downloads.Add(new Download
        {
            Service = PrefillPlatform.Steam.ToService(), ClientIp = "127.0.0.1", Datasource = "Default", GameAppId = 123,
            GameName = "Removed", IsEvicted = true, StartTimeUtc = DateTime.UtcNow, EndTimeUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        // What the scan reads on its way to its Remove-mode cleanup; only the Rust step is a stand-in.
        ctx.State.SetEvictedDataMode(EvictedDataMode.Remove.ToWireString());
        PhaseContext.SetField(ctx.Scan, "_stateService", ctx.State);
        PhaseContext.SetField(ctx.Scan, "_datasourceService", ctx.Datasources);
        PhaseContext.SetField(ctx.Scan, "_cacheScanGate", Idle());
        PhaseContext.SetField(ctx.Scan, "_rustProcessHelper", new ScanResultRustProcessHelper());
        RunVisibility? whileRunning = null;
        Guid removalId = default;
        ctx.Notifications.OnSent = (eventName, value) =>
        {
            if (eventName == SignalREvents.EvictionRemovalStarted && value is EvictionRemovalStarted started)
            {
                removalId = started.OperationId;
                whileRunning = Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == started.OperationId).Visibility;
            }
        };
        var scanNotice = new RunNotice(mode, trigger);
        var scanId = ctx.RegisterScan(scanNotice);

        await ((Task)typeof(CacheReconciliationService)
            .GetMethod("ReconcileCacheFilesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ctx.Scan, [context, scanId, CancellationToken.None, scanNotice, false])!)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(expected, whileRunning);
        // A fresh notice with the scan's mode and trigger; the scan's own notice stays with the scan.
        var cleanupNotice = tracker.GetOperation(removalId)!.Notice!;
        Assert.NotSame(scanNotice, cleanupNotice);
        Assert.Equal(scanNotice.Mode, cleanupNotice.Mode);
        Assert.Equal(scanNotice.Trigger, cleanupNotice.Trigger);
        // The fixture has no summary service, so the cleanup fails after its commit; a failure stays
        // until closed whatever the mode.
        var ended = Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == removalId);
        Assert.Equal(OperationStatus.Failed.ToWireString(), ended.Status);
        Assert.True(ended.Retained);
    }

    [Theory]
    [InlineData(NotificationMode.All, RunTrigger.Manual, RunVisibility.Card)]
    [InlineData(NotificationMode.Manual, RunTrigger.Scheduled, RunVisibility.Background)]
    [InlineData(NotificationMode.Manual, RunTrigger.RunAll, RunVisibility.Background)]
    [InlineData(NotificationMode.Silent, RunTrigger.Manual, RunVisibility.Background)]
    [InlineData(NotificationMode.Hidden, RunTrigger.Scheduled, RunVisibility.Hidden)]
    public void TheScanRegistersWithTheNoticeItWasAdmittedWith(NotificationMode mode, RunTrigger trigger, RunVisibility expected)
    {
        using var ctx = new PhaseContext();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        var notice = new RunNotice(mode, trigger);

        var id = ctx.RegisterScan(notice);

        Assert.Same(notice, tracker.GetOperation(id)!.Notice);
        Assert.Equal(expected, Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == id).Visibility);
    }

    [Theory]
    [InlineData(RunTrigger.Manual)]
    [InlineData(RunTrigger.Scheduled)]
    public async Task WithNoDownloadsARunNowEndsSkippedWithItsNoticeAndAnAutomaticRunLeavesNoRow(RunTrigger trigger)
    {
        using var ctx = new PhaseContext();
        await using var database = await TestDatabase.CreateAsync();
        await using var context = new AppDbContext(database.Options);
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        var notice = new RunNotice(NotificationMode.All, trigger);
        ctx.Scan.SelectRunNotice(notice);
        using var services = new ServiceCollection().AddSingleton(context).BuildServiceProvider();

        await (Task)typeof(CacheReconciliationService).GetMethod("ExecuteWorkAsync",
            BindingFlags.Instance | BindingFlags.NonPublic, [typeof(IServiceProvider), typeof(CancellationToken)])!
            .Invoke(ctx.Scan, [services, CancellationToken.None])!;

        var runs = tracker.GetRuns().Runs;
        if (trigger != RunTrigger.Manual)
        {
            Assert.Empty(runs);
            return;
        }

        var row = Assert.Single(runs);
        Assert.Equal(OperationStatus.Skipped.ToWireString(), row.Status);
        Assert.Equal(RunVisibility.Card, row.Visibility);
        Assert.Same(notice, tracker.GetOperation(row.OperationId)!.Notice);
    }

    /// <summary>
    /// A real <see cref="GameCacheDetectionService"/> so the phase's start call registers a
    /// genuine detection operation, and an uninitialized <see cref="CacheReconciliationService"/>
    /// carrying only the fields the phase reads. The detection's background run fails at once on
    /// its missing database and reports that to the tracker stand-in, which ignores it: the phase
    /// must see the detection as running until the test ends it.
    /// </summary>
    private sealed class PhaseContext : IDisposable
    {
        private readonly string _root;
        private readonly CacheReconciliationService _scan;

        public FakeTracker Tracker { get; }
        public GameCacheDetectionService Detection { get; }
        public RecordingNotifications Notifications { get; }
        public StateService State { get; }
        public DatasourceService Datasources { get; }
        public CacheReconciliationService Scan => _scan;

        public PhaseContext()
        {
            _root = Path.Combine(Path.GetTempPath(), "lcm-cache-scan-phase", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            var pathResolver = new TempDirPathResolver(_root);
            var configuration = new ConfigurationBuilder().Build();
            var apiKeyService = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver);
            var dataProtection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "dp-keys")));
            var encryption = new SecureStateEncryptionService(
                dataProtection, apiKeyService, NullLogger<SecureStateEncryptionService>.Instance);
            var steamAuthStorage = new SteamAuthStorageService(
                NullLogger<SteamAuthStorageService>.Instance, pathResolver, encryption);
            var stateService = new StateService(
                NullLogger<StateService>.Instance, pathResolver, encryption, steamAuthStorage);
            typeof(StateService)
                .GetField("_cachedState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(stateService, new AppState());

            var operationStateService = new OperationStateService(
                NullLogger<OperationStateService>.Instance, configuration, stateService);
            var datasourceService = new DatasourceService(
                configuration, pathResolver, NullLogger<DatasourceService>.Instance);

            // One unambiguous monolithic log source, so the detection's capability gate lets it start.
            var datasource = Assert.Single(datasourceService.GetDatasources());
            Directory.CreateDirectory(datasource.LogPath);
            File.WriteAllText(Path.Combine(datasource.LogPath, "access.log"), string.Empty);

            var capabilityService = new DatasourceCapabilityService(datasourceService);
            State = stateService;
            Datasources = datasourceService;

            Notifications = (RecordingNotifications)DispatchProxy
                .Create<ISignalRNotificationService, RecordingNotifications>();
            Tracker = (FakeTracker)DispatchProxy
                .Create<IUnifiedOperationTracker, FakeTracker>();

            Detection = new GameCacheDetectionService(
                NullLogger<GameCacheDetectionService>.Instance,
                pathResolver,
                operationStateService,
                dbContextFactory: null!,
                detectionDataService: null!,
                evictedDetectionPreservationService: null!,
                unknownGameResolutionService: null!,
                rustProcessHelper: null!,
                (ISignalRNotificationService)(object)Notifications,
                datasourceService,
                capabilityService,
                (IUnifiedOperationTracker)(object)Tracker,
                Idle());

            _scan = (CacheReconciliationService)RuntimeHelpers.GetUninitializedObject(typeof(CacheReconciliationService));
            SetField(_scan, "_logger", NullLogger<CacheReconciliationService>.Instance);
            SetField(_scan, "_operationTracker", (IUnifiedOperationTracker)(object)Tracker);
            SetField(_scan, "_notifications", (ISignalRNotificationService)(object)Notifications);
            SetField(_scan, "_gameCacheDetectionService", Detection);
            SetField(_scan, "_capabilityService", capabilityService);
            foreach (var name in new[] { "_evictionRemovalTerminalStates", "_evictionScanTerminalStates" })
            {
                var field = typeof(CacheReconciliationService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
                field.SetValue(_scan, Activator.CreateInstance(field.FieldType));
            }
        }

        public Guid RegisterScan(RunNotice? notice = null)
        {
            return (Guid)typeof(CacheReconciliationService).GetMethod("RegisterEvictionScanOperation",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_scan,
                ["Eviction Scan", new CancellationTokenSource(), notice ?? new RunNotice(NotificationMode.All, RunTrigger.Manual)])!;
        }

        public Task ReportProgressAsync(Guid id) => (Task)typeof(CacheReconciliationService)
            .GetMethod("ReportScanProgressAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_scan, [id, 0d, "signalr.evictionScan.scanning", new EvictionScanResult()])!;

        public Task RunPhaseAsync(Guid scanOperationId, CancellationToken token)
        {
            var phase = typeof(CacheReconciliationService).GetMethod(
                "RunFullDetectionPhaseAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (Task)phase.Invoke(_scan, [scanOperationId, token])!;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A temp tree the OS still holds open is not this test's concern.
            }
        }

        internal static void SetField(object target, string name, object value)
            => typeof(CacheReconciliationService)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(target, value);
    }

    /// <summary>
    /// The scan's Rust step answering with a finished scan that found nothing newly evicted and
    /// nothing back on disk, so the scan goes on to its Remove-mode cleanup.
    /// </summary>
    private sealed class ScanResultRustProcessHelper : RustProcessHelper
    {
        public ScanResultRustProcessHelper()
            : base(
                NullLogger<RustProcessHelper>.Instance,
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                pathResolver: null!,
                operationTracker: null!)
        {
        }

        public override Task<RustExecutionResult> RunEvictionScanAsync(
            string datasourceConfigPath,
            string? progressFile = null,
            CancellationToken cancellationToken = default,
            Guid? operationId = null,
            Func<RustProgressEvent, Task>? onProgressEvent = null) =>
            Task.FromResult(new RustExecutionResult
            {
                Success = true,
                Data = """{"success":true,"processed":1,"evicted":0,"unEvicted":0}"""
            });
    }

    private sealed class TempDirPathResolver : PathResolverBase
    {
        private readonly string _basePath;

        public TempDirPathResolver(string basePath) : base(NullLogger.Instance)
        {
            _basePath = basePath;
        }

        protected override string BasePath => _basePath;
        protected override string RustExecutableExtension => string.Empty;

        public override string ResolvePath(string relativePath) => relativePath;
        public override string NormalizePath(string path) => path;
        public override bool IsDockerSocketAvailable() => false;
    }

    /// <summary>
    /// Tracker stand-in the test drives. Registration mints a running row; progress lands on it;
    /// terminal subscriptions are kept so <see cref="Complete"/> and a cancel can fire them. The
    /// service's own CompleteOperation call (its background run failing) is ignored on purpose:
    /// the phase must keep waiting until the test ends the detection. Not sealed for DispatchProxy.
    /// </summary>
    public class FakeTracker : DispatchProxy
    {
        private readonly object _sync = new();
        private readonly List<OperationInfo> _operations = [];
        private readonly List<Action<OperationInfo>> _terminalHandlers = [];
        private readonly Dictionary<Guid, Func<OperationTerminalInfo, Task>> _terminalEmits = [];
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal List<Guid> Cancelled { get; } = [];

        internal IReadOnlyList<OperationInfo> Waiting
        {
            get { lock (_sync) return _operations.Where(o => o.Status == OperationStatus.Waiting).ToList(); }
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IUnifiedOperationTracker.RegisterOperation):
                {
                    var operation = new OperationInfo
                    {
                        Id = Guid.NewGuid(),
                        Type = (OperationType)args![0]!,
                        Name = (string)args[1]!,
                        Status = (OperationStatus)args[6]!,
                        Metadata = args[3],
                        ParentOperationId = (Guid?)args[7],
                        StartedAt = (DateTime?)args[8] ?? DateTime.UtcNow,
                        Notice = (RunNotice?)args[10]
                    };
                    if (args[5] is Func<OperationTerminalInfo, Task> emit)
                        _terminalEmits[operation.Id] = emit;
                    lock (_sync)
                    {
                        _operations.Add(operation);
                        _changed.TrySetResult();
                        _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    return operation.Id;
                }
                case nameof(IUnifiedOperationTracker.GetOperation):
                    lock (_sync)
                    {
                        return _operations.FirstOrDefault(o => o.Id == (Guid)args![0]!);
                    }
                case nameof(IUnifiedOperationTracker.GetActiveOperations):
                    lock (_sync)
                    {
                        return _operations.Where(o => o.Status == OperationStatus.Running &&
                            (args![0] == null || o.Type == (OperationType)args[0]!)).ToList();
                    }
                case nameof(IUnifiedOperationTracker.UpdateProgress):
                    lock (_sync)
                    {
                        var operation = _operations.FirstOrDefault(o => o.Id == (Guid)args![0]!);
                        if (operation != null && !operation.Status.IsTerminal())
                        {
                            operation.PercentComplete = (double)args![1]!;
                            operation.Message = args[2] as string ?? string.Empty;
                            (args[3] as Action<OperationInfo>)?.Invoke(operation);
                        }
                    }
                    return null;
                case nameof(IUnifiedOperationTracker.UpdateMetadata):
                    lock (_sync)
                    {
                        var operation = _operations.FirstOrDefault(o => o.Id == (Guid)args![0]!);
                        if (operation?.Metadata != null && !operation.Status.IsTerminal())
                            ((Action<object>)args![1]!).Invoke(operation.Metadata);
                    }
                    return null;
                case nameof(IUnifiedOperationTracker.CancelOperation):
                {
                    var id = (Guid)args![0]!;
                    lock (_sync)
                    {
                        Cancelled.Add(id);
                    }

                    End(id, OperationStatus.Cancelled);
                    return default(OperationCancelResult);
                }
                case "add_OperationTerminal":
                    lock (_sync)
                    {
                        _terminalHandlers.Add((Action<OperationInfo>)args![0]!);
                    }

                    return null;
                case "remove_OperationTerminal":
                    lock (_sync)
                    {
                        _terminalHandlers.Remove((Action<OperationInfo>)args![0]!);
                    }

                    return null;
                default:
                    return targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask : null;
            }
        }

        internal async Task<OperationInfo> WaitForOperationAsync(OperationType type)
        {
            while (true)
            {
                Task changed;
                lock (_sync)
                {
                    var found = _operations.FirstOrDefault(o => o.Type == type);
                    if (found != null)
                    {
                        return found;
                    }
                    changed = _changed.Task;
                }

                await changed.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        internal void SetPercent(Guid id, double percent)
        {
            lock (_sync)
            {
                var operation = _operations.FirstOrDefault(o => o.Id == id);
                if (operation != null)
                {
                    operation.PercentComplete = percent;
                }
            }
        }

        internal void Complete(Guid id) => End(id, OperationStatus.Completed);

        internal int Count(OperationType type)
        {
            lock (_sync) return _operations.Count(operation => operation.Type == type);
        }

        internal Task FireTerminal(Guid id, OperationTerminalInfo terminal) => _terminalEmits[id](terminal);
        internal OperationInfo? Get(Guid id) { lock (_sync) return _operations.FirstOrDefault(o => o.Id == id); }
        internal void Fail(Guid id, string error)
        {
            var operation = Get(id)!;
            ((GameDetectionMetrics)operation.Metadata!).Error = error;
            End(id, OperationStatus.Failed);
        }

        private void End(Guid id, OperationStatus status)
        {
            OperationInfo? operation;
            Action<OperationInfo>[] handlers;
            lock (_sync)
            {
                operation = _operations.FirstOrDefault(o => o.Id == id);
                if (operation == null)
                {
                    return;
                }

                operation.Status = status;
                handlers = _terminalHandlers.ToArray();
            }

            foreach (var handler in handlers)
            {
                handler(operation);
            }
        }
    }

    /// <summary>
    /// Records every broadcast as its event name and payload. Not sealed for DispatchProxy.
    /// </summary>
    public class RecordingNotifications : DispatchProxy
    {
        internal Action<string, object?>? OnSent { get; set; }
        private readonly object _sync = new();
        private readonly List<(string Event, object? Payload)> _sent = [];
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (args is [string eventName, ..])
            {
                OnSent?.Invoke(eventName, args.Length > 1 ? args[1] : null);
                lock (_sync)
                {
                    _sent.Add((eventName, args.Length > 1 ? args[1] : null));
                    _changed.TrySetResult();
                    _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            return targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask : null;
        }

        internal async Task<object?> WaitForAsync(string eventName, Func<object?, bool> matches)
        {
            while (true)
            {
                Task changed;
                lock (_sync)
                {
                    var hit = _sent.FirstOrDefault(s => s.Event == eventName && matches(s.Payload));
                    if (hit.Event != null)
                    {
                        return hit.Payload;
                    }
                    changed = _changed.Task;
                }

                await changed.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
