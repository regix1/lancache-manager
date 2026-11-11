using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The queue's two doors share one lock: a start that answers a conflict itself and a request that parks
/// on one. A real tracker, conflict checker and queue, so the order of the two is the queue's own.
/// </summary>
public sealed class OperationWaitlistStartUnlessBusyTests
{
    private readonly UnifiedOperationTracker _tracker;
    private readonly OperationWaitlistService _queue;

    public OperationWaitlistStartUnlessBusyTests()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        _tracker = new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
        var conflictChecker = OperationConflictTestServices.Create(_tracker, NullLogger<OperationConflictChecker>.Instance);
        _queue = new OperationWaitlistService(_tracker, conflictChecker, NullLogger<OperationWaitlistService>.Instance);
    }

    // A conflict is the caller's answer: nothing starts and nothing parks.
    [Fact]
    public async Task ReturnsTheConflictWithoutStartingAsync()
    {
        var clearId = _tracker.RegisterOperation(OperationType.CacheClearing, "Cache Clear", new CancellationTokenSource());
        var starts = 0;

        var conflict = await _queue.StartUnlessBusyAsync(
            OperationType.LogProcessing,
            ConflictScope.Bulk(),
            () =>
            {
                starts++;
                return Task.FromResult<Guid?>(null);
            },
            CancellationToken.None, OperationRequestKey.Plain);

        Assert.NotNull(conflict);
        Assert.Equal(clearId, conflict!.ActiveOperationId);
        Assert.Equal(0, starts);
        Assert.Empty(_tracker.GetWaitingOperations());
    }

    // With nothing in its way the start runs once and there is no conflict to answer.
    [Fact]
    public async Task StartsOnceWhenNothingIsInTheWayAsync()
    {
        var starts = 0;

        var conflict = await _queue.StartUnlessBusyAsync(
            OperationType.LogProcessing,
            ConflictScope.Bulk(),
            () =>
            {
                starts++;
                return Task.FromResult<Guid?>(null);
            },
            CancellationToken.None, OperationRequestKey.Plain);

        Assert.Null(conflict);
        Assert.Equal(1, starts);
    }

    // A clear sent while a start is registering waits for the lock, then finds the registered run and
    // parks behind it; its own start never runs beside the other.
    [Fact]
    public async Task AClearSentWhileAStartRegistersParksBehindItAsync()
    {
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clearStarts = 0;
        var logRun = _queue.StartUnlessBusyAsync(
            OperationType.LogProcessing,
            ConflictScope.Bulk(),
            () =>
            {
                var logId = _tracker.RegisterOperation(OperationType.LogProcessing, "Log Processing", new CancellationTokenSource());
                registered.TrySetResult();
                return releaseStart.Task.ContinueWith(_ => (Guid?)logId, TaskScheduler.Default);
            },
            CancellationToken.None, OperationRequestKey.Plain);
        await registered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var clear = _queue.EnqueueAsync(
            OperationType.CacheClearing,
            ConflictScope.Bulk(),
            "Cache Clear (All)",
            () =>
            {
                clearStarts++;
                return Task.FromResult<Guid?>(Guid.NewGuid());
            },
            CancellationToken.None, OperationRequestKey.Plain);
        Assert.False(clear.IsCompleted);
        releaseStart.TrySetResult();

        Assert.Null(await logRun.WaitAsync(TimeSpan.FromSeconds(5)));
        var waitingRun = await clear.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OperationStartStatus.Waiting, waitingRun.Status);
        Assert.Equal(0, clearStarts);
    }

    // A run of the same type that registers beside the start's own run (a depot Apply mappings click) did not
    // ask for the start's work: once the crawl ends, a request for that crawl waits and runs instead of joining it.
    [Fact]
    public async Task ARunRegisteredBesideTheStartDoesNotTakeItsRequestAsync()
    {
        var applyId = Guid.Empty;
        var crawlId = Guid.Empty;
        var conflict = await _queue.StartUnlessBusyAsync(
            OperationType.DepotMapping,
            ConflictScope.Bulk(),
            () =>
            {
                applyId = _tracker.RegisterOperation(OperationType.DepotMapping, "Depot Mapping", new CancellationTokenSource());
                crawlId = _tracker.RegisterOperation(OperationType.DepotMapping, "Depot Mapping", new CancellationTokenSource());
                return Task.FromResult<Guid?>(crawlId);
            },
            CancellationToken.None, DepotMappingRequestKey.Crawl(incrementalOnly: false));
        Assert.Null(conflict);
        _tracker.CompleteOperation(crawlId, success: true);

        var waitingRun = await _queue.EnqueueAsync(
            OperationType.DepotMapping,
            ConflictScope.Bulk(),
            "Depot Mapping",
            () => Task.FromResult<Guid?>(null),
            CancellationToken.None, DepotMappingRequestKey.Crawl(incrementalOnly: false));

        Assert.Equal(OperationStartStatus.Waiting, waitingRun.Status);
        Assert.Equal(waitingRun.OperationId, Assert.Single(_tracker.GetWaitingOperations()).Id);

        _tracker.CancelOperation(waitingRun.OperationId);
        _tracker.CompleteOperation(applyId, success: true);
    }

    // A manual log run sent while a cache clear runs waits on a card and starts once the clear ends; it
    // never starts beside the clear.
    [Fact]
    public async Task ALogRunSentDuringAClearParksAndStartsWhenTheClearEndsAsync()
    {
        var clearId = _tracker.RegisterOperation(OperationType.CacheClearing, "Cache Clear", new CancellationTokenSource());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;

        var waitingRun = await _queue.EnqueueAsync(
            OperationType.LogProcessing,
            ConflictScope.Bulk(),
            "Log Processing",
            () =>
            {
                Interlocked.Increment(ref starts);
                started.TrySetResult();
                return Task.FromResult<Guid?>(
                    _tracker.RegisterOperation(OperationType.LogProcessing, "Log Processing", new CancellationTokenSource()));
            },
            CancellationToken.None, OperationRequestKey.Plain);

        Assert.Equal(OperationStartStatus.Waiting, waitingRun.Status);
        Assert.Equal(0, starts);

        _tracker.CompleteOperation(clearId, success: true);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, starts);
    }
}
