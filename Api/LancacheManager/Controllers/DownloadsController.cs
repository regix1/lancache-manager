using LancacheManager.Models;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Core.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for download history
/// Handles retrieval of latest and active downloads
/// </summary>
[ApiController]
[Route("api/downloads")]
[Authorize]
public class DownloadsController : ControllerBase
{

    private readonly AppDbContext _context;
    private readonly StatsDataService _statsService;
    private readonly IStateService _stateRepository;
    private readonly ILogger<DownloadsController> _logger;

    public DownloadsController(
        AppDbContext context,
        StatsDataService statsService,
        IStateService stateRepository,
        ILogger<DownloadsController> logger)
    {
        _context = context;
        _statsService = statsService;
        _stateRepository = stateRepository;
        _logger = logger;
    }

    [HttpGet("latest")]
    public async Task<IActionResult> GetLatestAsync([FromQuery] int count = int.MaxValue, [FromQuery] long? startTime = null, [FromQuery] long? endTime = null, [FromQuery] long? eventId = null, [FromQuery] bool showPrefillTraffic = false)
    {
        // Convert single eventId to list for filtering
        var eventIdList = eventId.HasValue
            ? new List<long> { eventId.Value }
            : new List<long>();
        var hiddenClientIps = _stateRepository.GetHiddenClientIps();
        var evictedMode = _stateRepository.GetEvictedDataMode();

        try
        {
            List<Download> downloads;

            // If no time filtering and no event filter, use cached service method
            if (!startTime.HasValue && !endTime.HasValue && eventIdList.Count == 0)
            {
                downloads = await _statsService.GetLatestDownloadsAsync(count, includePrefill: showPrefillTraffic);
            }
            else
            {
                // With filtering, query database directly
                // Database stores dates in UTC, so filter with UTC
                var startDate = startTime.HasValue
                    ? startTime.Value.FromUnixSeconds()
                    : DateTime.MinValue;
                var endDate = endTime.HasValue
                    ? endTime.Value.FromUnixSeconds()
                    : DateTime.UtcNow;

                // Apply event filter if provided (filters to only tagged downloads)
                if (eventIdList.Count > 0)
                {
                    // Use subquery to atomically fetch downloads with event associations
                    // This eliminates the race condition by using a single query
                    var eventQuery = _context.Downloads
                            .AsNoTracking()
                            .Where(d => _context.EventDownloads
                                .Where(ed => eventIdList.Contains(ed.EventId))
                                .Select(ed => ed.DownloadId)
                                .Contains(d.Id))
                            .Where(d => d.StartTimeUtc >= startDate && d.StartTimeUtc <= endDate)
                            .ApplyEvictedFilter(evictedMode);
                    downloads = await eventQuery
                        .OrderByDescending(d => d.StartTimeUtc)
                        .Take(count)
                        .ToListAsync();
                }
                else
                {
                    var baseQuery = _context.Downloads
                            .AsNoTracking()
                            .Where(d => d.StartTimeUtc >= startDate && d.StartTimeUtc <= endDate)
                            .ApplyEvictedFilter(evictedMode);
                    downloads = await baseQuery
                        .OrderByDescending(d => d.StartTimeUtc)
                        .Take(count)
                        .ToListAsync();
                }

                downloads.WithUtcMarking();
            }

            // Apply hidden-client filter to ALL downloads (both cached and direct query paths)
            if (hiddenClientIps.Count > 0)
            {
                downloads = downloads
                    .Where(d => !hiddenClientIps.Contains(d.ClientIp))
                    .ToList();
            }

            // Filter out prefill sessions unless explicitly requested (safety net - the cached path
            // already honors includePrefill, but the time/event-filtered direct queries above do not).
            if (!showPrefillTraffic)
            {
                downloads = downloads
                    .Where(d => !DownloadKindConstants.IsPrefillDownload(d))
                    .ToList();
            }

            // ShowClean: include evicted downloads but mask the flag (no badge/dimming on frontend)
            // Note: Hide/Remove modes are already filtered at the DB level via ApplyEvictedFilter
            if (evictedMode == EvictedDataMode.ShowClean.ToWireString())
            {
                foreach (var d in downloads) d.IsEvicted = false;
            }

            // Resolve game names via Steam depot mappings + Epic lookup
            await ResolveNamesAsync(downloads);

            // Return just the array - frontend will use array.length for actual count
            return Ok(downloads);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting latest downloads");
            return Ok(new List<Download>());
        }
    }

