using System.Reflection;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using static LancacheManager.Tests.CacheScanGateHarness;

namespace LancacheManager.Tests;

/// <summary>
/// An eviction scan whose game detection failed still says so after a restart. A restart during the
/// disk scan restores the run from its repair record, so the failure has to be on that record by the
/// time the disk scan starts, not only once the whole scan is done. The scan runs on the scan fixture
/// of <see cref="CacheScanDetectionPhaseTests"/>, reached by reflection because it is private there.
/// </summary>
// The fixture's registry starts a run when the process-wide downloads-ended event fires, so this class
// shares that event's collection like the fixture's own tests.
[Collection(nameof(DownloadsEndedEventCollection))]
public sealed class EvictionScanDetectionFailureRestartTests
{
    private static readonly Type PhaseContextType = typeof(CacheScanDetectionPhaseTests)
        .GetNestedType("PhaseContext", BindingFlags.NonPublic)!;

    private static readonly Type ScanResultType = typeof(CacheScanDetectionPhaseTests)
        .GetNestedType("ScanResultRustProcessHelper", BindingFlags.NonPublic)!;

    [Theory]
    [InlineData("signalr.generic.failed", "Cache index could not be read", "Cache index could not be read", null)]
    [InlineData("signalr.gameDetect.error.timedOut", null, null, "signalr.gameDetect.error.timedOut")]
    public async Task ADiskScanAfterAFailedDetectionRunsWithTheFailureOnItsRepairRecordAsync(
        string completionStageKey,
        string? errorDetail,
        string? storedError,
        string? storedStageKey)
    {
        using var ctx = (IDisposable)Activator.CreateInstance(PhaseContextType)!;
        var scan = (CacheReconciliationService)PhaseContextType.GetProperty("Scan")!.GetValue(ctx)!;
        var tracker = (CacheScanDetectionPhaseTests.FakeTracker)PhaseContextType.GetProperty("Tracker")!.GetValue(ctx)!;
        var repairs = (OperationStateService)PhaseContextType
            .GetField("_operationStateService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(ctx)!;
        await using var context = (AppDbContext)PhaseContextType.GetMethod("CreateContext")!.Invoke(ctx, null)!;
        SetScanField(scan, "_stateService", PhaseContextType.GetProperty("State")!.GetValue(ctx)!);
        SetScanField(scan, "_datasourceService", PhaseContextType.GetProperty("Datasources")!.GetValue(ctx)!);
        SetScanField(scan, "_cacheScanGate", Idle());

        // The record a restart during the disk scan would restore the run from.
        var diskScanStarted = false;
        EvictionScanRepair? restoredFrom = null;
        Func<Guid, CancellationToken, Task> diskScan = async (operationId, cancellationToken) =>
        {
            diskScanStarted = true;
            restoredFrom = repairs.GetPendingRepairs().Single(repair => repair.Id == operationId).EvictionScan;
            var checkpoint = await context.EvictionScanCheckpoints
                .SingleAsync(item => item.OperationId == operationId, cancellationToken);
            checkpoint.FinalizedAtUtc = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
        };
        SetScanField(scan, "_rustProcessHelper", Activator.CreateInstance(
            ScanResultType,
            diskScan,
            """{"success":true,"processed":1,"evicted":0,"unEvicted":0}""")!);

        var notice = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        var scanId = (Guid)PhaseContextType.GetMethod("RegisterScan")!.Invoke(ctx, [notice])!;
        var run = (Task)typeof(CacheReconciliationService)
            .GetMethod("ReconcileCacheFilesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scan, [context, scanId, CancellationToken.None, notice])!;

        // The detection ends the way a failed one ends: on its key, with the error text when it threw.
        var detection = await tracker.WaitForOperationAsync(OperationType.GameDetection);
        var metrics = (GameDetectionMetrics)detection.Metadata!;
        metrics.CompletionStageKey = completionStageKey;
        metrics.CompletionContext = errorDetail is null
            ? null
            : new Dictionary<string, object?> { ["errorDetail"] = errorDetail };
        tracker.Fail(detection.Id, errorDetail ?? completionStageKey);
        await (Task)PhaseContextType.GetMethod("WaitForRepairAsync")!.Invoke(ctx, [run, TimeSpan.FromSeconds(10)])!;

        Assert.True(diskScanStarted, "the scan never reached its disk scan after the detection failed");
        // The finished scan's record carries the failure, so the scan did see its detection fail.
        var finished = ((StateService)PhaseContextType.GetProperty("State")!.GetValue(ctx)!)
            .LoadOperationRepairs().Single(repair => repair.Id == scanId).EvictionScan;
        Assert.Equal((storedError, storedStageKey), (finished?.DetectionError, finished?.DetectionStageKey));
        Assert.True(
            (storedError, storedStageKey) == (restoredFrom?.DetectionError, restoredFrom?.DetectionStageKey),
            $"a restart during the disk scan restores a record whose detection error is "
            + $"'{restoredFrom?.DetectionError}' and detection key is '{restoredFrom?.DetectionStageKey}', "
            + $"so the restored scan ends without saying its game detection failed "
            + $"(expected error '{storedError}', key '{storedStageKey}')");
    }

    private static void SetScanField(CacheReconciliationService scan, string name, object value)
        => typeof(CacheReconciliationService)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(scan, value);
}
