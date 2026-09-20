using System.Text.Json;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// Locks the cacheSizeScan display-flag opt-in: the scheduled service computes the flag from its
/// effective notification mode + the run trigger, and every lifecycle payload carries that
/// run-stable flag on the wire as camelCase <c>showNotification</c>. Lifecycle events are always
/// emitted; the flag gates only whether the frontend shows the card.
/// </summary>
public class CacheSizeScanNotificationFlagTests
{
    // Mirrors the wire: payloads serialize through the global camelCase policy.
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(NotificationMode.All, RunTrigger.Scheduled, true)]
    [InlineData(NotificationMode.All, RunTrigger.Manual, true)]
    [InlineData(NotificationMode.Manual, RunTrigger.Scheduled, false)]
    [InlineData(NotificationMode.Manual, RunTrigger.Manual, true)]
    [InlineData(NotificationMode.Silent, RunTrigger.Scheduled, false)]
    [InlineData(NotificationMode.Silent, RunTrigger.Manual, false)]
    [InlineData(NotificationMode.Hidden, RunTrigger.Scheduled, false)]
    [InlineData(NotificationMode.Hidden, RunTrigger.Manual, false)]
    public void ScheduledRunStampsFlagFromModeAndTrigger(NotificationMode mode, RunTrigger trigger, bool expected)
    {
        // The exact decision the scheduled service makes once per run before threading it down.
        var show = mode.AllowsTrigger(trigger);
        var hidden = mode == NotificationMode.Hidden;

        var started = new CacheSizeScanStarted(
            StageKey: "signalr.cacheSizeScan.starting",
            OperationId: Guid.NewGuid(),
            ShowNotification: show,
            HideNotification: hidden);

        var progress = new CacheSizeScanProgress(
            OperationId: started.OperationId,
            Status: "running",
            StageKey: "signalr.cacheSizeScan.scanning",
            PercentComplete: 42,
            DirectoriesScanned: 1,
            TotalDirectories: 2,
            TotalFiles: 3,
            TotalBytes: 4,
            ShowNotification: show,
            HideNotification: hidden);

        var complete = new CacheSizeScanComplete(
            Success: true,
            OperationId: started.OperationId,
            StageKey: "signalr.cacheSizeScan.complete",
            TotalFiles: 3,
            TotalBytes: 4,
            ShowNotification: show,
            HideNotification: hidden);

        Assert.Equal(expected, started.ShowNotification);
        Assert.Equal(expected, progress.ShowNotification);
        Assert.Equal(expected, complete.ShowNotification);
        Assert.Equal(hidden, started.HideNotification);
        Assert.Equal(hidden, progress.HideNotification);
        Assert.Equal(hidden, complete.HideNotification);
    }

    [Fact]
    public void FlagSerializesAsCamelCaseOnEveryLifecyclePayload()
    {
        var operationId = Guid.NewGuid();

        var startedJson = JsonSerializer.Serialize(
            new CacheSizeScanStarted("signalr.cacheSizeScan.starting", operationId, ShowNotification: false),
            WireOptions);
        var progressJson = JsonSerializer.Serialize(
            new CacheSizeScanProgress(operationId, "running", "signalr.cacheSizeScan.scanning", 10, 0, 0, 0, 0, ShowNotification: false),
            WireOptions);
        var completeJson = JsonSerializer.Serialize(
            new CacheSizeScanComplete(true, operationId, "signalr.cacheSizeScan.complete", 0, 0, ShowNotification: true),
            WireOptions);

        Assert.Contains("\"showNotification\":false", startedJson);
        Assert.Contains("\"showNotification\":false", progressJson);
        Assert.Contains("\"showNotification\":true", completeJson);
        Assert.Contains("\"hideNotification\":false", startedJson);
        Assert.Contains("\"hideNotification\":false", progressJson);
        Assert.Contains("\"hideNotification\":false", completeJson);
    }

    [Fact]
    public void FlagDefaultsToVisibleForNonScheduledCallers()
    {
        // Callers that do not opt into gating (e.g. a direct manual refresh) leave the flag at its
        // visible default rather than accidentally suppressing the card.
        var started = new CacheSizeScanStarted("signalr.cacheSizeScan.starting", Guid.NewGuid());
        Assert.True(started.ShowNotification);
        Assert.False(started.HideNotification);
    }

    [Fact]
    public void HiddenModeStaysDistinctFromSilentMode()
    {
        var silent = new RunNotice(NotificationMode.Silent, RunTrigger.Scheduled);
        var hidden = new RunNotice(NotificationMode.Hidden, RunTrigger.Scheduled);

        Assert.False(silent.ShowNotification);
        Assert.False(silent.HideNotification);
        Assert.False(hidden.ShowNotification);
        Assert.True(hidden.HideNotification);
    }
}
