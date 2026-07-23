using LancacheManager.Configuration;
using LancacheManager.Core.Constants;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Utilities;
using System.Collections.Concurrent;
using System.Text.Json;
using LancacheManager.Models;
using LancacheManager.Models.Responses;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace LancacheManager.Core.Services;

/// <summary>
/// Shared compute logic for <c>GET /api/dashboard/batch</c>. Extracted from DashboardController
/// so a startup warmer (DashboardCacheWarmerService) can pre-populate the IMemoryCache before the
/// first user request arrives - otherwise the first request after a server restart would run
/// 9 parallel DB queries on a cold connection pool.
/// </summary>
public class DashboardBatchService : IDashboardBatchService
{
    // Shared with the scheduled warmer so the live entry stays useful between refreshes
    // without turning the heavy query fan-out into a 15-second background workload.
    internal static readonly TimeSpan LiveCacheWindow = TimeSpan.FromMinutes(5);

    private readonly CacheManagementService _cacheService;
    private readonly GameCacheDetectionService _gameCacheDetectionService;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IStateService _stateRepository;
    private readonly IOptions<ApiOptions> _apiOptions;
    private readonly ILogger<DashboardBatchService> _logger;
    private readonly CacheSnapshotService _cacheSnapshotService;
    private readonly IMemoryCache _memoryCache;
    private readonly JsonSerializerOptions _wireJsonOptions;

    // Every request captures both applicable generations before doing any work. Generations are
    // part of the key, and a response is cached only if its captured generations remain current.
    private long _liveCacheGeneration;
    private long _detectionCacheGeneration;

    // One flight per cache key so concurrent misses share a single fan-out. Stored as a Lazy
    // so GetOrAdd's value factory racing under contention never starts more than one recompute:
    // constructing a Lazy is inert, and only the ONE instance that actually gets stored into the
    // dictionary ever has its factory invoked. Whichever caller observes the flight complete
    // (success or failure) retires it via the atomic key+value TryRemove, so a newer flight for
    // the same key is never removed early and a cached failure is never replayed forever. [31]
    private readonly ConcurrentDictionary<string, Lazy<Task<DashboardBatchResponse>>> _inflight = new();

