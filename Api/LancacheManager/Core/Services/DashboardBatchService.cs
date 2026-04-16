using LancacheManager.Configuration;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using LancacheManager.Models.Responses;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace LancacheManager.Core.Services;

/// <summary>
/// Shared compute logic for <c>GET /api/dashboard/batch</c>. Extracted from DashboardController
/// so a startup warmer (DashboardCacheWarmerService) can pre-populate the IMemoryCache before the
/// first user request arrives — otherwise the first request after a server restart would run
/// 9 parallel DB queries on a cold connection pool.
/// </summary>
public class DashboardBatchService : IDashboardBatchService
{
    private readonly CacheManagementService _cacheService;
    private readonly GameCacheDetectionService _gameCacheDetectionService;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IStateService _stateRepository;
    private readonly IOptions<ApiOptions> _apiOptions;
    private readonly ILogger<DashboardBatchService> _logger;
    private readonly CacheSnapshotService _cacheSnapshotService;
    private readonly IMemoryCache _memoryCache;

    public DashboardBatchService(
        CacheManagementService cacheService,
        GameCacheDetectionService gameCacheDetectionService,
        IDbContextFactory<AppDbContext> dbContextFactory,
        IServiceScopeFactory scopeFactory,
        IStateService stateRepository,
        IOptions<ApiOptions> apiOptions,
        ILogger<DashboardBatchService> logger,
        CacheSnapshotService cacheSnapshotService,
        IMemoryCache memoryCache)
    {
        _cacheService = cacheService;
        _gameCacheDetectionService = gameCacheDetectionService;
        _dbContextFactory = dbContextFactory;
        _scopeFactory = scopeFactory;
        _stateRepository = stateRepository;
        _apiOptions = apiOptions;
        _logger = logger;
        _cacheSnapshotService = cacheSnapshotService;
        _memoryCache = memoryCache;
    }

    public async Task<DashboardBatchResponse> GetBatchAsync(
        long? startTime,
        long? endTime,
        long? eventId,
        CancellationToken ct)
    {
        // Shared state used by multiple sub-queries
        var hiddenClientIps = _stateRepository.GetHiddenClientIps();
        var statsExcludedOnlyIps = _stateRepository.GetStatsExcludedOnlyClientIps();
        var excludedClientIps = _stateRepository.GetExcludedClientIps();
        var evictedMode = _stateRepository.GetEvictedDataMode();
        var eventIdList = eventId.HasValue ? new List<long> { eventId.Value } : new List<long>();

        var cacheKey = $"dashboard-batch:{startTime}:{endTime}:{eventId}:{evictedMode}";
        if (_memoryCache.TryGetValue(cacheKey, out DashboardBatchResponse? cachedResponse) && cachedResponse != null)
        {
            return cachedResponse;
        }

        // Pre-fetch event download IDs once (shared by clients, services, dashboard, downloads)
        HashSet<long>? eventDownloadIds = eventIdList.Count > 0
            ? await GetEventDownloadIdsAsync(eventIdList)
            : null;

        // Cache must complete first (cacheGrowth depends on its result)
        var cacheResult = await SafeExecuteAsync("cache", () => GetCacheInfoAsync());
        long actualCacheSize = cacheResult?.UsedCacheSize ?? 0;

        // Launch remaining 9 queries fully in parallel. AddPooledDbContextFactory bounds concurrency.
        var clientsTask = SafeExecuteAsync("clients", () => GetClientStatsAsync(startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode, statsExcludedOnlyIps));
        var servicesTask = SafeExecuteAsync("services", () => GetServiceStatsAsync(startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode, statsExcludedOnlyIps));
        var dashboardTask = SafeExecuteAsync("dashboard", () => GetDashboardStatsAsync(startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode, statsExcludedOnlyIps));
        var downloadsTask = SafeExecuteAsync("downloads", () => GetLatestDownloadsAsync(startTime, endTime, eventIdList, eventDownloadIds, excludedClientIps, evictedMode));
        var detectionTask = SafeExecuteAsync("detection", () => GetCachedDetectionAsync());
        var sparklinesTask = SafeExecuteAsync("sparklines", () => GetSparklineDataAsync(startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode, statsExcludedOnlyIps));
        var hourlyTask = SafeExecuteAsync("hourlyActivity", () => GetHourlyActivityAsync(startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode, statsExcludedOnlyIps));
        var cacheSnapshotTask = SafeExecuteAsync("cacheSnapshot", () => GetCacheSnapshotAsync(startTime, endTime));
        var cacheGrowthTask = SafeExecuteAsync("cacheGrowth", () => GetCacheGrowthAsync(startTime, endTime, actualCacheSize, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode, statsExcludedOnlyIps));

        await Task.WhenAll(clientsTask, servicesTask, dashboardTask, downloadsTask, detectionTask, sparklinesTask, hourlyTask, cacheSnapshotTask, cacheGrowthTask);

        DashboardBatchResponse response = new()
        {
            Cache = cacheResult,
            Clients = await clientsTask,
            Services = await servicesTask,
            Dashboard = await dashboardTask,
            Downloads = await downloadsTask,
            Detection = await detectionTask,
            Sparklines = await sparklinesTask,
            HourlyActivity = await hourlyTask,
            CacheSnapshot = await cacheSnapshotTask,
            CacheGrowth = await cacheGrowthTask
        };

        // Non-live ranges (startTime/endTime fixed) cache for 60s; live (no bounds) cache for 15s.
        var isLive = !startTime.HasValue && !endTime.HasValue;
        var cacheOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(TimeSpan.FromSeconds(isLive ? 15 : 60))
            .SetSize(50_000)
            .SetPriority(CacheItemPriority.High);
        _memoryCache.Set(cacheKey, response, cacheOptions);

        return response;
    }

