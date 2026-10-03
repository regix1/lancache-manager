using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
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
        using var child = new SilentChild();
        var manager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        using var process = Process.Start(child.Start)!;
        manager.Track(process);
        using (var tracked = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            await manager.WaitForProcessesExitAsync([child.Name], tracked.Token);
        }

        manager.Untrack(process);
        using var untracked = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.WaitForProcessesExitAsync([child.Name], untracked.Token));
    }

    [Fact]
    public async Task ASilentChildInARepairIsStoppedAsync()
    {
        using var child = new SilentChild();
        var helper = new RustProcessHelper(
            NullLogger<RustProcessHelper>.Instance,
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            pathResolver: null!,
            operationTracker: null!);
        RustProcessHelper.ChildSilenceLimit.Value = TimeSpan.FromSeconds(2);

        // Well under the child's own 30 s exit, so only the stop can end the run in time; the
        // message tells the stop apart from the bound running out.
        var stopped = await Assert.ThrowsAsync<TimeoutException>(
            () => helper.ExecuteTrackedProcessWithProgressEventsAsync(
                    child.Start,
                    operationId: null,
                    CancellationToken.None,
                    onProgressEvent: null,
                    "silent")
                .WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.StartsWith("silent wrote no progress", stopped.Message);
        Assert.Empty(Process.GetProcessesByName(child.Name));
    }

    [Fact]
    public async Task AChildThatReportsProgressKeepsRunningAsync()
    {
        var work = Directory.CreateTempSubdirectory("progress-child-");
        try
        {
            await using var pipe = new LogProcessingOperationOwnershipTests.LogPipe(work.FullName);
            var extension = OperatingSystem.IsWindows() ? ".exe" : "";
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "log-process", "LogProcess" + extension))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(work.FullName);
            start.ArgumentList.Add(Path.Combine(work.FullName, "progress.json"));
            start.ArgumentList.Add("0");
            start.ArgumentList.Add("default");
            start.ArgumentList.Add(Path.Combine(work.FullName, "positions.json"));
            var helper = new RustProcessHelper(
                NullLogger<RustProcessHelper>.Instance,
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                pathResolver: null!,
                operationTracker: null!);
            RustProcessHelper.ChildSilenceLimit.Value = TimeSpan.FromSeconds(2);

            var run = helper.ExecuteTrackedProcessWithProgressEventsAsync(
                start,
                operationId: null,
                CancellationToken.None,
                onProgressEvent: null,
                "progress");
            await pipe.ConnectAsync();
            // Each step makes the child print one progress event; together they outlast the limit
            // three times over.
            for (var step = 0; step < 6; step++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                await pipe.SendAsync(new LogProcessingProgress());
            }
            await pipe.SendAsync(new LogProcessingProgress(), exitCode: 0);

            Assert.Equal(0, (await run.WaitAsync(TimeSpan.FromSeconds(15))).ExitCode);
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AHungRepairProgramFailsTheAttemptAsync()
    {
        using var child = new SilentChild();
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(
            Path.Combine(Path.GetTempPath(), "lm-silent-repair-" + Guid.NewGuid().ToString("N")),
            OperationType.GameRemoval);
        // The job's purge fails, so alpha's log step started and was never kept, and the repair
        // redoes it.
        harness.Rust.FailingPurges = 1;
        harness.Rust.ReportDepotIds.Add(1);
        typeof(OperationStateService)
            .GetField("_repairSilenceLimit", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(harness.Owner, TimeSpan.FromSeconds(2));
        typeof(CacheManagementService)
            .GetField("_rustProcessHelper", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(harness.Manager, new SilentRedoRustProcessHelper(harness.Rust, child, harness.Tracker));
        var config = harness.CreateConfig(
            OperationType.GameRemoval,
            new RemovalMetrics { EntityKey = "570", EntityName = "Dota 2", EntityKind = "steam" },
            async (operationId, cancellationToken, report) =>
            {
                var game = await harness.Manager.RemoveGameFromCacheAsync(
                    570,
                    cancellationToken,
                    (percent, stage, context, files, bytes) =>
                        report(new RemovalProgressUpdate(percent, stage, context, files, bytes)),
                    operationId);
                return (game.CacheFilesDeleted, checked((long)game.TotalBytesFreed));
            });

        // Beta's cache step fails at once, so the run still ends failed and alpha's log step is the one redone.
        harness.ReleaseRust();
        var operationId = await TrackedRemovalOperationRunner.StartAsync(
            harness.Tracker,
            harness.NotificationService,
            config);
        await harness.WaitForTerminalAsync(operationId);

        // Well under the child's own 30 s exit, which would also fail the attempt.
        var bound = Stopwatch.StartNew();
        while (harness.ReadRepair(operationId).RetryAtUtc is null)
        {
            Assert.True(bound.Elapsed < TimeSpan.FromSeconds(15), "The hung attempt was never counted as failed.");
            await Task.Delay(250);
        }

        Assert.Empty(Process.GetProcessesByName(child.Name));
        // One failed attempt, not three, so the repair still holds the queue.
        Assert.Equal(operationId, harness.Owner.GetBlockingRepair()?.Id);
    }

    /// <summary>
    /// A copy of the log-processing helper under a unique name, so no other run of the helper matches
    /// it. The name stays within the 15 characters Linux keeps of a process name, so a lookup by name
    /// finds it on both systems. Its control file names a pipe this class holds open and never
    /// answers, so the child connects and then waits 30 s for a command, printing nothing and using
    /// no CPU time. A pipe nobody serves would not do: on Linux the child retries that connect in a
    /// loop that uses CPU time.
    /// </summary>
    private sealed class SilentChild : IDisposable
    {
        private readonly string _copy;
        private readonly DirectoryInfo _work;
        private readonly NamedPipeServerStream _pipe;

        public SilentChild()
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "log-process");
            var extension = OperatingSystem.IsWindows() ? ".exe" : "";
            Name = $"silent{Guid.NewGuid().ToString("N")[..8]}";
            _copy = Path.Combine(folder, Name + extension);
            _work = Directory.CreateTempSubdirectory("silent-child-");
            File.Copy(Path.Combine(folder, "LogProcess" + extension), _copy);
            var pipeName = $"silent-{Guid.NewGuid():N}";
            _pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            File.WriteAllText(Path.Combine(_work.FullName, "log-processing-pipe"), pipeName);
            Start = new ProcessStartInfo(_copy)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            Start.ArgumentList.Add(_work.FullName);
            Start.ArgumentList.Add(Path.Combine(_work.FullName, "progress.json"));
            Start.ArgumentList.Add("0");
            Start.ArgumentList.Add("default");
            Start.ArgumentList.Add(Path.Combine(_work.FullName, "positions.json"));
        }

        public string Name { get; }
        public ProcessStartInfo Start { get; }

        public void Dispose()
        {
            foreach (var process in Process.GetProcessesByName(Name))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                process.Dispose();
            }
            _pipe.Dispose();
            File.Delete(_copy);
            _work.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Hands every launch to the removal fake except the repair's redo purge, which starts the silent
    /// child for real, the way the attempt starts the purge binary.
    /// </summary>
    private sealed class SilentRedoRustProcessHelper : RustProcessHelper
    {
        private readonly RustProcessHelper _launches;
        private readonly SilentChild _child;
        private int _purges;

        public SilentRedoRustProcessHelper(
            RustProcessHelper launches,
            SilentChild child,
            IUnifiedOperationTracker tracker)
            : base(
                NullLogger<RustProcessHelper>.Instance,
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                pathResolver: null!,
                tracker)
        {
            _launches = launches;
            _child = child;
        }

        public override Task<ProcessExecutionResult> ExecuteTrackedProcessWithProgressEventsAsync(
            ProcessStartInfo start,
            Guid? operationId,
            CancellationToken cancellationToken,
            Func<RustProgressEvent, Task>? onProgressEvent,
            string processLabel = "rust")
        {
            // The job's own purge fails, so the second purge is the repair's redo.
            return processLabel == "cache_purge_log_entries" && Interlocked.Increment(ref _purges) == 2
                ? base.ExecuteTrackedProcessWithProgressEventsAsync(
                    _child.Start,
                    operationId,
                    cancellationToken,
                    onProgressEvent,
                    processLabel)
                : _launches.ExecuteTrackedProcessWithProgressEventsAsync(
                    start,
                    operationId,
                    cancellationToken,
                    onProgressEvent,
                    processLabel);
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
