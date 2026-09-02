using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Extensions;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for download history
/// Handles single-download lookups, event associations, and the grouped retro view
/// </summary>
[ApiController]
[Route("api/downloads")]
[Authorize]
public class DownloadsController : ControllerBase
{

    private readonly AppDbContext _context;
    private readonly IStateService _stateRepository;
    private readonly IEventsService _eventsService;
    private readonly ILogger<DownloadsController> _logger;

    public DownloadsController(
        AppDbContext context,
        IStateService stateRepository,
        IEventsService eventsService,
        ILogger<DownloadsController> logger)
    {
        _context = context;
        _stateRepository = stateRepository;
        _eventsService = eventsService;
        _logger = logger;
    }

    /// <summary>
    /// Get a download by ID with its tags and events
    /// </summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType(typeof(DownloadWithEventsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DownloadWithEventsResponse>> GetByIdAsync(long id)
    {
        var download = await _context.Downloads
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id)
            ?? throw new NotFoundException("Download");

        var hiddenClientIps = _stateRepository.GetHiddenClientIps();
        if (hiddenClientIps.Contains(download.ClientIp))
        {
            throw new NotFoundException("Download");
        }

        // Hide evicted downloads in hide/remove mode
        var evictedMode = _stateRepository.GetEvictedDataMode();
        if ((evictedMode == EvictedDataMode.Hide.ToWireString() || evictedMode == EvictedDataMode.Remove.ToWireString()) && download.IsEvicted)
        {
            throw new NotFoundException("Download");
        }
        // ShowClean: mask the evicted flag so frontend shows no badge/dimming
        if (evictedMode == EvictedDataMode.ShowClean.ToWireString())
        {
            download.IsEvicted = false;
        }

        download.WithUtcMarking();

        // Get events for this download
        var eventDownloads = await _context.EventDownloads
            .AsNoTracking()
            .Include(ed => ed.Event)
            .Where(ed => ed.DownloadId == id)
            .ToListAsync();

        var events = eventDownloads.Select(ed => new DownloadEventAssociation
        {
            Id = ed.Event.Id,
            Name = ed.Event.Name,
            ColorIndex = ed.Event.ColorIndex,
            StartTimeUtc = ed.Event.StartTimeUtc,
            EndTimeUtc = ed.Event.EndTimeUtc,
            AutoTagged = ed.AutoTagged
        }).ToList();

