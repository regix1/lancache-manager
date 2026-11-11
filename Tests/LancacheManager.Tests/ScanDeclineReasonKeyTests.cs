using System.Runtime.CompilerServices;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Tests.CacheScanGateHarness;

namespace LancacheManager.Tests;

/// <summary>
/// A scan parked behind another job asks the download gate again when it is promoted. When the gate
/// refuses, the waiting row ends skipped on the refusal's own key, so the card says why in the reader's
/// language: a client download writing to the cache, or a download tracker that has not reported yet.
/// </summary>
public sealed class ScanDeclineReasonKeyTests
{
    private const string DownloadInProgressKey = "errors.unmappedRemoval.downloadInProgress";
    private const string TrackerNotReportedKey = "errors.unmappedRemoval.trackerNotReported";

    [Theory]
    [InlineData(OperationType.CacheSizeScan, false, DownloadInProgressKey)]
    [InlineData(OperationType.GameDetection, false, DownloadInProgressKey)]
    [InlineData(OperationType.CorruptionDetection, false, DownloadInProgressKey)]
    [InlineData(OperationType.CacheSizeScan, true, TrackerNotReportedKey)]
    [InlineData(OperationType.GameDetection, true, TrackerNotReportedKey)]
    [InlineData(OperationType.CorruptionDetection, true, TrackerNotReportedKey)]
    public async Task APromotedScanTheGateRefusesEndsSkippedOnTheRefusalKeyAsync(
        OperationType type, bool trackerUnreported, string expectedKey)
    {
        var snapshot = new DownloadSpeedSnapshot();
        var speedTracker = TrackerWith(snapshot, []);
        var gate = GateOver(speedTracker);
        var (name, start) = ScanStart(type, gate);

        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var queue = new OperationWaitlistService(
            tracker,
            OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationWaitlistService>.Instance);
        var blockerId = tracker.RegisterOperation(OperationType.LogProcessing, "Blocker", new CancellationTokenSource());

        var waiting = await queue.EnqueueAsync(type, ConflictScope.Bulk(), name, start, CancellationToken.None,
            OperationConflictTestServices.RequestKeyFor(type));
        Assert.Equal(OperationStartStatus.Waiting, waiting.Status);

        // The gate turns the scan away only after the request was parked, so only the re-check at
        // promotion sees it.
        if (trackerUnreported)
        {
            SetField(speedTracker, "_unreportedSinceUtc", DateTime.UtcNow);
        }
        else
        {
            MakeBusy(snapshot);
        }

        tracker.CompleteOperation(blockerId, success: true);
        var row = await WaitForTerminalAsync(tracker, waiting.OperationId);

        Assert.Equal(OperationStatus.Skipped, row.Status);
        Assert.Equal(expectedKey, row.Message);
    }

    [Fact]
    public async Task ADatasourceScanRefusedForADownloadNamesTheRefusalByKeyAsync()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), "lancache-manager-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cachePath);
        try
        {
            var service = (CacheManagementService)RuntimeHelpers.GetUninitializedObject(typeof(CacheManagementService));
            SetField(service, "_logger", NullLogger<CacheManagementService>.Instance);
            SetField(service, "_cacheScanGate", Downloading());
            SetField(service, "_datasourceService", DatasourceServiceWith(("alpha", cachePath, cachePath, true, "auto")));

            var refusal = await Assert.ThrowsAsync<DownloadInProgressException>(
                () => service.GetCacheSizeAsync(force: true, datasource: "alpha"));

            Assert.Equal(DownloadInProgressKey, refusal.StageKey);
        }
        finally
        {
            Directory.Delete(cachePath, recursive: true);
        }
    }

    private static (string Name, Func<Task<Guid?>> Start) ScanStart(OperationType type, CacheScanGate gate)
    {
        switch (type)
        {
            case OperationType.CacheSizeScan:
            {
                var service = (CacheManagementService)RuntimeHelpers.GetUninitializedObject(typeof(CacheManagementService));
                SetField(service, "_logger", NullLogger<CacheManagementService>.Instance);
                SetField(service, "_cacheScanGate", gate);
                return ("Cache File Scan",
                    () => service.StartCacheSizeScanInBackgroundAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual)));
            }
            case OperationType.GameDetection:
            {
                var service = (GameCacheDetectionService)RuntimeHelpers.GetUninitializedObject(typeof(GameCacheDetectionService));
                SetField(service, "_cacheScanGate", gate);
                return ("Game Detection",
                    () => service.StartDetectionAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual), incremental: true));
            }
            default:
            {
                var service = (CorruptionDetectionService)RuntimeHelpers.GetUninitializedObject(typeof(CorruptionDetectionService));
                SetField(service, "_cacheScanGate", gate);
                return ("Corruption Detection",
                    async () => (Guid?)await service.StartDetectionAsync());
            }
        }
    }

    private static async Task<OperationInfo> WaitForTerminalAsync(UnifiedOperationTracker tracker, Guid operationId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (tracker.GetOperation(operationId) is { } operation && operation.Status.IsTerminal())
            {
                return operation;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The parked scan did not end within 5 seconds.");
    }
}