    // ───────────────────── Sub-query implementations ─────────────────────

    private async Task<CacheInfo> GetCacheInfoAsync()
    {
        return await _cacheService.GetCacheInfoAsync();
    }

    private async Task<object> GetClientStatsAsync(
        long? startTime, long? endTime,
        List<long> eventIdList, HashSet<long>? eventDownloadIds,
        List<string> hiddenClientIps, string evictedMode,
        List<string> statsExcludedOnlyIps)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var maxLimit = _apiOptions.Value.MaxClientsPerRequest;
        var defaultLimit = _apiOptions.Value.DefaultClientsLimit;
        var effectiveLimit = Math.Min(defaultLimit, maxLimit);

        var query = context.Downloads.AsNoTracking();
        query = query.ApplyEventFilter(eventIdList, eventDownloadIds);
        query = query.ApplyHiddenClientFilter(hiddenClientIps);
        query = query.ApplyEvictedFilter(evictedMode);

        if (startTime.HasValue)
        {
            var startDate = startTime.Value.FromUnixSeconds();
            query = query.Where(d => d.StartTimeUtc >= startDate);
        }
        if (endTime.HasValue)
        {
            var endDate = endTime.Value.FromUnixSeconds();
            query = query.Where(d => d.StartTimeUtc <= endDate);
        }

        var ipStats = await query
            .GroupBy(d => d.ClientIp)
            .Select(g => new
            {
                ClientIp = g.Key,
                TotalCacheHitBytes = g.Sum(d => d.CacheHitBytes),
                TotalCacheMissBytes = g.Sum(d => d.CacheMissBytes),
                TotalDownloads = g.Count(),
                MinStartTimeUtc = g.Min(d => d.StartTimeUtc),
                MaxEndTimeUtc = g.Max(d => d.EndTimeUtc),
                LastActivityUtc = g.Max(d => d.StartTimeUtc)
            })
            .ToListAsync();

        // Calculate duration client-side (DateTime subtraction can't be translated to SQL)
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

        var result = ipStatsWithDuration
            .OrderByDescending(s => s.TotalCacheHitBytes + s.TotalCacheMissBytes)
            .Take(effectiveLimit)
            .Select(s => new ClientStats
            {
                ClientIp = s.ClientIp,
                TotalCacheHitBytes = s.TotalCacheHitBytes,
                TotalCacheMissBytes = s.TotalCacheMissBytes,
                TotalDownloads = s.TotalDownloads,
                TotalDurationSeconds = s.TotalDurationSeconds,
                LastActivityUtc = s.LastActivityUtc.AsUtc()
            })
            .ToList();

