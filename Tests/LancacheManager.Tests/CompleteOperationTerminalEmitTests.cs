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
        tracker.CompleteOperation(operationId, success: false, error: "Cancelled by user");

        var info = await emitGate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(info.Success);
        Assert.True(info.Cancelled);
        Assert.Equal("Cancelled by user", info.Error);
    }
}
