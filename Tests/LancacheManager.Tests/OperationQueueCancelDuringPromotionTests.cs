using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
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
public sealed class OperationQueueCancelDuringPromotionTests
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
                Task.Run(() => tracker.CompleteOperation(id, false, cancelled: true))
                    .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            else
            {
                tracker.CompleteOperation(id, false, cancelled: true);
            }
        });

        Assert.Equal(OperationCancelResult.Requested, tracker.CancelOperation(id));
        Assert.Equal(OperationStatus.Cancelled, tracker.GetOperation(id)!.Status);
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
        var cancelling = Task.Run(() => tracker.CancelOperation(id));
        release.Set();
        await completion;
        Assert.Equal(OperationCancelResult.AlreadyFinished, await cancelling);
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
        Assert.Equal(OperationStatus.Cancelling, tracker.GetOperation(id)!.Status);
        tracker.CompleteOperation(id, false, cancelled: true);
    }

    [Fact]
    public async Task CancelWhileStartRuns_CancelsThePromotedOperationAsync()
    {
        var harness = new PromotionHarness();

        var queued = await harness.EnqueueBehindBlockerAsync();
        harness.ReleaseBlocker();
        await harness.StartEntered.WaitAsync(TimeSpan.FromSeconds(5));

        // The waiter has already left the queue and the promoted operation does not exist yet.
        // This is the exact window the cancel used to fall into.
        Assert.Equal(OperationCancelResult.Requested, harness.Tracker.CancelOperation(queued.OperationId));

        harness.ReleaseStart();

        // The intent must reach whatever the waiter turned into.
        await harness.PromotedCancelled.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancelAfterPromotion_FollowsTheHandoffToTheRunningOperationAsync()
    {
        var harness = new PromotionHarness();

        var queued = await harness.EnqueueBehindBlockerAsync();
        harness.ReleaseBlocker();
        await harness.StartEntered.WaitAsync(TimeSpan.FromSeconds(5));
        harness.ReleaseStart();
        await harness.WaitForPromotionPassAsync();

        // A card that had not yet swapped to the promoted operation's id still cancels the work.
        Assert.Equal(OperationCancelResult.Requested, harness.Tracker.CancelOperation(queued.OperationId));
        await harness.PromotedCancelled.WaitAsync(TimeSpan.FromSeconds(5));
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
        Assert.False(harness.PromotedCancelled.IsCompleted);
        Assert.False(harness.PromotedToken.IsCancellationRequested);
    }

    /// <summary>
    /// A real tracker, conflict checker and queue, with a start delegate the test can hold open at
    /// will. The blocker keeps the enqueued operation parked until the test releases it.
    /// </summary>
    private sealed class PromotionHarness
    {
        private readonly TaskCompletionSource _startEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _promotedCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _promotedCts = new();
        private readonly OperationQueueService _queue;
        private Guid _blockerId;

        public PromotionHarness()
        {
            var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
            Tracker = new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
            var conflictChecker = new OperationConflictChecker(Tracker, NullLogger<OperationConflictChecker>.Instance);
            var notifications = DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
            _queue = new OperationQueueService(
                Tracker, conflictChecker, notifications, NullLogger<OperationQueueService>.Instance);

            _promotedCts.Token.Register(() => _promotedCancelled.TrySetResult());
        }

        public UnifiedOperationTracker Tracker { get; }

        public Task StartEntered => _startEntered.Task;

        public Task PromotedCancelled => _promotedCancelled.Task;

        public CancellationToken PromotedToken => _promotedCts.Token;

        public async Task<QueuedOperationResponse> EnqueueBehindBlockerAsync()
        {
            _blockerId = Tracker.RegisterOperation(
                OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());

            var queued = await _queue.EnqueueAsync(
                OperationType.GameDetection,
                ConflictScope.Bulk(),
                "Game Detection",
                StartAsync,
                CancellationToken.None);

            Assert.True(queued.Queued);
            return queued;
        }

        public void ReleaseBlocker() => Tracker.CompleteOperation(_blockerId, success: true);

        public void ReleaseStart() => _releaseStart.TrySetResult();

        /// <summary>
        /// Blocks until the in-flight promotion pass has fully finished. The queue serializes
        /// promotion and enqueue on one mutex, and <see cref="StartEntered"/> already proved the
        /// promotion holds it, so an enqueue cannot return until that pass is done. This is a real
        /// barrier rather than a delay, which keeps the negative test honest.
        /// </summary>
        public Task<QueuedOperationResponse> WaitForPromotionPassAsync() => _queue.EnqueueAsync(
            OperationType.LogProcessing,
            ConflictScope.Bulk(),
            "Promotion barrier",
            () => Task.FromResult<Guid?>(Guid.NewGuid()),
            CancellationToken.None);

        private async Task<Guid?> StartAsync()
        {
            _startEntered.TrySetResult();
            await _releaseStart.Task;
            return Tracker.RegisterOperation(
                OperationType.GameDetection, "Game Detection", _promotedCts);
        }
    }
}
