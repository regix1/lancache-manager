using LancacheManager.Core.Services;
using LancacheManager.Models;

namespace LancacheManager.Tests;

public class CorruptionProgressRelayTests
{
    [Fact]
    public async Task CancelledTokenStillStartsWorkerSoItCanReachTerminalCleanup()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var entered = false;

        await CorruptionDetectionService.StartDetectionWorker(
            token =>
            {
                entered = true;
                Assert.True(token.IsCancellationRequested);
                return Task.CompletedTask;
            },
            cancellation.Token);

        Assert.True(entered);
    }

    [Fact]
    public void RisingEnumerationCountIsCapturedBeforeTimeGate()
    {
        var metrics = new CorruptionDetectionMetrics();
        var relay = new CorruptionProgressRelay(
            metrics, CorruptionDetectionMethod.Structural, "primary", 0, 1);

        Assert.True(relay.Capture("signalr.corruptionDetect.enumerating", 0,
            new Dictionary<string, object?> { ["count"] = 1 }, 1, 0, 1_000).ShouldEmit);
        var pending = relay.Capture("signalr.corruptionDetect.enumerating", 0,
            new Dictionary<string, object?> { ["count"] = 2 }, 2, 0, 1_100);

        Assert.True(pending.IsNew);
        Assert.False(pending.ShouldEmit);
        Assert.Equal(2, metrics.CurrentProgress!.Context["count"]);

        var released = relay.Capture("signalr.corruptionDetect.enumerating", 0,
            new Dictionary<string, object?> { ["count"] = 2 }, 2, 0, 1_250);
        Assert.False(released.IsNew);
        Assert.True(released.ShouldEmit);
    }

    [Fact]
    public void BelowFivePercentProgressAndStageChangesAreNotDiscarded()
    {
        var metrics = new CorruptionDetectionMetrics();
        var relay = new CorruptionProgressRelay(
            metrics, CorruptionDetectionMethod.Structural, "primary", 0, 1);

        relay.Capture("enumerating", 0, null, 10, 0, 1_000);
        var stageChange = relay.Capture("scanning", 0.1, null, 1, 1_000, 1_001);
        var smallAdvance = relay.Capture("scanning", 0.2, null, 2, 1_000, 1_251);

        Assert.True(stageChange.ShouldEmit);
        Assert.True(smallAdvance.ShouldEmit);
        Assert.Equal(0.2, metrics.CurrentProgress!.PercentComplete, 6);
    }

    [Fact]
    public void DatasourceBandsNeverRegressAndContextTracksActiveDatasource()
    {
        var metrics = new CorruptionDetectionMetrics();
        var first = new CorruptionProgressRelay(
            metrics, CorruptionDetectionMethod.Structural, "one", 0, 2);
        var second = new CorruptionProgressRelay(
            metrics, CorruptionDetectionMethod.Structural, "two", 1, 2);

        var firstComplete = first.Capture("scanning", 100, null, 100, 100, 1_000).Snapshot;
        var secondStart = second.Capture("starting", 0, null, 0, 0, 1_001).Snapshot;
        var secondProgress = second.Capture("scanning", 1, null, 1, 100, 1_002).Snapshot;

        Assert.Equal(50, firstComplete.PercentComplete);
        Assert.Equal(50, secondStart.PercentComplete);
        Assert.True(secondProgress.PercentComplete >= secondStart.PercentComplete);
        Assert.Equal("two", secondProgress.Context["datasourceName"]);
        Assert.Equal(2, secondProgress.Context["datasourceIndex"]);
    }
}
