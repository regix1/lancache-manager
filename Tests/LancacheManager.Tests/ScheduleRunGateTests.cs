using System.Reflection;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Covers the one question every schedule asks before it runs, and the identical way each caller
/// treats the answer: the loop declines before it touches any run state, the startup pass declines
/// the same way, a manual Run Now never arms a run it cannot start, and Run all counts the refusals
/// while still triggering everything that can run.
///
/// Every gate installed here answers null for a key it was not given, because the static hook is
/// process-wide and services under test in other classes must keep running normally.
/// </summary>
[Collection(nameof(DownloadsEndedEventCollection))]
public class ScheduleRunGateTests
{
    private const string DownloadReason = "A client download is writing to the cache right now.";
    private const string EvictionKey = "cacheReconciliation";

    [Fact]
    public async Task ScheduledPrefill_RejectsDuplicateRunsAsync()
    {
        using var service = new ConfigurableRunGateProbeService();
        var tracker = CreateRealTracker();
        var registry = CreateRegistry([service], CacheScanGateHarness.Idle(), tracker);
        var first = await registry.TriggerRunAsync(service.ScheduleServiceKey);
        Assert.False(first.Status.IsRunning);
        var duplicates = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => registry.TriggerRunAsync(service.ScheduleServiceKey))));
        Assert.All(duplicates, duplicate =>
        {
            Assert.True(duplicate.Status.IsRunning);
            Assert.False(duplicate.FollowUpQueued);
            Assert.Null(duplicate.Status.OperationId);
        });
        Assert.True(service.TakePendingManualRun(out var notice));
        var starting = await registry.TriggerAllAsync();
        Assert.Equal(0, starting.TriggeredCount);
        Assert.Equal(1, starting.AlreadyRunningCount);
        Assert.Equal(0, starting.FollowUpCount);
        Assert.False(service.HasPendingRun);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = service.InvokeRunScheduledWorkAsync(RunTrigger.Manual, CancellationToken.None, notice,
            async _ => { entered.SetResult(); await release.Task; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var running = await registry.TriggerRunAsync(service.ScheduleServiceKey);
            Assert.True(running.Status.IsRunning);
            Assert.False(running.FollowUpQueued);
            Assert.Empty(tracker.GetActiveOperations());
        }
        finally
        {
            release.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.False(service.HasPendingRun);
        Assert.False((await registry.TriggerRunAsync(service.ScheduleServiceKey)).Status.IsRunning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartGate_RejectsSuccessorsAndReleasesClaimsAsync(bool throws)
    {
        using var service = new RunGateProbeService("no-follow-up-probe", queueManualRuns: false);
        var previous = ScheduledServiceBase.ScheduleRunGate;
        ScheduledServiceBase.ScheduleRunGate = (key, trigger) =>
        {
            if (key != service.ServiceKey) return previous?.Invoke(key, trigger);
            Assert.False(service.TryTriggerImmediateRun(new RunNotice(NotificationMode.All, RunTrigger.Manual), out _, out var followUp));
            Assert.False(followUp);
            if (throws) throw new IOException("gate unavailable");
            return "not ready";
        };
        try
        {
            await service.InvokeRunScheduledWorkAsync(RunTrigger.Scheduled, CancellationToken.None);
            Assert.False(service.WorkRan);
            Assert.False(service.HasPendingRun);
            Assert.True(service.TryTriggerImmediateRun(new RunNotice(NotificationMode.All, RunTrigger.Manual), out _, out _));
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previous;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAllHeavyRequestsKeepTheInternalDetectionWithItsParentInEitherOrder(bool evictionFirst)
    {
        using var detection = new RunGateProbeService("gameDetection");
        using var eviction = new RunGateProbeService(EvictionKey);
        using var files = new RunGateProbeService("cacheSizeScan");
        RunGateProbeService[] services = evictionFirst ? [eviction, detection, files] : [detection, eviction, files];
        var tracker = CreateRealTracker();
        var notifications = CreateDefaultProxy<ISignalRNotificationService>();
        var registry = CreateRegistry(services, CacheScanGateHarness.Idle(), tracker, notifications);
        var queue = new OperationQueueService(tracker,
            new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationQueueService>.Instance);
        var starts = new Dictionary<string, TaskCompletionSource<Guid>>
        {
            [detection.ServiceKey] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            [eviction.ServiceKey] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            [files.ServiceKey] = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        Guid? child = null;
        var result = await registry.TriggerAllAsync();
        Assert.Equal(3, result.Item1);
        foreach (var service in services)
        {
            Assert.True(service.TakePendingManualRun(out var notice));
            var type = service == detection ? OperationType.GameDetection
                : service == eviction ? OperationType.EvictionScan : OperationType.CacheSizeScan;
            await service.InvokeRunScheduledWorkAsync(RunTrigger.Manual, CancellationToken.None, notice,
                async token =>
                {
                    await queue.EnqueueAsync(type, ConflictScope.Bulk(), service.ServiceKey, () =>
                    {
                        Assert.DoesNotContain(tracker.GetActiveOperations(), o =>
                            o.Type is OperationType.GameDetection or OperationType.EvictionScan or OperationType.CacheSizeScan);
                        var id = tracker.RegisterOperation(type, service.ServiceKey, new CancellationTokenSource());
                        if (type == OperationType.EvictionScan)
                        {
                            child = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection",
                                new CancellationTokenSource(), new GameDetectionMetrics { ParentOperationId = id }, parentOperationId: id);
                        }
                        starts[service.ServiceKey].TrySetResult(id);
                        return Task.FromResult<Guid?>(id);
                    }, CancellationToken.None, notice: notice);
                });
        }
        foreach (var service in services)
        {
            var id = await starts[service.ServiceKey].Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(OperationStatus.Running, tracker.GetOperation(id)!.Status);
            if (service == eviction)
            {
                Assert.NotNull(child);
                Assert.Equal(id, tracker.GetOperation(child.Value)!.ParentOperationId);
                tracker.CompleteOperation(child.Value, success: true);
                Assert.Equal(OperationStatus.Running, tracker.GetOperation(id)!.Status);
            }
            tracker.CompleteOperation(id, success: true);
        }
    }

    [Fact]
    public async Task MappingReporterCancellationReachesTheAdmittedRun()
    {
        using var service = new RunGateProbeService("depotMapping");
        var tracker = CreateRealTracker();
        var terminals = new List<ScheduledRunCompleteEvent>();
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (args?.Length > 1 && args[1] is ScheduledRunCompleteEvent terminal) terminals.Add(terminal);
            return Task.CompletedTask;
        });
        _ = CreateRegistry(service, CacheScanGateHarness.Idle(), tracker, notifications);
        var notice = new RunNotice(NotificationMode.Silent, RunTrigger.Manual);
        Guid operationId = default;
        var result = await service.InvokeRunScheduledWorkAsync(RunTrigger.Manual, CancellationToken.None, notice,
            async token =>
            {
                using var nested = CancellationTokenSource.CreateLinkedTokenSource(token);
                await using var reporter = new MappingOperationReporter(notifications, tracker, MappingOperations.Steam,
                    notice, nested.Token, NullLogger.Instance);
                await reporter.StartAsync();
                operationId = reporter.OperationId;
                var reporterToken = reporter.Token;
                tracker.CancelOperation(operationId);
                Assert.True(notice.Cancelled);
                Assert.False(token.IsCancellationRequested);
                Assert.False(nested.IsCancellationRequested);
                await reporter.CompleteAsync(success: false, cancelled: true);
                reporterToken.ThrowIfCancellationRequested();
            });
        Assert.False(result.RunFailed);
        Assert.False(result.ShuttingDown);
        Assert.Equal(operationId, notice.OperationId);
        Assert.Equal(OperationStatus.Cancelled, tracker.GetOperation(operationId)!.Status);
        Assert.Single(terminals);
        Assert.Empty(tracker.GetActiveOperations());
        Assert.Empty(tracker.GetWaitingOperations());
    }

    [Fact]
    public async Task CancelledRunTokenDoesNotReportAnotherFailure()
    {
        using var service = new RunGateProbeService(EvictionKey);
        var tracker = CreateRealTracker();
        _ = CreateRegistry(service, CacheScanGateHarness.Idle(), tracker);
        var cts = new CancellationTokenSource();
        var notice = new RunNotice(NotificationMode.Silent, RunTrigger.Manual) { Token = cts.Token };
        var operationId = tracker.RegisterOperation(OperationType.EvictionScan, "Cache scan", cts);
        var completed = service.RunCompleted;
        string? error = null;
        var cancelled = false;
        service.RunCompleted = (admitted, key, failure, wasCancelled) =>
        {
            error = failure;
            cancelled = wasCancelled;
            completed?.Invoke(admitted, key, failure, wasCancelled);
        };
        var lastRun = DateTime.UtcNow.AddHours(-1);
        service.SetLastRunUtc(lastRun);
        var result = await service.InvokeRunScheduledWorkAsync(RunTrigger.Manual, CancellationToken.None, notice,
            token =>
            {
                Assert.Equal(OperationCancelResult.Requested, tracker.CancelOperation(operationId));
                tracker.CompleteOperation(operationId, success: false, cancelled: true);
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
        Assert.False(notice.Cancelled);
        Assert.False(result.RunFailed);
        Assert.False(result.ShuttingDown);
        Assert.Null(error);
        Assert.True(cancelled);
        Assert.True(service.WorkRan);
        Assert.True(service.LastRunUtc > lastRun);
        Assert.Empty(tracker.GetActiveOperations());
        Assert.Empty(tracker.GetWaitingOperations());
        Assert.Equal(OperationStatus.Cancelled, tracker.GetOperation(operationId)!.Status);
    }

    [Fact]
    public async Task HiddenUntrackedFailureRemainsHidden()
    {
        using var service = new RunGateProbeService("gameDetection");
        var tracker = CreateRealTracker();
        var announced = new TaskCompletionSource<ScheduledRunCompleteEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = CreateProxy<ISignalRNotificationService>((_, args) =>
        {
            if (args?.Length > 1 && args[1] is ScheduledRunCompleteEvent terminal)
                announced.TrySetResult(terminal);
            return Task.CompletedTask;
        });
        _ = CreateRegistry(service, CacheScanGateHarness.Idle(), tracker, notifications);
        var notice = new RunNotice(NotificationMode.Hidden, RunTrigger.Scheduled);

        var result = await service.InvokeRunScheduledWorkAsync(
            RunTrigger.Scheduled,
            CancellationToken.None,
            notice,
            _ => throw new InvalidOperationException("the worker broke"));
        var terminal = await announced.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.RunFailed);
        // The failure is recorded under the run's own notice, so its kept row stays hidden.
        var failed = Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == terminal.OperationId);
        Assert.Equal("failed", failed.Status);
        Assert.Equal(RunVisibility.Hidden, failed.Visibility);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringAdmissionDoesNotStartOrStampWork(bool queueManualRuns)
    {
        using var service = new RunGateProbeService(EvictionKey, queueManualRuns);
        var tracker = CreateRealTracker();
        var notice = new RunNotice(NotificationMode.Silent, RunTrigger.Manual);
        var lastRun = DateTime.UtcNow.AddHours(-1);
        service.SetLastRunUtc(lastRun);
        var result = await WithGateAsync((key, _) =>
        {
            if (key == EvictionKey) notice.Cancel(tracker, Guid.Empty);
            return null;
        }, () => service.InvokeRunScheduledWorkAsync(RunTrigger.Manual, CancellationToken.None, notice));
        Assert.False(result.ShuttingDown);
        Assert.False(result.RunFailed);
        Assert.False(service.WorkRan);
        Assert.Equal(lastRun, service.LastRunUtc);
        Assert.False(service.EndBroadcast);
        Assert.True(service.TryTriggerImmediateRun(new RunNotice(NotificationMode.All, RunTrigger.Manual), out _, out var followUp));
        Assert.False(followUp);
    }

    [Fact]
    public async Task GateReleasedHoldWithoutReporterClosesTheAdmittedNotice()
    {
        using var service = new RunGateProbeService(EvictionKey);
        service.SetNotificationMode(NotificationMode.Silent);
        var tracker = CreateRealTracker();
        var snapshot = new DownloadSpeedSnapshot();
        CacheScanGateHarness.MakeBusy(snapshot);
        var previousGate = ScheduledServiceBase.ScheduleRunGate;
        var previousWait = ScheduledServiceBase.WaitForDownloadAnswer;
        try
        {
            var registry = new ServiceScheduleRegistry(
                [service], CacheScanGateHarness.VisibleClientsStateService(),
                CreateDefaultProxy<ISignalRNotificationService>(), tracker,
                activityRegistry: null, cacheScanGate: CacheScanGateHarness.With(snapshot));
            await registry.TriggerRunAsync(EvictionKey);
            var held = Assert.Single(tracker.GetWaitingOperations());
            var notice = Assert.IsType<RunNotice>(held.Notice);
            CacheScanGateHarness.MakeIdle(snapshot);
            var result = await service.InvokeRunScheduledWorkAsync(RunTrigger.Scheduled, CancellationToken.None);
            Assert.False(result.RunFailed);
            Assert.Same(notice, service.CurrentRunNotice);
            Assert.Empty(tracker.GetWaitingOperations());
            Assert.Equal(OperationStatus.Skipped, tracker.GetOperation(held.Id)!.Status);
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previousGate;
            ScheduledServiceBase.WaitForDownloadAnswer = previousWait;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PendingCancellationPreventsWorkAcrossSlotConsumption(bool consumeFirst)
    {
        using var service = new RunGateProbeService(EvictionKey);
        service.SetNotificationMode(NotificationMode.Silent);
        var tracker = CreateRealTracker();
        var registry = CreateRegistry(service, CacheScanGateHarness.Idle(), tracker);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        service.Work = () => { calls++; entered.TrySetResult(); return release.Task; };
        var running = service.InvokeRunScheduledWorkAsync(RunTrigger.Manual, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        RunNotice? consumed = null;
        try
        {
            await registry.TriggerRunAsync(EvictionKey);
            await registry.TriggerRunAsync(EvictionKey);
            var waiting = Assert.Single(tracker.GetWaitingOperations());
            Assert.Empty(tracker.GetActiveOperations());
            var notice = Assert.IsType<RunNotice>(waiting.Notice);
            Assert.False(notice.ShowNotification);
            if (consumeFirst) Assert.True(service.TakePendingManualRun(out consumed));
            Assert.Equal(OperationCancelResult.Requested, tracker.CancelOperation(waiting.Id));
            Assert.True(notice.Cancelled);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (service.HasPendingRun && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.False(service.HasPendingRun);
            Assert.False(running.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await running;
        }
        var lastRun = service.LastRunUtc;
        if (consumed is not null)
        {
            var result = await service.InvokeRunScheduledWorkAsync(RunTrigger.Manual, CancellationToken.None, consumed);
            Assert.False(result.ShuttingDown);
            Assert.False(result.RunFailed);
            Assert.Equal(lastRun, service.LastRunUtc);
        }
        Assert.Equal(1, calls);
        Assert.True(service.TriggerImmediateRun(new RunNotice(NotificationMode.All, RunTrigger.Manual)).ShowNotification);
        Assert.True(service.TakePendingManualRun(out var later));
        Assert.False(later!.Cancelled);
    }

    [Fact]
    public async Task HiddenHoldCancellationRemovesTheRetainedAdmission()
    {
        using var service = new RunGateProbeService(EvictionKey);
        service.SetNotificationMode(NotificationMode.Silent);
        var tracker = CreateRealTracker();
        var registry = CreateRegistry(service, CacheScanGateHarness.Downloading(), tracker);
        await registry.TriggerRunAsync(EvictionKey);
        var held = Assert.Single(tracker.GetWaitingOperations());
        var notice = Assert.IsType<RunNotice>(held.Notice);
        Assert.Equal(held.Id, notice.OperationId);
        tracker.CancelOperation(held.Id);
        Assert.True(notice.Cancelled);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (tracker.GetOperation(held.Id)?.Status != OperationStatus.Cancelled && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        RaiseDownloadsEnded();
        Assert.False(service.HasPendingRun);
        Assert.False(service.TakePendingDeferredRun());
        Assert.Equal(OperationStatus.Cancelled, tracker.GetOperation(held.Id)!.Status);
    }

    [Fact]
    public async Task QueueTerminalTransfersTheSameNoticeThroughDownloadHoldAndSecondQueue()
    {
        using var service = new RunGateProbeService(EvictionKey);
        service.SetNotificationMode(NotificationMode.Silent);
        var snapshot = new DownloadSpeedSnapshot();
        var tracker = CreateRealTracker();
        var notifications = CreateDefaultProxy<ISignalRNotificationService>();
        var registry = CreateRegistry(service, CacheScanGateHarness.With(snapshot), tracker, notifications);
        var queue = new OperationQueueService(tracker,
            new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationQueueService>.Instance);
        var blocker = tracker.RegisterOperation(OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());
        var notice = new RunNotice(NotificationMode.Silent, RunTrigger.Manual);
        var queued = await queue.EnqueueAsync(OperationType.EvictionScan, ConflictScope.Bulk(), "Eviction Scan",
            () => throw new DownloadInProgressException("Downloading"), CancellationToken.None, notice: notice);
        Assert.Same(notice, tracker.GetOperation(queued.OperationId)!.Notice);
        CacheScanGateHarness.MakeBusy(snapshot);
        tracker.CompleteOperation(blocker, success: true);
        var holds = Assert.IsType<Dictionary<string, (Guid Id, RunNotice Notice)>>(
            typeof(ServiceScheduleRegistry).GetField("_deferredRuns", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            lock (holds)
            {
                if (holds.TryGetValue(EvictionKey, out var held))
                {
                    Assert.Same(notice, held.Notice);
                    break;
                }
            }
            Assert.True(DateTime.UtcNow < deadline, "The queued terminal did not retain its download hold");
            await Task.Yield();
        }
        CacheScanGateHarness.MakeIdle(snapshot);
        RaiseDownloadsEnded();
        Assert.True(service.TakePendingManualRun(out var consumed));
        Assert.Same(notice, consumed);
        tracker.RegisterOperation(OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());
        var second = await queue.EnqueueAsync(OperationType.EvictionScan, ConflictScope.Bulk(), "Eviction Scan",
            () => Task.FromResult<Guid?>(Guid.NewGuid()), CancellationToken.None, notice: consumed);
        Assert.True(second.Queued);
        Assert.Same(notice, tracker.GetOperation(second.OperationId)!.Notice);
        // A silent run parked a second time is still one background row.
        Assert.Equal(RunVisibility.Background,
            Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == second.OperationId).Visibility);
    }

    [Fact]
    public async Task SilentManualHttpHoldRetainsItsNoticeThroughBackstopAndQueue()
    {
        using var service = new RunGateProbeService(EvictionKey);
        service.SetNotificationMode(NotificationMode.Silent);
        var snapshot = new DownloadSpeedSnapshot();
        CacheScanGateHarness.MakeBusy(snapshot);
        var tracker = CreateRealTracker();
        var notifications = CreateDefaultProxy<ISignalRNotificationService>();
        var registry = CreateRegistry(service, CacheScanGateHarness.With(snapshot), tracker, notifications);
        var controller = new ScheduleController(registry);
        var held = Assert.IsType<QueuedOperationResponse>(Assert.IsType<AcceptedResult>((await controller.TriggerRunAsync(EvictionKey)).Result).Value);
        Assert.Equal("skipped", held.Status);
        var holdRun = Assert.Single(tracker.GetWaitingOperations());
        Assert.Equal(RunVisibility.Background, Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == holdRun.Id).Visibility);
        Assert.False(service.HasPendingRun);
        var holds = Assert.IsType<Dictionary<string, (Guid Id, RunNotice Notice)>>(
            typeof(ServiceScheduleRegistry).GetField("_deferredRuns", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry));
        var notice = holds[EvictionKey].Notice;
        CacheScanGateHarness.MakeIdle(snapshot);
        service.SetNotificationMode(NotificationMode.All);
        var resumed = Assert.IsType<QueuedOperationResponse>(Assert.IsType<AcceptedResult>((await controller.TriggerRunAsync(EvictionKey)).Result).Value);
        Assert.Equal("started", resumed.Status);
        Assert.True(service.TakePendingManualRun(out var consumed));
        Assert.Same(notice, consumed);
        await WithGateAsync(DeclineOnly("other"), () => service.InvokeRunScheduledWorkAsync(RunTrigger.Manual, CancellationToken.None, consumed));
        Assert.Same(notice, service.CurrentRunNotice);
        Assert.Equal(RunTrigger.Manual, notice.Trigger);
        tracker.RegisterOperation(OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());
        var queue = new OperationQueueService(tracker,
            new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationQueueService>.Instance);
        var queued = await queue.EnqueueAsync(OperationType.EvictionScan, ConflictScope.Bulk(), "Eviction Scan",
            () => Task.FromResult<Guid?>(Guid.NewGuid()), CancellationToken.None, notice: notice);
        Assert.True(queued.Queued);
        Assert.Same(notice, tracker.GetOperation(queued.OperationId)!.Notice);
        // The schedule's mode changed after the hold, but the run keeps the notice it was admitted with.
        var row = Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == queued.OperationId);
        Assert.Equal("evictionScan", row.OperationType);
        Assert.Equal(RunVisibility.Background, row.Visibility);
    }

    [Fact]
    public async Task SilentManualHttpRequestParksOnlyBehindExecutingLoop()
    {
        using var service = new RunGateProbeService(EvictionKey);
        service.SetNotificationMode(NotificationMode.Silent);
        var tracker = CreateRealTracker();
        tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        var notifications = CreateDefaultProxy<ISignalRNotificationService>();
        var registry = CreateRegistry(service, CacheScanGateHarness.Idle(), tracker, notifications);
        var controller = new ScheduleController(registry);
        await controller.TriggerRunAsync(EvictionKey);
        Assert.Empty(tracker.GetWaitingOperations());
        Assert.True(service.TakePendingManualRun(out var first));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Work = () => { started.TrySetResult(); return release.Task; };
        var running = WithGateAsync(DeclineOnly("other"), () => service.InvokeRunScheduledWorkAsync(RunTrigger.Manual, CancellationToken.None, first));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var response = Assert.IsType<QueuedOperationResponse>(Assert.IsType<AcceptedResult>((await controller.TriggerRunAsync(EvictionKey)).Result).Value);
            await controller.TriggerRunAsync(EvictionKey);
            Assert.True(response.AlreadyRunning);
            Assert.True(response.FollowUpQueued);
            // Two clicks behind the running loop park one follow-up, a background row under Silent.
            var parked = Assert.Single(tracker.GetWaitingOperations());
            Assert.Equal(RunVisibility.Background, Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == parked.Id).Visibility);
            Assert.False(running.IsCompleted);
            Assert.True(service.TakePendingManualRun(out var followup));
            Assert.NotSame(first, followup);
            Assert.Same(first, service.CurrentRunNotice);
            Assert.Same(followup, parked.Notice);
        }
        finally { release.TrySetResult(); await running; }
    }

    [Theory]
    [InlineData(NotificationMode.Manual, RunTrigger.Scheduled, RunVisibility.Background)]
    [InlineData(NotificationMode.Manual, RunTrigger.Startup, RunVisibility.Background)]
    [InlineData(NotificationMode.Manual, RunTrigger.Manual, RunVisibility.Card)]
    [InlineData(NotificationMode.Silent, RunTrigger.Scheduled, RunVisibility.Background)]
    [InlineData(NotificationMode.Silent, RunTrigger.Manual, RunVisibility.Background)]
    public async Task HeldRun_RetainsNoticeThroughReleaseAndASecondWait(
        NotificationMode mode, RunTrigger trigger, RunVisibility visibility)
    {
        using var service = new RunGateProbeService(EvictionKey);
        service.SetNotificationMode(mode);
        var notice = new RunNotice(mode, trigger);
        service.SelectRunNotice(notice);
        var notifications = CreateDefaultProxy<ISignalRNotificationService>();
        var holdTracker = CreateRealTracker();
        var previousGate = ScheduledServiceBase.ScheduleRunGate;
        var previousWait = ScheduledServiceBase.WaitForDownloadAnswer;
        try
        {
            _ = new ServiceScheduleRegistry([service], CacheScanGateHarness.VisibleClientsStateService(),
                notifications, holdTracker, activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.Downloading());
            Assert.NotNull(ScheduledServiceBase.ScheduleRunGate!(EvictionKey, trigger));
            var held = Assert.Single(holdTracker.GetWaitingOperations());
            Assert.Same(notice, held.Notice);
            Assert.Equal(visibility, Assert.Single(holdTracker.GetRuns().Runs, run => run.OperationId == held.Id).Visibility);
            service.SetNotificationMode(NotificationMode.All);
            RaiseDownloadsEnded();
            Assert.Equal(trigger == RunTrigger.Manual, service.HasPendingRun);
            RunNotice? consumed;
            if (trigger == RunTrigger.Manual) Assert.True(service.TakePendingManualRun(out consumed));
            else Assert.True(service.TakePendingDeferredRun(out consumed));
            await WithGateAsync(DeclineOnly("other"), () =>
                service.InvokeRunScheduledWorkAsync(trigger, CancellationToken.None, consumed));
            Assert.Same(notice, service.CurrentRunNotice);
            Assert.Equal(mode, service.CurrentRunNotice.Mode);
            Assert.Equal(trigger, service.CurrentRunNotice.Trigger);
            if (mode == NotificationMode.Silent)
            {
                var tracker = CreateRealTracker();
                tracker.RegisterOperation(OperationType.EvictionScan, "scan", new CancellationTokenSource());
                var queue = new OperationQueueService(tracker,
                    new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance),
                    NullLogger<OperationQueueService>.Instance);
                var queued = await queue.EnqueueAsync(OperationType.CacheSizeScan, ConflictScope.Bulk(), "size",
                    () => Task.FromResult<Guid?>(Guid.NewGuid()), CancellationToken.None, notice: notice);
                Assert.Equal(RunVisibility.Background,
                    Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == queued.OperationId).Visibility);
            }
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previousGate;
            ScheduledServiceBase.WaitForDownloadAnswer = previousWait;
        }
    }

    [Fact]
    public async Task DeclinedRun_LeavesLastRunUtcAndTheRunningFlagUntouchedAsync()
    {
        using var service = new RunGateProbeService(EvictionKey);
        var stampedBefore = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        service.SetLastRunUtc(stampedBefore);

        var (shuttingDown, runFailed) = await WithGateAsync(
            DeclineOnly(EvictionKey),
            () => service.InvokeRunScheduledWorkAsync(RunTrigger.Scheduled, CancellationToken.None));

        Assert.False(shuttingDown);
        Assert.False(runFailed);
        Assert.Equal(stampedBefore, service.LastRunUtc);
        Assert.False(service.IsCurrentlyExecuting);
        Assert.False(service.WorkRan);
        Assert.False(service.EndBroadcast);
    }

    [Fact]
    public async Task AllowedRun_StampsLastRunUtcSoTheDeclineIsWhatSpareditAsync()
    {
        using var service = new RunGateProbeService(EvictionKey);
        var stampedBefore = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        service.SetLastRunUtc(stampedBefore);

        await WithGateAsync(
            DeclineOnly("someOtherKey"),
            () => service.InvokeRunScheduledWorkAsync(RunTrigger.Scheduled, CancellationToken.None));

        Assert.True(service.WorkRan);
        Assert.True(service.EndBroadcast);
        Assert.NotEqual(stampedBefore, service.LastRunUtc);
    }

    [Fact]
    public async Task DeclinedRun_IsNotAFailureSoTheLoopTakesItsOrdinarySleepAsync()
    {
        using var service = new RunGateProbeService(EvictionKey);

        var started = DateTime.UtcNow;
        var (_, runFailed) = await WithGateAsync(
            DeclineOnly(EvictionKey),
            () => service.InvokeRunScheduledWorkAsync(RunTrigger.Scheduled, CancellationToken.None));

        // RunFailed false is what sends the caller to its interval sleep instead of ErrorRetryDelay,
        // and returning without waiting is what stops a long download costing one attempt a minute.
        Assert.False(runFailed);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DeclinedStartupRun_DoesNotExecuteStartupWorkAsync()
    {
        using var allowed = new StartupGateProbeService(EvictionKey);
        await WithGateAsync(DeclineOnly("someOtherKey"), async () =>
        {
            await allowed.StartAsync(CancellationToken.None);
            await allowed.StartupRan.WaitAsync(TimeSpan.FromSeconds(5));
            await allowed.StopAsync(CancellationToken.None);
            return true;
        });

        using var declined = new StartupGateProbeService(EvictionKey);
        await WithGateAsync(DeclineOnly(EvictionKey), async () =>
        {
            await declined.StartAsync(CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(750));
            await declined.StopAsync(CancellationToken.None);
            return true;
        });

        Assert.True(allowed.StartupRan.IsCompletedSuccessfully);
        Assert.False(declined.StartupRan.IsCompleted);
        Assert.Null(declined.LastRunUtc);
    }

    [Fact]
    public async Task ServiceThatProducesTheDownloadSignal_IsNeverBlockedByItAsync()
    {
        // The speed tracker runs on the same base class as the eight schedules. A gate that answered
        // for every subclass would stop the one service that reports whether a download is running,
        // exactly while one is.
        using var speedTracker = new RunGateProbeService(nameof(RustSpeedTrackerService));

        await WithGateAsync(
            DeclineOnly(EvictionKey),
            () => speedTracker.InvokeRunScheduledWorkAsync(RunTrigger.Scheduled, CancellationToken.None));

        Assert.True(speedTracker.WorkRan);
    }

    [Fact]
    public async Task RefusedRunNow_DoesNotArmThePendingRunAndReportsTheReasonAsync()
    {
        using var service = new RunGateProbeService(EvictionKey);
        var registry = CreateRegistry(service, CacheScanGateHarness.Downloading());

        var (status, skippedReason, _) = await registry.TriggerRunAsync(EvictionKey);

        Assert.NotNull(skippedReason);
        Assert.False(status.IsRunning);
        Assert.False(service.HasPendingRun);
    }

    [Fact]
    public async Task AcceptedRunNow_ArmsThePendingRunAndReportsNoReasonAsync()
    {
        using var service = new RunGateProbeService(EvictionKey);
        var registry = CreateRegistry(service, CacheScanGateHarness.Idle());

        var (status, skippedReason, _) = await registry.TriggerRunAsync(EvictionKey);

        Assert.Null(skippedReason);
        Assert.False(status.IsRunning);
        Assert.True(service.HasPendingRun);
    }

    [Fact]
    public async Task RunNow_ReportsAlreadyRunningWhileTheServicesOperationIsLiveAsync()
    {
        using var service = new RunGateProbeService(EvictionKey);
        var tracker = CreateRealTracker();
        var registry = CreateRegistry(service, CacheScanGateHarness.Idle(), tracker);

        using var cts = new CancellationTokenSource();
        tracker.RegisterOperation(OperationType.EvictionScan, EvictionKey, cts);

        var (status, skippedReason, _) = await registry.TriggerRunAsync(EvictionKey);

        Assert.Null(skippedReason);
        Assert.True(status.IsRunning);
    }

    /// <summary>
    /// Only a schedule whose work walks the cache tree waits for the tracker before its startup run
    /// is asked about. Everything else that runs on startup, the live log monitor and the dashboard
    /// warmer among them, has to start as promptly as it did.
    /// </summary>
    [Fact]
    public async Task OnlyACacheReadingScheduleWaitsForTheTrackerAsync()
    {
        using var service = new RunGateProbeService(EvictionKey);

        // A tracker that has not published yet, so there is something to wait for.
        var tracker = CacheScanGateHarness.TrackerWith(new DownloadSpeedSnapshot(), []);
        CacheScanGateHarness.SetField(tracker, "_unreportedSinceUtc", DateTime.UtcNow);

        var previousGate = ScheduledServiceBase.ScheduleRunGate;
        var previousWait = ScheduledServiceBase.WaitForDownloadAnswer;
        try
        {
            // Constructed here rather than through the helper, which restores the hooks: this test
            // is about the wait the registry installs, so the installed one has to stay put.
            _ = new ServiceScheduleRegistry(
                [service],
                CacheScanGateHarness.VisibleClientsStateService(),
                CreateDefaultProxy<ISignalRNotificationService>(),
                CreateRealTracker(),
                activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.GateOver(tracker));

            var wait = ScheduledServiceBase.WaitForDownloadAnswer!;

            // Log rotation never touches the cache tree, so it is not held up for an answer that
            // could not refuse it anyway.
            Assert.True(wait("logRotation", CancellationToken.None).IsCompleted);

            using var cancel = new CancellationTokenSource();
            var waiting = wait(EvictionKey, cancel.Token);
            Assert.False(waiting.IsCompleted);

            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previousGate;
            ScheduledServiceBase.WaitForDownloadAnswer = previousWait;
        }
    }

    [Fact]
    public async Task RunAll_CountsTheRefusalsAndStillTriggersEverythingElseAsync()
    {
        using var refused = new RunGateProbeService(EvictionKey);
        using var allowed = new RunGateProbeService("logRotation");
        var registry = CreateRegistry(
            new ScheduledBackgroundService[] { refused, allowed },
            CacheScanGateHarness.Downloading(),
            CreateRealTracker());

        var (triggeredCount, alreadyRunningCount, skippedCount, skippedReason, _) =
            await registry.TriggerAllAsync();

        // Both are asked the same question. The eviction scan walks the cache tree and declines; log
        // rotation never touches it and runs, so the fan-out is not blocked wholesale and the counts
        // add up to the two it considered.
        Assert.Equal(1, triggeredCount);
        Assert.Equal(0, alreadyRunningCount);
        Assert.Equal(1, skippedCount);
        Assert.NotNull(skippedReason);
        Assert.True(allowed.HasPendingRun);
        Assert.False(refused.HasPendingRun);
    }

    [Theory]
    [InlineData("gameImageFetch")]
    [InlineData("cacheSnapshot")]
    [InlineData("operationHistoryCleanup")]
    [InlineData("logRotation")]
    [InlineData("dashboardCacheWarmer")]
    public async Task JobThatNeverReadsTheCache_RunsWhileADownloadIsWritingAsync(string serviceKey)
    {
        using var service = new RunGateProbeService(serviceKey);
        var registry = CreateRegistry(service, CacheScanGateHarness.Downloading());

        var (_, skippedReason, _) = await registry.TriggerRunAsync(serviceKey);

        Assert.Null(skippedReason);
        Assert.True(service.HasPendingRun);
    }

    [Theory]
    [InlineData("cacheReconciliation")]
    [InlineData("cacheSizeScan")]
    [InlineData("gameDetection")]
    public async Task CacheScan_DeclinesWhileADownloadIsWritingAsync(string serviceKey)
    {
        using var service = new RunGateProbeService(serviceKey);
        var registry = CreateRegistry(service, CacheScanGateHarness.Downloading());

        var (_, skippedReason, _) = await registry.TriggerRunAsync(serviceKey);

        Assert.NotNull(skippedReason);
        Assert.False(service.HasPendingRun);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task HeldRun_NamesThePrefillOnlyWhenItIsTheOnlyClientDownloadingAsync(
        int downloadingClients,
        bool expectNamed)
    {
        // The gate reports that bytes are landing, never whose. With one client on the wire the only
        // download running is the one this app started; with a second machine downloading too, saying
        // "waiting for Scheduled Prefill to finish" sends the reader to watch the wrong thing and the
        // scan stays held after the prefill ends.
        using var service = new RunGateProbeService(EvictionKey);
        var snapshot = new DownloadSpeedSnapshot();
        CacheScanGateHarness.MakeBusy(snapshot);
        snapshot.ClientSpeeds = [.. Enumerable.Range(0, downloadingClients)
            .Select(index => new ClientSpeedInfo { ClientIp = $"10.0.0.{index + 5}", BytesPerSecond = 1_000_000 })];

        var notifications = CreateDefaultProxy<ISignalRNotificationService>();
        var tracker = CreateRealTracker();
        using var prefillCts = new CancellationTokenSource();
        tracker.RegisterOperation(OperationType.ScheduledPrefill, "Scheduled Prefill", prefillCts);

        var previous = ScheduledServiceBase.ScheduleRunGate;
        try
        {
            _ = new ServiceScheduleRegistry(
                [service],
                CacheScanGateHarness.VisibleClientsStateService(),
                notifications,
                tracker,
                activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.With(snapshot));

            Assert.NotNull(ScheduledServiceBase.ScheduleRunGate!(EvictionKey, RunTrigger.Scheduled));

            // The tracked row carries the name, which is what the card reads live and after a page
            // refresh.
            var held = Assert.Single(tracker.GetWaitingOperations());
            Assert.Equal(expectNamed ? "Scheduled Prefill" : null, held.BlockedByName);
            Assert.Equal(held.BlockedByName, Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == held.Id).BlockedByName);
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previous;
        }
    }

    [Fact]
    public void SilentSchedule_IsHeldAsABackgroundRow()
    {
        // Silent asks this schedule to stay out of the way, so the run is still held but as a
        // background row rather than a card that sits in the bar until the download finishes.
        using var service = new RunGateProbeService(EvictionKey);
        service.SetNotificationMode(NotificationMode.Silent);
        var tracker = CreateRealTracker();

        var previous = ScheduledServiceBase.ScheduleRunGate;
        try
        {
            _ = new ServiceScheduleRegistry(
                [service],
                CacheScanGateHarness.VisibleClientsStateService(),
                CreateDefaultProxy<ISignalRNotificationService>(),
                tracker,
                activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.Downloading());

            Assert.NotNull(ScheduledServiceBase.ScheduleRunGate!(EvictionKey, RunTrigger.Scheduled));

            var held = Assert.Single(tracker.GetWaitingOperations());
            Assert.Equal("Eviction Scan", held.Name);
            Assert.Equal(RunVisibility.Background, Assert.Single(tracker.GetRuns().Runs).Visibility);
            Assert.NotNull(ScheduledServiceBase.ScheduleRunGate!(EvictionKey, RunTrigger.Scheduled));
            Assert.Single(tracker.GetRuns().Runs);
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previous;
        }
    }

    [Fact]
    public void HeldSchedule_ShowsOneWaitingRowPerDownload()
    {
        using var service = new RunGateProbeService(EvictionKey);
        var tracker = CreateRealTracker();
        var snapshot = new DownloadSpeedSnapshot();
        var previous = ScheduledServiceBase.ScheduleRunGate;
        try
        {
            _ = new ServiceScheduleRegistry(
                [service],
                CacheScanGateHarness.VisibleClientsStateService(),
                CreateDefaultProxy<ISignalRNotificationService>(),
                tracker,
                activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.With(snapshot));
            var gate = ScheduledServiceBase.ScheduleRunGate!;

            CacheScanGateHarness.MakeBusy(snapshot);
            Assert.NotNull(gate(EvictionKey, RunTrigger.Scheduled));
            var first = Assert.Single(tracker.GetWaitingOperations()).Id;

            // Same download still running: the run is refused again, but it is already held and its
            // row is already listed, so no second row goes up.
            Assert.NotNull(gate(EvictionKey, RunTrigger.Scheduled));
            Assert.NotNull(gate(EvictionKey, RunTrigger.Scheduled));
            Assert.Equal(first, Assert.Single(tracker.GetWaitingOperations()).Id);

            // Downloads stop and the held run is handed to its loop, so the next download is a new
            // hold whose row takes over from the first.
            CacheScanGateHarness.MakeIdle(snapshot);
            Assert.Null(gate(EvictionKey, RunTrigger.Scheduled));

            CacheScanGateHarness.MakeBusy(snapshot);
            Assert.NotNull(gate(EvictionKey, RunTrigger.Scheduled));
            var second = Assert.Single(tracker.GetWaitingOperations());
            Assert.NotEqual(first, second.Id);
            Assert.Equal(first, second.PreviousOperationId);
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previous;
        }
    }

    [Fact]
    public void ScheduleRefusedForADownload_RunsOnceTheDownloadStops()
    {
        // The whole point of holding the run: a nightly scan refused during a prefill used to slip a
        // full interval. Another schedule asking after the download stops is the backstop path, and
        // it is the one a test can drive without the tracker's own edge.
        using var refused = new RunGateProbeService(EvictionKey);
        using var asksLater = new RunGateProbeService("cacheSizeScan");
        var snapshot = new DownloadSpeedSnapshot();
        var previous = ScheduledServiceBase.ScheduleRunGate;
        try
        {
            _ = new ServiceScheduleRegistry(
                [refused, asksLater],
                CacheScanGateHarness.VisibleClientsStateService(),
                CreateDefaultProxy<ISignalRNotificationService>(),
                CreateRealTracker(),
                activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.With(snapshot));
            var gate = ScheduledServiceBase.ScheduleRunGate!;

            CacheScanGateHarness.MakeBusy(snapshot);
            Assert.NotNull(gate(EvictionKey, RunTrigger.Scheduled));
            Assert.False(refused.TakePendingDeferredRun());

            CacheScanGateHarness.MakeIdle(snapshot);
            Assert.Null(gate("cacheSizeScan", RunTrigger.Scheduled));

            // The held run is armed on the schedule that was refused, not on the one that asked, and
            // on the deferred flag rather than the Run Now one so it is still reported as Scheduled.
            Assert.True(refused.TakePendingDeferredRun());
            Assert.False(refused.HasPendingRun);
            Assert.False(asksLater.TakePendingDeferredRun());
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previous;
        }
    }

    [Fact]
    public async Task RefusedRunNow_IsHeldAndRunsWhenTheCacheIsFreeAsync()
    {
        // A click is held like any other refused run. Run All is why: it refuses several schedules at
        // once and used to report them as simply not run, leaving the person to come back and press
        // it again once the download finished.
        using var service = new RunGateProbeService(EvictionKey);
        using var asksLater = new RunGateProbeService("cacheSizeScan");
        var snapshot = new DownloadSpeedSnapshot();
        var gate = CacheScanGateHarness.With(snapshot);
        CacheScanGateHarness.MakeBusy(snapshot);
        var registry = CreateRegistry([service, asksLater], gate);

        var (_, skippedReason, _) = await registry.TriggerRunAsync(EvictionKey);
        Assert.NotNull(skippedReason);
        // The answer says the run is kept rather than telling the person to try again, which is what
        // the gate's own sentence does for the controllers.
        Assert.Contains("queued", skippedReason);
        Assert.False(service.HasPendingRun);

        CacheScanGateHarness.MakeIdle(snapshot);
        await registry.TriggerRunAsync("cacheSizeScan");

        Assert.False(service.TakePendingDeferredRun());
        Assert.True(service.HasPendingRun);
    }

    [Fact]
    public void ManualRunRefusedWhileTheRowIsUp_AddsNoSecondRow()
    {
        // A click while the held run's row is showing needs no row of its own - it still gets the
        // reason on the response it is waiting for.
        using var service = new RunGateProbeService(EvictionKey);
        var tracker = CreateRealTracker();

        var previous = ScheduledServiceBase.ScheduleRunGate;
        try
        {
            _ = new ServiceScheduleRegistry(
                [service],
                CacheScanGateHarness.VisibleClientsStateService(),
                CreateDefaultProxy<ISignalRNotificationService>(),
                tracker,
                activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.Downloading());
            var gate = ScheduledServiceBase.ScheduleRunGate!;

            Assert.NotNull(gate(EvictionKey, RunTrigger.Scheduled));
            var held = Assert.Single(tracker.GetWaitingOperations()).Id;

            // A second timer tick adds nothing, and so does the click: one hold, one row.
            Assert.NotNull(gate(EvictionKey, RunTrigger.Scheduled));
            Assert.NotNull(gate(EvictionKey, RunTrigger.Manual));
            Assert.Equal(held, Assert.Single(tracker.GetRuns().Runs).OperationId);
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previous;
        }
    }

    [Fact]
    public void DownloadsEndingReleasesTheHold_WithoutWaitingForAScheduleToPoll()
    {
        // The gap this covers: a run is held, downloads stop, another download starts, and no schedule
        // asked the gate in between. The tracker sees that edge itself, so the held run is released
        // without anyone polling, leaving the next download free to hold it again.
        using var service = new RunGateProbeService(EvictionKey);
        var tracker = CreateRealTracker();

        var previous = ScheduledServiceBase.ScheduleRunGate;
        try
        {
            _ = new ServiceScheduleRegistry(
                [service],
                CacheScanGateHarness.VisibleClientsStateService(),
                CreateDefaultProxy<ISignalRNotificationService>(),
                tracker,
                activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.Downloading());
            var gate = ScheduledServiceBase.ScheduleRunGate!;

            Assert.NotNull(gate(EvictionKey, RunTrigger.Scheduled));
            var first = Assert.Single(tracker.GetWaitingOperations()).Id;

            // Downloads end. Nothing asks the gate, which is exactly the case that used to stay
            // armed-out; the tracker's own edge is what re-arms it.
            RaiseDownloadsEnded();

            Assert.NotNull(gate(EvictionKey, RunTrigger.Scheduled));
            var second = Assert.Single(tracker.GetWaitingOperations());
            Assert.NotEqual(first, second.Id);
            Assert.Equal(first, second.PreviousOperationId);
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previous;
        }
    }

    // The event is static and parameterless, so the test raises it the way the tracker does.
    private static void RaiseDownloadsEnded()
    {
        var field = typeof(RustSpeedTrackerService).GetField(
            nameof(RustSpeedTrackerService.DownloadsEnded),
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        ((Action?)field!.GetValue(null))?.Invoke();
    }

    [Fact]
    public async Task RunAll_TriggersEverythingWhileNothingIsDownloadingAsync()
    {
        using var first = new RunGateProbeService(EvictionKey);
        using var second = new RunGateProbeService("logRotation");
        var registry = CreateRegistry(
            new ScheduledBackgroundService[] { first, second },
            CacheScanGateHarness.Idle(),
            CreateRealTracker());

        var (triggeredCount, alreadyRunningCount, skippedCount, skippedReason, _) =
            await registry.TriggerAllAsync();

        Assert.Equal(2, triggeredCount);
        Assert.Equal(0, alreadyRunningCount);
        Assert.Equal(0, skippedCount);
        Assert.Null(skippedReason);
        Assert.True(first.HasPendingRun);
        Assert.True(second.HasPendingRun);
    }

    // Run All starts runs on a person's request but counts as automatic, so Manual only draws its
    // runs as background rows. [67]
    [Theory]
    [InlineData(NotificationMode.All, true)]
    [InlineData(NotificationMode.Manual, false)]
    [InlineData(NotificationMode.Silent, false)]
    public async Task RunAll_AdmitsItsRunsWithTheRunAllTrigger(NotificationMode mode, bool shown)
    {
        using var service = new RunGateProbeService(EvictionKey);
        service.SetNotificationMode(mode);
        var schedules = CreateRegistry(service, CacheScanGateHarness.Idle(), CreateRealTracker());

        await schedules.TriggerAllAsync();

        Assert.True(service.TakePendingManualRun(out var admitted));
        Assert.Equal(RunTrigger.RunAll, admitted!.Trigger);
        Assert.Equal(mode, admitted.Mode);
        Assert.Equal(shown, admitted.ShowNotification);
    }

    [Fact]
    public async Task RunAllRunHeldForADownload_IsWokenAsAnImmediateRun()
    {
        using var service = new RunGateProbeService(EvictionKey);
        var snapshot = new DownloadSpeedSnapshot();
        CacheScanGateHarness.MakeBusy(snapshot);
        var schedules = CreateRegistry(service, CacheScanGateHarness.With(snapshot), CreateRealTracker());

        var (_, _, skippedCount, _, _) = await schedules.TriggerAllAsync();
        Assert.Equal(1, skippedCount);
        Assert.False(service.HasPendingRun);

        CacheScanGateHarness.MakeIdle(snapshot);
        RaiseDownloadsEnded();

        Assert.False(service.TakePendingDeferredRun());
        Assert.True(service.TakePendingManualRun(out var woken));
        Assert.Equal(RunTrigger.RunAll, woken!.Trigger);
    }

    // A Run Now pressed while the schedule's Run All run is still pending makes that run the
    // person's own, so under Manual only its waiting row is sent again as a card. [67]
    [Fact]
    public async Task RunNowAfterRunAll_TurnsThePendingRunIntoACard()
    {
        using var service = new RunGateProbeService(EvictionKey);
        service.SetNotificationMode(NotificationMode.Manual);
        var tracker = CreateRealTracker();
        var schedules = CreateRegistry(service, CacheScanGateHarness.Idle(), tracker);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Work = () => { started.TrySetResult(); return release.Task; };
        var running = WithGateAsync(DeclineOnly("other"), () => service.InvokeRunScheduledWorkAsync(RunTrigger.Scheduled, CancellationToken.None));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var (_, _, _, _, followUps) = await schedules.TriggerAllAsync();
            Assert.Equal(1, followUps);
            var pending = Assert.Single(tracker.GetWaitingOperations());
            Assert.Equal(RunTrigger.RunAll, pending.Notice!.Trigger);
            var before = Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == pending.Id);
            Assert.Equal(RunVisibility.Background, before.Visibility);

            await schedules.TriggerRunAsync(EvictionKey);

            Assert.Equal(RunTrigger.Manual, pending.Notice.Trigger);
            var after = Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == pending.Id);
            Assert.Equal(RunVisibility.Card, after.Visibility);
            Assert.True(after.Revision > before.Revision);
        }
        finally
        {
            release.TrySetResult();
            await running;
        }
    }

    // A schedule that takes no second run (scheduled prefill) refuses the extra Run Now, but the
    // pending Run All run still becomes the person's own run. [103]
    [Fact]
    public void RunNowAfterRunAll_WithoutFollowUps_TurnsThePendingRunIntoACard()
    {
        using var service = new RunGateProbeService(EvictionKey, queueManualRuns: false);
        service.SetNotificationMode(NotificationMode.Manual);
        Assert.True(service.TryTriggerImmediateRun(new RunNotice(NotificationMode.Manual, RunTrigger.RunAll), out var first, out _));

        Assert.False(service.TryTriggerImmediateRun(new RunNotice(NotificationMode.Manual, RunTrigger.Manual), out var retained, out _));

        Assert.Same(first, retained);
        Assert.Equal(RunTrigger.Manual, retained.Trigger);
        Assert.True(retained.ShowNotification);
    }

    [Fact]
    public void RefusedScheduledRun_PutsUpTheWaitingRowThatStays()
    {
        // The row a run blocked by another heavy operation already gets from the queue. A run blocked
        // by a download used to get a terminal one that dismissed itself after a few seconds, so the
        // person was left with nothing on screen saying the run was still coming.
        using var service = new RunGateProbeService(EvictionKey);
        var tracker = CreateRealTracker();

        var previous = ScheduledServiceBase.ScheduleRunGate;
        try
        {
            // Constructed here rather than through the helper, which restores the hook: this test is
            // about the answer the registry installs, so the installed one has to stay put.
            _ = new ServiceScheduleRegistry(
                [service],
                CacheScanGateHarness.VisibleClientsStateService(),
                CreateDefaultProxy<ISignalRNotificationService>(),
                tracker,
                activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.Downloading());

            var reason = ScheduledServiceBase.ScheduleRunGate!(EvictionKey, RunTrigger.Scheduled);

            Assert.NotNull(reason);
            var waiting = Assert.Single(tracker.GetRuns().Runs);
            Assert.Equal("waiting", waiting.Status);
            Assert.Equal(OperationType.EvictionScan.ToWireString(), waiting.OperationType);
            // The name the card reads. The schedule key would render the card as "cacheReconciliation:
            // waiting for ...", which is not what the schedule is called anywhere a person looks.
            Assert.Equal("Eviction Scan", waiting.Name);
            // No prefill is running in this test, so there is no blocker to name and the card falls
            // back to its own wording rather than guessing at the download.
            Assert.Null(waiting.BlockedByName);
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previous;
        }
    }

    [Fact]
    public async Task RefusalAtPromotion_CompletesAsSkippedRatherThanFailedAsync()
    {
        var (status, row) =
            await PromoteWithStartFailureAsync(new DownloadInProgressException(DownloadReason));

        Assert.Equal(OperationStatus.Skipped, status.Status);
        // The operation record keeps the reason, which is what the logs and the history read. A
        // null startError would send this decline into the transient retry path instead.
        Assert.Equal(DownloadReason, status.Message);

        // The waiting row ends declined, not handed to something that took it over.
        Assert.Equal("skipped", row.Status);
        Assert.Null(row.NextOperationId);
        // The row carries no error text for a skip: the gate's English would reach every locale
        // untranslated. The card says why in its own words instead.
        Assert.Null(row.Error);
    }

    [Fact]
    public async Task RefusalOnTheImmediatePath_StillReachesTheCallerAsync()
    {
        // Nothing is queued ahead of it, so the start delegate runs inline. A refusal there has to
        // come back out: the HTTP caller is waiting on it and its 400 with the reason is the answer,
        // and swallowing it here would take that away.
        //
        // It must ALSO stay quiet by default. The caller renders the exception, so announcing as
        // well puts two notices on screen for one click, the second of them describing the
        // scheduled run rather than what was clicked.
        var announcements = new List<string>();
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync)
                && args?[1] is ScheduledRunCompleteEvent)
            {
                lock (announcements)
                {
                    announcements.Add((string)args[0]!);
                }
            }

            return Task.CompletedTask;
        });

        var tracker = CreateRealTracker();
        var registryNotifications = notifications;
        var previous = ScheduledServiceBase.ScheduleRunGate;
        try
        {
            using var service = new RunGateProbeService("gameDetection");
            _ = new ServiceScheduleRegistry(
                [service],
                CacheScanGateHarness.VisibleClientsStateService(),
                registryNotifications,
                tracker,
                activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.Idle());

            var queue = new OperationQueueService(
                tracker,
                new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance),
                NullLogger<OperationQueueService>.Instance);

            var refused = await Assert.ThrowsAsync<DownloadInProgressException>(
                () => queue.EnqueueAsync(
                    OperationType.GameDetection,
                    ConflictScope.Bulk(),
                    "Game Detection",
                    () => throw new DownloadInProgressException(DownloadReason),
                    CancellationToken.None));

            Assert.Equal(DownloadReason, refused.Message);
            Assert.Empty(tracker.GetWaitingOperations());

            await Task.Delay(TimeSpan.FromMilliseconds(250));
            lock (announcements)
            {
                Assert.Empty(announcements);
            }
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previous;
        }
    }

    [Fact]
    public async Task RefusalOnTheImmediatePath_IsAnnouncedThroughTheTrackerAsync()
    {
        // The queue has no idea which card a schedule owns and is not given one. It reports the
        // refusal on the tracker, and the registry, already subscribed to that terminal hook, turns
        // it into the same card a refused scheduled run produces.
        var announced = new TaskCompletionSource<ScheduledRunCompleteEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync)
                && (string?)args?[0] == SignalREvents.GameDetectionComplete
                && args[1] is ScheduledRunCompleteEvent complete)
            {
                announced.TrySetResult(complete);
            }

            return Task.CompletedTask;
        });

        var tracker = CreateRealTracker();
        using var service = new RunGateProbeService("gameDetection");
        var previous = ScheduledServiceBase.ScheduleRunGate;
        try
        {
            _ = new ServiceScheduleRegistry(
                [service],
                CacheScanGateHarness.VisibleClientsStateService(),
                notifications,
                tracker,
                activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.Idle());

            var queue = new OperationQueueService(
                tracker,
                new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance),
                NullLogger<OperationQueueService>.Instance);

            await Assert.ThrowsAsync<DownloadInProgressException>(
                () => queue.EnqueueAsync(
                    OperationType.GameDetection,
                    ConflictScope.Bulk(),
                    "Game Detection",
                    () => throw new DownloadInProgressException(DownloadReason),
                    CancellationToken.None,
                    reportRefusal: true));

            var terminal = await announced.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(OperationStatus.Skipped, terminal.Status);
            Assert.Null(terminal.Error);
            // A refusal with no notice of its own is a full card, and its skip is kept.
            var declined = Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == terminal.OperationId);
            Assert.Equal(RunVisibility.Card, declined.Visibility);
            Assert.True(declined.Retained);
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previous;
        }
    }

    [Fact]
    public async Task RealFailureAtPromotion_StillCompletesAsFailedAsync()
    {
        var (status, _) = await PromoteWithStartFailureAsync(new InvalidOperationException("the worker broke"));

        Assert.Equal(OperationStatus.Failed, status.Status);
        Assert.Equal("the worker broke", status.Message);
    }

    [Fact]
    public async Task StatedPreconditionThatIsNotADownload_StillCompletesAsFailedAsync()
    {
        // A write-permission re-check on a wrong PUID or PGID, and a datasource that cannot map
        // logical objects, both throw the base ValidationException at promotion. Reporting those as
        // skips would leave a misconfigured install doing nothing and saying nothing.
        var (status, row) =
            await PromoteWithStartFailureAsync(new ValidationException("Cannot write to the cache directory"));

        Assert.Equal(OperationStatus.Failed, status.Status);
        Assert.Equal("Cannot write to the cache directory", status.Message);
        Assert.Equal("failed", row.Status);
        Assert.Null(row.NextOperationId);
    }

    /// <summary>
    /// Parks a request behind a live operation, then finishes the blocker so the queue promotes the
    /// waiter and its start delegate throws <paramref name="startFailure"/>. Returns the waiting
    /// operation's terminal state and its row.
    /// </summary>
    private static async Task<(OperationInfo Status, OperationRun Row)>
        PromoteWithStartFailureAsync(Exception startFailure)
    {
        var tracker = CreateRealTracker();
        var conflictChecker = new OperationConflictChecker(
            tracker, NullLogger<OperationConflictChecker>.Instance);
        var queue = new OperationQueueService(
            tracker, conflictChecker,
            NullLogger<OperationQueueService>.Instance);

        using var blockerCts = new CancellationTokenSource();
        var blocker = tracker.RegisterOperation(
            OperationType.CacheSizeScan, "Cache File Scan", blockerCts);

        var queued = await queue.EnqueueAsync(
            OperationType.GameDetection,
            ConflictScope.Bulk(),
            "Game Detection",
            () => throw startFailure,
            CancellationToken.None);
        Assert.True(queued.Queued);

        tracker.CompleteOperation(blocker, success: true);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var waiting = tracker.GetOperation(queued.OperationId);
            if (waiting is not null && waiting.Status.IsTerminal())
            {
                return (waiting, Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == queued.OperationId));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new InvalidOperationException("The promoted operation never reached a terminal state.");
    }

    [Fact]
    public void RefusalReason_SerializesAsSkippedReasonOnBothResponses()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        var runNow = JsonSerializer.Serialize(
            new QueuedOperationResponse { Status = "skipped", SkippedReason = DownloadReason },
            options);
        var runAll = JsonSerializer.Serialize(
            new TriggerAllResponse { SkippedCount = 1, SkippedReason = DownloadReason },
            options);

        Assert.Contains("\"skippedReason\"", runNow);
        Assert.Contains("\"status\":\"skipped\"", runNow);
        Assert.Contains("\"skippedReason\"", runAll);
        Assert.Contains("\"skippedCount\":1", runAll);
    }

    [Fact]
    public void SkippedOperation_KeepsTheSuppliedReasonAndFallsBackWithoutOne()
    {
        var tracker = CreateRealTracker();

        using var withReason = new CancellationTokenSource();
        var explained = tracker.RegisterOperation(OperationType.EvictionScan, EvictionKey, withReason);
        tracker.CompleteOperation(explained, success: true, error: DownloadReason, skipped: true);

        using var withoutReason = new CancellationTokenSource();
        var bare = tracker.RegisterOperation(OperationType.EvictionScan, EvictionKey, withoutReason);
        tracker.CompleteOperation(bare, success: true, skipped: true);

        Assert.Equal(OperationStatus.Skipped, tracker.GetOperation(explained)!.Status);
        Assert.Equal(DownloadReason, tracker.GetOperation(explained)!.Message);
        // A skip with no reason stores the translated nothing-to-do key, so a reloaded card reads the
        // same words in the reader's language. [105]
        Assert.Equal(ScheduledRunReporter.NothingToDoStageKey, tracker.GetOperation(bare)!.Message);
    }

    private static Func<string, RunTrigger, string?> DeclineOnly(string serviceKey)
        => (key, _) => string.Equals(key, serviceKey, StringComparison.OrdinalIgnoreCase)
            ? DownloadReason
            : null;

    // The hook is a process-wide static, so it is always restored: another test class's service loop
    // must not inherit a gate this class installed.
    private static async Task<T> WithGateAsync<T>(Func<string, RunTrigger, string?> gate, Func<Task<T>> body)
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

    private static ServiceScheduleRegistry CreateRegistry(
        ScheduledBackgroundService service,
        CacheScanGate gate,
        UnifiedOperationTracker? tracker = null,
        ISignalRNotificationService? notifications = null)
        => CreateRegistry([service], gate, tracker, notifications);

    // A registry with a real gate installs the process-wide hooks, so whatever was there is put
    // back: another test class's service loop must not inherit this one's answer, and must not
    // inherit a startup wait over a tracker this class threw away either.
    private static ServiceScheduleRegistry CreateRegistry(
        IReadOnlyList<ScheduledServiceBase> services,
        CacheScanGate gate,
        UnifiedOperationTracker? tracker = null,
        ISignalRNotificationService? notifications = null)
    {
        var previousGate = ScheduledServiceBase.ScheduleRunGate;
        var previousWait = ScheduledServiceBase.WaitForDownloadAnswer;
        try
        {
            return new ServiceScheduleRegistry(
                services,
                CacheScanGateHarness.VisibleClientsStateService(),
                notifications ?? CreateDefaultProxy<ISignalRNotificationService>(),
                tracker,
                activityRegistry: null,
                cacheScanGate: gate);
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previousGate;
            ScheduledServiceBase.WaitForDownloadAnswer = previousWait;
        }
    }

    private static UnifiedOperationTracker CreateRealTracker()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        return new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
    }

    private static T CreateDefaultProxy<T>() where T : class
        => DispatchProxy.Create<T, DefaultDispatch<T>>();

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, DefaultDispatch<T>>();
        ((DefaultDispatch<T>)(object)proxy).Handler = handler;
        return proxy;
    }

    private class DefaultDispatch<T> : DispatchProxy where T : class
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (Handler is not null)
            {
                return Handler(targetMethod!, args);
            }

            var returnType = targetMethod!.ReturnType;
            if (returnType == typeof(void))
            {
                return null;
            }

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = returnType.GetGenericArguments()[0];
                var fromResult = typeof(Task)
                    .GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(resultType);
                return fromResult.Invoke(null, [DefaultValue(resultType)]);
            }

            return DefaultValue(returnType);
        }

        private static object? DefaultValue(Type type)
        {
            if (!type.IsValueType || Nullable.GetUnderlyingType(type) != null)
            {
                return null;
            }

            return Activator.CreateInstance(type);
        }
    }

    private sealed class ConfigurableRunGateProbeService : ConfigurableScheduledService
    {
        public ConfigurableRunGateProbeService()
            : base(NullLogger<ConfigurableRunGateProbeService>.Instance, TimeSpan.FromHours(1))
        {
        }

        public string ScheduleServiceKey => "scheduledPrefill";
        protected override string ServiceName => ScheduleServiceKey;
        protected override bool QueueManualRuns => false;
        public bool HasPendingRun => HasPendingManualRun();

        public bool TakePendingManualRun(out RunNotice? notice) => ConsumePendingManualRun(out notice);

        public Task<(bool ShuttingDown, bool RunFailed)> InvokeRunScheduledWorkAsync(
            RunTrigger trigger,
            CancellationToken stoppingToken,
            RunNotice? notice,
            Func<CancellationToken, Task> executeWork)
            => RunScheduledWorkAsync(
                ScheduleServiceKey,
                trigger,
                executeWork,
                stoppingToken,
                "{ServiceName} probe run failed",
                () => { },
                notice);

        protected override Task ExecuteWorkAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    private sealed class RunGateProbeService : ScheduledBackgroundService
    {
        private readonly string _serviceKey;

        public RunGateProbeService(string serviceKey, bool queueManualRuns = true)
            : base(NullLogger<RunGateProbeService>.Instance, new ConfigurationBuilder().Build())
        {
            _serviceKey = serviceKey;
            QueueManualRuns = queueManualRuns;
        }

        protected override bool QueueManualRuns { get; }
        protected override TimeSpan ErrorRetryDelay => TimeSpan.Zero;

        public override string ServiceKey => _serviceKey;
        protected override string ServiceName => _serviceKey;
        protected override TimeSpan Interval => TimeSpan.FromHours(1);
        protected override TimeSpan StartupDelay => TimeSpan.Zero;
        public override bool DefaultRunOnStartup => false;

        public bool WorkRan { get; private set; }
        public bool EndBroadcast { get; private set; }
        public bool HasPendingRun => HasPendingManualRun();
        public Func<Task>? Work { get; set; }

        public bool TakePendingDeferredRun() => ConsumePendingDeferredRun();
        public bool TakePendingDeferredRun(out RunNotice? notice) => ConsumePendingDeferredRun(out notice);
        public bool TakePendingManualRun(out RunNotice? notice) => ConsumePendingManualRun(out notice);

        public void SetLastRunUtc(DateTime value) => LastRunUtc = value;

        public Task<(bool ShuttingDown, bool RunFailed)> InvokeRunScheduledWorkAsync(
            RunTrigger trigger,
            CancellationToken stoppingToken,
            RunNotice? notice = null,
            Func<CancellationToken, Task>? executeWork = null)
            => RunScheduledWorkAsync(
                ServiceKey,
                trigger,
                token =>
                {
                    WorkRan = true;
                    return executeWork?.Invoke(token) ?? Work?.Invoke() ?? Task.CompletedTask;
                },
                stoppingToken,
                "{ServiceName} probe run failed",
                () => EndBroadcast = true,
                notice);

        protected override Task ExecuteWorkAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    private sealed class StartupGateProbeService : ScheduledBackgroundService
    {
        private readonly string _serviceKey;
        private readonly TaskCompletionSource _startupRan =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StartupGateProbeService(string serviceKey)
            : base(NullLogger<StartupGateProbeService>.Instance, new ConfigurationBuilder().Build())
        {
            _serviceKey = serviceKey;
        }

        public override string ServiceKey => _serviceKey;
        protected override string ServiceName => _serviceKey;
        protected override TimeSpan Interval => TimeSpan.FromHours(1);
        protected override TimeSpan StartupDelay => TimeSpan.Zero;
        public override bool DefaultRunOnStartup => true;

        public Task StartupRan => _startupRan.Task;

        protected override Task OnStartupAsync(CancellationToken stoppingToken)
        {
            _startupRan.TrySetResult();
            return Task.CompletedTask;
        }

        protected override Task ExecuteWorkAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }
}
