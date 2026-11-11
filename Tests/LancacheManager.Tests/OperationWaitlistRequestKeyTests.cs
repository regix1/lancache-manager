using LancacheManager.Controllers;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A request joins a waiting one only when its request key equals the waiting one's; one that asks for other
/// work waits on its own card and runs after. A key that does not belong to the job type is refused.
/// </summary>
public sealed class OperationWaitlistRequestKeyTests
{
    [Fact]
    public async Task ResetsOfOtherTablesWaitOnTheirOwnCardsAsync()
    {
        var (tracker, queue) = CreateQueue();
        // The reset is never started: the clear stays in the way until the test ends.
        var controller = new DatabaseController(null!, NullLogger<DatabaseController>.Instance, queue);
        var blocker = tracker.RegisterOperation(OperationType.CacheClearing, "Cache Clear (All)", new CancellationTokenSource());

        var downloads = await ResetTablesAsync(controller, "Downloads");
        var logEntries = await ResetTablesAsync(controller, "LogEntries");
        var everything = Waiting(await controller.ResetDatabaseAsync(CancellationToken.None));
        var downloadsAgain = await ResetTablesAsync(controller, "Downloads");
        var everythingAgain = Waiting(await controller.ResetDatabaseAsync(CancellationToken.None));

        Assert.Equal(3, new[] { downloads.OperationId, logEntries.OperationId, everything.OperationId }.Distinct().Count());
        // The same tables, or the whole database again, is the same work: one card.
        Assert.Equal(downloads.OperationId, downloadsAgain.OperationId);
        Assert.Equal(everything.OperationId, everythingAgain.OperationId);
        Assert.Equal(3, tracker.GetWaitingOperations().Count(operation => operation.Type == OperationType.DatabaseReset));

        foreach (var waiting in tracker.GetWaitingOperations().ToList())
            tracker.CancelOperation(waiting.Id);
        tracker.CompleteOperation(blocker, success: true);
    }

    [Fact]
    public async Task TheSameTablesInAnotherOrderJoinTheWaitingResetAsync()
    {
        var (tracker, queue) = CreateQueue();
        var controller = new DatabaseController(null!, NullLogger<DatabaseController>.Instance, queue);
        var blocker = tracker.RegisterOperation(OperationType.CacheClearing, "Cache Clear (All)", new CancellationTokenSource());

        var first = await ResetTablesAsync(controller, "Downloads", "LogEntries");
        var reordered = await ResetTablesAsync(controller, "LogEntries", "Downloads");
        var fewer = await ResetTablesAsync(controller, "LogEntries");

        Assert.Equal(first.OperationId, reordered.OperationId);
        Assert.NotEqual(first.OperationId, fewer.OperationId);
        Assert.Equal(2, tracker.GetWaitingOperations().Count(operation => operation.Type == OperationType.DatabaseReset));

        foreach (var waiting in tracker.GetWaitingOperations().ToList())
            tracker.CancelOperation(waiting.Id);
        tracker.CompleteOperation(blocker, success: true);
    }

    [Fact]
    public void KeysCompareAsValues()
    {
        Assert.Equal(DatabaseResetRequestKey.ForTables(["Downloads", "LogEntries"]),
            DatabaseResetRequestKey.ForTables(["LogEntries", "Downloads"]));
        Assert.Equal(DatabaseResetRequestKey.ForTables(["Downloads", "LogEntries"]).GetHashCode(),
            DatabaseResetRequestKey.ForTables(["LogEntries", "Downloads"]).GetHashCode());
        Assert.NotEqual(DatabaseResetRequestKey.EveryTable, DatabaseResetRequestKey.ForTables(["Downloads"]));
        Assert.Equal(new UnmappedFileRemovalRequestKey(new UnmappedFilesLimits(10, 20)),
            new UnmappedFileRemovalRequestKey(new UnmappedFilesLimits(10, 20)));
        Assert.NotEqual(new UnmappedFileRemovalRequestKey(new UnmappedFilesLimits(10, 20)),
            new UnmappedFileRemovalRequestKey(new UnmappedFilesLimits(10, 21)));
        Assert.Equal(DepotMappingRequestKey.Crawl(incrementalOnly: true), new DepotMappingRequestKey(DepotMappingWork.IncrementalCrawl));
        Assert.NotEqual(DepotMappingRequestKey.Crawl(incrementalOnly: true), DepotMappingRequestKey.Crawl(incrementalOnly: false));
        Assert.NotEqual(new CorruptionDetectionRequestKey(null, 3, 7), new CorruptionDetectionRequestKey(null, 3, 8));
        // Two keys of other job types are never equal, even with nothing in them that differs.
        Assert.NotEqual<OperationRequestKey>(OperationRequestKey.Plain, new GameImageFetchRequestKey(false));
    }

    [Fact]
    public async Task ADepotMappingRequestWithThePlainKeyIsRefusedAsync()
    {
        var (_, queue) = CreateQueue();
        var started = false;

        var error = await Assert.ThrowsAsync<ArgumentException>(() => queue.EnqueueAsync(
            OperationType.DepotMapping, ConflictScope.Bulk(), "Depot Mapping",
            () => { started = true; return Task.FromResult<Guid?>(Guid.NewGuid()); },
            CancellationToken.None, OperationRequestKey.Plain));

        Assert.Equal("requestKey", error.ParamName);
        Assert.False(started);
    }

    [Fact]
    public async Task APlainJobGivenATypedKeyIsRefusedAsync()
    {
        var (_, queue) = CreateQueue();
        var started = false;

        var error = await Assert.ThrowsAsync<ArgumentException>(() => queue.StartUnlessBusyAsync(
            OperationType.EvictionScan, ConflictScope.Bulk(),
            () => { started = true; return Task.FromResult<Guid?>(Guid.NewGuid()); },
            CancellationToken.None, DepotMappingRequestKey.Crawl(incrementalOnly: false)));

        Assert.Equal("requestKey", error.ParamName);
        Assert.False(started);
    }

    private static (UnifiedOperationTracker Tracker, OperationWaitlistService Queue) CreateQueue()
    {
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance), NullLogger<UnifiedOperationTracker>.Instance);
        var checker = OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance);
        return (tracker, new OperationWaitlistService(tracker, checker, NullLogger<OperationWaitlistService>.Instance));
    }

    private static async Task<OperationStartResponse> ResetTablesAsync(DatabaseController controller, params string[] tables) =>
        Waiting(await controller.ResetSelectedTablesAsync(new ResetTablesRequest { Tables = [.. tables] }, CancellationToken.None));

    private static OperationStartResponse Waiting(IActionResult result)
    {
        var response = Assert.IsType<OperationStartResponse>(Assert.IsType<AcceptedResult>(result).Value);
        Assert.Equal(OperationStartStatus.Waiting, response.Status);
        return response;
    }
}
