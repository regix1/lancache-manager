using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

/// <summary>
/// Service for collecting and exposing LanCache-specific metrics to Prometheus/Grafana
/// Uses OpenTelemetry Metrics API for instrumentation
///
/// Metrics follow Prometheus naming conventions:
/// - snake_case names
/// - _total suffix for counters
/// - _bytes suffix for byte measurements
/// - _ratio suffix for ratios (0-1)
/// - _info suffix for metadata
/// </summary>
public class LancacheMetricsService : ScopedScheduledBackgroundService
{
    private readonly Meter _meter;
    private readonly Stopwatch _uptimeStopwatch;
    private readonly string _version;

    // Thread-safe storage for metric values
    private readonly ConcurrentDictionary<string, ServiceMetrics> _serviceMetrics = new();
    private readonly ConcurrentDictionary<string, ClientMetrics> _clientMetrics = new();

    // Keyed by the game identity key, so download traffic and cache-on-disk figures for the same
    // game carry identical labels. The two sets are ranked separately and rarely hold the same games.
    private readonly ConcurrentDictionary<string, GameMetrics> _gameMetrics = new();
    private readonly ConcurrentDictionary<string, GameCacheMetrics> _gameCacheMetrics = new();

    // Cache metrics
    private long _cacheCapacityBytes;
    private long _cacheUsedBytes;
    private long _cacheFreeBytes;
    private long _cacheUsageRatioBits;

    // Global counters
    private long _totalDownloads;
    private long _totalBytesServed;
    private long _cacheHitBytesTotal;
    private long _cacheMissBytesTotal;
    private long _cacheHitRatioBits;

    // Activity metrics
    private int _activeDownloads;
    private int _activeClients;
    private long _currentBytesPerSecond;

    // Download size metrics
    private long _averageDownloadSizeBytes;
    private long _largestDownloadBytes;

    // Peak usage hour metrics
    private int _peakHour;
    private long _peakHourDownloads;
    private long _currentHourDownloads;
    private string _peakTimeOfDay = "Night";
    private readonly ConcurrentDictionary<int, HourlyMetrics> _hourlyMetrics = new();

    // Cache growth metrics
    private long _cacheGrowthDailyBytes;
    private int _cacheGrowthTrend; // -1=down, 0=stable, 1=up
    private double _cacheGrowthPercentChange;
    private int _estimatedDaysUntilFull;

    // Games on disk (read from the persisted detection summary, never recomputed here)
    private long _gamesOnDiskBytes;
    private long _gamesOnDiskCount;
    private long _detectionComputedTimestamp;

    // The inputs the per-game on-disk figures were last read for. Plain fields rather than
    // interlocked ones because only the collection loop touches them, and it runs one cycle at a
    // time. The -1 start makes the first cycle always read.
    private long _gamesOnDiskQueryTicks = -1;
    private int _gamesOnDiskQueryTopGameCount;

    // Steam mapping coverage
    private long _steamUnknownGameBytes;
    private long _steamUnknownGameDownloads;

    // Time tracking
    private long _lastUpdateTimestamp;

    private class HourlyMetrics
    {
        public long Downloads;
        public long BytesServed;
    }

    // Configurable update interval (default 15 seconds)
    private int _updateIntervalSeconds = 15;
    private readonly object _intervalLock = new();
    private int _updateCount;

    // How many games each per-game gauge reports. Read from state every cycle rather than mirrored
    // in a field, so the API and the collection loop can never disagree about it. The cap bounds
    // the number of SERIES, never the number of gauges: all registration happens in the constructor
    // and cannot change at runtime. [32]
    private const int MinTopGameCount = 1;
    private const int MaxTopGameCount = 500;

    private class ServiceMetrics
    {
        public long TotalBytes;
        public long HitBytes;
        public long MissBytes;
        public long Downloads;
        public long HitRatioBits;
        public long ActiveDownloads;
    }

    private class ClientMetrics
    {
        public long TotalBytes;
        public long HitBytes;
        public long MissBytes;
        public long Downloads;
    }

    /// <summary>
    /// Per-game download counters. The dictionary key is the opaque identity key, which must never
    /// reach a label because it embeds a control character, so the label values ride here alongside
    /// the counters. <see cref="Labels"/> is the array the observers hand to Prometheus; the three
    /// strings behind it are kept because the cache-on-disk side reads them to label a game the two
    /// families share. All four are plain assignments: a reference write is atomic, so an observer
    /// can only ever see the previous or the current label set, never a torn one.
    /// </summary>
    private class GameMetrics
    {
        public long TotalBytes;
        public long HitBytes;
        public long MissBytes;
        public long Downloads;
        public long HitRatioBits;
        public long ActiveDownloads;
        public string Service = string.Empty;
        public string GameName = string.Empty;
        public string AppId = "0";
        public KeyValuePair<string, object?>[] Labels = [];
    }

    /// <summary>
    /// Per-game cache-on-disk counters. Separate from <see cref="GameMetrics"/> because the two are
    /// ranked over different tables and hold different games.
    /// </summary>
    private class GameCacheMetrics
    {
        public long CacheBytes;
        public long CacheFiles;
        public string Service = string.Empty;
        public string GameName = string.Empty;
        public string AppId = "0";
        public KeyValuePair<string, object?>[] Labels = [];
    }

    /// <summary>
    /// One identity-exact row of per-game download totals. Declared rather than projected into an
    /// anonymous type because the three arms of the query are concatenated into one list.
    /// </summary>
    private class GameDownloadTotals
    {
        public string Service { get; set; } = string.Empty;
        public long? GameAppId { get; set; }
        public string? EpicAppId { get; set; }
        public string? GameName { get; set; }
        public long TotalBytes { get; set; }
        public long HitBytes { get; set; }
        public long MissBytes { get; set; }
        public long Downloads { get; set; }
        public int ActiveCount { get; set; }
    }

    /// <summary>
    /// One row of per-game cache-on-disk size. Only the columns the metrics need are projected:
    /// reading the entity would drag CacheFilePathsJson, which holds every cache path the game
    /// matched.
    /// </summary>
    private class GameCacheTotals
    {
        public long GameAppId { get; set; }
        public string? GameName { get; set; }
        public string? Service { get; set; }
        public string? EpicAppId { get; set; }
        public ulong TotalSizeBytes { get; set; }
        public int CacheFilesFound { get; set; }
    }

    protected override string ServiceName => "LancacheMetricsService";
    protected override TimeSpan StartupDelay => TimeSpan.FromSeconds(3);
    protected override TimeSpan Interval
    {
        get
        {
            lock (_intervalLock)
            {
                return TimeSpan.FromSeconds(_updateIntervalSeconds);
            }
        }
    }

