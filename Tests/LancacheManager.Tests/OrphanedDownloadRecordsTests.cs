using LancacheManager.Core.Cache;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

/// <summary>
/// Download records with no log entries left are listed for the user to remove by hand: grouped
/// under the title the Downloads page would show, with the rows the log processor may still be
/// filling in, evicted rows, and rows that have history all left out.
/// </summary>
public sealed class OrphanedDownloadRecordsTests
{
    private const string ProductId = "C19N0723PHFL";

    [Fact]
    public async Task ListsSettledRowsWithNoLogEntriesGroupedUnderTheirResolvedTitle()
    {
        var options = NewOptions();
        await using (var seed = new AppDbContext(options))
        {
            seed.XboxGameMappings.Add(new XboxGameMapping
            {
                ProductId = ProductId,
                Title = "Call of Duty®: Black Ops 4",
                ImageUrl = "https://store-images.microsoft.com/image/apps.1.jpg"
            });
            seed.Downloads.AddRange(
                Row("xbox", ProductId, hitBytes: 1000),
                Row("xbox", ProductId, hitBytes: 500),
                Row("xbox", ProductId, isActive: true),
                Row("xbox", ProductId, endedAgo: TimeSpan.FromMinutes(1)),
                Row("xbox", ProductId, isEvicted: true));
            var backed = Row("wsus", null);
            seed.Downloads.Add(backed);
            await seed.SaveChangesAsync();
            seed.LogEntries.Add(LogLine(backed.Id));
            await seed.SaveChangesAsync();
        }

        await using var db = new AppDbContext(options);
        var groups = await OrphanedDownloadRecords.ListAsync(db, CancellationToken.None);

        // Only the two settled, inactive, non-evicted rows with no history make the list; the
        // stored name is null, so the title comes from the product id like on the Downloads page.
        var group = Assert.Single(groups);
        Assert.Equal("xbox", group.Service);
        Assert.Equal("Call of Duty®: Black Ops 4", group.GameName);
        Assert.Equal(2, group.DownloadCount);
        Assert.Equal(1500, group.TotalBytes);
        Assert.Equal(2, group.DownloadIds.Count);
    }

    [Fact]
    public async Task RemovesOnlyTheChosenRowsThatStillHaveNoLogEntries()
    {
        var options = NewOptions();
        long orphanId;
        long otherOrphanId;
        long backedId;
        await using (var seed = new AppDbContext(options))
        {
            var orphan = Row("xbox", ProductId);
            var otherOrphan = Row("riot", null);
            var backed = Row("wsus", null);
            seed.Downloads.AddRange(orphan, otherOrphan, backed);
            await seed.SaveChangesAsync();
            orphanId = orphan.Id;
            otherOrphanId = otherOrphan.Id;
            backedId = backed.Id;
            seed.LogEntries.Add(LogLine(backedId));
            await seed.SaveChangesAsync();
        }

        await using var db = new AppDbContext(options);
        var removed = await OrphanedDownloadRecords.RemoveAsync(db, new[] { orphanId, backedId }, CancellationToken.None);

        // The backed row was asked for too and stays: it has history, so it is a real download.
        Assert.Equal(1, removed);
        var remaining = await db.Downloads.Select(d => d.Id).OrderBy(id => id).ToListAsync();
        Assert.Equal(new[] { otherOrphanId, backedId }.OrderBy(id => id), remaining);
    }

    private static DbContextOptions<AppDbContext> NewOptions()
        => new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"orphaned_downloads_{Guid.NewGuid():N}")
            .Options;

    private static Download Row(
        string service,
        string? xboxProductId,
        bool isActive = false,
        bool isEvicted = false,
        TimeSpan? endedAgo = null,
        long hitBytes = 100)
        => new()
        {
            Service = service,
            ClientIp = "10.0.0.5",
            StartTimeUtc = DateTime.UtcNow.AddHours(-2),
            EndTimeUtc = DateTime.UtcNow - (endedAgo ?? TimeSpan.FromHours(1)),
            CacheHitBytes = hitBytes,
            IsActive = isActive,
            IsEvicted = isEvicted,
            XboxProductId = xboxProductId,
            LastUrl = "http://assets1.xboxlive.com/some/manifest"
        };

    private static LogEntryRecord LogLine(long downloadId)
        => new()
        {
            Timestamp = DateTime.UtcNow,
            ClientIp = "10.0.0.5",
            Service = "wsus",
            Method = "GET",
            Url = "/some/object",
            CacheStatus = "HIT",
            DownloadId = downloadId
        };
}
