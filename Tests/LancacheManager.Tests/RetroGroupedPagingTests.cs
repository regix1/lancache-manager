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
/// Pins the grouped page the Downloads views read. The views used to build these groups in the
/// browser from the whole table, so every ordering, key and label here is a copy of what
/// createGroups and its sort produced, and a drift in either direction is a visible reordering
/// rather than a failure.
/// </summary>
public class RetroGroupedPagingTests
{
    /// <summary>
    /// The retro view with no grouping flags at all: one row per depot and client, newest end
    /// time first. The grouped page added a frequency bucket ahead of every sort, and this pins
    /// that a request which asks for no buckets is ordered exactly as it was.
    /// </summary>
    [Fact]
    public async Task TheUngroupedRetroPageIsOneRowPerDepotAndClientNewestFirst()
    {
        var response = await GetGroupedPageAsync(new RetroDownloadQuery());

        Assert.Equal(5, response.TotalItems);
        Assert.Equal(
            [
                "no-depot-wsus-10.0.0.4-6",
                "depot-2002-10.0.0.3",
                "no-depot-epicgames-10.0.0.2-4",
                "depot-3003-10.0.0.2",
                "depot-1001-10.0.0.1"
            ],
            response.Items.Select(i => i.Id));
    }

    /// <summary>
    /// The retro view's own grouping. MergeAcrossServices is off, so one title logged under two
    /// services stays two service-scoped rows and no row carries a group type.
    /// </summary>
    [Fact]
    public async Task GroupByGameAloneKeepsTheServiceScopedRows()
    {
        var response = await GetGroupedPageAsync(new RetroDownloadQuery { GroupByGame = true });

        Assert.Equal(5, response.TotalItems);
        var ids = response.Items.Select(i => i.Id).ToList();
        Assert.Contains("steam-name-rocket league", ids);
        Assert.Contains("epicgames-name-rocket league", ids);
        Assert.Contains("steam-app-620", ids);
        Assert.All(response.Items, item => Assert.Equal(string.Empty, item.GroupType));
    }

    /// <summary>
    /// One title logged under two services is a single row, keyed the way the views key it, so a
    /// group id survives the move to the server.
    /// </summary>
    [Fact]
    public async Task MergeAcrossServicesFoldsOneTitleIntoOneRow()
    {
        var response = await GetGroupedPageAsync(new RetroDownloadQuery
        {
            GroupByGame = true,
            MergeAcrossServices = true
        });

        Assert.Equal(4, response.TotalItems);

        var rocketLeague = Assert.Single(response.Items, i => i.Id == "game-Rocket League");
        Assert.Equal(2, rocketLeague.RequestCount);
        Assert.Equal(140, rocketLeague.TotalBytes);
        Assert.Equal("game", rocketLeague.GroupType);

        var portal = Assert.Single(response.Items, i => i.Id == "game-appid-620");
        Assert.Equal(620, portal.SteamAppId);
        Assert.Equal("game", portal.GroupType);
    }

    /// <summary>
    /// The group's own start times: the earliest member start names the group, the latest member
    /// start is what the recent sort orders on. The retro view orders on the latest END time, so
    /// these are two different columns over the same members.
    /// </summary>
    [Fact]
    public async Task AGroupCarriesBothItsEarliestAndItsLatestMemberStart()
    {
        var response = await GetGroupedPageAsync(new RetroDownloadQuery
        {
            GroupByGame = true,
            MergeAcrossServices = true
        });

        var portal = Assert.Single(response.Items, i => i.Id == "game-appid-620");
        Assert.Equal(new DateTime(2026, 8, 31, 10, 0, 0, DateTimeKind.Utc), portal.StartTimeUtc);
        Assert.Equal(new DateTime(2026, 8, 31, 11, 0, 0, DateTimeKind.Utc), portal.LastStartTimeUtc);
    }

    /// <summary>
    /// Groups with more than one download come first, then single-download groups, each part in
    /// the requested order. The largest sort is used because the seed's biggest group holds one
    /// download, so an unbucketed order would lead with it.
    /// </summary>
    [Fact]
    public async Task GroupByFrequencyPutsMultiDownloadGroupsFirst()
    {
        var bucketed = await GetGroupedPageAsync(new RetroDownloadQuery
        {
            GroupByGame = true,
            MergeAcrossServices = true,
            GroupByFrequency = true,
            Sort = "largest"
        });

        Assert.Equal(
            ["game-appid-620", "game-Rocket League", "service-steam", "service-wsus"],
            bucketed.Items.Select(i => i.Id));

        var unbucketed = await GetGroupedPageAsync(new RetroDownloadQuery
        {
            GroupByGame = true,
            MergeAcrossServices = true,
            Sort = "largest"
        });

        Assert.Equal(
            ["service-steam", "game-appid-620", "game-Rocket League", "service-wsus"],
            unbucketed.Items.Select(i => i.Id));
    }

