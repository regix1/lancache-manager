using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// Locks the eviction/reconciliation notification contract to the display-flag pattern: lifecycle
/// events are ALWAYS emitted and carry a <c>ShowNotification</c> flag the frontend gates on, rather
/// than the backend suppressing the transport. Also pins the runSilent/scanSilent split so a manual
/// Remove-mode run silences the scan bar while still showing the removal bar.
/// </summary>
public class EvictionNotificationDisplayFlagContractTests
{
    // Mirror of the two-concept split in CacheReconciliationService: runSilent is purely the
    // notification mode for the trigger; the scan phase layers the Remove data mode on top.
    private static bool RunSilent(NotificationMode mode, RunTrigger trigger) =>
        !mode.AllowsTrigger(trigger);

    private static bool ScanSilent(NotificationMode mode, RunTrigger trigger, bool isRemoveMode) =>
        RunSilent(mode, trigger) || isRemoveMode;

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
            Assert.True(ScanSilent(NotificationMode.Silent, trigger, isRemoveMode: false));
            Assert.True(ScanSilent(NotificationMode.Silent, trigger, isRemoveMode: true));
        }
    }

    [Fact]
    public void AllMode_ScheduledRun_IsVisible_ScanAndRemoval()
    {
        // Default reconciliation mode is All: a scheduled tick must surface its scan bar (and, in
        // Remove mode, its removal bar) so the user sees progress they explicitly asked for.
        Assert.False(RunSilent(NotificationMode.All, RunTrigger.Scheduled));
        Assert.False(ScanSilent(NotificationMode.All, RunTrigger.Scheduled, isRemoveMode: false));

        // Removal follows runSilent, so a mode=All scheduled Remove-mode run still shows the removal bar.
        var runSilent = RunSilent(NotificationMode.All, RunTrigger.Scheduled);
        Assert.True(!runSilent, "removal ShowNotification == !runSilent must be true");
    }

    [Fact]
    public void ManualRemoveMode_SilencesScan_ButShowsRemoval()
    {
        // The defect-8 split: a manual Remove-mode run keeps runSilent false (so the removal bar
        // shows) while scanSilent is forced true (the scan bar is hidden).
        var mode = NotificationMode.All;
        const RunTrigger trigger = RunTrigger.Manual;

        var runSilent = RunSilent(mode, trigger);
        var scanSilent = ScanSilent(mode, trigger, isRemoveMode: true);

        Assert.False(runSilent);   // removal ShowNotification = !runSilent = true (visible)
        Assert.True(scanSilent);   // scan ShowNotification = !scanSilent = false (hidden)
    }

    [Fact]
    public void ManualMode_ScheduledTrigger_IsSilent_ButManualTrigger_IsVisible()
    {
        Assert.True(RunSilent(NotificationMode.Manual, RunTrigger.Scheduled));
        Assert.True(RunSilent(NotificationMode.Manual, RunTrigger.Startup));
        Assert.False(RunSilent(NotificationMode.Manual, RunTrigger.Manual));
    }
}
