using System.Reflection;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// Every eviction scan runs a full game detection first. When that detection times out, it names
/// its reason by stage key so each reader words it in their own language. The scan card shows the
/// failed detection as "Game cache detection failed: {{errorDetail}}", so the key must reach the
/// card as a key the browser translates, never as the text printed inside that sentence.
/// </summary>
// A registry holding a run starts it when the process-wide downloads-ended event fires, so these
// tests share that event's collection and never see another class raise it mid-test.
[Collection(nameof(DownloadsEndedEventCollection))]
public sealed class EvictionScanTimedOutDetectionTextTests
{
    private const string TimedOutKey = "signalr.gameDetect.error.timedOut";

    [Fact]
    public async Task AScanWhoseDetectionTimedOutDoesNotPrintTheTimeoutKeyAsItsErrorAsync()
    {
        // The detection-phase harness is private to CacheScanDetectionPhaseTests.cs:4745, so it is
        // driven here through reflection rather than copied.
        var phaseType = typeof(CacheScanDetectionPhaseTests).GetNestedType("PhaseContext", BindingFlags.NonPublic)!;
        using var phaseContext = (IDisposable)Activator.CreateInstance(phaseType)!;
        object? Call(string name, params object?[] arguments) => phaseType.GetMethod(name)!.Invoke(phaseContext, arguments);
        var tracker = (CacheScanDetectionPhaseTests.FakeTracker)phaseType.GetProperty("Tracker")!.GetValue(phaseContext)!;
        var notifications = (CacheScanDetectionPhaseTests.RecordingNotifications)phaseType.GetProperty("Notifications")!.GetValue(phaseContext)!;

        var scanId = (Guid)Call("RegisterScan", [null])!;
        // The scan prepares its repair record before its detection phase, which saves a failure there.
        var repairs = (OperationStateService)phaseType
            .GetField("_operationStateService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(phaseContext)!;
        var datasources = (DatasourceService)phaseType.GetProperty("Datasources")!.GetValue(phaseContext)!;
        await repairs.PrepareRepairAsync(new OperationRepair
        {
            Id = scanId,
            Type = OperationType.EvictionScan,
            Name = "Eviction Scan",
            StartedAt = DateTime.UtcNow,
            Sources = [new OperationRepairSource { Datasource = Assert.Single(datasources.GetDatasources()).Name }],
            EvictionScan = new EvictionScanRepair()
        }, CancellationToken.None);
        var phase = (Task)Call("RunPhaseAsync", scanId, CancellationToken.None)!;
        var detection = await tracker.WaitForOperationAsync(OperationType.GameDetection);

        // The detection ends the way GameCacheDetectionService.cs:1076-1079 and its finalize at
        // :1149-1158 end one that timed out.
        var metrics = (GameDetectionMetrics)detection.Metadata!;
        metrics.CompletionStageKey = TimedOutKey;
        metrics.CompletionContext = new Dictionary<string, object?> { ["errorDetail"] = TimedOutKey };
        detection.Message = TimedOutKey;
        tracker.Fail(detection.Id, TimedOutKey);
        await phase.WaitAsync(TimeSpan.FromSeconds(5));

        await (Task)Call("ReportProgressAsync", scanId)!;
        var progress = Assert.IsType<EvictionScanProgress>(await notifications.WaitForAsync(
            SignalREvents.EvictionScanProgress, value => value is EvictionScanProgress { StageKey: "signalr.evictionScan.scanning" }));
        var context = progress.Context ?? new Dictionary<string, object?>();

        // The browser prints this value inside "Game cache detection failed: {{errorDetail}}"
        // (detailMessageFormatters.ts:37-43), as the server's own warning does (UnifiedOperationTracker.cs:1168-1176).
        var printed = context.GetValueOrDefault("detectionError") as string;
        Assert.False(
            printed is not null && LocaleKeys.TranslatedInBothLocales(printed),
            $"the scan card reads \"Game cache detection failed: {printed}\": the detection's stage key is printed as its error text instead of being translated");
        Assert.True(
            context.Values.OfType<string>().Contains(TimedOutKey),
            $"the scan's context ({string.Join(", ", context.Select(entry => $"{entry.Key}={entry.Value}"))}) no longer carries the detection's timeout key, so the card cannot say why its detection failed");
    }
}
