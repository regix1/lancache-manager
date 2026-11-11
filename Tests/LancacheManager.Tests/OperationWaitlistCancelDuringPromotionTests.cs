using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Promotion swaps a parked operation for a freshly-registered one, so for a moment the id on the
/// user's card belongs to an operation that no longer drives any work. A cancel clicked in that
/// moment used to return 200, end the waiting card, and leave the promoted work running — the user
/// was told the operation stopped while it carried on.
///
/// These tests hold the promotion open at the one seam that already exists, the caller-supplied
/// start delegate, so the cancel lands at an exact point rather than a hoped-for one. Nothing here
/// depends on timing.
/// </summary>
public sealed class OperationWaitlistCancelDuringPromotionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelThatCompletesThroughItsTokenStillReturnsRequested(bool background)
    {
        var tracker = new PromotionHarness().Tracker;
        var cts = new CancellationTokenSource();
        var id = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", cts);
        using var registration = cts.Token.Register(() =>
        {
            if (background)
            {
                Task.Run(() => tracker.CompleteOperation(id, false, canceled: true))
                    .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            else
            {
                tracker.CompleteOperation(id, false, canceled: true);
            }
        });

        Assert.Equal(OperationCancelResult.Requested, tracker.CancelOperation(id));
        Assert.Equal(OperationStatus.Canceled, tracker.GetOperation(id)!.Status);
        Assert.Equal(OperationCancelResult.AlreadyFinished, tracker.CancelOperation(id));
    }

    [Fact]
    public async Task TerminalClaimBeforeCancelKeepsAlreadyFinishedAsync()
    {
        var tracker = new PromotionHarness().Tracker;
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        var id = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", cts);
        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var completion = Task.Run(() => tracker.CompleteOperation(id, true, onCompleting: _ =>
        {
            claimed.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        }));
        await claimed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var canceling = Task.Run(() => tracker.CancelOperation(id));
        release.Set();
        await completion;
        Assert.Equal(OperationCancelResult.AlreadyFinished, await canceling);
        Assert.False(token.IsCancellationRequested);
        Assert.Equal(OperationStatus.Completed, tracker.GetOperation(id)!.Status);
    }

    [Fact]
    public void RepeatedLiveCancelKeepsRequestedWithoutRepeatingTokenCallbacks()
    {
        var tracker = new PromotionHarness().Tracker;
        var cts = new CancellationTokenSource();
        var id = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", cts);
        var callbacks = 0;
        using var registration = cts.Token.Register(() => callbacks++);
        Assert.Equal(OperationCancelResult.Requested, tracker.CancelOperation(id));
        Assert.Equal(OperationCancelResult.Requested, tracker.CancelOperation(id));
        Assert.Equal(1, callbacks);
        Assert.Equal(OperationStatus.Canceling, tracker.GetOperation(id)!.Status);
        tracker.CompleteOperation(id, false, canceled: true);
    }

    [Fact]
    public async Task CancelWhileStartRuns_CancelsThePromotedOperationAsync()
    {
        var harness = new PromotionHarness();

        var waitingRun = await harness.EnqueueBehindBlockerAsync();
        harness.ReleaseBlocker();
        await harness.StartEntered.WaitAsync(TimeSpan.FromSeconds(5));

        // The waiter has already left the queue and the promoted operation does not exist yet.
        // This is the exact window the cancel used to fall into.
        Assert.Equal(OperationCancelResult.Requested, harness.Tracker.CancelOperation(waitingRun.OperationId));

        harness.ReleaseStart();

        // The intent must reach whatever the waiter turned into.
        await harness.PromotedCanceled.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancelAfterPromotion_FollowsTheHandoffToTheRunningOperationAsync()
    {
        var harness = new PromotionHarness();

        var waitingRun = await harness.EnqueueBehindBlockerAsync();
        harness.ReleaseBlocker();
        await harness.StartEntered.WaitAsync(TimeSpan.FromSeconds(5));
        harness.ReleaseStart();
        await harness.WaitForPromotionPassAsync();

        // A card that had not yet swapped to the promoted operation's id still cancels the work.
        Assert.Equal(OperationCancelResult.Requested, harness.Tracker.CancelOperation(waitingRun.OperationId));
        await harness.PromotedCanceled.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PromotionWithoutCancel_LeavesThePromotedOperationRunningAsync()
    {
        var harness = new PromotionHarness();

        await harness.EnqueueBehindBlockerAsync();
        harness.ReleaseBlocker();
        await harness.StartEntered.WaitAsync(TimeSpan.FromSeconds(5));
        harness.ReleaseStart();
        await harness.WaitForPromotionPassAsync();

        // Carrying cancel intent across the handoff must not cancel an ordinary promotion.
        Assert.False(harness.PromotedCanceled.IsCompleted);
        Assert.False(harness.PromotedToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ARequestDoesNotJoinAWaiterThatIsBeingCanceledAsync()
    {
        var harness = new PromotionHarness();
        var canceling = await harness.EnqueueBehindBlockerAsync();
        // The status a cancel sets first, before the waiter leaves the queue on another thread.
        harness.Tracker.GetOperation(canceling.OperationId)!.Status = OperationStatus.Canceling;

        var again = await harness.EnqueueBehindBlockerAsync();

        Assert.Equal(OperationStartStatus.Waiting, again.Status);
        Assert.NotEqual(canceling.OperationId, again.OperationId);
    }

    // A request made while the same work is being canceled does not join it: the canceled run stops
    // before it does the work, so the request waits and runs once that run has ended.
    [Fact]
    public async Task ARequestDoesNotJoinARunThatIsBeingCanceledAsync()
    {
        var harness = new PromotionHarness();
        var running = harness.Tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource());
        // The status a cancel sets first, before the run's worker ends it.
        harness.Tracker.GetOperation(running)!.Status = OperationStatus.Canceling;

        var waitingRun = await harness.EnqueueBehindBlockerAsync();

        Assert.NotEqual(running, waitingRun.OperationId);
    }

    // A parked start refused with a keyed reason that carries values ends Failed at once on that key, the
    // values on its failure reason, so the card words it in the reader's language.
    [Fact]
    public async Task ARefusalWithValuesAtPromotionEndsFailedOnItsKeyAsync()
    {
        var harness = new PromotionHarness();
        var waitingRun = await harness.EnqueueBehindCacheClearAsync(
            OperationType.LogRemoval,
            "Log Removal",
            () => throw new ValidationException("Logs directory is read-only for datasource 'default'")
            {
                StageKey = "errors.logs.directoryReadOnly",
                Context = new Dictionary<string, object?> { ["datasource"] = "default" }
            });
        var ended = harness.WaitForTerminal(waitingRun.OperationId);

        harness.ReleaseBlocker();

        var operation = await ended.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Equal("errors.logs.directoryReadOnly", operation.Message);
        Assert.Equal("errors.logs.directoryReadOnly", operation.FailureReason!.StageKey);
        Assert.Equal("default", operation.FailureReason.Context["datasource"]);
    }

    // A parked log run whose start fails for a reason other than "busy" ends Failed at once with the
    // datasource on its failure reason; it does not wait out the retry window and end skipped.
    [Fact]
    public async Task ALogRunThatFailsToStartAtPromotionEndsFailedOnItsKeyAsync()
    {
        var harness = new PromotionHarness();
        var waitingRun = await harness.EnqueueBehindCacheClearAsync(
            OperationType.LogProcessing,
            "Log Processing (default)",
            () => throw new ServiceUnavailableException("Failed to start log processing for 'default'")
            {
                StageKey = "errors.logs.startFailedForDatasource",
                Context = new Dictionary<string, object?> { ["datasource"] = "default" }
            });
        var ended = harness.WaitForTerminal(waitingRun.OperationId);

        harness.ReleaseBlocker();

        var operation = await ended.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Equal("errors.logs.startFailedForDatasource", operation.Message);
        Assert.Equal("default", operation.FailureReason!.Context["datasource"]);
    }

    /// <summary>
    /// A real tracker, conflict checker and queue, with a start delegate the test can hold open at
    /// will. The blocker keeps the operation it sends parked until the test releases it.
    /// </summary>
    internal sealed class PromotionHarness
    {
        private readonly TaskCompletionSource _startEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _promotedCanceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _promotedCts = new();
        private readonly OperationWaitlistService _queue;
        private Guid _blockerId;

        public PromotionHarness()
        {
            var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
            Tracker = new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
            var conflictChecker = OperationConflictTestServices.Create(Tracker, NullLogger<OperationConflictChecker>.Instance);
            _queue = new OperationWaitlistService(
                Tracker, conflictChecker, NullLogger<OperationWaitlistService>.Instance);

            _promotedCts.Token.Register(() => _promotedCanceled.TrySetResult());
        }

        public UnifiedOperationTracker Tracker { get; }

        public Task StartEntered => _startEntered.Task;

        public Task PromotedCanceled => _promotedCanceled.Task;

        public CancellationToken PromotedToken => _promotedCts.Token;

        public async Task<OperationStartResponse> EnqueueBehindBlockerAsync()
        {
            _blockerId = Tracker.RegisterOperation(
                OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());

            var waitingRun = await _queue.EnqueueAsync(
                OperationType.GameDetection,
                ConflictScope.Bulk(),
                "Game Detection",
                StartAsync,
                CancellationToken.None, new GameDetectionRequestKey(DetectionScanType.Full));

            Assert.Equal(OperationStartStatus.Waiting, waitingRun.Status);
            return waitingRun;
        }

        /// <summary>Parks <paramref name="start"/> behind a running cache clear, which every job waits for.</summary>
        public async Task<OperationStartResponse> EnqueueBehindCacheClearAsync(
            OperationType type,
            string displayName,
            Func<Task<Guid?>> start)
        {
            _blockerId = Tracker.RegisterOperation(
                OperationType.CacheClearing, "Cache Clear", new CancellationTokenSource());

            var waiting = await _queue.EnqueueAsync(type, ConflictScope.Bulk(), displayName, start, CancellationToken.None,
                OperationConflictTestServices.RequestKeyFor(type));

            Assert.Equal(OperationStartStatus.Waiting, waiting.Status);
            return waiting;
        }

        /// <summary>Completes with the operation as it ended.</summary>
        public Task<OperationInfo> WaitForTerminal(Guid operationId)
        {
            var ended = new TaskCompletionSource<OperationInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            Tracker.OperationTerminal += operation =>
            {
                if (operation.Id == operationId) ended.TrySetResult(operation);
            };
            return ended.Task;
        }

        public void ReleaseBlocker() => Tracker.CompleteOperation(_blockerId, success: true);

        public void ReleaseStart() => _releaseStart.TrySetResult();

        /// <summary>
        /// Blocks until the in-flight promotion pass has fully finished. The queue serializes
        /// promotion and enqueue on one mutex, and <see cref="StartEntered"/> already proved the
        /// promotion holds it, so an enqueue cannot return until that pass is done. This is a real
        /// barrier rather than a delay, which keeps the negative test honest.
        /// </summary>
        public Task<OperationStartResponse> WaitForPromotionPassAsync() => _queue.EnqueueAsync(
            OperationType.LogProcessing,
            ConflictScope.Bulk(),
            "Promotion barrier",
            () => Task.FromResult<Guid?>(Guid.NewGuid()),
            CancellationToken.None, OperationRequestKey.Plain);

        private async Task<Guid?> StartAsync()
        {
            _startEntered.TrySetResult();
            await _releaseStart.Task;
            return Tracker.RegisterOperation(
                OperationType.GameDetection, "Game Detection", _promotedCts);
        }
    }
}
