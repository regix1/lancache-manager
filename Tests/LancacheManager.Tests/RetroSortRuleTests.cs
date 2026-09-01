using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// One test per value the Downloads sort dropdown can send, then the rules that decide the order
/// when two of the reader's settings meet. Each test names the whole page and not just its first
/// row: a sort that leads with the right row and loses the rest still looks right at a glance.
/// The four seeded groups are chosen so all nine sorts answer with a different order, so a token
/// wired to the wrong branch cannot pass by luck.
/// </summary>
public class RetroSortRuleTests
{
    private static readonly DateTime Day = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Newest first, on the group's latest end time. This is the value the endpoint falls back to,
    /// and the browser's dropdown never sends the word.
    /// </summary>
    [Fact]
    public async Task LatestOrdersByTheGroupsLatestEndTime()
    {
        var page = await GetPageAsync(new RetroDownloadQuery { Sort = "latest" });

        Assert.Equal(["Apollo", "Zeus", "Neptune", "Mercury"], NamesOf(page));
    }

    /// <summary>
    /// The word the browser's dropdown actually sends for the same order. Both strings have to land
    /// on the one branch or the page a reader lands on differs from the page the dropdown asks for.
    /// </summary>
    [Fact]
    public async Task RecentOrdersByTheGroupsLatestEndTime()
    {
        var page = await GetPageAsync(new RetroDownloadQuery { Sort = "recent" });

        Assert.Equal(["Apollo", "Zeus", "Neptune", "Mercury"], NamesOf(page));
    }

    /// <summary>
    /// The two words are one sort. Pinned as a comparison as well as by the two orders above, so
    /// splitting them into separate branches fails here even if both branches still order on time.
    /// </summary>
    [Fact]
    public async Task LatestAndRecentAreTheSameSort()
    {
        var latest = await GetPageAsync(new RetroDownloadQuery { Sort = "latest" });
        var recent = await GetPageAsync(new RetroDownloadQuery { Sort = "recent" });

        Assert.Equal(NamesOf(latest), NamesOf(recent));
    }

    /// <summary>
    /// Oldest first, on the group's earliest start. Neptune started before every other group and
    /// ended before two of them, so this order is not the reverse of the latest one.
    /// </summary>
    [Fact]
    public async Task OldestOrdersByTheGroupsEarliestStart()
    {
        var page = await GetPageAsync(new RetroDownloadQuery { Sort = "oldest" });

        Assert.Equal(["Neptune", "Zeus", "Mercury", "Apollo"], NamesOf(page));
    }

    /// <summary>
    /// Biggest group first, on the bytes the whole group moved.
    /// </summary>
    [Fact]
    public async Task LargestOrdersByTheGroupsTotalBytes()
    {
        var page = await GetPageAsync(new RetroDownloadQuery { Sort = "largest" });

        Assert.Equal(["Zeus", "Apollo", "Neptune", "Mercury"], NamesOf(page));
    }

    /// <summary>
    /// Smallest group first, the same column the other way up.
    /// </summary>
    [Fact]
    public async Task SmallestOrdersByTheGroupsTotalBytes()
    {
        var page = await GetPageAsync(new RetroDownloadQuery { Sort = "smallest" });

        Assert.Equal(["Mercury", "Neptune", "Apollo", "Zeus"], NamesOf(page));
    }

    /// <summary>
    /// Best cache hit rate first. The seeded hit rates deliberately do not follow the byte totals,
    /// so this cannot be confused with the largest sort.
    /// </summary>
    [Fact]
    public async Task EfficiencyOrdersByTheGroupsCacheHitPercent()
    {
        var page = await GetPageAsync(new RetroDownloadQuery { Sort = "efficiency" });

        Assert.Equal(["Apollo", "Neptune", "Zeus", "Mercury"], NamesOf(page));
    }

    /// <summary>
    /// Worst cache hit rate first, the same column the other way up.
    /// </summary>
    [Fact]
    public async Task EfficiencyLowOrdersByTheGroupsCacheHitPercent()
    {
        var page = await GetPageAsync(new RetroDownloadQuery { Sort = "efficiency-low" });

        Assert.Equal(["Mercury", "Zeus", "Neptune", "Apollo"], NamesOf(page));
    }

