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
/// One test per filter the Downloads toolbar can send, each over a seed where that filter is the
/// only thing able to change the answer. Every test reads the same rows with the filter off as
/// well, so what the filter removes is named by the pair rather than by one assertion.
/// No row here carries a depot, so one download is one row and a row names its download.
/// </summary>
public class RetroFilterRuleTests
{
    private static readonly DateTime Day = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The service dropdown sends the folded display name, so "xbox" has to reach the rows logged
    /// as xboxlive and microsoft too. wsus carries mixed Windows Update traffic, is outside that
    /// fold, and is not swept in with them.
    /// </summary>
    [Fact]
    public async Task TheServiceFilterFoldsTheXboxAliasesAndLeavesWsusOut()
    {
        List<Download> rows =
        [
            NewDownload(1, "xbox", "10.0.0.1", null, Day.AddHours(10), 10, 0),
            NewDownload(2, "xboxlive", "10.0.0.1", null, Day.AddHours(11), 10, 0),
            NewDownload(3, "microsoft", "10.0.0.1", null, Day.AddHours(12), 10, 0),
            NewDownload(4, "wsus", "10.0.0.1", null, Day.AddHours(13), 10, 0),
            NewDownload(5, "steam", "10.0.0.1", "Portal 2", Day.AddHours(14), 10, 0)
        ];

        var everything = await GetPageAsync(new RetroDownloadQuery(), rows);
        Assert.Equal([5L, 4L, 3L, 2L, 1L], DownloadIdsOf(everything));

        var xbox = await GetPageAsync(new RetroDownloadQuery { Service = "xbox" }, rows);
        Assert.Equal([3L, 2L, 1L], DownloadIdsOf(xbox));

        var wsus = await GetPageAsync(new RetroDownloadQuery { Service = "wsus" }, rows);
        Assert.Equal([4L], DownloadIdsOf(wsus));
    }

    /// <summary>
    /// One address picked from the client dropdown reaches that client's downloads only.
    /// </summary>
    [Fact]
    public async Task TheClientFilterTakesOneAddress()
    {
        var rows = ThreeClients();

        var everyone = await GetPageAsync(new RetroDownloadQuery(), rows);
        Assert.Equal([3L, 2L, 1L], DownloadIdsOf(everyone));

        var one = await GetPageAsync(new RetroDownloadQuery { Client = "10.0.0.2" }, rows);
        Assert.Equal([2L], DownloadIdsOf(one));
    }

    /// <summary>
    /// A client group in the dropdown is several addresses, and the browser sends them as one
    /// comma-separated value with a space after each comma. Both members come back and nothing
    /// else does.
    /// </summary>
    [Fact]
    public async Task AClientGroupSendsItsAddressesCommaSeparated()
    {
        var rows = ThreeClients();

        var group = await GetPageAsync(new RetroDownloadQuery { Client = "10.0.0.1, 10.0.0.3" }, rows);
        Assert.Equal([3L, 1L], DownloadIdsOf(group));
    }

    /// <summary>
    /// The search box matches the resolved game name and the client address, ignoring case. A row
    /// matching neither is gone.
    /// </summary>
    [Fact]
    public async Task TheSearchBoxMatchesTheNameAndTheClientAddress()
    {
        List<Download> rows =
        [
            NewDownload(1, "steam", "10.0.0.1", "Portal 2", Day.AddHours(10), 10, 0),
            NewDownload(2, "steam", "10.0.0.2", "Half-Life", Day.AddHours(11), 10, 0),
            NewDownload(3, "wsus", "10.0.0.3", null, Day.AddHours(12), 10, 0)
        ];

        var unsearched = await GetPageAsync(new RetroDownloadQuery(), rows);
        Assert.Equal([3L, 2L, 1L], DownloadIdsOf(unsearched));

        var byName = await GetPageAsync(new RetroDownloadQuery { Search = "portal" }, rows);
        Assert.Equal([1L], DownloadIdsOf(byName));

        var byAddress = await GetPageAsync(new RetroDownloadQuery { Search = "10.0.0.2" }, rows);
        Assert.Equal([2L], DownloadIdsOf(byAddress));
    }