        return result;
    }

    private async Task<object> GetServiceStatsAsync(
        long? startTime, long? endTime,
        List<long> eventIdList, HashSet<long>? eventDownloadIds,
        List<string> hiddenClientIps, string evictedMode,
        List<string> statsExcludedOnlyIps)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var query = context.Downloads.AsNoTracking()
            .ApplyHiddenClientFilter(hiddenClientIps)
            .ApplyEvictedFilter(evictedMode);

        query = query.ApplyEventFilter(eventIdList, eventDownloadIds);

        if (startTime.HasValue)
        {
            var startDate = startTime.Value.FromUnixSeconds();
            query = query.Where(d => d.StartTimeUtc >= startDate);
        }
        if (endTime.HasValue)
        {
            var endDate = endTime.Value.FromUnixSeconds();
            query = query.Where(d => d.StartTimeUtc <= endDate);
        }

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

        return serviceStats.WithUtcMarking();
    }

    private async Task<object> GetDashboardStatsAsync(
        long? startTime, long? endTime,
        List<long> eventIdList, HashSet<long>? eventDownloadIds,
        List<string> hiddenClientIps, string evictedMode,
        List<string> statsExcludedOnlyIps)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        DateTime? cutoffTime = startTime.HasValue ? startTime.Value.FromUnixSeconds() : null;
        DateTime? endDateTime = endTime.HasValue ? endTime.Value.FromUnixSeconds() : null;

        var downloadsQuery = context.Downloads.AsNoTracking()
            .ApplyHiddenClientFilter(hiddenClientIps)
            .ApplyEvictedFilter(evictedMode);

        downloadsQuery = downloadsQuery.ApplyEventFilter(eventIdList, eventDownloadIds);

        if (cutoffTime.HasValue)
            downloadsQuery = downloadsQuery.Where(d => d.StartTimeUtc >= cutoffTime.Value);
        if (endDateTime.HasValue)
            downloadsQuery = downloadsQuery.Where(d => d.StartTimeUtc <= endDateTime.Value);

        // Calculate period metrics (exclude stats-excluded IPs)
        var periodQuery = statsExcludedOnlyIps.Count > 0
            ? downloadsQuery.Where(d => !statsExcludedOnlyIps.Contains(d.ClientIp))
            : downloadsQuery;

        // Combined period aggregates: hitBytes + missBytes + count in ONE query (was 3 round trips)
        var periodAgg = await periodQuery
            .GroupBy(d => 1)
            .Select(g => new
            {
                HitBytes = g.Sum(d => (long?)d.CacheHitBytes) ?? 0,
                MissBytes = g.Sum(d => (long?)d.CacheMissBytes) ?? 0,
                Count = g.Count()
            })
            .FirstOrDefaultAsync();

        var periodHitBytes = periodAgg?.HitBytes ?? 0;
        var periodMissBytes = periodAgg?.MissBytes ?? 0;
        var periodTotal = periodHitBytes + periodMissBytes;
        var periodHitRatio = periodTotal > 0 ? (periodHitBytes * 100.0) / periodTotal : 0;
        var periodDownloadCount = periodAgg?.Count ?? 0;

        // Active downloads (last 5 minutes, not ended)
        var activeThreshold = DateTime.UtcNow.AddMinutes(-5);
        var activeDownloads = await context.Downloads.AsNoTracking()
            .ApplyHiddenClientFilter(hiddenClientIps)
            .ApplyEvictedFilter(evictedMode)
            .CountAsync(d => d.StartTimeUtc >= activeThreshold && d.EndTimeUtc == default);

        // Unique clients in period
        var uniqueClientsQuery = statsExcludedOnlyIps.Count > 0
            ? downloadsQuery.Where(d => !statsExcludedOnlyIps.Contains(d.ClientIp))
            : downloadsQuery;
        var uniqueClientsCount = await uniqueClientsQuery.Select(d => d.ClientIp).Distinct().CountAsync();

        // Combined all-time aggregates: hitBytes + missBytes in ONE query (was 2 round trips)
        var allTimeQuery = context.Downloads.AsNoTracking()
            .ApplyHiddenClientFilter(hiddenClientIps)
            .ApplyEvictedFilter(evictedMode);
        if (statsExcludedOnlyIps.Count > 0)
            allTimeQuery = allTimeQuery.Where(d => !statsExcludedOnlyIps.Contains(d.ClientIp));

        var allTimeAgg = await allTimeQuery
            .GroupBy(d => 1)
            .Select(g => new
            {
                HitBytes = g.Sum(d => (long?)d.CacheHitBytes) ?? 0,
                MissBytes = g.Sum(d => (long?)d.CacheMissBytes) ?? 0
            })
            .FirstOrDefaultAsync();

        var totalHitBytes = allTimeAgg?.HitBytes ?? 0;
        var totalMissBytes = allTimeAgg?.MissBytes ?? 0;
        var totalServed = totalHitBytes + totalMissBytes;
        var cacheHitRatio = totalServed > 0 ? (totalHitBytes * 100.0) / totalServed : 0;

        // Service breakdown (also provides top service — no separate query needed)
        var serviceBreakdown = await downloadsQuery
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
            .ToListAsync();

        var topServiceName = serviceBreakdown.FirstOrDefault()?.Service ?? "N/A";

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

        return new DashboardStatsResponse
        {
            TotalBandwidthSaved = totalHitBytes,
            TotalAddedToCache = totalMissBytes,
            TotalServed = totalServed,
            CacheHitRatio = cacheHitRatio,
            ActiveDownloads = activeDownloads,
            UniqueClients = uniqueClientsCount,
            TopService = topServiceName,
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
            ServiceBreakdown = serviceBreakdown,
            LastUpdated = DateTime.UtcNow
        };
    }

    private async Task<object> GetLatestDownloadsAsync(
        long? startTime, long? endTime,
        List<long> eventIdList, HashSet<long>? eventDownloadIds,
        List<string> excludedClientIps, string evictedMode)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        const string PrefillToken = "prefill";
        List<Download> downloads;

        if (!startTime.HasValue && !endTime.HasValue && eventIdList.Count == 0)
        {
            // StatsDataService is registered Scoped — resolve via a scoped container so its
            // AppDbContext has a proper lifetime tied to this query.
            using var scope = _scopeFactory.CreateScope();
            var statsService = scope.ServiceProvider.GetRequiredService<IStatsDataService>();
            downloads = await statsService.GetLatestDownloadsAsync(int.MaxValue);
        }
        else
        {
            var startDate = startTime.HasValue ? startTime.Value.FromUnixSeconds() : DateTime.MinValue;
            var endDate = endTime.HasValue ? endTime.Value.FromUnixSeconds() : DateTime.UtcNow;

            IQueryable<Download> query;

            if (eventIdList.Count > 0)
            {
                query = context.Downloads.AsNoTracking()
                    .Where(d => context.EventDownloads
                        .Where(ed => eventIdList.Contains(ed.EventId))
                        .Select(ed => ed.DownloadId)
                        .Contains(d.Id))
                    .Where(d => d.StartTimeUtc >= startDate && d.StartTimeUtc <= endDate);
            }
            else
            {
                query = context.Downloads.AsNoTracking()
                    .Where(d => d.StartTimeUtc >= startDate && d.StartTimeUtc <= endDate);
            }

            query = query.ApplyEvictedFilter(evictedMode);
            downloads = await query
                .OrderByDescending(d => d.StartTimeUtc)
                .ToListAsync();
        }

        // Filter out excluded and prefill client IPs
        if (excludedClientIps.Count > 0)
        {
            downloads = downloads
                .Where(d => !excludedClientIps.Contains(d.ClientIp))
                .ToList();
        }

        downloads = downloads
            .Where(d => !string.Equals(d.ClientIp, PrefillToken, StringComparison.OrdinalIgnoreCase))
            .Where(d => !string.Equals(d.Datasource, PrefillToken, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (evictedMode == EvictedDataModes.ShowClean)
        {
            foreach (var d in downloads) d.IsEvicted = false;
        }

        // Resolve game names via Steam depot mappings + Epic lookup
        await ResolveGameNamesAsync(context, downloads);

        return downloads;
    }

    private async Task<object> GetCachedDetectionAsync()
    {
        var cachedResults = await _gameCacheDetectionService.GetCachedDetectionAsync();

        if (cachedResults == null)
        {
            return new CachedDetectionResponse { HasCachedResults = false };
        }

        var lastDetectionTimeUtc = cachedResults.StartTime.AsUtc();
        var games = cachedResults.Games ?? [];
        var activeGamesCount = games.Count(g => !g.IsEvicted);

        // Project into slim DTOs — the dashboard does NOT read cache_file_paths,
        // sample_urls, datasources, depot_ids, evicted_sample_urls, evicted_depot_ids,
        // or evicted_bytes. These unbounded list fields inflate the /api/dashboard/batch
        // payload by ~70-90% on large caches. The full GameCacheInfo / ServiceCacheInfo
        // shape remains on /api/games/cached-detection for the Management tab.
        var slimGames = games
            .Select(g => new DashboardGameSummary
            {
                GameAppId = g.GameAppId,
                GameName = g.GameName,
                CacheFilesFound = g.CacheFilesFound,
                TotalSizeBytes = g.TotalSizeBytes,
                Service = g.Service,
                ImageUrl = g.ImageUrl,
                EpicAppId = g.EpicAppId,
                IsEvicted = g.IsEvicted,
                EvictedDownloadsCount = g.EvictedDownloadsCount
            })
            .ToList();

        var slimServices = (cachedResults.Services ?? new List<ServiceCacheInfo>())
            .Select(s => new DashboardServiceSummary
            {
                ServiceName = s.ServiceName,
                CacheFilesFound = s.CacheFilesFound,
                TotalSizeBytes = s.TotalSizeBytes,
                IsEvicted = s.IsEvicted,
                EvictedDownloadsCount = s.EvictedDownloadsCount
            })
            .ToList();

        return new CachedDetectionResponse
        {
            HasCachedResults = true,
            Games = slimGames,
            Services = slimServices,
            TotalGamesDetected = activeGamesCount,
            TotalServicesDetected = cachedResults.TotalServicesDetected,
            LastDetectionTime = lastDetectionTimeUtc.ToString("o")
        };
    }

    // ───────────────────── New batch sub-queries (sparklines, hourly, cacheGrowth, cacheSnapshot) ─────────────────────

    private async Task<object> GetSparklineDataAsync(
        long? startTime, long? endTime,
        List<long> eventIdList, HashSet<long>? eventDownloadIds,
        List<string> hiddenClientIps, string evictedMode,
        List<string> statsExcludedOnlyIps)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var query = BuildBaseDownloadsQuery(context, hiddenClientIps, evictedMode);
        query = query.ApplyEventFilter(eventIdList, eventDownloadIds);

        if (startTime.HasValue)
        {
            var cutoffTime = startTime.Value.FromUnixSeconds();
            query = query.Where(d => d.StartTimeUtc >= cutoffTime);
        }
        if (endTime.HasValue)
        {
            var endDateTime = endTime.Value.FromUnixSeconds();
            query = query.Where(d => d.StartTimeUtc <= endDateTime);
        }

        // Determine bucket size based on time range
        int bucketMinutes = 1440; // default: 1 day
        if (startTime.HasValue && endTime.HasValue)
        {
            var rangeHours = (endTime.Value - startTime.Value) / 3600.0;
            if (rangeHours <= 2) bucketMinutes = 15;
            else if (rangeHours <= 13) bucketMinutes = 30;
            else if (rangeHours <= 25) bucketMinutes = 60;
        }

        var filteredQuery = statsExcludedOnlyIps.Count > 0
            ? query.Where(d => !statsExcludedOnlyIps.Contains(d.ClientIp))
            : query;

        // Group in SQL — mirrors the HourlyActivity / CacheGrowth SQL-side GroupBy pattern.
        // Returns 10-60 aggregated rows instead of tens of thousands of raw rows.
        List<BucketAggregate> bucketedData;
        if (bucketMinutes >= 1440)
        {
            bucketedData = await filteredQuery
                .GroupBy(d => d.StartTimeUtc.Date)
                .OrderBy(g => g.Key)
                .Select(g => new BucketAggregate
                {
                    CacheHitBytes = g.Sum(d => d.CacheHitBytes),
                    CacheMissBytes = g.Sum(d => d.CacheMissBytes)
                })
                .ToListAsync();
        }
        else if (bucketMinutes >= 60)
        {
            bucketedData = await filteredQuery
                .GroupBy(d => new { d.StartTimeUtc.Date, d.StartTimeUtc.Hour })
                .OrderBy(g => g.Key.Date).ThenBy(g => g.Key.Hour)
                .Select(g => new BucketAggregate
                {
                    CacheHitBytes = g.Sum(d => d.CacheHitBytes),
                    CacheMissBytes = g.Sum(d => d.CacheMissBytes)
                })
                .ToListAsync();
        }
        else
        {
            // Sub-hour buckets: group by Date + Hour + (Minute / bucketMinutes).
            bucketedData = await filteredQuery
                .GroupBy(d => new
                {
                    d.StartTimeUtc.Date,
                    d.StartTimeUtc.Hour,
                    MinuteBucket = d.StartTimeUtc.Minute / bucketMinutes
                })
                .OrderBy(g => g.Key.Date).ThenBy(g => g.Key.Hour).ThenBy(g => g.Key.MinuteBucket)
                .Select(g => new BucketAggregate
                {
                    CacheHitBytes = g.Sum(d => d.CacheHitBytes),
                    CacheMissBytes = g.Sum(d => d.CacheMissBytes)
                })
                .ToListAsync();
        }

        var bandwidthSavedData = bucketedData.Select(d => (double)d.CacheHitBytes).ToList();
        var addedToCacheData = bucketedData.Select(d => (double)d.CacheMissBytes).ToList();
        var totalServedData = bucketedData.Select(d => (double)(d.CacheHitBytes + d.CacheMissBytes)).ToList();
        var cacheHitRatioData = bucketedData.Select(d =>
        {
            var total = d.CacheHitBytes + d.CacheMissBytes;
            return total > 0 ? (d.CacheHitBytes * 100.0) / total : 0.0;
        }).ToList();

        return new SparklineDataResponse
        {
            BandwidthSaved = BuildSparklineMetric(bandwidthSavedData),
            CacheHitRatio = BuildSparklineMetricForRatio(cacheHitRatioData),
            TotalServed = BuildSparklineMetric(totalServedData),
            AddedToCache = BuildSparklineMetric(addedToCacheData),
            Period = startTime.HasValue ? "filtered" : "all"
        };
    }

    private async Task<object> GetHourlyActivityAsync(
        long? startTime, long? endTime,
        List<long> eventIdList, HashSet<long>? eventDownloadIds,
        List<string> hiddenClientIps, string evictedMode,
        List<string> statsExcludedOnlyIps)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var query = BuildBaseDownloadsQuery(context, hiddenClientIps, evictedMode);
        query = query.ApplyEventFilter(eventIdList, eventDownloadIds);

        DateTime? cutoffTime = null;
        DateTime? endDateTime = null;

        if (startTime.HasValue)
        {
            cutoffTime = startTime.Value.FromUnixSeconds();
            query = query.Where(d => d.StartTimeUtc >= cutoffTime);
        }
        if (endTime.HasValue)
        {
            endDateTime = endTime.Value.FromUnixSeconds();
            query = query.Where(d => d.StartTimeUtc <= endDateTime);
        }

        int daysInPeriod = 1;
        long? periodStartTimestamp = null;
        long? periodEndTimestamp = null;

        if (startTime.HasValue && endTime.HasValue)
        {
            daysInPeriod = Math.Max(1, (int)Math.Ceiling((endDateTime!.Value - cutoffTime!.Value).TotalDays));
            periodStartTimestamp = startTime.Value;
            periodEndTimestamp = endTime.Value;
        }
        else
        {
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

        var filteredQuery = statsExcludedOnlyIps.Count > 0
            ? query.Where(d => !statsExcludedOnlyIps.Contains(d.ClientIp))
            : query;

        var hourlyData = await filteredQuery
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

        var peakHour = allHours.OrderByDescending(h => h.Downloads).FirstOrDefault()?.Hour ?? 0;

        return new HourlyActivityResponse
        {
            Hours = allHours,
            PeakHour = peakHour,
            TotalDownloads = allHours.Sum(h => h.Downloads),
            TotalBytesServed = allHours.Sum(h => h.BytesServed),
            DaysInPeriod = daysInPeriod,
            PeriodStart = periodStartTimestamp,
            PeriodEnd = periodEndTimestamp,
            Period = startTime.HasValue ? "filtered" : "all"
        };
    }

    private async Task<object> GetCacheGrowthAsync(
        long? startTime, long? endTime, long actualCacheSize,
        List<long> eventIdList, HashSet<long>? eventDownloadIds,
        List<string> hiddenClientIps, string evictedMode,
        List<string> statsExcludedOnlyIps)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        const string interval = "daily";
        DateTime? cutoffTime = startTime.HasValue
            ? startTime.Value.FromUnixSeconds()
            : (DateTime?)null;
        DateTime? endDateTime = endTime.HasValue
            ? endTime.Value.FromUnixSeconds()
            : (DateTime?)null;
        var intervalMinutes = TimeUtils.ParseInterval(interval);

        long currentCacheSize = 0;
        long totalCapacity = 0;

        var allTimeQuery = BuildBaseDownloadsQuery(context, hiddenClientIps, evictedMode);
        var totalCacheMiss = await AggregateExcludingAsync(allTimeQuery, statsExcludedOnlyIps,
            q => q.SumAsync(d => (long?)d.CacheMissBytes).ContinueWith(t => t.Result ?? 0L));

        currentCacheSize = totalCacheMiss;

        var baseQuery = BuildBaseDownloadsQuery(context, hiddenClientIps, evictedMode);
        baseQuery = baseQuery.ApplyEventFilter(eventIdList, eventDownloadIds);

        if (cutoffTime.HasValue)
            baseQuery = baseQuery.Where(d => d.StartTimeUtc >= cutoffTime.Value);
        if (endDateTime.HasValue)
            baseQuery = baseQuery.Where(d => d.StartTimeUtc <= endDateTime.Value);

        var filteredQuery = statsExcludedOnlyIps.Count > 0
            ? baseQuery.Where(d => !statsExcludedOnlyIps.Contains(d.ClientIp))
            : baseQuery;

        List<CacheGrowthDataPoint> dataPoints;

        if (intervalMinutes >= 1440)
        {
            dataPoints = await filteredQuery
                .GroupBy(d => d.StartTimeUtc.Date)
                .OrderBy(g => g.Key)
                .Select(g => new CacheGrowthDataPoint
                {
                    Timestamp = g.Key,
                    CumulativeCacheMissBytes = 0,
                    GrowthFromPrevious = g.Sum(d => d.CacheMissBytes)
                })
                .ToListAsync();
        }
        else
        {
            dataPoints = await filteredQuery
                .GroupBy(d => new { d.StartTimeUtc.Date, d.StartTimeUtc.Hour })
                .OrderBy(g => g.Key.Date).ThenBy(g => g.Key.Hour)
                .Select(g => new CacheGrowthDataPoint
                {
                    Timestamp = g.Key.Date.AddHours(g.Key.Hour),
                    CumulativeCacheMissBytes = 0,
                    GrowthFromPrevious = g.Sum(d => d.CacheMissBytes)
                })
                .ToListAsync();
        }

        long cumulative = 0;
        foreach (var dp in dataPoints)
        {
            cumulative += dp.GrowthFromPrevious;
            dp.CumulativeCacheMissBytes = cumulative;
        }
        dataPoints.WithUtcMarking();

        var trend = "stable";
        double percentChange = 0;
        long avgDailyGrowth = 0;

        if (dataPoints.Count >= 2)
        {
            var firstValue = dataPoints.First().CumulativeCacheMissBytes;
            var lastValue = dataPoints.Last().CumulativeCacheMissBytes;

            var daysCovered = (dataPoints.Last().Timestamp - dataPoints.First().Timestamp).TotalDays;
            if (daysCovered > 0)
                avgDailyGrowth = (long)((lastValue - firstValue) / daysCovered);

            var growthValues = dataPoints.Select(d => (double)d.GrowthFromPrevious).ToList();
            var midpoint = growthValues.Count / 2;
            var olderHalf = growthValues.Take(midpoint).ToList();
            var recentHalf = growthValues.Skip(midpoint).ToList();

            var olderAvg = olderHalf.Count > 0 ? olderHalf.Average() : 0;
            var recentAvg = recentHalf.Count > 0 ? recentHalf.Average() : 0;

            if (olderAvg == 0 && recentAvg == 0)
                percentChange = 0;
            else if (olderAvg == 0)
                percentChange = recentAvg > 0 ? 100 : 0;
            else
                percentChange = ((recentAvg - olderAvg) / olderAvg) * 100;

            percentChange = Math.Max(-999, Math.Min(999, percentChange));
            percentChange = Math.Round(percentChange, 1);

            if (percentChange > 5) trend = "up";
            else if (percentChange < -5) trend = "down";
        }

        long netAvgDailyGrowth = avgDailyGrowth;
        long estimatedBytesDeleted = 0;
        bool hasDataDeletion = false;
        bool cacheWasCleared = false;

        if (actualCacheSize > 0)
        {
            var allTimeQueryForCumulative = BuildBaseDownloadsQuery(context, hiddenClientIps, evictedMode);
            var cumulativeDownloads = await AggregateExcludingAsync(allTimeQueryForCumulative, statsExcludedOnlyIps,
                q => q.SumAsync(d => (long?)d.CacheMissBytes).ContinueWith(t => t.Result ?? 0L));

            if (actualCacheSize < cumulativeDownloads)
            {
                hasDataDeletion = true;
                estimatedBytesDeleted = cumulativeDownloads - actualCacheSize;

                const long CLEARED_THRESHOLD_BYTES = 100L * 1024 * 1024;
                var cacheRatio = cumulativeDownloads > 0
                    ? (double)actualCacheSize / cumulativeDownloads
                    : 1.0;

                cacheWasCleared = actualCacheSize < CLEARED_THRESHOLD_BYTES || cacheRatio < 0.05;

                if (cacheWasCleared)
                {
                    netAvgDailyGrowth = avgDailyGrowth;
                }
                else if (dataPoints.Count >= 2)
                {
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

        int? daysUntilFull = null;
        if (netAvgDailyGrowth > 0 && totalCapacity > 0)
        {
            var remainingSpace = totalCapacity - (actualCacheSize > 0 ? actualCacheSize : currentCacheSize);
            if (remainingSpace > 0)
                daysUntilFull = (int)Math.Ceiling((double)remainingSpace / netAvgDailyGrowth);
        }

        return new CacheGrowthResponse
        {
            DataPoints = dataPoints,
            CurrentCacheSize = actualCacheSize > 0 ? actualCacheSize : currentCacheSize,
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
        };
    }

    private async Task<object> GetCacheSnapshotAsync(long? startTime, long? endTime)
    {
        if (!startTime.HasValue || !endTime.HasValue)
        {
            return new CacheSnapshotResponse { HasData = false };
        }

        var startUtc = startTime.Value.FromUnixSeconds();
        var endUtc = endTime.Value.FromUnixSeconds();

        var summary = await _cacheSnapshotService.GetSnapshotSummaryAsync(startUtc, endUtc);

        if (summary == null)
        {
            return new CacheSnapshotResponse { HasData = false };
        }

        return new CacheSnapshotResponse
        {
            HasData = true,
            StartUsedSize = summary.StartUsedSize,
            EndUsedSize = summary.EndUsedSize,
            AverageUsedSize = summary.AverageUsedSize,
            TotalCacheSize = summary.TotalCacheSize,
            SnapshotCount = summary.SnapshotCount,
            IsEstimate = summary.IsEstimate
        };
    }

    // ───────────────────── Shared query helpers ─────────────────────

    private static IQueryable<Download> BuildBaseDownloadsQuery(AppDbContext context, List<string> hiddenClientIps, string evictedMode)
    {
        return context.Downloads.AsNoTracking()
            .ApplyHiddenClientFilter(hiddenClientIps)
            .ApplyEvictedFilter(evictedMode);
    }

    private static async Task<T> AggregateExcludingAsync<T>(
        IQueryable<Download> query,
        List<string> statsExcludedIps,
        Func<IQueryable<Download>, Task<T>> aggregator)
    {
        if (statsExcludedIps.Count == 0)
        {
            return await aggregator(query);
        }

        var filtered = query.Where(d => !statsExcludedIps.Contains(d.ClientIp));
        return await aggregator(filtered);
    }

    private sealed class BucketAggregate
    {
        public long CacheHitBytes { get; set; }
        public long CacheMissBytes { get; set; }
    }

    private static SparklineMetric BuildSparklineMetric(List<double> data)
    {
        var trimmed = data.ToList();
        while (trimmed.Count > 1 && trimmed.Last() == 0)
            trimmed.RemoveAt(trimmed.Count - 1);

        if (trimmed.Count < 2)
            return new SparklineMetric { Data = trimmed, Trend = "stable" };

        string trend = "stable";
        if (trimmed.Count >= 4)
        {
            int recentCount = Math.Min(3, trimmed.Count / 2);
            double recent = trimmed.TakeLast(recentCount).Average();
            double earlier = trimmed.Skip(Math.Max(0, trimmed.Count - recentCount * 2)).Take(recentCount).Average();
            double diff = (recent - earlier) / Math.Max(earlier, 0.001);
            trend = diff > 0.05 ? "up" : diff < -0.05 ? "down" : "stable";
        }
        else if (trimmed.Count >= 2)
        {
            double diff = (trimmed.Last() - trimmed.First()) / Math.Max(trimmed.First(), 0.001);
            trend = diff > 0.05 ? "up" : diff < -0.05 ? "down" : "stable";
        }

        return new SparklineMetric { Data = trimmed, Trend = trend };
    }

    private static SparklineMetric BuildSparklineMetricForRatio(List<double> data)
    {
        var trimmed = data.ToList();
        while (trimmed.Count > 1 && trimmed.Last() == 0)
            trimmed.RemoveAt(trimmed.Count - 1);

        if (trimmed.Count < 2)
            return new SparklineMetric { Data = trimmed, Trend = "stable" };

        string trend = "stable";
        if (trimmed.Count >= 4)
        {
            int recentCount = Math.Min(3, trimmed.Count / 2);
            double recent = trimmed.TakeLast(recentCount).Average();
            double earlier = trimmed.Skip(Math.Max(0, trimmed.Count - recentCount * 2)).Take(recentCount).Average();
            double diff = recent - earlier;
            trend = diff > 2 ? "up" : diff < -2 ? "down" : "stable";
        }
        else if (trimmed.Count >= 2)
        {
            double diff = trimmed.Last() - trimmed.First();
            trend = diff > 2 ? "up" : diff < -2 ? "down" : "stable";
        }

        return new SparklineMetric { Data = trimmed, Trend = trend };
    }

    // ───────────────────── Helpers ─────────────────────

    private async Task<object?> SafeExecuteAsync(string name, Func<Task<object>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "sub-query '{Name}' failed", name);
            return null;
        }
    }

    /// <summary>
    /// Overload for CacheInfo (value type differs from object)
    /// </summary>
    private async Task<CacheInfo?> SafeExecuteAsync(string name, Func<Task<CacheInfo>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "sub-query '{Name}' failed", name);
            return null;
        }
    }

    private async Task<HashSet<long>> GetEventDownloadIdsAsync(List<long> eventIdList)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var ids = await context.EventDownloads
            .AsNoTracking()
            .Where(ed => eventIdList.Contains(ed.EventId))
            .Select(ed => ed.DownloadId)
            .Distinct()
            .ToListAsync();
        return new HashSet<long>(ids);
    }

    /// <summary>
    /// Resolve game names for downloads using Steam depot mappings and Epic lookup.
    /// Mirrors the logic in DownloadsController.ResolveGameNamesAsync.
    /// </summary>
    private static async Task ResolveGameNamesAsync(AppDbContext context, List<Download> downloads)
    {
        if (downloads.Count == 0) return;

        // Build Steam depot mapping lookup for downloads with a DepotId
        var depotIds = downloads
            .Where(d => d.DepotId.HasValue)
            .Select(d => d.DepotId!.Value)
            .Distinct()
            .ToList();

        var steamMappings = depotIds.Count > 0
            ? await context.SteamDepotMappings
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
            ? await context.EpicGameMappings
                .AsNoTracking()
                .Where(m => epicAppIds.Contains(m.AppId))
                .ToDictionaryAsync(m => m.AppId, m => m.Name)
            : new Dictionary<string, string>();

        // Apply name resolution priority: existing GameName -> Steam AppName -> Epic Name -> fallback to Service
        foreach (var d in downloads)
        {
            if (string.IsNullOrEmpty(d.GameName) && d.DepotId.HasValue
                && steamMappings.TryGetValue(d.DepotId.Value, out var steamMapping))
            {
                d.GameName = steamMapping.AppName;
                d.GameAppId = steamMapping.AppId;
            }

            if (string.IsNullOrEmpty(d.GameName) && !string.IsNullOrEmpty(d.EpicAppId)
                && epicMappings.TryGetValue(d.EpicAppId, out var epicName))
            {
                d.GameName = epicName;
            }

            if (string.IsNullOrEmpty(d.GameName))
            {
                d.GameName = d.Service;
            }
        }
    }
}