    /// <summary>
    /// Most downloads first, on how many downloads the group stands for rather than their size.
    /// </summary>
    [Fact]
    public async Task SessionsOrdersByHowManyDownloadsTheGroupHolds()
    {
        var page = await GetPageAsync(new RetroDownloadQuery { Sort = "sessions" });

        Assert.Equal(["Zeus", "Apollo", "Mercury", "Neptune"], NamesOf(page));
    }

    /// <summary>
    /// By name, ignoring case. Every group here carries a real title, so the name on the wire is
    /// also the title on screen.
    /// <see cref="RetroGroupedPagingTests.AlphabeticalOrdersTheLabelTheBrowserShows"/> covers the
    /// one row where the two differ, a nameless service.
    /// </summary>
    [Fact]
    public async Task AlphabeticalOrdersByTheNameTheRowCarries()
    {
        var page = await GetPageAsync(new RetroDownloadQuery { Sort = "alphabetical" });

        Assert.Equal(["Apollo", "Mercury", "Neptune", "Zeus"], NamesOf(page));
    }

    /// <summary>
    /// By service name, then newest first inside each service, so a reader scanning one service
    /// still gets that service's newest rows at the top of its run.
    /// </summary>
    [Fact]
    public async Task ServiceOrdersByServiceNameThenNewestFirst()
    {
        var page = await GetPageAsync(new RetroDownloadQuery { Sort = "service" });

        Assert.Equal(["Mercury", "Neptune", "Zeus", "Apollo"], NamesOf(page));
    }

    /// <summary>
    /// The service sort orders the folded service, which is the one the row's badge shows. A game
    /// logged under the "microsoft" alias is badged Xbox, so ordering the raw alias files it under
    /// M, rows away from the Xbox run the reader is scanning.
    /// </summary>
    [Fact]
    public async Task ServiceOrdersTheFoldedServiceNotTheRawAlias()
    {
        var page = await GetPageAsync(
            new RetroDownloadQuery { Sort = "service" },
            [
                NewDownload(1, "microsoft", "10.0.0.1", 7001, "Halo", At(9, 0), At(9, 30), 100, 0),
                NewDownload(2, "steam", "10.0.0.2", 7002, "Portal", At(10, 0), At(10, 30), 100, 0)
            ]);

        Assert.Equal(["Portal", "Halo"], NamesOf(page));
    }

    /// <summary>
    /// A group holding more than one download sorts ahead of every single-download group, and each
    /// part keeps the requested order. Neptune is the only single-download group here, and it moves
    /// to the end under all four sorts that honor the buckets.
    /// </summary>
    [Theory]
    [InlineData("latest", "Apollo,Zeus,Neptune,Mercury", "Apollo,Zeus,Mercury,Neptune")]
    [InlineData("oldest", "Neptune,Zeus,Mercury,Apollo", "Zeus,Mercury,Apollo,Neptune")]
    [InlineData("largest", "Zeus,Apollo,Neptune,Mercury", "Zeus,Apollo,Mercury,Neptune")]
    [InlineData("smallest", "Mercury,Neptune,Apollo,Zeus", "Mercury,Apollo,Zeus,Neptune")]
    public async Task GroupByFrequencyPutsMultiDownloadGroupsFirst(
        string sort,
        string withoutBuckets,
        string withBuckets)
    {
        var plain = await GetPageAsync(new RetroDownloadQuery { Sort = sort });
        Assert.Equal(withoutBuckets.Split(','), NamesOf(plain));

        var bucketed = await GetPageAsync(new RetroDownloadQuery { Sort = sort, GroupByFrequency = true });
        Assert.Equal(withBuckets.Split(','), NamesOf(bucketed));
    }