    /// <summary>
    /// Hiding localhost drops both loopback spellings, the IPv4 and the IPv6 one, and leaves every
    /// other client alone.
    /// </summary>
    [Fact]
    public async Task HideLocalhostDropsBothLoopbackAddresses()
    {
        List<Download> rows =
        [
            NewDownload(1, "steam", "127.0.0.1", "Portal 2", Day.AddHours(10), 10, 0),
            NewDownload(2, "steam", "::1", "Portal 2", Day.AddHours(11), 10, 0),
            NewDownload(3, "steam", "10.0.0.1", "Portal 2", Day.AddHours(12), 10, 0)
        ];

        var shown = await GetPageAsync(new RetroDownloadQuery(), rows);
        Assert.Equal([3L, 2L, 1L], DownloadIdsOf(shown));

        var hidden = await GetPageAsync(new RetroDownloadQuery { HideLocalhost = true }, rows);
        Assert.Equal([3L], DownloadIdsOf(hidden));
    }

    /// <summary>
    /// The "hide metadata" checkbox sends ShowZeroBytes off, which drops a session that moved
    /// exactly nothing. A completed zero-byte session never reaches this endpoint, so the only
    /// row the checkbox can still act on is a running one, and the views ask for those. A single
    /// byte is not zero and stays.
    /// </summary>
    [Fact]
    public async Task HidingZeroByteSessionsDropsTheRunningDownloadThatMovedNothing()
    {
        var moved = NewDownload(1, "steam", "10.0.0.1", "Portal 2", Day.AddHours(10), 1, 0);
        var movedNothing = NewDownload(2, "steam", "10.0.0.1", "Half-Life", Day.AddHours(11), 0, 0);
        movedNothing.IsActive = true;
        List<Download> rows = [moved, movedNothing];

        var shown = await GetPageAsync(new RetroDownloadQuery { IncludeActive = true }, rows);
        Assert.Equal([2L, 1L], DownloadIdsOf(shown));

        var hidden = await GetPageAsync(
            new RetroDownloadQuery { IncludeActive = true, ShowZeroBytes = false },
            rows);
        Assert.Equal([1L], DownloadIdsOf(hidden));
    }

    /// <summary>
    /// The small-file threshold is a megabyte counted over both byte columns together, and a
    /// download that reaches it exactly is not a small file. One byte short is.
    /// </summary>
    [Fact]
    public async Task HideSmallFilesKeepsExactlyOneMegabyte()
    {
        List<Download> rows =
        [
            NewDownload(1, "steam", "10.0.0.1", "Portal 2", Day.AddHours(10), 1048575, 0),
            NewDownload(2, "steam", "10.0.0.1", "Portal 2", Day.AddHours(11), 1048576, 0),
            NewDownload(3, "steam", "10.0.0.1", "Portal 2", Day.AddHours(12), 1048575, 1)
        ];

        var shown = await GetPageAsync(new RetroDownloadQuery(), rows);
        Assert.Equal([3L, 2L, 1L], DownloadIdsOf(shown));

        var hidden = await GetPageAsync(new RetroDownloadQuery { HideSmallFiles = true }, rows);
        Assert.Equal([3L, 2L], DownloadIdsOf(hidden));
    }

    /// <summary>
    /// The eviction checkbox alone hides evicted rows while the stored mode is showing them, so
    /// the reader's own choice is enough without the setting behind it.
    /// </summary>
    [Fact]
    public async Task HideEvictedAloneDropsTheEvictedRow()
    {
        var rows = OneEvictedRow();

        var shown = await GetPageAsync(new RetroDownloadQuery(), rows);
        Assert.Equal([2L, 1L], DownloadIdsOf(shown));

        var hidden = await GetPageAsync(new RetroDownloadQuery { HideEvicted = true }, rows);
        Assert.Equal([1L], DownloadIdsOf(hidden));
    }

