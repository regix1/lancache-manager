using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A repair attempt sets a silence limit for its own children. The tracker raises the terminal that
/// promotes queued jobs on a task that copies the caller's context, so a job promoted by a terminal
/// the repair's flow raised must still start without that limit.
/// </summary>
public sealed class OperationQueueSilenceLimitTests
{
    [Fact]
    public async Task AJobPromotedByATerminalFromALimitedFlowStartsWithNoLimitAsync()
    {
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var queue = new OperationQueueService(
            tracker,
            OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationQueueService>.Instance);
        var blockerId = tracker.RegisterOperation(
            OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());
        var limitAtStart = new TaskCompletionSource<TimeSpan?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<Guid?> StartAsync()
        {
            limitAtStart.TrySetResult(RustProcessHelper.ChildSilenceLimit.Value);
            return Task.FromResult<Guid?>(tracker.RegisterOperation(
                OperationType.GameDetection, "Game Detection", new CancellationTokenSource()));
        }

        var queued = await queue.EnqueueAsync(
            OperationType.GameDetection,
            ConflictScope.Bulk(),
            "Game Detection",
            StartAsync,
            CancellationToken.None);
        Assert.True(queued.Queued);

        // The blocker ends inside a flow that holds the limit, as a repair's log pass does.
        await Task.Run(() =>
        {
            RustProcessHelper.ChildSilenceLimit.Value = TimeSpan.FromMinutes(30);
            tracker.CompleteOperation(blockerId, success: true);
        });

        Assert.Null(await limitAtStart.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