        return Ok(new DownloadWithEventsResponse
        {
            Download = download,
            Events = events
        });
    }

    /// <summary>
    /// Get events for multiple download IDs in a single batch request
    /// </summary>
    [HttpPost("batch-download-events")]
    [ProducesResponseType(typeof(Dictionary<long, DownloadEventBatchEntry>), StatusCodes.Status200OK)]
    public async Task<ActionResult<Dictionary<long, DownloadEventBatchEntry>>> GetBatchEventsAsync([FromBody] BatchDownloadEventsRequest request)
    {
        if (request.DownloadIds == null || request.DownloadIds.Count == 0)
        {
            return Ok(new Dictionary<long, DownloadEventBatchEntry>());
        }

        // Limit to prevent abuse
        const int maxIds = 500;
        var downloadIds = request.DownloadIds.Take(maxIds).ToList();

        // Get all events for these downloads in a single query
        var eventDownloads = await _context.EventDownloads
            .AsNoTracking()
            .Include(ed => ed.Event)
            .Where(ed => downloadIds.Contains(ed.DownloadId))
            .ToListAsync();

        // Group by download ID and return as dictionary
        var result = downloadIds.ToDictionary(
            id => id,
            id => new DownloadEventBatchEntry
            {
                Events = eventDownloads
                    .Where(ed => ed.DownloadId == id)
                    .Select(ed => new DownloadEventTag
                    {
                        Id = ed.Event.Id,
                        Name = ed.Event.Name,
                        ColorIndex = ed.Event.ColorIndex,
                        AutoTagged = ed.AutoTagged
                    })
                    .ToList()
            }
        );

        return Ok(result);
    }

    /// <summary>
    /// Get downloads with their tags and events for a time range
    /// </summary>
    [HttpGet("with-associations")]
    [ProducesResponseType(typeof(List<DownloadWithEventTags>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<DownloadWithEventTags>>> GetWithEventsAsync(
        [FromQuery] int count = 100,
        [FromQuery] long? startTime = null,
        [FromQuery] long? endTime = null)
    {
        const int maxCount = 200;

        // Each row carries a correlated event subquery, so an unbounded count materializes the
        // whole table twice over.
        count = Math.Clamp(count, 1, maxCount);

        var hiddenClientIps = _stateRepository.GetHiddenClientIps();
        var evictedMode = _stateRepository.GetEvictedDataMode();
        var startDate = startTime.HasValue
            ? startTime.Value.FromUnixSeconds()
            : DateTime.MinValue;
        var endDate = endTime.HasValue
            ? endTime.Value.FromUnixSeconds()
            : DateTime.UtcNow;

        // Use a single query with projection to atomically fetch downloads and their event associations
        // This eliminates the race condition by avoiding separate queries
        var baseQuery = _context.Downloads
            .AsNoTracking()
            .Where(d => hiddenClientIps.Count == 0 || !hiddenClientIps.Contains(d.ClientIp))
            .Where(d => d.StartTimeUtc >= startDate && d.StartTimeUtc <= endDate);

        baseQuery = baseQuery.ApplyEvictedFilter(evictedMode).ApplyEmptySessionFilter();

        var downloadsWithEvents = await baseQuery
            .OrderByDescending(d => d.StartTimeUtc)
            .Take(count)
            .Select(d => new
            {
                Download = d,
                Events = _context.EventDownloads
                    .AsNoTracking()
                    .Where(ed => ed.DownloadId == d.Id)
                    .Select(ed => new
                    {
                        ed.Event.Id,
                        ed.Event.Name,
                        ed.Event.ColorIndex,
                        ed.AutoTagged
                    })
                    .ToList()
            })
            .ToListAsync();

        if (downloadsWithEvents.Count == 0)
        {
            return Ok(new List<DownloadWithEventTags>());
        }

        // Mark timestamps as UTC and build response
        var result = downloadsWithEvents.Select(item =>
        {
            item.Download.WithUtcMarking();
            return new DownloadWithEventTags
            {
                Download = item.Download,
                Events = item.Events.Select(ed => new DownloadEventTag
                {
                    Id = ed.Id,
                    Name = ed.Name,
                    ColorIndex = ed.ColorIndex,
                    AutoTagged = ed.AutoTagged
                }).ToList()
            };
        }).ToList();

        return Ok(result);
    }

    /// <summary>
    /// Gets paginated, grouped download data for the Retro view.
    /// </summary>
    /// <remarks>
    /// Groups downloads by DepotId and ClientIp and aggregates cache statistics, then resolves
    /// game names on the aggregated rows before returning them. GroupByGame merges those rows
    /// into per-game buckets, and MergeAcrossServices keys those buckets the way the grouped
    /// Downloads views do so they can page against this endpoint instead of the whole table.
    ///
    /// The aggregate is ordered and paged in the database whenever the request asks for nothing
    /// that has to look outside the page: no merge (GroupByGame, GroupByService,
    /// MergeAcrossServices), no filter on the resolved game name (Search, HideUnknown), no
    /// hit/miss bucket, and a sort with a column behind it. Those requests read one page of groups
    /// and two counts, so an Xbox-heavy table - where every no-depot row is its own group - costs a
    /// page rather than the whole table on every refresh.
    ///
    /// Everything else falls back to reading the whole grouped set into memory, because it has to:
    /// a merge combines groups the page has not seen, the name filters read a name resolved in C#
    /// after the aggregate, the hit/miss bucket decides membership, and the alphabetical and
    /// service sorts order text this endpoint computes (the shown title, the folded service name)
    /// rather than a column. The two efficiency sorts fall back for a narrower reason: they order
    /// the cache-hit percentage rounded to one decimal, the provider does not translate that
    /// rounding over an aggregate, and ordering the unrounded ratio instead would separate rows
    /// that show the same percentage and put them in a different order than the in-memory path.
    /// </remarks>
    [HttpGet("retro")]
    [ProducesResponseType(typeof(RetroDownloadResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RetroDownloadResponse>> GetRetroDownloadsAsync([FromQuery] RetroDownloadQuery query)
    {
        const int maxPageSize = 200;

        // Clamp page size
        query.PageSize = Math.Clamp(query.PageSize, 1, maxPageSize);
        if (query.Page < 1) query.Page = 1;

        // A cascade delete removes the event's EventDownloads rows, so an unknown id would
        // otherwise flow through BuildRetroBaseQuery as an empty (but 200 OK) result instead of
        // a clear signal that the id is gone. Checked outside the try below so the throw reaches
        // GlobalExceptionMiddleware instead of being swallowed into an empty response.
        if (query.EventId.HasValue)
        {
            await _eventsService.GetByIdOrThrowAsync(query.EventId.Value, "Event");
        }

        try
        {
            // ShowClean keeps evicted rows in the list but suppresses the badge and dimming, the
            // same way the row-level download list does. Hide/Remove already dropped those rows
            // in BuildRetroBaseQuery, so their counts come back zero without extra filtering.
            var maskEviction = _stateRepository.GetEvictedDataMode() == EvictedDataMode.ShowClean.ToWireString();

            // Everything below the merge reads rows the page does not hold: a merge combines
            // groups, the two name filters read the name resolved after the aggregate, and the
            // hit/miss bucket decides membership. The four sorts listed here order a value with no
            // column behind it - a title and a folded service name computed in C#, and a rounded
            // percentage the provider will not round for us. When none of them is asked for, the
            // database orders and pages the aggregate and only the page is materialized.
            var pageInSql = !query.GroupByGame
                && !query.GroupByService
                && !query.MergeAcrossServices
                && !query.HideUnknown
                && string.IsNullOrEmpty(query.Search)
                && query.HitMiss is not ("hit" or "miss")
                && query.Sort is not ("alphabetical" or "service" or "efficiency" or "efficiency-low");

            if (pageInSql)
            {
                var groupCount = await BuildRetroGroupedQuery(query).CountAsync();
                // Nothing merged or dropped rows after the aggregate, so every download behind the
                // page's groups is a download the base query returned. Counting those directly is
                // the same number the in-memory path sums off the rows it is holding anyway.
                var downloadCount = await BuildRetroBaseQuery(query).CountAsync();
                var pageRows = await BuildRetroPagedQuery(query).ToListAsync();

                await GameNameResolver.ResolveAsync(_context, pageRows, HttpContext.RequestAborted);

                var pageItems = pageRows.Select(r => BuildRetroRow(r, maskEviction, true)).ToList();
                var pagePairs = pageItems
                    .Where(r => r.DepotId.HasValue)
                    .ToDictionary(
                        r => r.Id,
                        r => new List<(long DepotId, string ClientIp)> { (r.DepotId!.Value, r.ClientIp) },
                        StringComparer.Ordinal);

                await FillDownloadIdsAsync(query, pageItems, pagePairs, []);

                return Ok(new RetroDownloadResponse
                {
                    Items = pageItems,
                    TotalItems = groupCount,
                    TotalDownloads = downloadCount,
                    TotalPages = Math.Max(1, (int)Math.Ceiling(groupCount / (double)query.PageSize)),
                    CurrentPage = query.Page,
                    PageSize = query.PageSize
                });
            }

            // A request that merges reads the coarser aggregate: its no-depot rows are folded into
            // one bucket regardless, so keying them on the identity behind that bucket returns one
            // group per title per client rather than one per download. A request that only filters
            // or sorts still shows one row per group, and the retro table lists a no-depot download
            // on its own row, so it keeps the depot-and-client aggregate.
            var mergeGroups = query.GroupByGame || query.GroupByService;

            // The whole grouped set, in the group key's order. The sorts below are stable, so the
            // ties they leave alone fall back to this order - the same order BuildRetroPagedQuery
            // spells out as its last tie-break, so a page boundary lands in the same place on
            // either path.
            var groupedRows = await (mergeGroups ? BuildRetroMergedQuery(query) : BuildRetroGroupedQuery(query))
                .OrderBy(r => r.DepotId)
                .ThenBy(r => r.ClientIp)
                .ThenBy(r => r.RowKey)
                .ToListAsync();

            // The game name a no-depot group was keyed on, read before the resolver fills an empty
            // one, so the downloads behind the page's rows can be fetched back on the same key.
            var keyedGameNames = mergeGroups
                ? groupedRows.Select(r => r.GameName).ToArray()
                : [];

            // Resolve game names at group level over the same shared resolver the dashboard
            // batch uses, so both views pick the same owner when a depot has multiple mappings.
            await GameNameResolver.ResolveAsync(_context, groupedRows, HttpContext.RequestAborted);

            var grouped = groupedRows.Select(r => BuildRetroRow(r, maskEviction, !mergeGroups)).ToList();

            // Keep each row's byte-weighted speed accumulator alongside the DTO. A merged row has
            // to divide the summed weights after merging; averaging the already-divided per-row
            // averages (or picking one member's) reports a speed no download actually reached.
            var speedWeightsByRowId = new Dictionary<string, RetroSpeedWeights>(StringComparer.Ordinal);
            // A merged no-depot group stands for every download of one title on one client, so the
            // columns it was keyed on travel with it and FillDownloadIdsAsync reads them back.
            var noDepotByRowId = new Dictionary<string, List<RetroNoDepotIdentity>>(StringComparer.Ordinal);
            for (var i = 0; i < grouped.Count; i++)
            {
                speedWeightsByRowId[grouped[i].Id] = new RetroSpeedWeights
                {
                    WeightedSpeedSum = groupedRows[i].WeightedSpeedSum,
                    SpeedBytesSum = groupedRows[i].SpeedBytesSum
                };
                if (mergeGroups && groupedRows[i].DepotId == null)
                {
                    noDepotByRowId[grouped[i].Id] =
                    [
                        new RetroNoDepotIdentity(
                            groupedRows[i].Service,
                            groupedRows[i].ClientIp,
                            keyedGameNames[i],
                            groupedRows[i].GameAppId,
                            groupedRows[i].EpicAppId,
                            groupedRows[i].XboxProductId)
                    ];
                }
            }

            // Filter: search by game name / service / depot / app id / client (all group-level
            // fields). Unmapped Steam content carries the synthetic "Unknown/Other" name from
            // ResolveRetroAppName, so searching "unknown" or "other" reaches rows whose own
            // columns hold neither word.
            if (!string.IsNullOrEmpty(query.Search))
            {
                var searchLower = query.Search.ToLower();
                grouped = grouped
                    .Where(r => r.AppName.ToLower().Contains(searchLower)
                             || r.Service.ToLower().Contains(searchLower)
                             || (r.DepotId.HasValue && r.DepotId.Value.ToString().Contains(searchLower))
                             || (r.SteamAppId.HasValue && r.SteamAppId.Value.ToString().Contains(searchLower))
                             || r.ClientIp.Contains(searchLower))
                    .ToList();
            }

            // Filter: hide unknown games. Mirror the Normal/Compact views - remove only the
            // unmapped-Steam "Unknown/Other" rows. Known services (WSUS, Riot, Epic, ...) keep
            // their rows even when they carry no per-title name.
            if (query.HideUnknown)
            {
                grouped = grouped
                    .Where(r => !string.Equals(r.AppName, UnknownOtherAppName, StringComparison.Ordinal))
                    .ToList();
            }

            // Filter: hit/miss bucket, byte-weighted CacheHitPercent >= 50 = "hit", < 50 = "miss".
            // Applied to the aggregated per (DepotId, ClientIp) group, before the GroupByGame merge
            // and before pagination, so TotalItems/TotalPages reflect the filtered set.
            if (query.HitMiss == "hit")
            {
                grouped = grouped.Where(r => r.CacheHitPercent >= 50).ToList();
            }
            else if (query.HitMiss == "miss")
            {
                grouped = grouped.Where(r => r.CacheHitPercent < 50).ToList();
            }

            // Track which (DepotId, ClientIp) pairs make up each row so the page's DownloadIds
            // can be fetched after pagination. Game-merged rows span multiple pairs.
            var pairsByRowId = grouped
                .Where(r => r.DepotId.HasValue)
                .ToDictionary(
                    r => r.Id,
                    r => new List<(long DepotId, string ClientIp)> { (r.DepotId!.Value, r.ClientIp) },
                    StringComparer.Ordinal);

            // Which depot group holds each merged bucket's newest download, so the page's primary
            // rows can be fetched afterwards for the page's rows only.
            var newestMemberByRowId = new Dictionary<string, RetroDownloadDto>(StringComparer.Ordinal);

            // Merge by service (coarsest, wins over GroupByGame) or by game (in-memory over the
            // depot-group list). GroupByService intentionally overrides GroupByGame rather than
            // combining with it - a per-service total already subsumes a per-game breakdown.
            List<RetroDownloadDto> effectiveList;
            if (query.GroupByService || query.GroupByGame)
            {
                var mergedBuckets = new Dictionary<string, List<RetroDownloadDto>>(StringComparer.Ordinal);
                var bucketKinds = new Dictionary<string, RetroBucketKind>(StringComparer.Ordinal);
                var bucketOrder = new List<string>();

                foreach (var row in grouped)
                {
                    // Fold xboxlive/microsoft into xbox for the merge key so a service (or
                    // per-game-within-service) bucket never splits into separate rows just
                    // because the raw LogEntries.Service alias differs. Display-only.
                    var normalizedService = ServiceBreakdownMerger.NormalizeXboxService(row.Service);

                    string mergeKey;
                    var bucketKind = RetroBucketKind.Game;
                    if (query.GroupByService)
                    {
                        mergeKey = normalizedService;
                    }
                    else if (query.MergeAcrossServices)
                    {
                        // The grouped Downloads views key a game bucket on the title alone, so one
                        // game seen under two services stays a single row. Their key spellings are
                        // reproduced exactly, because a group id is what the views use to remember
                        // which rows are expanded.
                        if (row.SteamAppId.HasValue && row.SteamAppId.Value != 0)
                        {
                            mergeKey = $"game-appid-{row.SteamAppId.Value}";
                        }
                        else if (IsRealGameName(row.AppName, row.Service))
                        {
                            mergeKey = $"game-{row.AppName}";
                        }
                        else if (query.GroupUnknownGames
                                 && string.Equals(row.AppName, UnknownOtherAppName, StringComparison.Ordinal))
                        {
                            // ResolveRetroAppName gives this name to unmapped Steam content and
                            // nothing else, so the name is the test.
                            mergeKey = "unknown-other";
                            bucketKind = RetroBucketKind.Unknown;
                        }
                        else
                        {
                            // The folded service, so the xbox aliases share the one bucket they
                            // already share a display name with instead of showing three rows
                            // under the same label. wsus is outside that fold and keeps its own
                            // bucket - it carries mixed Windows Update traffic.
                            mergeKey = $"service-{normalizedService.ToLowerInvariant()}";
                            bucketKind = RetroBucketKind.Service;
                        }
                    }
                    else if (row.SteamAppId.HasValue && row.SteamAppId.Value != 0)
                    {
                        mergeKey = $"{normalizedService}-app-{row.SteamAppId.Value}";
                    }
                    else if (!string.IsNullOrEmpty(row.EpicAppId))
                    {
                        mergeKey = $"{normalizedService}-epic-{row.EpicAppId}";
                    }
                    else if (!string.IsNullOrEmpty(row.AppName) && row.AppName != row.Service)
                    {
                        mergeKey = $"{normalizedService}-name-{row.AppName.ToLowerInvariant()}";
                    }
                    else
                    {
                        // No resolved game identity (e.g. WSUS/Windows Update, unmapped
                        // depots). Collapse every such row for this service into a single
                        // per-service bucket so "group by game" groups them together instead
                        // of showing one row per depot/client. The merged row renders
                        // aggregated depot/client counts.
                        mergeKey = $"{normalizedService}-unknown";
                    }

                    if (!mergedBuckets.TryGetValue(mergeKey, out var bucket))
                    {
                        bucket = new List<RetroDownloadDto>();
                        mergedBuckets[mergeKey] = bucket;
                        bucketKinds[mergeKey] = bucketKind;
                        bucketOrder.Add(mergeKey);
                    }
                    bucket.Add(row);
                }

                effectiveList = bucketOrder.Select(key =>
                {
                    var bucket = mergedBuckets[key];
                    var first = bucket[0];
                    var mergedHitBytes = bucket.Sum(r => r.CacheHitBytes);
                    var mergedMissBytes = bucket.Sum(r => r.CacheMissBytes);
                    var mergedTotalBytes = bucket.Sum(r => r.TotalBytes);
                    var mergedCacheHitPercent = mergedTotalBytes > 0 ? (mergedHitBytes * 100.0) / mergedTotalBytes : 0;

                    var clientIpsSet = new HashSet<string>(StringComparer.Ordinal);
                    var depotIdsSet = new HashSet<uint>();
                    var allDownloadIds = new List<long>();
                    var mergedPairs = new List<(long DepotId, string ClientIp)>();
                    var mergedNoDepot = new List<RetroNoDepotIdentity>();
                    var mergedWeightedSpeedSum = 0d;
                    var mergedSpeedBytesSum = 0d;
                    foreach (var row in bucket)
                    {
                        foreach (var ip in row.ClientIps) clientIpsSet.Add(ip);
                        foreach (var did in row.DepotIds) depotIdsSet.Add(did);
                        allDownloadIds.AddRange(row.DownloadIds);
                        if (pairsByRowId.TryGetValue(row.Id, out var rowPairs))
                        {
                            mergedPairs.AddRange(rowPairs);
                        }
                        if (noDepotByRowId.TryGetValue(row.Id, out var rowNoDepot))
                        {
                            mergedNoDepot.AddRange(rowNoDepot);
                        }
                        if (speedWeightsByRowId.TryGetValue(row.Id, out var rowWeights))
                        {
                            mergedWeightedSpeedSum += rowWeights.WeightedSpeedSum;
                            mergedSpeedBytesSum += rowWeights.SpeedBytesSum;
                        }
                    }

                    // A merged row is fully evicted only when every member row is; it is partially
                    // evicted when any member carries eviction but the whole bucket does not.
                    var mergedFullyEvicted = bucket.All(r => r.IsEvicted);
                    var mergedAnyEvicted = bucket.Any(r => r.IsEvicted || r.IsPartiallyEvicted);
                    if (mergedPairs.Count > 0)
                    {
                        pairsByRowId[key] = mergedPairs;
                    }
                    if (mergedNoDepot.Count > 0)
                    {
                        noDepotByRowId[key] = mergedNoDepot;
                    }

                    var displayService = ServiceBreakdownMerger.NormalizeXboxService(first.Service);

                    // A service-level bucket can span many depots/apps across many games, so a
                    // single member row's DepotId/EpicAppId/SteamAppId/AppName would be
                    // misleading here - null them out and show the service name instead. The
                    // Unknown/Other bucket spans several clients of one service and shows a
                    // neutral service so it renders its own icon rather than Steam's.
                    var kind = bucketKinds[key];
                    var hideRowIdentity = query.GroupByService || kind != RetroBucketKind.Game;
                    // A service bucket is keyed on the folded service, so it has to show that same
                    // folded name or the row would be labeled with one alias of the three it
                    // holds. A game bucket names the service its title was logged under.
                    var bucketService = query.MergeAcrossServices && kind == RetroBucketKind.Game
                        ? first.Service
                        : displayService;
                    // A Steam bucket whose depots never mapped to a title still knows which app it
                    // is, so it is named by that app id rather than shown as unidentified content.
                    // Only ResolveRetroAppName mints that placeholder, and only for Steam, so the
                    // name is the test. The row-level name is untouched, which is what the search
                    // and the hide-unknown filter read, and both run before this merge.
                    var gameBucketName = first.SteamAppId is > 0
                        && string.Equals(first.AppName, UnknownOtherAppName, StringComparison.Ordinal)
                        ? $"Steam App {first.SteamAppId.Value}"
                        : first.AppName;
                    var bucketName = kind switch
                    {
                        RetroBucketKind.Unknown => UnknownOtherAppName,
                        RetroBucketKind.Service => bucketService,
                        _ => query.GroupByService ? displayService : gameBucketName
                    };
                    // The collapsed row is drawn from the newest member, and its title is hidden
                    // unless SOME member resolved a name, which the newest one alone cannot say.
                    var newestMember = bucket
                        .OrderByDescending(r => r.LastStartTimeUtc)
                        .ThenBy(r => r.Id, StringComparer.Ordinal)
                        .First();
                    newestMemberByRowId[key] = newestMember;
                    // Not IsRealGameName: the views hide the title outright when this is false, so
                    // a bucket named "Steam App 620" or "Epic Games" would render with no title at
                    // all rather than with a weaker one. The flag asks only whether some member
                    // carries a name of its own, which is what a title needs.
                    var hasRealGameName = kind == RetroBucketKind.Unknown
                        || bucket.Any(r => !string.IsNullOrWhiteSpace(r.AppName)
                            && !string.Equals(r.AppName, UnknownOtherAppName, StringComparison.Ordinal)
                            && !string.Equals(r.AppName, r.Service, StringComparison.OrdinalIgnoreCase));
                    // No "metadata" here, and that is not an omission: the views give that type to
                    // a zero-byte group, and ApplyEmptySessionFilter drops every zero-byte
                    // completed row before this endpoint ever sees it.
                    var groupType = query.MergeAcrossServices
                        ? (kind == RetroBucketKind.Game ? "game" : "content")
                        : string.Empty;

                    return new RetroDownloadDto
                    {
                        Id = key,
                        DepotId = hideRowIdentity ? null : first.DepotId,
                        EpicAppId = hideRowIdentity ? null : first.EpicAppId,
                        ClientIp = first.ClientIp,
                        Service = kind == RetroBucketKind.Unknown ? "unknown" : bucketService,
                        Datasource = first.Datasource,
                        AppName = bucketName,
                        SteamAppId = hideRowIdentity ? null : first.SteamAppId,
                        StartTimeUtc = bucket.Min(r => r.StartTimeUtc),
                        LastStartTimeUtc = bucket.Max(r => r.LastStartTimeUtc),
                        EndTimeUtc = bucket.Max(r => r.EndTimeUtc),
                        CacheHitBytes = mergedHitBytes,
                        CacheMissBytes = mergedMissBytes,
                        CacheHitPercent = Math.Round(mergedCacheHitPercent, 1),
                        TotalBytes = mergedTotalBytes,
                        AverageBytesPerSecond = mergedSpeedBytesSum > 0
                            ? mergedWeightedSpeedSum / mergedSpeedBytesSum
                            : 0,
                        RequestCount = bucket.Sum(r => r.RequestCount),
                        DownloadIds = allDownloadIds,
                        ClientIps = clientIpsSet.ToList(),
                        DepotIds = depotIdsSet.ToList(),
                        IsEvicted = mergedFullyEvicted,
                        IsPartiallyEvicted = mergedAnyEvicted && !mergedFullyEvicted,
                        GroupType = groupType,
                        HasRealGameName = hasRealGameName
                    };
                }).ToList();
            }
            else
            {
                effectiveList = grouped;
            }

            // Groups with more than one download sort ahead of single-download groups. These five
            // sorts impose their own full order, so they never bucket. When no bucketing applies
            // the key is constant, and OrderBy is stable, so the ThenBy chain below is left in
            // sole charge and the order is exactly what a plain OrderBy would give.
            var bucketByFrequency = query.GroupByFrequency
                && query.Sort is not ("service" or "alphabetical" or "efficiency" or "efficiency-low" or "sessions");
            var bucketed = effectiveList.OrderBy(g => bucketByFrequency && g.RequestCount == 1 ? 1 : 0);

            // The grouped Downloads views order by the newest member's START time; retro orders by
            // the group's latest END time. One list, two columns, chosen per request.
            //
            // The two sorts that order text order the text on screen, not the key underneath it: a
            // nameless service row shows its service's title rather than the bare service, and the
            // service badge shows the folded service rather than the alias the log used. Ordering
            // the keys files a row somewhere the reader has no reason to look for it.
            effectiveList = query.Sort switch
            {
                "oldest" => bucketed.ThenBy(g => g.StartTimeUtc).ToList(),
                "largest" => bucketed.ThenByDescending(g => g.TotalBytes).ToList(),
                "smallest" => bucketed.ThenBy(g => g.TotalBytes).ToList(),
                "efficiency" => bucketed.ThenByDescending(g => g.CacheHitPercent).ToList(),
                "efficiency-low" => bucketed.ThenBy(g => g.CacheHitPercent).ToList(),
                "sessions" => bucketed.ThenByDescending(g => g.RequestCount).ToList(),
                "alphabetical" => bucketed
                    .ThenBy(
                        g => g.Id.StartsWith("service-", StringComparison.Ordinal)
                            ? ServiceBreakdownMerger.ServiceGroupTitle(g.Service)
                            : g.AppName,
                        StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                "service" => bucketed.ThenBy(g => ServiceBreakdownMerger.NormalizeXboxService(g.Service))
                    .ThenByDescending(g => query.MergeAcrossServices ? g.LastStartTimeUtc : g.EndTimeUtc)
                    .ToList(),
                // "recent" and "latest" are both pure chronological (newest first)
                _ => bucketed.ThenByDescending(g => query.MergeAcrossServices ? g.LastStartTimeUtc : g.EndTimeUtc).ToList(),
            };

            // Paginate from the post-merge list
            var totalItems = effectiveList.Count;
            // Every row carries the downloads it stands for, and the whole filtered list is already
            // here, so the download count is a sum over it rather than a second pass over the table.
            var totalDownloads = effectiveList.Sum(g => g.RequestCount);
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)query.PageSize));
            var items = effectiveList
                .Skip((query.Page - 1) * query.PageSize)
                .Take(query.PageSize)
                .ToList();

            var newestNoDepotDownloadIds = await FillDownloadIdsAsync(query, items, pairsByRowId, noDepotByRowId);

            if (query.MergeAcrossServices)
            {
                await FillPrimaryDownloadsAsync(
                    query, items, newestMemberByRowId, noDepotByRowId, newestNoDepotDownloadIds);
            }

            return Ok(new RetroDownloadResponse
            {
                Items = items,
                TotalItems = totalItems,
                TotalDownloads = totalDownloads,
                TotalPages = totalPages,
                CurrentPage = query.Page,
                PageSize = query.PageSize
            });
        }
        // A client that navigates away cancels HttpContext.RequestAborted, which the name
        // resolution above observes. That is not a failure to report as an empty page, so it is
        // left to the middleware, which answers 499 without logging an error.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error getting retro downloads");
            return Ok(new RetroDownloadResponse());
        }
    }

    /// <summary>
    /// SQL-side aggregate projection for one retro group (DepotId + ClientIp, or a single
    /// no-depot download identified by RowKey).
    /// </summary>
    private sealed class RetroGroupRow : IGameNameRow
    {
        public long? DepotId { get; set; }
        public string ClientIp { get; set; } = string.Empty;
        public long RowKey { get; set; }
        public string Service { get; set; } = string.Empty;
        public string Datasource { get; set; } = string.Empty;
        public string? GameName { get; set; }
        public long? GameAppId { get; set; }
        public string? EpicAppId { get; set; }
        public string? XboxProductId { get; set; }
        public long CacheHitBytes { get; set; }
        public long CacheMissBytes { get; set; }
        public DateTime StartTimeUtc { get; set; }
        public DateTime LastStartTimeUtc { get; set; }
        public DateTime EndTimeUtc { get; set; }
        public int RequestCount { get; set; }
        public int EvictedCount { get; set; }
        public double WeightedSpeedSum { get; set; }
        public double SpeedBytesSum { get; set; }
    }

    /// <summary>
    /// What a merged bucket stands for, which decides whether its row can carry a depot and app
    /// id and which name and service it shows.
    /// </summary>
    private enum RetroBucketKind
    {
        /// <summary>One identified game.</summary>
        Game,
        /// <summary>Every unmapped Steam row, folded together.</summary>
        Unknown,
        /// <summary>Every nameless row of one service.</summary>
        Service
    }

    /// <summary>
    /// Byte-weighted speed accumulator for one retro group, kept out of the wire DTO. Merged rows
    /// sum these across their member rows and divide once, so the merged speed stays byte-weighted
    /// over every underlying download.
    /// </summary>
    private sealed class RetroSpeedWeights
    {
        public double WeightedSpeedSum { get; set; }
        public double SpeedBytesSum { get; set; }
    }

    /// <summary>
    /// The columns a no-depot download is grouped on under the merged aggregate. A group is no
    /// longer one download there, so the downloads behind the page's rows are fetched back on
    /// these columns after pagination.
    /// </summary>
    private sealed record RetroNoDepotIdentity(
        string Service,
        string ClientIp,
        string? GameName,
        long? GameAppId,
        string? EpicAppId,
        string? XboxProductId);

    private const string UnknownOtherAppName = "Unknown/Other";

    /// <summary>
    /// Whether a download row is Steam content whose game could not be identified: either no game
    /// name at all, or a name that is just the service name echoed back. Deliberately scoped to
    /// Steam. WSUS and bare-metal traffic is nameless by design, so treating "no game name" as a
    /// mapping failure across every service reports a healthy install as broken.
    /// </summary>
    public static bool IsUnmappedSteam(string service, string? gameName) =>
        string.Equals(service, "steam", StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(gameName)
                || string.Equals(gameName, service, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a resolved group name names a game rather than echoing the service back or standing
    /// in for content that could not be identified. The Steam placeholder and the per-service
    /// fallback labels are read from <see cref="RustSpeedTrackerService"/>, which already owns both
    /// tables, so the browser's copy of this rule has one counterpart here instead of several.
    /// </summary>
    internal static bool IsRealGameName(string appName, string service) =>
        !string.IsNullOrWhiteSpace(appName)
        && !string.Equals(appName, UnknownOtherAppName, StringComparison.Ordinal)
        && !string.Equals(appName, service, StringComparison.OrdinalIgnoreCase)
        && !RustSpeedTrackerService._steamAppPlaceholder.IsMatch(appName)
        && !(RustSpeedTrackerService._serviceFallbackLabels.TryGetValue(service.ToLowerInvariant(), out var fallbackLabel)
             && string.Equals(appName, fallbackLabel, StringComparison.Ordinal));

    /// <summary>
    /// Display name for a retro group. Unmapped Steam content (a Steam row with no resolved
    /// game name) is surfaced as "Unknown/Other" to match the Normal/Compact views. Every
    /// other service keeps its service-name fallback.
    /// </summary>
    private static string ResolveRetroAppName(string service, string? gameName)
    {
        return IsUnmappedSteam(service, gameName) ? UnknownOtherAppName : (gameName ?? service);
    }

    /// <summary>
    /// Builds the filtered (row-level) retro query. Shared by the aggregate query and the
    /// page-level DownloadIds detail query so both see exactly the same rows.
    /// </summary>
    private IQueryable<Download> BuildRetroBaseQuery(RetroDownloadQuery query)
    {
        var hiddenClientIps = _stateRepository.GetHiddenClientIps();

        var baseQuery = _context.Downloads.AsNoTracking();

        // Only completed downloads for the retro view. The grouped Downloads views ask for the
        // running one as well, so it stays on screen while it downloads instead of appearing only
        // once it finishes.
        if (!query.IncludeActive)
        {
            baseQuery = baseQuery.Where(d => !d.IsActive);
        }

        // Exclude hidden client IPs
        if (hiddenClientIps.Count > 0)
        {
            baseQuery = baseQuery.Where(d => !hiddenClientIps.Contains(d.ClientIp));
        }

        // Apply eviction filter (hide/remove modes exclude evicted downloads)
        var evictedMode = _stateRepository.GetEvictedDataMode();
        baseQuery = baseQuery.ApplyEvictedFilter(evictedMode).ApplyEmptySessionFilter();

        // Filter: hide evicted for this reader. The stored mode above hides them for every reader,
        // so this only bites while that mode is showing them.
        if (query.HideEvicted)
        {
            baseQuery = baseQuery.Where(d => !d.IsEvicted);
        }

        // Filter: time range
        if (query.StartTime.HasValue || query.EndTime.HasValue)
        {
            var startDate = query.StartTime.HasValue
                ? query.StartTime.Value.FromUnixSeconds()
                : DateTime.MinValue;
            var endDate = query.EndTime.HasValue
                ? query.EndTime.Value.FromUnixSeconds()
                : DateTime.UtcNow;
            baseQuery = baseQuery.Where(d => d.StartTimeUtc >= startDate && d.StartTimeUtc <= endDate);
        }

        // Filter: event tag (only downloads associated with the event)
        if (query.EventId.HasValue)
        {
            var eventId = query.EventId.Value;
            baseQuery = baseQuery.Where(d => _context.EventDownloads
                .Where(ed => ed.EventId == eventId)
                .Select(ed => ed.DownloadId)
                .Contains(d.Id));
        }

        // Filter: hide localhost
        if (query.HideLocalhost)
        {
            baseQuery = baseQuery.Where(d => d.ClientIp != "127.0.0.1" && d.ClientIp != "::1");
        }

        // Filter: service
        if (!string.IsNullOrEmpty(query.Service) && query.Service != "all")
        {
            // The frontend filters by the folded display name ("xbox" covers the raw
            // "xbox"/"xboxlive"/"microsoft" aliases), so expand it back to every raw
            // LogEntries.Service value it represents instead of an exact match.
            if (ServiceBreakdownMerger.NormalizeXboxService(query.Service) == "xbox")
            {
                var xboxServiceNames = ServiceBreakdownMerger.XboxRawServiceNames;
                baseQuery = baseQuery.Where(d => xboxServiceNames.Contains(d.Service));
            }
            else
            {
                baseQuery = baseQuery.Where(d => d.Service == query.Service);
            }
        }

        // Filter: client IP
        if (!string.IsNullOrEmpty(query.Client) && query.Client != "all")
        {
            var clientIps = query.Client.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            baseQuery = baseQuery.Where(d => clientIps.Contains(d.ClientIp));
        }

        // Filter: hide zero-byte downloads
        if (!query.ShowZeroBytes)
        {
            baseQuery = baseQuery.Where(d => (d.CacheHitBytes + d.CacheMissBytes) > 0);
        }

        // Filter: hide downloads under 1 MB. The sum is written out because TotalBytes is computed
        // from these two columns and no column holds it. Completed zero-byte sessions are already
        // gone by here, so the threshold is the whole test.
        if (query.HideSmallFiles)
        {
            baseQuery = baseQuery.Where(d => (d.CacheHitBytes + d.CacheMissBytes) >= 1048576);
        }

        return baseQuery;
    }

    /// <summary>
    /// Aggregates per (DepotId, ClientIp) so only group-level scalars cross the wire. No-depot rows
    /// keep one group per row via RowKey = Id (matches the historical "no-depot-{service}-{ip}-{id}"
    /// key). Raw download rows are never materialized here.
    /// </summary>
    private IQueryable<RetroGroupRow> BuildRetroGroupedQuery(RetroDownloadQuery query)
    {
        return SelectRetroGroupRows(BuildRetroBaseQuery(query)
            .GroupBy(d => new
            {
                d.DepotId,
                d.ClientIp,
                RowKey = d.DepotId == null ? d.Id : 0L
            }));
    }

    /// <summary>
    /// The same aggregate one grain coarser, for the requests that merge groups into game or
    /// service buckets. A no-depot download is keyed on the identity the merge folds it under
    /// rather than on its own id, so a range full of Windows Update or Xbox traffic reads one
    /// group per title per client instead of one per download. Steam keeps the depot-and-client
    /// grain, because the hit/miss bucket, the search over depot ids and the unknown-game rule all
    /// read a depot group the way they do today.
    /// </summary>
    private IQueryable<RetroGroupRow> BuildRetroMergedQuery(RetroDownloadQuery query)
    {
        return SelectRetroGroupRows(BuildRetroBaseQuery(query)
            .GroupBy(d => new
            {
                d.DepotId,
                d.ClientIp,
                Service = d.DepotId == null ? d.Service : null,
                GameName = d.DepotId == null ? d.GameName : null,
                GameAppId = d.DepotId == null ? d.GameAppId : null,
                EpicAppId = d.DepotId == null ? d.EpicAppId : null,
                XboxProductId = d.DepotId == null ? d.XboxProductId : null
            }));
    }

    /// <summary>
    /// The group-level scalars both retro aggregates return. Every field is an aggregate rather
    /// than a key member, so the two group keys share one projection and cannot drift apart.
    /// DepotId, ClientIp and the row key sit in both keys, so their aggregate is that key value.
    /// </summary>
    private static IQueryable<RetroGroupRow> SelectRetroGroupRows<TKey>(
        IQueryable<IGrouping<TKey, Download>> grouped)
    {
        return grouped
            .Select(g => new RetroGroupRow
            {
                DepotId = g.Min(d => d.DepotId),
                ClientIp = g.Min(d => d.ClientIp)!,
                // Zero for a depot group, and the oldest member's id for a no-depot group, which
                // is what its row id is spelled with. Under the merged key that group can hold
                // more than one download, so the ids behind it are fetched for the page's rows.
                RowKey = g.Min(d => d.DepotId == null ? d.Id : 0L),
                Service = g.Max(d => d.Service)!,
                Datasource = g.Max(d => d.Datasource)!,
                GameName = g.Max(d => d.GameName),
                GameAppId = g.Max(d => d.GameAppId),
                EpicAppId = g.Max(d => d.EpicAppId),
                XboxProductId = g.Max(d => d.XboxProductId),
                CacheHitBytes = g.Sum(d => d.CacheHitBytes),
                CacheMissBytes = g.Sum(d => d.CacheMissBytes),
                StartTimeUtc = g.Min(d => d.StartTimeUtc),
                LastStartTimeUtc = g.Max(d => d.StartTimeUtc),
                EndTimeUtc = g.Max(d => d.EndTimeUtc),
                RequestCount = g.Count(),
                EvictedCount = g.Sum(d => d.IsEvicted ? 1 : 0),
                // Per-row speed is TotalBytes / (EndTime - StartTime); weight it by bytes the
                // same way the previous in-memory grouping did.
                WeightedSpeedSum = g.Sum(d =>
                    (d.EndTimeUtc - d.StartTimeUtc).TotalSeconds > 0
                        ? ((d.CacheHitBytes + d.CacheMissBytes)
                           / (d.EndTimeUtc - d.StartTimeUtc).TotalSeconds)
                          * (d.CacheHitBytes + d.CacheMissBytes)
                        : 0),
                SpeedBytesSum = g.Sum(d =>
                    (d.EndTimeUtc - d.StartTimeUtc).TotalSeconds > 0
                    && (d.CacheHitBytes + d.CacheMissBytes) > 0
                        ? (double)(d.CacheHitBytes + d.CacheMissBytes)
                        : 0)
            });
    }

    /// <summary>
    /// One page of grouped rows, ordered by the database. The frequency bucket and the sort repeat
    /// the expressions <see cref="GetRetroDownloadsAsync"/> applies in memory, over the same
    /// columns; the group key closes the order so equal sort keys cannot drift between the two
    /// paths and move a page boundary. The four sorts with no column behind them never reach here
    /// - the caller keeps those on the in-memory path.
    /// </summary>
    private IQueryable<RetroGroupRow> BuildRetroPagedQuery(RetroDownloadQuery query)
    {
        // "sessions" is the only sort that both reaches this method and imposes its own full
        // order; the other four the in-memory rule lists are all on the in-memory path already.
        var bucketByFrequency = query.GroupByFrequency && query.Sort != "sessions";
        var bucketed = BuildRetroGroupedQuery(query)
            .OrderBy(r => bucketByFrequency && r.RequestCount == 1 ? 1 : 0);

        var ordered = query.Sort switch
        {
            "oldest" => bucketed.ThenBy(r => r.StartTimeUtc),
            "largest" => bucketed.ThenByDescending(r => r.CacheHitBytes + r.CacheMissBytes),
            "smallest" => bucketed.ThenBy(r => r.CacheHitBytes + r.CacheMissBytes),
            "sessions" => bucketed.ThenByDescending(r => r.RequestCount),
            // "recent" and "latest" are both pure chronological (newest first). MergeAcrossServices
            // keeps a request off this path, so the group's latest end time is the only column the
            // in-memory sort can be reading here.
            _ => bucketed.ThenByDescending(r => r.EndTimeUtc)
        };

        return ordered
            .ThenBy(r => r.DepotId)
            .ThenBy(r => r.ClientIp)
            .ThenBy(r => r.RowKey)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize);
    }

    /// <summary>
    /// The wire row for one aggregated group, after its game name has been resolved.
    /// <paramref name="noDepotIsOneDownload"/> says whether a no-depot group stands for the single
    /// download its row key names, which is true of every group the depot-and-client aggregate
    /// returns and false under the merged key, where the ids are fetched for the page instead.
    /// </summary>
    private static RetroDownloadDto BuildRetroRow(RetroGroupRow r, bool maskEviction, bool noDepotIsOneDownload)
    {
        var totalBytes = r.CacheHitBytes + r.CacheMissBytes;
        var fullyEvicted = !maskEviction && r.EvictedCount == r.RequestCount;
        var anyEvicted = !maskEviction && r.EvictedCount > 0;
        return new RetroDownloadDto
        {
            Id = r.DepotId.HasValue
                ? $"depot-{r.DepotId.Value}-{r.ClientIp}"
                : $"no-depot-{r.Service}-{r.ClientIp}-{r.RowKey}",
            DepotId = r.DepotId,
            EpicAppId = r.EpicAppId,
            ClientIp = r.ClientIp,
            Service = r.Service,
            Datasource = r.Datasource,
            AppName = ResolveRetroAppName(r.Service, r.GameName),
            SteamAppId = r.GameAppId,
            StartTimeUtc = r.StartTimeUtc,
            LastStartTimeUtc = r.LastStartTimeUtc,
            EndTimeUtc = r.EndTimeUtc,
            CacheHitBytes = r.CacheHitBytes,
            CacheMissBytes = r.CacheMissBytes,
            CacheHitPercent = totalBytes > 0
                ? Math.Round((r.CacheHitBytes * 100.0) / totalBytes, 1)
                : 0,
            TotalBytes = totalBytes,
            AverageBytesPerSecond = r.SpeedBytesSum > 0 ? r.WeightedSpeedSum / r.SpeedBytesSum : 0,
            RequestCount = r.RequestCount,
            // Depot groups get their DownloadIds filled after pagination (page rows only);
            // a no-depot group under the depot-and-client key is a single download whose id IS
            // the row key, and under the merged key it is filled after pagination as well.
            DownloadIds = noDepotIsOneDownload && r.RowKey != 0
                ? new List<long> { r.RowKey }
                : new List<long>(),
            ClientIps = new List<string> { r.ClientIp },
            DepotIds = r.DepotId.HasValue && r.DepotId.Value != 0
                ? new List<uint> { (uint)r.DepotId.Value }
                : new List<uint>(),
            IsEvicted = fullyEvicted,
            IsPartiallyEvicted = anyEvicted && !fullyEvicted
        };
    }

    /// <summary>
    /// Fetches one download per row on the current page: the newest member of each group, which is
    /// what the grouped views draw a collapsed row from. Deliberately not the group's members - a
    /// game group can hold thousands of downloads, so those are fetched per expanded group through
    /// <see cref="GetByIdsAsync"/> instead. Two queries, both bounded by the page: the newest id in
    /// each group's newest depot group, then the rows for those ids.
    /// </summary>
    private async Task FillPrimaryDownloadsAsync(
        RetroDownloadQuery query,
        List<RetroDownloadDto> pageItems,
        Dictionary<string, RetroDownloadDto> newestMemberByRowId,
        Dictionary<string, List<RetroNoDepotIdentity>> noDepotByRowId,
        Dictionary<RetroNoDepotIdentity, long> newestNoDepotDownloadIds)
    {
        var pairs = new HashSet<(long DepotId, string ClientIp)>();
        var primaryIdByRowId = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in pageItems)
        {
            if (!newestMemberByRowId.TryGetValue(item.Id, out var member)) continue;
            if (member.DepotId.HasValue)
            {
                pairs.Add((member.DepotId.Value, member.ClientIp));
            }
            else if (member.DownloadIds.Count > 0)
            {
                // A no-depot group under the depot-and-client aggregate is a single download and
                // already names it.
                primaryIdByRowId[item.Id] = member.DownloadIds[0];
            }
            else if (noDepotByRowId.TryGetValue(member.Id, out var memberIdentities)
                     && memberIdentities.Count == 1
                     && newestNoDepotDownloadIds.TryGetValue(memberIdentities[0], out var newestId))
            {
                // A no-depot group under the merged aggregate covers several downloads, and the
                // id fetch for this page already found the newest of them.
                primaryIdByRowId[item.Id] = newestId;
            }
        }

        var primaryIdByPair = new Dictionary<(long DepotId, string ClientIp), long>();
        if (pairs.Count > 0)
        {
            var depotIdList = pairs.Select(p => p.DepotId).Distinct().ToList();
            var clientIpList = pairs.Select(p => p.ClientIp).Distinct().ToList();

            var candidates = await BuildRetroBaseQuery(query)
                .Where(d => d.DepotId != null
                         && depotIdList.Contains(d.DepotId.Value)
                         && clientIpList.Contains(d.ClientIp))
                .Select(d => new { d.Id, DepotId = d.DepotId!.Value, d.ClientIp, d.StartTimeUtc })
                .ToListAsync();

            foreach (var pairGroup in candidates
                         .Where(c => pairs.Contains((c.DepotId, c.ClientIp)))
                         .GroupBy(c => (c.DepotId, c.ClientIp)))
            {
                primaryIdByPair[pairGroup.Key] = pairGroup
                    .OrderByDescending(c => c.StartTimeUtc)
                    .ThenByDescending(c => c.Id)
                    .First().Id;
            }

            foreach (var item in pageItems)
            {
                if (!newestMemberByRowId.TryGetValue(item.Id, out var member)) continue;
                if (!member.DepotId.HasValue) continue;
                if (primaryIdByPair.TryGetValue((member.DepotId.Value, member.ClientIp), out var id))
                {
                    primaryIdByRowId[item.Id] = id;
                }
            }
        }

        if (primaryIdByRowId.Count == 0) return;

        var primaryIds = primaryIdByRowId.Values.Distinct().ToList();
        var rows = await _context.Downloads
            .AsNoTracking()
            .Where(d => primaryIds.Contains(d.Id))
            .ToListAsync();

        await GameNameResolver.ResolveAsync(_context, rows, HttpContext.RequestAborted);

        if (_stateRepository.GetEvictedDataMode() == EvictedDataMode.ShowClean.ToWireString())
        {
            foreach (var row in rows) row.IsEvicted = false;
        }

        var rowsById = rows.ToDictionary(r => r.Id);
        foreach (var item in pageItems)
        {
            if (primaryIdByRowId.TryGetValue(item.Id, out var primaryId)
                && rowsById.TryGetValue(primaryId, out var row))
            {
                row.WithUtcMarking();
                item.PrimaryDownload = row;
            }
        }
    }

    /// <summary>
    /// The download rows behind a set of ids, newest first. Serves an expanded group's member
    /// list, which is why it takes ids rather than a filter: the caller already holds them on the
    /// grouped row, and only the one group a user opened is ever fetched.
    /// </summary>
    [HttpPost("by-ids")]
    [ProducesResponseType(typeof(List<Download>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<Download>>> GetByIdsAsync([FromBody] BatchDownloadEventsRequest request)
    {
        if (request.DownloadIds.Count == 0)
        {
            return Ok(new List<Download>());
        }

        // Same ceiling as the event batch above, for the same reason: a group can name far more
        // ids than anyone renders, and the views page their member list. The ceiling is applied
        // after the ordering so the rows it keeps are the newest members of the group, not
        // whichever ones the id list happened to start with. The whole id list goes to the
        // database as one array parameter, so passing more than the ceiling costs nothing.
        const int maxIds = 500;

        var hiddenClientIps = _stateRepository.GetHiddenClientIps();
        var evictedMode = _stateRepository.GetEvictedDataMode();

        var rows = await _context.Downloads
            .AsNoTracking()
            .Where(d => request.DownloadIds.Contains(d.Id))
            .Where(d => hiddenClientIps.Count == 0 || !hiddenClientIps.Contains(d.ClientIp))
            .ApplyEvictedFilter(evictedMode)
            .ApplyEmptySessionFilter()
            .OrderByDescending(d => d.StartTimeUtc)
            .ThenByDescending(d => d.Id)
            .Take(maxIds)
            .ToListAsync();

        await GameNameResolver.ResolveAsync(_context, rows, HttpContext.RequestAborted);

        if (evictedMode == EvictedDataMode.ShowClean.ToWireString())
        {
            foreach (var row in rows) row.IsEvicted = false;
        }

        foreach (var row in rows) row.WithUtcMarking();

        return Ok(rows);
    }

    /// <summary>
    /// Fetches the underlying download IDs for the current page's rows: the depot-backed ones by
    /// depot and client, and, when the merged aggregate built the groups, the no-depot ones by the
    /// identity they were keyed on. A no-depot group under the depot-and-client aggregate is a
    /// single download and already carries its id, so nothing is fetched for it.
    /// Returns the newest download behind each no-depot identity this page asked about, which
    /// <see cref="FillPrimaryDownloadsAsync"/> needs and which this fetch already holds.
    /// </summary>
    private async Task<Dictionary<RetroNoDepotIdentity, long>> FillDownloadIdsAsync(
        RetroDownloadQuery query,
        List<RetroDownloadDto> pageItems,
        Dictionary<string, List<(long DepotId, string ClientIp)>> pairsByRowId,
        Dictionary<string, List<RetroNoDepotIdentity>> noDepotByRowId)
    {
        var neededPairs = new HashSet<(long DepotId, string ClientIp)>();
        var neededIdentities = new HashSet<RetroNoDepotIdentity>();
        foreach (var item in pageItems)
        {
            if (pairsByRowId.TryGetValue(item.Id, out var pairs))
            {
                foreach (var pair in pairs) neededPairs.Add(pair);
            }
            if (noDepotByRowId.TryGetValue(item.Id, out var identities))
            {
                foreach (var identity in identities) neededIdentities.Add(identity);
            }
        }

        if (neededPairs.Count > 0)
        {
            // Over-fetch by depot list + ip list (pair-exact filtering happens in memory below);
            // both lists are bounded by the page size, so this stays a small indexed query.
            var depotIdList = neededPairs.Select(p => p.DepotId).Distinct().ToList();
            var clientIpList = neededPairs.Select(p => p.ClientIp).Distinct().ToList();

            var detailRows = await BuildRetroBaseQuery(query)
                .Where(d => d.DepotId != null
                         && depotIdList.Contains(d.DepotId.Value)
                         && clientIpList.Contains(d.ClientIp))
                .Select(d => new { d.Id, DepotId = d.DepotId!.Value, d.ClientIp })
                .ToListAsync();

            var idsByPair = new Dictionary<(long DepotId, string ClientIp), List<long>>();
            foreach (var row in detailRows)
            {
                var pair = (row.DepotId, row.ClientIp);
                if (!neededPairs.Contains(pair)) continue;
                if (!idsByPair.TryGetValue(pair, out var ids))
                {
                    ids = new List<long>();
                    idsByPair[pair] = ids;
                }
                ids.Add(row.Id);
            }

            foreach (var item in pageItems)
            {
                if (!pairsByRowId.TryGetValue(item.Id, out var pairs)) continue;
                foreach (var pair in pairs)
                {
                    if (idsByPair.TryGetValue(pair, out var ids))
                    {
                        item.DownloadIds.AddRange(ids);
                    }
                }
            }
        }

        var newestByIdentity = new Dictionary<RetroNoDepotIdentity, long>();
        if (neededIdentities.Count == 0) return newestByIdentity;

        // The same over-fetch shape as the depot pairs above: the service and client lists are
        // bounded by the page, the index behind them is the one the base query already uses, and
        // the identity-exact match happens in memory.
        var serviceList = neededIdentities.Select(i => i.Service).Distinct().ToList();
        var identityClientIps = neededIdentities.Select(i => i.ClientIp).Distinct().ToList();

        var noDepotRows = await BuildRetroBaseQuery(query)
            .Where(d => d.DepotId == null
                     && serviceList.Contains(d.Service)
                     && identityClientIps.Contains(d.ClientIp))
            .Select(d => new
            {
                d.Id,
                d.Service,
                d.ClientIp,
                d.GameName,
                d.GameAppId,
                d.EpicAppId,
                d.XboxProductId,
                d.StartTimeUtc
            })
            .ToListAsync();

        var idsByIdentity = new Dictionary<RetroNoDepotIdentity, List<long>>();
        var newestStartByIdentity = new Dictionary<RetroNoDepotIdentity, DateTime>();
        foreach (var row in noDepotRows)
        {
            var identity = new RetroNoDepotIdentity(
                row.Service, row.ClientIp, row.GameName, row.GameAppId, row.EpicAppId, row.XboxProductId);
            if (!neededIdentities.Contains(identity)) continue;
            if (!idsByIdentity.TryGetValue(identity, out var ids))
            {
                ids = new List<long>();
                idsByIdentity[identity] = ids;
            }
            ids.Add(row.Id);

            // Newest by start time, ties broken by the higher id, which is the order the grouped
            // views draw a collapsed row from.
            if (!newestStartByIdentity.TryGetValue(identity, out var bestStart)
                || row.StartTimeUtc > bestStart
                || (row.StartTimeUtc == bestStart && row.Id > newestByIdentity[identity]))
            {
                newestStartByIdentity[identity] = row.StartTimeUtc;
                newestByIdentity[identity] = row.Id;
            }
        }

        foreach (var item in pageItems)
        {
            if (!noDepotByRowId.TryGetValue(item.Id, out var identities)) continue;
            foreach (var identity in identities)
            {
                if (idsByIdentity.TryGetValue(identity, out var ids))
                {
                    item.DownloadIds.AddRange(ids);
                }
            }
        }

        return newestByIdentity;
    }
}