    /// <summary>
    /// The stored evicted-data mode is read from the repository rather than sent with the request,
    /// and it alone decides the same rows with the checkbox off: hide and remove drop them, show
    /// keeps them badged, and showClean keeps them with the badge cleared.
    /// </summary>
    [Fact]
    public async Task TheStoredEvictedModeAloneDecidesEvictedRows()
    {
        var rows = OneEvictedRow();

        var show = await GetPageAsync(new RetroDownloadQuery(), rows, EvictedDataMode.Show.ToWireString());
        Assert.Equal([2L, 1L], DownloadIdsOf(show));
        Assert.True(show.Items.Single(i => i.DownloadIds.Contains(2L)).IsEvicted);

        var hide = await GetPageAsync(new RetroDownloadQuery(), rows, EvictedDataMode.Hide.ToWireString());
        Assert.Equal([1L], DownloadIdsOf(hide));

        var remove = await GetPageAsync(new RetroDownloadQuery(), rows, EvictedDataMode.Remove.ToWireString());
        Assert.Equal([1L], DownloadIdsOf(remove));

        var showClean = await GetPageAsync(new RetroDownloadQuery(), rows, EvictedDataMode.ShowClean.ToWireString());
        Assert.Equal([2L, 1L], DownloadIdsOf(showClean));
        Assert.False(showClean.Items.Single(i => i.DownloadIds.Contains(2L)).IsEvicted);
    }

    /// <summary>
    /// Hiding unknown games drops Steam content whose title could not be resolved and nothing
    /// else. A service that is nameless by design, such as WSUS, keeps its row.
    /// </summary>
    [Fact]
    public async Task HideUnknownDropsUnmappedSteamAndKeepsNamelessServices()
    {
        List<Download> rows =
        [
            NewDownload(1, "steam", "10.0.0.1", null, Day.AddHours(10), 10, 0),
            NewDownload(2, "steam", "10.0.0.1", "Portal 2", Day.AddHours(11), 10, 0),
            NewDownload(3, "wsus", "10.0.0.2", null, Day.AddHours(12), 10, 0)
        ];

        var shown = await GetPageAsync(new RetroDownloadQuery(), rows);
        Assert.Equal([3L, 2L, 1L], DownloadIdsOf(shown));

        var hidden = await GetPageAsync(new RetroDownloadQuery { HideUnknown = true }, rows);
        Assert.Equal([3L, 2L], DownloadIdsOf(hidden));
    }

    /// <summary>
    /// A download that is still running is left out unless the caller asks for it. Retro is a
    /// history view and does not; the grouped views do, so the live row stays on screen.
    /// </summary>
    [Fact]
    public async Task ARunningDownloadArrivesOnlyWhenIncludeActiveIsSet()
    {
        var finished = NewDownload(1, "steam", "10.0.0.1", "Portal 2", Day.AddHours(10), 100, 0);
        var running = NewDownload(2, "steam", "10.0.0.1", "Half-Life", Day.AddHours(11), 100, 0);
        running.IsActive = true;
        List<Download> rows = [finished, running];

        var history = await GetPageAsync(new RetroDownloadQuery(), rows);
        Assert.Equal([1L], DownloadIdsOf(history));

        var live = await GetPageAsync(new RetroDownloadQuery { IncludeActive = true }, rows);
        Assert.Equal([2L, 1L], DownloadIdsOf(live));
    }

    /// <summary>
    /// The header time range bounds the start time and includes both of its ends, so a download
    /// that started exactly on a bound is inside the range rather than on the wrong side of it.
    /// </summary>
    [Fact]
    public async Task TheTimeRangeIncludesBothOfItsEnds()
    {
        List<Download> rows =
        [
            NewDownload(1, "steam", "10.0.0.1", "Portal 2", Day.AddHours(10), 10, 0),
            NewDownload(2, "steam", "10.0.0.1", "Portal 2", Day.AddHours(11), 10, 0),
            NewDownload(3, "steam", "10.0.0.1", "Portal 2", Day.AddHours(12), 10, 0)
        ];
        var middle = UnixSeconds(Day.AddHours(11));

        var unbounded = await GetPageAsync(new RetroDownloadQuery(), rows);
        Assert.Equal([3L, 2L, 1L], DownloadIdsOf(unbounded));

        var from = await GetPageAsync(new RetroDownloadQuery { StartTime = middle }, rows);
        Assert.Equal([3L, 2L], DownloadIdsOf(from));

        var until = await GetPageAsync(new RetroDownloadQuery { EndTime = middle }, rows);
        Assert.Equal([2L, 1L], DownloadIdsOf(until));

        var window = await GetPageAsync(
            new RetroDownloadQuery { StartTime = middle, EndTime = middle },
            rows);
        Assert.Equal([2L], DownloadIdsOf(window));
    }