    /// <summary>
    /// The sorts that impose their own full order ignore the frequency buckets, so a
    /// single-download group can outrank a multi-download one.
    /// </summary>
    [Theory]
    [InlineData("efficiency")]
    [InlineData("service")]
    [InlineData("alphabetical")]
    [InlineData("efficiency-low")]
    [InlineData("sessions")]
    public async Task TheseSortsIgnoreTheFrequencyBuckets(string sort)
    {
        var bucketed = await GetGroupedPageAsync(new RetroDownloadQuery
        {
            GroupByGame = true,
            MergeAcrossServices = true,
            GroupByFrequency = true,
            Sort = sort
        });

        var unbucketed = await GetGroupedPageAsync(new RetroDownloadQuery
        {
            GroupByGame = true,
            MergeAcrossServices = true,
            Sort = sort
        });

        Assert.Equal(unbucketed.Items.Select(i => i.Id), bucketed.Items.Select(i => i.Id));
    }

    /// <summary>
    /// The recent sort orders on the latest member start, newest first, which is the order the
    /// views show by default.
    /// </summary>
    [Fact]
    public async Task TheDefaultSortOrdersOnTheLatestMemberStart()
    {
        var response = await GetGroupedPageAsync(new RetroDownloadQuery
        {
            GroupByGame = true,
            MergeAcrossServices = true
        });

        Assert.Equal(
            ["service-wsus", "service-steam", "game-Rocket League", "game-appid-620"],
            response.Items.Select(i => i.Id));
    }

    /// <summary>
    /// A page is one page. The totals describe the whole filtered set so the pager can size
    /// itself before it has seen the rest.
    /// </summary>
    [Fact]
    public async Task APageCarriesItsSliceAndTheWholeSetsTotals()
    {
        var response = await GetGroupedPageAsync(new RetroDownloadQuery
        {
            GroupByGame = true,
            MergeAcrossServices = true,
            Page = 2,
            PageSize = 2
        });

        Assert.Equal(4, response.TotalItems);
        Assert.Equal(2, response.TotalPages);
        Assert.Equal(2, response.CurrentPage);
        Assert.Equal(2, response.PageSize);
        Assert.Equal(["game-Rocket League", "game-appid-620"], response.Items.Select(i => i.Id));
    }

    /// <summary>
    /// Unmapped Steam content collapses into one bucket that shows a neutral service, so the row
    /// renders its own icon instead of borrowing Steam's, and names no depot or app.
    /// </summary>
    [Fact]
    public async Task GroupUnknownGamesCollapsesUnmappedSteamIntoOneBucket()
    {
        var folded = await GetGroupedPageAsync(new RetroDownloadQuery
        {
            GroupByGame = true,
            MergeAcrossServices = true,
            GroupUnknownGames = true
        });

        var unknown = Assert.Single(folded.Items, i => i.Id == "unknown-other");
        Assert.Equal("Unknown/Other", unknown.AppName);
        Assert.Equal("unknown", unknown.Service);
        Assert.Equal("content", unknown.GroupType);
        Assert.Null(unknown.DepotId);
        Assert.Null(unknown.SteamAppId);

        var perService = await GetGroupedPageAsync(new RetroDownloadQuery
        {
            GroupByGame = true,
            MergeAcrossServices = true
        });

        Assert.DoesNotContain("unknown-other", perService.Items.Select(i => i.Id));
        Assert.Contains("service-steam", perService.Items.Select(i => i.Id));
    }

    /// <summary>
    /// "Unknown/Other" is a name this endpoint invents for unmapped Steam content and no column
    /// holds it, so searching for it has to reach those rows anyway. The cross-tab jump from the
    /// dashboard lands on an empty list otherwise.
    /// </summary>
    [Fact]
    public async Task SearchingTheUnknownLabelFindsUnmappedSteamRows()
    {
        var response = await GetGroupedPageAsync(new RetroDownloadQuery
        {
            GroupByGame = true,
            MergeAcrossServices = true,
            GroupUnknownGames = true,
            Search = "other"
        });

        var unknown = Assert.Single(response.Items);
        Assert.Equal("unknown-other", unknown.Id);
    }

    /// <summary>
    /// The views search the Steam app id, so a search for one has to reach the group that carries
    /// it even though no other field on that group holds the digits.
    /// </summary>
    [Fact]
    public async Task SearchingASteamAppIdFindsItsGroup()
    {
        var response = await GetGroupedPageAsync(new RetroDownloadQuery
        {
            GroupByGame = true,
            MergeAcrossServices = true,
            Search = "620"
        });

        var portal = Assert.Single(response.Items);
        Assert.Equal("game-appid-620", portal.Id);
    }

