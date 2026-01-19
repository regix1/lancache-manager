using LancacheManager.Models;
using LancacheManager.Configuration;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Core.Interfaces.Repositories;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Net;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for statistics and analytics
/// Handles client stats, service stats, and dashboard metrics
/// </summary>
[ApiController]
[Route("api/stats")]
[RequireGuestSession]
public class StatsController : ControllerBase
{
    private const string PrefillToken = "prefill";

    private readonly AppDbContext _context;
    private readonly StatsRepository _statsService;
    private readonly IClientGroupsRepository _clientGroupsRepository;
    private readonly CacheSnapshotService _cacheSnapshotService;
    private readonly IStateRepository _stateRepository;
    private readonly ILogger<StatsController> _logger;
    private readonly IOptions<ApiOptions> _apiOptions;
    private readonly IHubContext<LancacheManager.Hubs.DownloadHub> _downloadHubContext;

    public StatsController(
        AppDbContext context,
        StatsRepository statsService,
        IClientGroupsRepository clientGroupsRepository,
        CacheSnapshotService cacheSnapshotService,
        IStateRepository stateRepository,
        ILogger<StatsController> logger,
        IOptions<ApiOptions> apiOptions,
        IHubContext<LancacheManager.Hubs.DownloadHub> downloadHubContext)
    {
        _context = context;
        _statsService = statsService;
        _clientGroupsRepository = clientGroupsRepository;
        _cacheSnapshotService = cacheSnapshotService;
        _stateRepository = stateRepository;
        _logger = logger;
        _apiOptions = apiOptions;
        _downloadHubContext = downloadHubContext;
    }

    /// <summary>
    /// Converts a single event ID into a list for filtering.
    /// </summary>
    private static List<int> ParseEventId(int? eventId)
    {
        if (!eventId.HasValue)
            return new List<int>();

        return new List<int> { eventId.Value };
    }

    /// <summary>
    /// Gets download IDs tagged to specific events.
    /// Used to filter stats to only show downloads associated with events.
    /// </summary>
    private async Task<HashSet<int>> GetEventDownloadIdsAsync(List<int> eventIds)
    {
        if (eventIds.Count == 0)
            return new HashSet<int>();

        var downloadIds = await _context.EventDownloads
            .AsNoTracking()
            .Where(ed => eventIds.Contains(ed.EventId))
            .Select(ed => ed.DownloadId)
            .Distinct()
            .ToListAsync();
        return downloadIds.ToHashSet();
    }

    /// <summary>
    /// Applies event filtering to a downloads query.
    /// When eventIds are provided, only returns downloads tagged to those events.
    /// Must be called with pre-fetched download IDs for reliable SQLite compatibility.
    /// </summary>
    private IQueryable<Download> ApplyEventFilter(IQueryable<Download> query, List<int> eventIds, HashSet<int>? eventDownloadIds)
    {
        if (eventIds.Count == 0 || eventDownloadIds == null)
            return query;

        // Filter to only downloads that are tagged to the events
        return query.Where(d => eventDownloadIds.Contains(d.Id));
    }

    private static IQueryable<Download> ApplyExcludedClientFilter(IQueryable<Download> query, List<string> excludedClientIps)
    {
        query = ApplyPrefillFilter(query);

        if (excludedClientIps.Count == 0)
        {
            return query;
        }

        return query.Where(d => !excludedClientIps.Contains(d.ClientIp));
    }

    /// <summary>
    /// Filters out hidden IPs (complete removal from queries).
    /// Use this for queries where hidden IPs should not appear at all.
    /// </summary>
    private static IQueryable<Download> ApplyHiddenClientFilter(IQueryable<Download> query, List<string> hiddenClientIps)
    {
        query = ApplyPrefillFilter(query);

        if (hiddenClientIps.Count == 0)
        {
            return query;
        }

        return query.Where(d => !hiddenClientIps.Contains(d.ClientIp));
    }

    private static IQueryable<Download> ApplyPrefillFilter(IQueryable<Download> query)
    {
        return query
            .Where(d => d.ClientIp == null || d.ClientIp.ToLower() != PrefillToken)
            .Where(d => d.Datasource == null || d.Datasource.ToLower() != PrefillToken);
    }

    /// <summary>
    /// Calculates sum of cache hit bytes excluding stats-excluded IPs.
    /// Used for calculations where excluded IPs should be visible but not counted.
    /// </summary>
    private static async Task<long> SumCacheHitBytesExcludingAsync(
        IQueryable<Download> query,
        List<string> statsExcludedIps)
    {
        if (statsExcludedIps.Count == 0)
        {
            return await query.SumAsync(d => (long?)d.CacheHitBytes) ?? 0L;
        }

        // Sum all values, then subtract excluded values
        var total = await query.SumAsync(d => (long?)d.CacheHitBytes) ?? 0L;
        var excluded = await query
            .Where(d => statsExcludedIps.Contains(d.ClientIp))
            .SumAsync(d => (long?)d.CacheHitBytes) ?? 0L;
        
        return total - excluded;
    }

    /// <summary>
    /// Calculates sum of cache miss bytes excluding stats-excluded IPs.
    /// Used for calculations where excluded IPs should be visible but not counted.
    /// </summary>
    private static async Task<long> SumCacheMissBytesExcludingAsync(
        IQueryable<Download> query,
        List<string> statsExcludedIps)
    {
        if (statsExcludedIps.Count == 0)
        {
            return await query.SumAsync(d => (long?)d.CacheMissBytes) ?? 0L;
        }

        // Sum all values, then subtract excluded values
        var total = await query.SumAsync(d => (long?)d.CacheMissBytes) ?? 0L;
        var excluded = await query
            .Where(d => statsExcludedIps.Contains(d.ClientIp))
            .SumAsync(d => (long?)d.CacheMissBytes) ?? 0L;
        
        return total - excluded;
    }

    /// <summary>
    /// Counts downloads excluding stats-excluded IPs.
    /// Used for calculations where excluded IPs should be visible but not counted.
    /// </summary>
    private static async Task<int> CountExcludingAsync(
        IQueryable<Download> query,
        List<string> statsExcludedIps)
    {
        if (statsExcludedIps.Count == 0)
        {
            return await query.CountAsync();
        }

        // Count all, then subtract excluded count
        var total = await query.CountAsync();
        var excluded = await query
            .Where(d => statsExcludedIps.Contains(d.ClientIp))
            .CountAsync();
        
        return total - excluded;
    }

    private static List<string> NormalizeClientIps(IEnumerable<string>? ips, out List<string> invalidIps)
    {
        invalidIps = new List<string>();
        var normalized = new List<string>();

        if (ips == null)
        {
            return normalized;
        }

        foreach (var rawIp in ips)
        {
            var trimmed = rawIp?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (!IPAddress.TryParse(trimmed, out var parsed))
            {
                invalidIps.Add(trimmed);
                continue;
            }

            var normalizedIp = parsed.ToString();
            if (!normalized.Contains(normalizedIp))
            {
                normalized.Add(normalizedIp);
            }
        }

        return normalized;
    }


