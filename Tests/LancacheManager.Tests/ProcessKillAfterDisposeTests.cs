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
/// The dispose tests reach that state without spawning anything: a Process for the current process,
/// once disposed, is associated with nothing, which is the same state a run's Process is left in
/// after its finally releases it.
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

    [Fact]
    public async Task CancelledRun_WaitsForTheKilledChildBeforeReleasingItAsync()
    {
        var manager = new KillWaitProcessManager();
        var helper = new RustProcessHelper(
            NullLogger<RustProcessHelper>.Instance,
            manager,
            pathResolver: null!,
            operationTracker: null!);
        using var cts = new CancellationTokenSource();
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-n 30 127.0.0.1")
            : new ProcessStartInfo("sleep", "30");
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;

        var run = helper.RunTrackedProcessAsync(
            start,
            operationId: null,
            cts.Token,
            async process =>
            {
                await process.WaitForExitAsync(cts.Token);
                return process.ExitCode;
            });
        await cts.CancelAsync();

        // Without the post-kill wait the run releases the child and completes first.
        Assert.Same(
            manager.WaitStarted.Task,
            await Task.WhenAny(manager.WaitStarted.Task, run).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(run.IsCompleted);
        Assert.Equal(TimeSpan.FromSeconds(5), manager.WaitTimeout);

        manager.Release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void RemoveLogsCommand_IsTheRemoveShapeWithoutOperationId()
    {
        var withPositions = RustProcessHelper.BuildCorruptionManagerArguments(
            "remove-logs",
            "C:/logs",
            "C:/cache",
            "steam",
            "C:/ops/evidence.json",
            "C:/ops/progress.json",
            "bare_metal",
            "C:/ops/positions.json");
        var withoutPositions = RustProcessHelper.BuildCorruptionManagerArguments(
            "remove-logs",
            "C:/logs",
            "C:/cache",
            "steam",
            "C:/ops/evidence.json",
            "C:/ops/progress.json",
            "monolithic");

        Assert.Equal(
            "remove-logs \"C:/logs\" \"C:/cache\" \"steam\" \"C:/ops/progress.json\" --evidence-file \"C:/ops/evidence.json\" --progress --key-scheme bare_metal --stem-positions \"C:/ops/positions.json\"",
            withPositions);
        Assert.Equal(
            "remove-logs \"C:/logs\" \"C:/cache\" \"steam\" \"C:/ops/progress.json\" --evidence-file \"C:/ops/evidence.json\" --progress --key-scheme monolithic",
            withoutPositions);
    }

    [Fact]
    public void RemoveCommand_NeverPassesStemPositions()
    {
        // The binary's remove command rejects --stem-positions; the log lines go through remove-logs.
        var operationId = Guid.Parse("4f2c4b1e-8d3a-4c55-9a51-2f0f3b7c9e10");

        var arguments = RustProcessHelper.BuildCorruptionManagerArguments(
            "remove",
            "C:/logs",
            "C:/cache",
            "steam",
            "C:/ops/evidence.json",
            "C:/ops/progress.json",
            "bare_metal",
            "C:/ops/positions.json",
            operationId);

        Assert.Equal(
            $"remove \"C:/logs\" \"C:/cache\" \"steam\" \"C:/ops/progress.json\" --evidence-file \"C:/ops/evidence.json\" --progress --key-scheme bare_metal --operation-id \"{operationId}\"",
            arguments);
    }

    [Fact]
    public async Task TrackedProcessDoesNotHoldTheLeftoverWaitAsync()
    {
        // A copy under a unique name keeps every other run of the helper out of the name match.
        var folder = Path.Combine(AppContext.BaseDirectory, "log-process");
        var extension = OperatingSystem.IsWindows() ? ".exe" : "";
        var name = $"LogProcess-wait-{Guid.NewGuid().ToString("N")[..8]}";
        var copy = Path.Combine(folder, name + extension);
        var work = Directory.CreateTempSubdirectory("leftover-wait-");
        File.Copy(Path.Combine(folder, "LogProcess" + extension), copy);

        // The control file names a pipe nobody serves, so the child waits in its connect.
        File.WriteAllText(Path.Combine(work.FullName, "log-processing-pipe"), $"unserved-{Guid.NewGuid():N}");
        var start = new ProcessStartInfo(copy) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(work.FullName);
        start.ArgumentList.Add(Path.Combine(work.FullName, "progress.json"));
        start.ArgumentList.Add("0");
        start.ArgumentList.Add("default");
        start.ArgumentList.Add(Path.Combine(work.FullName, "positions.json"));
        var manager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var process = Process.Start(start)!;
        try
        {
            manager.Track(process);
            using (var tracked = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                await manager.WaitForProcessesExitAsync([name], tracked.Token);
            }

            manager.Untrack(process);
            using var untracked = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => manager.WaitForProcessesExitAsync([name], untracked.Token));
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            process.Dispose();
            File.Delete(copy);
            work.Delete(recursive: true);
        }
    }

    private sealed class KillWaitProcessManager : ProcessManager
    {
        public TaskCompletionSource WaitStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan? WaitTimeout { get; private set; }

        public KillWaitProcessManager()
            : base(NullLogger<ProcessManager>.Instance)
        {
        }

        public override async Task WaitAfterKillAsync(Process process, TimeSpan timeout)
        {
            WaitTimeout = timeout;
            WaitStarted.TrySetResult();
            await Release.Task;
        }
    }
}
