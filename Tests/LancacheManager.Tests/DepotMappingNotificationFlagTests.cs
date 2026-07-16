using System.Text.Json;
using LancacheManager.Models;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;

namespace LancacheManager.Tests;

/// <summary>
/// Locks the depotMapping display-flag opt-in: the scheduling dispatch computes the flag from the
/// effective notification mode + the run trigger once per run, and the terminal DepotMappingComplete
/// payload carries that run-stable flag on the wire as camelCase <c>showNotification</c>. The event is
/// always emitted; the flag gates only whether the frontend shows the card.
/// </summary>
public class DepotMappingNotificationFlagTests
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(NotificationMode.All, RunTrigger.Scheduled, true)]
    [InlineData(NotificationMode.Manual, RunTrigger.Scheduled, false)]
    [InlineData(NotificationMode.Manual, RunTrigger.Manual, true)]
    [InlineData(NotificationMode.Silent, RunTrigger.Scheduled, false)]
    public void ScheduledRunStampsTerminalFlagFromModeAndTrigger(NotificationMode mode, RunTrigger trigger, bool expected)
    {
        // The exact decision the scheduling dispatch makes before threading it into the emit sites.
        var show = mode.AllowsTrigger(trigger);

        var complete = new DepotMappingComplete(
            OperationId: Guid.NewGuid(),
            Success: true,
            Message: "Depot mapping completed",
            TotalMappings: 5,
            DownloadsUpdated: 2,
            ScanMode: DepotScanMode.Full,
            IsLoggedOn: true,
            Timestamp: DateTime.UtcNow,
            ShowNotification: show);

        Assert.Equal(expected, complete.ShowNotification);
    }

    [Fact]
    public void FlagSerializesAsCamelCaseOnTerminalPayload()
    {
        var silent = JsonSerializer.Serialize(
            new DepotMappingComplete(Guid.NewGuid(), Success: true, Message: "done", ShowNotification: false),
            WireOptions);
        var visible = JsonSerializer.Serialize(
            new DepotMappingComplete(Guid.NewGuid(), Success: true, Message: "done", ShowNotification: true),
            WireOptions);

        Assert.Contains("\"showNotification\":false", silent);
        Assert.Contains("\"showNotification\":true", visible);
    }

    [Fact]
    public void FlagDefaultsToVisible()
    {
        var complete = new DepotMappingComplete(Guid.NewGuid(), Success: true, Message: "done");
        Assert.True(complete.ShowNotification);
    }

    /// <summary>
    /// A direct (REST) rebuild goes through TryStartRebuild without the scheduling dispatch, so it
    /// stamps the display flag by evaluating its default <see cref="RunTrigger.Manual"/> trigger
    /// against the effective notification mode: visible in Manual mode, hidden in Silent mode, and
    /// always visible in All. This is the decision TryStartRebuild makes for a user-initiated rebuild,
    /// so it stays correct regardless of the flag a prior scheduled run left behind.
    /// </summary>
    [Theory]
    [InlineData(NotificationMode.All, true)]
    [InlineData(NotificationMode.Manual, true)]
    [InlineData(NotificationMode.Silent, false)]
    public void DirectRebuildEvaluatesManualTriggerAgainstMode(NotificationMode mode, bool expected)
    {
        // TryStartRebuild's default trigger for a direct rebuild request.
        var show = mode.AllowsTrigger(RunTrigger.Manual);

        Assert.Equal(expected, show);
    }
}
