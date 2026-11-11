using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A run's message is a locale key from registration to its end, so the card and the run list show it
/// in the reader's language. Exception text travels as the error detail, never as the message.
/// </summary>
public sealed class OperationMessageKeyTests
{
    [Fact]
    public void EveryWayARunStartsWritesTheStartingKey()
    {
        var tracker = CreateTracker();

        var registered = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource());
        Assert.Equal("signalr.generic.starting", tracker.GetOperation(registered)!.Message);

        var waitingRun = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource(),
            initialStatus: OperationStatus.Waiting);
        Assert.True(tracker.BeginWaitingOperation(waitingRun, state: null, onTerminalCleanup: null, onTerminalEmit: null));
        Assert.Equal("signalr.generic.starting", tracker.GetOperation(waitingRun)!.Message);

        var restored = Guid.NewGuid();
        Assert.True(tracker.TryRestoreOperation(restored, OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource()));
        Assert.Equal("signalr.generic.starting", tracker.GetOperation(restored)!.Message);

        var removal = tracker.RegisterOperation(OperationType.UnmappedFileRemoval, "Unmapped Files", new CancellationTokenSource());
        Assert.Equal("signalr.unmappedRemove.running", tracker.GetOperation(removal)!.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelAndForceStopWriteTheCancelingKey(bool forceKill)
    {
        var tracker = CreateTracker();
        var id = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource());

        if (forceKill) Assert.True(tracker.ForceKillOperation(id));
        else Assert.Equal(OperationCancelResult.Requested, tracker.CancelOperation(id));

        Assert.Equal("signalr.generic.canceling", tracker.GetOperation(id)!.Message);
    }

    [Theory]
    [InlineData(true, false, "signalr.generic.complete")]
    [InlineData(false, false, "signalr.generic.failed")]
    [InlineData(false, true, "signalr.generic.canceled")]
    public void AnEndingWithNoReasonWritesTheGenericKey(bool success, bool canceled, string expected)
    {
        var tracker = CreateTracker();
        var id = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource());

        tracker.CompleteOperation(id, success, canceled: canceled);

        Assert.Equal(expected, tracker.GetOperation(id)!.Message);
    }

    // A start that throws a refusal nobody keyed ends on the generic key; its text is the error detail.
    [Fact]
    public async Task AnUnkeyedRefusalAtPromotionEndsOnTheFailedKeyWithItsTextAsDetailAsync()
    {
        var harness = new OperationWaitlistCancelDuringPromotionTests.PromotionHarness();
        var waitingRun = await harness.EnqueueBehindCacheClearAsync(
            OperationType.LogProcessing,
            OperationType.LogProcessing.ToString(),
            () => throw new ServiceUnavailableException("Failed to start log processing for 'default'"));
        var ended = harness.WaitForTerminal(waitingRun.OperationId);

        harness.ReleaseBlocker();

        var operation = await ended.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Equal("signalr.generic.failed", operation.Message);
        Assert.Equal("Failed to start log processing for 'default'", operation.ErrorDetail);
    }

    // A decline ends skipped on its own key, never on the exception's text.
    [Fact]
    public async Task ADeclineAtPromotionEndsSkippedOnItsKeyAsync()
    {
        var harness = new OperationWaitlistCancelDuringPromotionTests.PromotionHarness();
        var waitingRun = await harness.EnqueueBehindCacheClearAsync(
            OperationType.GameDetection,
            OperationType.GameDetection.ToString(),
            () => throw new DownloadInProgressException(
                "The download tracker has not reported yet", "errors.unmappedRemoval.trackerNotReported"));
        var ended = harness.WaitForTerminal(waitingRun.OperationId);

        harness.ReleaseBlocker();

        var operation = await ended.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(OperationStatus.Skipped, operation.Status);
        Assert.Equal("errors.unmappedRemoval.trackerNotReported", operation.Message);
    }

    private static UnifiedOperationTracker CreateTracker()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        return new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
    }
}
