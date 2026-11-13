using LancacheManager.Hubs;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// A user force stops the mapping an Epic sign-in starts while its code waits on Epic's answer to its library read,
/// a step that does not see the stop. The card ends at once, but the code still runs and still holds the sign-in. The
/// Epic status the page reads must keep naming that run until its code ends, and read idle only after.
/// </summary>
public sealed class ForceStoppedEpicSignInStatusTests
{
    private const string ReadWhileTheCodeRuns = "read while the stopped mapping's code still runs";
    private const string ReadAfterTheCodeEnded = "read after the stopped mapping's code ended";

    [Theory]
    [InlineData(ReadWhileTheCodeRuns)]
    [InlineData(ReadAfterTheCodeEnded)]
    public async Task AForceStoppedSignInMapping_ReportsItsRunUntilItsCodeEnds(string when)
    {
        await using var host = await ForceStoppedOwnerHost.CreateAsync();
        using var epic = new EpicHost(host);

        var held = epic.HoldNextLibraryRead();
        await epic.SignInAsync();
        await held.WaitAsync(ForceStoppedOwnerHost.Timeout);
        var stopped = Assert.Single(host.Tracker.GetActiveOperations(OperationType.EpicMapping)).Id;

        await host.ForceStopAsync(stopped);

        if (when == ReadWhileTheCodeRuns)
        {
            var status = epic.Service.GetScheduleStatus();
            Assert.True(status.IsProcessing && status.OperationId == stopped && status.Status != EpicMappingStatus.Idle,
                $"The Epic status {when} answered IsProcessing={status.IsProcessing}, OperationId={status.OperationId}, " +
                $"Status={status.Status}; it must still name the running sign-in mapping {stopped}");
            epic.ReleaseLibraryRead();
            return;
        }

        var ended = host.Notifications.WhenSent(SignalREvents.IntegrationLoginEnded);
        epic.ReleaseLibraryRead();
        await ended.WaitAsync(ForceStoppedOwnerHost.Timeout);
        var after = epic.Service.GetScheduleStatus();
        Assert.True(!after.IsProcessing && after.OperationId is null && after.Status == EpicMappingStatus.Idle,
            $"The Epic status {when} answered IsProcessing={after.IsProcessing}, OperationId={after.OperationId}, " +
            $"Status={after.Status}; it must read idle with no run");
    }
}