    /// <summary>
    /// These four sorts impose their own full order over the whole list, so the frequency buckets
    /// are left out of it and a single-download group can outrank a multi-download one. The
    /// expected order is the one the same sort gives with the buckets switched off.
    ///
    /// The sessions sort is exempt from the buckets in the same branch and is not covered here,
    /// because no seed can tell the two apart: the bucket files single-download groups last and
    /// sessions orders on the download count descending, which already files them last. Its order
    /// is pinned by <see cref="SessionsOrdersByHowManyDownloadsTheGroupHolds"/>.
    /// </summary>
    [Theory]
    [InlineData("service", "Mercury,Neptune,Zeus,Apollo")]
    [InlineData("alphabetical", "Apollo,Mercury,Neptune,Zeus")]
    [InlineData("efficiency", "Apollo,Neptune,Zeus,Mercury")]
    [InlineData("efficiency-low", "Mercury,Zeus,Neptune,Apollo")]
    public async Task TheseSortsIgnoreTheFrequencyBuckets(string sort, string expected)
    {
        var bucketed = await GetPageAsync(new RetroDownloadQuery { Sort = sort, GroupByFrequency = true });

        Assert.Equal(expected.Split(','), NamesOf(bucketed));
    }

    /// <summary>
    /// One list, two time columns. The grouped Downloads views order on the newest member's start;
    /// the retro view orders on the group's latest end. The three games here have opposite orders
    /// under the two columns, so a request that reads the wrong one gets the page backwards.
    /// </summary>
    [Theory]
    [InlineData("latest")]
    [InlineData("service")]
    public async Task MergeAcrossServicesOrdersOnTheNewestMemberStart(string sort)
    {
        var rows = ThreeGamesWhoseStartsAndEndsDisagree();

        var merged = await GetPageAsync(
            new RetroDownloadQuery { Sort = sort, GroupByGame = true, MergeAcrossServices = true },
            rows);
        Assert.Equal(["Ares", "Cronos", "Boreas"], NamesOf(merged));

        var retro = await GetPageAsync(
            new RetroDownloadQuery { Sort = sort, GroupByGame = true },
            rows);
        Assert.Equal(["Boreas", "Cronos", "Ares"], NamesOf(retro));
    }

    /// <summary>
    /// Nothing in the sort names a second key, so two rows that tie keep the order they arrived in
    /// and every sort that ties on them agrees. Reversing a sort must not reverse the tied rows and
    /// adding a tiebreak to one sort alone must not move them under that sort only: either one
    /// reorders a page that did not change, which is what a reader sees as a list flickering
    /// between refreshes.
    /// </summary>
    [Fact]
    public async Task TiedRowsComeBackInOneOrderUnderEverySortThatTiesOnThem()
    {
        var rows = TwoGroupsThatTieOnEverySortKey();

        var largest = NamesOf(await GetPageAsync(new RetroDownloadQuery { Sort = "largest" }, rows));
        Assert.Equal(2, largest.Count);

        foreach (var sort in new[] { "latest", "oldest", "smallest", "efficiency", "efficiency-low", "sessions" })
        {
            var page = await GetPageAsync(new RetroDownloadQuery { Sort = sort }, rows);
            Assert.Equal(largest, NamesOf(page));
        }
    }

    private static List<string> NamesOf(RetroDownloadResponse page)
        => page.Items.Select(i => i.AppName).ToList();

    private static DateTime At(int hour, int minute) => Day.AddHours(hour).AddMinutes(minute);

    /// <summary>
    /// Four groups, one per depot and client. Zeus holds four downloads, Apollo three, Mercury two
    /// and Neptune one, which is what the frequency buckets split on. The byte totals, the hit
    /// rates, the starts and the ends each rank the four differently, so no two of the nine sorts
    /// answer with the same order.
    /// </summary>
    private static List<Download> FourGroupsNoTwoSortsAgreeOn() =>
    [
        NewDownload(1, "steam", "10.0.0.1", 1001, "Zeus", At(1, 0), At(1, 30), 500, 500),
        NewDownload(2, "steam", "10.0.0.1", 1001, "Zeus", At(8, 0), At(8, 30), 500, 500),
        NewDownload(3, "steam", "10.0.0.1", 1001, "Zeus", At(9, 0), At(9, 30), 500, 500),
        NewDownload(4, "steam", "10.0.0.1", 1001, "Zeus", At(10, 0), At(12, 0), 500, 500),
        NewDownload(5, "epicgames", "10.0.0.2", 2002, "Mercury", At(2, 0), At(2, 30), 125, 375),
        NewDownload(6, "epicgames", "10.0.0.2", 2002, "Mercury", At(4, 0), At(5, 0), 125, 375),
        NewDownload(7, "wsus", "10.0.0.3", 3003, "Apollo", At(3, 0), At(3, 30), 1000, 0),
        NewDownload(8, "wsus", "10.0.0.3", 3003, "Apollo", At(11, 0), At(11, 30), 1000, 0),
        NewDownload(9, "wsus", "10.0.0.3", 3003, "Apollo", At(13, 0), At(15, 0), 1000, 0),
        NewDownload(10, "riot", "10.0.0.4", 4004, "Neptune", At(0, 30), At(6, 0), 1500, 500)
    ];