    /// <summary>
    /// A reader hiding small files hides every row under 1 MB. A completed zero-byte session never
    /// reaches this endpoint at all, so the threshold is the whole test.
    /// </summary>
    [Fact]
    public async Task HideSmallFilesDropsRowsUnderAMegabyte()
    {
        var day = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        List<Download> rows =
        [
            NewDownload(1, "steam", "10.0.0.1", null, null, "Portal 2", day.AddHours(10), 500000, 0),
            NewDownload(2, "steam", "10.0.0.1", null, null, "Portal 2", day.AddHours(11), 2000000, 0),
            NewDownload(3, "steam", "10.0.0.1", null, null, "Portal 2", day.AddHours(12), 0, 0)
        ];

        var shown = await GetGroupedPageAsync(new RetroDownloadQuery(), rows);
        Assert.Equal(
            ["no-depot-steam-10.0.0.1-2", "no-depot-steam-10.0.0.1-1"],
            shown.Items.Select(i => i.Id));

        var hidden = await GetGroupedPageAsync(new RetroDownloadQuery { HideSmallFiles = true }, rows);
        Assert.Equal(1, hidden.TotalItems);
        Assert.Equal(["no-depot-steam-10.0.0.1-2"], hidden.Items.Select(i => i.Id));
    }

    /// <summary>
    /// The per-reader eviction checkbox, with the stored evicted-data mode showing evicted rows.
    /// That is the only state in which the checkbox has anything left to hide.
    /// </summary>
    [Fact]
    public async Task HideEvictedDropsEvictedRowsWhileTheStoredModeShowsThem()
    {
        var day = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        var kept = NewDownload(1, "steam", "10.0.0.1", null, null, "Portal 2", day.AddHours(10), 100, 0);
        var evicted = NewDownload(2, "steam", "10.0.0.1", null, null, "Portal 2", day.AddHours(11), 100, 0);
        evicted.IsEvicted = true;

        var shown = await GetGroupedPageAsync(new RetroDownloadQuery(), [kept, evicted]);
        Assert.Equal(2, shown.TotalItems);

        var hidden = await GetGroupedPageAsync(new RetroDownloadQuery { HideEvicted = true }, [kept, evicted]);
        Assert.Equal("no-depot-steam-10.0.0.1-1", Assert.Single(hidden.Items).Id);
    }

    /// <summary>
    /// Retro is a history view and leaves a download that is still running out of the list. The
    /// grouped Downloads views ask for it, and it has to come back as a whole row: its own group,
    /// counted in the totals, carrying the running download the collapsed row is drawn from.
    /// </summary>
    [Fact]
    public async Task ARunningDownloadComesBackOnlyWhenTheCallerAsksForIt()
    {
        var day = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        var finished = NewDownload(1, "steam", "10.0.0.1", null, null, "Portal 2", day.AddHours(10), 100, 0);
        var running = NewDownload(2, "steam", "10.0.0.1", null, null, "Half-Life", day.AddHours(11), 100, 0);
        running.IsActive = true;

        var history = await GetGroupedPageAsync(new RetroDownloadQuery(), [finished, running]);
        Assert.Equal(1, history.TotalItems);
        Assert.Equal(["no-depot-steam-10.0.0.1-1"], history.Items.Select(i => i.Id));

        var withRunning = await GetGroupedPageAsync(
            new RetroDownloadQuery { IncludeActive = true },
            [finished, running]);
        Assert.Equal(2, withRunning.TotalItems);
        Assert.Equal(
            ["no-depot-steam-10.0.0.1-2", "no-depot-steam-10.0.0.1-1"],
            withRunning.Items.Select(i => i.Id));

        var grouped = await GetGroupedPageAsync(
            new RetroDownloadQuery { IncludeActive = true, GroupByGame = true, MergeAcrossServices = true },
            [finished, running]);
        Assert.Equal(2, grouped.TotalItems);
        var runningGroup = Assert.Single(grouped.Items, item => item.AppName == "Half-Life");
        Assert.Equal(1, runningGroup.RequestCount);
        Assert.Equal([2L], runningGroup.DownloadIds);
        Assert.True(runningGroup.HasRealGameName);
        Assert.NotNull(runningGroup.PrimaryDownload);
        Assert.True(runningGroup.PrimaryDownload!.IsActive);
    }

    /// <summary>
    /// A client group is several addresses, so the client filter takes the list of them. A single
    /// address is that list with one member.
    /// </summary>
    [Fact]
    public async Task TheClientFilterTakesEveryAddressOfAGroup()
    {
        var day = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        List<Download> rows =
        [
            NewDownload(1, "steam", "10.0.0.1", null, null, "Portal 2", day.AddHours(10), 100, 0),
            NewDownload(2, "steam", "10.0.0.2", null, null, "Portal 2", day.AddHours(11), 100, 0),
            NewDownload(3, "steam", "10.0.0.3", null, null, "Portal 2", day.AddHours(12), 100, 0)
        ];

        var members = await GetGroupedPageAsync(new RetroDownloadQuery { Client = "10.0.0.1, 10.0.0.2" }, rows);
        Assert.Equal(
            ["no-depot-steam-10.0.0.2-2", "no-depot-steam-10.0.0.1-1"],
            members.Items.Select(i => i.Id));

        var one = await GetGroupedPageAsync(new RetroDownloadQuery { Client = "10.0.0.3" }, rows);
        Assert.Equal(["no-depot-steam-10.0.0.3-3"], one.Items.Select(i => i.Id));
    }

