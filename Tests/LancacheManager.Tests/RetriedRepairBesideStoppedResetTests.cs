using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A repair failed three times and failed out, so a database reset could start. A user force stops the reset, whose
/// code still runs and keeps its conflict hold, and clicks Retry on the repair. The retried repair must wait for the
/// stopped reset's code to end, the way a new job would, in every order of the force stop, the Retry and the end of
/// the reset's code.
/// </summary>
public sealed class RetriedRepairBesideStoppedResetTests
{
    private const string RetryAfterTheForceStop = "Retry after the force stop, while the reset's code runs";
    private const string RetryBeforeTheForceStop = "Retry while the reset runs, then the force stop";
    private const string RetryAfterTheResetEnded = "Retry after the stopped reset's code ended";

    /// <summary>How long the test watches for a repair that must not start; the repair looks again every second.</summary>
    private static readonly TimeSpan MustNotStartWindow = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(RetryAfterTheForceStop)]
    [InlineData(RetryBeforeTheForceStop)]
    [InlineData(RetryAfterTheResetEnded)]
    public async Task ARetriedRepair_WaitsForAForceStoppedResetWhoseCodeStillRuns(string order)
    {
        var root = Path.Combine(Path.GetTempPath(), "retried-repair-stopped-reset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repairId = Guid.NewGuid();
            var state = StateTestMethods.CreateStateService(Path.Combine(root, "state"));
            state.SetSetupCompleted(true);
            state.SaveOperationRepairs(
            [
                new OperationRepair
                {
                    Id = repairId,
                    Type = OperationType.EvictionScan,
                    Name = "Eviction scan",
                    StartedAt = DateTime.UtcNow.AddMinutes(-3),
                    Phase = OperationRepairPhase.Repairing,
                    Outcome = OperationStatus.Failed,
                    Error = "signalr.generic.interruptedByRestart",
                    Sources =
                    [
                        new OperationRepairSource
                        {
                            Datasource = "alpha",
                            LogRoot = Path.Combine(root, "logs"),
                            CacheRoot = Path.Combine(root, "cache"),
                            KeyScheme = "steam"
                        }
                    ],
                    EvictionScanId = repairId,
                    EvictionScan = new EvictionScanRepair { Processed = 8, Evicted = 3, UnEvicted = 2 }
                }
            ]);

            // The first three attempts fail, so the repair fails out; the attempt after Retry is its deletes.
            var attempts = 0;
            var failedOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retriedDeletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var now = DateTime.UtcNow;
            OperationConflictChecker? checker = null;
            using var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
            await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(
                root,
                stateService: state,
                processManager: processes,
                now: () => now,
                waitUntil: (retryAt, _) =>
                {
                    now = retryAt;
                    return Task.CompletedTask;
                },
                apply: (_, _) =>
                {
                    var attempt = Interlocked.Increment(ref attempts);
                    if (attempt <= 3)
                    {
                        if (attempt == 3) failedOut.TrySetResult();
                        throw new IOException("The repair's delete failed");
                    }
                    retriedDeletes.TrySetResult();
                    return Task.CompletedTask;
                },
                registrations: services => services
                    .AddSingleton(_ => checker!)
                    .AddSingleton<IOperationConflictChecker>(_ => checker!));
            var tracker = harness.Tracker;
            checker = new OperationConflictChecker(tracker, harness.Owner, NullLogger<OperationConflictChecker>.Instance);
            var cancellation = new OperationCancellationService(
                tracker, processes, harness.Owner, NullLogger<OperationCancellationService>.Instance);

            await failedOut.Task.WaitAsync(Timeout);
            var deadline = DateTime.UtcNow + Timeout;
            while (harness.Owner.GetBlockingRepair() is not null && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.Null(harness.Owner.GetBlockingRepair());

            // The reset as it runs: its card and the hold its code keeps until it ends.
            var resetId = tracker.RegisterOperation(OperationType.DatabaseReset, "Database Reset", new CancellationTokenSource());
            var resetHold = checker.HoldRun(OperationType.DatabaseReset, [ConflictScope.Bulk()], () => resetId);
            async Task ForceStopAsync() => Assert.True(await cancellation.ForceKillAsync(resetId), "The force stop was refused");
            async Task RetryAsync()
            {
                // The failed-out run lets go of its task entry a moment after the card shows the failure.
                var until = DateTime.UtcNow + Timeout;
                while (!await harness.Owner.RetryRepairAsync(repairId))
                {
                    Assert.True(DateTime.UtcNow < until, "The Retry was refused");
                    await Task.Delay(10);
                }
            }

            switch (order)
            {
                case RetryAfterTheForceStop:
                    await ForceStopAsync();
                    await RetryAsync();
                    await AssertWaitsAsync(retriedDeletes.Task, order);
                    resetHold.Dispose();
                    break;
                case RetryBeforeTheForceStop:
                    await RetryAsync();
                    await AssertWaitsAsync(retriedDeletes.Task, order);
                    await ForceStopAsync();
                    await AssertWaitsAsync(retriedDeletes.Task, order);
                    resetHold.Dispose();
                    break;
                default:
                    await ForceStopAsync();
                    resetHold.Dispose();
                    await RetryAsync();
                    break;
            }

            await retriedDeletes.Task.WaitAsync(Timeout);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task AssertWaitsAsync(Task retriedDeletes, string order)
    {
        await Task.WhenAny(retriedDeletes, Task.Delay(MustNotStartWindow));
        Assert.False(retriedDeletes.IsCompleted,
            $"The retried repair ran its deletes ({order}) while the force-stopped database reset's code still ran and " +
            "held its conflict hold; it must wait until that code has ended");
    }
}
