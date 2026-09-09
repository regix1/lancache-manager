using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Proves the PR2 keystone invariant: an operation's <see cref="OperationInfo.OnTerminalEmit"/> is
/// invoked EXACTLY ONCE from inside <see cref="UnifiedOperationTracker.CompleteOperation"/>, gated by
/// the <c>CompletedFlag</c> CompareExchange, even when two callers race to complete the same op
/// (worker <c>finally</c> vs universal force-kill). This is what lets the terminal SignalR event be
/// centralized without double- or zero-emit.
/// </summary>
public class CompleteOperationTerminalEmitTests
{
    [Theory]
    [InlineData(true, false, false, OperationStatus.Completed)]
    [InlineData(true, false, true, OperationStatus.Skipped)]
    [InlineData(false, true, false, OperationStatus.Cancelled)]
    [InlineData(false, false, false, OperationStatus.Failed)]
    public async Task WinningCompletionFreezesEveryTrackerMutation(bool success, bool cancelled, bool skipped, OperationStatus status)
    {
        var tracker = CreateTracker();
        var counts = new int[1];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var emissions = 0;
        var cleanups = 0;
        var subscribers = 0;
        var publications = 0;
        var parent = Guid.NewGuid();
        var start = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var id = tracker.RegisterOperation(OperationType.GameDetection, "Detection", new CancellationTokenSource(),
            counts, () => Interlocked.Increment(ref cleanups), _ =>
            {
                Interlocked.Increment(ref emissions);
                return Task.CompletedTask;
            }, parentOperationId: parent, startedAt: start);
        tracker.OperationTerminal += _ =>
        {
            Interlocked.Increment(ref subscribers);
            notified.TrySetResult();
        };
        var operation = Assert.IsType<OperationInfo>(tracker.GetOperation(id));
        var winner = Task.Run(() => tracker.CompleteOperation(id, success, "Final reason", cancelled, skipped, current =>
        {
            counts[0] = 7;
            current.PercentComplete = 81;
            Interlocked.Increment(ref publications);
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var process = new System.Diagnostics.Process();
        var contenders = new[]
        {
            Task.Run(() => tracker.CompleteOperation(id, !success, "Late reason", onCompleting: _ => counts[0] = 99)),
            Task.Run(() => tracker.UpdateProgress(id, 99, "Late progress", _ => counts[0] = 99)),
            Task.Run(() => tracker.UpdateMetadata(id, _ => counts[0] = 99)),
            Task.Run(() => tracker.CancelOperation(id)),
            Task.Run(() => tracker.ForceKillOperation(id)),
            Task.Run(() => tracker.AssociateProcess(id, process)),
            Task.Run(() => tracker.DisassociateProcess(id, process))
        };
        release.Set();
        await winner;
        var completedAt = operation.CompletedAt;
        await Task.WhenAll(contenders);
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(status, operation.Status);
        Assert.Equal(success, operation.Success);
        Assert.Equal(cancelled, operation.Cancelled);
        Assert.Equal(success && !skipped ? "Operation completed successfully" : "Final reason", operation.Message);
        Assert.Equal(81, operation.PercentComplete);
        Assert.Equal(7, counts[0]);
        Assert.Equal(start, operation.StartedAt);
        Assert.Equal(parent, operation.ParentOperationId);
        Assert.NotNull(completedAt);
        Assert.Equal(completedAt, operation.CompletedAt);
        Assert.Null(operation.AssociatedProcess);
        Assert.Null(operation.CancellationTokenSource);
        Assert.Equal(1, publications);
        Assert.Equal(1, emissions);
        Assert.Equal(1, cleanups);
        Assert.Equal(1, subscribers);
    }

    [Fact]
    public async Task AcceptedProgressPublishesBeforeCompletionAndLateProgressDoesNothing()
    {
        var tracker = CreateTracker();
        var id = tracker.RegisterOperation(OperationType.GameDetection, "Detection", new CancellationTokenSource());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var published = 0;
        var seenAtTerminal = 0;
        var progress = Task.Run(() => tracker.UpdateProgress(id, 30, "Scanning", _ =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            published = 30;
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var complete = Task.Run(() => tracker.CompleteOperation(id, true, onCompleting: _ => seenAtTerminal = published));
        release.Set();
        await Task.WhenAll(progress, complete);
        tracker.UpdateProgress(id, 90, "Late", _ => published = 90);
        Assert.Equal(30, seenAtTerminal);
        Assert.Equal(30, published);
        Assert.Equal(30, tracker.GetOperation(id)!.PercentComplete);
    }

    [Fact]
    public async Task TerminalEmissionAndCancellationSubscribersRunOutsideTheOperationLock()
    {
        var tracker = CreateTracker();
        var cts = new CancellationTokenSource();
        var id = tracker.RegisterOperation(OperationType.GameDetection, "Detection", cts);
        var operation = tracker.GetOperation(id)!;
        var callback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var emitHeldLock = true;
        var cleanupHeldLock = true;
        using var registration = cts.Token.Register(() =>
        {
            Assert.False(Monitor.IsEntered(operation));
            tracker.CompleteOperation(id, false, cancelled: true);
            callback.TrySetResult();
        });
        operation.OnTerminalEmit = _ =>
        {
            emitHeldLock = Monitor.IsEntered(operation);
            return Task.CompletedTask;
        };
        operation.OnTerminalCleanup = () => cleanupHeldLock = Monitor.IsEntered(operation);
        Assert.Equal(OperationCancelResult.Requested, tracker.CancelOperation(id));
        await callback.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OperationStatus.Cancelled, operation.Status);
        Assert.False(emitHeldLock);
        Assert.False(cleanupHeldLock);
    }

    private static UnifiedOperationTracker CreateTracker()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        return new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
    }

    [Fact]
    public async Task CompleteOperation_InvokesOnTerminalEmit_ExactlyOnce_WhenCalledTwiceAsync()
    {
        var tracker = CreateTracker();
        var emitCount = 0;
        var emitGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        OperationTerminalInfo? captured = null;

        var operationId = tracker.RegisterOperation(
            OperationType.GameDetection,
            "exactly-once emit test",
            new CancellationTokenSource(),
            metadata: null,
            onTerminalCleanup: null,
            onTerminalEmit: info =>
            {
                captured = info;
                if (Interlocked.Increment(ref emitCount) == 1)
                {
                    emitGate.TrySetResult();
                }

                return Task.CompletedTask;
            });

        // Second completion is the racing caller (e.g. universal force-kill after the worker finally).
        tracker.CompleteOperation(operationId, success: true);
        tracker.CompleteOperation(operationId, success: false, error: "duplicate complete should be a no-op");

        // The emit is fire-and-forget; await its signal (the first invocation) before asserting.
        await emitGate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, Volatile.Read(ref emitCount));
        Assert.NotNull(captured);
        Assert.True(captured!.Value.Success);
        Assert.False(captured.Value.Cancelled);
        Assert.Null(captured.Value.Error);
    }

    [Fact]
    public async Task CompleteOperation_PassesCancelledAndError_ToOnTerminalEmitAsync()
    {
        var tracker = CreateTracker();
        var emitGate = new TaskCompletionSource<OperationTerminalInfo>(TaskCreationOptions.RunContinuationsAsynchronously);

        var operationId = tracker.RegisterOperation(
            OperationType.GameDetection,
            "cancelled emit test",
            new CancellationTokenSource(),
            metadata: null,
            onTerminalCleanup: null,
            onTerminalEmit: info =>
            {
                emitGate.TrySetResult(info);
                return Task.CompletedTask;
            });

        // Mark the op cancelled (mirrors what CancelOperation/ForceKillOperation do) so CompleteOperation
        // produces the Cancelled terminal state and forwards Cancelled=true to the emit.
        tracker.CancelOperation(operationId);
        tracker.CompleteOperation(operationId, success: false, error: "Stopped before finishing");

        var info = await emitGate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(info.Success);
        Assert.True(info.Cancelled);
        Assert.Equal("Stopped before finishing", info.Error);
    }
}