    /// <summary>
    /// The tagged-event filter keeps the downloads tagged to that event and drops the rest, even
    /// though they sit in the same time range and under the same service.
    /// </summary>
    [Fact]
    public async Task TheEventFilterKeepsOnlyTaggedDownloads()
    {
        List<Download> rows =
        [
            NewDownload(1, "steam", "10.0.0.1", "Portal 2", Day.AddHours(10), 10, 0),
            NewDownload(2, "steam", "10.0.0.1", "Portal 2", Day.AddHours(11), 10, 0),
            NewDownload(3, "steam", "10.0.0.1", "Portal 2", Day.AddHours(12), 10, 0)
        ];
        List<EventDownload> tags =
        [
            new() { Id = 1, EventId = 7, DownloadId = 2, TaggedAtUtc = Day.AddHours(11) }
        ];

        var untagged = await GetPageAsync(new RetroDownloadQuery(), rows, tags: tags);
        Assert.Equal([3L, 2L, 1L], DownloadIdsOf(untagged));

        var tagged = await GetPageAsync(new RetroDownloadQuery { EventId = 7 }, rows, tags: tags);
        Assert.Equal([2L], DownloadIdsOf(tagged));
    }

    /// <summary>
    /// The hit/miss control splits on the byte-weighted hit percentage at fifty, and a download
    /// sitting exactly on fifty counts as a hit.
    /// </summary>
    [Fact]
    public async Task TheHitMissFilterSplitsAtFiftyPercent()
    {
        List<Download> rows =
        [
            NewDownload(1, "steam", "10.0.0.1", "Portal 2", Day.AddHours(10), 50, 50),
            NewDownload(2, "steam", "10.0.0.2", "Portal 2", Day.AddHours(11), 49, 51),
            NewDownload(3, "steam", "10.0.0.3", "Portal 2", Day.AddHours(12), 100, 0)
        ];

        var both = await GetPageAsync(new RetroDownloadQuery(), rows);
        Assert.Equal([3L, 2L, 1L], DownloadIdsOf(both));

        var hits = await GetPageAsync(new RetroDownloadQuery { HitMiss = "hit" }, rows);
        Assert.Equal([3L, 1L], DownloadIdsOf(hits));

        var misses = await GetPageAsync(new RetroDownloadQuery { HitMiss = "miss" }, rows);
        Assert.Equal([2L], DownloadIdsOf(misses));
    }

    /// <summary>
    /// Each filter removes its own rows and no others. Three rows, each one only a single filter
    /// can reach: turning one filter on leaves the other's row untouched, and turning both on
    /// removes exactly the two rows the pair names.
    /// </summary>
    [Fact]
    public async Task TurningOnOneFilterDoesNotChangeWhatAnotherRemoves()
    {
        List<Download> rows =
        [
            NewDownload(1, "steam", "10.0.0.1", "Portal 2", Day.AddHours(10), 2000000, 0),
            NewDownload(2, "steam", "127.0.0.1", "Portal 2", Day.AddHours(11), 2000000, 0),
            NewDownload(3, "steam", "10.0.0.2", "Portal 2", Day.AddHours(12), 500, 0)
        ];

        var neither = await GetPageAsync(new RetroDownloadQuery(), rows);
        Assert.Equal([3L, 2L, 1L], DownloadIdsOf(neither));

        var withoutLocalhost = await GetPageAsync(new RetroDownloadQuery { HideLocalhost = true }, rows);
        Assert.Equal([3L, 1L], DownloadIdsOf(withoutLocalhost));

        var withoutSmallFiles = await GetPageAsync(new RetroDownloadQuery { HideSmallFiles = true }, rows);
        Assert.Equal([2L, 1L], DownloadIdsOf(withoutSmallFiles));

        var withoutBoth = await GetPageAsync(
            new RetroDownloadQuery { HideLocalhost = true, HideSmallFiles = true },
            rows);
        Assert.Equal([1L], DownloadIdsOf(withoutBoth));
    }

