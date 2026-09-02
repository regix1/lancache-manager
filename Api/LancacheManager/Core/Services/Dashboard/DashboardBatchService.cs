using LancacheManager.Configuration;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Scheduling;
using LancacheManager.Infrastructure.Utilities;
using System.Collections.Concurrent;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace LancacheManager.Core.Services;

/// <summary>
/// Shared compute logic for <c>GET /api/dashboard/batch</c>. Extracted from DashboardController
/// so a startup warmer (DashboardCacheWarmerService) can pre-populate the IMemoryCache before the
/// first user request arrives - otherwise the first request after a server restart would run
/// 8 parallel DB queries on a cold connection pool.
/// </summary>
public partial class DashboardBatchService : IDashboardBatchService
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
    private readonly IClientHostnameService _clientHostnameService;
    private readonly IConfiguration _configuration;
    private readonly IOptions<MemoryCacheOptions> _memoryCacheOptions;
    private readonly JsonSerializerOptions _wireJsonOptions;

    // Every request captures both applicable generations before doing any work. Generations are
    // part of the key, and a response is cached only if its captured generations remain current.
    private long _liveCacheGeneration;
    private long _detectionCacheGeneration;

    // Bumping a generation only makes the old entry unreachable by key; the entry itself keeps its
    // pre-serialized downloads section for the rest of its expiry window while the next request
    // builds a replacement beside it. Every entry is linked to the eviction token of the generation
    // that produced it, so an invalidation releases that memory immediately.
    private CancellationTokenSource _liveCacheEviction = new();
    private CancellationTokenSource _detectionCacheEviction = new();

    // One flight per cache key so concurrent misses share a single fan-out. Stored as a Lazy
    // so GetOrAdd's value factory racing under contention never starts more than one recompute:
    // constructing a Lazy is inert, and only the ONE instance that actually gets stored into the
    // dictionary ever has its factory invoked. The caller that created the flight retires it on
    // every exit path - success, fault, and its own cancellation - via the atomic key+value
    // TryRemove, so a newer flight for the same key is never removed early, a cached failure is
    // never replayed forever, and an abandoned request never strands its entry here.
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
        IClientHostnameService clientHostnameService,
        IConfiguration configuration,
        IOptions<MemoryCacheOptions> memoryCacheOptions,
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
        _clientHostnameService = clientHostnameService;
        _configuration = configuration;
        _memoryCacheOptions = memoryCacheOptions;
        // The MVC wire options: pre-serialized sections must match what the output formatter
        // would have produced for the same object, byte for byte.
        _wireJsonOptions = mvcJsonOptions.Value.JsonSerializerOptions;
    }

    public async Task<DashboardBatchResponse> GetBatchAsync(
        long? startTime,
        long? endTime,
        long? eventId,
        string? timeZoneId,
        bool includeClientHostnames,
        CancellationToken ct,
        string? service = null,
        string? client = null)
    {
        // A reader that names no zone, or one this server cannot resolve, is grouped on the zone
        // the server reports as its own. That is still a zone name, so the database resolves each
        // row's own offset either way, and it is the id /api/system/config hands out.
        var readerTimeZoneId = KnownTimeZoneId(timeZoneId) ?? ServerTimeZone.IanaId(_configuration);

        var isLive = !startTime.HasValue && !endTime.HasValue;
        var liveCacheGeneration = isLive ? Volatile.Read(ref _liveCacheGeneration) : 0;
        var detectionCacheGeneration = Volatile.Read(ref _detectionCacheGeneration);
        // Read after the generations, so a token captured here is never older than the generation
        // it is paired with; the generationsAreCurrent check before the write covers the reverse.
        // The token STRUCT is what travels, never the source: a build can take seconds, an
        // invalidation lands on a notification thread meanwhile and disposes the source it
        // displaced, and CancellationTokenSource.Token throws once disposed. A token handed out
        // before that stays readable, reports itself already cancelled, and the entry is declined.
        var liveCacheEviction = Volatile.Read(ref _liveCacheEviction).Token;
        var detectionCacheEviction = Volatile.Read(ref _detectionCacheEviction).Token;

        // Shared state used by multiple sub-queries
        var hiddenClientIps = _stateRepository.GetHiddenClientIps();
        var statsExcludedOnlyIps = _stateRepository.GetStatsExcludedOnlyClientIps();
        var evictedMode = _stateRepository.GetEvictedDataMode();
        var eventIdList = eventId.HasValue ? new List<long> { eventId.Value } : new List<long>();

        // The zone is in the key because it changes the hourly buckets in the response body.
        // Whether client names may appear is part of the key, not a filter applied afterwards: one
        // cached body is handed to every reader, so a guest must never be served the entry an
        // account holder warmed.
        // The download filters are in the key for the same reason the zone is: they change the body,
        // so a reader that picked a client must not be handed the entry an unfiltered reader warmed.
        // The two client-visibility lists sit beside evictedMode for the same reason, and because
        // saving them only raises the live generation: a ranged entry keeps its key across that
        // bump and would keep serving the pre-change clients, services, dashboard, sparklines and
        // hourly sections for the rest of its window. Joined with a character no address carries so
        // the two lists cannot run together into one shared spelling.
        var cacheKey = $"dashboard-batch:{startTime}:{endTime}:{eventId}:{evictedMode}:{readerTimeZoneId}:{liveCacheGeneration}:{detectionCacheGeneration}:{includeClientHostnames}:{service}:{client}:{string.Join(",", hiddenClientIps)}|{string.Join(",", statsExcludedOnlyIps)}";

        // Concurrent misses for one key share a single fan-out via a Lazy-backed single-flight.
        // The Lazy is constructed before GetOrAdd (construction is inert - it never invokes
        // RunSingleFlightAsync), so GetOrAdd's plain-value overload deterministically stores
        // exactly one Lazy per key; ReferenceEquals against the caller's own Lazy then tells it
        // whether it created that stored flight or only joined one already in progress. Every
        // caller waits on its own token, and only the creator retires the entry, in a finally so
        // that its own cancellation cleans up too - the flight runs on the creator's token, so
        // there is nobody else left to do it. A joiner that sees the flight end for a reason other
        // than its own token either rethrows, if it owns the failed flight, or loops back to mint
        // a fresh attempt, bounding every caller to at most two awaited flights; a Lazy with
        // ExecutionAndPublication would otherwise replay a thrown exception forever.
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
                // looping under pathological contention.
                return await RunSingleFlightAsync(
                    cacheKey, startTime, endTime, eventIdList, readerTimeZoneId,
                    hiddenClientIps, statsExcludedOnlyIps, evictedMode,
                    isLive, liveCacheGeneration, detectionCacheGeneration,
                    liveCacheEviction, detectionCacheEviction, includeClientHostnames,
                    service, client, ct);
            }

            var myLazy = new Lazy<Task<DashboardBatchResponse>>(
                () => RunSingleFlightAsync(
                    cacheKey, startTime, endTime, eventIdList, readerTimeZoneId,
                    hiddenClientIps, statsExcludedOnlyIps, evictedMode,
                    isLive, liveCacheGeneration, detectionCacheGeneration,
                    liveCacheEviction, detectionCacheEviction, includeClientHostnames,
                    service, client, ct),
                LazyThreadSafetyMode.ExecutionAndPublication);
            var stored = _inflight.GetOrAdd(cacheKey, myLazy);
            var mine = ReferenceEquals(stored, myLazy);

            try
            {
                return await stored.Value.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                attempt++;
                if (mine)
                {
                    // This caller's own fresh flight failed; propagate directly instead of
                    // looping forever on a repeatable fault, matching the pre-single-flight
                    // behavior where each waiter's own compute attempt threw straight up.
                    throw;
                }
                // A flight this caller only joined ended for a reason other than its own
                // token; loop back and contend again, up to the attempt cap above.
            }
            finally
            {
                if (mine)
                {
                    _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<DashboardBatchResponse>>>(cacheKey, stored));
                }
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
        List<long> eventIdList, string readerTimeZoneId,
        List<string> hiddenClientIps, List<string> statsExcludedOnlyIps, string evictedMode,
        bool isLive, long liveCacheGeneration, long detectionCacheGeneration,
        CancellationToken liveCacheEviction, CancellationToken detectionCacheEviction,
        bool includeClientHostnames,
        string? service, string? client,
        CancellationToken ct)
    {
        // Pre-fetch event download IDs once (shared by clients, services, dashboard, downloads)
        HashSet<long>? eventDownloadIds = eventIdList.Count > 0
            ? await GetEventDownloadIdsAsync(eventIdList, ct)
            : null;

        // Cache must complete first because cached detection depends on its result.
        var cacheResult = await SafeExecuteAsync("cache", () => GetCacheInfoAsync(), ct);
        long actualCacheSize = cacheResult?.UsedCacheSize ?? 0;

        // Launch remaining queries fully in parallel. AddPooledDbContextFactory bounds concurrency.
        var clientsTask = SafeExecuteAsync("clients", () => GetClientStatsAsync(startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode, statsExcludedOnlyIps, includeClientHostnames, ct), ct);
        var servicesTask = SafeExecuteAsync("services", () => GetServiceStatsAsync(startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode, statsExcludedOnlyIps, ct), ct);
        var dashboardTask = SafeExecuteAsync("dashboard", () => GetDashboardStatsAsync(startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode, statsExcludedOnlyIps, ct), ct);
        var downloadTotalsTask = SafeExecuteAsync("downloadTotals", () => GetDownloadTotalsAsync(startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode, ct), ct);
        var filteredDownloadTotalsTask = SafeExecuteAsync("filteredDownloadTotals", () => GetFilteredDownloadTotalsAsync(startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode, service, client, ct), ct);
        var serviceOptionsTask = SafeExecuteAsync("serviceOptions", () => GetServiceOptionsAsync(startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode, ct), ct);
        var clientOptionsTask = SafeExecuteAsync("clientOptions", () => GetClientOptionsAsync(startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode, ct), ct);
        var recentDownloadsTask = SafeExecuteAsync("recentDownloads", () => GetRecentDownloadsAsync(startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode, service, client, ct), ct);
        var detectionTask = SafeExecuteAsync("detection", () => GetCachedDetectionAsync(actualCacheSize, ct), ct);
        var sparklinesTask = SafeExecuteAsync("sparklines", () => GetSparklineDataAsync(startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode, statsExcludedOnlyIps, ct), ct);
        var hourlyTask = SafeExecuteAsync("hourlyActivity", () => GetHourlyActivityAsync(startTime, endTime, eventIdList, eventDownloadIds, readerTimeZoneId, hiddenClientIps, evictedMode, statsExcludedOnlyIps, ct), ct);
        var cacheSnapshotTask = SafeExecuteAsync("cacheSnapshot", () => GetCacheSnapshotAsync(startTime, endTime, ct), ct);

        await Task.WhenAll(clientsTask, servicesTask, dashboardTask, downloadTotalsTask, filteredDownloadTotalsTask, serviceOptionsTask, clientOptionsTask, recentDownloadsTask, detectionTask, sparklinesTask, hourlyTask, cacheSnapshotTask);

        var detectionResult = await detectionTask;

        // Pre-serialize the recent slice once per cache window. It is the only section still
        // carrying rows, so it dominates the payload, and a JsonElement re-emits as a raw UTF-8
        // copy on every poll of every client instead of re-serializing the rows per request. The
        // entity list itself is released here instead of living in the cache entry.
        // Serializing to UTF-8 first and parsing that buffer, rather than SerializeToElement,
        // is what makes the entry's real byte length available for SetSize below.
        var recentDownloadsResult = await recentDownloadsTask;
        byte[] recentDownloadsUtf8 = recentDownloadsResult == null
            ? []
            : JsonSerializer.SerializeToUtf8Bytes(recentDownloadsResult, _wireJsonOptions);
        object? recentDownloadsSection = recentDownloadsResult == null
            ? null
            : JsonSerializer.Deserialize<JsonElement>(recentDownloadsUtf8);
        // The parsed element owns its own copy of the bytes, so only the length is carried forward.
        var recentDownloadsSectionBytes = recentDownloadsUtf8.Length;

        DashboardBatchResponse response = new()
        {
            Cache = cacheResult,
            Clients = await clientsTask,
            Services = await servicesTask,
            Dashboard = await dashboardTask,
            DownloadTotals = await downloadTotalsTask,
            FilteredDownloadTotals = await filteredDownloadTotalsTask,
            ServiceOptions = await serviceOptionsTask,
            ClientOptions = await clientOptionsTask,
            RecentDownloads = recentDownloadsSection,
            Detection = detectionResult,
            Sparklines = await sparklinesTask,
            HourlyActivity = await hourlyTask,
            CacheSnapshot = await cacheSnapshotTask
        };

        // Non-live ranges (startTime/endTime fixed) cache for 60s; live uses the shared warm window.
        // The size is the recent slice's real byte length plus what the remaining sections
        // used to be charged as the whole entry, so the global SizeLimit sees an entry's true
        // cost instead of a flat 50 KB. A moving time window mints a new key per request, and it
        // is that limit which now bounds how many of those can pile up; ranged entries are the
        // low-priority ones so compaction takes them before the live entry every reader shares.
        var entrySize = 50_000L + recentDownloadsSectionBytes;
        var cacheOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(isLive ? LiveCacheWindow : TimeSpan.FromSeconds(60))
            .SetSize(entrySize)
            .SetPriority(isLive ? CacheItemPriority.High : CacheItemPriority.Low)
            .AddExpirationToken(new CancellationChangeToken(detectionCacheEviction));

        // The live generation is part of the key only for live requests, so only they are linked
        // to its token; a ranged entry's live generation is a constant zero.
        if (isLive)
        {
            cacheOptions.AddExpirationToken(new CancellationChangeToken(liveCacheEviction));
        }

        var generationsAreCurrent =
            detectionCacheGeneration == Volatile.Read(ref _detectionCacheGeneration)
            && (!isLive || liveCacheGeneration == Volatile.Read(ref _liveCacheGeneration));
        // A response with a failed (null) section would otherwise be served as-is for the
        // whole cache window; skipping the write makes the next request recompute.
        if (generationsAreCurrent && !HasFailedSection(response))
        {
            _memoryCache.Set(cacheKey, response, cacheOptions);

            // An entry bigger than the whole SizeLimit is refused without an exception and without
            // evicting anything, so on a large enough downloads table the dashboard would stop
            // being cached at all and rebuild on every poll with nothing to explain it.
            if (_memoryCacheOptions.Value.SizeLimit is long sizeLimit && entrySize > sizeLimit)
            {
                _logger.LogWarning(
                    "dashboard batch response is {EntryBytes} bytes and the memory cache limit is {SizeLimitBytes} bytes, so it was not cached and every request will rebuild it",
                    entrySize,
                    sizeLimit);
            }
        }

        return response;
    }

    /// <summary>
    /// Serves a cache hit. Cache file scan stats (totalFiles, cacheScanTimestampUtc) change
    /// independently of traffic aggregates, so mount + persisted scan are re-read on every
    /// hit - onto a copy, because the cached instance is shared by concurrent requests.
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
            DownloadTotals = cached.DownloadTotals,
            FilteredDownloadTotals = cached.FilteredDownloadTotals,
            ServiceOptions = cached.ServiceOptions,
            ClientOptions = cached.ClientOptions,
            RecentDownloads = cached.RecentDownloads,
            Detection = cached.Detection,
            Sparklines = cached.Sparklines,
            HourlyActivity = cached.HourlyActivity,
            CacheSnapshot = cached.CacheSnapshot
        };
    }

    /// <inheritdoc />
    public void InvalidateLiveCache()
    {
        // Exchange rather than a plain assignment: these run on SignalR notification threads, so
        // two invalidations can overlap and each must cancel exactly the source it took out.
        // The evicted source is cancelled but never disposed: CancellationTokenSource.Token
        // throws ObjectDisposedException once the source is disposed, and a request reads it
        // from the field it captured, so disposing here would fail any request that an
        // invalidation overlaps. A cancelled source holds no timer and is collectable as-is.
        var evicted = Interlocked.Exchange(ref _liveCacheEviction, new CancellationTokenSource());
        Interlocked.Increment(ref _liveCacheGeneration);
        evicted.Cancel();
    }

    /// <inheritdoc />
    public void InvalidateDetectionCache()
    {
        var evicted = Interlocked.Exchange(ref _detectionCacheEviction, new CancellationTokenSource());
        Interlocked.Increment(ref _detectionCacheGeneration);
        evicted.Cancel();
    }

    /// <inheritdoc />
    public void InvalidateAllCache()
    {
        var liveEvicted = Interlocked.Exchange(ref _liveCacheEviction, new CancellationTokenSource());
        var detectionEvicted = Interlocked.Exchange(ref _detectionCacheEviction, new CancellationTokenSource());
        Interlocked.Increment(ref _liveCacheGeneration);
        Interlocked.Increment(ref _detectionCacheGeneration);
        liveEvicted.Cancel();
        detectionEvicted.Cancel();
    }

    // ───────────────────── Sub-query implementations ─────────────────────

    // Deliberately takes no CancellationToken: the underlying call reads mount metadata and
    // small persisted state files, not the database, and completes in milliseconds.
    private async Task<CacheInfo> GetCacheInfoAsync()
    {
        return await _cacheService.GetCacheInfoAsync();
    }

    private async Task<object> GetClientStatsAsync(
        long? startTime, long? endTime,
        List<long> eventIdList, HashSet<long>? eventDownloadIds,
        List<string> hiddenClientIps, string evictedMode,
        List<string> statsExcludedOnlyIps, bool includeClientHostnames, CancellationToken ct)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(ct);
        var maxLimit = _apiOptions.Value.MaxClientsPerRequest;
        var defaultLimit = _apiOptions.Value.DefaultClientsLimit;
        var effectiveLimit = Math.Min(defaultLimit, maxLimit);

        var query = context.Downloads.AsNoTracking();
        query = query.ApplyEventFilter(eventIdList, eventDownloadIds);
        query = query.ApplyHiddenClientFilter(hiddenClientIps);
        query = query.ApplyEvictedFilter(evictedMode);
        query = query.ApplyStatsExcludedClientFilter(statsExcludedOnlyIps);
        query = query.ApplyTimeRange(startTime, endTime);

        // The GROUP BY and its client-side duration fold are shared with GET /api/stats/clients, so
        // the two surfaces cannot drift at the step above the shared ranking helper.
        var ipAggregates = await ClientStatsAggregationHelper.QueryIpAggregatesAsync(query, ct);

        // IClientGroupsService is Scoped (its AppDbContext is) and this service is a singleton,
        // so the nickname mapping is read through a per-call scope, like the live downloads path.
        Dictionary<string, ClientGroupAssignment> ipToGroupMapping;
        using (var scope = _scopeFactory.CreateScope())
        {
            var clientGroupsService = scope.ServiceProvider.GetRequiredService<IClientGroupsService>();
            ipToGroupMapping = await clientGroupsService.GetIpMappingAsync(ct);
        }

        // Empty unless the hostname lookup is on and this reader may be shown names, in which case
        // a row with no nickname is labelled with the machine's own name instead of its address.
        // Only the addresses that will be displayed are resolved, so the busiest clients are the
        // ones that get names. A reader who may not see them costs no query at all: the label is
        // decided here, not stripped from the response afterwards, because a name and a nickname
        // arrive in the same field and cannot be told apart once they are.
        var ipToHostname = includeClientHostnames
            ? (await _clientHostnameService.ResolveAsync(
                ClientStatsAggregationHelper.TopClientIpsByTraffic(ipAggregates, effectiveLimit), ct)).Hostnames
            : new Dictionary<string, string>();

        // Same shared fold as GET /api/stats/clients: groups are summed before the top-N cut and
        // the rows carry the nickname, so both surfaces rank and label clients identically.
        return ClientStatsAggregationHelper.AggregateAndRank(
            ipAggregates, ipToGroupMapping, ipToHostname, effectiveLimit);
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
        query = query.ApplyTimeRange(startTime, endTime);

        var serviceStatsQuery = query.ApplyStatsExcludedClientFilter(statsExcludedOnlyIps);

        var serviceStats = await ServiceStatsQuery(serviceStatsQuery).ToListAsync(ct);

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

        downloadsQuery = downloadsQuery.ApplyTimeRange(startTime, endTime);

        // Calculate period metrics (exclude stats-excluded IPs)
        var periodQuery = downloadsQuery.ApplyStatsExcludedClientFilter(statsExcludedOnlyIps);

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

        // Active downloads: the flag the cleanup service maintains, gated on the end still being
        // recent. Same predicate as GET /api/dashboard/stats, which fills the same response field.
        // The old check asked for an unset EndTimeUtc, which the ingestion never leaves behind: it
        // stamps an end on insert and advances it on every update, so this count was always zero.
        var activeThreshold = DateTime.UtcNow.AddMinutes(-5);
        var activeQuery = context.Downloads.AsNoTracking()
            .ApplyHiddenClientFilter(hiddenClientIps)
            .ApplyEvictedFilter(evictedMode)
            .Where(d => d.IsActive && d.EndTimeUtc > activeThreshold)
            .ApplyStatsExcludedClientFilter(statsExcludedOnlyIps);
        var activeDownloads = await activeQuery.CountAsync(ct);

        // Unique clients in period
        var uniqueClientsQuery = downloadsQuery.ApplyStatsExcludedClientFilter(statsExcludedOnlyIps);
        var uniqueClientsCount = await uniqueClientsQuery.Select(d => d.ClientIp).Distinct().CountAsync(ct);

        // Combined all-time aggregates: hitBytes + missBytes in ONE query (was 2 round trips)
        var allTimeQuery = context.Downloads.AsNoTracking()
            .ApplyHiddenClientFilter(hiddenClientIps)
            .ApplyEvictedFilter(evictedMode)
            .ApplyStatsExcludedClientFilter(statsExcludedOnlyIps);

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
        var serviceBreakdown = await ServiceBreakdownMerger.QueryMergedAsync(downloadsQuery, periodTotal, ct);

        var topServiceName = serviceBreakdown.FirstOrDefault()?.Service ?? "N/A";

        var periodLabel = DashboardPeriod.Label(cutoffTime, endDateTime);

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

    /// <summary>
    /// How many game groups the recent section carries. The rows are grouped before they are
    /// capped, so one game producing hundreds of downloads can no longer crowd every other game
    /// out of the section.
    /// </summary>
    private const int RecentGameGroupLimit = 100;

    /// <summary>
    /// One page of the depot-and-client aggregate the scan reads newest-first. The number of
    /// depot groups standing behind a hundred games is not a constant: a row with no depot forms
    /// a group on its own, and one game downloaded by many clients forms a group per client, so
    /// the scan pages and folds until it has the games rather than reading a fixed window.
    /// </summary>
    private const int RecentScanPageSize = 500;

    /// <summary>
    /// The scan stops here whatever it has folded so far. This stops a pathological range from
    /// scanning the whole table; reaching it means the panel shows fewer than
    /// <see cref="RecentGameGroupLimit"/> games, never wrong ones.
    /// </summary>
    private const int RecentScanCeiling = 20_000;

    /// <summary>
    /// How long a finished row keeps riding the raw array beside the active ones. A live preview
    /// retires when the row behind it comes back finished, so a row that simply vanished from the
    /// feed on the poll after it completed would leave its preview on screen until the client's
    /// own sticky timeout. This tail is twice that timeout, so a late poll still sees the row.
    /// </summary>
    private static readonly TimeSpan _recentFinishedTail = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The downloads the dashboard's own surfaces are allowed to see. Composed from
    /// <see cref="DownloadQueryExtensions"/> so the totals, the filter options and the recent
    /// slice can never disagree about which rows exist. Rows tagged to app 0 are dropped on the
    /// live view only, the way the download list itself has always treated them. The evicted
    /// filter runs on both arms, matching the client and service cards drawn beside these figures,
    /// which have always applied it whatever the range.
    /// </summary>
    private static IQueryable<Download> BuildVisibleDownloadsQuery(
        AppDbContext context,
        long? startTime, long? endTime,
        List<long> eventIdList, HashSet<long>? eventDownloadIds,
        List<string> hiddenClientIps, string evictedMode)
    {
        var query = context.Downloads.AsNoTracking()
            .ApplyHiddenClientFilter(hiddenClientIps)
            .ApplyEmptySessionFilter()
            .ApplyEventFilter(eventIdList, eventDownloadIds)
            .ApplyTimeRange(startTime, endTime);

        var isLive = !startTime.HasValue && !endTime.HasValue && eventIdList.Count == 0;
        if (isLive)
        {
            query = query.Where(d => !d.GameAppId.HasValue || d.GameAppId.Value != 0);
        }

        return query.ApplyEvictedFilter(evictedMode);
    }

    /// <summary>
    /// Narrows a downloads query to the service and the clients the reader picked. The service
    /// arrives as the folded key the dropdown shows, so an xbox selection is expanded back into
    /// every raw value behind it - LINQ cannot translate the fold itself. The client value is a
    /// comma-separated list because a dropdown entry can stand for a client group, which is several
    /// addresses; a single address is that list with one member.
    /// </summary>
    private static IQueryable<Download> ApplyDownloadFilters(IQueryable<Download> query, string? service, string? client)
    {
        if (!string.IsNullOrEmpty(service) && service != "all")
        {
            if (ServiceBreakdownMerger.NormalizeXboxService(service) == "xbox")
            {
                var xboxServiceNames = ServiceBreakdownMerger.XboxRawServiceNames;
                query = query.Where(d => xboxServiceNames.Contains(d.Service));
            }
            else
            {
                query = query.Where(d => d.Service == service);
            }
        }

        if (!string.IsNullOrEmpty(client) && client != "all")
        {
            var clientIps = client.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            query = query.Where(d => clientIps.Contains(d.ClientIp));
        }

        return query;
    }

    private async Task<object> GetDownloadTotalsAsync(
        long? startTime, long? endTime,
        List<long> eventIdList, HashSet<long>? eventDownloadIds,
        List<string> hiddenClientIps, string evictedMode, CancellationToken ct)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(ct);
        return await QueryDownloadTotalsAsync(
            BuildVisibleDownloadsQuery(context, startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode),
            ct);
    }

    private async Task<object> GetFilteredDownloadTotalsAsync(
        long? startTime, long? endTime,
        List<long> eventIdList, HashSet<long>? eventDownloadIds,
        List<string> hiddenClientIps, string evictedMode,
        string? service, string? client, CancellationToken ct)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(ct);
        return await QueryDownloadTotalsAsync(
            ApplyDownloadFilters(
                BuildVisibleDownloadsQuery(context, startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode),
                service, client),
            ct);
    }

    /// <summary>
    /// Bytes and rows in ONE round trip, the same grouping the period totals use. TotalBytes is
    /// computed off the entity with no column behind it, so the sum names the two stored columns.
    /// Shared by the filtered and unfiltered passes so the two can only ever differ by the filters.
    /// </summary>
    private static async Task<DownloadTotals> QueryDownloadTotalsAsync(IQueryable<Download> query, CancellationToken ct)
    {
        var totals = await query
            .GroupBy(d => 1)
            .Select(g => new
            {
                HitBytes = g.Sum(d => d.CacheHitBytes),
                MissBytes = g.Sum(d => d.CacheMissBytes),
                Count = g.Count()
            })
            .FirstOrDefaultAsync(ct);

        return new DownloadTotals
        {
            CacheHitBytes = totals?.HitBytes ?? 0,
            CacheMissBytes = totals?.MissBytes ?? 0,
            Count = totals?.Count ?? 0
        };
    }

    private async Task<object> GetServiceOptionsAsync(
        long? startTime, long? endTime,
        List<long> eventIdList, HashSet<long>? eventDownloadIds,
        List<string> hiddenClientIps, string evictedMode, CancellationToken ct)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(ct);
        var query = BuildVisibleDownloadsQuery(context, startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode);

        // Unlike the service breakdown this keeps localhost and ip-address. Neither names a service
        // that files on disk can be attributed to, but both are still values a reader filters by,
        // and dropping them here would take two entries out of the dropdown.
        return await query
            .GroupBy(d => d.Service)
            .Select(g => new ServiceFilterOption
            {
                Service = g.Key,
                HasLargeFiles = g.Max(d => d.CacheHitBytes + d.CacheMissBytes) > 1024 * 1024
            })
            .ToListAsync(ct);
    }

    private async Task<object> GetClientOptionsAsync(
        long? startTime, long? endTime,
        List<long> eventIdList, HashSet<long>? eventDownloadIds,
        List<string> hiddenClientIps, string evictedMode, CancellationToken ct)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(ct);
        var query = BuildVisibleDownloadsQuery(context, startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode);

        return await query.Select(d => d.ClientIp).Distinct().ToListAsync(ct);
    }

    /// <summary>
    /// The recent-activity section: the newest games, grouped here rather than in the browser, and
    /// beside them the raw rows the live previews reconcile against. Every active row is carried
    /// whatever its age: a prefill that has been running for hours is old and still running, and
    /// it is that row's advancing byte count which retires the preview sitting beside it, so an
    /// age-bounded array would leave that preview on screen for good.
    /// </summary>
    private async Task<object> GetRecentDownloadsAsync(
        long? startTime, long? endTime,
        List<long> eventIdList, HashSet<long>? eventDownloadIds,
        List<string> hiddenClientIps, string evictedMode,
        string? service, string? client, CancellationToken ct)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(ct);
        var query = ApplyDownloadFilters(
            BuildVisibleDownloadsQuery(context, startTime, endTime, eventIdList, eventDownloadIds, hiddenClientIps, evictedMode),
            service, client);

        var showClean = evictedMode == EvictedDataMode.ShowClean.ToWireString();

        var groupsByKey = new Dictionary<string, DashboardGameGroup>(StringComparer.Ordinal);
        // Held as a set because an active row folded below can name a depot pair the scan already
        // recorded for the same game, and reading one pair twice would list its members twice.
        var pairsByKey = new Dictionary<string, HashSet<(long DepotId, string ClientIp)>>(StringComparer.Ordinal);
        var groupedQuery = BuildRecentGroupedQuery(query);

        var scanned = 0;
        while (groupsByKey.Count < RecentGameGroupLimit && scanned < RecentScanCeiling)
        {
            var page = await groupedQuery.Skip(scanned).Take(RecentScanPageSize).ToListAsync(ct);
            if (page.Count == 0) break;
            scanned += page.Count;

            // Names are resolved a page at a time because the fold key depends on them: a name is
            // NULL on the row until the depot, Epic and Xbox tables fill it, so the scan cannot
            // know how many games it has until the page it just read has been folded.
            await GameNameResolver.ResolveAsync(context, page, ct);
            foreach (var row in page)
            {
                FoldIntoGameGroups(groupsByKey, pairsByKey, row, showClean);
            }

            if (page.Count < RecentScanPageSize) break;
        }

        // A game's rank by latest member start is the rank of its newest depot group, so folding a
        // newest-first scan already yields the games in this order.
        var groups = groupsByKey.Values
            .OrderByDescending(g => g.LastSeen)
            .Take(RecentGameGroupLimit)
            .ToList();

        var finishedCutoff = DateTime.UtcNow - _recentFinishedTail;
        var rows = await query
            .Where(d => d.IsActive || d.EndTimeUtc >= finishedCutoff)
            .OrderByDescending(d => d.StartTimeUtc)
            .ToListAsync(ct);

        if (showClean)
        {
            foreach (var d in rows) d.IsEvicted = false;
        }

        // Resolve game names via Steam depot mappings + Epic lookup
        await GameNameResolver.ResolveAsync(context, rows, ct);

        // An active game whose newest download is older than the newest hundred still belongs on
        // the panel, so its group is folded from the active rows and appended. Its totals cover
        // its active members only, and it sorts after the hundred because it is older than they
        // are. Doing it here keeps one owner for the fold rules instead of a second copy in the
        // browser.
        var carriedKeys = new HashSet<string>(groups.Select(g => g.Id), StringComparer.Ordinal);
        var activeGroups = new Dictionary<string, DashboardGameGroup>(StringComparer.Ordinal);
        foreach (var d in rows.Where(d => d.IsActive))
        {
            FoldIntoGameGroups(activeGroups, pairsByKey, new DashboardGroupRow
            {
                // Shaped exactly like an aggregate row of one member, so the fold cannot tell the
                // two sources apart: a depot-backed row names its pair and a no-depot one names
                // its own id.
                DepotId = d.DepotId,
                RowKey = d.DepotId == null ? d.Id : 0L,
                ClientIp = d.ClientIp,
                Service = d.Service,
                GameName = d.GameName,
                GameAppId = d.GameAppId,
                CacheHitBytes = d.CacheHitBytes,
                CacheMissBytes = d.CacheMissBytes,
                LastStartTimeUtc = d.StartTimeUtc,
                RequestCount = 1,
                EvictedCount = d.IsEvicted ? 1 : 0
            }, showClean);
        }
        groups.AddRange(activeGroups.Values.Where(g => !carriedKeys.Contains(g.Id)));

        // The derived fields are stored rather than computed on read, because the browser's type
        // is checked against the properties this class declares and a computed one is not one.
        foreach (var group in groups)
        {
            group.TotalBytes = group.CacheHitBytes + group.CacheMissBytes;
            // A group of zero-byte rows is the metadata traffic the views draw differently.
            group.Type = group.TotalBytes > 0 ? "content" : "metadata";
            group.IsEvicted = group.EvictedCount == group.Count;
            group.IsPartiallyEvicted = group.EvictedCount > 0 && group.EvictedCount < group.Count;
        }

        await FillGroupDownloadIdsAsync(query, groups, pairsByKey, ct);

        // Projected onto the narrower row here rather than serialized as entities: LastUrl and
        // XboxProductId sit on every row and no client reads either.
        var wireRows = rows.Select(d => new DashboardDownloadRow
        {
            Id = d.Id,
            Service = d.Service,
            ClientIp = d.ClientIp,
            StartTimeUtc = d.StartTimeUtc,
            EndTimeUtc = d.EndTimeUtc,
            CacheHitBytes = d.CacheHitBytes,
            CacheMissBytes = d.CacheMissBytes,
            IsActive = d.IsActive,
            IsEvicted = d.IsEvicted,
            GameAppId = d.GameAppId,
            GameName = d.GameName,
            GameImageUrl = d.GameImageUrl,
            DepotId = d.DepotId,
            EpicAppId = d.EpicAppId,
            Datasource = d.Datasource,
            DurationSeconds = d.DurationSeconds
        }).ToList();

        return new { groups, rows = wireRows };
    }

    /// <summary>
    /// Aggregates the visible downloads per (DepotId, ClientIp) so only group-level scalars cross
    /// the database boundary, the same shape the retro page groups on. No-depot rows keep one
    /// group per row via RowKey = Id. The order closes on the group key so paging the scan cannot
    /// see one aggregate row twice or skip one.
    /// </summary>
    private static IQueryable<DashboardGroupRow> BuildRecentGroupedQuery(IQueryable<Download> query)
    {
        return query
            .GroupBy(d => new
            {
                d.DepotId,
                d.ClientIp,
                RowKey = d.DepotId == null ? d.Id : 0L
            })
            .Select(g => new DashboardGroupRow
            {
                DepotId = g.Key.DepotId,
                ClientIp = g.Key.ClientIp,
                RowKey = g.Key.RowKey,
                Service = g.Max(d => d.Service)!,
                GameName = g.Max(d => d.GameName),
                GameAppId = g.Max(d => d.GameAppId),
                EpicAppId = g.Max(d => d.EpicAppId),
                XboxProductId = g.Max(d => d.XboxProductId),
                CacheHitBytes = g.Sum(d => d.CacheHitBytes),
                CacheMissBytes = g.Sum(d => d.CacheMissBytes),
                LastStartTimeUtc = g.Max(d => d.StartTimeUtc),
                RequestCount = g.Count(),
                EvictedCount = g.Sum(d => d.IsEvicted ? 1 : 0)
            })
            .OrderByDescending(r => r.LastStartTimeUtc)
            .ThenBy(r => r.ClientIp)
            .ThenBy(r => r.DepotId)
            .ThenBy(r => r.RowKey);
    }

    /// <summary>
    /// Folds one depot-and-client aggregate into the game it belongs to, minting the group the
    /// first time that game is seen. A row whose resolved name does not name a game lands in its
    /// service's bucket instead, which is the rule the panel applied while it did this grouping
    /// itself. ShowClean drops the evicted tally so the group's flags stay clear, matching what
    /// the raw rows get in that mode.
    /// </summary>
    private static void FoldIntoGameGroups(
        Dictionary<string, DashboardGameGroup> groups,
        Dictionary<string, HashSet<(long DepotId, string ClientIp)>> pairsByKey,
        DashboardGroupRow row,
        bool showClean)
    {
        var foldedService = ServiceBreakdownMerger.NormalizeXboxService(row.Service);
        var isGame = !string.IsNullOrWhiteSpace(row.GameName)
            && DownloadsController.IsRealGameName(row.GameName!, row.Service);

        string key;
        string name;
        if (isGame)
        {
            // One game seen under two services stays one row, so the app id keys it whenever there
            // is one and the title keys it otherwise.
            key = row.GameAppId is > 0 ? $"game-appid-{row.GameAppId}" : $"game-{row.GameName}";
            name = row.GameName!;
        }
        else
        {
            // The folded service itself, not a title. The bucket's label is written in the
            // reader's language from the locale files, so a title rendered here in English would
            // reach a reader who does not read English and no gate would notice.
            key = $"service-{foldedService.ToLowerInvariant()}";
            name = foldedService;
        }

        if (!groups.TryGetValue(key, out var group))
        {
            group = new DashboardGameGroup
            {
                Id = key,
                Name = name,
                Service = foldedService,
                HasRealGameName = isGame,
                GameAppId = isGame ? row.GameAppId : null,
                GameName = isGame ? row.GameName : null
            };
            groups[key] = group;
        }

        group.CacheHitBytes += row.CacheHitBytes;
        group.CacheMissBytes += row.CacheMissBytes;
        group.Count += row.RequestCount;
        if (!showClean)
        {
            group.EvictedCount += row.EvictedCount;
        }
        if (row.LastStartTimeUtc > group.LastSeen)
        {
            group.LastSeen = row.LastStartTimeUtc;
        }
        group.ClientIps.Add(row.ClientIp);

        if (row.DepotId is long depotId)
        {
            if (!pairsByKey.TryGetValue(key, out var pairs))
            {
                pairs = [];
                pairsByKey[key] = pairs;
            }
            pairs.Add((depotId, row.ClientIp));
        }
        else
        {
            group.DownloadIds.Add(row.RowKey);
        }
    }

    /// <summary>
    /// Member ids for the groups that survived the cap, read in a second query the way the retro
    /// page fills its own. They are left off the aggregate deliberately: reading them there costs
    /// one row per member over every group the scan touched rather than only the ones being sent.
    /// </summary>
    private static async Task FillGroupDownloadIdsAsync(
        IQueryable<Download> query,
        List<DashboardGameGroup> groups,
        Dictionary<string, HashSet<(long DepotId, string ClientIp)>> pairsByKey,
        CancellationToken ct)
    {
        var neededPairs = new HashSet<(long DepotId, string ClientIp)>();
        foreach (var group in groups)
        {
            if (pairsByKey.TryGetValue(group.Id, out var pairs))
            {
                foreach (var pair in pairs) neededPairs.Add(pair);
            }
        }
        if (neededPairs.Count == 0) return;

        // Over-fetched by depot list and ip list, with the pair-exact filtering done in memory
        // below; both lists are bounded by the groups being sent, so this stays one indexed query.
        var depotIdList = neededPairs.Select(p => p.DepotId).Distinct().ToList();
        var clientIpList = neededPairs.Select(p => p.ClientIp).Distinct().ToList();

        var memberRows = await query
            .Where(d => d.DepotId != null
                     && depotIdList.Contains(d.DepotId.Value)
                     && clientIpList.Contains(d.ClientIp))
            .Select(d => new { d.Id, DepotId = d.DepotId!.Value, d.ClientIp })
            .ToListAsync(ct);

        var idsByPair = new Dictionary<(long DepotId, string ClientIp), List<long>>();
        foreach (var row in memberRows)
        {
            var pair = (row.DepotId, row.ClientIp);
            if (!neededPairs.Contains(pair)) continue;
            if (!idsByPair.TryGetValue(pair, out var ids))
            {
                ids = [];
                idsByPair[pair] = ids;
            }
            ids.Add(row.Id);
        }

        foreach (var group in groups)
        {
            if (!pairsByKey.TryGetValue(group.Id, out var pairs)) continue;
            foreach (var pair in pairs)
            {
                if (idsByPair.TryGetValue(pair, out var ids))
                {
                    group.DownloadIds.AddRange(ids);
                }
            }
        }
    }

    /// <summary>
    /// One depot-and-client aggregate of the recent scan. It carries the mapping columns as well
    /// as the sums because <see cref="GameNameResolver"/> resolves names straight onto it, so one
    /// representative object per depot group answers both the fold's key and its totals.
    /// </summary>
    internal sealed class DashboardGroupRow : IGameNameRow
    {
        public long? DepotId { get; init; }
        public string ClientIp { get; init; } = string.Empty;
        public long RowKey { get; init; }
        public string Service { get; init; } = string.Empty;
        public string? GameName { get; set; }
        public long? GameAppId { get; set; }
        public string? EpicAppId { get; init; }
        public string? XboxProductId { get; init; }
        public long CacheHitBytes { get; init; }
        public long CacheMissBytes { get; init; }
        public DateTime LastStartTimeUtc { get; init; }
        public int RequestCount { get; init; }
        public int EvictedCount { get; init; }
    }

    /// <summary>
    /// One game of the batch response's recent section, carrying what the panel's row draws and
    /// nothing it computes from members: the eviction flags, the distinct client addresses the
    /// dropdown narrows on, and the member ids the event badges are fetched with.
    /// </summary>
    internal sealed class DashboardGameGroup
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Service { get; init; } = string.Empty;
        public long TotalBytes { get; set; }
        public long CacheHitBytes { get; set; }
        public long CacheMissBytes { get; set; }
        public int Count { get; set; }
        public DateTime LastSeen { get; set; }
        public bool IsEvicted { get; set; }
        public bool IsPartiallyEvicted { get; set; }
        public bool HasRealGameName { get; init; }
        public long? GameAppId { get; init; }
        public string? GameName { get; init; }
        public HashSet<string> ClientIps { get; } = new(StringComparer.Ordinal);
        public List<long> DownloadIds { get; } = [];

        /// <summary>
        /// Members that are evicted, counted while the group is folded. Internal rather than
        /// public so it stays off the wire: the two flags above are what the panel reads, and the
        /// browser's type declares every public member this class has.
        /// </summary>
        internal int EvictedCount { get; set; }
    }

    /// <summary>
    /// One row of the batch response's recent slice: <see cref="Download"/> minus the columns
    /// nothing on the wire reads. The computed members mirror the entity's so the JSON the
    /// frontend receives is unchanged apart from the two dropped fields.
    /// </summary>
    internal sealed class DashboardDownloadRow
    {
        public long Id { get; init; }
        public string Service { get; init; } = string.Empty;
        public string ClientIp { get; init; } = string.Empty;
        public DateTime StartTimeUtc { get; init; }
        public DateTime EndTimeUtc { get; init; }
        public long CacheHitBytes { get; init; }
        public long CacheMissBytes { get; init; }
        public bool IsActive { get; init; }
        public bool IsEvicted { get; init; }
        public long? GameAppId { get; init; }
        public string? GameName { get; init; }
        public string? GameImageUrl { get; init; }
        public long? DepotId { get; init; }
        public string? EpicAppId { get; init; }
        public string Datasource { get; init; } = string.Empty;
        public double? DurationSeconds { get; init; }

        public long TotalBytes => CacheHitBytes + CacheMissBytes;

        public double CacheHitPercent => TotalBytes > 0 ? (CacheHitBytes * 100.0) / TotalBytes : 0;

        public double AverageBytesPerSecond
        {
            get
            {
                var duration = DurationSeconds ?? (EndTimeUtc - StartTimeUtc).TotalSeconds;
                return duration > 0 ? TotalBytes / duration : 0;
            }
        }
    }

    private async Task<object> GetCachedDetectionAsync(long usedCacheSizeBytes, CancellationToken ct)
    {
        var cachedResults = await _gameCacheDetectionService.GetCachedDetectionAsync(ct);
        var games = cachedResults?.Games ?? [];

        // A response carrying only the unmapped bucket has no detection rows behind it, so its
        // StartTime is the load time rather than a scan time. The Games on Disk card would print
        // that as the moment the last scan ran. The bucket belongs to the detection panel.
        if (cachedResults == null || (games.Count == 0 && (cachedResults.Services?.Count ?? 0) == 0))
        {
            return CachedDetectionResponseBuilder.BuildEmpty();
        }

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

    // ───────────────────── New batch sub-queries (sparklines, hourly, cache snapshot) ─────────────────────

    private async Task<object> GetSparklineDataAsync(
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
            // Same overlap rule as the hourly section: a download still running at the cutoff
            // served part of its bytes inside the range. The start arm keeps unwritten ends.
            query = query.Where(d => d.EndTimeUtc >= cutoffTime || d.StartTimeUtc >= cutoffTime);
        }
        if (endTime.HasValue)
        {
            endDateTime = endTime.Value.FromUnixSeconds();
            query = query.Where(d => d.StartTimeUtc <= endDateTime);
        }

        var filteredQuery = query.ApplyStatsExcludedClientFilter(statsExcludedOnlyIps);

        // Rows, not a GROUP BY: spreading a download's bytes over the buckets it was active in
        // needs each row's own window, which no per-bucket aggregate carries.
        var spans = await filteredQuery
            .Select(d => new HourOfDayBuckets.Span(d.StartTimeUtc, d.EndTimeUtc, d.CacheHitBytes, d.CacheMissBytes))
            .ToListAsync(ct);

        // The drawn extent: every download's active window clipped to the picked range. Width and
        // chart window both come from this, so a still-running download carries the series to its
        // last write instead of ending it at the moment it began.
        DateTime? firstDrawn = null;
        DateTime? lastDrawn = null;
        foreach (var span in spans)
        {
            var start = span.StartUtc;
            var end = span.EndUtc > start ? span.EndUtc : start;
            var from = cutoffTime is { } lower && lower > start ? lower : start;
            var to = endDateTime is { } upper && upper < end ? upper : end;
            if (to < from)
            {
                continue;
            }
            if (firstDrawn is null || from < firstDrawn)
            {
                firstDrawn = from;
            }
            if (lastDrawn is null || to > lastDrawn)
            {
                lastDrawn = to;
            }
        }

        // Width comes from the stretch that actually has downloads, not only the picker. A live
        // view or a 7-day window that only contains one event day would otherwise stay on daily
        // buckets and collapse to a single point.
        double rangeHours = 999999;
        if (startTime.HasValue && endTime.HasValue)
        {
            rangeHours = (endTime.Value - startTime.Value) / 3600.0;
        }

        if (firstDrawn is not null && lastDrawn is not null)
        {
            var dataHours = Math.Max((lastDrawn.Value - firstDrawn.Value).TotalHours, 0);
            rangeHours = startTime.HasValue && endTime.HasValue
                ? Math.Min(rangeHours, dataHours)
                : dataHours;
        }

        var bucketMinutes = SparklineBuckets.ResolveMinutes(rangeHours);
        var present = SparklineBuckets.Spread(spans, bucketMinutes, cutoffTime, endDateTime);

        // The window comes from the rows the query returned, never from the picker: the width above
        // is already narrowed to those rows, and the two have to agree.
        DateTime windowStart;
        DateTime windowEnd;
        if (firstDrawn is not null && lastDrawn is not null)
        {
            (windowStart, windowEnd) = SparklineBuckets.CoveringWindow(firstDrawn.Value, lastDrawn.Value, bucketMinutes);
        }
        else
        {
            windowStart = DateTime.UtcNow;
            windowEnd = windowStart;
        }

        var bucketedData = SparklineBuckets.Fill(windowStart, windowEnd, bucketMinutes, present);

        var bandwidthSavedData = bucketedData.Select(d => (double)d.CacheHitBytes).ToList();
        var addedToCacheData = bucketedData.Select(d => (double)d.CacheMissBytes).ToList();
        var totalServedData = bucketedData.Select(d => (double)(d.CacheHitBytes + d.CacheMissBytes)).ToList();
        var cacheHitRatioData = bucketedData.Select(d =>
        {
            var total = d.CacheHitBytes + d.CacheMissBytes;
            return total > 0 ? (d.CacheHitBytes * 100.0) / total : 0.0;
        }).ToList();
        // DateTimeOffset applies the host's local offset to anything not marked UTC, which shifts
        // the buckets Fill inserted away from the ones the query returned.
        var bucketStarts = bucketedData
            .Select(d => new DateTimeOffset(DateTime.SpecifyKind(d.Start, DateTimeKind.Utc)).ToUnixTimeSeconds())
            .ToList();

        // Traffic is a property of the bucket, not of one metric, so every metric trims on this.
        var lastBucketWithTraffic = bucketedData.FindLastIndex(d => d.CacheHitBytes + d.CacheMissBytes > 0);

        return new SparklineDataResponse
        {
            BandwidthSaved = BuildSparklineMetric(bandwidthSavedData, SparklineTrendScale.Proportional, lastBucketWithTraffic),
            CacheHitRatio = BuildSparklineMetric(cacheHitRatioData, SparklineTrendScale.Points, lastBucketWithTraffic),
            TotalServed = BuildSparklineMetric(totalServedData, SparklineTrendScale.Proportional, lastBucketWithTraffic),
            AddedToCache = BuildSparklineMetric(addedToCacheData, SparklineTrendScale.Proportional, lastBucketWithTraffic),
            BucketMinutes = bucketMinutes,
            BucketStarts = bucketStarts,
            Period = startTime.HasValue ? "filtered" : "all"
        };
    }

    private async Task<object> GetHourlyActivityAsync(
        long? startTime, long? endTime,
        List<long> eventIdList, HashSet<long>? eventDownloadIds, string readerTimeZoneId,
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
            // In range when the active window reaches past the cutoff, not only when the download
            // began inside it: one still running at the cutoff served part of its bytes in range.
            // The start-time arm keeps rows whose end was never written.
            query = query.Where(d => d.EndTimeUtc >= cutoffTime || d.StartTimeUtc >= cutoffTime);
        }
        if (endTime.HasValue)
        {
            endDateTime = endTime.Value.FromUnixSeconds();
            query = query.Where(d => d.StartTimeUtc <= endDateTime);
        }

        var filteredQuery = query.ApplyStatsExcludedClientFilter(statsExcludedOnlyIps);

        // Rows, not a GROUP BY: spreading a download's bytes over the hours it was active needs
        // each row's own window, which no per-hour aggregate carries.
        var spans = await filteredQuery
            .Select(d => new HourOfDayBuckets.Span(d.StartTimeUtc, d.EndTimeUtc, d.CacheHitBytes, d.CacheMissBytes))
            .ToListAsync(ct);

        // Divisor for the per-day averages: how many times each hour of the day repeats over the
        // period. For a picked range that is the range's own length; with no range it is the
        // stretch the recorded downloads cover. Both arms count the same way, where counting only
        // days that saw traffic shrank the divisor over every quiet day and inflated the averages.
        var periodStart = cutoffTime;
        var periodEnd = endDateTime;
        if (spans.Count > 0)
        {
            periodStart ??= spans.Min(s => s.StartUtc);
            periodEnd ??= spans.Max(s => s.EndUtc > s.StartUtc ? s.EndUtc : s.StartUtc);
        }

        var daysInPeriod = periodStart.HasValue && periodEnd.HasValue
            ? Math.Max(1, (int)Math.Ceiling((periodEnd.Value - periodStart.Value).TotalDays))
            : 1;

        long? periodStartTimestamp = periodStart is { } first
            ? new DateTimeOffset(first.AsUtc()).ToUnixTimeSeconds()
            : null;
        long? periodEndTimestamp = periodEnd is { } last
            ? new DateTimeOffset(last.AsUtc()).ToUnixTimeSeconds()
            : null;

        // Every name this field can carry came out of KnownTimeZoneId or ServerTimeZone.IanaId,
        // both of which only pass names this resolver accepts.
        var zone = ScheduleTiming.ResolveTimeZone(readerTimeZoneId)!;

        // Bucketed on the reader's clock, the same one the widget draws its current-hour marker on.
        var allHours = HourOfDayBuckets.Build(spans, zone, cutoffTime, endDateTime);

        foreach (var hour in allHours)
        {
            hour.AvgDownloads = Math.Round((double)hour.Downloads / daysInPeriod, 1);
            hour.AvgBytesServed = hour.BytesServed / daysInPeriod;
        }

        var peakHour = PeakHour(allHours);

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

    private async Task<object> GetCacheSnapshotAsync(long? startTime, long? endTime, CancellationToken ct)
    {
        // A property of the running scheduler, not of the query range, so every return path below
        // (including both HasData=false ones) can report it.
        var nextSnapshotUtc = _cacheSnapshotService.NextRunUtc;

        if (!startTime.HasValue || !endTime.HasValue)
        {
            return new CacheSnapshotResponse { HasData = false, NextSnapshotUtc = nextSnapshotUtc };
        }

        var startUtc = startTime.Value.FromUnixSeconds();
        var endUtc = endTime.Value.FromUnixSeconds();

        var summary = await _cacheSnapshotService.GetSnapshotSummaryAsync(startUtc, endUtc, ct);

        if (summary == null)
        {
            return new CacheSnapshotResponse { HasData = false, NextSnapshotUtc = nextSnapshotUtc };
        }

        return new CacheSnapshotResponse
        {
            HasData = true,
            StartUsedSize = summary.StartUsedSize,
            EndUsedSize = summary.EndUsedSize,
            AverageUsedSize = summary.AverageUsedSize,
            TotalCacheSize = summary.TotalCacheSize,
            SnapshotCount = summary.SnapshotCount,
            IsEstimate = summary.IsEstimate,
            NextSnapshotUtc = nextSnapshotUtc
        };
    }

    // ───────────────────── Shared query helpers ─────────────────────

    /// <summary>
    /// The zone id in IANA form, or null when this server cannot resolve the name. An unresolvable
    /// name would leave the hourly section with no clock to bucket on, so it is refused here and
    /// the caller falls back to the server's own zone. Neither the incoming name nor
    /// TimeZoneInfo.Id is kept as it stands: either can be the Windows spelling, and one zone has
    /// to mean one cache key, so the name is put into IANA form first.
    /// </summary>
    internal static string? KnownTimeZoneId(string? timeZoneId)
    {
        if (timeZoneId is null)
        {
            return null;
        }

        var ianaId = ServerTimeZone.IanaId(timeZoneId, timeZoneId);
        return ScheduleTiming.ResolveTimeZone(ianaId) is not null ? ianaId : null;
    }

    /// <summary>
    /// The per-service totals both the batch endpoint and the services endpoint report.
    /// </summary>
    internal static IQueryable<ServiceStats> ServiceStatsQuery(IQueryable<Download> filteredQuery)
    {
        return filteredQuery
            .ApplyPlaceholderServiceFilter()
            .GroupBy(d => d.Service)
            .Select(g => new ServiceStats
            {
                Service = g.Key,
                TotalCacheHitBytes = g.Sum(d => d.CacheHitBytes),
                TotalCacheMissBytes = g.Sum(d => d.CacheMissBytes),
                TotalDownloads = g.Count(),
                LastActivityUtc = g.Max(d => d.StartTimeUtc)
            })
            .OrderByDescending(s => s.TotalCacheHitBytes + s.TotalCacheMissBytes);
    }

    /// <summary>
    /// The hour that served the most bytes, which is the hour the heatmap shades darkest. Counting
    /// downloads instead names a different hour when one large game outweighs many small ones.
    /// </summary>
    internal static int PeakHour(IReadOnlyList<HourlyActivityItem> hours)
    {
        return hours.MaxBy(h => h.BytesServed)?.Hour ?? 0;
    }

    private static IQueryable<Download> BuildBaseDownloadsQuery(AppDbContext context, List<string> hiddenClientIps, string evictedMode)
    {
        return context.Downloads.AsNoTracking()
            .ApplyHiddenClientFilter(hiddenClientIps)
            .ApplyEvictedFilter(evictedMode);
    }

    /// <summary>
    /// How a sparkline's trend is measured. Byte counts have no fixed upper bound, so they are
    /// compared proportionally; a percentage ratio is already normalised to 0-100, so it is
    /// compared by point difference instead.
    /// </summary>
    internal enum SparklineTrendScale
    {
        Proportional,
        Points
    }

    internal static SparklineMetric BuildSparklineMetric(
        List<double> data,
        SparklineTrendScale scale,
        int lastBucketWithTraffic)
    {
        // Gap-filled slots are real points, so trailing empty buckets would compare 0 against 0 and
        // report "stable" whatever the range did. Trimmed to the last bucket that carried traffic,
        // never to the last one this metric was non-zero in: a hit ratio that genuinely falls to 0%
        // is a measurement. The data itself stays whole, index-aligned with BucketStarts.
        var measured = lastBucketWithTraffic >= 0
            ? data.GetRange(0, lastBucketWithTraffic + 1)
            : data;

        if (measured.Count < 2)
            return new SparklineMetric { Data = data, Trend = "stable" };

        double recent;
        double earlier;
        if (measured.Count >= 4)
        {
            // Compare the last few points to the points immediately before them, so the trend
            // matches what the tail of the rendered sparkline looks like.
            int recentCount = Math.Min(3, measured.Count / 2);
            recent = measured.TakeLast(recentCount).Average();
            earlier = measured.Skip(Math.Max(0, measured.Count - recentCount * 2)).Take(recentCount).Average();
        }
        else
        {
            // Too few points to window, so compare last to first.
            recent = measured.Last();
            earlier = measured.First();
        }

        double diff = scale == SparklineTrendScale.Proportional
            ? (recent - earlier) / Math.Max(earlier, 0.001)
            : recent - earlier;
        double threshold = scale == SparklineTrendScale.Proportional ? 0.05 : 2.0;
        string trend = diff > threshold ? "up" : diff < -threshold ? "down" : "stable";

        return new SparklineMetric { Data = data, Trend = trend };
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
            || response.DownloadTotals == null
            || response.FilteredDownloadTotals == null
            || response.ServiceOptions == null
            || response.ClientOptions == null
            || response.RecentDownloads == null
            || response.Detection == null
            || response.Sparklines == null
            || response.HourlyActivity == null
            || response.CacheSnapshot == null;
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
            // client abort masquerade as missing data.
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
}