    /// <summary>
    /// The filters run against Postgres, where neither TotalBytes nor CacheHitPercent exists: both
    /// are computed from CacheHitBytes and CacheMissBytes and no column holds either. ToQueryString
    /// compiles the query through the Npgsql provider without opening a connection, so a filter
    /// written against one of them fails here instead of throwing on the request.
    /// </summary>
    [Fact]
    public void TheRetroFiltersCompileAgainstPostgres()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_smoke_test")
            .Options;

        var controller = NewController(new AppDbContext(options));
        var buildBaseQuery = typeof(DownloadsController)
            .GetMethod("BuildRetroBaseQuery", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var baseQuery = (IQueryable<Download>)buildBaseQuery.Invoke(
            controller,
            [
                new RetroDownloadQuery
                {
                    ShowZeroBytes = false,
                    HideSmallFiles = true,
                    HideEvicted = true,
                    Client = "10.0.0.1,10.0.0.2"
                }
            ])!;

        Assert.Contains("1048576", baseQuery.ToQueryString());
    }

    /// <summary>
    /// The page reports how many downloads it is paging over as well as how many rows. Merging
    /// changes the row count and must not change the download count: the same six downloads are
    /// five rows ungrouped and four once one title logged under two services folds together.
    /// </summary>
    [Fact]
    public async Task ThePageCountsItsDownloadsAsWellAsItsRows()
    {
        var ungrouped = await GetGroupedPageAsync(new RetroDownloadQuery());

        Assert.Equal(5, ungrouped.TotalItems);
        Assert.Equal(6, ungrouped.TotalDownloads);

        var merged = await GetGroupedPageAsync(new RetroDownloadQuery
        {
            GroupByGame = true,
            MergeAcrossServices = true
        });

        Assert.Equal(4, merged.TotalItems);
        Assert.Equal(6, merged.TotalDownloads);
    }

    /// <summary>
    /// Alphabetical orders the title on screen. A nameless service row carries the bare service
    /// ("epicgames") but is shown as "Epic Games", and 'G' comes before the 'M' of "Epic Mickey",
    /// so the service row leads. Ordering the bare key instead compares the 'G' against the space
    /// in the title and reverses the two, in English and not only in a translated locale. The row
    /// still carries the bare service on the wire, because the browser translates that title.
    /// </summary>
    [Fact]
    public async Task AlphabeticalOrdersTheLabelTheBrowserShows()
    {
        var day = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        var response = await GetGroupedPageAsync(
            new RetroDownloadQuery
            {
                GroupByGame = true,
                MergeAcrossServices = true,
                Sort = "alphabetical"
            },
            [
                NewDownload(1, "epicgames", "10.0.0.1", null, null, null, day.AddHours(10), 40, 0),
                NewDownload(2, "steam", "10.0.0.1", null, null, "Epic Mickey", day.AddHours(11), 60, 0)
            ]);

        Assert.Equal(["service-epicgames", "game-Epic Mickey"], response.Items.Select(i => i.Id));
        Assert.Equal(["epicgames", "Epic Mickey"], response.Items.Select(i => i.AppName));
    }

    /// <summary>
    /// A Steam group whose depots never mapped to a title still knows which app it is, so the row
    /// is named by that app id rather than by the unidentified-content placeholder. The group keeps
    /// reporting that no member resolved a real name, so the views that hide an unresolved title
    /// still hide it.
    /// </summary>
    [Fact]
    public async Task ASteamGroupWithNoTitleIsNamedByItsAppId()
    {
        var day = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        var response = await GetGroupedPageAsync(
            new RetroDownloadQuery
            {
                GroupByGame = true,
                MergeAcrossServices = true
            },
            [
                NewDownload(1, "steam", "10.0.0.1", 1001, 620, null, day.AddHours(10), 100, 0)
            ]);

        var row = Assert.Single(response.Items);
        Assert.Equal("game-appid-620", row.Id);
        Assert.Equal("Steam App 620", row.AppName);
        Assert.False(row.HasRealGameName);
    }

    /// <summary>
    /// A request that asks for nothing outside the page has its aggregate ordered and paged by the
    /// database; anything else reads the whole grouped set into memory. Both have to land on the
    /// same page boundaries. The in-memory path is driven here by a search every seeded address
    /// matches, so it keeps every row and only changes which path builds the page.
    /// </summary>
    [Theory]
    [InlineData("latest")]
    [InlineData("recent")]
    [InlineData("oldest")]
    [InlineData("largest")]
    [InlineData("smallest")]
    [InlineData("efficiency")]
    [InlineData("efficiency-low")]
    [InlineData("sessions")]
    [InlineData("alphabetical")]
    [InlineData("service")]
    public async Task EverySortPagesTheSameWhicheverPathBuildsThePage(string sort)
    {
        var rows = PagingSeed();

        foreach (var byFrequency in new[] { false, true })
        {
            for (var page = 1; page <= 3; page++)
            {
                var paged = await GetGroupedPageAsync(
                    new RetroDownloadQuery
                    {
                        Sort = sort,
                        GroupByFrequency = byFrequency,
                        Page = page,
                        PageSize = 3
                    },
                    rows);

                var inMemory = await GetGroupedPageAsync(
                    new RetroDownloadQuery
                    {
                        Sort = sort,
                        GroupByFrequency = byFrequency,
                        Page = page,
                        PageSize = 3,
                        Search = "10.0.0."
                    },
                    rows);

                Assert.Equal(7, paged.TotalItems);
                Assert.Equal(3, paged.TotalPages);
                Assert.Equal(inMemory.TotalItems, paged.TotalItems);
                Assert.Equal(inMemory.TotalPages, paged.TotalPages);
                Assert.Equal(inMemory.TotalDownloads, paged.TotalDownloads);
                Assert.Equal(inMemory.Items.Select(i => i.Id), paged.Items.Select(i => i.Id));
            }
        }
    }

