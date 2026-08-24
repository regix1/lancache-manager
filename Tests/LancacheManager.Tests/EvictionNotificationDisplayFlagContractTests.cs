using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// Locks the eviction/reconciliation notification contract to the display-flag pattern: lifecycle
/// events are ALWAYS emitted and carry a <c>ShowNotification</c> flag the frontend gates on, rather
/// than the backend suppressing the transport. Scan and Remove-mode cleanup both follow the
/// schedule notification mode; evicted-data mode does not hide the scan card.
/// </summary>
public class EvictionNotificationDisplayFlagContractTests
{
    // Mirror of CacheReconciliationService: scan display silence is the schedule notification
    // mode for the trigger. Evicted-data mode is not part of this gate.
    private static bool RunSilent(NotificationMode mode, RunTrigger trigger) =>
        !mode.AllowsTrigger(trigger);

    private static bool ScanSilent(NotificationMode mode, RunTrigger trigger) =>
        RunSilent(mode, trigger);

    [Fact]
    public void EvictionScanRecords_DefaultShowNotification_IsTrue()
    {
        var started = new EvictionScanStarted("signalr.evictionScan.scanning", Guid.NewGuid());
        var progress = new EvictionScanProgress(Guid.NewGuid(), "running", "signalr.evictionScan.progress", 42.0, 1, 2, 0, 0);
        var complete = new EvictionScanComplete(true, Guid.NewGuid(), "signalr.evictionScan.complete", 2, 0, 0);

        Assert.True(started.ShowNotification);
        Assert.True(progress.ShowNotification);
        Assert.True(complete.ShowNotification);
    }

    [Fact]
    public void EvictionScanRecords_HonorExplicitSilentFlag()
    {
        var started = new EvictionScanStarted("signalr.evictionScan.scanning", Guid.NewGuid(), ShowNotification: false);
        var progress = new EvictionScanProgress(Guid.NewGuid(), "running", "signalr.evictionScan.progress", 42.0, 1, 2, 0, 0, ShowNotification: false);
        var complete = new EvictionScanComplete(true, Guid.NewGuid(), "signalr.evictionScan.complete", 2, 0, 0, ShowNotification: false);

        Assert.False(started.ShowNotification);
        Assert.False(progress.ShowNotification);
        Assert.False(complete.ShowNotification);
    }

    [Fact]
    public void EvictionRemovalRecords_DefaultShowNotification_IsTrue()
    {
        var started = new EvictionRemovalStarted("signalr.evictionRemove.starting.bulk", Guid.NewGuid());
        var progress = new EvictionRemovalProgress(Guid.NewGuid(), "running", "signalr.evictionRemove.removingLogs", 60.0, 0, 0);
        var complete = new EvictionRemovalComplete(true, Guid.NewGuid(), "signalr.evictionRemove.complete", 3, 4);

        Assert.True(started.ShowNotification);
        Assert.True(progress.ShowNotification);
        Assert.True(complete.ShowNotification);
    }

    [Fact]
    public void EvictionRemovalRecords_HonorExplicitSilentFlag()
    {
        var started = new EvictionRemovalStarted("signalr.evictionRemove.starting.bulk", Guid.NewGuid(), ShowNotification: false);
        var progress = new EvictionRemovalProgress(Guid.NewGuid(), "running", "signalr.evictionRemove.removingLogs", 60.0, 0, 0, ShowNotification: false);
        var complete = new EvictionRemovalComplete(true, Guid.NewGuid(), "signalr.evictionRemove.complete", 3, 4, ShowNotification: false);

        Assert.False(started.ShowNotification);
        Assert.False(progress.ShowNotification);
        Assert.False(complete.ShowNotification);
    }

    [Fact]
    public void EpicMappingComplete_DefaultShowNotification_IsTrue()
    {
        var complete = new SignalRNotifications.EpicMappingComplete(
            OperationId: Guid.NewGuid(),
            Success: true,
            Status: OperationStatus.Completed,
            StageKey: "signalr.epicMapping.completed");

        Assert.True(complete.ShowNotification);
    }

    [Fact]
    public void SilentMode_RunAndScanBothSilent_OnEveryTrigger()
    {
        foreach (var trigger in Enum.GetValues<RunTrigger>())
        {
            Assert.True(RunSilent(NotificationMode.Silent, trigger));
            Assert.True(ScanSilent(NotificationMode.Silent, trigger));
        }
    }

    [Fact]
    public void AllMode_ScheduledRun_IsVisible_ScanAndRemoval()
    {
        // Default reconciliation mode is All: a scheduled tick must surface its scan bar (and, in
        // Remove mode, its removal bar) so the user sees progress they explicitly asked for.
        Assert.False(RunSilent(NotificationMode.All, RunTrigger.Scheduled));
        Assert.False(ScanSilent(NotificationMode.All, RunTrigger.Scheduled));

        // Removal follows the same schedule mode, so a mode=All scheduled Remove-mode run shows both.
        var runSilent = RunSilent(NotificationMode.All, RunTrigger.Scheduled);
        Assert.True(!runSilent, "removal ShowNotification == !runSilent must be true");
    }

    [Fact]
    public void ManualRemoveMode_ShowsScanAndRemoval()
    {
        // Evicted-data Remove does not hide the scan card. Both bars follow the schedule mode.
        var mode = NotificationMode.All;
        const RunTrigger trigger = RunTrigger.Manual;

        var runSilent = RunSilent(mode, trigger);
        var scanSilent = ScanSilent(mode, trigger);

        Assert.False(runSilent);
        Assert.False(scanSilent);
    }

    [Fact]
    public void ManualMode_ScheduledTrigger_IsSilent_ButManualTrigger_IsVisible()
    {
        Assert.True(RunSilent(NotificationMode.Manual, RunTrigger.Scheduled));
        Assert.True(RunSilent(NotificationMode.Manual, RunTrigger.Startup));
        Assert.False(RunSilent(NotificationMode.Manual, RunTrigger.Manual));
    }
}
