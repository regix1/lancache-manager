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
public sealed class OperationWaitingBlockerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lm-blocker-repair-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

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
            // The successor ended before the force stop arrived; the force stop must leave that ending as it is. An ending
            // written while the force stop runs is covered by AnEndingTheJobWritesWhileTheForceStopRunsIsLeftAsItIsAsync.
            tracker.CompleteOperation(successor, false, "Original failure", onCompleting: operation => operation.PercentComplete = 63);
            var terminal = tracker.GetOperation(successor)!;
            var completedAt = terminal.CompletedAt;
            var response = Assert.IsType<OperationForceKillResponse>(Assert.IsType<OkObjectResult>((await controller.ForceKillAsync(waiter)).Result).Value);
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
        Assert.Equal(OperationStatus.Completed, status.Status);
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
        // A failed final target is kept until its card is closed; closing it reaps it and the chain.
        Assert.True(tracker.CloseRun(last));
        Assert.Null(tracker.GetOperation(first, true));
        var links = Assert.IsType<System.Collections.Concurrent.ConcurrentDictionary<Guid, Guid>>(
            typeof(UnifiedOperationTracker).GetField("_handoffs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tracker));
        Assert.Empty(links);
    }

    [Theory]
    [InlineData(OperationStatus.Completed)]
    [InlineData(OperationStatus.Cancelled)]
    public void ADroppedRunStillAnswersHowItEnded(OperationStatus ended)
    {
        var tracker = CreateTracker();
        var controller = CreateController(tracker);
        var id = tracker.RegisterOperation(OperationType.EvictionScan, "Scan", new CancellationTokenSource());
        tracker.CompleteOperation(id, success: ended == OperationStatus.Completed, cancelled: ended == OperationStatus.Cancelled);
        Reap(tracker, id);
        Assert.Null(tracker.GetOperation(id));
        var status = Assert.IsType<OperationStatusResponse>(Assert.IsType<OkObjectResult>(controller.GetOperationStatus(id).Result).Value);
        Assert.False(status.Active);
        Assert.Equal(ended, status.Status);
    }

    [Fact]
    public void ADroppedRunIsForgottenAtALaterDropFiveMinutesOn()
    {
        var tracker = CreateTracker();
        var a = tracker.RegisterOperation(OperationType.EvictionScan, "Scan", new CancellationTokenSource());
        tracker.CompleteOperation(a, true);
        Reap(tracker, a);
        var reaped = Assert.IsType<System.Collections.Concurrent.ConcurrentDictionary<Guid, (OperationStatus Status, DateTime ReapedAtUtc)>>(
            typeof(UnifiedOperationTracker).GetField("_reapedStatuses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tracker));
        reaped[a] = (OperationStatus.Completed, DateTime.UtcNow.AddMinutes(-6));
        var b = tracker.RegisterOperation(OperationType.EvictionScan, "Scan", new CancellationTokenSource());
        tracker.CompleteOperation(b, true);
        Reap(tracker, b);
        Assert.Null(tracker.GetReapedStatus(a));
        Assert.Equal(OperationStatus.Completed, tracker.GetReapedStatus(b));
    }

    [Fact]
    public void AKeptEndingOlderThanFiveMinutesIsForgottenOnRead()
    {
        var tracker = CreateTracker();
        var id = tracker.RegisterOperation(OperationType.EvictionScan, "Scan", new CancellationTokenSource());
        tracker.CompleteOperation(id, success: false, cancelled: true);
        Reap(tracker, id);
        Assert.Equal(OperationStatus.Cancelled, tracker.GetReapedStatus(id));
        var reaped = Assert.IsType<System.Collections.Concurrent.ConcurrentDictionary<Guid, (OperationStatus Status, DateTime ReapedAtUtc)>>(
            typeof(UnifiedOperationTracker).GetField("_reapedStatuses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tracker));
        reaped[id] = (OperationStatus.Cancelled, DateTime.UtcNow.AddMinutes(-6));
        Assert.Null(tracker.GetReapedStatus(id));
    }

    [Fact]
    public void AQueuedItemAnswersItsSuccessorsCanceledEndingAfterBothDrop()
    {
        var tracker = CreateTracker();
        var controller = CreateController(tracker);
        var queued = tracker.RegisterOperation(OperationType.EvictionScan, "Scan", new CancellationTokenSource(), initialStatus: OperationStatus.Waiting);
        var successor = tracker.RegisterOperation(OperationType.EvictionScan, "Scan", new CancellationTokenSource());
        tracker.RecordHandoff(queued, successor);
        tracker.CompleteOperation(queued, true);
        Reap(tracker, queued);
        tracker.CompleteOperation(successor, success: false, cancelled: true);
        Reap(tracker, successor);
        var status = Assert.IsType<OperationStatusResponse>(Assert.IsType<OkObjectResult>(controller.GetOperationStatus(queued).Result).Value);
        Assert.Equal(OperationStatus.Cancelled, status.Status);
        Assert.Null(status.NextOperationId);
    }

    internal static void Reap(UnifiedOperationTracker tracker, Guid id)
    {
        typeof(UnifiedOperationTracker).GetMethod("ReapOperation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tracker, [id]);
    }

    private static OperationsController CreateController(UnifiedOperationTracker tracker) => new(
        tracker,
        new OperationCancellationService(tracker, new ProcessManager(NullLogger<ProcessManager>.Instance),
            OperationConflictTestServices.Owner, NullLogger<OperationCancellationService>.Instance),
        OperationConflictTestServices.Owner)
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
            OperationConflictTestServices.Create(ordered, NullLogger<OperationConflictChecker>.Instance),
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
        var checker = OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance);
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
            OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance),
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
        var conflictChecker = OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance);
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
        var conflictChecker = OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance);
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
        var conflictChecker = OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance);
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
        var conflictChecker = OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance);
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
                OperationConflictTestServices.Owner,
                NullLogger<OperationCancellationService>.Instance),
            OperationConflictTestServices.Owner);

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
        var conflictChecker = OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance);
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

    [Fact]
    public async Task BlockerClearedFiresOnEndRepairAndNotifyAsync()
    {
        var tracker = CreateTracker();
        using var raised = new SemaphoreSlim(0);
        tracker.BlockerCleared += () => raised.Release();

        tracker.EndRepair(Guid.NewGuid(), null);
        Assert.True(await raised.WaitAsync(TimeSpan.FromSeconds(5)));
        tracker.NotifyBlockerCleared();
        Assert.True(await raised.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RepairEndPromotesParkedWaiterAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(
            _root,
            apply: async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            });
        var queue = new OperationQueueService(
            harness.Tracker,
            new OperationConflictChecker(harness.Tracker, harness.Owner, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationQueueService>.Instance);
        var repair = OperationConflictTestServices.NewCacheClearRepair(Guid.NewGuid());
        await harness.Owner.PrepareRepairAsync(repair, CancellationToken.None);
        await harness.Owner.StartWorkAsync(repair.Id, "alpha", CancellationToken.None);
        await harness.Owner.FinishRepairAsync(repair.Id, success: false, cancelled: false, error: "interrupted");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var terminals = 0;
        harness.Tracker.OperationTerminal += _ => Interlocked.Increment(ref terminals);
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = await queue.EnqueueAsync(OperationType.GameRemoval, ConflictScope.NamedGame("blizzard", "Diablo IV"),
            "Game Removal", () =>
            {
                started.TrySetResult(Volatile.Read(ref terminals));
                return Task.FromResult<Guid?>(Guid.NewGuid());
            }, CancellationToken.None);
        Assert.True(queued.Queued);

        release.TrySetResult();

        // The waiter starts with no operation ending before it: the repair's end alone wakes the queue.
        Assert.Equal(0, await started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForceKillRecordsTheRepairOutcomeAndEndsTheJobAsync(bool workStarted)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(
            _root,
            apply: async (_, cancellationToken) => await release.Task.WaitAsync(cancellationToken));
        var cancellation = new OperationCancellationService(harness.Tracker,
            new ProcessManager(NullLogger<ProcessManager>.Instance), harness.Owner,
            NullLogger<OperationCancellationService>.Instance);
        var emitted = new TaskCompletionSource<OperationRepair>(TaskCreationOptions.RunContinuationsAsynchronously);
        var emits = 0;
        var id = harness.Tracker.RegisterOperation(OperationType.CacheClearing, "Cache Clear", new CancellationTokenSource(),
            onTerminalEmit: _ =>
            {
                Interlocked.Increment(ref emits);
                emitted.TrySetResult(Assert.Single(harness.StateService.LoadOperationRepairs()));
                return Task.CompletedTask;
            },
            ownerCompletes: true);
        await harness.Owner.PrepareRepairAsync(OperationConflictTestServices.NewCacheClearRepair(id), CancellationToken.None);
        if (workStarted)
        {
            await harness.Owner.StartWorkAsync(id, "alpha", CancellationToken.None);
        }

        Assert.True(await cancellation.ForceKillAsync(id));
        Assert.True(await cancellation.ForceKillAsync(id));

        // The outcome was stored before the job's terminal was sent.
        var atTerminal = await emitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(workStarted ? OperationRepairPhase.Repairing : OperationRepairPhase.Completed, atTerminal.Phase);
        Assert.Equal(OperationStatus.Cancelled, atTerminal.Outcome);
        var operation = harness.Tracker.GetOperation(id)!;
        Assert.Equal(1, operation.CompletedFlag);
        Assert.Equal(OperationStatus.Cancelled, operation.Status);
        Assert.Equal(workStarted, operation.Repairing);
        Assert.Equal(1, Volatile.Read(ref emits));
    }

    [Fact]
    public async Task ForceKillEndsTheJobWhenTheOutcomeSaveFailsAsync()
    {
        var state = OperationRepairTests.CreateFailingStateService(_root);
        var applies = 0;
        await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(
            _root,
            stateService: state,
            apply: (_, _) =>
            {
                Interlocked.Increment(ref applies);
                return Task.CompletedTask;
            },
            waitUntil: (_, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken));
        var cancellation = new OperationCancellationService(harness.Tracker,
            new ProcessManager(NullLogger<ProcessManager>.Instance), harness.Owner,
            NullLogger<OperationCancellationService>.Instance);
        var id = harness.Tracker.RegisterOperation(OperationType.CacheClearing, "Cache Clear", new CancellationTokenSource(),
            ownerCompletes: true);
        await harness.Owner.PrepareRepairAsync(OperationConflictTestServices.NewCacheClearRepair(id), CancellationToken.None);
        await harness.Owner.StartWorkAsync(id, "alpha", CancellationToken.None);
        state.FailNextRepairWrite = true;

        Assert.True(await cancellation.ForceKillAsync(id));

        var operation = harness.Tracker.GetOperation(id)!;
        Assert.Equal(1, operation.CompletedFlag);
        Assert.Equal(OperationStatus.Cancelled, operation.Status);
        Assert.True(operation.Repairing);
        Assert.Equal(id, harness.Owner.GetBlockingRepair()?.Id);
        Assert.Equal(0, Volatile.Read(ref applies));
    }

    [Fact]
    public async Task AJobThatSavesAfterAForceStopWhoseSaveFailedKeepsTheCancelAsync()
    {
        var state = OperationRepairTests.CreateFailingStateService(_root);
        await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(
            _root,
            stateService: state,
            apply: (_, _) => Task.CompletedTask,
            waitUntil: (_, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken));
        var cancellation = new OperationCancellationService(harness.Tracker,
            new ProcessManager(NullLogger<ProcessManager>.Instance), harness.Owner,
            NullLogger<OperationCancellationService>.Instance);
        var id = harness.Tracker.RegisterOperation(OperationType.CacheClearing, "Cache Clear", new CancellationTokenSource(),
            ownerCompletes: true);
        await harness.Owner.PrepareRepairAsync(OperationConflictTestServices.NewCacheClearRepair(id), CancellationToken.None);
        await harness.Owner.StartWorkAsync(id, "alpha", CancellationToken.None);
        state.FailNextRepairWrite = true;

        Assert.True(await cancellation.ForceKillAsync(id));
        // The job's own save as a clean cache clear makes it, after the force stop already ended the run canceled.
        await harness.Owner.FinishRepairAsync(id, success: true, cancelled: false, error: null);

        Assert.Equal(OperationStatus.Cancelled, state.LoadOperationRepairs().Single(repair => repair.Id == id).Outcome);
    }

    [Fact]
    public async Task ForceKillOfAnEndedOperationChangesNothingAsync()
    {
        await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(_root);
        var cancellation = new OperationCancellationService(harness.Tracker,
            new ProcessManager(NullLogger<ProcessManager>.Instance), harness.Owner,
            NullLogger<OperationCancellationService>.Instance);
        var id = harness.Tracker.RegisterOperation(OperationType.CacheClearing, "Cache Clear", new CancellationTokenSource(),
            ownerCompletes: true);
        await harness.Owner.PrepareRepairAsync(OperationConflictTestServices.NewCacheClearRepair(id), CancellationToken.None);
        await harness.Owner.StartWorkAsync(id, "alpha", CancellationToken.None);
        harness.Tracker.CompleteOperation(id, success: true);
        var completedAt = harness.Tracker.GetOperation(id)!.CompletedAt;

        Assert.True(await cancellation.ForceKillAsync(id));

        var operation = harness.Tracker.GetOperation(id)!;
        Assert.Equal(OperationStatus.Completed, operation.Status);
        Assert.Equal(completedAt, operation.CompletedAt);
        Assert.False(operation.Repairing);
        Assert.Equal(OperationRepairPhase.Running, Assert.Single(harness.Owner.GetPendingRepairs()).Phase);
    }

    [Fact]
    public async Task AnEndingTheJobWritesWhileTheForceStopRunsIsLeftAsItIsAsync()
    {
        await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(_root);
        var cancellation = new OperationCancellationService(harness.Tracker,
            new ProcessManager(NullLogger<ProcessManager>.Instance), harness.Owner,
            NullLogger<OperationCancellationService>.Instance);
        var source = new CancellationTokenSource();
        var id = harness.Tracker.RegisterOperation(OperationType.CacheClearing, "Cache Clear", source,
            ownerCompletes: true);
        await harness.Owner.PrepareRepairAsync(OperationConflictTestServices.NewCacheClearRepair(id), CancellationToken.None);
        await harness.Owner.StartWorkAsync(id, "alpha", CancellationToken.None);
        DateTime? completedAt = null;
        // The job's own success lands after the force stop's cancel and before its terminal check.
        source.Token.Register(() =>
        {
            harness.Tracker.CompleteOperation(id, success: true);
            completedAt = harness.Tracker.GetOperation(id)!.CompletedAt;
        });

        Assert.True(await cancellation.ForceKillAsync(id));

        var operation = harness.Tracker.GetOperation(id)!;
        Assert.Equal(OperationStatus.Completed, operation.Status);
        Assert.NotNull(completedAt);
        Assert.Equal(completedAt, operation.CompletedAt);
        Assert.Equal(OperationRepairPhase.Running, Assert.Single(harness.Owner.GetPendingRepairs()).Phase);
    }

    [Fact]
    public async Task AForceStopAfterTheJobSavedItsOutcomeEndsWithThatOutcomeAsync()
    {
        await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(_root);
        var cancellation = new OperationCancellationService(harness.Tracker,
            new ProcessManager(NullLogger<ProcessManager>.Instance), harness.Owner,
            NullLogger<OperationCancellationService>.Instance);
        var ended = new TaskCompletionSource<OperationTerminalInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = harness.Tracker.RegisterOperation(OperationType.CacheClearing, "Cache Clear", new CancellationTokenSource(),
            onTerminalEmit: terminal =>
            {
                ended.TrySetResult(terminal);
                return Task.CompletedTask;
            },
            ownerCompletes: true);
        await harness.Owner.PrepareRepairAsync(OperationConflictTestServices.NewCacheClearRepair(id), CancellationToken.None);
        await harness.Owner.StartWorkAsync(id, "alpha", CancellationToken.None);
        // One datasource cleared and another failed, as the cache clear's failure catch saves it.
        await harness.Owner.SetRunWarningAsync(id, new RunWarning(
            "common.notifications.warnings.datasourcesNotCleared",
            new Dictionary<string, object?> { ["datasources"] = "beta" }));
        await harness.Owner.FinishRepairAsync(id, success: false, cancelled: false, error: "Datasource beta failed",
            update: repair => repair.RunCompleted = true);

        Assert.True(await cancellation.ForceKillAsync(id));

        // The job's owner still completes the run, as the cache clear's failure catch does for a partly cleared clear.
        Assert.Equal(0, harness.Tracker.GetOperation(id)!.CompletedFlag);
        harness.Tracker.CompleteOperation(id, success: true);

        var operation = harness.Tracker.GetOperation(id)!;
        Assert.Equal(OperationStatus.Completed, operation.Status);
        Assert.Contains(operation.Warnings, warning =>
            warning.StageKey == "common.notifications.warnings.datasourcesNotCleared");
        var emitted = await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(emitted.Success);
        Assert.False(emitted.Cancelled);
    }

    [Fact]
    public async Task AForceStopAfterALogPassSavedItsOutcomeLeavesTheEndingToTheOwnerAsync()
    {
        await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(
            _root,
            registrations: services => services.AddSingleton<ISignalRNotificationService>(
                new ScheduledRunReporterTests.CapturingNotificationService()));
        var cancellation = new OperationCancellationService(harness.Tracker,
            new ProcessManager(NullLogger<ProcessManager>.Instance), harness.Owner,
            NullLogger<OperationCancellationService>.Instance);
        var ended = new TaskCompletionSource<OperationTerminalInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = harness.Tracker.RegisterOperation(OperationType.LogProcessing, "Log Processing", new CancellationTokenSource(),
            onTerminalEmit: terminal =>
            {
                ended.TrySetResult(terminal);
                return Task.CompletedTask;
            },
            ownerCompletes: true);
        // The record a log pass prepares: a downloads refresh is all it owes, so its own save drops the record.
        await harness.Owner.PrepareRepairAsync(new OperationRepair
        {
            Id = id,
            Type = OperationType.LogProcessing,
            Name = "Log processing",
            StartedAt = DateTime.UtcNow,
            Sources =
            [
                new OperationRepairSource
                {
                    Datasource = "alpha",
                    LogRoot = "logs/alpha",
                    CacheRoot = "cache/alpha",
                    KeyScheme = "steam",
                    RefreshDownloads = true
                }
            ],
            LogProcessing = new LogProcessingRepair()
        }, CancellationToken.None);
        await harness.Owner.StartWorkAsync(id, "alpha", CancellationToken.None);
        await harness.Owner.FinishRepairAsync(id, success: true, cancelled: false, error: null);
        Assert.False(harness.Owner.OwnsRepair(id));

        Assert.True(await cancellation.ForceKillAsync(id));

        // The pass's owner still completes the run with its own counts.
        Assert.Equal(0, harness.Tracker.GetOperation(id)!.CompletedFlag);
        harness.Tracker.CompleteOperation(id, success: true);

        Assert.Equal(OperationStatus.Completed, harness.Tracker.GetOperation(id)!.Status);
        var emitted = await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(emitted.Success);
        Assert.False(emitted.Cancelled);
    }

    [Fact]
    public async Task AForceStopWhileTheJobsRunGoesOnAfterItsSaveEndsTheRunAtOnceAsync()
    {
        await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(_root);
        var cancellation = new OperationCancellationService(harness.Tracker,
            new ProcessManager(NullLogger<ProcessManager>.Instance), harness.Owner,
            NullLogger<OperationCancellationService>.Instance);
        var id = harness.Tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            ownerCompletes: true);
        await harness.Owner.PrepareRepairAsync(NewScanRepair(id), CancellationToken.None);
        await harness.Owner.StartWorkAsync(id, "alpha", CancellationToken.None);
        // The scan's early save, before its remove step and checkpoint tail.
        await harness.Owner.FinishRepairAsync(id, success: true, cancelled: false, error: null,
            update: repair => repair.RunContinues = true);

        Assert.True(await cancellation.ForceKillAsync(id).WaitAsync(TimeSpan.FromSeconds(10)));

        var operation = harness.Tracker.GetOperation(id)!;
        Assert.Equal(1, operation.CompletedFlag);
        Assert.Equal(OperationStatus.Cancelled, operation.Status);
        var stored = harness.StateService.LoadOperationRepairs().Single(repair => repair.Id == id);
        Assert.Equal(OperationStatus.Completed, stored.Outcome);
        Assert.True(stored.RunCancelled);
        // The force stop ended the run, so the scan's last save finds no run going on.
        Assert.False(stored.RunContinues);

        // The scan's last save after the stop records how the run ended.
        await harness.Owner.FinishRepairAsync(id, success: false, cancelled: true, error: null);

        stored = harness.StateService.LoadOperationRepairs().Single(repair => repair.Id == id);
        Assert.False(stored.RunContinues);
        Assert.True(stored.RunCancelled);
    }

    [Fact]
    public async Task XWhileTheJobsRunGoesOnAfterItsSaveIsSavedAtOnceAsync()
    {
        await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(_root);
        var cancellation = new OperationCancellationService(harness.Tracker,
            new ProcessManager(NullLogger<ProcessManager>.Instance), harness.Owner,
            NullLogger<OperationCancellationService>.Instance);
        var controller = new OperationsController(harness.Tracker, cancellation, harness.Owner)
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
        var id = harness.Tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            ownerCompletes: true);
        await harness.Owner.PrepareRepairAsync(NewScanRepair(id), CancellationToken.None);
        await harness.Owner.StartWorkAsync(id, "alpha", CancellationToken.None);
        await harness.Owner.FinishRepairAsync(id, success: true, cancelled: false, error: null,
            update: repair => repair.RunContinues = true);

        await controller.CancelOperation(id);

        // X leaves the ending to the scan, but the stop is saved already for a restart before the scan's last save.
        Assert.Equal(0, harness.Tracker.GetOperation(id)!.CompletedFlag);
        var stored = harness.StateService.LoadOperationRepairs().Single(repair => repair.Id == id);
        Assert.True(stored.RunCancelled);
        Assert.True(stored.RunContinues);

        // The scan passed its last token check, so its last save reports a clean run.
        await harness.Owner.FinishRepairAsync(id, success: true, cancelled: false, error: null);

        Assert.Equal(OperationStatus.Cancelled, harness.Tracker.GetOperation(id)!.Status);
        stored = harness.StateService.LoadOperationRepairs().Single(repair => repair.Id == id);
        Assert.False(stored.RunContinues);
        Assert.True(stored.RunCancelled);
    }

    [Fact]
    public async Task XThenARealFailureInTheScanTailEndsRedAsync()
    {
        await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(_root);
        var cancellation = new OperationCancellationService(harness.Tracker,
            new ProcessManager(NullLogger<ProcessManager>.Instance), harness.Owner,
            NullLogger<OperationCancellationService>.Instance);
        var controller = new OperationsController(harness.Tracker, cancellation, harness.Owner)
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
        var id = harness.Tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            ownerCompletes: true);
        await harness.Owner.PrepareRepairAsync(NewScanRepair(id), CancellationToken.None);
        await harness.Owner.StartWorkAsync(id, "alpha", CancellationToken.None);
        await harness.Owner.FinishRepairAsync(id, success: true, cancelled: false, error: null,
            update: repair => repair.RunContinues = true);

        await controller.CancelOperation(id);

        // The remove step unwound on the canceled token, then the checkpoint tail failed for a real reason.
        await harness.Owner.FinishRepairAsync(id, success: false, cancelled: false, error: "Database unavailable");
        harness.Tracker.CompleteOperation(id, success: false, error: "Database unavailable", cancelled: false);

        Assert.Equal(OperationStatus.Failed, harness.Tracker.GetOperation(id)!.Status);
        var stored = harness.StateService.LoadOperationRepairs().Single(repair => repair.Id == id);
        Assert.False(stored.RunCancelled);
        Assert.False(stored.RunContinues);
        Assert.Equal("Database unavailable", stored.RunError);
    }

    [Fact]
    public async Task AForceStopThenARealFailureInTheScanTailStaysCanceledAsync()
    {
        await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(_root);
        var cancellation = new OperationCancellationService(harness.Tracker,
            new ProcessManager(NullLogger<ProcessManager>.Instance), harness.Owner,
            NullLogger<OperationCancellationService>.Instance);
        var id = harness.Tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            ownerCompletes: true);
        await harness.Owner.PrepareRepairAsync(NewScanRepair(id), CancellationToken.None);
        await harness.Owner.StartWorkAsync(id, "alpha", CancellationToken.None);
        await harness.Owner.FinishRepairAsync(id, success: true, cancelled: false, error: null,
            update: repair => repair.RunContinues = true);

        Assert.True(await cancellation.ForceKillAsync(id).WaitAsync(TimeSpan.FromSeconds(10)));

        await harness.Owner.FinishRepairAsync(id, success: false, cancelled: false, error: "Database unavailable");
        harness.Tracker.CompleteOperation(id, success: false, error: "Database unavailable", cancelled: false);

        Assert.Equal(OperationStatus.Cancelled, harness.Tracker.GetOperation(id)!.Status);
        var stored = harness.StateService.LoadOperationRepairs().Single(repair => repair.Id == id);
        Assert.True(stored.RunCancelled);
    }

    [Fact]
    public async Task AFailingLastSaveBetweenAForceStopsSaveAndItsEndingStaysCanceledAsync()
    {
        var state = OperationRepairTests.CreateFailingStateService(_root);
        await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(_root, stateService: state);
        var id = harness.Tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            ownerCompletes: true);
        await harness.Owner.PrepareRepairAsync(NewScanRepair(id), CancellationToken.None);
        await harness.Owner.StartWorkAsync(id, "alpha", CancellationToken.None);
        await harness.Owner.FinishRepairAsync(id, success: true, cancelled: false, error: null,
            update: repair => repair.RunContinues = true);
        Task? lastSave = null;
        state.OnRepairWrite = contents =>
        {
            var repairs = System.Text.Json.JsonSerializer.Deserialize<List<OperationRepair>>(contents) ?? [];
            if (lastSave is null && repairs.Any(repair => repair.Id == id && repair.RunCancelled))
            {
                // The force stop's save holds the admission gate here; the scan's tail failed for a real reason, so
                // its last save waits for the gate.
                lastSave = harness.Owner.FinishRepairAsync(id, success: false, cancelled: false, error: "Database unavailable");
            }
        };

        // The force stop's steps, with the scan's last save landing before the force stop completes the run.
        harness.Tracker.ForceKillOperation(id, followHandoff: true);
        Assert.False(await harness.Owner.RecordForceStopAsync(id));
        Assert.NotNull(lastSave);
        await lastSave.WaitAsync(TimeSpan.FromSeconds(10));
        harness.Tracker.CompleteOperation(id, success: false, error: "Force killed by user", cancelled: true);
        harness.Tracker.CompleteOperation(id, success: false, error: "Database unavailable", cancelled: false);

        Assert.Equal(OperationStatus.Cancelled, harness.Tracker.GetOperation(id)!.Status);
        var stored = harness.StateService.LoadOperationRepairs().Single(repair => repair.Id == id);
        Assert.True(stored.RunCancelled);
        Assert.False(stored.RunContinues);
        Assert.Null(stored.RunError);
    }

    [Fact]
    public async Task AFailingLastSaveBeforeAForceStopEndsRedLiveAndSavedAsync()
    {
        var state = OperationRepairTests.CreateFailingStateService(_root);
        await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(_root, stateService: state);
        var cancellation = new OperationCancellationService(harness.Tracker,
            new ProcessManager(NullLogger<ProcessManager>.Instance), harness.Owner,
            NullLogger<OperationCancellationService>.Instance);
        var id = harness.Tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            ownerCompletes: true);
        await harness.Owner.PrepareRepairAsync(NewScanRepair(id), CancellationToken.None);
        await harness.Owner.StartWorkAsync(id, "alpha", CancellationToken.None);
        await harness.Owner.FinishRepairAsync(id, success: true, cancelled: false, error: null,
            update: repair => repair.RunContinues = true);
        Task<bool>? forceStop = null;
        state.OnRepairWrite = contents =>
        {
            var repairs = System.Text.Json.JsonSerializer.Deserialize<List<OperationRepair>>(contents) ?? [];
            if (forceStop is null && repairs.Any(repair => repair.Id == id && repair.RunError == "Database unavailable"))
            {
                // The scan's failing last save holds the admission gate here, so the force stop waits for it.
                forceStop = cancellation.ForceKillAsync(id);
            }
        };

        await harness.Owner.FinishRepairAsync(id, success: false, cancelled: false, error: "Database unavailable");
        harness.Tracker.CompleteOperation(id, success: false, error: "Database unavailable", cancelled: false);
        Assert.NotNull(forceStop);
        Assert.True(await forceStop.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(OperationStatus.Failed, harness.Tracker.GetOperation(id)!.Status);
        var stored = harness.StateService.LoadOperationRepairs().Single(repair => repair.Id == id);
        Assert.False(stored.RunCancelled);
        Assert.Equal("Database unavailable", stored.RunError);
    }

    [Fact]
    public async Task XWhoseSaveFailedStillEndsTheScanCanceledAsync()
    {
        var state = OperationRepairTests.CreateFailingStateService(_root);
        await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(_root, stateService: state);
        var id = harness.Tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            ownerCompletes: true);
        await harness.Owner.PrepareRepairAsync(NewScanRepair(id), CancellationToken.None);
        await harness.Owner.StartWorkAsync(id, "alpha", CancellationToken.None);
        await harness.Owner.FinishRepairAsync(id, success: true, cancelled: false, error: null,
            update: repair => repair.RunContinues = true);

        state.FailNextRepairWrite = true;
        harness.Tracker.CancelOperation(id);
        await harness.Owner.RecordCancelAsync(id);

        // The scan passed its last token check, so its last save reports a clean run.
        await harness.Owner.FinishRepairAsync(id, success: true, cancelled: false, error: null);

        Assert.Equal(OperationStatus.Cancelled, harness.Tracker.GetOperation(id)!.Status);
        var stored = harness.StateService.LoadOperationRepairs().Single(repair => repair.Id == id);
        Assert.True(stored.RunCancelled);
    }

    [Fact]
    public async Task XDuringTheScansEarlySaveIsSavedOnceThatSaveIsDoneAsync()
    {
        var state = OperationRepairTests.CreateFailingStateService(_root);
        await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(_root, stateService: state);
        var id = harness.Tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            ownerCompletes: true);
        await harness.Owner.PrepareRepairAsync(NewScanRepair(id), CancellationToken.None);
        await harness.Owner.StartWorkAsync(id, "alpha", CancellationToken.None);
        Task? pending = null;
        state.OnRepairWrite = contents =>
        {
            var repairs = System.Text.Json.JsonSerializer.Deserialize<List<OperationRepair>>(contents) ?? [];
            if (pending is null && repairs.Any(repair => repair.Id == id && repair.RunContinues))
            {
                // The early save holds the admission gate here, so the cancel waits for it.
                harness.Tracker.CancelOperation(id);
                pending = harness.Owner.RecordCancelAsync(id);
            }
        };

        await harness.Owner.FinishRepairAsync(id, success: true, cancelled: false, error: null,
            update: repair => repair.RunContinues = true);

        Assert.NotNull(pending);
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        var stored = harness.StateService.LoadOperationRepairs().Single(repair => repair.Id == id);
        Assert.True(stored.RunCancelled);
    }

    [Fact]
    public async Task XBeforeTheScansEarlySaveIsKeptByThatSaveAsync()
    {
        await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(_root);
        var id = harness.Tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            ownerCompletes: true);
        await harness.Owner.PrepareRepairAsync(NewScanRepair(id), CancellationToken.None);
        await harness.Owner.StartWorkAsync(id, "alpha", CancellationToken.None);

        // The record shows no run going on yet, so this X finds nothing to save.
        harness.Tracker.CancelOperation(id);
        await harness.Owner.RecordCancelAsync(id);

        await harness.Owner.FinishRepairAsync(id, success: true, cancelled: false, error: null,
            update: repair => repair.RunContinues = true);

        var stored = harness.StateService.LoadOperationRepairs().Single(repair => repair.Id == id);
        Assert.True(stored.RunContinues);
        Assert.True(stored.RunCancelled);
        Assert.Equal(OperationStatus.Cancelled, harness.Tracker.GetOperation(id)!.Status);
    }

    [Fact]
    public async Task AFailedLastSaveIsWrittenWithTheRecordsNextSaveAsync()
    {
        var state = OperationRepairTests.CreateFailingStateService(_root);
        var repairBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(
            _root,
            stateService: state,
            apply: async (_, cancellationToken) => await repairBlocked.Task.WaitAsync(cancellationToken));
        try
        {
            var id = harness.Tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
                ownerCompletes: true);
            await harness.Owner.PrepareRepairAsync(NewScanRepair(id), CancellationToken.None);
            await harness.Owner.StartWorkAsync(id, "alpha", CancellationToken.None);
            await harness.Owner.FinishRepairAsync(id, success: true, cancelled: false, error: null,
                update: repair => repair.RunContinues = true);

            state.FailNextRepairWrite = true;
            await harness.Owner.FinishRepairAsync(id, success: true, cancelled: false, error: null);
            await harness.Owner.SetRunWarningAsync(id, new RunWarning(
                "common.notifications.warnings.datasourcesFailed",
                new Dictionary<string, object?> { ["datasources"] = "alpha" }));

            Assert.False(harness.Owner.GetPendingRepairs().Single(repair => repair.Id == id).RunContinues);
            var stored = harness.StateService.LoadOperationRepairs().Single(repair => repair.Id == id);
            Assert.False(stored.RunContinues);
        }
        finally
        {
            repairBlocked.TrySetResult();
        }
    }

    private static OperationRepair NewScanRepair(Guid id) => new()
    {
        Id = id,
        Type = OperationType.EvictionScan,
        Name = "Eviction scan",
        StartedAt = DateTime.UtcNow,
        Sources =
        [
            new OperationRepairSource
            {
                Datasource = "alpha",
                LogRoot = "logs/alpha",
                CacheRoot = "cache/alpha",
                KeyScheme = "steam",
                RefreshDownloads = true
            }
        ],
        EvictionScan = new EvictionScanRepair()
    };

    [Fact]
    public async Task AJobThatEndsAfterAForceStopSavedItsCancelEndsCanceledAsync()
    {
        await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(_root);
        var id = harness.Tracker.RegisterOperation(OperationType.CacheClearing, "Cache Clear", new CancellationTokenSource(),
            ownerCompletes: true);
        await harness.Owner.PrepareRepairAsync(OperationConflictTestServices.NewCacheClearRepair(id), CancellationToken.None);
        await harness.Owner.StartWorkAsync(id, "alpha", CancellationToken.None);
        // The force stop's save, which a thread switch can leave before the job's own final save.
        await harness.Owner.RecordForceStopAsync(id);

        await harness.Owner.FinishRepairAsync(id, success: true, cancelled: false, error: null,
            update: repair => repair.RunCompleted = true);
        harness.Tracker.CompleteOperation(id, success: true);

        Assert.Equal(OperationStatus.Cancelled, harness.Tracker.GetOperation(id)!.Status);
    }

    [Fact]
    public void ALaterScheduledFailureLeavesAFailedOutRepairCard()
    {
        var tracker = CreateTracker();
        var schedules = CreateRegistry(new IdleScanProbeService(), tracker);
        // Detached and called directly, so the endings are handled in a known order.
        var handle = (Action<OperationInfo>)Delegate.CreateDelegate(typeof(Action<OperationInfo>), schedules,
            typeof(ServiceScheduleRegistry).GetMethod("OnTrackedOperationTerminal", BindingFlags.Instance | BindingFlags.NonPublic)!);
        tracker.OperationTerminal -= handle;
        OperationInfo Fail(bool owesRepair)
        {
            var id = tracker.RegisterOperation(OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());
            if (owesRepair) tracker.BeginRepair(id);
            tracker.CompleteOperation(id, success: false, error: "Disk read failed");
            var operation = tracker.GetOperation(id)!;
            handle(operation);
            return operation;
        }

        var repairFailed = Fail(owesRepair: true);
        tracker.EndRepair(repairFailed.Id, "Repair failed: disk gone");
        var plain = Fail(owesRepair: false);
        var latest = Fail(owesRepair: false);

        var kept = Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == repairFailed.Id);
        Assert.False(kept.Closed);
        Assert.Equal("Repair failed: disk gone", kept.RepairError);
        Assert.Null(tracker.GetOperation(plain.Id));
        Assert.Equal(3, tracker.GetOperation(latest.Id)!.ConsecutiveFailures);
    }

    [Fact]
    public async Task AForceStopWithNoCancelFirstCancelsTheTokenBeforeTheChildExitsAsync()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
        var cancellation = new OperationCancellationService(tracker, processManager,
            OperationConflictTestServices.Owner, NullLogger<OperationCancellationService>.Instance);
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        var operationId = tracker.RegisterOperation(OperationType.LogProcessing, "Log processing", cts, ownerCompletes: true);

        // A child that ignores the CANCEL line on stdin, like a purge that checks for a cancel only after
        // its whole pass: it dies only to the hard kill.
        var start = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("ping", "-n 60 127.0.0.1")
            : new System.Diagnostics.ProcessStartInfo("sleep", "60");
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = new System.Diagnostics.Process { StartInfo = start, EnableRaisingEvents = true };
        var tokenCanceledAtExit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => tokenCanceledAtExit.TrySetResult(token.IsCancellationRequested);
        Assert.True(process.Start());
        try
        {
            tracker.AssociateProcess(operationId, process);

            Assert.True(await cancellation.ForceKillAsync(operationId));

            Assert.True(await tokenCanceledAtExit.Task.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(OperationStatus.Cancelled, tracker.GetOperation(operationId)!.Status);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
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
        return new ServiceScheduleRegistry(
            new IHostedService[] { service }, stateService, notifications,
            ScheduleExecutionTestService.Create(), tracker);
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
