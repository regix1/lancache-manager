using System.Text.Json;
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

    [Fact]
    public void StructuralModeAndReuseCountersRemainAdditiveInRecoverySnapshot()
    {
        var metrics = new CorruptionDetectionMetrics
        {
            DetectionMethod = CorruptionDetectionMethod.Structural,
            ScanMode = StructuralScanMode.Incremental
        };
        var relay = new CorruptionProgressRelay(
            metrics,
            CorruptionDetectionMethod.Structural,
            "primary",
            0,
            1,
            StructuralScanMode.Incremental);
        var source = new Dictionary<string, object?>
        {
            ["effectiveScanMode"] = "baseline",
            ["baselineStatus"] = "building",
            ["resumed"] = true,
            ["filesDiscovered"] = 12L,
            ["filesReused"] = 7L,
            ["filesInspected"] = 3L,
            ["filesRevalidated"] = 1L,
            ["invalidFiles"] = 2L,
            ["filesPendingRetry"] = 2L,
            ["stateCommitted"] = false
        };

        var snapshot = relay.Capture("scanning", 75, source, 10, 12, 1_000).Snapshot;

        Assert.Equal("incremental", snapshot.Context["scanMode"]);
        Assert.Equal("baseline", snapshot.Context["effectiveScanMode"]);
        Assert.Equal(7L, snapshot.Context["filesReused"]);
        Assert.Equal(3L, snapshot.Context["filesInspected"]);
        Assert.Equal("primary", snapshot.Context["datasourceName"]);
    }

    [Fact]
    public void StructuralSummaryUsesExactTerminalWireShape()
    {
        var metrics = new CorruptionDetectionMetrics
        {
            DetectionMethod = CorruptionDetectionMethod.Structural,
            ScanMode = StructuralScanMode.Incremental,
            EffectiveScanMode = StructuralEffectiveScanMode.Baseline,
            BaselineStatus = StructuralBaselineStatus.Ready,
            StateCommitted = true,
            Resumed = true,
            FilesDiscovered = 12,
            FilesProcessed = 12,
            FilesReused = 7,
            FilesInspected = 5,
            FilesRevalidated = 1,
            InvalidFiles = 2,
            FilesPruned = 3,
            StateEntries = 9
        };

        var summary = Assert.IsType<StructuralScanStatusResponse>(
            CorruptionDetectionService.SnapshotStructuralSummary(metrics));

        Assert.Equal("incremental", summary.ScanMode);
        Assert.Equal("baseline", summary.EffectiveScanMode);
        Assert.Equal("ready", summary.BaselineStatus);
        Assert.Equal(12, summary.FilesProcessed);
        Assert.True(summary.StateCommitted);
        Assert.True(summary.Resumed);
    }

    [Fact]
    public void RustProgressContextUpdatesTypedStructuralRecoveryMetrics()
    {
        var metrics = new CorruptionDetectionMetrics
        {
            DetectionMethod = CorruptionDetectionMethod.Structural,
            ScanMode = StructuralScanMode.Incremental
        };
        var context = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            """
            {
              "scanMode": "incremental",
              "effectiveScanMode": "baseline",
              "baselineStatus": "building",
              "resumed": true,
              "filesDiscovered": 20,
              "filesProcessed": 18,
              "filesReused": 12,
              "filesInspected": 6,
              "filesRevalidated": 2,
              "invalidFiles": 3,
              "filesPendingRetry": 2,
              "filesPruned": 4,
              "stateEntries": 16,
              "stateCommitted": false
            }
            """)!;

        CorruptionDetectionService.UpdateStructuralProgressMetrics(metrics, context);

        Assert.Equal(StructuralEffectiveScanMode.Baseline, metrics.EffectiveScanMode);
        Assert.Equal(StructuralBaselineStatus.Building, metrics.BaselineStatus);
        Assert.True(metrics.Resumed);
        Assert.Equal(20, metrics.FilesDiscovered);
        Assert.Equal(18, metrics.FilesProcessed);
        Assert.Equal(12, metrics.FilesReused);
        Assert.Equal(6, metrics.FilesInspected);
        Assert.Equal(2, metrics.FilesRevalidated);
        Assert.Equal(3, metrics.InvalidFiles);
        Assert.Equal(2, metrics.FilesPendingRetry);
        Assert.Equal(4, metrics.FilesPruned);
        Assert.Equal(16, metrics.StateEntries);
        Assert.False(metrics.StateCommitted);
    }
}
