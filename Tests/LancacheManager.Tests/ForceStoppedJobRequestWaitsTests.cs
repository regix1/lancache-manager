using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// A user force stops a job while its code sits in a step that does not see the stop, so the job's card has
/// ended while its code still runs. The same job asked for again in that time, through the operation queue
/// as the job's button asks, must wait on a card and start once the stopped job's code has ended: it must
/// not start beside the stopped job, and it must not be dropped with nothing shown.
/// </summary>
public sealed class ForceStoppedJobRequestWaitsTests
{
    [Fact]
    public async Task ACacheClearAskedForWhileAForceStoppedClearStillRuns_WaitsOnACardAndRunsAfterIt()
    {
        await using var host = await ForceStoppedOwnerHost.CreateAsync();
        var clearing = host.Create<CacheClearingService>();

        // The clear's first step after its start announces it; that send does not see the stop.
        host.Notifications.Hold(SignalREvents.CacheClearingStarted);
        var stopped = await clearing.StartCacheClearAsync()
            ?? throw new InvalidOperationException("The first cache clear did not start");

        await AskAgainWhileTheStoppedJobRunsAsync(host, "cache clear", stopped,
            () => host.AskThroughTheQueueAsync(
                OperationType.CacheClearing, ConflictScope.Bulk(), "Cache Clear (All)", () => clearing.StartCacheClearAsync()));
    }

    [Fact]
    public async Task ADatabaseResetAskedForWhileAForceStoppedResetStillRuns_WaitsOnACardAndRunsAfterIt()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var host = await ForceStoppedOwnerHost.CreateAsync();
        await using var context = database.Factory.CreateDbContext();
        var reset = host.Create<DatabaseService>(context, database.Factory);
        List<string> tables = ["GameImages"];

        // The reset's first progress report is sent from its worker; that send does not see the stop.
        host.Notifications.Hold(SignalREvents.DatabaseResetProgress);
        var stopped = reset.StartResetAsync(tables);

        await AskAgainWhileTheStoppedJobRunsAsync(host, "database reset", stopped,
            () => host.AskThroughTheQueueAsync(
                OperationType.DatabaseReset, ConflictScope.Bulk(), "Database Reset",
                () => Task.FromResult<Guid?>(reset.StartResetAsync(tables)), DatabaseResetRequestKey.ForTables(tables)));
    }

    [Fact]
    public async Task AGameDetectionAskedForWhileAForceStoppedDetectionStillRuns_WaitsOnACardAndRunsAfterIt()
    {
        await using var host = await ForceStoppedOwnerHost.CreateAsync();
        var detection = host.Create<GameCacheDetectionService>();

        // The detection announces its start after it registered; that send does not see the stop.
        host.Notifications.Hold(SignalREvents.GameDetectionStarted);
        var starting = detection.StartDetectionAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual), incremental: false);
        await host.Notifications.Entered.Task.WaitAsync(ForceStoppedOwnerHost.Timeout);
        var stopped = Assert.Single(host.Tracker.GetActiveOperations(OperationType.GameDetection)).Id;

        await AskAgainWhileTheStoppedJobRunsAsync(host, "game detection", stopped,
            () => host.AskThroughTheQueueAsync(
                OperationType.GameDetection, ConflictScope.Bulk(), "Game Detection",
                () => detection.StartDetectionAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual), incremental: false),
                new GameDetectionRequestKey(DetectionScanType.Full)));
        await starting.WaitAsync(ForceStoppedOwnerHost.Timeout);
    }

    [Fact]
    public async Task ACacheSizeScanAskedForWhileAForceStoppedScanStillRuns_WaitsOnACardAndRunsAfterIt()
    {
        await using var host = await ForceStoppedOwnerHost.CreateAsync();
        var management = host.Create<CacheManagementService>();
        var notice = new RunNotice(NotificationMode.All, RunTrigger.Manual);

        // The scan announces its start after it registered; that send does not see the stop.
        host.Notifications.Hold(SignalREvents.CacheSizeScanStarted);
        _ = management.StartCacheSizeScanInBackgroundAsync(notice);
        await host.Notifications.Entered.Task.WaitAsync(ForceStoppedOwnerHost.Timeout);
        var stopped = Assert.Single(host.Tracker.GetActiveOperations(OperationType.CacheSizeScan)).Id;

        await AskAgainWhileTheStoppedJobRunsAsync(host, "cache size scan", stopped,
            () => host.AskThroughTheQueueAsync(
                OperationType.CacheSizeScan, ConflictScope.Bulk(), "Cache File Scan",
                () => management.StartCacheSizeScanInBackgroundAsync(notice)));
    }

    /// <summary>
    /// With the stopped job's code held in its notification send: force stops the job, asks for it again,
    /// asserts the request waits on a card, lets the stopped code go on, and asserts the waiting card's start
    /// then runs a new job.
    /// </summary>
    private static async Task AskAgainWhileTheStoppedJobRunsAsync(
        ForceStoppedOwnerHost host, string job, Guid stopped,
        Func<Task<(string Outcome, Task<Guid?> Started)>> askAgain)
    {
        var timeout = ForceStoppedOwnerHost.Timeout;
        await host.Notifications.Entered.Task.WaitAsync(timeout);
        await host.ForceStopAsync(stopped);

        var (outcome, started) = await askAgain();
        ForceStoppedOwnerHost.AssertWaits($"A {job}", job, outcome);

        host.Notifications.Release();
        await ForceStoppedOwnerHost.AssertRunsAfterAsync(job, job, stopped, started);
    }
}
