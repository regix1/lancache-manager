using System.Diagnostics;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A tracked run disposes its Process the instant its work ends, so a cancel arriving in that
/// instant kills a process whose handle is already gone. The kill threw, and the handler that was
/// supposed to swallow the throw read the id of the same process to report it, which threw again
/// and escaped. The cancellation itself had already succeeded, so the user was shown a failure for
/// work that had stopped exactly as they asked.
///
/// These tests reach that state without spawning anything: a Process for the current process, once
/// disposed, is associated with nothing, which is the same state a run's Process is left in after
/// its finally releases it.
/// </summary>
public sealed class ProcessKillAfterDisposeTests
{
    private static Process DisposedProcess()
    {
        var process = Process.GetCurrentProcess();
        process.Dispose();
        return process;
    }

    [Fact]
    public void DisposedProcess_ReportsItselfWithInvalidOperationException()
    {
        var process = DisposedProcess();

        // The premise every guard rests on. Process never reports this state as
        // ObjectDisposedException, so a handler catching only that catches nothing, and HasExited
        // cannot guard the state it throws in.
        Assert.Throws<InvalidOperationException>(() => { _ = process.Id; });
        Assert.Throws<InvalidOperationException>(() => { _ = process.HasExited; });
    }

    [Fact]
    public void KillProcessTree_AfterDispose_ReportsNotKilled()
    {
        var manager = new ProcessManager(NullLogger<ProcessManager>.Instance);

        Assert.False(manager.KillProcessTree(DisposedProcess(), "cancel"));
    }

    [Fact]
    public async Task WaitAfterKill_AfterDispose_ReturnsAsync()
    {
        var logger = new CapturingLogger<ProcessManager>();
        var manager = new ProcessManager(logger);

        await manager.WaitAfterKillAsync(DisposedProcess(), TimeSpan.FromSeconds(5));

        // Returning before the wait is the whole behavior, and a silent return is the only sign of
        // it: drop the running check and the wait reaches the released handle instead, where the
        // fallback catch swallows the throw and logs it. So the log is what separates the two.
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task GracefulCancel_AfterDispose_ReportsExitedAsync()
    {
        var logger = new CapturingLogger<ProcessManager>();
        var manager = new ProcessManager(logger);

        Assert.True(await manager.GracefulCancelAsync(DisposedProcess(), TimeSpan.FromSeconds(5), "force-kill"));

        // Same shape: without the running check the CANCEL write and the wait both throw into
        // catches that still answer true, so the answer alone cannot tell the two paths apart.
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Untrack_AfterDispose_DoesNotThrow()
    {
        // Dispose() releases every tracked process without untracking it, so a run still unwinding
        // at shutdown untracks a process whose id can no longer be read.
        var manager = new ProcessManager(NullLogger<ProcessManager>.Instance);

        manager.Untrack(DisposedProcess());
    }

    [Fact]
    public void Cancel_WhenTheRunDisposedItsProcess_IsStillReportedAsRequested()
    {
        var manager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(manager, NullLogger<UnifiedOperationTracker>.Instance);
        var cts = new CancellationTokenSource();
        var process = Process.GetCurrentProcess();

        var operationId = tracker.RegisterOperation(OperationType.GameDetection, "cache_game_detect", cts);
        tracker.AssociateProcess(operationId, process);

        // The run's finally, landing between the cancel request and the kill.
        process.Dispose();

        Assert.Equal(OperationCancelResult.Requested, tracker.CancelOperation(operationId));
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public void ForceKill_WhenTheRunDisposedItsProcess_IsStillReportedAsKilled()
    {
        var manager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(manager, NullLogger<UnifiedOperationTracker>.Instance);
        var cts = new CancellationTokenSource();
        var process = Process.GetCurrentProcess();

        var operationId = tracker.RegisterOperation(OperationType.GameDetection, "cache_game_detect", cts);
        tracker.AssociateProcess(operationId, process);
        process.Dispose();

        Assert.True(tracker.ForceKillOperation(operationId));
    }
}