    /// <summary>
    /// Groups the sort cannot separate page in the group key's order, so the same page asked for
    /// twice holds the same rows and no row is skipped between two pages. Every group here is
    /// identical on every column a sort reads, so the tie-break decides the whole order, and the
    /// rows are seeded in the reverse of it.
    /// </summary>
    [Theory]
    [InlineData("latest")]
    [InlineData("recent")]
    [InlineData("oldest")]
    [InlineData("largest")]
    [InlineData("smallest")]
    [InlineData("efficiency")]
    [InlineData("efficiency-low")]
    [InlineData("sessions")]
    [InlineData("alphabetical")]
    [InlineData("service")]
    public async Task TiedGroupsPageInTheGroupKeysOrder(string sort)
    {
        var day = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        var rows = Enumerable.Range(1, 9)
            .Select(n => NewDownload(n, "wsus", $"10.0.0.{10 - n}", null, null, null, day.AddHours(1), 100, 100))
            .ToList();

        var first = await GetGroupedPageAsync(new RetroDownloadQuery { Sort = sort, PageSize = 4 }, rows);

        Assert.Equal(9, first.TotalItems);
        Assert.Equal(3, first.TotalPages);
        Assert.Equal(
            [
                "no-depot-wsus-10.0.0.1-9",
                "no-depot-wsus-10.0.0.2-8",
                "no-depot-wsus-10.0.0.3-7",
                "no-depot-wsus-10.0.0.4-6"
            ],
            first.Items.Select(i => i.Id));

        var last = await GetGroupedPageAsync(
            new RetroDownloadQuery { Sort = sort, Page = 3, PageSize = 4 },
            rows);

        Assert.Equal(["no-depot-wsus-10.0.0.9-1"], last.Items.Select(i => i.Id));
    }