    /// <summary>Three downloads of the same title on three clients, one each.</summary>
    private static List<Download> ThreeClients() =>
    [
        NewDownload(1, "steam", "10.0.0.1", "Portal 2", Day.AddHours(10), 10, 0),
        NewDownload(2, "steam", "10.0.0.2", "Portal 2", Day.AddHours(11), 10, 0),
        NewDownload(3, "steam", "10.0.0.3", "Portal 2", Day.AddHours(12), 10, 0)
    ];

    /// <summary>One ordinary download and one evicted download, alike in everything else.</summary>
    private static List<Download> OneEvictedRow()
    {
        var kept = NewDownload(1, "steam", "10.0.0.1", "Portal 2", Day.AddHours(10), 100, 0);
        var evicted = NewDownload(2, "steam", "10.0.0.1", "Portal 2", Day.AddHours(11), 100, 0);
        evicted.IsEvicted = true;
        return [kept, evicted];
    }

    /// <summary>
    /// The downloads the page stands for, in the page's own order. Every row here is a no-depot
    /// row, so each carries exactly the one download it was built from.
    /// </summary>
    private static List<long> DownloadIdsOf(RetroDownloadResponse page)
        => page.Items.SelectMany(i => i.DownloadIds).ToList();

    private static long UnixSeconds(DateTime utc)
        => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds();

    private static async Task<RetroDownloadResponse> GetPageAsync(
        RetroDownloadQuery query,
        List<Download> rows,
        string evictedMode = "show",
        List<EventDownload>? tags = null)
    {
        var controller = await NewControllerAsync(rows, evictedMode, tags);
        var result = await controller.GetRetroDownloadsAsync(query);
        return Assert.IsType<RetroDownloadResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    private static async Task<DownloadsController> NewControllerAsync(
        List<Download> rows,
        string evictedMode,
        List<EventDownload>? tags)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"retro-filters-{Guid.NewGuid():N}")
            .Options;

        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.AddRange(rows);
            if (tags != null)
            {
                seed.EventDownloads.AddRange(tags);
            }
            await seed.SaveChangesAsync();
        }

        var stateService = DispatchProxy.Create<IStateService, StoredSettingsProxy>();
        ((StoredSettingsProxy)stateService).EvictedMode = evictedMode;

        return new DownloadsController(
            new AppDbContext(options),
            stateService,
            DispatchProxy.Create<IEventsService, ExistingEventProxy>(),
            NullLogger<DownloadsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static Download NewDownload(
        long id,
        string service,
        string clientIp,
        string? gameName,
        DateTime startTimeUtc,
        long cacheHitBytes,
        long cacheMissBytes) => new()
        {
            Id = id,
            Service = service,
            ClientIp = clientIp,
            GameName = gameName,
            StartTimeUtc = startTimeUtc,
            EndTimeUtc = startTimeUtc.AddMinutes(5),
            CacheHitBytes = cacheHitBytes,
            CacheMissBytes = cacheMissBytes,
            IsActive = false
        };

    /// <summary>
    /// The two stored settings this endpoint reads. Hidden clients are none, so only the seeded
    /// rows decide the answer; the evicted mode is what each test sets. Every other member falls
    /// through to the shared benign default.
    /// </summary>
    private class StoredSettingsProxy : NullReturningProxy
    {
        public string EvictedMode { get; set; } = "show";

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                nameof(IStateService.GetHiddenClientIps) => new List<string>(),
                nameof(IStateService.GetEvictedDataMode) => EvictedMode,
                _ => base.Invoke(targetMethod, args)
            };
    }

    /// <summary>
    /// The endpoint looks the event id up before it filters on it, so an event the reader picked
    /// from the header has to be found or the request answers "not found" instead of a page.
    /// </summary>
    private class ExistingEventProxy : NullReturningProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == nameof(IEventsService.GetByIdAsync)
                ? Task.FromResult<Event?>(new Event())
                : base.Invoke(targetMethod, args);
    }
}