    /// <summary>
    /// Get a download by ID with its tags and events
    /// </summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetByIdAsync(long id)
    {
        var download = await _context.Downloads
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && d.ClientIp != DownloadKindConstants.PrefillToken && d.ClientIp != "Prefill")
            ?? throw new NotFoundException("Download");

        var hiddenClientIps = _stateRepository.GetHiddenClientIps();
        if (hiddenClientIps.Contains(download.ClientIp))
        {
            throw new NotFoundException("Download");
        }

        if (DownloadKindConstants.IsPrefillDownload(download))
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

        var events = eventDownloads.Select(ed => new
        {
            ed.Event.Id,
            ed.Event.Name,
            ed.Event.ColorIndex,
            ed.Event.StartTimeUtc,
            ed.Event.EndTimeUtc,
            ed.AutoTagged,
            ed.TaggedAtUtc
        }).ToList();

        return Ok(new
        {
            download,
            events
        });
    }

    /// <summary>
    /// Get events for multiple download IDs in a single batch request
    /// </summary>
    [HttpPost("batch-download-events")]
    public async Task<IActionResult> GetBatchEventsAsync([FromBody] BatchDownloadEventsRequest request)
    {
        if (request.DownloadIds == null || request.DownloadIds.Count == 0)
        {
            return Ok(new Dictionary<long, object>());
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
            id => new
            {
                events = eventDownloads
                    .Where(ed => ed.DownloadId == id)
                    .Select(ed => new
                    {
                        ed.Event.Id,
                        ed.Event.Name,
                        ed.Event.ColorIndex,
                        ed.AutoTagged
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
    public async Task<IActionResult> GetWithEventsAsync(
        [FromQuery] int count = 100,
        [FromQuery] long? startTime = null,
        [FromQuery] long? endTime = null)
    {
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
            .Where(d => d.ClientIp != DownloadKindConstants.PrefillToken && d.ClientIp != "Prefill")
            .Where(d => d.Datasource != DownloadKindConstants.PrefillToken && d.Datasource != "Prefill")
            .Where(d => d.StartTimeUtc >= startDate && d.StartTimeUtc <= endDate);

        baseQuery = baseQuery.ApplyEvictedFilter(evictedMode);

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
            return Ok(new List<object>());
        }

        // Mark timestamps as UTC and build response
        var result = downloadsWithEvents.Select(item =>
        {
            item.Download.WithUtcMarking();
            return new
            {
                download = item.Download,
                events = (object)item.Events
            };
        }).ToList();

        return Ok(result);
    }

    /// <summary>
    /// Get paginated, grouped download data for the Retro view.
    /// Groups downloads by DepotId + ClientIp and aggregates cache statistics.
    /// Resolves game names via shared ResolveGameNamesAsync method.
    /// </summary>
    [HttpGet("retro")]
    public async Task<ActionResult<RetroDownloadResponse>> GetRetroDownloadsAsync([FromQuery] RetroDownloadQuery query)
    {
        const int maxPageSize = 200;

        // Clamp page size
        query.PageSize = Math.Clamp(query.PageSize, 1, maxPageSize);
        if (query.Page < 1) query.Page = 1;

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
                    // Datasource is part of the key so prefill-daemon rows never re-merge with a real
                    // client's rows that happen to share the same depot+IP (host-networking collision -
                    // see log_processor.rs's "_prefill" grouping suffix). Without this, Max(Datasource)
                    // below would badge a real client's card as prefill and pollute its hit/miss bytes.
                    d.Datasource,
                    RowKey = d.DepotId == null ? d.Id : 0L
                })
                .Select(g => new RetroGroupRow
                {
                    DepotId = g.Key.DepotId,
                    ClientIp = g.Key.ClientIp,
                    RowKey = g.Key.RowKey,
                    Service = g.Max(d => d.Service)!,
                    Datasource = g.Key.Datasource,
                    GameName = g.Max(d => d.GameName),
                    GameAppId = g.Max(d => d.GameAppId),
                    EpicAppId = g.Max(d => d.EpicAppId),
                    XboxProductId = g.Max(d => d.XboxProductId),
                    CacheHitBytes = g.Sum(d => d.CacheHitBytes),
                    CacheMissBytes = g.Sum(d => d.CacheMissBytes),
                    StartTimeUtc = g.Min(d => d.StartTimeUtc),
                    EndTimeUtc = g.Max(d => d.EndTimeUtc),
                    RequestCount = g.Count(),
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

            // Resolve game names at group level (a depot maps to exactly one app, so this is
            // equivalent to the previous per-row resolution but over far fewer items).
            await ResolveGroupNamesAsync(groupedRows);

            var grouped = groupedRows.Select(r =>
            {
                var totalBytes = r.CacheHitBytes + r.CacheMissBytes;
                // Datasource is now part of the SQL group key, so a depot+IP pair can produce two rows
                // (e.g. default + prefill). Suffix the id when the datasource isn't the default so the
                // two rows get distinct ids instead of colliding on the historical "depot-{id}-{ip}" key.
                var datasourceSuffix = string.Equals(r.Datasource, "default", StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : $"-{r.Datasource}";
                return new RetroDownloadDto
                {
                    Id = r.DepotId.HasValue
                        ? $"depot-{r.DepotId.Value}-{r.ClientIp}{datasourceSuffix}"
                        : $"no-depot-{r.Service}-{r.ClientIp}-{r.RowKey}",
                    DepotId = r.DepotId,
                    EpicAppId = r.EpicAppId,
                    ClientIp = r.ClientIp,
                    Service = r.Service,
                    Datasource = r.Datasource,
                    AppName = r.GameName ?? r.Service,
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
                        : new List<uint>()
                };
            }).ToList();

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

            // Filter: hide unknown games
            if (query.HideUnknown)
            {
                grouped = grouped
                    .Where(r => !string.IsNullOrEmpty(r.AppName)
                             && r.AppName != r.Service
                             && !r.AppName.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // Track which (DepotId, ClientIp) pairs make up each row so the page's DownloadIds
            // can be fetched after pagination. Game-merged rows span multiple pairs.
            var pairsByRowId = grouped
                .Where(r => r.DepotId.HasValue)
                .ToDictionary(
                    r => r.Id,
                    r => new List<(long DepotId, string ClientIp, string Datasource)> { (r.DepotId!.Value, r.ClientIp, r.Datasource) },
                    StringComparer.Ordinal);

            // Merge by game when GroupByGame=true (in-memory over the depot-group list)
            List<RetroDownloadDto> effectiveList;
            if (query.GroupByGame)
            {
                var mergedBuckets = new Dictionary<string, List<RetroDownloadDto>>(StringComparer.Ordinal);
                var bucketOrder = new List<string>();

                foreach (var row in grouped)
                {
                    string mergeKey;
                    if (row.SteamAppId.HasValue && row.SteamAppId.Value != 0)
                    {
                        mergeKey = $"{row.Service}-app-{row.SteamAppId.Value}";
                    }
                    else if (!string.IsNullOrEmpty(row.EpicAppId))
                    {
                        mergeKey = $"{row.Service}-epic-{row.EpicAppId}";
                    }
                    else if (!string.IsNullOrEmpty(row.AppName) && row.AppName != row.Service)
                    {
                        mergeKey = $"{row.Service}-name-{row.AppName.ToLowerInvariant()}";
                    }
                    else
                    {
                        // Stable per-row key - never merges with other rows
                        var depotPart = row.DepotId.HasValue ? row.DepotId.Value.ToString() : "0";
                        mergeKey = $"{row.Service}-unknown-{depotPart}-{row.ClientIp}";
                    }

                    // Prefill traffic never merges into a real client's game card: keeping the two in
                    // separate buckets is what lets a game's card show the real client's true hit rate
                    // (e.g. 100%) while the prefill's all-miss traffic gets its own badged card when
                    // shown. The prefix is empty for real traffic, so existing keys are unchanged.
                    if (row.IsPrefill)
                    {
                        mergeKey = $"prefill-{mergeKey}";
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
                    var mergedPairs = new List<(long DepotId, string ClientIp, string Datasource)>();
                    foreach (var row in bucket)
                    {
                        foreach (var ip in row.ClientIps) clientIpsSet.Add(ip);
                        foreach (var did in row.DepotIds) depotIdsSet.Add(did);
                        allDownloadIds.AddRange(row.DownloadIds);
                        if (pairsByRowId.TryGetValue(row.Id, out var rowPairs))
                        {
                            mergedPairs.AddRange(rowPairs);
                        }
                    }
                    if (mergedPairs.Count > 0)
                    {
                        pairsByRowId[key] = mergedPairs;
                    }

                    return new RetroDownloadDto
                    {
                        Id = key,
                        DepotId = first.DepotId,
                        EpicAppId = first.EpicAppId,
                        ClientIp = first.ClientIp,
                        Service = first.Service,
                        Datasource = first.Datasource,
                        AppName = first.AppName,
                        SteamAppId = first.SteamAppId,
                        StartTimeUtc = bucket.Min(r => r.StartTimeUtc),
                        EndTimeUtc = bucket.Max(r => r.EndTimeUtc),
                        CacheHitBytes = mergedHitBytes,
                        CacheMissBytes = mergedMissBytes,
                        CacheHitPercent = Math.Round(mergedCacheHitPercent, 1),
                        TotalBytes = mergedTotalBytes,
                        AverageBytesPerSecond = first.AverageBytesPerSecond,
                        RequestCount = bucket.Sum(r => r.RequestCount),
                        DownloadIds = allDownloadIds,
                        ClientIps = clientIpsSet.ToList(),
                        DepotIds = depotIdsSet.ToList()
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
                _ => effectiveList.OrderByDescending(g => g.EndTimeUtc).ToList(), // "latest" default
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
    /// Resolves game names for downloads using Steam depot mappings and Epic game mappings.
    /// Priority: existing GameName → Steam AppName → Epic Name → fallback to Service.
    /// Mutates the download objects in-place.
    /// </summary>
    /// <summary>
    /// SQL-side aggregate projection for one retro group (DepotId + ClientIp, or a single
    /// no-depot download identified by RowKey).
    /// </summary>
    private sealed class RetroGroupRow
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
        public double WeightedSpeedSum { get; set; }
        public double SpeedBytesSum { get; set; }
    }

    /// <summary>
    /// Builds the filtered (row-level) retro query. Shared by the aggregate query and the
    /// page-level DownloadIds detail query so both see exactly the same rows.
    /// </summary>
    private IQueryable<Download> BuildRetroBaseQuery(RetroDownloadQuery query)
    {
        var hiddenClientIps = _stateRepository.GetHiddenClientIps();

        // Base query: exclude prefill sessions unless the caller opted in via showPrefillTraffic.
        var baseQuery = _context.Downloads
            .AsNoTracking()
            .ApplyPrefillFilter(excludePrefill: !query.ShowPrefillTraffic)
            .Where(d => !d.IsActive); // Only completed downloads for retro view

        // Exclude hidden client IPs
        if (hiddenClientIps.Count > 0)
        {
            baseQuery = baseQuery.Where(d => !hiddenClientIps.Contains(d.ClientIp));
        }

        // Apply eviction filter (hide/remove modes exclude evicted downloads)
        var evictedMode = _stateRepository.GetEvictedDataMode();
        baseQuery = baseQuery.ApplyEvictedFilter(evictedMode);

        // Filter: time range (matches GetLatestAsync behavior)
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
            baseQuery = baseQuery.Where(d => d.Service == query.Service);
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
    /// Group-level variant of ResolveGameNamesAsync: fills missing game names on aggregated
    /// retro rows from Steam depot mappings and Epic game mappings.
    /// </summary>
    private async Task ResolveGroupNamesAsync(List<RetroGroupRow> rows)
    {
        if (rows.Count == 0) return;

        var depotIds = rows
            .Where(r => string.IsNullOrEmpty(r.GameName) && r.DepotId.HasValue)
            .Select(r => r.DepotId!.Value)
            .Distinct()
            .ToList();

        var steamMappings = depotIds.Count > 0
            ? await _context.SteamDepotMappings
                .AsNoTracking()
                .Where(m => m.IsOwner && depotIds.Contains(m.DepotId))
                .ToDictionaryAsync(m => m.DepotId, m => m)
            : new Dictionary<long, SteamDepotMapping>();

        var epicAppIds = rows
            .Where(r => string.IsNullOrEmpty(r.GameName) && !string.IsNullOrEmpty(r.EpicAppId))
            .Select(r => r.EpicAppId!)
            .Distinct()
            .ToList();

        var epicMappings = epicAppIds.Count > 0
            ? await _context.EpicGameMappings
                .AsNoTracking()
                .Where(m => epicAppIds.Contains(m.AppId))
                .ToDictionaryAsync(m => m.AppId, m => m.Name)
            : new Dictionary<string, string>();

        var xboxProductIds = rows
            .Where(r => string.IsNullOrEmpty(r.GameName) && !string.IsNullOrEmpty(r.XboxProductId))
            .Select(r => r.XboxProductId!)
            .Distinct()
            .ToList();

        var xboxMappings = xboxProductIds.Count > 0
            ? await _context.XboxGameMappings
                .AsNoTracking()
                .Where(m => xboxProductIds.Contains(m.ProductId))
                .ToDictionaryAsync(m => m.ProductId, m => m.Title)
            : new Dictionary<string, string>();

        foreach (var r in rows)
        {
            if (string.IsNullOrEmpty(r.GameName) && r.DepotId.HasValue
                && steamMappings.TryGetValue(r.DepotId.Value, out var steamMapping))
            {
                r.GameName = steamMapping.AppName;
                r.GameAppId = steamMapping.AppId;
            }

            if (string.IsNullOrEmpty(r.GameName) && !string.IsNullOrEmpty(r.EpicAppId)
                && epicMappings.TryGetValue(r.EpicAppId, out var epicName))
            {
                r.GameName = epicName;
            }

            if (string.IsNullOrEmpty(r.GameName) && !string.IsNullOrEmpty(r.XboxProductId)
                && xboxMappings.TryGetValue(r.XboxProductId, out var xboxTitle))
            {
                r.GameName = xboxTitle;
            }

            if (string.IsNullOrEmpty(r.GameName))
            {
                r.GameName = r.Service;
            }
        }
    }

    /// <summary>
    /// Fetches the underlying download IDs for the current page's depot-backed rows only.
    /// No-depot rows already carry their single download id from the aggregate query.
    /// </summary>
    private async Task FillDownloadIdsAsync(
        RetroDownloadQuery query,
        List<RetroDownloadDto> pageItems,
        Dictionary<string, List<(long DepotId, string ClientIp, string Datasource)>> pairsByRowId)
    {
        var neededPairs = new HashSet<(long DepotId, string ClientIp, string Datasource)>();
        foreach (var item in pageItems)
        {
            if (pairsByRowId.TryGetValue(item.Id, out var pairs))
            {
                foreach (var pair in pairs) neededPairs.Add(pair);
            }
        }
        if (neededPairs.Count == 0) return;

        // Over-fetch by depot list + ip list (pair-exact filtering, including Datasource, happens in
        // memory below); both lists are bounded by the page size, so this stays a small indexed query.
        var depotIdList = neededPairs.Select(p => p.DepotId).Distinct().ToList();
        var clientIpList = neededPairs.Select(p => p.ClientIp).Distinct().ToList();

        var detailRows = await BuildRetroBaseQuery(query)
            .Where(d => d.DepotId != null
                     && depotIdList.Contains(d.DepotId.Value)
                     && clientIpList.Contains(d.ClientIp))
            .Select(d => new { d.Id, DepotId = d.DepotId!.Value, d.ClientIp, d.Datasource })
            .ToListAsync();

        // Keyed by (DepotId, ClientIp, Datasource) - not just the pair - so a depot+IP collision
        // between prefill and real-client traffic (same group key split in BuildRetroBaseQuery's
        // GroupBy) can't leak the other side's download ids into this row's DownloadIds.
        var idsByPair = new Dictionary<(long DepotId, string ClientIp, string Datasource), List<long>>();
        foreach (var row in detailRows)
        {
            var pair = (row.DepotId, row.ClientIp, row.Datasource);
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

    private async Task ResolveNamesAsync(List<Download> downloads)
    {
        if (downloads.Count == 0) return;

        // Build Steam depot mapping lookup for downloads with a DepotId
        var depotIds = downloads
            .Where(d => d.DepotId.HasValue)
            .Select(d => d.DepotId!.Value)
            .Distinct()
            .ToList();

        var steamMappings = depotIds.Count > 0
            ? await _context.SteamDepotMappings
                .AsNoTracking()
                .Where(m => m.IsOwner && depotIds.Contains(m.DepotId))
                .ToDictionaryAsync(m => m.DepotId, m => m)
            : new Dictionary<long, SteamDepotMapping>();

        // Build Epic game name lookup for Epic downloads
        var epicAppIds = downloads
            .Where(d => !string.IsNullOrEmpty(d.EpicAppId))
            .Select(d => d.EpicAppId!)
            .Distinct()
            .ToList();

        var epicMappings = epicAppIds.Count > 0
            ? await _context.EpicGameMappings
                .AsNoTracking()
                .Where(m => epicAppIds.Contains(m.AppId))
                .ToDictionaryAsync(m => m.AppId, m => m.Name)
            : new Dictionary<string, string>();

        // Build Xbox game name lookup for Xbox downloads (named-style: GameName from the shared
        // XboxGameMapping catalog keyed by XboxProductId metadata).
        var xboxProductIds = downloads
            .Where(d => !string.IsNullOrEmpty(d.XboxProductId))
            .Select(d => d.XboxProductId!)
            .Distinct()
            .ToList();

        var xboxMappings = xboxProductIds.Count > 0
            ? await _context.XboxGameMappings
                .AsNoTracking()
                .Where(m => xboxProductIds.Contains(m.ProductId))
                .ToDictionaryAsync(m => m.ProductId, m => m.Title)
            : new Dictionary<string, string>();

        // Apply name resolution priority: existing GameName → Steam AppName → Epic Name → Xbox Title → fallback to Service
        foreach (var d in downloads)
        {
            // Fill from Steam mapping if game name is missing
            if (string.IsNullOrEmpty(d.GameName) && d.DepotId.HasValue
                && steamMappings.TryGetValue(d.DepotId.Value, out var steamMapping))
            {
                d.GameName = steamMapping.AppName;
                d.GameAppId = steamMapping.AppId;
            }

            // Fill from Epic mapping if still missing
            if (string.IsNullOrEmpty(d.GameName) && !string.IsNullOrEmpty(d.EpicAppId)
                && epicMappings.TryGetValue(d.EpicAppId, out var epicName))
            {
                d.GameName = epicName;
            }

            // Fill from Xbox mapping if still missing
            if (string.IsNullOrEmpty(d.GameName) && !string.IsNullOrEmpty(d.XboxProductId)
                && xboxMappings.TryGetValue(d.XboxProductId, out var xboxTitle))
            {
                d.GameName = xboxTitle;
            }

            // Final fallback: use service name
            if (string.IsNullOrEmpty(d.GameName))
            {
                d.GameName = d.Service;
            }
        }
    }
}