    [HttpGet("clients")]
    [OutputCache(PolicyName = "stats-short")]
    public async Task<IActionResult> GetClients(
        [FromQuery] long? startTime = null,
        [FromQuery] long? endTime = null,
        [FromQuery] int? limit = null,
        [FromQuery] int? eventId = null,
        [FromQuery] bool includeExcluded = false)
    {
        try
        {
            // Get configuration
            var maxLimit = _apiOptions.Value.MaxClientsPerRequest;
            var defaultLimit = _apiOptions.Value.DefaultClientsLimit;
            var effectiveLimit = Math.Min(limit ?? defaultLimit, maxLimit);

            // Parse event IDs
            var eventIdList = ParseEventId(eventId);

            // Build base query with time filtering
            var query = _context.Downloads.AsNoTracking();

            // Apply event filter if provided (filters to only tagged downloads)
            HashSet<int>? eventDownloadIds = eventIdList.Count > 0 ? await GetEventDownloadIdsAsync(eventIdList) : null;
            query = ApplyEventFilter(query, eventIdList, eventDownloadIds);

            // Filter out hidden IPs completely, but include excluded IPs so they can be shown with a badge.
            var hiddenClientIps = includeExcluded ? new List<string>() : _stateRepository.GetHiddenClientIps();
            query = ApplyHiddenClientFilter(query, hiddenClientIps);

            if (startTime.HasValue)
            {
                var startDate = DateTimeOffset.FromUnixTimeSeconds(startTime.Value).UtcDateTime;
                query = query.Where(d => d.StartTimeUtc >= startDate);
            }
            if (endTime.HasValue)
            {
                var endDate = DateTimeOffset.FromUnixTimeSeconds(endTime.Value).UtcDateTime;
                query = query.Where(d => d.StartTimeUtc <= endDate);
            }

            // IMPROVEMENT #1: Push aggregation to database (SQL GROUP BY)
            // NOTE: Duration must be calculated client-side due to SQLite limitations
            // SQLite can't translate DateTime subtraction with TotalSeconds in aggregates
            var ipStats = await query
                .GroupBy(d => d.ClientIp)
                .Select(g => new
                {
                    ClientIp = g.Key,
                    TotalCacheHitBytes = g.Sum(d => d.CacheHitBytes),
                    TotalCacheMissBytes = g.Sum(d => d.CacheMissBytes),
                    TotalDownloads = g.Count(),
                    // Get min start and max end for duration calculation client-side
                    MinStartTimeUtc = g.Min(d => d.StartTimeUtc),
                    MaxEndTimeUtc = g.Max(d => d.EndTimeUtc),
                    LastActivityUtc = g.Max(d => d.StartTimeUtc)
                })
                .ToListAsync();

            // Calculate duration client-side (total time span from first download to last completion)
            var ipStatsWithDuration = ipStats.Select(s => new
            {
                s.ClientIp,
                s.TotalCacheHitBytes,
                s.TotalCacheMissBytes,
                s.TotalDownloads,
                TotalDurationSeconds = s.MaxEndTimeUtc > s.MinStartTimeUtc
                    ? (s.MaxEndTimeUtc - s.MinStartTimeUtc).TotalSeconds
                    : 0,
                s.LastActivityUtc
            }).ToList();

            // IMPROVEMENT #2: Create reverse lookup dictionary (O(1) instead of O(n))
            var ipToGroupMapping = await _clientGroupsRepository.GetIpToGroupMappingAsync();

            // Build GroupId → GroupInfo lookup
            var groupIdToInfo = ipToGroupMapping.Values
                .GroupBy(v => v.GroupId)
                .ToDictionary(g => g.Key, g => g.First());

            // IMPROVEMENT #8: Single-pass partitioning (grouped vs ungrouped)
            var groupedStats = new Dictionary<int, List<(string Ip, long Hit, long Miss, int Downloads, double Duration, DateTime LastActivity)>>();
            var ungroupedStats = new List<ClientStatsWithGroup>();

            foreach (var stat in ipStatsWithDuration)
            {
                if (ipToGroupMapping.TryGetValue(stat.ClientIp, out var groupInfo))
                {
                    // Add to grouped collection
                    if (!groupedStats.ContainsKey(groupInfo.GroupId))
                    {
                        groupedStats[groupInfo.GroupId] = new();
                    }
                    groupedStats[groupInfo.GroupId].Add((
                        stat.ClientIp,
                        stat.TotalCacheHitBytes,
                        stat.TotalCacheMissBytes,
                        stat.TotalDownloads,
                        stat.TotalDurationSeconds,
                        stat.LastActivityUtc
                    ));
                }
                else
                {
                    // IMPROVEMENT #3: Use helper method
                    ungroupedStats.Add(CreateClientStats(
                        clientIp: stat.ClientIp,
                        totalCacheHitBytes: stat.TotalCacheHitBytes,
                        totalCacheMissBytes: stat.TotalCacheMissBytes,
                        totalDownloads: stat.TotalDownloads,
                        totalDurationSeconds: stat.TotalDurationSeconds,
                        lastActivityUtc: stat.LastActivityUtc
                    ));
                }
            }

            // Aggregate grouped stats
            var groupedClientStats = groupedStats.Select(kvp =>
            {
                var groupId = kvp.Key;
                var members = kvp.Value;
                var groupInfo = groupIdToInfo[groupId]; // IMPROVEMENT #2: O(1) lookup!

                return CreateClientStats(
                    clientIp: members.First().Ip,
                    totalCacheHitBytes: members.Sum(m => m.Hit),
                    totalCacheMissBytes: members.Sum(m => m.Miss),
                    totalDownloads: members.Sum(m => m.Downloads),
                    totalDurationSeconds: members.Sum(m => m.Duration),
                    lastActivityUtc: members.Max(m => m.LastActivity),
                    displayName: groupInfo.Nickname,
                    groupId: groupId,
                    groupMemberIps: members.Select(m => m.Ip).OrderBy(ip => ip).ToList()
                );
            }).ToList();

            // Combine and sort by total bytes
            var allStats = groupedClientStats
                .Concat(ungroupedStats)
                .OrderByDescending(c => c.TotalBytes)
                .Take(effectiveLimit) // IMPROVEMENT #4: Configurable limit
                .ToList();

            return Ok(allStats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting client stats");
            // IMPROVEMENT #6: Return proper error instead of empty 200 OK
            return StatusCode(500, new
            {
                error = "Failed to retrieve client statistics",
                message = "An error occurred while processing your request. Please try again later."
            });
        }
    }

    [HttpGet("exclusions")]
    [RequireAuth]
    public IActionResult GetExcludedClients()
    {
        var excludedIps = _stateRepository.GetExcludedClientIps();
        return Ok(new StatsExclusionsResponse
        {
            Ips = excludedIps
        });
    }

    [HttpPut("exclusions")]
    [RequireAuth]
    public async Task<IActionResult> UpdateExcludedClients([FromBody] UpdateStatsExclusionsRequest request)
    {
        var normalizedIps = NormalizeClientIps(request.Ips, out var invalidIps);
        if (invalidIps.Count > 0)
        {
            return BadRequest(new
            {
                error = "Invalid exclusion rules",
                message = "One or more exclusions are not valid. Please correct them and try again.",
                invalidIps,
            });
        }

        _stateRepository.SetExcludedClientIps(normalizedIps);
        // Notify clients to refresh downloads/stats since exclusions affect all tabs
        await _downloadHubContext.Clients.All.SendAsync("DownloadsRefresh", new
        {
            reason = "exclusions-updated"
        });
        return Ok(new StatsExclusionsResponse
        {
            Ips = normalizedIps
        });
    }

    /// <summary>
    /// Creates a ClientStatsWithGroup object with calculated metrics
    /// </summary>
    private static ClientStatsWithGroup CreateClientStats(
        string clientIp,
        long totalCacheHitBytes,
        long totalCacheMissBytes,
        int totalDownloads,
        double totalDurationSeconds,
        DateTime lastActivityUtc,
        string? displayName = null,
        int? groupId = null,
        List<string>? groupMemberIps = null)
    {
        var totalBytes = totalCacheHitBytes + totalCacheMissBytes;
        var cacheHitPercent = totalBytes > 0
            ? (double)totalCacheHitBytes / totalBytes * 100
            : 0;
        var averageBytesPerSecond = totalDurationSeconds > 0
            ? totalBytes / totalDurationSeconds
            : 0;

        return new ClientStatsWithGroup
        {
            ClientIp = clientIp,
            DisplayName = displayName,
            GroupId = groupId,
            IsGrouped = groupId.HasValue,
            GroupMemberIps = groupMemberIps,
            TotalCacheHitBytes = totalCacheHitBytes,
            TotalCacheMissBytes = totalCacheMissBytes,
            TotalBytes = totalBytes,
            CacheHitPercent = cacheHitPercent,
            TotalDownloads = totalDownloads,
            TotalDurationSeconds = totalDurationSeconds,
            AverageBytesPerSecond = averageBytesPerSecond,
            LastActivityUtc = lastActivityUtc.AsUtc()
        };
    }

    [HttpGet("services")]
    [OutputCache(PolicyName = "stats-short")]
    public async Task<IActionResult> GetServices([FromQuery] string? since = null, [FromQuery] long? startTime = null, [FromQuery] long? endTime = null, [FromQuery] int? eventId = null)
    {
        try
        {
            // Parse event IDs
            var eventIdList = ParseEventId(eventId);

            // ALWAYS query Downloads table directly to ensure consistency with dashboard stats
            // Previously used cached ServiceStats table which caused fluctuating values
            // Filter out hidden IPs completely, but include excluded IPs (they'll be excluded from calculations)
            var query = _context.Downloads.AsNoTracking();
            var hiddenClientIps = _stateRepository.GetHiddenClientIps();
            var statsExcludedOnlyIps = _stateRepository.GetStatsExcludedOnlyClientIps();
            query = ApplyHiddenClientFilter(query, hiddenClientIps);

            // Apply event filter if provided (filters to only tagged downloads)
            HashSet<int>? eventDownloadIds = eventIdList.Count > 0 ? await GetEventDownloadIdsAsync(eventIdList) : null;
            query = ApplyEventFilter(query, eventIdList, eventDownloadIds);

            // Apply time filtering if provided
            if (startTime.HasValue)
            {
                var startDate = DateTimeOffset.FromUnixTimeSeconds(startTime.Value).UtcDateTime;
                query = query.Where(d => d.StartTimeUtc >= startDate);
            }
            if (endTime.HasValue)
            {
                var endDate = DateTimeOffset.FromUnixTimeSeconds(endTime.Value).UtcDateTime;
                query = query.Where(d => d.StartTimeUtc <= endDate);
            }
            else if (!string.IsNullOrEmpty(since) && since != "all")
            {
                // Parse time period string for backwards compatibility
                var cutoffTime = TimeUtils.ParseTimePeriod(since);
                if (cutoffTime.HasValue)
                {
                    query = query.Where(d => d.StartTimeUtc >= cutoffTime.Value);
                }
            }
            // No filter = all data (consistent with dashboard)

            // Aggregate by service from Downloads table (exclude stats-excluded IPs from calculations)
            var serviceStatsQuery = statsExcludedOnlyIps.Count > 0
                ? query.Where(d => !statsExcludedOnlyIps.Contains(d.ClientIp))
                : query;
            var serviceStats = await serviceStatsQuery
                .GroupBy(d => d.Service)
                .Select(g => new ServiceStats
                {
                    Service = g.Key,
                    TotalCacheHitBytes = g.Sum(d => d.CacheHitBytes),
                    TotalCacheMissBytes = g.Sum(d => d.CacheMissBytes),
                    TotalDownloads = g.Count(),
                    LastActivityUtc = g.Max(d => d.StartTimeUtc),
                    LastActivityLocal = g.Max(d => d.StartTimeLocal)
                })
                .OrderByDescending(s => s.TotalCacheHitBytes + s.TotalCacheMissBytes)
                .ToListAsync();

            // Fix timezone for proper JSON serialization
            foreach (var stat in serviceStats)
            {
                stat.LastActivityUtc = stat.LastActivityUtc.AsUtc();
            }

            return Ok(serviceStats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting service stats");
            return Ok(new List<ServiceStats>());
        }
    }

    [HttpGet("dashboard")]
    [OutputCache(PolicyName = "stats-short")]
    public async Task<IActionResult> GetDashboardStats(
        [FromQuery] long? startTime = null,
        [FromQuery] long? endTime = null,
        [FromQuery] int? eventId = null)
    {
        // Parse event IDs
        var eventIdList = ParseEventId(eventId);
        var hiddenClientIps = _stateRepository.GetHiddenClientIps();
        var statsExcludedOnlyIps = _stateRepository.GetStatsExcludedOnlyClientIps();

        // Use Unix timestamps if provided, otherwise return ALL data (no time filter)
        // This ensures consistency: frontend always provides timestamps for time-filtered queries
        DateTime? cutoffTime = null;
        DateTime? endDateTime = null;

        if (startTime.HasValue)
        {
            cutoffTime = DateTimeOffset.FromUnixTimeSeconds(startTime.Value).UtcDateTime;
        }
        if (endTime.HasValue)
        {
            endDateTime = DateTimeOffset.FromUnixTimeSeconds(endTime.Value).UtcDateTime;
        }
        // If no timestamps provided, cutoffTime and endDateTime remain null = query ALL data

        // IMPORTANT: Calculate ALL metrics from Downloads table directly (no cache)
        // This ensures consistency - mixing cached ServiceStats with live Downloads caused fluctuating values

        // Build the base query for period-specific metrics
        // Filter out hidden IPs completely, but include excluded IPs (they'll be excluded from calculations)
        var downloadsQuery = ApplyHiddenClientFilter(_context.Downloads.AsNoTracking(), hiddenClientIps);

        // Apply event filter if provided (filters to only tagged downloads)
        HashSet<int>? eventDownloadIds = eventIdList.Count > 0 ? await GetEventDownloadIdsAsync(eventIdList) : null;
        downloadsQuery = ApplyEventFilter(downloadsQuery, eventIdList, eventDownloadIds);

        if (cutoffTime.HasValue)
        {
            downloadsQuery = downloadsQuery.Where(d => d.StartTimeUtc >= cutoffTime.Value);
        }
        if (endDateTime.HasValue)
        {
            downloadsQuery = downloadsQuery.Where(d => d.StartTimeUtc <= endDateTime.Value);
        }

        // Calculate ALL-TIME totals from Downloads table directly (no cache)
        // Note: All-time totals should NOT be filtered by event - they represent overall system stats
        // Filter out hidden IPs, but exclude stats-excluded IPs from calculations
        var allTimeQuery = ApplyHiddenClientFilter(_context.Downloads.AsNoTracking(), hiddenClientIps);
        var totalHitBytesTask = SumCacheHitBytesExcludingAsync(allTimeQuery, statsExcludedOnlyIps);
        var totalMissBytesTask = SumCacheMissBytesExcludingAsync(allTimeQuery, statsExcludedOnlyIps);

        // Calculate PERIOD-specific metrics (exclude stats-excluded IPs from calculations)
        var periodHitBytesTask = SumCacheHitBytesExcludingAsync(downloadsQuery, statsExcludedOnlyIps);
        var periodMissBytesTask = SumCacheMissBytesExcludingAsync(downloadsQuery, statsExcludedOnlyIps);
        var periodDownloadCountTask = CountExcludingAsync(downloadsQuery, statsExcludedOnlyIps);

        // Get top service from Downloads table (not cached ServiceStats)
        // Exclude stats-excluded IPs from the sum calculation
        var topServiceQuery = ApplyHiddenClientFilter(_context.Downloads.AsNoTracking(), hiddenClientIps);
        var topServiceGroups = await topServiceQuery
            .GroupBy(d => d.Service)
            .Select(g => new { Service = g.Key, TotalBytes = g.Sum(d => (long?)(d.CacheHitBytes + d.CacheMissBytes)) ?? 0L })
            .ToListAsync();
        
        // Subtract excluded IPs from each service's total
        if (statsExcludedOnlyIps.Count > 0)
        {
            var excludedServiceGroups = await topServiceQuery
                .Where(d => statsExcludedOnlyIps.Contains(d.ClientIp))
                .GroupBy(d => d.Service)
                .Select(g => new { Service = g.Key, TotalBytes = g.Sum(d => (long?)(d.CacheHitBytes + d.CacheMissBytes)) ?? 0L })
                .ToListAsync();
            
            var excludedByService = excludedServiceGroups.ToDictionary(g => g.Service, g => g.TotalBytes);
            topServiceGroups = topServiceGroups
                .Select(g => new { 
                    g.Service, 
                    TotalBytes = excludedByService.TryGetValue(g.Service, out var excludedBytes) 
                        ? g.TotalBytes - excludedBytes 
                        : g.TotalBytes 
                })
                .ToList();
        }
        
        var topService = topServiceGroups
            .OrderByDescending(s => s.TotalBytes)
            .FirstOrDefault();

        // Active downloads and unique clients (exclude stats-excluded IPs from counts)
        var activeDownloadsQuery = ApplyHiddenClientFilter(_context.Downloads.AsNoTracking(), hiddenClientIps)
            .Where(d => d.IsActive && d.EndTimeUtc > DateTime.UtcNow.AddMinutes(-5));
        var activeDownloadsTask = CountExcludingAsync(activeDownloadsQuery, statsExcludedOnlyIps);

        // Unique clients: count distinct IPs, excluding stats-excluded IPs
        Task<int> uniqueClientsQuery;
        if (cutoffTime.HasValue || endDateTime.HasValue)
        {
            // For period queries, count distinct IPs excluding stats-excluded
            var allIps = await downloadsQuery.Select(d => d.ClientIp).Distinct().ToListAsync();
            var excludedCount = statsExcludedOnlyIps.Count > 0 
                ? allIps.Count(ip => statsExcludedOnlyIps.Contains(ip))
                : 0;
            uniqueClientsQuery = Task.FromResult(allIps.Count - excludedCount);
        }
        else
        {
            // For all-time, count distinct IPs excluding stats-excluded
            var allIps = await ApplyHiddenClientFilter(_context.Downloads.AsNoTracking(), hiddenClientIps)
                .Select(d => d.ClientIp)
                .Distinct()
                .ToListAsync();
            var excludedCount = statsExcludedOnlyIps.Count > 0 
                ? allIps.Count(ip => statsExcludedOnlyIps.Contains(ip))
                : 0;
            uniqueClientsQuery = Task.FromResult(allIps.Count - excludedCount);
        }

        // Await all tasks in parallel
        await Task.WhenAll(
            totalHitBytesTask, totalMissBytesTask,
            periodHitBytesTask, periodMissBytesTask, periodDownloadCountTask,
            activeDownloadsTask, uniqueClientsQuery);

        // All-time metrics (from Downloads table directly)
        var totalBandwidthSaved = await totalHitBytesTask;
        var totalAddedToCache = await totalMissBytesTask;
        var totalServed = totalBandwidthSaved + totalAddedToCache;
        var cacheHitRatio = totalServed > 0
            ? (double)totalBandwidthSaved / totalServed
            : 0;
        var topServiceName = topService?.Service ?? "none";

        // Period-specific metrics
        var periodHitBytes = await periodHitBytesTask;
        var periodMissBytes = await periodMissBytesTask;
        var periodDownloadCount = await periodDownloadCountTask;
        var activeDownloads = await activeDownloadsTask;
        var uniqueClientsCount = await uniqueClientsQuery;

        var periodTotal = periodHitBytes + periodMissBytes;
        var periodHitRatio = periodTotal > 0
            ? (double)periodHitBytes / periodTotal
            : 0;

        // Determine period label for response
        string periodLabel = "all";
        if (cutoffTime.HasValue && endDateTime.HasValue)
        {
            var duration = endDateTime.Value - cutoffTime.Value;
            periodLabel = duration.TotalHours <= 24 ? $"{(int)duration.TotalHours}h" : $"{(int)duration.TotalDays}d";
        }
        else if (cutoffTime.HasValue)
        {
            periodLabel = "since " + cutoffTime.Value.ToString("yyyy-MM-dd");
        }

        return Ok(new DashboardStatsResponse
        {
            // All-time metrics (always from ServiceStats totals)
            TotalBandwidthSaved = totalBandwidthSaved,
            TotalAddedToCache = totalAddedToCache,
            TotalServed = totalServed,
            CacheHitRatio = cacheHitRatio,

            // Current status
            ActiveDownloads = activeDownloads,
            UniqueClients = uniqueClientsCount,
            TopService = topServiceName,

            // Period-specific metrics
            Period = new DashboardPeriodStats
            {
                Duration = periodLabel,
                Since = cutoffTime,
                BandwidthSaved = periodHitBytes,
                AddedToCache = periodMissBytes,
                TotalServed = periodTotal,
                HitRatio = periodHitRatio,
                Downloads = periodDownloadCount
            },

            // Service breakdown (uses period-filtered query for consistency, including event filter if provided)
            ServiceBreakdown = await downloadsQuery
                .GroupBy(d => d.Service)
                .Select(g => new ServiceBreakdownItem
                {
                    Service = g.Key,
                    Bytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                    Percentage = periodTotal > 0
                        ? (g.Sum(d => d.CacheHitBytes + d.CacheMissBytes) * 100.0) / periodTotal
                        : 0
                })
                .OrderByDescending(s => s.Bytes)
                .ToListAsync(),

            LastUpdated = DateTime.UtcNow
        });
    }


    /// <summary>
    /// Get hourly activity data for peak usage hours widget
    /// Groups downloads by hour of day to show activity patterns
    /// </summary>
    [HttpGet("hourly-activity")]
    [OutputCache(PolicyName = "stats-long")]
    public async Task<IActionResult> GetHourlyActivity(
        [FromQuery] long? startTime = null,
        [FromQuery] long? endTime = null,
        [FromQuery] int? eventId = null)
    {
        try
        {
            // Parse event IDs
            var eventIdList = ParseEventId(eventId);

            // Build query with optional time filtering
            // Filter out hidden IPs completely, but include excluded IPs (they'll be excluded from calculations)
            var query = _context.Downloads.AsNoTracking();
            var hiddenClientIps = _stateRepository.GetHiddenClientIps();
            var statsExcludedOnlyIps = _stateRepository.GetStatsExcludedOnlyClientIps();
            query = ApplyHiddenClientFilter(query, hiddenClientIps);

            // Apply event filter if provided (filters to only tagged downloads)
            HashSet<int>? eventDownloadIds = eventIdList.Count > 0 ? await GetEventDownloadIdsAsync(eventIdList) : null;
            query = ApplyEventFilter(query, eventIdList, eventDownloadIds);

            DateTime? cutoffTime = null;
            DateTime? endDateTime = null;

            if (startTime.HasValue)
            {
                cutoffTime = DateTimeOffset.FromUnixTimeSeconds(startTime.Value).UtcDateTime;
                query = query.Where(d => d.StartTimeUtc >= cutoffTime);
            }
            if (endTime.HasValue)
            {
                endDateTime = DateTimeOffset.FromUnixTimeSeconds(endTime.Value).UtcDateTime;
                query = query.Where(d => d.StartTimeUtc <= endDateTime);
            }

            // Calculate number of distinct days in the period
            int daysInPeriod = 1;
            long? periodStartTimestamp = null;
            long? periodEndTimestamp = null;

            if (startTime.HasValue && endTime.HasValue)
            {
                // Use the provided time range
                daysInPeriod = Math.Max(1, (int)Math.Ceiling((endDateTime!.Value - cutoffTime!.Value).TotalDays));
                periodStartTimestamp = startTime.Value;
                periodEndTimestamp = endTime.Value;
            }
            else
            {
                // For "all" data, count distinct days from the actual data
                var dateRange = await query
                    .Select(d => d.StartTimeLocal.Date)
                    .Distinct()
                    .ToListAsync();

                daysInPeriod = Math.Max(1, dateRange.Count);

                if (dateRange.Count > 0)
                {
                    var minDate = dateRange.Min();
                    var maxDate = dateRange.Max();
                    periodStartTimestamp = new DateTimeOffset(minDate, TimeSpan.Zero).ToUnixTimeSeconds();
                    periodEndTimestamp = new DateTimeOffset(maxDate.AddDays(1).AddSeconds(-1), TimeSpan.Zero).ToUnixTimeSeconds();
                }
            }

            // Query downloads and group by local time hour (StartTimeLocal is already in configured timezone)
            // Exclude stats-excluded IPs from calculations
            var hourlyDataAll = await query
                .GroupBy(d => d.StartTimeLocal.Hour)
                .Select(g => new HourlyActivityItem
                {
                    Hour = g.Key,
                    Downloads = g.Count(),
                    BytesServed = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                    CacheHitBytes = g.Sum(d => d.CacheHitBytes),
                    CacheMissBytes = g.Sum(d => d.CacheMissBytes)
                })
                .ToListAsync();

            // Subtract excluded IPs from calculations if any
            List<HourlyActivityItem> hourlyData;
            if (statsExcludedOnlyIps.Count > 0)
            {
                var excludedHourlyData = await query
                    .Where(d => statsExcludedOnlyIps.Contains(d.ClientIp))
                    .GroupBy(d => d.StartTimeLocal.Hour)
                    .Select(g => new HourlyActivityItem
                    {
                        Hour = g.Key,
                        Downloads = g.Count(),
                        BytesServed = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                        CacheHitBytes = g.Sum(d => d.CacheHitBytes),
                        CacheMissBytes = g.Sum(d => d.CacheMissBytes)
                    })
                    .ToListAsync();

                var excludedByHour = excludedHourlyData.ToDictionary(h => h.Hour);
                hourlyData = hourlyDataAll.Select(h => new HourlyActivityItem
                {
                    Hour = h.Hour,
                    Downloads = excludedByHour.TryGetValue(h.Hour, out var excl) ? h.Downloads - excl.Downloads : h.Downloads,
                    BytesServed = excludedByHour.TryGetValue(h.Hour, out var excl2) ? h.BytesServed - excl2.BytesServed : h.BytesServed,
                    CacheHitBytes = excludedByHour.TryGetValue(h.Hour, out var excl3) ? h.CacheHitBytes - excl3.CacheHitBytes : h.CacheHitBytes,
                    CacheMissBytes = excludedByHour.TryGetValue(h.Hour, out var excl4) ? h.CacheMissBytes - excl4.CacheMissBytes : h.CacheMissBytes
                }).ToList();
            }
            else
            {
                hourlyData = hourlyDataAll;
            }

            // Fill in missing hours with zeros and calculate averages
            var allHours = Enumerable.Range(0, 24)
                .Select(h => {
                    var existing = hourlyData.FirstOrDefault(hd => hd.Hour == h);
                    if (existing != null)
                    {
                        existing.AvgDownloads = Math.Round((double)existing.Downloads / daysInPeriod, 1);
                        existing.AvgBytesServed = existing.BytesServed / daysInPeriod;
                        return existing;
                    }
                    return new HourlyActivityItem { Hour = h };
                })
                .OrderBy(h => h.Hour)
                .ToList();

            // Find peak hour (based on total downloads, not average)
            var peakHour = allHours.OrderByDescending(h => h.Downloads).FirstOrDefault()?.Hour ?? 0;

            return Ok(new HourlyActivityResponse
            {
                Hours = allHours,
                PeakHour = peakHour,
                TotalDownloads = allHours.Sum(h => h.Downloads),
                TotalBytesServed = allHours.Sum(h => h.BytesServed),
                DaysInPeriod = daysInPeriod,
                PeriodStart = periodStartTimestamp,
                PeriodEnd = periodEndTimestamp,
                Period = startTime.HasValue ? "filtered" : "all"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting hourly activity data");
            return Ok(new HourlyActivityResponse { Period = "error" });
        }
    }

    /// <summary>
    /// Get cache growth data over time
    /// Shows how much new data has been added to the cache
    /// Pass actualCacheSize to detect deletions and calculate net growth
    /// </summary>
    [HttpGet("cache-growth")]
    [OutputCache(PolicyName = "stats-long")]
    public async Task<IActionResult> GetCacheGrowth(
        [FromQuery] long? startTime = null,
        [FromQuery] long? endTime = null,
        [FromQuery] string interval = "daily",
        [FromQuery] long? actualCacheSize = null,
        [FromQuery] int? eventId = null)
    {
        try
        {
            // Parse event IDs
            var eventIdList = ParseEventId(eventId);
            var hiddenClientIps = _stateRepository.GetHiddenClientIps();
            var statsExcludedOnlyIps = _stateRepository.GetStatsExcludedOnlyClientIps();

            DateTime? cutoffTime = startTime.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(startTime.Value).UtcDateTime
                : (DateTime?)null;
            DateTime? endDateTime = endTime.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(endTime.Value).UtcDateTime
                : (DateTime?)null;
            var intervalMinutes = TimeUtils.ParseInterval(interval);

            // Get cache info for current size/capacity
            long currentCacheSize = 0;
            long totalCapacity = 0;

            // Try to get cache info from the system controller's cache service
            // For now, we'll calculate from downloads data (exclude stats-excluded IPs from calculations)
            var allTimeQuery = ApplyHiddenClientFilter(_context.Downloads.AsNoTracking(), hiddenClientIps);
            var totalCacheMiss = await SumCacheMissBytesExcludingAsync(allTimeQuery, statsExcludedOnlyIps);

            currentCacheSize = totalCacheMiss; // Approximation: total cache misses = data added to cache

            // Build base query with time filtering (filter out hidden IPs, exclude stats-excluded from calculations)
            var baseQuery = ApplyHiddenClientFilter(_context.Downloads.AsNoTracking(), hiddenClientIps);

            // Apply event filter if provided (filters to only tagged downloads)
            HashSet<int>? eventDownloadIds = eventIdList.Count > 0 ? await GetEventDownloadIdsAsync(eventIdList) : null;
            baseQuery = ApplyEventFilter(baseQuery, eventIdList, eventDownloadIds);

            if (cutoffTime.HasValue)
            {
                baseQuery = baseQuery.Where(d => d.StartTimeUtc >= cutoffTime.Value);
            }
            if (endDateTime.HasValue)
            {
                baseQuery = baseQuery.Where(d => d.StartTimeUtc <= endDateTime.Value);
            }

            // Get daily cache growth data points
            // Exclude stats-excluded IPs from calculations
            List<CacheGrowthDataPoint> dataPoints;

            if (intervalMinutes >= 1440) // Daily or larger
            {
                // Group by date
                var allDataPoints = await baseQuery
                    .GroupBy(d => d.StartTimeUtc.Date)
                    .OrderBy(g => g.Key)
                    .Select(g => new CacheGrowthDataPoint
                    {
                        Timestamp = g.Key,
                        CumulativeCacheMissBytes = 0, // Will calculate cumulative below
                        GrowthFromPrevious = g.Sum(d => d.CacheMissBytes)
                    })
                    .ToListAsync();

                // Subtract excluded IPs if any
                if (statsExcludedOnlyIps.Count > 0)
                {
                    var excludedDataPoints = await baseQuery
                        .Where(d => statsExcludedOnlyIps.Contains(d.ClientIp))
                        .GroupBy(d => d.StartTimeUtc.Date)
                        .Select(g => new { Date = g.Key, Growth = g.Sum(d => d.CacheMissBytes) })
                        .ToListAsync();

                    var excludedByDate = excludedDataPoints.ToDictionary(d => d.Date, d => d.Growth);
                    dataPoints = allDataPoints.Select(dp => new CacheGrowthDataPoint
                    {
                        Timestamp = dp.Timestamp,
                        CumulativeCacheMissBytes = 0,
                        GrowthFromPrevious = excludedByDate.TryGetValue(dp.Timestamp.Date, out var excl) 
                            ? dp.GrowthFromPrevious - excl 
                            : dp.GrowthFromPrevious
                    }).ToList();
                }
                else
                {
                    dataPoints = allDataPoints;
                }
            }
            else
            {
                // Group by hour for smaller intervals
                var allHourlyData = await baseQuery
                    .GroupBy(d => new { d.StartTimeUtc.Date, d.StartTimeUtc.Hour })
                    .OrderBy(g => g.Key.Date).ThenBy(g => g.Key.Hour)
                    .Select(g => new CacheGrowthDataPoint
                    {
                        Timestamp = g.Key.Date.AddHours(g.Key.Hour),
                        CumulativeCacheMissBytes = 0,
                        GrowthFromPrevious = g.Sum(d => d.CacheMissBytes)
                    })
                    .ToListAsync();

                // Subtract excluded IPs if any
                if (statsExcludedOnlyIps.Count > 0)
                {
                    var excludedHourlyData = await baseQuery
                        .Where(d => statsExcludedOnlyIps.Contains(d.ClientIp))
                        .GroupBy(d => new { d.StartTimeUtc.Date, d.StartTimeUtc.Hour })
                        .Select(g => new { Date = g.Key.Date, Hour = g.Key.Hour, Growth = g.Sum(d => d.CacheMissBytes) })
                        .ToListAsync();

                    var excludedByDateTime = excludedHourlyData.ToDictionary(
                        d => d.Date.AddHours(d.Hour), 
                        d => d.Growth);

                    dataPoints = allHourlyData.Select(dp => new CacheGrowthDataPoint
                    {
                        Timestamp = dp.Timestamp,
                        CumulativeCacheMissBytes = 0,
                        GrowthFromPrevious = excludedByDateTime.TryGetValue(dp.Timestamp, out var excl) 
                            ? dp.GrowthFromPrevious - excl 
                            : dp.GrowthFromPrevious
                    }).ToList();
                }
                else
                {
                    dataPoints = allHourlyData;
                }
            }

            // Calculate cumulative values
            long cumulative = 0;
            foreach (var dp in dataPoints)
            {
                cumulative += dp.GrowthFromPrevious;
                dp.CumulativeCacheMissBytes = cumulative;
                dp.Timestamp = dp.Timestamp.AsUtc();
            }

            // Calculate trend and statistics using period-over-period comparison
            // Compare recent half growth to older half growth for meaningful trends
            var trend = "stable";
            double percentChange = 0;
            long avgDailyGrowth = 0;

            if (dataPoints.Count >= 2)
            {
                var firstValue = dataPoints.First().CumulativeCacheMissBytes;
                var lastValue = dataPoints.Last().CumulativeCacheMissBytes;

                var daysCovered = (dataPoints.Last().Timestamp - dataPoints.First().Timestamp).TotalDays;
                if (daysCovered > 0)
                {
                    avgDailyGrowth = (long)((lastValue - firstValue) / daysCovered);
                }

                // Period-over-period comparison: compare recent half growth rate to older half
                var growthValues = dataPoints.Select(d => (double)d.GrowthFromPrevious).ToList();
                var midpoint = growthValues.Count / 2;
                var olderHalf = growthValues.Take(midpoint).ToList();
                var recentHalf = growthValues.Skip(midpoint).ToList();

                var olderAvg = olderHalf.Count > 0 ? olderHalf.Average() : 0;
                var recentAvg = recentHalf.Count > 0 ? recentHalf.Average() : 0;

                if (olderAvg == 0 && recentAvg == 0)
                {
                    percentChange = 0;
                }
                else if (olderAvg == 0)
                {
                    percentChange = recentAvg > 0 ? 100 : 0; // New growth, cap at 100%
                }
                else
                {
                    percentChange = ((recentAvg - olderAvg) / olderAvg) * 100;
                }

                // Cap percentage at reasonable bounds (±999%)
                percentChange = Math.Max(-999, Math.Min(999, percentChange));
                percentChange = Math.Round(percentChange, 1);

                if (percentChange > 5) trend = "up";
                else if (percentChange < -5) trend = "down";
            }

            // Calculate net growth accounting for deletions
            // If actual cache size is provided and less than cumulative downloads,
            // data was deleted and we should show net growth
            long netAvgDailyGrowth = avgDailyGrowth;
            long estimatedBytesDeleted = 0;
            bool hasDataDeletion = false;
            bool cacheWasCleared = false;

            if (actualCacheSize.HasValue && actualCacheSize.Value > 0)
            {
                // Total cumulative downloads (all data ever added to cache)
                // Exclude stats-excluded IPs from calculations
                var allTimeQueryForCumulative = ApplyHiddenClientFilter(_context.Downloads.AsNoTracking(), hiddenClientIps);
                var cumulativeDownloads = await SumCacheMissBytesExcludingAsync(allTimeQueryForCumulative, statsExcludedOnlyIps);

                // If actual cache is smaller than cumulative downloads, data was deleted
                if (actualCacheSize.Value < cumulativeDownloads)
                {
                    hasDataDeletion = true;
                    estimatedBytesDeleted = cumulativeDownloads - actualCacheSize.Value;

                    // Detect if cache was essentially cleared (actual cache is very small)
                    // If actual cache is <5% of cumulative downloads OR <100MB, treat as "cleared"
                    const long CLEARED_THRESHOLD_BYTES = 100L * 1024 * 1024; // 100MB
                    var cacheRatio = cumulativeDownloads > 0
                        ? (double)actualCacheSize.Value / cumulativeDownloads
                        : 1.0;

                    cacheWasCleared = actualCacheSize.Value < CLEARED_THRESHOLD_BYTES || cacheRatio < 0.05;

                    if (cacheWasCleared)
                    {
                        // Cache was cleared - show the positive download rate as growth
                        // The deletion is a past event, current growth is positive (downloads happening)
                        netAvgDailyGrowth = avgDailyGrowth;
                    }
                    else if (dataPoints.Count >= 2)
                    {
                        // Cache has some deletions but wasn't fully cleared
                        // Calculate proportional net growth
                        var firstTimestamp = dataPoints.First().Timestamp;
                        var lastTimestamp = dataPoints.Last().Timestamp;
                        var totalDays = (lastTimestamp - firstTimestamp).TotalDays;

                        if (totalDays > 0)
                        {
                            var deletionRate = (double)estimatedBytesDeleted / totalDays;
                            netAvgDailyGrowth = (long)(avgDailyGrowth - deletionRate);
                        }
                    }
                }
            }

            // Estimate days until full using net growth (not raw download growth)
            int? daysUntilFull = null;
            if (netAvgDailyGrowth > 0 && totalCapacity > 0)
            {
                var remainingSpace = totalCapacity - (actualCacheSize ?? currentCacheSize);
                if (remainingSpace > 0)
                {
                    daysUntilFull = (int)Math.Ceiling((double)remainingSpace / netAvgDailyGrowth);
                }
            }

            return Ok(new CacheGrowthResponse
            {
                DataPoints = dataPoints,
                CurrentCacheSize = actualCacheSize ?? currentCacheSize,
                TotalCapacity = totalCapacity,
                AverageDailyGrowth = avgDailyGrowth,
                NetAverageDailyGrowth = netAvgDailyGrowth,
                Trend = trend,
                PercentChange = percentChange,
                EstimatedDaysUntilFull = daysUntilFull,
                Period = startTime.HasValue ? "filtered" : "all",
                HasDataDeletion = hasDataDeletion,
                EstimatedBytesDeleted = estimatedBytesDeleted,
                CacheWasCleared = cacheWasCleared
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache growth data");
            return Ok(new CacheGrowthResponse { Period = "error" });
        }
    }

    /// <summary>
    /// Get sparkline data for dashboard stat cards
    /// Returns daily aggregated data for bandwidth saved, cache hit ratio, total served, and added to cache
    /// </summary>
    [HttpGet("sparklines")]
    [OutputCache(PolicyName = "stats-long")]
    public async Task<IActionResult> GetSparklineData(
        [FromQuery] long? startTime = null,
        [FromQuery] long? endTime = null,
        [FromQuery] int? eventId = null)
    {
        try
        {
            // Parse event IDs
            var eventIdList = ParseEventId(eventId);

            // Build query with optional time filtering
            // Filter out hidden IPs completely, but include excluded IPs (they'll be excluded from calculations)
            var query = _context.Downloads.AsNoTracking();
            var hiddenClientIps = _stateRepository.GetHiddenClientIps();
            var statsExcludedOnlyIps = _stateRepository.GetStatsExcludedOnlyClientIps();
            query = ApplyHiddenClientFilter(query, hiddenClientIps);

            // Apply event filter if provided (filters to only tagged downloads)
            HashSet<int>? eventDownloadIds = eventIdList.Count > 0 ? await GetEventDownloadIdsAsync(eventIdList) : null;
            query = ApplyEventFilter(query, eventIdList, eventDownloadIds);

            if (startTime.HasValue)
            {
                var cutoffTime = DateTimeOffset.FromUnixTimeSeconds(startTime.Value).UtcDateTime;
                query = query.Where(d => d.StartTimeUtc >= cutoffTime);
            }
            if (endTime.HasValue)
            {
                var endDateTime = DateTimeOffset.FromUnixTimeSeconds(endTime.Value).UtcDateTime;
                query = query.Where(d => d.StartTimeUtc <= endDateTime);
            }

            // Query downloads grouped by date (exclude stats-excluded IPs from calculations)
            var dailyDataAll = await query
                .GroupBy(d => d.StartTimeUtc.Date)
                .OrderBy(g => g.Key)
                .Select(g => new
                {
                    Date = g.Key,
                    CacheHitBytes = g.Sum(d => d.CacheHitBytes),
                    CacheMissBytes = g.Sum(d => d.CacheMissBytes)
                })
                .ToListAsync();

            // Subtract excluded IPs from calculations if any
            var dailyData = dailyDataAll;
            if (statsExcludedOnlyIps.Count > 0)
            {
                var excludedDailyData = await query
                    .Where(d => statsExcludedOnlyIps.Contains(d.ClientIp))
                    .GroupBy(d => d.StartTimeUtc.Date)
                    .Select(g => new { Date = g.Key, CacheHitBytes = g.Sum(d => d.CacheHitBytes), CacheMissBytes = g.Sum(d => d.CacheMissBytes) })
                    .ToListAsync();

                var excludedByDate = excludedDailyData.ToDictionary(d => d.Date);
                dailyData = dailyDataAll.Select(d => new
                {
                    d.Date,
                    CacheHitBytes = excludedByDate.TryGetValue(d.Date, out var excl) ? d.CacheHitBytes - excl.CacheHitBytes : d.CacheHitBytes,
                    CacheMissBytes = excludedByDate.TryGetValue(d.Date, out var excl2) ? d.CacheMissBytes - excl2.CacheMissBytes : d.CacheMissBytes
                }).ToList();
            }

            // Build sparkline data for each metric
            var bandwidthSavedData = dailyData.Select(d => (double)d.CacheHitBytes).ToList();
            var addedToCacheData = dailyData.Select(d => (double)d.CacheMissBytes).ToList();
            var totalServedData = dailyData.Select(d => (double)(d.CacheHitBytes + d.CacheMissBytes)).ToList();
            var cacheHitRatioData = dailyData.Select(d =>
            {
                var total = d.CacheHitBytes + d.CacheMissBytes;
                return total > 0 ? (d.CacheHitBytes * 100.0) / total : 0.0;
            }).ToList();

            return Ok(new SparklineDataResponse
            {
                BandwidthSaved = BuildSparklineMetric(bandwidthSavedData),
                CacheHitRatio = BuildSparklineMetricForRatio(cacheHitRatioData),
                TotalServed = BuildSparklineMetric(totalServedData),
                AddedToCache = BuildSparklineMetric(addedToCacheData),
                Period = startTime.HasValue ? "filtered" : "all"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting sparkline data");
            return Ok(new SparklineDataResponse { Period = "error" });
        }
    }

    // Simple linear regression to get slope and intercept
    private static (double slope, double intercept, bool valid) LinearRegression(IReadOnlyList<double> data)
    {
        int n = data.Count;
        if (n < 2) return (0, 0, false);

        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        for (int i = 0; i < n; i++)
        {
            sumX += i;
            sumY += data[i];
            sumXY += i * data[i];
            sumX2 += i * i;
        }

        double denom = n * sumX2 - sumX * sumX;
        if (Math.Abs(denom) < 0.0001) return (0, 0, false);

        double slope = (n * sumXY - sumX * sumY) / denom;
        double intercept = (sumY - slope * sumX) / n;
        return (slope, intercept, true);
    }

    private static SparklineMetric BuildSparklineMetric(List<double> data)
    {
        // Trim trailing zeros
        var trimmed = data.ToList();
        while (trimmed.Count > 1 && trimmed.Last() == 0)
            trimmed.RemoveAt(trimmed.Count - 1);

        if (trimmed.Count < 2)
            return new SparklineMetric { Data = data, PredictedData = [], Trend = "stable", PercentChange = 0 };

        var (slope, intercept, valid) = LinearRegression(trimmed);
        if (!valid)
            return new SparklineMetric { Data = data, PredictedData = [], Trend = "stable", PercentChange = 0 };

        // Generate 3 predicted points
        int n = trimmed.Count;
        var predicted = new List<double>
        {
            Math.Max(0, slope * n + intercept),
            Math.Max(0, slope * (n + 1) + intercept),
            Math.Max(0, slope * (n + 2) + intercept)
        };

        // Percent change = slope * 3 days / current trendline value
        double currentValue = slope * (n - 1) + intercept;
        double pct = Math.Abs(currentValue) > 0.001 ? (slope * 3 / Math.Abs(currentValue)) * 100 : 0;
        pct = Math.Clamp(pct, -500, 500);

        string trend = pct > 5 ? "up" : pct < -5 ? "down" : "stable";

        return new SparklineMetric
        {
            Data = data,
            PredictedData = predicted,
            Trend = trend,
            PercentChange = Math.Round(pct, 1)
        };
    }

    private static SparklineMetric BuildSparklineMetricForRatio(List<double> data)
    {
        // Trim trailing zeros
        var trimmed = data.ToList();
        while (trimmed.Count > 1 && trimmed.Last() == 0)
            trimmed.RemoveAt(trimmed.Count - 1);

        if (trimmed.Count < 2)
            return new SparklineMetric { Data = data, PredictedData = [], Trend = "stable", PercentChange = 0, IsAbsoluteChange = true };

        var (slope, intercept, valid) = LinearRegression(trimmed);
        if (!valid)
            return new SparklineMetric { Data = data, PredictedData = [], Trend = "stable", PercentChange = 0, IsAbsoluteChange = true };

        // Generate 3 predicted points (clamped 0-100 for ratios)
        int n = trimmed.Count;
        var predicted = new List<double>
        {
            Math.Clamp(slope * n + intercept, 0, 100),
            Math.Clamp(slope * (n + 1) + intercept, 0, 100),
            Math.Clamp(slope * (n + 2) + intercept, 0, 100)
        };

        // Point change = slope * 3 days
        double pts = Math.Clamp(slope * 3, -100, 100);
        string trend = pts > 2 ? "up" : pts < -2 ? "down" : "stable";

        return new SparklineMetric
        {
            Data = data,
            PredictedData = predicted,
            Trend = trend,
            PercentChange = Math.Round(pts, 1),
            IsAbsoluteChange = true
        };
    }

    /// <summary>
    /// Get historical cache size snapshot for a time range.
    /// Returns estimated used space based on periodic snapshots.
    /// </summary>
    [HttpGet("cache-snapshot")]
    [OutputCache(PolicyName = "stats-short")]
    public async Task<IActionResult> GetCacheSnapshot(
        [FromQuery] long? startTime = null,
        [FromQuery] long? endTime = null)
    {
        try
        {
            if (!startTime.HasValue || !endTime.HasValue)
            {
                return Ok(new CacheSnapshotResponse { HasData = false });
            }

            var startUtc = DateTimeOffset.FromUnixTimeSeconds(startTime.Value).UtcDateTime;
            var endUtc = DateTimeOffset.FromUnixTimeSeconds(endTime.Value).UtcDateTime;

            var summary = await _cacheSnapshotService.GetSnapshotSummaryAsync(startUtc, endUtc);

            if (summary == null)
            {
                return Ok(new CacheSnapshotResponse { HasData = false });
            }

            return Ok(new CacheSnapshotResponse
            {
                HasData = true,
                StartUsedSize = summary.StartUsedSize,
                EndUsedSize = summary.EndUsedSize,
                AverageUsedSize = summary.AverageUsedSize,
                TotalCacheSize = summary.TotalCacheSize,
                SnapshotCount = summary.SnapshotCount,
                IsEstimate = summary.IsEstimate
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache snapshot data");
            return Ok(new CacheSnapshotResponse { HasData = false });
        }
    }
}

/// <summary>
/// Response for historical cache size snapshots
/// </summary>
public class CacheSnapshotResponse
{
    public bool HasData { get; set; }
    public long StartUsedSize { get; set; }
    public long EndUsedSize { get; set; }
    public long AverageUsedSize { get; set; }
    public long TotalCacheSize { get; set; }
    public int SnapshotCount { get; set; }
    public bool IsEstimate { get; set; }
}

public class StatsExclusionsResponse
{
    public List<string> Ips { get; set; } = new();
}

public class UpdateStatsExclusionsRequest
{
    public List<string> Ips { get; set; } = new();
}
