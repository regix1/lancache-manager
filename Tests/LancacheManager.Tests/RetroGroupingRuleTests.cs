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
/// One test per grouping switch the Downloads toolbar can send, one per rule that decides which
/// switch wins when two of them arrive together, and one per paging rule the pager depends on.
/// The six seeded downloads are chosen so each switch answers with a different row count and a
/// different set of ids, so a flag wired to the branch beside it cannot pass by luck.
/// </summary>
public class RetroGroupingRuleTests
{
    private static readonly DateTime Day = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Group by game keys each bucket on the title within one service, so the two Portal 2
    /// downloads on different depots and clients collapse to one row while the same title logged
    /// under Steam and under Epic stays two rows.
    /// </summary>
    [Fact]
    public async Task GroupByGameFoldsATitleWithinOneService()
    {
        var page = await GetPageAsync(new RetroDownloadQuery { GroupByGame = true });

        Assert.Equal(5, page.TotalItems);
        Assert.Equal(
            [
                "wsus-unknown",
                "steam-name-unknown/other",
                "epicgames-name-rocket league",
                "steam-name-rocket league",
                "steam-app-620"
            ],
            page.Items.Select(i => i.Id));
        Assert.Equal(2, Assert.Single(page.Items, i => i.Id == "steam-app-620").RequestCount);
    }

    /// <summary>
    /// Group by service is one row per service and nothing finer. A service row can span many
    /// titles, so it names the service and carries no depot or app id of its own.
    /// </summary>
    [Fact]
    public async Task GroupByServiceIsOneRowPerService()
    {
        var page = await GetPageAsync(new RetroDownloadQuery { GroupByService = true });

        Assert.Equal(3, page.TotalItems);
        Assert.Equal(["wsus", "steam", "epicgames"], page.Items.Select(i => i.Id));
        Assert.Equal(["wsus", "steam", "epicgames"], page.Items.Select(i => i.AppName));

        var steam = Assert.Single(page.Items, i => i.Id == "steam");
        Assert.Equal(4, steam.RequestCount);
        Assert.Null(steam.SteamAppId);
        Assert.Null(steam.DepotId);
    }

    /// <summary>
    /// Merging across services keys a game bucket on the title alone, so one title seen under two
    /// services is a single row that adds up both services' bytes.
    /// </summary>
    [Fact]
    public async Task MergeAcrossServicesFoldsOneTitleAcrossServices()
    {
        var page = await GetPageAsync(new RetroDownloadQuery
        {
            GroupByGame = true,
            MergeAcrossServices = true
        });

        Assert.Equal(4, page.TotalItems);

        var rocketLeague = Assert.Single(page.Items, i => i.Id == "game-Rocket League");
        Assert.Equal(2, rocketLeague.RequestCount);
        Assert.Equal(140, rocketLeague.TotalBytes);
        Assert.Equal("game", rocketLeague.GroupType);

        var ids = page.Items.Select(i => i.Id).ToList();
        Assert.DoesNotContain("steam-name-rocket league", ids);
        Assert.DoesNotContain("epicgames-name-rocket league", ids);
    }

    /// <summary>
    /// Unmapped Steam content collapses into one bucket that shows a neutral service, instead of
    /// sitting in the leftover Steam bucket and borrowing the Steam icon.
    /// </summary>
    [Fact]
    public async Task GroupUnknownGamesCollapsesUnmappedSteamIntoOneRow()
    {
        var page = await GetPageAsync(new RetroDownloadQuery
        {
            GroupByGame = true,
            MergeAcrossServices = true,
            GroupUnknownGames = true
        });

        Assert.Equal(4, page.TotalItems);

        var unknown = Assert.Single(page.Items, i => i.Id == "unknown-other");
        Assert.Equal("Unknown/Other", unknown.AppName);
        Assert.Equal("unknown", unknown.Service);
        Assert.Equal("content", unknown.GroupType);
        Assert.DoesNotContain("service-steam", page.Items.Select(i => i.Id));
    }

    /// <summary>
    /// Both switches at once answer with the service rows. A per-service total already covers the
    /// per-game breakdown, so the coarser switch wins rather than the two combining.
    /// </summary>
    [Fact]
    public async Task GroupByServiceOverridesGroupByGame()
    {
        var page = await GetPageAsync(new RetroDownloadQuery
        {
            GroupByService = true,
            GroupByGame = true
        });

        Assert.Equal(3, page.TotalItems);
        Assert.Equal(["wsus", "steam", "epicgames"], page.Items.Select(i => i.Id));
        Assert.DoesNotContain("steam-app-620", page.Items.Select(i => i.Id));
    }

    /// <summary>
    /// Merging across services is a way of keying game buckets, so with no game grouping asked for
    /// there is nothing for it to key and the page stays one row per depot and client.
    /// </summary>
    [Fact]
    public async Task MergeAcrossServicesIsIgnoredWithoutGroupByGame()
    {
        var page = await GetPageAsync(new RetroDownloadQuery { MergeAcrossServices = true });

        Assert.Equal(6, page.TotalItems);
        Assert.DoesNotContain("game-Rocket League", page.Items.Select(i => i.Id));
        Assert.All(page.Items, item => Assert.Equal(string.Empty, item.GroupType));
    }

    /// <summary>
    /// The unknown bucket only exists inside the merged keying, so asking for it without asking to
    /// merge leaves unmapped Steam content in its per-service bucket.
    /// </summary>
    [Fact]
    public async Task GroupUnknownGamesIsIgnoredWithoutMergeAcrossServices()
    {
        var page = await GetPageAsync(new RetroDownloadQuery
        {
            GroupByGame = true,
            GroupUnknownGames = true
        });

        Assert.Equal(5, page.TotalItems);
        var ids = page.Items.Select(i => i.Id).ToList();
        Assert.DoesNotContain("unknown-other", ids);
        Assert.Contains("steam-name-unknown/other", ids);
    }