    /// <summary>
    /// More no-depot rows than a page holds, which is the shape an Xbox-heavy table has: every row
    /// is its own group. The page and the totals match the in-memory path, and the query behind the
    /// page asks the database for that page rather than for the whole aggregate.
    /// </summary>
    [Fact]
    public async Task ALargeNoDepotSetComesBackAsOnePageFromTheDatabase()
    {
        var day = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        var rows = Enumerable.Range(1, 501)
            .Select(n => NewDownload(n, "xboxlive", "10.0.0.1", null, null, null, day.AddMinutes(n), 100, 100))
            .ToList();

        var paged = await GetGroupedPageAsync(new RetroDownloadQuery { PageSize = 200 }, rows);

        Assert.Equal(501, paged.TotalItems);
        Assert.Equal(3, paged.TotalPages);
        Assert.Equal(501, paged.TotalDownloads);
        Assert.Equal(200, paged.Items.Count);

        var inMemory = await GetGroupedPageAsync(
            new RetroDownloadQuery { PageSize = 200, Search = "10.0.0." },
            rows);

        Assert.Equal(inMemory.Items.Select(i => i.Id), paged.Items.Select(i => i.Id));

        var sql = PagedQueryString(new RetroDownloadQuery { Page = 2, PageSize = 200 });

        Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OFFSET", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The paged aggregate has to compile against Postgres for every sort that reaches it. The
    /// frequency bucket has no column behind it, so a translation gap there would otherwise show up
    /// as a failed request rather than here. The two efficiency sorts are absent because the
    /// provider will not round an aggregate to one decimal, which is why they page in memory.
    /// </summary>
    [Theory]
    [InlineData("latest")]
    [InlineData("recent")]
    [InlineData("oldest")]
    [InlineData("largest")]
    [InlineData("smallest")]
    [InlineData("sessions")]
    public void TheRetroPagedQueryCompilesAgainstPostgres(string sort)
    {
        foreach (var byFrequency in new[] { false, true })
        {
            var sql = PagedQueryString(new RetroDownloadQuery
            {
                Sort = sort,
                GroupByFrequency = byFrequency,
                Page = 2,
                PageSize = 200
            });

            Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The SQL the paged aggregate compiles to, through the Npgsql provider and without opening a
    /// connection.
    /// </summary>
    private static string PagedQueryString(RetroDownloadQuery query)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_smoke_test")
            .Options;

        var controller = NewController(new AppDbContext(options));
        var buildPagedQuery = typeof(DownloadsController)
            .GetMethod("BuildRetroPagedQuery", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var pagedQuery = (IQueryable)buildPagedQuery.Invoke(controller, [query])!;
        return pagedQuery.ToQueryString();
    }

    /// <summary>
    /// Seven groups over ten downloads: two depot groups holding more than one download so the
    /// frequency bucket and the session sort have something to separate, five single-download
    /// groups, and two groups tied on cache-hit percentage. Every address shares the "10.0.0."
    /// prefix so a search on it keeps the whole set.
    /// </summary>
    private static List<Download> PagingSeed()
    {
        var day = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        return
        [
            NewDownload(1, "wsus", "10.0.0.1", 5001, null, null, day.AddHours(1), 900, 100),
            NewDownload(2, "wsus", "10.0.0.1", 5001, null, null, day.AddHours(2), 100, 900),
            NewDownload(3, "wsus", "10.0.0.1", 5001, null, null, day.AddHours(3), 500, 500),
            NewDownload(4, "xbox", "10.0.0.2", 5002, null, null, day.AddHours(4), 800, 200),
            NewDownload(5, "xbox", "10.0.0.2", 5002, null, null, day.AddHours(5), 200, 300),
            NewDownload(6, "epicgames", "10.0.0.3", null, null, "Rocket League", day.AddHours(6), 100, 0),
            NewDownload(7, "riot", "10.0.0.4", null, null, null, day.AddHours(7), 0, 250),
            NewDownload(8, "wsus", "10.0.0.5", null, null, null, day.AddHours(8), 400, 100),
            NewDownload(9, "steam", "10.0.0.6", null, 620, "Portal 2", day.AddHours(9), 50, 150),
            NewDownload(10, "xboxlive", "10.0.0.7", null, null, null, day.AddHours(10), 2000, 2000)
        ];
    }

    private static Task<RetroDownloadResponse> GetGroupedPageAsync(RetroDownloadQuery query)
        => GetGroupedPageAsync(query, Seed());

    /// <summary>
    /// A collapsed group row is drawn from one member, so the group carries its newest download
    /// rather than its membership. A depot-backed group and a no-depot group reach it by different
    /// routes, and both are covered here.
    /// </summary>
    [Fact]
    public async Task AGroupCarriesItsNewestDownloadAsThePrimary()
    {
        var response = await GetGroupedPageAsync(new RetroDownloadQuery
        {
            GroupByGame = true,
            MergeAcrossServices = true
        });

        var portal = Assert.Single(response.Items, i => i.Id == "game-appid-620");
        Assert.Equal(2, portal.PrimaryDownload!.Id);
        Assert.Equal("Portal 2", portal.PrimaryDownload.GameName);

        var rocketLeague = Assert.Single(response.Items, i => i.Id == "game-Rocket League");
        Assert.Equal(4, rocketLeague.PrimaryDownload!.Id);

        Assert.All(response.Items, item => Assert.NotNull(item.PrimaryDownload));
    }

    /// <summary>
    /// A request that merges reads an aggregate that groups a no-depot download on the identity
    /// its bucket is built from rather than on its own id, so several downloads of one title on
    /// one client arrive as a single group. The bucket still names every download behind it and
    /// still shows the newest of them, both of which are fetched for the page afterwards.
    /// </summary>
    [Fact]
    public async Task AMergedNoDepotBucketNamesEveryDownloadBehindIt()
    {
        var day = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        List<Download> rows =
        [
            NewDownload(1, "wsus", "10.0.0.1", null, null, null, day.AddHours(1), 100, 100),
            NewDownload(2, "wsus", "10.0.0.1", null, null, null, day.AddHours(2), 200, 200),
            NewDownload(3, "wsus", "10.0.0.1", null, null, null, day.AddHours(3), 300, 300),
            NewDownload(4, "riot", "10.0.0.2", null, null, "Valorant", day.AddHours(4), 400, 0),
            NewDownload(5, "riot", "10.0.0.2", null, null, "Valorant", day.AddHours(5), 0, 500)
        ];

        var response = await GetGroupedPageAsync(
            new RetroDownloadQuery { GroupByGame = true, MergeAcrossServices = true },
            rows);

        var windowsUpdate = Assert.Single(response.Items, i => i.Id == "service-wsus");
        Assert.Equal(3, windowsUpdate.RequestCount);
        Assert.Equal([1L, 2L, 3L], windowsUpdate.DownloadIds.Order());
        Assert.Equal(1200, windowsUpdate.TotalBytes);
        Assert.Equal(3, windowsUpdate.PrimaryDownload!.Id);

        var valorant = Assert.Single(response.Items, i => i.Id == "game-Valorant");
        Assert.Equal(2, valorant.RequestCount);
        Assert.Equal([4L, 5L], valorant.DownloadIds.Order());
        Assert.Equal(5, valorant.PrimaryDownload!.Id);
    }

    /// <summary>
    /// The unmerged retro table lists a no-depot download on its own row, so a request that only
    /// filters or sorts keeps the depot-and-client aggregate and the three Windows Update rows
    /// above stay three rows.
    /// </summary>
    [Fact]
    public async Task TheUnmergedRetroTableStillListsOneRowPerNoDepotDownload()
    {
        var day = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        List<Download> rows =
        [
            NewDownload(1, "wsus", "10.0.0.1", null, null, null, day.AddHours(1), 100, 100),
            NewDownload(2, "wsus", "10.0.0.1", null, null, null, day.AddHours(2), 200, 200),
            NewDownload(3, "wsus", "10.0.0.1", null, null, null, day.AddHours(3), 300, 300)
        ];

        var response = await GetGroupedPageAsync(new RetroDownloadQuery { Sort = "alphabetical" }, rows);

        Assert.Equal(3, response.TotalItems);
        Assert.Equal(
            ["no-depot-wsus-10.0.0.1-1", "no-depot-wsus-10.0.0.1-2", "no-depot-wsus-10.0.0.1-3"],
            response.Items.Select(i => i.Id).Order());
    }

    /// <summary>
    /// The views hide a group's title unless SOME member resolved a game name, so the newest
    /// member alone cannot answer it. The Unknown/Other bucket keeps its title regardless.
    /// </summary>
    [Fact]
    public async Task AGroupTitleShowsWhenAnyMemberResolvedAName()
    {
        var response = await GetGroupedPageAsync(new RetroDownloadQuery
        {
            GroupByGame = true,
            MergeAcrossServices = true,
            GroupUnknownGames = true
        });

        Assert.True(Assert.Single(response.Items, i => i.Id == "game-appid-620").HasRealGameName);
        Assert.True(Assert.Single(response.Items, i => i.Id == "game-Rocket League").HasRealGameName);
        Assert.True(Assert.Single(response.Items, i => i.Id == "unknown-other").HasRealGameName);
        Assert.False(Assert.Single(response.Items, i => i.Id == "service-wsus").HasRealGameName);
    }

    /// <summary>
    /// The views read a group's eviction state as "every member" and "some but not every member".
    /// The merged row already reports both, and this pins that the two agree, because a group that
    /// disagreed with its own members would flip its badge depending on which was read.
    /// </summary>
    [Fact]
    public async Task MergedEvictionMatchesEveryAndSomeOverTheMembers()
    {
        var day = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        var partly = NewDownload(1, "steam", "10.0.0.1", 1001, 620, "Portal 2", day.AddHours(10), 100, 0);
        var fully = NewDownload(2, "steam", "10.0.0.1", 1001, 620, "Portal 2", day.AddHours(11), 100, 0);
        fully.IsEvicted = true;

        var mixed = await GetGroupedPageAsync(
            new RetroDownloadQuery { GroupByGame = true, MergeAcrossServices = true },
            [partly, fully]);

        var mixedGroup = Assert.Single(mixed.Items);
        Assert.False(mixedGroup.IsEvicted);
        Assert.True(mixedGroup.IsPartiallyEvicted);

        partly.IsEvicted = true;
        var all = await GetGroupedPageAsync(
            new RetroDownloadQuery { GroupByGame = true, MergeAcrossServices = true },
            [partly, fully]);

        var allGroup = Assert.Single(all.Items);
        Assert.True(allGroup.IsEvicted);
        Assert.False(allGroup.IsPartiallyEvicted);
    }

    /// <summary>
    /// xbox, xboxlive and microsoft are one service to a reader, so they are one row: three rows
    /// under the same "Xbox" label is what the raw service name produced. wsus is outside that
    /// fold and keeps its own row.
    /// </summary>
    [Fact]
    public async Task TheXboxAliasesShareOneGroupAndWsusKeepsItsOwn()
    {
        var day = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        var response = await GetGroupedPageAsync(
            new RetroDownloadQuery { GroupByGame = true, MergeAcrossServices = true },
            [
                NewDownload(1, "xbox", "10.0.0.1", null, null, null, day.AddHours(10), 10, 0),
                NewDownload(2, "xboxlive", "10.0.0.1", null, null, null, day.AddHours(11), 10, 0),
                NewDownload(3, "microsoft", "10.0.0.1", null, null, null, day.AddHours(12), 10, 0),
                NewDownload(4, "wsus", "10.0.0.2", null, null, null, day.AddHours(13), 10, 0)
            ]);

        Assert.Equal(2, response.TotalItems);

        var xbox = Assert.Single(response.Items, i => i.Id == "service-xbox");
        Assert.Equal(3, xbox.RequestCount);
        Assert.Equal(30, xbox.TotalBytes);
        Assert.Equal("xbox", xbox.Service);
        Assert.Equal("xbox", xbox.AppName);

        var wsus = Assert.Single(response.Items, i => i.Id == "service-wsus");
        Assert.Equal(1, wsus.RequestCount);
        Assert.Equal("wsus", wsus.Service);
    }

    /// <summary>
    /// An expanded group's members, fetched by the ids the grouped row already carries. Newest
    /// first, matching the order the member list renders in.
    /// </summary>
    [Fact]
    public async Task MembersComeBackByIdNewestFirst()
    {
        var controller = await NewControllerAsync(Seed());

        var result = await controller.GetByIdsAsync(new BatchDownloadEventsRequest
        {
            DownloadIds = [1, 2]
        });

        var rows = Assert.IsType<List<Download>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal([2L, 1L], rows.Select(r => r.Id));
        Assert.All(rows, row => Assert.Equal("Portal 2", row.GameName));
    }

    /// <summary>
    /// A group can name more members than the ceiling returns. The 500 that come back are the
    /// newest ones, in that order, rather than whichever 500 the id list happened to start with.
    /// </summary>
    [Fact]
    public async Task MembersPastTheCeilingKeepTheNewestOnes()
    {
        var day = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        var seeded = Enumerable.Range(1, 501)
            .Select(minute => NewDownload(
                minute, "steam", "10.0.0.1", 1001, 620, "Portal 2", day.AddMinutes(minute), 100, 0))
            .ToList();
        var controller = await NewControllerAsync(seeded);

        var result = await controller.GetByIdsAsync(new BatchDownloadEventsRequest
        {
            DownloadIds = seeded.Select(row => row.Id).ToList()
        });

        var rows = Assert.IsType<List<Download>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(500, rows.Count);
        Assert.Equal(
            Enumerable.Range(2, 500).Reverse().Select(id => (long)id),
            rows.Select(row => row.Id));
    }

    /// <summary>
    /// An empty id set is a group with nothing to expand, not an error.
    /// </summary>
    [Fact]
    public async Task NoIdsComesBackEmpty()
    {
        var controller = await NewControllerAsync(Seed());

        var result = await controller.GetByIdsAsync(new BatchDownloadEventsRequest());

        var rows = Assert.IsType<List<Download>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Empty(rows);
    }

    private static async Task<RetroDownloadResponse> GetGroupedPageAsync(
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
            .UseInMemoryDatabase($"retro-grouped-{Guid.NewGuid():N}")
            .Options;

        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.AddRange(rows);
            await seed.SaveChangesAsync();
        }

        return NewController(new AppDbContext(options));
    }

    private static DownloadsController NewController(AppDbContext context)
    {
        var stateService = CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.GetHiddenClientIps) => new List<string>(),
            nameof(IStateService.GetEvictedDataMode) => "show",
            _ => DefaultReturn(method.ReturnType)
        });

