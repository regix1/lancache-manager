using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The waiting card must tell the user WHICH operation it is parked behind, and keep telling
/// the truth when the blocker changes hands (the first blocker finishes but another conflicting
/// operation is still active). The Schedules page must also report a service as running while
/// its work executes as a tracked background operation, because a fire-and-forget start drops
/// the loop's executing flag back to false for the whole run.
/// </summary>
public sealed class OperationWaitingBlockerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForceKillAwaitCannotOverwriteAWinningSuccessor(bool reapWaiter)
    {
        if (!OperatingSystem.IsWindows()) return;
        var tracker = CreateTracker();
        var controller = CreateController(tracker);
        var waiter = tracker.RegisterOperation(OperationType.EvictionScan, "Scan", new CancellationTokenSource());
        var successor = tracker.RegisterOperation(OperationType.EvictionScan, "Scan", new CancellationTokenSource());
        var unrelated = tracker.RegisterOperation(OperationType.EvictionScan, "Scan", new CancellationTokenSource());
        var pipeName = Guid.NewGuid().ToString("N");
        using var pipe = new System.IO.Pipes.NamedPipeServerStream(pipeName, System.IO.Pipes.PipeDirection.Out, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add($"$p = [System.IO.Pipes.NamedPipeClientStream]::new('.', '{pipeName}', [System.IO.Pipes.PipeDirection]::In); $p.Connect(); [Console]::WriteLine('ready'); $line = [Console]::ReadLine(); [Console]::WriteLine($line); $null = $p.ReadByte(); $p.Dispose()");
        Assert.True(process.Start());
        try
        {
            await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            tracker.AssociateProcess(successor, process);
            tracker.RecordHandoff(waiter, successor);
            tracker.CompleteOperation(waiter, true);
            if (reapWaiter) Reap(tracker, waiter);
            var forceKill = controller.ForceKillAsync(waiter);
            Assert.Equal("CANCEL", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(forceKill.IsCompleted);
            tracker.CompleteOperation(successor, false, "Original failure", onCompleting: operation => operation.PercentComplete = 63);
            var terminal = tracker.GetOperation(successor)!;
            var completedAt = terminal.CompletedAt;
            await pipe.WriteAsync(new byte[] { 1 });
            await pipe.FlushAsync();
            var response = Assert.IsType<OperationForceKillResponse>(Assert.IsType<OkObjectResult>((await forceKill).Result).Value);
            Assert.Equal(waiter, response.OperationId);
            Assert.Equal(OperationStatus.Failed, terminal.Status);
            Assert.Equal("Original failure", terminal.Message);
            Assert.False(terminal.Cancelled);
            Assert.Equal(63, terminal.PercentComplete);
            Assert.Equal(completedAt, terminal.CompletedAt);
            Assert.Null(terminal.AssociatedProcess);
            Assert.Equal(OperationStatus.Running, tracker.GetOperation(unrelated)!.Status);
            Assert.False(tracker.GetOperation(unrelated)!.Cancelled);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            tracker.CompleteOperation(successor, false, cancelled: true);
            tracker.CompleteOperation(unrelated, true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CancellationEndpointsFollowRetainedHandoffs(bool reapWaiter, bool forceKill)
    {
        var tracker = CreateTracker();
        var controller = CreateController(tracker);
        var waiter = tracker.RegisterOperation(OperationType.EvictionScan, "Scan", new CancellationTokenSource(), initialStatus: OperationStatus.Waiting);
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        var successor = tracker.RegisterOperation(OperationType.EvictionScan, "Scan", cts);
        var unrelated = tracker.RegisterOperation(OperationType.EvictionScan, "Scan", new CancellationTokenSource());
        tracker.RecordHandoff(waiter, successor);
        tracker.RecordHandoff(waiter, unrelated);
        tracker.RecordHandoff(waiter, waiter);
        tracker.CompleteOperation(waiter, true);
        if (reapWaiter) Reap(tracker, waiter);
        Assert.Equal(successor, tracker.GetOperation(waiter, followHandoff: true)!.Id);
        var status = Assert.IsType<OperationStatusResponse>(Assert.IsType<OkObjectResult>(controller.GetOperationStatus(waiter).Result).Value);
        Assert.Equal(waiter, status.Id);
        Assert.False(status.Active);
        Assert.Equal(100, status.PercentComplete);
        Assert.Null(status.Message);
        Assert.Equal(successor, status.NextOperationId);
        Assert.Equal(OperationStatus.Running, status.NextStatus);
        Assert.Equal(reapWaiter ? null : OperationStatus.Completed, status.Status);
        if (forceKill)
        {
            var response = Assert.IsType<OperationForceKillResponse>(Assert.IsType<OkObjectResult>((await controller.ForceKillAsync(waiter)).Result).Value);
            Assert.Equal(waiter, response.OperationId);
            Assert.Equal(OperationStatus.Cancelled, tracker.GetOperation(successor)!.Status);
        }
        else
        {
            var response = Assert.IsType<OperationCancelResponse>(Assert.IsType<OkObjectResult>(controller.CancelOperation(waiter).Result).Value);
            Assert.Equal(waiter, response.OperationId);
            Assert.Equal(OperationStatus.Cancelling, response.Status);
            Assert.False(response.AlreadyFinished);
            tracker.CompleteOperation(successor, false, cancelled: true);
        }
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(OperationStatus.Running, tracker.GetOperation(unrelated)!.Status);
        Assert.False(tracker.GetOperation(unrelated)!.Cancelled);
        var terminal = tracker.GetOperation(successor)!;
        var finished = Assert.IsType<OperationCancelResponse>(Assert.IsType<OkObjectResult>(controller.CancelOperation(waiter).Result).Value);
        Assert.Equal(waiter, finished.OperationId);
        Assert.Equal(OperationStatus.Cancelled, finished.Status);
        Assert.True(finished.AlreadyFinished);
        var completedAt = terminal.CompletedAt;
        await controller.ForceKillAsync(waiter);
        Assert.Equal(completedAt, terminal.CompletedAt);
        Reap(tracker, waiter);
        Reap(tracker, successor);
        Assert.Null(tracker.GetOperation(waiter, followHandoff: true));
        var missing = Assert.IsType<OperationStatusResponse>(Assert.IsType<OkObjectResult>(controller.GetOperationStatus(waiter).Result).Value);
        Assert.Null(missing.NextOperationId);
        Assert.IsType<NotFoundObjectResult>(controller.CancelOperation(waiter).Result);
        Assert.IsType<NotFoundObjectResult>((await controller.ForceKillAsync(waiter)).Result);
        Assert.Equal(OperationStatus.Running, tracker.GetOperation(unrelated)!.Status);
        tracker.CompleteOperation(unrelated, true);
    }

    [Fact]
    public void ReapingIntermediateRowsRetainsTheChainUntilTheFinalTargetIsReaped()
    {
        var tracker = CreateTracker();
        var first = tracker.RegisterOperation(OperationType.EvictionScan, "Scan", new CancellationTokenSource());
        var second = tracker.RegisterOperation(OperationType.EvictionScan, "Scan", new CancellationTokenSource());
        var last = tracker.RegisterOperation(OperationType.EvictionScan, "Scan", new CancellationTokenSource());
        tracker.RecordHandoff(first, second);
        tracker.RecordHandoff(second, last);
        tracker.CompleteOperation(first, true);
        tracker.CompleteOperation(second, true);
        Reap(tracker, first);
        Reap(tracker, second);
        Reap(tracker, last);
        Assert.Equal(last, tracker.GetOperation(first, true)!.Id);
        tracker.CompleteOperation(last, false, "Disk read failed");
        var controller = CreateController(tracker);
        var response = Assert.IsType<OperationStatusResponse>(Assert.IsType<OkObjectResult>(controller.GetOperationStatus(last).Result).Value);
        Assert.Equal(OperationStatus.Failed, response.Status);
        Assert.Equal("Disk read failed", response.Error);
        Assert.Null(response.Message);
        Assert.False(response.Active);
        Reap(tracker, last);
        Assert.Null(tracker.GetOperation(first, true));
        var links = Assert.IsType<System.Collections.Concurrent.ConcurrentDictionary<Guid, Guid>>(
            typeof(UnifiedOperationTracker).GetField("_handoffs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tracker));
        Assert.Empty(links);
    }

    private static void Reap(UnifiedOperationTracker tracker, Guid id)
    {
        typeof(UnifiedOperationTracker).GetMethod("ReapOperation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tracker, [id]);
    }

    private static OperationsController CreateController(UnifiedOperationTracker tracker) => new(
        tracker,
        new OperationCancellationService(tracker, new ProcessManager(NullLogger<ProcessManager>.Instance), NullLogger<OperationCancellationService>.Instance),
        CreateProxy<IOperationQueue>((method, _) => DefaultReturn(method.ReturnType)),
        CreateProxy<IServiceScheduleRegistry>((method, _) => DefaultReturn(method.ReturnType)));

    [Theory]
    [InlineData(OperationType.GameDetection, true)]
    [InlineData(OperationType.GameDetection, false)]
    [InlineData(OperationType.EvictionRemoval, true)]
    [InlineData(OperationType.EvictionRemoval, false)]
    public async Task EquivalentScanIsAcceptedWhileItsChildRuns(OperationType childType, bool childFirst)
    {
        var tracker = CreateTracker();
        var parent = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        var child = tracker.RegisterOperation(childType, "Child", new CancellationTokenSource());
        var ordered = CreateProxy<IUnifiedOperationTracker>((method, args) =>
            method.Name == nameof(IUnifiedOperationTracker.GetActiveOperations)
                ? tracker.GetActiveOperations().OrderBy(op => (op.Id == child) == childFirst ? 0 : 1).ToList()
                : method.Invoke(tracker, args));
        var events = new List<OperationWaitingNotification>();
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (args?.Length > 1 && args[1] is OperationWaitingNotification waiting) events.Add(waiting);
            return DefaultReturn(method.ReturnType);
        });
        var queue = new OperationQueueService(tracker,
            new OperationConflictChecker(ordered, NullLogger<OperationConflictChecker>.Instance),
            notifications, NullLogger<OperationQueueService>.Instance);
        var starts = 0;
        var accepted = await queue.EnqueueAsync(OperationType.EvictionScan, ConflictScope.Bulk(), "Eviction Scan",
            () => { starts++; return Task.FromResult<Guid?>(Guid.NewGuid()); }, CancellationToken.None);
        Assert.True(accepted.AlreadyRunning);
        Assert.False(accepted.Queued);
        Assert.Equal(parent, accepted.OperationId);
        Assert.Empty(tracker.GetWaitingOperations());
        Assert.Empty(events);
        Assert.Equal(0, starts);
        var distinct = await queue.EnqueueAsync(OperationType.EvictionScan, ConflictScope.Bulk(), "Other Scan",
            () => Task.FromResult<Guid?>(Guid.NewGuid()), CancellationToken.None);
        Assert.True(distinct.Queued);
    }

    [Fact]
    public async Task EquivalentScanAtPromotionReceivesTheWaitingHandoff()
    {
        var tracker = CreateTracker();
        var checker = new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance);
        var events = new List<OperationWaitingNotification>();
        var terminal = new TaskCompletionSource<OperationWaitingCompleteNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (args?.Length > 1 && args[1] is OperationWaitingNotification waiting)
                lock (events) events.Add(waiting);
            if (args?.Length > 1 && args[1] is OperationWaitingCompleteNotification complete) terminal.TrySetResult(complete);
            return DefaultReturn(method.ReturnType);
        });
        var queue = new OperationQueueService(tracker, checker, notifications, NullLogger<OperationQueueService>.Instance);
        var blocker = tracker.RegisterOperation(OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());
        var starts = 0;
        var queued = await queue.EnqueueAsync(OperationType.EvictionScan, ConflictScope.Bulk(), "Eviction Scan",
            () => { starts++; return Task.FromResult<Guid?>(Guid.NewGuid()); }, CancellationToken.None);
        var gate = Assert.IsType<SemaphoreSlim>(typeof(OperationQueueService).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(queue));
        await gate.WaitAsync();
        Guid active;
        try
        {
            tracker.CompleteOperation(blocker, success: true);
            active = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
            var verdict = await checker.CheckAsync(OperationType.EvictionScan, ConflictScope.Bulk(), CancellationToken.None);
            Assert.Equal("errors.conflict.duplicate", verdict!.StageKey);
            Assert.Equal(active, verdict.ActiveOperationId);
        }
        finally { gate.Release(); }
        var complete = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(queued.OperationId, active);
        Assert.True(complete.Promoted);
        Assert.Equal(queued.OperationId, complete.OperationId);
        Assert.Equal(OperationStatus.Completed, tracker.GetOperation(queued.OperationId)!.Status);
        Assert.Equal(OperationStatus.Running, tracker.GetOperation(active)!.Status);
        Assert.Equal(0, starts);
        lock (events) Assert.Equal("Cache File Scan", Assert.Single(events).BlockedByName);
        tracker.CancelOperation(queued.OperationId);
        Assert.True(tracker.GetOperation(active)!.Cancelled);
    }

    [Theory]
    [InlineData(NotificationMode.All, RunTrigger.Manual, true, false)]
    [InlineData(NotificationMode.All, RunTrigger.Scheduled, true, false)]
    [InlineData(NotificationMode.All, RunTrigger.Startup, true, false)]
    [InlineData(NotificationMode.Manual, RunTrigger.Manual, true, false)]
    [InlineData(NotificationMode.Manual, RunTrigger.Scheduled, false, false)]
    [InlineData(NotificationMode.Manual, RunTrigger.Startup, false, false)]
    [InlineData(NotificationMode.Silent, RunTrigger.Manual, false, true)]
    [InlineData(NotificationMode.Silent, RunTrigger.Scheduled, false, true)]
    [InlineData(NotificationMode.Silent, RunTrigger.Startup, false, true)]
    public async Task QueuedRun_UsesAdmittedModeAndTrigger(NotificationMode mode, RunTrigger trigger, bool visible, bool acknowledge)
    {
        var tracker = CreateTracker();
        var events = new List<OperationWaitingNotification>();
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync))
            {
                if (args?[1] is OperationWaitingNotification waiting) events.Add(waiting);
                return Task.CompletedTask;
            }
            return DefaultReturn(method.ReturnType);
        });
        var queue = new OperationQueueService(tracker,
            new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance),
            notifications, NullLogger<OperationQueueService>.Instance);
        tracker.RegisterOperation(OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());
        var notice = new RunNotice(mode, trigger);
        var queued = await queue.EnqueueAsync(OperationType.GameDetection, ConflictScope.Bulk(), "Game Detection",
            () => Task.FromResult<Guid?>(Guid.NewGuid()), CancellationToken.None, notice: notice);
        var duplicate = await queue.EnqueueAsync(OperationType.GameDetection, ConflictScope.Bulk(), "Game Detection",
            () => Task.FromResult<Guid?>(Guid.NewGuid()), CancellationToken.None, notice: notice);
        Assert.Equal(queued.OperationId, duplicate.OperationId);
        Assert.Equal(!visible, queue.IsWaiterSilent(queued.OperationId));
        Assert.Single(events);
        Assert.Equal(!visible, events[0].Silent);
        Assert.Equal(acknowledge, events[0].Acknowledge);
        if (acknowledge) Assert.False(notice.TryAcknowledge());
    }

    [Fact]
    public async Task EnqueueBehindActiveOperation_WaitingEventNamesBlockerAsync()
    {
        var tracker = CreateTracker();
        var conflictChecker = new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance);
        var waitingEvents = new List<OperationWaitingNotification>();
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync))
            {
                if ((string)args![0]! == SignalREvents.OperationWaiting
                    && args[1] is OperationWaitingNotification waiting)
                {
                    lock (waitingEvents)
                    {
                        waitingEvents.Add(waiting);
                    }
                }

                return Task.CompletedTask;
            }

            return DefaultReturn(method.ReturnType);
        });
        var queue = new OperationQueueService(
            tracker, conflictChecker, notifications, NullLogger<OperationQueueService>.Instance);

        tracker.RegisterOperation(
            OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());

        var queued = await queue.EnqueueAsync(
            OperationType.GameDetection,
            ConflictScope.Bulk(),
            "Game Detection",
            () => Task.FromResult<Guid?>(Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(queued.Queued);
        OperationWaitingNotification emitted;
        lock (waitingEvents)
        {
            emitted = Assert.Single(waitingEvents);
        }
        Assert.Equal(queued.OperationId, emitted.OperationId);
        Assert.Equal("Cache File Scan", emitted.BlockedByName);
        Assert.False(emitted.Silent);
        Assert.Equal("Cache File Scan", queue.GetWaitingBlockerName(queued.OperationId));
    }

    [Fact]
    public async Task BlockerHandoff_ReemitsWaitingNamingNewBlockerAsync()
    {
        var tracker = CreateTracker();
        var conflictChecker = new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance);
        var secondBlockerAnnounced = new TaskCompletionSource<OperationWaitingNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync))
            {
                if ((string)args![0]! == SignalREvents.OperationWaiting
                    && args[1] is OperationWaitingNotification { BlockedByName: "Eviction Scan" } reannounced)
                {
                    secondBlockerAnnounced.TrySetResult(reannounced);
                }

                return Task.CompletedTask;
            }

            return DefaultReturn(method.ReturnType);
        });
        var queue = new OperationQueueService(
            tracker, conflictChecker, notifications, NullLogger<OperationQueueService>.Instance);

        var firstBlockerId = tracker.RegisterOperation(
            OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());

        var startCalls = 0;
        var queued = await queue.EnqueueAsync(
            OperationType.GameDetection,
            ConflictScope.Bulk(),
            "Game Detection",
            () =>
            {
                Interlocked.Increment(ref startCalls);
                return Task.FromResult<Guid?>(Guid.NewGuid());
            },
            CancellationToken.None);
        Assert.True(queued.Queued);

        // A second conflicting operation is already active when the first blocker finishes, so
        // the promotion pass must keep the waiter parked and announce who blocks it NOW.
        tracker.RegisterOperation(
            OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        tracker.CompleteOperation(firstBlockerId, success: true);

        var reannounced = await secondBlockerAnnounced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(queued.OperationId, reannounced.OperationId);
        Assert.Equal("Game Detection", reannounced.Name);
        Assert.Equal(0, startCalls);
        Assert.Equal("Eviction Scan", queue.GetWaitingBlockerName(queued.OperationId));
    }

    [Fact]
    public async Task SilentRun_AnnouncesItsParkingOnceAndNotAgainOnHandoffAsync()
    {
        var tracker = CreateTracker();
        var conflictChecker = new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance);
        var waitingEvents = new List<OperationWaitingNotification>();
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync))
            {
                if ((string)args![0]! == SignalREvents.OperationWaiting
                    && args[1] is OperationWaitingNotification waiting)
                {
                    lock (waitingEvents)
                    {
                        waitingEvents.Add(waiting);
                    }
                }

                return Task.CompletedTask;
            }

            return DefaultReturn(method.ReturnType);
        });
        var queue = new OperationQueueService(
            tracker, conflictChecker, notifications, NullLogger<OperationQueueService>.Instance);

        var firstBlockerId = tracker.RegisterOperation(
            OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());

        var startCalls = 0;
        var queued = await queue.EnqueueAsync(
            OperationType.GameDetection,
            ConflictScope.Bulk(),
            "Game Detection",
            () =>
            {
                Interlocked.Increment(ref startCalls);
                return Task.FromResult<Guid?>(Guid.NewGuid());
            },
            CancellationToken.None,
            showWaitingCard: false,
            notice: new RunNotice(NotificationMode.Silent, RunTrigger.Scheduled));

        Assert.True(queued.Queued);
        lock (waitingEvents)
        {
            var announced = Assert.Single(waitingEvents);
            Assert.True(announced.Silent);
            Assert.Equal(queued.OperationId, announced.OperationId);
        }

        // The blocker changes hands, which is the one thing that makes a parked run speak twice.
        // A silent run stays quiet through it, but the queue must still record the new blocker:
        // the recovery endpoint reads that name for every waiter, silent or not.
        tracker.RegisterOperation(
            OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        tracker.CompleteOperation(firstBlockerId, success: true);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (queue.GetWaitingBlockerName(queued.OperationId) != "Eviction Scan"
            && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.Equal("Eviction Scan", queue.GetWaitingBlockerName(queued.OperationId));
        Assert.Equal(0, startCalls);
        lock (waitingEvents)
        {
            Assert.Single(waitingEvents, waiting => waiting.Acknowledge == true);
            Assert.False(waitingEvents.Last().Acknowledge);
        }
    }

    [Fact]
    public async Task WaitingRecovery_ReturnsTheParkedRunAndLeavesTheSilentOneOutAsync()
    {
        var tracker = CreateTracker();
        var conflictChecker = new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance);
        var notifications = CreateProxy<ISignalRNotificationService>((method, _) => DefaultReturn(method.ReturnType));
        var queue = new OperationQueueService(
            tracker, conflictChecker, notifications, NullLogger<OperationQueueService>.Instance);

        tracker.RegisterOperation(
            OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());

        var announced = await queue.EnqueueAsync(
            OperationType.GameDetection,
            ConflictScope.Bulk(),
            "Game Detection",
            () => Task.FromResult<Guid?>(Guid.NewGuid()),
            CancellationToken.None);
        var silent = await queue.EnqueueAsync(
            OperationType.EvictionScan,
            ConflictScope.Bulk(),
            "Eviction Scan",
            () => Task.FromResult<Guid?>(Guid.NewGuid()),
            CancellationToken.None,
            showWaitingCard: false);

        Assert.True(announced.Queued);
        Assert.True(silent.Queued);
        Assert.False(queue.IsWaiterSilent(announced.OperationId));
        Assert.True(queue.IsWaiterSilent(silent.OperationId));

        var controller = new OperationsController(
            tracker,
            new OperationCancellationService(
                tracker,
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                NullLogger<OperationCancellationService>.Instance),
            queue,
            CreateRegistry(new IdleScanProbeService(), tracker));

        // Both runs are parked, so both are Waiting operations the tracker would hand back. The
        // refresh must rebuild only the card that is actually on screen: the silent run's notice
        // already came and went, and reissuing it here is a second announcement for one parking.
        var rows = Assert.IsType<List<WaitingOperationResponse>>(
            Assert.IsType<OkObjectResult>(controller.GetWaitingOperations().Result).Value);
        Assert.Equal(2, rows.Count);
        Assert.False(Assert.Single(rows, item => item.OperationId == silent.OperationId).ShowNotification);
        var row = Assert.Single(rows, item => item.OperationId == announced.OperationId);
        Assert.Equal(announced.OperationId, row.OperationId);
        Assert.Equal("Cache File Scan", row.BlockedByName);
    }

    [Fact]
    public async Task StartThrowsAtPromotion_WaitingCardFailsWithTheRealReasonAsync()
    {
        var tracker = CreateTracker();
        var conflictChecker = new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance);
        var terminal = new TaskCompletionSource<OperationWaitingCompleteNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync))
            {
                if ((string)args![0]! == SignalREvents.OperationWaitingComplete
                    && args[1] is OperationWaitingCompleteNotification complete)
                {
                    terminal.TrySetResult(complete);
                }

                return Task.CompletedTask;
            }

            return DefaultReturn(method.ReturnType);
        });
        var queue = new OperationQueueService(
            tracker, conflictChecker, notifications, NullLogger<OperationQueueService>.Instance);

        var blockerId = tracker.RegisterOperation(
            OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());
        var queued = await queue.EnqueueAsync(
            OperationType.GameDetection,
            ConflictScope.Bulk(),
            "Game Detection",
            () => throw new InvalidOperationException("datasource evidence is ambiguous"),
            CancellationToken.None);
        Assert.True(queued.Queued);

        tracker.CompleteOperation(blockerId, success: true);

        // A thrown start is a permanent refusal for this run: the card must fail promptly with
        // the thrown message, never spin through the transient-gate retry loop first.
        var complete = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(queued.OperationId, complete.OperationId);
        Assert.False(complete.Promoted);
        Assert.Equal("datasource evidence is ambiguous", complete.Error);
    }

    [Fact]
    public void ScheduledServiceWithBackgroundOperation_ReportsRunning()
    {
        var tracker = CreateTracker();
        var probe = new IdleScanProbeService();
        var registry = CreateRegistry(probe, tracker);

        var idle = registry.GetAll().Single(s => s.Key == "cacheSizeScan");
        Assert.False(idle.IsRunning);

        // The loop is not executing (fire-and-forget start already returned); only the tracked
        // background operation says the scan is still going.
        var operationId = tracker.RegisterOperation(
            OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());
        var running = registry.GetAll().Single(s => s.Key == "cacheSizeScan");
        Assert.True(running.IsRunning);

        tracker.CompleteOperation(operationId, success: true);
        var finished = registry.GetAll().Single(s => s.Key == "cacheSizeScan");
        Assert.False(finished.IsRunning);
    }

    [Fact]
    public async Task TrackedOperationTerminal_BroadcastsSchedulesAsync()
    {
        var tracker = CreateTracker();
        var schedulesBroadcast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync))
            {
                if ((string)args![0]! == SignalREvents.SchedulesUpdated)
                {
                    schedulesBroadcast.TrySetResult();
                }

                return Task.CompletedTask;
            }

            return DefaultReturn(method.ReturnType);
        });
        _ = CreateRegistry(new IdleScanProbeService(), tracker, notifications);

        // A background run ends with no work-state tick at all; the terminal event is the only
        // signal that can turn the running dot off, so it must drive a schedules broadcast.
        var operationId = tracker.RegisterOperation(
            OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());
        tracker.CompleteOperation(operationId, success: true);

        await schedulesBroadcast.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static UnifiedOperationTracker CreateTracker()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        return new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
    }

    private static ServiceScheduleRegistry CreateRegistry(
        ScheduledBackgroundService service,
        UnifiedOperationTracker tracker,
        ISignalRNotificationService? notifications = null)
    {
        notifications ??= CreateProxy<ISignalRNotificationService>((method, _) => DefaultReturn(method.ReturnType));
        var stateService = (IStateService)System.Reflection.DispatchProxy.Create<IStateService, NullReturningProxy>();
        return new ServiceScheduleRegistry(new IHostedService[] { service }, stateService, notifications, tracker);
    }

    /// <summary>
    /// A scheduled service that is registered under the real cacheSizeScan key but whose loop
    /// never runs, mirroring the state a fire-and-forget scan leaves behind: executing flag
    /// false while the tracked operation carries the actual work.
    /// </summary>
    private sealed class IdleScanProbeService : ScheduledBackgroundService
    {
        public IdleScanProbeService()
            : base(NullLogger<IdleScanProbeService>.Instance, new ConfigurationBuilder().Build())
        {
        }

        public override string ServiceKey => "cacheSizeScan";
        protected override string ServiceName => "IdleScanProbe";
        protected override TimeSpan Interval => TimeSpan.FromHours(1);
        protected override TimeSpan StartupDelay => TimeSpan.Zero;
        public override bool DefaultRunOnStartup => false;

        protected override Task ExecuteWorkAsync(CancellationToken stoppingToken)
            => Task.CompletedTask;
    }

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = System.Reflection.DispatchProxy.Create<T, ProxyDispatch<T>>();
        ((ProxyDispatch<T>)(object)proxy).Handler = handler;
        return proxy;
    }

    private static object? DefaultReturn(Type returnType)
    {
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
        => !type.IsValueType || Nullable.GetUnderlyingType(type) != null
            ? null
            : Activator.CreateInstance(type);

    private class ProxyDispatch<T> : System.Reflection.DispatchProxy where T : class
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => Handler!(targetMethod!, args);
    }
}