    /// <summary>
    /// A page is the slice the reader asked for, taken from the same order the whole list is in.
    /// </summary>
    [Fact]
    public async Task PageAndPageSizeReturnTheirSliceOfTheOrder()
    {
        var page = await GetPageAsync(new RetroDownloadQuery { Page = 2, PageSize = 2 });

        Assert.Equal(2, page.CurrentPage);
        Assert.Equal(2, page.PageSize);
        Assert.Equal(
            ["no-depot-epicgames-10.0.0.2-4", "depot-3003-10.0.0.2"],
            page.Items.Select(i => i.Id));
    }

    /// <summary>
    /// The page size arrives from the query string, so it is clamped to a page that can be drawn:
    /// zero would divide the page count by zero, and an unbounded one would pull the whole table
    /// back through an endpoint that exists to page it.
    /// </summary>
    [Fact]
    public async Task PageSizeIsClampedToTheAllowedRange()
    {
        var tooSmall = await GetPageAsync(new RetroDownloadQuery { PageSize = 0 });

        Assert.Equal(1, tooSmall.PageSize);
        Assert.Single(tooSmall.Items);
        Assert.Equal(6, tooSmall.TotalPages);

        var tooLarge = await GetPageAsync(new RetroDownloadQuery { PageSize = 1000 });

        Assert.Equal(200, tooLarge.PageSize);
        Assert.Equal(6, tooLarge.Items.Count);
        Assert.Equal(1, tooLarge.TotalPages);
    }

    /// <summary>
    /// Six rows over a page of four is a full page and then a page of two. The short page is what
    /// the reader sees at the end of the list, and padding it would invent rows.
    /// </summary>
    [Fact]
    public async Task TheLastPageIsShortRatherThanPadded()
    {
        var page = await GetPageAsync(new RetroDownloadQuery { Page = 2, PageSize = 4 });

        Assert.Equal(2, page.TotalPages);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(
            ["depot-1005-10.0.0.5", "depot-1001-10.0.0.1"],
            page.Items.Select(i => i.Id));
    }

    /// <summary>
    /// A reader sitting on the last page when a filter shrinks the list asks for a page that no
    /// longer exists. That answers with an empty page carrying the real page count, so the pager
    /// can send them back to a page that does exist.
    /// </summary>
    [Fact]
    public async Task APagePastTheEndIsEmptyRatherThanAnError()
    {
        var page = await GetPageAsync(new RetroDownloadQuery { Page = 3, PageSize = 4 });

        Assert.Empty(page.Items);
        Assert.Equal(6, page.TotalItems);
        Assert.Equal(2, page.TotalPages);
        Assert.Equal(3, page.CurrentPage);
    }

    /// <summary>
    /// The totals count the whole filtered list and not the page, because the pager has to size
    /// itself before it has seen the other pages. Four of the six seeded downloads are Steam, so a
    /// total taken from the page would read two and one that ignored the filter would read six.
    ///
    /// Grouped by game, so the two totals are different numbers: the four Steam downloads fold to
    /// three rows because Portal 2 is logged twice. That is the "4 downloads in 3 rows" the pager
    /// label reads, and neither count can stand in for the other.
    /// </summary>
    [Fact]
    public async Task TheTotalsCountTheFilteredSetRatherThanThePage()
    {
        var page = await GetPageAsync(new RetroDownloadQuery
        {
            Service = "steam",
            GroupByGame = true,
            Page = 1,
            PageSize = 2
        });

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(3, page.TotalItems);
        Assert.Equal(4, page.TotalDownloads);
        Assert.Equal(2, page.TotalPages);
    }

    private static async Task<RetroDownloadResponse> GetPageAsync(RetroDownloadQuery query)
    {
        var controller = await NewControllerAsync();
        var result = await controller.GetRetroDownloadsAsync(query);
        return Assert.IsType<RetroDownloadResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    private static async Task<DownloadsController> NewControllerAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"retro-grouping-{Guid.NewGuid():N}")
            .Options;

        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.AddRange(Seed());
            await seed.SaveChangesAsync();
        }

        var stateService = CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.GetHiddenClientIps) => new List<string>(),
            nameof(IStateService.GetEvictedDataMode) => "show",
            _ => DefaultReturn(method.ReturnType)
        });

        return new DownloadsController(
            new AppDbContext(options),
            stateService,
            CreateProxy<IEventsService>((method, _) => DefaultReturn(method.ReturnType)),
            NullLogger<DownloadsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    /// <summary>
    /// One title on two depots and two clients under one service, one title logged under two
    /// services, one unmapped Steam download and one nameless service. Every row carries bytes,
    /// because a completed zero-byte download never reaches this endpoint.
    /// </summary>
    private static List<Download> Seed() =>
    [
        NewDownload(1, "steam", "10.0.0.1", 1001, 620, "Portal 2", Day.AddHours(10), 100, 0),
        NewDownload(2, "steam", "10.0.0.5", 1005, 620, "Portal 2", Day.AddHours(11), 20, 0),
        NewDownload(3, "steam", "10.0.0.2", 3003, null, "Rocket League", Day.AddHours(12), 50, 50),
        NewDownload(4, "epicgames", "10.0.0.2", null, null, "Rocket League", Day.AddHours(13), 0, 40),
        NewDownload(5, "steam", "10.0.0.3", 2002, null, null, Day.AddHours(14), 5, 5),
        NewDownload(6, "wsus", "10.0.0.4", null, null, null, Day.AddHours(15), 5, 5)
    ];

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