        return new DownloadsController(
            context,
            stateService,
            CreateProxy<IEventsService>((method, _) => DefaultReturn(method.ReturnType)),
            NullLogger<DownloadsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    /// <summary>
    /// One title under two services, one multi-download game, one unmapped Steam group that is
    /// deliberately the largest so a byte sort separates from a frequency-bucketed one, and one
    /// nameless service.
    /// </summary>
    private static List<Download> Seed()
    {
        var day = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        return
        [
            NewDownload(1, "steam", "10.0.0.1", 1001, 620, "Portal 2", day.AddHours(10), 100, 0),
            NewDownload(2, "steam", "10.0.0.1", 1001, 620, "Portal 2", day.AddHours(11), 100, 0),
            NewDownload(3, "steam", "10.0.0.2", 3003, null, "Rocket League", day.AddHours(12), 50, 50),
            NewDownload(4, "epicgames", "10.0.0.2", null, null, "Rocket League", day.AddHours(13), 0, 40),
            NewDownload(5, "steam", "10.0.0.3", 2002, null, null, day.AddHours(14), 5000, 5000),
            NewDownload(6, "wsus", "10.0.0.4", null, null, null, day.AddHours(15), 5, 5)
        ];
    }

    private static Download NewDownload(
        long id,
        string service,
        string clientIp,
        long? depotId,
        long? gameAppId,
        string? gameName,
        DateTime startTimeUtc,
        long cacheHitBytes,
        long cacheMissBytes) => new()
        {
            Id = id,
            Service = service,
            ClientIp = clientIp,
            DepotId = depotId,
            GameAppId = gameAppId,
            GameName = gameName,
            StartTimeUtc = startTimeUtc,
            EndTimeUtc = startTimeUtc.AddMinutes(5),
            CacheHitBytes = cacheHitBytes,
            CacheMissBytes = cacheMissBytes,
            IsActive = false
        };

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, ProxyDispatch<T>>();
        ((ProxyDispatch<T>)(object)proxy).Handler = handler;
        return proxy;
    }

    private static object? DefaultReturn(Type returnType)
    {
        if (returnType == typeof(void))
        {
            return null;
        }

        if (returnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            var fromResult = typeof(Task)
                .GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(resultType);
            return fromResult.Invoke(null, [DefaultValue(resultType)]);
        }

        return DefaultValue(returnType);
    }

    private static object? DefaultValue(Type type)
        => !type.IsValueType || Nullable.GetUnderlyingType(type) != null
            ? null
            : Activator.CreateInstance(type);

    private class ProxyDispatch<T> : DispatchProxy where T : class
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => Handler!(targetMethod!, args);
    }
}
