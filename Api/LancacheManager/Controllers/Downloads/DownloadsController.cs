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
            AutoTagged = ed.AutoTagged,
            TaggedAtUtc = ed.TaggedAtUtc
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
    /// game names on the aggregated rows before returning them.
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
            // Aggregate per (DepotId, ClientIp) in SQL so only group-level scalars cross the
            // wire. No-depot rows keep one group per row via RowKey = Id (matches the historical
            // "no-depot-{service}-{ip}-{id}" key). Raw download rows are never materialized here.
            var groupedRows = await BuildRetroBaseQuery(query)
                .GroupBy(d => new
                {
                    d.DepotId,
                    d.ClientIp,
                    RowKey = d.DepotId == null ? d.Id : 0L
                })
                .Select(g => new RetroGroupRow
                {
                    DepotId = g.Key.DepotId,
                    ClientIp = g.Key.ClientIp,
                    RowKey = g.Key.RowKey,
                    Service = g.Max(d => d.Service)!,
                    Datasource = g.Max(d => d.Datasource)!,
                    GameName = g.Max(d => d.GameName),
                    GameAppId = g.Max(d => d.GameAppId),
                    EpicAppId = g.Max(d => d.EpicAppId),
                    XboxProductId = g.Max(d => d.XboxProductId),
                    CacheHitBytes = g.Sum(d => d.CacheHitBytes),
                    CacheMissBytes = g.Sum(d => d.CacheMissBytes),
                    StartTimeUtc = g.Min(d => d.StartTimeUtc),
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
                })
                .ToListAsync();

            // Resolve game names at group level over the same shared resolver the dashboard
            // batch uses, so both views pick the same owner when a depot has multiple mappings.
            await GameNameResolver.ResolveAsync(_context, groupedRows, HttpContext.RequestAborted);

            // ShowClean keeps evicted rows in the list but suppresses the badge and dimming, the
            // same way the row-level download list does. Hide/Remove already dropped those rows
            // in BuildRetroBaseQuery, so their counts come back zero without extra filtering.
            var maskEviction = _stateRepository.GetEvictedDataMode() == EvictedDataMode.ShowClean.ToWireString();

            var grouped = groupedRows.Select(r =>
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
                    // no-depot groups are single downloads whose id IS the row key.
                    DownloadIds = r.RowKey != 0 ? new List<long> { r.RowKey } : new List<long>(),
                    ClientIps = new List<string> { r.ClientIp },
                    DepotIds = r.DepotId.HasValue && r.DepotId.Value != 0
                        ? new List<uint> { (uint)r.DepotId.Value }
                        : new List<uint>(),
                    IsEvicted = fullyEvicted,
                    IsPartiallyEvicted = anyEvicted && !fullyEvicted
                };
            }).ToList();

            // Keep each row's byte-weighted speed accumulator alongside the DTO. A merged row has
            // to divide the summed weights after merging; averaging the already-divided per-row
            // averages (or picking one member's) reports a speed no download actually reached.
            var speedWeightsByRowId = new Dictionary<string, RetroSpeedWeights>(StringComparer.Ordinal);
            for (var i = 0; i < grouped.Count; i++)
            {
                speedWeightsByRowId[grouped[i].Id] = new RetroSpeedWeights
                {
                    WeightedSpeedSum = groupedRows[i].WeightedSpeedSum,
                    SpeedBytesSum = groupedRows[i].SpeedBytesSum
                };
            }

            // Filter: search by game name / service / depot / client (all group-level fields)
            if (!string.IsNullOrEmpty(query.Search))
            {
                var searchLower = query.Search.ToLower();
                grouped = grouped
                    .Where(r => r.AppName.ToLower().Contains(searchLower)
                             || r.Service.ToLower().Contains(searchLower)
                             || (r.DepotId.HasValue && r.DepotId.Value.ToString().Contains(searchLower))
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

            // Merge by service (coarsest, wins over GroupByGame) or by game (in-memory over the
            // depot-group list). GroupByService intentionally overrides GroupByGame rather than
            // combining with it - a per-service total already subsumes a per-game breakdown.
            List<RetroDownloadDto> effectiveList;
            if (query.GroupByService || query.GroupByGame)
            {
                var mergedBuckets = new Dictionary<string, List<RetroDownloadDto>>(StringComparer.Ordinal);
                var bucketOrder = new List<string>();

                foreach (var row in grouped)
                {
                    // Fold xboxlive/microsoft into xbox for the merge key so a service (or
                    // per-game-within-service) bucket never splits into separate rows just
                    // because the raw LogEntries.Service alias differs. Display-only.
                    var normalizedService = ServiceBreakdownMerger.NormalizeXboxService(row.Service);

                    string mergeKey;
                    if (query.GroupByService)
                    {
                        mergeKey = normalizedService;
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

                    var displayService = ServiceBreakdownMerger.NormalizeXboxService(first.Service);

                    return new RetroDownloadDto
                    {
                        Id = key,
                        // A service-level bucket can span many depots/apps across many games, so a
                        // single member row's DepotId/EpicAppId/SteamAppId/AppName would be
                        // misleading here - null them out and show the service name instead.
                        DepotId = query.GroupByService ? null : first.DepotId,
                        EpicAppId = query.GroupByService ? null : first.EpicAppId,
                        ClientIp = first.ClientIp,
                        Service = displayService,
                        Datasource = first.Datasource,
                        AppName = query.GroupByService ? displayService : first.AppName,
                        SteamAppId = query.GroupByService ? null : first.SteamAppId,
                        StartTimeUtc = bucket.Min(r => r.StartTimeUtc),
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
                        IsPartiallyEvicted = mergedAnyEvicted && !mergedFullyEvicted
                    };
                }).ToList();
            }
            else
            {
                effectiveList = grouped;
            }

            // Sort (applied to whichever list is effective)
            effectiveList = query.Sort switch
            {
                "oldest" => effectiveList.OrderBy(g => g.StartTimeUtc).ToList(),
                "largest" => effectiveList.OrderByDescending(g => g.TotalBytes).ToList(),
                "smallest" => effectiveList.OrderBy(g => g.TotalBytes).ToList(),
                "efficiency" => effectiveList.OrderByDescending(g => g.CacheHitPercent).ToList(),
                "efficiency-low" => effectiveList.OrderBy(g => g.CacheHitPercent).ToList(),
                "sessions" => effectiveList.OrderByDescending(g => g.RequestCount).ToList(),
                "alphabetical" => effectiveList.OrderBy(g => g.AppName, StringComparer.OrdinalIgnoreCase).ToList(),
                "service" => effectiveList.OrderBy(g => g.Service).ThenByDescending(g => g.EndTimeUtc).ToList(),
                // "recent" and "latest" are both pure chronological (newest end time first)
                _ => effectiveList.OrderByDescending(g => g.EndTimeUtc).ToList(),
            };

            // Paginate from the post-merge list
            var totalItems = effectiveList.Count;
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)query.PageSize));
            var items = effectiveList
                .Skip((query.Page - 1) * query.PageSize)
                .Take(query.PageSize)
                .ToList();

            await FillDownloadIdsAsync(query, items, pairsByRowId);

            return Ok(new RetroDownloadResponse
            {
                Items = items,
                TotalItems = totalItems,
                TotalPages = totalPages,
                CurrentPage = query.Page,
                PageSize = query.PageSize
            });
        }
        catch (Exception ex)
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
        public DateTime EndTimeUtc { get; set; }
        public int RequestCount { get; set; }
        public int EvictedCount { get; set; }
        public double WeightedSpeedSum { get; set; }
        public double SpeedBytesSum { get; set; }
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

        var baseQuery = _context.Downloads
            .AsNoTracking()
            .Where(d => !d.IsActive); // Only completed downloads for retro view

        // Exclude hidden client IPs
        if (hiddenClientIps.Count > 0)
        {
            baseQuery = baseQuery.Where(d => !hiddenClientIps.Contains(d.ClientIp));
        }

        // Apply eviction filter (hide/remove modes exclude evicted downloads)
        var evictedMode = _stateRepository.GetEvictedDataMode();
        baseQuery = baseQuery.ApplyEvictedFilter(evictedMode).ApplyEmptySessionFilter();

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
            baseQuery = baseQuery.Where(d => d.ClientIp == query.Client);
        }

        // Filter: hide zero-byte downloads
        if (!query.ShowZeroBytes)
        {
            baseQuery = baseQuery.Where(d => (d.CacheHitBytes + d.CacheMissBytes) > 0);
        }

        return baseQuery;
    }

    /// <summary>
    /// Fetches the underlying download IDs for the current page's depot-backed rows only.
    /// No-depot rows already carry their single download id from the aggregate query.
    /// </summary>
    private async Task FillDownloadIdsAsync(
        RetroDownloadQuery query,
        List<RetroDownloadDto> pageItems,
        Dictionary<string, List<(long DepotId, string ClientIp)>> pairsByRowId)
    {
        var neededPairs = new HashSet<(long DepotId, string ClientIp)>();
        foreach (var item in pageItems)
        {
            if (pairsByRowId.TryGetValue(item.Id, out var pairs))
            {
                foreach (var pair in pairs) neededPairs.Add(pair);
            }
        }
        if (neededPairs.Count == 0) return;

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
}