    /// <summary>
    /// Three games whose newest start and whose latest end rank them in opposite orders, so the two
    /// columns cannot agree by accident.
    /// </summary>
    private static List<Download> ThreeGamesWhoseStartsAndEndsDisagree() =>
    [
        NewDownload(1, "steam", "10.0.0.1", 5001, "Ares", At(13, 0), At(13, 10), 100, 0),
        NewDownload(2, "steam", "10.0.0.1", 5002, "Boreas", At(9, 0), At(20, 0), 100, 0),
        NewDownload(3, "steam", "10.0.0.1", 5003, "Cronos", At(11, 0), At(15, 0), 100, 0)
    ];

    /// <summary>
    /// Two groups holding the same bytes, the same hit rate, the same download count and the same
    /// two times, so every sort but the two that order on name or service ties on them.
    /// </summary>
    private static List<Download> TwoGroupsThatTieOnEverySortKey() =>
    [
        NewDownload(1, "steam", "10.0.0.1", 6001, "Helios", At(8, 0), At(8, 30), 500, 500),
        NewDownload(2, "steam", "10.0.0.1", 6001, "Helios", At(9, 0), At(10, 0), 500, 500),
        NewDownload(3, "epicgames", "10.0.0.2", 6002, "Selene", At(8, 0), At(8, 30), 500, 500),
        NewDownload(4, "epicgames", "10.0.0.2", 6002, "Selene", At(9, 0), At(10, 0), 500, 500)
    ];

    private static Task<RetroDownloadResponse> GetPageAsync(RetroDownloadQuery query)
        => GetPageAsync(query, FourGroupsNoTwoSortsAgreeOn());

    private static async Task<RetroDownloadResponse> GetPageAsync(
        RetroDownloadQuery query,
        List<Download> rows)
    {
        var controller = await NewControllerAsync(rows);
        var result = await controller.GetRetroDownloadsAsync(query);
        return Assert.IsType<RetroDownloadResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    private static async Task<DownloadsController> NewControllerAsync(List<Download> rows)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"retro-sorts-{Guid.NewGuid():N}")
            .Options;

        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.AddRange(rows);
            await seed.SaveChangesAsync();
        }

        return new DownloadsController(
            new AppDbContext(options),
            DispatchProxy.Create<IStateService, StoredSettingsProxy>(),
            DispatchProxy.Create<IEventsService, NullReturningProxy>(),
            NullLogger<DownloadsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static Download NewDownload(
        long id,
        string service,
        string clientIp,
        long depotId,
        string gameName,
        DateTime startTimeUtc,
        DateTime endTimeUtc,
        long cacheHitBytes,
        long cacheMissBytes) => new()
        {
            Id = id,
            Service = service,
            ClientIp = clientIp,
            DepotId = depotId,
            GameName = gameName,
            StartTimeUtc = startTimeUtc,
            EndTimeUtc = endTimeUtc,
            CacheHitBytes = cacheHitBytes,
            CacheMissBytes = cacheMissBytes,
            IsActive = false
        };

    /// <summary>
    /// The two stored settings this endpoint reads. No client is hidden and evicted rows stay in
    /// the list, so the seed alone decides which rows a page holds and the sort is the only thing
    /// left that can change their order.
    /// </summary>
    private class StoredSettingsProxy : NullReturningProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                nameof(IStateService.GetHiddenClientIps) => new List<string>(),
                nameof(IStateService.GetEvictedDataMode) => "show",
                _ => base.Invoke(targetMethod, args)
            };
    }
}