    public DashboardBatchService(
        CacheManagementService cacheService,
        GameCacheDetectionService gameCacheDetectionService,
        IDbContextFactory<AppDbContext> dbContextFactory,
        IServiceScopeFactory scopeFactory,
        IStateService stateRepository,
        IOptions<ApiOptions> apiOptions,
        ILogger<DashboardBatchService> logger,
        CacheSnapshotService cacheSnapshotService,
        IMemoryCache memoryCache,
        IOptions<Microsoft.AspNetCore.Mvc.JsonOptions> mvcJsonOptions)
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
        // The MVC wire options: pre-serialized sections must match what the output formatter
        // would have produced for the same object, byte for byte.
        _wireJsonOptions = mvcJsonOptions.Value.JsonSerializerOptions;
    }

    public async Task<DashboardBatchResponse> GetBatchAsync(
        long? startTime,
        long? endTime,
        long? eventId,
        CancellationToken ct)
    {
        var isLive = !startTime.HasValue && !endTime.HasValue;
        var liveCacheGeneration = isLive ? Volatile.Read(ref _liveCacheGeneration) : 0;
        var detectionCacheGeneration = Volatile.Read(ref _detectionCacheGeneration);

        // Shared state used by multiple sub-queries
        var hiddenClientIps = _stateRepository.GetHiddenClientIps();
        var statsExcludedOnlyIps = _stateRepository.GetStatsExcludedOnlyClientIps();
        var evictedMode = _stateRepository.GetEvictedDataMode();
        var eventIdList = eventId.HasValue ? new List<long> { eventId.Value } : new List<long>();

        var cacheKey = $"dashboard-batch:{startTime}:{endTime}:{eventId}:{evictedMode}:{liveCacheGeneration}:{detectionCacheGeneration}";

        // Concurrent misses for one key share a single fan-out via a Lazy-backed single-flight.
        // The Lazy is constructed before GetOrAdd (construction is inert - it never invokes
        // RunSingleFlightAsync), so GetOrAdd's plain-value overload deterministically stores
        // exactly one Lazy per key; ReferenceEquals against the caller's own Lazy then tells it
        // whether it created that stored flight or only joined one already in progress. Every
        // caller waits on its own token. A caller's own cancellation always rethrows immediately
        // without touching the entry, since the flight may still be legitimately in progress for
        // other callers. Anything else - a foreign cancellation or an ordinary fault - retires
        // the entry (a Lazy with ExecutionAndPublication caches a thrown exception forever
        // otherwise) and either rethrows, if this caller owns the failed flight, or loops back
        // to mint its own fresh attempt, bounding every caller to at most two awaited flights. [31]
        const int MaxContestedFlightAttempts = 2;
        var attempt = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (_memoryCache.TryGetValue(cacheKey, out DashboardBatchResponse? cachedResponse) && cachedResponse != null)
            {
                return await WithFreshCacheInfoAsync(cachedResponse, ct);
            }

            if (attempt >= MaxContestedFlightAttempts)
            {
                // Continued contention kept handing this caller someone else's flight that
                // then failed; stop contending for the shared slot and run this caller's own
                // attempt directly, unregistered, so it is guaranteed to terminate instead of
                // looping under pathological contention. [31]
                return await RunSingleFlightAsync(
                    cacheKey, startTime, endTime, eventIdList,
                    hiddenClientIps, statsExcludedOnlyIps, evictedMode,
                    isLive, liveCacheGeneration, detectionCacheGeneration, ct);
            }

            var myLazy = new Lazy<Task<DashboardBatchResponse>>(
                () => RunSingleFlightAsync(
                    cacheKey, startTime, endTime, eventIdList,
                    hiddenClientIps, statsExcludedOnlyIps, evictedMode,
                    isLive, liveCacheGeneration, detectionCacheGeneration, ct),
                LazyThreadSafetyMode.ExecutionAndPublication);
            var stored = _inflight.GetOrAdd(cacheKey, myLazy);
            var mine = ReferenceEquals(stored, myLazy);

            try
            {
                var result = await stored.Value.WaitAsync(ct);
                _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<DashboardBatchResponse>>>(cacheKey, stored));
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<DashboardBatchResponse>>>(cacheKey, stored));
                attempt++;
                if (mine)
                {
                    // This caller's own fresh flight failed; propagate directly instead of
                    // looping forever on a repeatable fault, matching the pre-single-flight
                    // behavior where each waiter's own compute attempt threw straight up. [31]
                    throw;
                }
                // A flight this caller only joined ended for a reason other than its own
                // token; loop back and contend again, up to the attempt cap above.
            }
        }
    }

    /// <summary>
    /// The actual cache-miss compute path for one single-flight: fans out every sub-query,
    /// assembles the response, and writes it to the memory cache when every section
    /// succeeded and the captured generations are still current. Runs entirely under the
    /// creator's own token - a follower joining this same task never influences it.
    /// </summary>
    private async Task<DashboardBatchResponse> RunSingleFlightAsync(
        string cacheKey,
        long? startTime, long? endTime,
        List<long> eventIdList,
        List<string> hiddenClientIps, List<string> statsExcludedOnlyIps, string evictedMode,
        bool isLive, long liveCacheGeneration, long detectionCacheGeneration,
        CancellationToken ct)
    {
        // Pre-fetch event download IDs once (shared by clients, services, dashboard, downloads)
        HashSet<long>? eventDownloadIds = eventIdList.Count > 0
            ? await GetEventDownloadIdsAsync(eventIdList, ct)
            : null;

        // Cache must complete first (cacheGrowth depends on its result)
        var cacheResult = await SafeExecuteAsync("cache", () => GetCacheInfoAsync(), ct);
        long actualCacheSize = cacheResult?.UsedCacheSize ?? 0;
        long totalCacheCapacity = cacheResult?.TotalCacheSize ?? 0;

        // Launch remaining 9 queries fully in parallel. AddPooledDbContextFactory bounds concurrency.
        var clientsTask = SafeExecuteAsync("clients", () => GetClientStatsAsync(startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode, statsExcludedOnlyIps, ct), ct);
        var servicesTask = SafeExecuteAsync("services", () => GetServiceStatsAsync(startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode, statsExcludedOnlyIps, ct), ct);
        var dashboardTask = SafeExecuteAsync("dashboard", () => GetDashboardStatsAsync(startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode, statsExcludedOnlyIps, ct), ct);
        var downloadsTask = SafeExecuteAsync("downloads", () => GetLatestDownloadsAsync(startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode, ct), ct);
        var detectionTask = SafeExecuteAsync("detection", () => GetCachedDetectionAsync(actualCacheSize), ct);
        var sparklinesTask = SafeExecuteAsync("sparklines", () => GetSparklineDataAsync(startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode, statsExcludedOnlyIps, ct), ct);
        var hourlyTask = SafeExecuteAsync("hourlyActivity", () => GetHourlyActivityAsync(startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode, statsExcludedOnlyIps, ct), ct);
        var cacheSnapshotTask = SafeExecuteAsync("cacheSnapshot", () => GetCacheSnapshotAsync(startTime, endTime, ct), ct);
        var cacheGrowthTask = SafeExecuteAsync("cacheGrowth", () => GetCacheGrowthAsync(
            startTime, endTime, actualCacheSize, totalCacheCapacity,
            eventIdList, eventDownloadIds, hiddenClientIps, evictedMode, statsExcludedOnlyIps, ct), ct);

        await Task.WhenAll(clientsTask, servicesTask, dashboardTask, downloadsTask, detectionTask, sparklinesTask, hourlyTask, cacheSnapshotTask, cacheGrowthTask);

        var detectionResult = await detectionTask;

        // Pre-serialize the downloads section once per cache window. It dominates the payload
        // (the whole visible downloads list in live mode), and a JsonElement re-emits as a raw
        // UTF-8 copy on every poll of every client instead of re-serializing tens of MB of
        // entities per request. The entity list itself is released here instead of living in
        // the cache entry.
        var downloadsResult = await downloadsTask;
        object? downloadsSection = downloadsResult == null
            ? null
            : JsonSerializer.SerializeToElement(downloadsResult, _wireJsonOptions);

        DashboardBatchResponse response = new()
        {
            Cache = cacheResult,
            Clients = await clientsTask,
            Services = await servicesTask,
            Dashboard = await dashboardTask,
            Downloads = downloadsSection,
            Detection = detectionResult,
            Sparklines = await sparklinesTask,
            HourlyActivity = await hourlyTask,
            CacheSnapshot = await cacheSnapshotTask,
            CacheGrowth = await cacheGrowthTask
        };

        // Non-live ranges (startTime/endTime fixed) cache for 60s; live uses the shared warm window.
        var cacheOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(isLive ? LiveCacheWindow : TimeSpan.FromSeconds(60))
            .SetSize(50_000)
            .SetPriority(CacheItemPriority.High);

        var generationsAreCurrent =
            detectionCacheGeneration == Volatile.Read(ref _detectionCacheGeneration)
            && (!isLive || liveCacheGeneration == Volatile.Read(ref _liveCacheGeneration));
        // A response with a failed (null) section would otherwise be served as-is for the
        // whole cache window; skipping the write makes the next request recompute. [5]
        if (generationsAreCurrent && !HasFailedSection(response))
        {
            _memoryCache.Set(cacheKey, response, cacheOptions);
        }

        return response;
    }

    /// <summary>
    /// Serves a cache hit. Cache file scan stats (totalFiles, cacheScanTimestampUtc) change
    /// independently of traffic aggregates, so mount + persisted scan are re-read on every
    /// hit - onto a copy, because the cached instance is shared by concurrent requests. [7]
    /// When the re-read fails, the copy keeps the cached section instead of reporting a
    /// failure for data the cache still holds; the entry expires within its window and the
    /// recompute path surfaces any persistent failure.
    /// </summary>
    private async Task<DashboardBatchResponse> WithFreshCacheInfoAsync(DashboardBatchResponse cached, CancellationToken ct)
    {
        var freshCache = await SafeExecuteAsync("cache", () => GetCacheInfoAsync(), ct);
        return new DashboardBatchResponse
        {
            Cache = freshCache ?? cached.Cache,
            Clients = cached.Clients,
            Services = cached.Services,
            Dashboard = cached.Dashboard,
            Downloads = cached.Downloads,
            Detection = cached.Detection,
            Sparklines = cached.Sparklines,
            HourlyActivity = cached.HourlyActivity,
            CacheSnapshot = cached.CacheSnapshot,
            CacheGrowth = cached.CacheGrowth
        };
    }

    /// <inheritdoc />
    public void InvalidateLiveCache()
    {
        Interlocked.Increment(ref _liveCacheGeneration);
    }

    /// <inheritdoc />
    public void InvalidateDetectionCache()
    {
        Interlocked.Increment(ref _detectionCacheGeneration);
    }

    // ───────────────────── Sub-query implementations ─────────────────────

    // Deliberately takes no CancellationToken: the underlying call reads mount metadata and
    // small persisted state files, not the database, and completes in milliseconds. [9]
    private async Task<CacheInfo> GetCacheInfoAsync()
    {
        return await _cacheService.GetCacheInfoAsync();
    }

    private async Task<object> GetClientStatsAsync(
        long? startTime, long? endTime,
        List<long> eventIdList, HashSet<long>? eventDownloadIds,
        List<string> hiddenClientIps, string evictedMode,
        List<string> statsExcludedOnlyIps, CancellationToken ct)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(ct);
        var maxLimit = _apiOptions.Value.MaxClientsPerRequest;
        var defaultLimit = _apiOptions.Value.DefaultClientsLimit;
        var effectiveLimit = Math.Min(defaultLimit, maxLimit);

        var query = context.Downloads.AsNoTracking();
        query = query.ApplyEventFilter(eventIdList, eventDownloadIds);
        query = query.ApplyHiddenClientFilter(hiddenClientIps);
        query = query.ApplyEvictedFilter(evictedMode);
        if (statsExcludedOnlyIps.Count > 0)
            query = query.Where(d => !statsExcludedOnlyIps.Contains(d.ClientIp));

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
            .ToListAsync(ct);

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
        List<string> statsExcludedOnlyIps, CancellationToken ct)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(ct);
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
            .ToListAsync(ct);

        // xboxlive and microsoft rows are folded into xbox before UTC marking
        return ServiceBreakdownMerger.MergeXboxRows(serviceStats).WithUtcMarking();
    }

    private async Task<object> GetDashboardStatsAsync(
        long? startTime, long? endTime,
        List<long> eventIdList, HashSet<long>? eventDownloadIds,
        List<string> hiddenClientIps, string evictedMode,
        List<string> statsExcludedOnlyIps, CancellationToken ct)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(ct);
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
                HitBytes = g.Sum(d => d.CacheHitBytes),
                MissBytes = g.Sum(d => d.CacheMissBytes),
                Count = g.Count()
            })
            .FirstOrDefaultAsync(ct);

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
            .CountAsync(d => d.StartTimeUtc >= activeThreshold && d.EndTimeUtc == default, ct);

        // Unique clients in period
        var uniqueClientsQuery = statsExcludedOnlyIps.Count > 0
            ? downloadsQuery.Where(d => !statsExcludedOnlyIps.Contains(d.ClientIp))
            : downloadsQuery;
        var uniqueClientsCount = await uniqueClientsQuery.Select(d => d.ClientIp).Distinct().CountAsync(ct);

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
                HitBytes = g.Sum(d => d.CacheHitBytes),
                MissBytes = g.Sum(d => d.CacheMissBytes)
            })
            .FirstOrDefaultAsync(ct);

        var totalHitBytes = allTimeAgg?.HitBytes ?? 0;
        var totalMissBytes = allTimeAgg?.MissBytes ?? 0;
        var totalServed = totalHitBytes + totalMissBytes;
        var cacheHitRatio = totalServed > 0 ? (totalHitBytes * 100.0) / totalServed : 0;

        // Service breakdown (also provides top service - no separate query needed)
        // xboxlive and microsoft rows are folded into xbox after materialisation
        var serviceBreakdown = ServiceBreakdownMerger.MergeXboxRows(await downloadsQuery
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
            .ToListAsync(ct));

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
        List<string> excludedClientIps, string evictedMode, CancellationToken ct)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(ct);
        List<Download> downloads;

        if (!startTime.HasValue && !endTime.HasValue && eventIdList.Count == 0)
        {
            // StatsDataService is registered Scoped - resolve via a scoped container so its
            // AppDbContext has a proper lifetime tied to this query.
            using var scope = _scopeFactory.CreateScope();
            var statsService = scope.ServiceProvider.GetRequiredService<IStatsDataService>();
            downloads = await statsService.GetLatestDownloadsAsync(int.MaxValue, cancellationToken: ct);
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

            query = query.ApplyEvictedFilter(evictedMode).ApplyEmptySessionFilter();
            downloads = await query
                .OrderByDescending(d => d.StartTimeUtc)
                .ToListAsync(ct);
        }

        // Filter out excluded and prefill client IPs in a single pass - in live mode this list
        // is the whole visible downloads table, so each extra ToList is a full-size copy.
        downloads = downloads
            .Where(d => excludedClientIps.Count == 0 || !excludedClientIps.Contains(d.ClientIp))
            .Where(d => !string.Equals(d.ClientIp, DownloadKindConstants.PrefillToken, StringComparison.OrdinalIgnoreCase))
            .Where(d => !string.Equals(d.Datasource, DownloadKindConstants.PrefillToken, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (evictedMode == EvictedDataMode.ShowClean.ToWireString())
        {
            foreach (var d in downloads) d.IsEvicted = false;
        }

        // Resolve game names via Steam depot mappings + Epic lookup
        await EnrichGameNamesAsync(context, downloads, ct);

        return downloads;
    }

    private async Task<object> GetCachedDetectionAsync(long usedCacheSizeBytes)
    {
        var cachedResults = await _gameCacheDetectionService.GetCachedDetectionAsync();

        if (cachedResults == null)
        {
            return CachedDetectionResponseBuilder.BuildEmpty();
        }

        var games = cachedResults.Games ?? [];

        // Live usage is already fetched for this batch; reuse it so the games-on-disk
        // staleness flag reflects the same snapshot the rest of the response was built from.
        var detectionStale = await _cacheService.IsDetectionSummaryStaleAsync(usedCacheSizeBytes);

        return CachedDetectionResponseBuilder.Build(
            games,
            cachedResults.Services,
            cachedResults.TotalServicesDetected,
            cachedResults.StartTime.AsUtc(),
            slimForDashboard: true,
            diskSummary: cachedResults.DiskSummary,
            summaryComputedAtUtc: cachedResults.SummaryComputedAtUtc,
            detectionStale: detectionStale);
    }

    // ───────────────────── New batch sub-queries (sparklines, hourly, cacheGrowth, cacheSnapshot) ─────────────────────

    private async Task<object> GetSparklineDataAsync(
        long? startTime, long? endTime,
        List<long> eventIdList, HashSet<long>? eventDownloadIds,
        List<string> hiddenClientIps, string evictedMode,
        List<string> statsExcludedOnlyIps, CancellationToken ct)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(ct);
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

        // Group in SQL - mirrors the HourlyActivity / CacheGrowth SQL-side GroupBy pattern.
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
                .ToListAsync(ct);
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
                .ToListAsync(ct);
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
                .ToListAsync(ct);
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
            CacheHitRatio = BuildRatioSparkline(cacheHitRatioData),
            TotalServed = BuildSparklineMetric(totalServedData),
            AddedToCache = BuildSparklineMetric(addedToCacheData),
            Period = startTime.HasValue ? "filtered" : "all"
        };
    }

    private async Task<object> GetHourlyActivityAsync(
        long? startTime, long? endTime,
        List<long> eventIdList, HashSet<long>? eventDownloadIds,
        List<string> hiddenClientIps, string evictedMode,
        List<string> statsExcludedOnlyIps, CancellationToken ct)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(ct);
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
                .ToListAsync(ct);

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
            .ToListAsync(ct);

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
        long? startTime, long? endTime,
        long actualCacheSize, long totalCacheCapacity,
        List<long> eventIdList, HashSet<long>? eventDownloadIds,
        List<string> hiddenClientIps, string evictedMode,
        List<string> statsExcludedOnlyIps, CancellationToken ct)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(ct);
        const string interval = "daily";
        DateTime? cutoffTime = startTime.HasValue
            ? startTime.Value.FromUnixSeconds()
            : (DateTime?)null;
        DateTime? endDateTime = endTime.HasValue
            ? endTime.Value.FromUnixSeconds()
            : (DateTime?)null;
        var intervalMinutes = TimeUtils.ParseInterval(interval);

        long currentCacheSize = 0;
        long totalCapacity = totalCacheCapacity;

        var allTimeQuery = BuildBaseDownloadsQuery(context, hiddenClientIps, evictedMode);
        // Awaiting the sum directly keeps a cancelled query surfacing as a cancellation;
        // reading Result through a continuation would rewrap it as an AggregateException. [8]
        var totalCacheMiss = await AggregateExcludingAsync(allTimeQuery, statsExcludedOnlyIps,
            async q => await q.SumAsync(d => (long?)d.CacheMissBytes, ct) ?? 0L);

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
                .ToListAsync(ct);
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
                .ToListAsync(ct);
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

            percentChange = PercentageUtils.CalculateBoundedChange(olderAvg, recentAvg);
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
            var cumulativeDownloads = totalCacheMiss;

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

    private async Task<object> GetCacheSnapshotAsync(long? startTime, long? endTime, CancellationToken ct)
    {
        if (!startTime.HasValue || !endTime.HasValue)
        {
            return new CacheSnapshotResponse { HasData = false };
        }

        var startUtc = startTime.Value.FromUnixSeconds();
        var endUtc = endTime.Value.FromUnixSeconds();

        var summary = await _cacheSnapshotService.GetSnapshotSummaryAsync(startUtc, endUtc, ct);

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

    private static SparklineMetric BuildRatioSparkline(List<double> data)
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

    /// <summary>
    /// True when any sub-query section of the batch response is null. The wire contract uses
    /// null for a failed sub-query and an empty collection / HasData=false for a successful
    /// empty result, so a null section means the response is incomplete.
    /// </summary>
    internal static bool HasFailedSection(DashboardBatchResponse response)
    {
        return response.Cache == null
            || response.Clients == null
            || response.Services == null
            || response.Dashboard == null
            || response.Downloads == null
            || response.Detection == null
            || response.Sparklines == null
            || response.HourlyActivity == null
            || response.CacheSnapshot == null
            || response.CacheGrowth == null;
    }

    /// <summary>
    /// Classifies an exception from a sub-query as a cancellation. Covers direct
    /// OperationCanceledException/TaskCanceledException, cancellations wrapped in an
    /// AggregateException (e.g. from task combinators), and any exception observed after
    /// the request token was cancelled.
    /// </summary>
    internal static bool IsCancellation(Exception ex, CancellationToken ct)
    {
        if (ex is OperationCanceledException)
        {
            return true;
        }

        if (ex is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
            {
                if (inner is OperationCanceledException)
                {
                    return true;
                }
            }
        }

        return ct.IsCancellationRequested;
    }

    private async Task<object?> SafeExecuteAsync(string name, Func<Task<object>> action, CancellationToken ct)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsCancellation(ex, ct))
        {
            // A cancelled request is not a failed sub-query; soft-nulling it here would let a
            // client abort masquerade as missing data. [8]
            _logger.LogInformation("sub-query '{Name}' cancelled", name);
            throw new OperationCanceledException($"sub-query '{name}' was cancelled", ex, ct);
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
    private async Task<CacheInfo?> SafeExecuteAsync(string name, Func<Task<CacheInfo>> action, CancellationToken ct)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsCancellation(ex, ct))
        {
            _logger.LogInformation("sub-query '{Name}' cancelled", name);
            throw new OperationCanceledException($"sub-query '{name}' was cancelled", ex, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "sub-query '{Name}' failed", name);
            return null;
        }
    }

    private async Task<HashSet<long>> GetEventDownloadIdsAsync(List<long> eventIdList, CancellationToken ct)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(ct);
        var ids = await context.EventDownloads
            .AsNoTracking()
            .Where(ed => eventIdList.Contains(ed.EventId))
            .Select(ed => ed.DownloadId)
            .Distinct()
            .ToListAsync(ct);
        return new HashSet<long>(ids);
    }

    /// <summary>
    /// Resolve game names for downloads using Steam depot mappings and Epic lookup.
    /// Mirrors the logic in DownloadsController.ResolveGameNamesAsync.
    /// </summary>
    private static async Task EnrichGameNamesAsync(AppDbContext context, List<Download> downloads, CancellationToken ct)
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
                .ToDictionaryAsync(m => m.DepotId, m => m, ct)
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
                .ToDictionaryAsync(m => m.AppId, m => m.Name, ct)
            : new Dictionary<string, string>();

        // Build Xbox game name lookup for Xbox downloads (named-style: GameName from the shared
        // XboxGameMapping catalog keyed by XboxProductId metadata).
        var xboxProductIds = downloads
            .Where(d => !string.IsNullOrEmpty(d.XboxProductId))
            .Select(d => d.XboxProductId!)
            .Distinct()
            .ToList();

        var xboxMappings = xboxProductIds.Count > 0
            ? await context.XboxGameMappings
                .AsNoTracking()
                .Where(m => xboxProductIds.Contains(m.ProductId))
                .ToDictionaryAsync(m => m.ProductId, m => m.Title, ct)
            : new Dictionary<string, string>();

        // Apply name resolution priority: existing GameName -> Steam AppName -> Epic Name -> Xbox Title -> fallback to Service
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

            if (string.IsNullOrEmpty(d.GameName) && !string.IsNullOrEmpty(d.XboxProductId)
                && xboxMappings.TryGetValue(d.XboxProductId, out var xboxTitle))
            {
                d.GameName = xboxTitle;
            }

            if (string.IsNullOrEmpty(d.GameName))
            {
                d.GameName = d.Service;
            }
        }
    }
}
