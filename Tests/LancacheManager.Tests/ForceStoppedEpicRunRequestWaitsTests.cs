using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// A user force stops an Epic run while its code waits on Epic's answer to its library read, a step that does not see
/// the stop, so the run's card has ended while its code still runs. A job asked for in that time through the operation
/// queue must wait on a card and start once the stopped run's code has ended: an Epic catalog refresh asked for again,
/// and a database reset asked for while a force-stopped sign-in mapping still runs its cancel branch, which ends with
/// the sign-in's release.
/// </summary>
public sealed class ForceStoppedEpicRunRequestWaitsTests
{
    [Fact]
    public async Task AnEpicRefreshAskedForWhileAForceStoppedRefreshStillRuns_WaitsOnACardAndRunsAfterIt()
    {
        await using var host = await ForceStoppedOwnerHost.CreateAsync();
        using var epic = new EpicHost(host);
        await epic.SignInAndWaitForTheMappingAsync();

        var held = epic.HoldNextLibraryRead();
        Assert.True(epic.Service.TryStartRefresh(), "The first Epic refresh did not start");
        await held.WaitAsync(ForceStoppedOwnerHost.Timeout);
        var stopped = Assert.Single(host.Tracker.GetActiveOperations(OperationType.EpicMapping)).Id;

        await AskAgainWhileTheStoppedRunRunsAsync(host, epic, "Epic refresh", stopped,
            () => host.AskThroughTheQueueAsync(
                OperationType.EpicMapping, ConflictScope.Bulk(), "Epic Mapping",
                async () =>
                {
                    var read = epic.NextLibraryRead();
                    if (!epic.Service.TryStartRefresh()) return null;
                    // The refresh registers its card on its own thread, before its library read.
                    await read.WaitAsync(ForceStoppedOwnerHost.Timeout);
                    return host.Tracker.GetActiveOperations(OperationType.EpicMapping).Single(operation => operation.Id != stopped).Id;
                }));
    }

    [Fact]
    public async Task ADatabaseResetAskedForWhileAForceStoppedSignInMappingStillRuns_WaitsOnACardUntilTheSignInIsReleased()
    {
        await using var host = await ForceStoppedOwnerHost.CreateAsync();
        using var epic = new EpicHost(host);

        // The sign-in is saved, then its mapping reads the library in the background.
        var held = epic.HoldNextLibraryRead();
        await epic.SignInAsync();
        await held.WaitAsync(ForceStoppedOwnerHost.Timeout);
        var stopped = Assert.Single(host.Tracker.GetActiveOperations(OperationType.EpicMapping)).Id;

        string? signInAtStart = null;
        await AskAgainWhileTheStoppedRunRunsAsync(host, epic, "database reset", stopped,
            () => host.AskThroughTheQueueAsync(
                OperationType.DatabaseReset, ConflictScope.Bulk(), "Database Reset",
                () =>
                {
                    signInAtStart = epic.SignInState();
                    return Task.FromResult<Guid?>(host.Tracker.RegisterOperation(
                        OperationType.DatabaseReset, "Database Reset", new CancellationTokenSource()));
                },
                DatabaseResetRequestKey.EveryTable));
        Assert.True(signInAtStart == EpicHost.Released,
            $"The waiting database reset started while the force-stopped sign-in still held {signInAtStart}");
    }

    /// <summary>
    /// With the stopped run's code held in its library read: force stops the run, asks for the job, asserts the
    /// request waits on a card, lets the library read answer, and asserts the waiting card's start then runs.
    /// </summary>
    private static async Task AskAgainWhileTheStoppedRunRunsAsync(
        ForceStoppedOwnerHost host, EpicHost epic, string job, Guid stopped,
        Func<Task<(string Outcome, Task<Guid?> Started)>> askAgain)
    {
        await host.ForceStopAsync(stopped);

        var (outcome, started) = await askAgain();
        ForceStoppedOwnerHost.AssertWaits($"The {job}", "Epic run", outcome);

        epic.ReleaseLibraryRead();
        await ForceStoppedOwnerHost.AssertRunsAfterAsync(job, "Epic run", stopped, started);
    }
}
