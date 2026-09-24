using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
            var response = Assert.IsType<OperationCancelResponse>(Assert.IsType<OkObjectResult>((await controller.CancelOperation(waiter)).Result).Value);
            Assert.Equal(waiter, response.OperationId);
            Assert.Equal(OperationStatus.Cancelling, response.Status);
            Assert.False(response.AlreadyFinished);
            tracker.CompleteOperation(successor, false, cancelled: true);
        }
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(OperationStatus.Running, tracker.GetOperation(unrelated)!.Status);
        Assert.False(tracker.GetOperation(unrelated)!.Cancelled);
        var terminal = tracker.GetOperation(successor)!;
        var finished = Assert.IsType<OperationCancelResponse>(Assert.IsType<OkObjectResult>((await controller.CancelOperation(waiter)).Result).Value);
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
        Assert.IsType<NotFoundObjectResult>((await controller.CancelOperation(waiter)).Result);
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
        new OperationCancellationService(tracker, new ProcessManager(NullLogger<ProcessManager>.Instance), NullLogger<OperationCancellationService>.Instance))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = new ServiceCollection()
                        .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
                        .BuildServiceProvider()
                }
            }
        };

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
        var queue = new OperationQueueService(tracker,
            new OperationConflictChecker(ordered, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationQueueService>.Instance);
        var starts = 0;
        var accepted = await queue.EnqueueAsync(OperationType.EvictionScan, ConflictScope.Bulk(), "Eviction Scan",
            () => { starts++; return Task.FromResult<Guid?>(Guid.NewGuid()); }, CancellationToken.None);
        Assert.True(accepted.AlreadyRunning);
        Assert.False(accepted.Queued);
        Assert.Equal(parent, accepted.OperationId);
        Assert.Empty(tracker.GetWaitingOperations());
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
        var queue = new OperationQueueService(tracker, checker, NullLogger<OperationQueueService>.Instance);
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
        // The waiting run ends as handed on to the running duplicate, still naming what it waited for.
        var ended = await WaitForOperationAsync(tracker, queued.OperationId, operation => operation.Status == OperationStatus.Completed);
        Assert.NotEqual(queued.OperationId, active);
        Assert.Equal(active, ended.NextOperationId);
        Assert.Equal("Cache File Scan", ended.BlockedByName);
        Assert.Equal(OperationStatus.Running, tracker.GetOperation(active)!.Status);
        Assert.Equal(0, starts);
        tracker.CancelOperation(queued.OperationId);
        Assert.True(tracker.GetOperation(active)!.Cancelled);
    }

    // A parked run's row reads the notice it was admitted with, and a duplicate request joins the
    // same waiting run.
    [Theory]
    [InlineData(NotificationMode.All, RunTrigger.Manual, RunVisibility.Card)]
    [InlineData(NotificationMode.All, RunTrigger.Scheduled, RunVisibility.Card)]
    [InlineData(NotificationMode.All, RunTrigger.Startup, RunVisibility.Card)]
    [InlineData(NotificationMode.Manual, RunTrigger.Manual, RunVisibility.Card)]
    [InlineData(NotificationMode.Manual, RunTrigger.Scheduled, RunVisibility.Background)]
    [InlineData(NotificationMode.Manual, RunTrigger.Startup, RunVisibility.Background)]
    [InlineData(NotificationMode.Silent, RunTrigger.Manual, RunVisibility.Background)]
    [InlineData(NotificationMode.Silent, RunTrigger.Scheduled, RunVisibility.Background)]
    [InlineData(NotificationMode.Silent, RunTrigger.Startup, RunVisibility.Background)]
    [InlineData(NotificationMode.Hidden, RunTrigger.Manual, RunVisibility.Hidden)]
    [InlineData(NotificationMode.Hidden, RunTrigger.Scheduled, RunVisibility.Hidden)]
    [InlineData(NotificationMode.Hidden, RunTrigger.Startup, RunVisibility.Hidden)]
    public async Task QueuedRun_UsesAdmittedModeAndTrigger(
        NotificationMode mode,
        RunTrigger trigger,
        RunVisibility visibility)
    {
        var tracker = CreateTracker();
        var queue = new OperationQueueService(tracker,
            new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationQueueService>.Instance);
        tracker.RegisterOperation(OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());
        var notice = new RunNotice(mode, trigger);
        var queued = await queue.EnqueueAsync(OperationType.GameDetection, ConflictScope.Bulk(), "Game Detection",
            () => Task.FromResult<Guid?>(Guid.NewGuid()), CancellationToken.None, notice: notice);
        var duplicate = await queue.EnqueueAsync(OperationType.GameDetection, ConflictScope.Bulk(), "Game Detection",
            () => Task.FromResult<Guid?>(Guid.NewGuid()), CancellationToken.None, notice: notice);
        Assert.Equal(queued.OperationId, duplicate.OperationId);
        Assert.Same(notice, tracker.GetOperation(queued.OperationId)!.Notice);
        var row = Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == queued.OperationId);
        Assert.Equal("waiting", row.Status);
        Assert.Equal(visibility, row.Visibility);
    }

    [Fact]
    public async Task EnqueueBehindActiveOperation_WaitingRowNamesBlockerAsync()
    {
        var tracker = CreateTracker();
        var conflictChecker = new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance);
        var queue = new OperationQueueService(
            tracker, conflictChecker, NullLogger<OperationQueueService>.Instance);

        tracker.RegisterOperation(
            OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());

        var queued = await queue.EnqueueAsync(
            OperationType.GameDetection,
            ConflictScope.Bulk(),
            "Game Detection",
            () => Task.FromResult<Guid?>(Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(queued.Queued);
        var row = Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == queued.OperationId);
        Assert.Equal("waiting", row.Status);
        Assert.Equal("Cache File Scan", row.BlockedByName);
        // A waiter with no notice is a full card.
        Assert.Equal(RunVisibility.Card, row.Visibility);
    }

    [Fact]
    public async Task BlockerHandoff_WaitingRowNamesNewBlockerAsync()
    {
        var tracker = CreateTracker();
        var conflictChecker = new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance);
        var queue = new OperationQueueService(
            tracker, conflictChecker, NullLogger<OperationQueueService>.Instance);

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
        // the promotion pass must keep the waiter parked and its row must name who blocks it NOW.
        tracker.RegisterOperation(
            OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        tracker.CompleteOperation(firstBlockerId, success: true);

        var waiting = await WaitForOperationAsync(tracker, queued.OperationId, operation => operation.BlockedByName == "Eviction Scan");
        Assert.Equal(OperationStatus.Waiting, waiting.Status);
        Assert.Equal("Game Detection", waiting.Name);
        Assert.Equal(0, startCalls);
        Assert.Equal("Eviction Scan", Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == queued.OperationId).BlockedByName);
    }

    [Fact]
    public async Task SilentRun_KeepsItsBackgroundRowAndRecordsTheNewBlockerAsync()
    {
        var tracker = CreateTracker();
        var conflictChecker = new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance);
        var queue = new OperationQueueService(
            tracker, conflictChecker, NullLogger<OperationQueueService>.Instance);

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
            notice: new RunNotice(NotificationMode.Silent, RunTrigger.Scheduled));

        Assert.True(queued.Queued);
        Assert.Equal(RunVisibility.Background, Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == queued.OperationId).Visibility);

        // The blocker changes hands; the silent run's row records the new blocker and stays a
        // background row, because the recovery endpoint and the run list read that name for every
        // waiter.
        tracker.RegisterOperation(
            OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        tracker.CompleteOperation(firstBlockerId, success: true);

        await WaitForOperationAsync(tracker, queued.OperationId, operation => operation.BlockedByName == "Eviction Scan");
        var row = Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == queued.OperationId);
        Assert.Equal("Eviction Scan", row.BlockedByName);
        Assert.Equal(RunVisibility.Background, row.Visibility);
        Assert.Equal(0, startCalls);
    }

    [Fact]
    public async Task WaitingRecovery_ReturnsEveryParkedRunWithItsBlockerAsync()
    {
        var tracker = CreateTracker();
        var conflictChecker = new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance);
        var queue = new OperationQueueService(
            tracker, conflictChecker, NullLogger<OperationQueueService>.Instance);

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
            notice: new RunNotice(NotificationMode.Silent, RunTrigger.Scheduled));

        Assert.True(announced.Queued);
        Assert.True(silent.Queued);

        var controller = new OperationsController(
            tracker,
            new OperationCancellationService(
                tracker,
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                NullLogger<OperationCancellationService>.Instance));

        // Both runs are parked, so the endpoint lists both; how each is drawn comes from its run row.
        var rows = Assert.IsType<List<WaitingOperationResponse>>(
            Assert.IsType<OkObjectResult>(controller.GetWaitingOperations().Result).Value);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, item => Assert.Equal("Cache File Scan", item.BlockedByName));
        var runs = tracker.GetRuns().Runs;
        Assert.Equal(RunVisibility.Card, Assert.Single(runs, run => run.OperationId == announced.OperationId).Visibility);
        Assert.Equal(RunVisibility.Background, Assert.Single(runs, run => run.OperationId == silent.OperationId).Visibility);
    }

    [Fact]
    public async Task StartThrowsAtPromotion_WaitingCardFailsWithTheRealReasonAsync()
    {
        var tracker = CreateTracker();
        var conflictChecker = new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance);
        var queue = new OperationQueueService(
            tracker, conflictChecker, NullLogger<OperationQueueService>.Instance);

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
        var failed = await WaitForOperationAsync(tracker, queued.OperationId, operation => operation.Status.IsTerminal());
        Assert.Equal(OperationStatus.Failed, failed.Status);
        Assert.Null(failed.NextOperationId);
        Assert.Equal("datasource evidence is ambiguous",
            Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == queued.OperationId).Error);
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

    // A bounded wait used as a failure detector: the queue promotes and re-checks blockers off the
    // calling thread, after the terminal that triggers it has returned.
    private static async Task<OperationInfo> WaitForOperationAsync(
        UnifiedOperationTracker tracker, Guid operationId, Func<OperationInfo, bool> reached)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            if (tracker.GetOperation(operationId) is { } operation && reached(operation)) return operation;
            Assert.True(DateTime.UtcNow < deadline, $"Operation {operationId} never reached the expected state");
            await Task.Delay(10);
        }
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