    public LancacheMetricsService(
        IServiceProvider serviceProvider,
        ILogger<LancacheMetricsService> logger,
        IConfiguration configuration)
        : base(serviceProvider, logger, configuration)
    {
        _uptimeStopwatch = Stopwatch.StartNew();

        // Get version from environment or assembly
        _version = Environment.GetEnvironmentVariable("LANCACHE_MANAGER_VERSION")
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";

        _logger.LogInformation("Initializing LancacheMetricsService v{Version}", _version);

        _meter = new Meter("LancacheManager", "1.0.0");

        // ============================================
        // INFO METRICS (metadata about the instance)
        // ============================================
        _meter.CreateObservableGauge(
            "lancache_info",
            () => new Measurement<int>(1, new KeyValuePair<string, object?>("version", _version)),
            description: "LanCache Manager information"
        );

        _meter.CreateObservableGauge(
            "lancache_uptime_seconds",
            () => (long)_uptimeStopwatch.Elapsed.TotalSeconds,
            description: "Time since LanCache Manager started"
        );

        _meter.CreateObservableGauge(
            "lancache_last_update_timestamp",
            () => Interlocked.Read(ref _lastUpdateTimestamp),
            description: "Unix timestamp of last metrics update"
        );

        // ============================================
        // CACHE STORAGE METRICS
        // ============================================
        _meter.CreateObservableGauge(
            "lancache_cache_capacity_bytes",
            () => Interlocked.Read(ref _cacheCapacityBytes),
            description: "Total cache storage capacity in bytes"
        );

        _meter.CreateObservableGauge(
            "lancache_cache_used_bytes",
            () => Interlocked.Read(ref _cacheUsedBytes),
            description: "Current cache used space in bytes"
        );

        _meter.CreateObservableGauge(
            "lancache_cache_free_bytes",
            () => Interlocked.Read(ref _cacheFreeBytes),
            description: "Current cache free space in bytes"
        );

        _meter.CreateObservableGauge(
            "lancache_cache_usage_ratio",
            () => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _cacheUsageRatioBits)),
            description: "Cache usage as ratio (0-1)"
        );

        // ============================================
        // GLOBAL TRAFFIC METRICS
        // ============================================
        _meter.CreateObservableGauge(
            "lancache_downloads_total",
            () => Interlocked.Read(ref _totalDownloads),
            description: "Total number of download sessions"
        );

        _meter.CreateObservableGauge(
            "lancache_bytes_served_total",
            () => Interlocked.Read(ref _totalBytesServed),
            description: "Total bytes served (hits + misses)"
        );

        _meter.CreateObservableGauge(
            "lancache_cache_hit_bytes_total",
            () => Interlocked.Read(ref _cacheHitBytesTotal),
            description: "Total cache hit bytes (bandwidth saved)"
        );

        _meter.CreateObservableGauge(
            "lancache_cache_miss_bytes_total",
            () => Interlocked.Read(ref _cacheMissBytesTotal),
            description: "Total cache miss bytes (downloaded from origin)"
        );

        _meter.CreateObservableGauge(
            "lancache_cache_hit_ratio",
            () => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _cacheHitRatioBits)),
            description: "Overall cache hit ratio (0-1)"
        );

        // ============================================
        // ACTIVITY METRICS (current state)
        // ============================================
        _meter.CreateObservableGauge(
            "lancache_active_downloads",
            () => _activeDownloads,
            description: "Number of currently active downloads"
        );

        _meter.CreateObservableGauge(
            "lancache_active_clients",
            () => _activeClients,
            description: "Number of unique clients with active downloads"
        );

        _meter.CreateObservableGauge(
            "lancache_throughput_bytes_per_second",
            () => Interlocked.Read(ref _currentBytesPerSecond),
            description: "Current download throughput in bytes/s"
        );

        // ============================================
        // DOWNLOAD SIZE METRICS
        // ============================================
        _meter.CreateObservableGauge(
            "lancache_download_size_average_bytes",
            () => Interlocked.Read(ref _averageDownloadSizeBytes),
            description: "Average download size in bytes"
        );

        _meter.CreateObservableGauge(
            "lancache_download_size_largest_bytes",
            () => Interlocked.Read(ref _largestDownloadBytes),
            description: "Largest single download in bytes"
        );

        // ============================================
        // PEAK USAGE HOUR METRICS
        // ============================================
        _meter.CreateObservableGauge(
            "lancache_peak_hour",
            () => _peakHour,
            description: "Hour of day with most downloads (0-23)"
        );

        _meter.CreateObservableGauge(
            "lancache_peak_hour_downloads",
            () => Interlocked.Read(ref _peakHourDownloads),
            description: "Number of downloads in peak hour (7-day period)"
        );

        _meter.CreateObservableGauge(
            "lancache_current_hour_downloads",
            () => Interlocked.Read(ref _currentHourDownloads),
            description: "Number of downloads in current hour (7-day period)"
        );

        _meter.CreateObservableGauge(
            "lancache_peak_time_of_day",
            () => new Measurement<int>(1, new KeyValuePair<string, object?>("period", _peakTimeOfDay)),
            description: "Time of day category for peak hour (Morning/Afternoon/Evening/Night)"
        );

        // Per-hour activity metrics (with hour label)
        _meter.CreateObservableGauge(
            "lancache_hourly_downloads",
            HourlyDownloadsObserver,
            description: "Downloads per hour of day (7-day period)"
        );

        _meter.CreateObservableGauge(
            "lancache_hourly_bytes",
            HourlyBytesObserver,
            description: "Bytes served per hour of day (7-day period)"
        );

        // ============================================
        // CACHE GROWTH METRICS
        // ============================================
        _meter.CreateObservableGauge(
            "lancache_cache_growth_daily_bytes",
            () => Interlocked.Read(ref _cacheGrowthDailyBytes),
            description: "Average daily cache growth in bytes (7-day period)"
        );

        _meter.CreateObservableGauge(
            "lancache_cache_growth_trend",
            () => _cacheGrowthTrend,
            description: "Cache growth trend: -1=decreasing, 0=stable, 1=increasing"
        );

        _meter.CreateObservableGauge(
            "lancache_cache_growth_percent_change",
            () => _cacheGrowthPercentChange,
            description: "Cache growth percent change (period over period)"
        );

        _meter.CreateObservableGauge(
            "lancache_cache_days_until_full",
            () => _estimatedDaysUntilFull,
            description: "Estimated days until cache is full (0 if not calculable)"
        );

        // ============================================
        // PER-SERVICE METRICS (with labels)
        // ============================================
        _meter.CreateObservableGauge(
            "lancache_service_bytes_total",
            ServiceBytesObserver,
            description: "Total bytes served per service"
        );

        _meter.CreateObservableGauge(
            "lancache_service_hit_bytes_total",
            ServiceHitBytesObserver,
            description: "Cache hit bytes per service"
        );

        _meter.CreateObservableGauge(
            "lancache_service_miss_bytes_total",
            ServiceMissBytesObserver,
            description: "Cache miss bytes per service"
        );

        _meter.CreateObservableGauge(
            "lancache_service_downloads_total",
            ServiceDownloadsObserver,
            description: "Total downloads per service"
        );

        _meter.CreateObservableGauge(
            "lancache_service_hit_ratio",
            ServiceHitRatioObserver,
            description: "Cache hit ratio per service (0-1)"
        );

        _meter.CreateObservableGauge(
            "lancache_service_active_downloads",
            ServiceActiveDownloadsObserver,
            description: "Active downloads per service"
        );

        // ============================================
        // PER-CLIENT METRICS (top clients with labels)
        // ============================================
        _meter.CreateObservableGauge(
            "lancache_client_bytes_total",
            ClientBytesObserver,
            description: "Total bytes served per client (top 10)"
        );

        _meter.CreateObservableGauge(
            "lancache_client_downloads_total",
            ClientDownloadsObserver,
            description: "Total downloads per client (top 10)"
        );

        // ============================================
        // PER-GAME METRICS (top N with labels)
        // ============================================
        // Gauges, and deliberately without the _total suffix their neighbours carry. Rows are
        // deleted from Downloads when a service stops appearing in the logs, so a per-game
        // cumulative figure can fall; a counter would report every such deletion as a reset and
        // produce a fake rate() spike. [18]
        _meter.CreateObservableGauge(
            "lancache_game_bytes",
            () => GameObserver(m => Interlocked.Read(ref m.TotalBytes)),
            description: "Total bytes served per game, all time"
        );

        _meter.CreateObservableGauge(
            "lancache_game_cache_hit_bytes",
            () => GameObserver(m => Interlocked.Read(ref m.HitBytes)),
            description: "Cache hit bytes per game, all time"
        );

        _meter.CreateObservableGauge(
            "lancache_game_cache_miss_bytes",
            () => GameObserver(m => Interlocked.Read(ref m.MissBytes)),
            description: "Cache miss bytes per game, all time"
        );

        _meter.CreateObservableGauge(
            "lancache_game_downloads",
            () => GameObserver(m => Interlocked.Read(ref m.Downloads)),
            description: "Download session count per game, all time"
        );

        _meter.CreateObservableGauge(
            "lancache_game_cache_hit_ratio",
            () => GameObserver(m => BitConverter.Int64BitsToDouble(Interlocked.Read(ref m.HitRatioBits))),
            description: "Cache hit ratio per game (0-1)"
        );

        _meter.CreateObservableGauge(
            "lancache_game_active_downloads",
            () => GameObserver(m => Interlocked.Read(ref m.ActiveDownloads)),
            description: "Downloads per game active now or ended in the last 5 minutes"
        );

        // ============================================
        // GAMES ON DISK METRICS
        // ============================================
        _meter.CreateObservableGauge(
            "lancache_game_cache_bytes",
            () => GameCacheObserver(m => Interlocked.Read(ref m.CacheBytes)),
            description: "Cache bytes on disk per game. These are raw per-game totals and are NOT deduplicated, so they do not sum to lancache_games_on_disk_bytes: lancache slices shared objects across games and the headline counts each file once"
        );

        _meter.CreateObservableGauge(
            "lancache_game_cache_files",
            () => GameCacheObserver(m => Interlocked.Read(ref m.CacheFiles)),
            description: "Cache files on disk per game, not deduplicated"
        );

        _meter.CreateObservableGauge(
            "lancache_games_on_disk_bytes",
            () => Interlocked.Read(ref _gamesOnDiskBytes),
            description: "Deduplicated cache bytes on disk across all identified games"
        );

        _meter.CreateObservableGauge(
            "lancache_games_on_disk_count",
            () => Interlocked.Read(ref _gamesOnDiskCount),
            description: "Number of identified games with cache on disk"
        );

        _meter.CreateObservableGauge(
            "lancache_detection_computed_timestamp",
            () => Interlocked.Read(ref _detectionComputedTimestamp),
            description: "Unix timestamp of the last cache detection run. The games-on-disk figures only change when a scan or a removal runs, so this is how old they are"
        );

        // ============================================
        // STEAM MAPPING COVERAGE METRICS
        // ============================================
        // Steam-only on purpose. WSUS and bare-metal traffic carries no game name by design, so a
        // metric counting every nameless download would report a healthy install as broken.
        _meter.CreateObservableGauge(
            "lancache_steam_unknown_game_bytes",
            () => Interlocked.Read(ref _steamUnknownGameBytes),
            description: "Bytes of Steam traffic whose game could not be identified"
        );

        _meter.CreateObservableGauge(
            "lancache_steam_unknown_game_downloads",
            () => Interlocked.Read(ref _steamUnknownGameDownloads),
            description: "Download sessions of Steam traffic whose game could not be identified"
        );

        _logger.LogInformation("LancacheMetricsService initialization complete");
    }

    // Service metrics measurement providers
    private IEnumerable<Measurement<long>> ServiceBytesObserver()
    {
        foreach (var kvp in _serviceMetrics)
        {
            yield return new Measurement<long>(
                Interlocked.Read(ref kvp.Value.TotalBytes),
                new KeyValuePair<string, object?>("service", kvp.Key)
            );
        }
    }

    private IEnumerable<Measurement<long>> ServiceHitBytesObserver()
    {
        foreach (var kvp in _serviceMetrics)
        {
            yield return new Measurement<long>(
                Interlocked.Read(ref kvp.Value.HitBytes),
                new KeyValuePair<string, object?>("service", kvp.Key)
            );
        }
    }

    private IEnumerable<Measurement<long>> ServiceMissBytesObserver()
    {
        foreach (var kvp in _serviceMetrics)
        {
            yield return new Measurement<long>(
                Interlocked.Read(ref kvp.Value.MissBytes),
                new KeyValuePair<string, object?>("service", kvp.Key)
            );
        }
    }

    private IEnumerable<Measurement<long>> ServiceDownloadsObserver()
    {
        foreach (var kvp in _serviceMetrics)
        {
            yield return new Measurement<long>(
                Interlocked.Read(ref kvp.Value.Downloads),
                new KeyValuePair<string, object?>("service", kvp.Key)
            );
        }
    }

    private IEnumerable<Measurement<double>> ServiceHitRatioObserver()
    {
        foreach (var kvp in _serviceMetrics)
        {
            yield return new Measurement<double>(
                BitConverter.Int64BitsToDouble(Interlocked.Read(ref kvp.Value.HitRatioBits)),
                new KeyValuePair<string, object?>("service", kvp.Key)
            );
        }
    }

    private IEnumerable<Measurement<long>> ServiceActiveDownloadsObserver()
    {
        foreach (var kvp in _serviceMetrics)
        {
            yield return new Measurement<long>(
                Interlocked.Read(ref kvp.Value.ActiveDownloads),
                new KeyValuePair<string, object?>("service", kvp.Key)
            );
        }
    }

    // Hourly metrics measurement providers
    private IEnumerable<Measurement<long>> HourlyDownloadsObserver()
    {
        foreach (var kvp in _hourlyMetrics)
        {
            yield return new Measurement<long>(
                Interlocked.Read(ref kvp.Value.Downloads),
                new KeyValuePair<string, object?>("hour", kvp.Key.ToString("D2"))
            );
        }
    }

    private IEnumerable<Measurement<long>> HourlyBytesObserver()
    {
        foreach (var kvp in _hourlyMetrics)
        {
            yield return new Measurement<long>(
                Interlocked.Read(ref kvp.Value.BytesServed),
                new KeyValuePair<string, object?>("hour", kvp.Key.ToString("D2"))
            );
        }
    }

    // Client metrics measurement providers
    private IEnumerable<Measurement<long>> ClientBytesObserver()
    {
        foreach (var kvp in _clientMetrics)
        {
            yield return new Measurement<long>(
                Interlocked.Read(ref kvp.Value.TotalBytes),
                new KeyValuePair<string, object?>("client", kvp.Key)
            );
        }
    }

    private IEnumerable<Measurement<long>> ClientDownloadsObserver()
    {
        foreach (var kvp in _clientMetrics)
        {
            yield return new Measurement<long>(
                Interlocked.Read(ref kvp.Value.Downloads),
                new KeyValuePair<string, object?>("client", kvp.Key)
            );
        }
    }

    // Game metrics measurement providers
    /// <summary>
    /// The label set both per-game families carry. Built once per game per cycle in the update loop
    /// and stored on the holder rather than rebuilt inside each observer: at the 500-game cap the
    /// eight per-game gauges would otherwise allocate four thousand of these arrays on every scrape.
    /// Taking the values as arguments is what lets one builder serve both holder types.
    /// </summary>
    private static KeyValuePair<string, object?>[] GameLabels(string service, string gameName, string appId) => new[]
    {
        new KeyValuePair<string, object?>("service", service),
        new KeyValuePair<string, object?>("game", gameName),
        new KeyValuePair<string, object?>("app_id", appId)
    };

    /// <summary>
    /// Every per-game download gauge reads the same dictionary and the same labels and differs only
    /// in which counter it reports, so each registration supplies that counter instead of repeating
    /// the loop. Adding a label is then one edit to <see cref="GameLabels"/> rather than six
    /// identical ones the compiler cannot check. The service, client and hourly observers below are
    /// left as they are: they pass a single label the measurement takes directly, so there is no
    /// array to prebuild and nothing to save.
    /// </summary>
    private IEnumerable<Measurement<T>> GameObserver<T>(Func<GameMetrics, T> read) where T : struct
    {
        foreach (var kvp in _gameMetrics)
        {
            yield return new Measurement<T>(read(kvp.Value), kvp.Value.Labels);
        }
    }

    /// <summary>
    /// The cache-on-disk counterpart of <see cref="GameObserver{T}"/>, over its own dictionary.
    /// </summary>
    private IEnumerable<Measurement<T>> GameCacheObserver<T>(Func<GameCacheMetrics, T> read) where T : struct
    {
        foreach (var kvp in _gameCacheMetrics)
        {
            yield return new Measurement<T>(read(kvp.Value), kvp.Value.Labels);
        }
    }

    /// <summary>
    /// Get the current metrics update interval in seconds
    /// </summary>
    public int GetUpdateInterval()
    {
        lock (_intervalLock)
        {
            return _updateIntervalSeconds;
        }
    }

    /// <summary>
    /// Set the metrics update interval in seconds (5-60 range)
    /// </summary>
    public void SetUpdateInterval(int seconds)
    {
        // Clamp to valid range
        seconds = Math.Clamp(seconds, 5, 60);

        lock (_intervalLock)
        {
            if (_updateIntervalSeconds != seconds)
            {
                _logger.LogInformation("Metrics update interval changed from {Old}s to {New}s", _updateIntervalSeconds, seconds);
                _updateIntervalSeconds = seconds;
            }
        }
    }

    protected override async Task ExecuteWorkAsync(
        IServiceProvider scopedServices,
        CancellationToken stoppingToken)
    {
        _updateCount++;
        await UpdateMetricsAsync(scopedServices, stoppingToken);

        // Update timestamp
        Interlocked.Exchange(ref _lastUpdateTimestamp, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // Log periodically based on interval
        int logFrequency = Math.Max(1, 600 / _updateIntervalSeconds); // Log roughly every 10 minutes
        if (_updateCount % logFrequency == 0)
        {
            _logger.LogDebug(
                "Metrics updated - Downloads: {Downloads}, Services: {Services}, ActiveDownloads: {Active}",
                _totalDownloads, _serviceMetrics.Count, _activeDownloads
            );
        }
    }

    /// <summary>
    /// The download set every query in a cycle reads from. Metrics are statistics, so they honour
    /// the same client exclusions every other stats surface does. Hidden and stats-only are two
    /// independent toggles on the Client Management page, so both lists have to be applied:
    /// honouring only the first leaves stats-only clients in the endpoint while the dashboard drops
    /// them. Composed in one place, matching DashboardBatchService and StatsController, so no query
    /// can be left out. Internal so the tests exercise this composition rather than a copy of
    /// it. [28] [37]
    /// </summary>
    internal static IQueryable<Download> ExcludedClientsRemoved(
        AppDbContext context,
        List<string> hiddenClientIps,
        List<string> statsExcludedOnlyIps)
    {
        var downloads = context.Downloads.ApplyHiddenClientFilter(hiddenClientIps);
        if (statsExcludedOnlyIps.Count > 0)
        {
            downloads = downloads.Where(d => !statsExcludedOnlyIps.Contains(d.ClientIp));
        }

        return downloads;
    }

    private async Task UpdateMetricsAsync(IServiceProvider scopedServices, CancellationToken cancellationToken)
    {
        var context = scopedServices.GetRequiredService<AppDbContext>();
        var cacheService = scopedServices.GetRequiredService<CacheManagementService>();
        var stateService = scopedServices.GetRequiredService<IStateService>();

        var downloads = ExcludedClientsRemoved(
            context,
            stateService.GetHiddenClientIps(),
            stateService.GetStatsExcludedOnlyClientIps());

        // ============================================
        // CACHE STORAGE METRICS
        // ============================================
        try
        {
            var cacheInfo = await cacheService.GetCacheInfoAsync();
            Interlocked.Exchange(ref _cacheCapacityBytes, cacheInfo.TotalCacheSize);
            Interlocked.Exchange(ref _cacheUsedBytes, cacheInfo.UsedCacheSize);
            Interlocked.Exchange(ref _cacheFreeBytes, cacheInfo.FreeCacheSize);

            var usageRatio = cacheInfo.TotalCacheSize > 0
                ? (double)cacheInfo.UsedCacheSize / cacheInfo.TotalCacheSize
                : 0;
            Interlocked.Exchange(ref _cacheUsageRatioBits, BitConverter.DoubleToInt64Bits(usageRatio));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get cache storage info");
        }

        // ============================================
        // GLOBAL METRICS (aggregated)
        // ============================================
        // One pass for every figure that reads the whole filtered set. The Steam coverage counts
        // ride along rather than running their own query: they read exactly these rows, and at an
        // update interval an operator can set to 5 seconds a second scan of the largest table in
        // the schema is the cost. Conditional aggregates are safe here in a way a CASE grouping key
        // is not, because GroupBy(_ => 1) is one group either way and the extra columns add no
        // memory to it.
        //
        // The coverage predicate is the same rule as DownloadsController.IsUnmappedSteam, written
        // so the database provider can translate it: Steam rows with no game name, or whose name is
        // just the service name echoed back. The lowercased name is compared against a local rather
        // than a literal because StringComparison does not reach SQL, matching how every other
        // service-name query in the app is written. Steam-only on purpose: WSUS and bare-metal
        // traffic carries no game name by design, so counting every nameless download would report
        // a healthy install as broken.
        var steamServiceName = "steam";
        var totals = await downloads
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Downloads = g.LongCount(),
                TotalBytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                HitBytes = g.Sum(d => d.CacheHitBytes),
                MissBytes = g.Sum(d => d.CacheMissBytes),
                MaxSize = g.Max(d => d.CacheHitBytes + d.CacheMissBytes),
                SteamUnknownBytes = g.Sum(d => d.Service.ToLower() == steamServiceName
                    && (d.GameName == null
                        || d.GameName == ""
                        || d.GameName.ToLower() == steamServiceName)
                    ? d.CacheHitBytes + d.CacheMissBytes
                    : 0L),
                SteamUnknownDownloads = g.LongCount(d => d.Service.ToLower() == steamServiceName
                    && (d.GameName == null
                        || d.GameName == ""
                        || d.GameName.ToLower() == steamServiceName))
            })
            .FirstOrDefaultAsync(cancellationToken);

        var totalDownloads = totals?.Downloads ?? 0;
        Interlocked.Exchange(ref _totalDownloads, totalDownloads);

        if (totals != null)
        {
            Interlocked.Exchange(ref _totalBytesServed, totals.TotalBytes);
            Interlocked.Exchange(ref _cacheHitBytesTotal, totals.HitBytes);
            Interlocked.Exchange(ref _cacheMissBytesTotal, totals.MissBytes);
            Interlocked.Exchange(ref _largestDownloadBytes, totals.MaxSize);

            var hitRatio = totals.TotalBytes > 0
                ? (double)totals.HitBytes / totals.TotalBytes
                : 0;
            Interlocked.Exchange(ref _cacheHitRatioBits, BitConverter.DoubleToInt64Bits(hitRatio));

            var avgSize = totalDownloads > 0 ? totals.TotalBytes / totalDownloads : 0;
            Interlocked.Exchange(ref _averageDownloadSizeBytes, avgSize);
        }

        Interlocked.Exchange(ref _steamUnknownGameBytes, totals?.SteamUnknownBytes ?? 0);
        Interlocked.Exchange(ref _steamUnknownGameDownloads, totals?.SteamUnknownDownloads ?? 0);

        // ============================================
        // ACTIVITY METRICS (currently active)
        // ============================================
        var fiveMinutesAgo = DateTime.UtcNow.AddMinutes(-5);

        // Use IsActive flag for truly active downloads
        var activeDownloadsData = await downloads
            .Where(d => d.IsActive || d.EndTimeUtc >= fiveMinutesAgo)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Count = g.Count(),
                UniqueClients = g.Select(d => d.ClientIp).Distinct().Count(),
                BytesInProgress = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes)
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (activeDownloadsData != null)
        {
            Interlocked.Exchange(ref _activeDownloads, activeDownloadsData.Count);
            Interlocked.Exchange(ref _activeClients, activeDownloadsData.UniqueClients);
        }

        // Calculate throughput from recent downloads (last minute)
        var oneMinuteAgo = DateTime.UtcNow.AddMinutes(-1);
        var recentBytes = await downloads
            .Where(d => d.EndTimeUtc >= oneMinuteAgo)
            .SumAsync(d => (long?)d.CacheHitBytes + d.CacheMissBytes, cancellationToken) ?? 0;
        Interlocked.Exchange(ref _currentBytesPerSecond, recentBytes / 60);

        // ============================================
        // PER-SERVICE METRICS
        // ============================================
        var serviceStats = await downloads
            .GroupBy(d => d.Service.ToLower())
            .Select(g => new
            {
                Service = g.Key,
                TotalBytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                HitBytes = g.Sum(d => d.CacheHitBytes),
                MissBytes = g.Sum(d => d.CacheMissBytes),
                Downloads = g.LongCount(),
                ActiveCount = g.Count(d => d.IsActive || d.EndTimeUtc >= fiveMinutesAgo)
            })
            .ToListAsync(cancellationToken);

        // Clear old services that no longer exist
        var currentServices = serviceStats.Select(s => s.Service).ToHashSet();
        RemoveMissingKeys(_serviceMetrics, currentServices);

        // Update service metrics
        foreach (var stat in serviceStats)
        {
            var metrics = _serviceMetrics.GetOrAdd(stat.Service, _ => new ServiceMetrics());
            Interlocked.Exchange(ref metrics.TotalBytes, stat.TotalBytes);
            Interlocked.Exchange(ref metrics.HitBytes, stat.HitBytes);
            Interlocked.Exchange(ref metrics.MissBytes, stat.MissBytes);
            Interlocked.Exchange(ref metrics.Downloads, stat.Downloads);
            Interlocked.Exchange(ref metrics.ActiveDownloads, stat.ActiveCount);

            var hitRatio = stat.TotalBytes > 0
                ? (double)stat.HitBytes / stat.TotalBytes
                : 0;
            Interlocked.Exchange(ref metrics.HitRatioBits, BitConverter.DoubleToInt64Bits(hitRatio));
        }

        // ============================================
        // TOP CLIENTS METRICS (top 10 by bytes)
        // ============================================
        var topClients = await downloads
            .GroupBy(d => d.ClientIp)
            .Select(g => new
            {
                ClientIp = g.Key,
                TotalBytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                HitBytes = g.Sum(d => d.CacheHitBytes),
                MissBytes = g.Sum(d => d.CacheMissBytes),
                Downloads = g.LongCount()
            })
            .OrderByDescending(c => c.TotalBytes)
            .Take(10)
            .ToListAsync(cancellationToken);

        // Clear old clients
        var currentClients = topClients.Select(c => c.ClientIp).ToHashSet();
        RemoveMissingKeys(_clientMetrics, currentClients);

        // Update client metrics
        foreach (var client in topClients)
        {
            var metrics = _clientMetrics.GetOrAdd(client.ClientIp, _ => new ClientMetrics());
            Interlocked.Exchange(ref metrics.TotalBytes, client.TotalBytes);
            Interlocked.Exchange(ref metrics.HitBytes, client.HitBytes);
            Interlocked.Exchange(ref metrics.MissBytes, client.MissBytes);
            Interlocked.Exchange(ref metrics.Downloads, client.Downloads);
        }

        // ============================================
        // PEAK USAGE HOUR METRICS (7-day period)
        // ============================================
        var sevenDaysAgo = DateTime.UtcNow.AddDays(-7);

        var hourlyActivity = await downloads
            .Where(d => d.StartTimeUtc >= sevenDaysAgo)
            .GroupBy(d => d.StartTimeLocal.Hour)
            .Select(g => new
            {
                Hour = g.Key,
                Downloads = g.LongCount(),
                BytesServed = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes)
            })
            .ToListAsync(cancellationToken);

        // Initialize all 24 hours
        for (int hour = 0; hour < 24; hour++)
        {
            var hourMetrics = _hourlyMetrics.GetOrAdd(hour, _ => new HourlyMetrics());
            var hourData = hourlyActivity.FirstOrDefault(h => h.Hour == hour);
            Interlocked.Exchange(ref hourMetrics.Downloads, hourData?.Downloads ?? 0);
            Interlocked.Exchange(ref hourMetrics.BytesServed, hourData?.BytesServed ?? 0);
        }

        // Find peak hour
        var peakHourData = hourlyActivity.OrderByDescending(h => h.Downloads).FirstOrDefault();
        if (peakHourData != null)
        {
            _peakHour = peakHourData.Hour;
            Interlocked.Exchange(ref _peakHourDownloads, peakHourData.Downloads);
            _peakTimeOfDay = GetTimeOfDayLabel(peakHourData.Hour);
        }

        // Current hour downloads
        var currentHour = DateTime.Now.Hour;
        var currentHourData = hourlyActivity.FirstOrDefault(h => h.Hour == currentHour);
        Interlocked.Exchange(ref _currentHourDownloads, currentHourData?.Downloads ?? 0);

        // ============================================
        // CACHE GROWTH METRICS (7-day period)
        // ============================================
        var dailyGrowth = await downloads
            .Where(d => d.StartTimeUtc >= sevenDaysAgo)
            .GroupBy(d => d.StartTimeUtc.Date)
            .OrderBy(g => g.Key)
            .Select(g => new
            {
                Date = g.Key,
                GrowthBytes = g.Sum(d => d.CacheMissBytes)
            })
            .ToListAsync(cancellationToken);

        if (dailyGrowth.Count >= 2)
        {
            // Calculate average daily growth
            var totalGrowth = dailyGrowth.Sum(d => d.GrowthBytes);
            var daysCovered = (dailyGrowth.Last().Date - dailyGrowth.First().Date).TotalDays;
            var avgDailyGrowth = daysCovered > 0 ? (long)(totalGrowth / daysCovered) : 0;
            Interlocked.Exchange(ref _cacheGrowthDailyBytes, avgDailyGrowth);

            // Period-over-period comparison for trend
            var midpoint = dailyGrowth.Count / 2;
            var olderHalf = dailyGrowth.Take(midpoint).ToList();
            var recentHalf = dailyGrowth.Skip(midpoint).ToList();

            var olderAvg = olderHalf.Count > 0 ? olderHalf.Average(d => d.GrowthBytes) : 0;
            var recentAvg = recentHalf.Count > 0 ? recentHalf.Average(d => d.GrowthBytes) : 0;

            var percentChange = PercentageUtils.CalculateBoundedChange(olderAvg, recentAvg);
            _cacheGrowthPercentChange = Math.Round(percentChange, 1);

            // Determine trend
            if (percentChange > 5) _cacheGrowthTrend = 1; // Up
            else if (percentChange < -5) _cacheGrowthTrend = -1; // Down
            else _cacheGrowthTrend = 0; // Stable

            // Estimate days until full
            if (avgDailyGrowth > 0 && _cacheFreeBytes > 0)
            {
                _estimatedDaysUntilFull = (int)Math.Ceiling((double)Interlocked.Read(ref _cacheFreeBytes) / avgDailyGrowth);
            }
            else
            {
                _estimatedDaysUntilFull = 0;
            }
        }
        else
        {
            Interlocked.Exchange(ref _cacheGrowthDailyBytes, 0);
            _cacheGrowthPercentChange = 0;
            _cacheGrowthTrend = 0;
            _estimatedDaysUntilFull = 0;
        }

        // ============================================
        // PER-GAME METRICS (top N by bytes)
        // ============================================
        // Last in the cycle and behind its own catch. Every query above is unguarded, so a failure
        // in the newest and most expensive one would otherwise abort the cycle and leave every
        // metric that follows it stuck on its previous value with no error signal. [26]
        try
        {
            // The persisted cap is the source of truth so the setting survives a restart. Clamped
            // on read because state.json is a plain file an operator can edit by hand. [32]
            var topGameCount = Math.Clamp(stateService.GetTopGameCount(), MinTopGameCount, MaxTopGameCount);

            // Three groupings, each keyed on exactly the columns that define one game's identity,
            // so every Take below is exact and a game in the global top N is necessarily in the top
            // N of its own arm. The display name stays out of every key: a key carrying GameName
            // splits one game into a row per recorded name, each ranking on its own bytes, so the
            // game can miss the cut however far the fetch over-reaches. The service stays out of
            // the two id-based keys for the same reason, because a Steam or Epic id identifies the
            // game on its own and one id recorded under two service names would otherwise rank
            // twice on partial bytes. The named arm does key on service, because there the service
            // is half the identity. Rows carrying no game information at all are excluded: they do
            // not belong in a per-game metric. [31]
            //
            // The service aggregate is asserted non-null: Download.Service is a non-nullable column
            // and a group always holds at least one row, so the compiler's empty-sequence case
            // cannot arise. Coalescing instead would add a default that can never be reached.
            var epicTotals = await downloads
                .Where(d => d.EpicAppId != null && d.EpicAppId != "")
                .GroupBy(d => d.EpicAppId)
                .Select(g => new GameDownloadTotals
                {
                    Service = g.Min(d => d.Service.ToLower())!,
                    GameAppId = null,
                    EpicAppId = g.Key,
                    GameName = g.Max(d => d.GameName),
                    TotalBytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                    HitBytes = g.Sum(d => d.CacheHitBytes),
                    MissBytes = g.Sum(d => d.CacheMissBytes),
                    Downloads = g.LongCount(),
                    ActiveCount = g.Count(d => d.IsActive || d.EndTimeUtc >= fiveMinutesAgo)
                })
                .OrderByDescending(x => x.TotalBytes)
                .Take(topGameCount)
                .ToListAsync(cancellationToken);

            var steamTotals = await downloads
                .Where(d => (d.EpicAppId == null || d.EpicAppId == "")
                    && d.GameAppId != null
                    && d.GameAppId != 0)
                .GroupBy(d => d.GameAppId)
                .Select(g => new GameDownloadTotals
                {
                    // One service name per id, chosen the same deterministic way the detection save
                    // path chooses one, so the label cannot change with row visit order. [31]
                    Service = g.Min(d => d.Service.ToLower())!,
                    GameAppId = g.Key,
                    EpicAppId = null,
                    GameName = g.Max(d => d.GameName),
                    TotalBytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                    HitBytes = g.Sum(d => d.CacheHitBytes),
                    MissBytes = g.Sum(d => d.CacheMissBytes),
                    Downloads = g.LongCount(),
                    ActiveCount = g.Count(d => d.IsActive || d.EndTimeUtc >= fiveMinutesAgo)
                })
                .OrderByDescending(x => x.TotalBytes)
                .Take(topGameCount)
                .ToListAsync(cancellationToken);

            var namedTotals = await downloads
                .Where(d => (d.EpicAppId == null || d.EpicAppId == "")
                    && (d.GameAppId == null || d.GameAppId == 0)
                    && d.GameName != null
                    && d.GameName != "")
                .GroupBy(d => new { Service = d.Service.ToLower(), d.GameName })
                .Select(g => new GameDownloadTotals
                {
                    Service = g.Key.Service,
                    GameAppId = null,
                    EpicAppId = null,
                    GameName = g.Key.GameName,
                    TotalBytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                    HitBytes = g.Sum(d => d.CacheHitBytes),
                    MissBytes = g.Sum(d => d.CacheMissBytes),
                    Downloads = g.LongCount(),
                    ActiveCount = g.Count(d => d.IsActive || d.EndTimeUtc >= fiveMinutesAgo)
                })
                .OrderByDescending(x => x.TotalBytes)
                .Take(topGameCount)
                .ToListAsync(cancellationToken);

            // Each arm already folds to one row per identity in SQL and the three arms produce
            // disjoint keys, so this is normally a concatenation. It still totals rather than
            // overwrites: the arms lowercase the service in SQL while the key lowercases it in C#,
            // and two rows that ever do land on one key must not lose half their bytes.
            var mergedGames = new Dictionary<string, GameDownloadTotals>(StringComparer.Ordinal);
            foreach (var row in epicTotals.Concat(steamTotals).Concat(namedTotals))
            {
                var identityKey = GamesOnDiskCalculator.GetDownloadGameKey(row.GameAppId, row.EpicAppId, row.Service, row.GameName);
                if (mergedGames.TryGetValue(identityKey, out var existing))
                {
                    existing.TotalBytes += row.TotalBytes;
                    existing.HitBytes += row.HitBytes;
                    existing.MissBytes += row.MissBytes;
                    existing.Downloads += row.Downloads;
                    existing.ActiveCount += row.ActiveCount;
                    if (string.IsNullOrEmpty(existing.GameName))
                    {
                        existing.GameName = row.GameName;
                    }
                    continue;
                }

                mergedGames[identityKey] = row;
            }

            var topGames = mergedGames
                .OrderByDescending(pair => pair.Value.TotalBytes)
                .Take(topGameCount)
                .ToList();

            // A dropped game stops being reported entirely, so its series ends at its last real
            // sample rather than at a zero it never served. [25]
            var currentGames = topGames.Select(pair => pair.Key).ToHashSet();
            RemoveMissingKeys(_gameMetrics, currentGames);

            // Update game metrics
            foreach (var pair in topGames)
            {
                var row = pair.Value;
                var appId = !string.IsNullOrEmpty(row.EpicAppId)
                    ? row.EpicAppId
                    : (row.GameAppId?.ToString() ?? "0");
                var gameName = string.IsNullOrEmpty(row.GameName) ? appId : row.GameName;

                // The labels are built once here and go in before the holder is published, so a
                // scrape landing between the insert and the first label write cannot emit a blank
                // series. They are assigned again below because an existing game's recorded name
                // can change between cycles. [34]
                var labels = GameLabels(row.Service, gameName, appId);
                var metrics = _gameMetrics.GetOrAdd(pair.Key, _ => new GameMetrics
                {
                    Service = row.Service,
                    GameName = gameName,
                    AppId = appId,
                    Labels = labels
                });
                metrics.Service = row.Service;
                metrics.GameName = gameName;
                metrics.AppId = appId;
                metrics.Labels = labels;
                Interlocked.Exchange(ref metrics.TotalBytes, row.TotalBytes);
                Interlocked.Exchange(ref metrics.HitBytes, row.HitBytes);
                Interlocked.Exchange(ref metrics.MissBytes, row.MissBytes);
                Interlocked.Exchange(ref metrics.Downloads, row.Downloads);
                Interlocked.Exchange(ref metrics.ActiveDownloads, row.ActiveCount);

                var hitRatio = row.TotalBytes > 0
                    ? (double)row.HitBytes / row.TotalBytes
                    : 0;
                Interlocked.Exchange(ref metrics.HitRatioBits, BitConverter.DoubleToInt64Bits(hitRatio));
            }

            // ============================================
            // GAMES ON DISK METRICS
            // ============================================
            // The persisted summary only. Recomputing it re-stats every cache file, which cannot
            // run on a loop that fires as often as every 5 seconds. Read before the per-game
            // figures because its timestamp is what decides whether those need re-reading at all.
            var detectionSummary = await context.CachedDetectionSummaries
                .Where(s => s.Id == CachedDetectionSummary.SingletonId)
                .Select(s => new
                {
                    s.GamesOnDiskBytes,
                    s.GamesOnDiskCount,
                    s.ComputedAtUtc
                })
                .FirstOrDefaultAsync(cancellationToken);

            Interlocked.Exchange(ref _gamesOnDiskBytes, detectionSummary != null ? (long)detectionSummary.GamesOnDiskBytes : 0);
            Interlocked.Exchange(ref _gamesOnDiskCount, detectionSummary?.GamesOnDiskCount ?? 0);
            Interlocked.Exchange(
                ref _detectionComputedTimestamp,
                detectionSummary != null
                    ? new DateTimeOffset(DateTime.SpecifyKind(detectionSummary.ComputedAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds()
                    : 0
            );

            // The per-game on-disk figures only move when a detection run or a removal rewrites the
            // summary, so the scan behind them is skipped while both that timestamp and the game cap
            // hold still. The cap belongs in the check because raising it admits games the last scan
            // never fetched. The prune sits inside the skip for the same reason the query does:
            // running it against a set that was not re-read would empty a dictionary whose numbers
            // are still current. The labels refresh with the figures, so a game renamed on the
            // download side between detection runs keeps its detected name here until the next one.
            //
            // Correct only while every path that removes cache in bulk rewrites the summary. If one
            // ever stops doing that, these figures go at most one detection run stale, which is
            // exactly what lancache_detection_computed_timestamp exists to report.
            var detectionTicks = detectionSummary?.ComputedAtUtc.Ticks ?? 0;
            if (detectionTicks != _gamesOnDiskQueryTicks || topGameCount != _gamesOnDiskQueryTopGameCount)
            {
                await UpdateGamesOnDiskMetricsAsync(context, topGameCount, cancellationToken);
                _gamesOnDiskQueryTicks = detectionTicks;
                _gamesOnDiskQueryTopGameCount = topGameCount;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A shutdown part-way through a query is not a metrics failure, and the loop in the
            // base class ends cleanly on this rather than logging an error.
            throw;
        }
        catch (Exception ex)
        {
            // Warning, not Debug: the default log level is Warning, so a Debug line prints nothing
            // at all and every per-game gauge keeps serving the previous cycle's numbers with no
            // gap, including the timestamp gauge that exists to report staleness. [33]
            _logger.LogWarning(ex, "Failed to update per-game metrics");
        }
    }

    /// <summary>
    /// Reads the per-game cache-on-disk figures and republishes their gauges. Called only when the
    /// detection summary or the game cap has moved, because nothing else can change what it reads.
    /// </summary>
    private async Task UpdateGamesOnDiskMetricsAsync(
        AppDbContext context,
        int topGameCount,
        CancellationToken cancellationToken)
    {
        // Only the columns the metrics need. Reading the entity would drag CacheFilePathsJson,
        // which holds every cache path each game matched. Evicted games are skipped so the
        // figure agrees with the dashboard. Rows carrying no identity at all are dropped in SQL
        // rather than after the cut: with no app id, no Epic id and no name they all fold onto
        // one key the download arms can never produce, and publish as a single game named "0"
        // with no partner series while taking a top-N slot from a real game. [35]
        var gamesOnDisk = await context.CachedGameDetections
            .Where(g => !g.IsEvicted
                && (g.GameAppId != 0
                    || (g.EpicAppId != null && g.EpicAppId != "")
                    || (g.GameName != null && g.GameName != "")))
            .Select(g => new GameCacheTotals
            {
                GameAppId = g.GameAppId,
                GameName = g.GameName,
                Service = g.Service,
                EpicAppId = g.EpicAppId,
                TotalSizeBytes = g.TotalSizeBytes,
                CacheFilesFound = g.CacheFilesFound
            })
            .OrderByDescending(x => x.TotalSizeBytes)
            .Take(topGameCount)
            .ToListAsync(cancellationToken);

        var mergedGameCaches = new Dictionary<string, GameCacheTotals>(StringComparer.Ordinal);
        foreach (var row in gamesOnDisk)
        {
            var identityKey = GamesOnDiskCalculator.GetDownloadGameKey(row.GameAppId, row.EpicAppId, row.Service, row.GameName);
            if (mergedGameCaches.TryGetValue(identityKey, out var existing))
            {
                existing.TotalSizeBytes += row.TotalSizeBytes;
                existing.CacheFilesFound += row.CacheFilesFound;
                continue;
            }

            mergedGameCaches[identityKey] = row;
        }

        // Clear old games that no longer hold cache
        RemoveMissingKeys(_gameCacheMetrics, mergedGameCaches.Keys);

        // Update game cache metrics
        foreach (var pair in mergedGameCaches)
        {
            var row = pair.Value;
            var appId = !string.IsNullOrEmpty(row.EpicAppId)
                ? row.EpicAppId
                : row.GameAppId.ToString();

            // A game reported by both families has to carry identical labels in both or a join
            // across them returns nothing, which is the whole reason the two share an identity
            // key. When this game also reports download traffic this cycle its labels are
            // copied from there rather than derived a second way: the download side resolves
            // the name with a server-side max over every row recorded for the id, so a game
            // renamed since it was detected would otherwise be labelled one way here and
            // another way there. A game held only on disk has no partner series, so it falls
            // back to its own row, lowercased to match the download arms and reading a missing
            // service as steam, which is what a NULL service column means. [29] [30]
            string service;
            string gameName;
            if (_gameMetrics.TryGetValue(pair.Key, out var downloadMetrics))
            {
                service = downloadMetrics.Service;
                gameName = downloadMetrics.GameName;
            }
            else
            {
                service = string.IsNullOrEmpty(row.Service) ? "steam" : row.Service.ToLowerInvariant();
                gameName = string.IsNullOrEmpty(row.GameName) ? appId : row.GameName;
            }

            // Published with its labels already set, for the same reason the download holders
            // are, and re-assigned because an existing game's name can change between
            // cycles. [34]
            var labels = GameLabels(service, gameName, appId);
            var metrics = _gameCacheMetrics.GetOrAdd(pair.Key, _ => new GameCacheMetrics
            {
                Service = service,
                GameName = gameName,
                AppId = appId,
                Labels = labels
            });
            metrics.Service = service;
            metrics.GameName = gameName;
            metrics.AppId = appId;
            metrics.Labels = labels;
            Interlocked.Exchange(ref metrics.CacheBytes, (long)row.TotalSizeBytes);
            Interlocked.Exchange(ref metrics.CacheFiles, row.CacheFilesFound);
        }
    }

    /// <summary>
    /// Drops the entries whose key is no longer being reported. Removing an entry is what ends its
    /// series at its last real sample; reporting a zero instead would be a real measurement,
    /// indistinguishable from an entity that genuinely served nothing. <c>Keys</c> on a
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> is already a snapshot, so it is enumerated
    /// directly rather than copied again. [38]
    /// </summary>
    private static void RemoveMissingKeys<T>(ConcurrentDictionary<string, T> metrics, ICollection<string> currentKeys)
    {
        foreach (var key in metrics.Keys)
        {
            if (!currentKeys.Contains(key))
            {
                metrics.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Get time of day label for an hour (0-23)
    /// </summary>
    private static string GetTimeOfDayLabel(int hour)
    {
        if (hour >= 5 && hour < 12) return "Morning";
        if (hour >= 12 && hour < 17) return "Afternoon";
        if (hour >= 17 && hour < 21) return "Evening";
        return "Night";
    }
}
