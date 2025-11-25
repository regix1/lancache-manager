using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using LancacheManager.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Application.Services;

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
public class LancacheMetricsService : BackgroundService
{
    private readonly Meter _meter;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LancacheMetricsService> _logger;
    private readonly Stopwatch _uptimeStopwatch;
    private readonly string _version;

    // Thread-safe storage for metric values
    private readonly ConcurrentDictionary<string, ServiceMetrics> _serviceMetrics = new();
    private readonly ConcurrentDictionary<string, ClientMetrics> _clientMetrics = new();

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

    // Time tracking
    private long _lastUpdateTimestamp;

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

    public LancacheMetricsService(IServiceScopeFactory scopeFactory, ILogger<LancacheMetricsService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
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
        // PER-SERVICE METRICS (with labels)
        // ============================================
        _meter.CreateObservableGauge(
            "lancache_service_bytes_total",
            GetServiceBytesMetrics,
            description: "Total bytes served per service"
        );

        _meter.CreateObservableGauge(
            "lancache_service_hit_bytes_total",
            GetServiceHitBytesMetrics,
            description: "Cache hit bytes per service"
        );

        _meter.CreateObservableGauge(
            "lancache_service_miss_bytes_total",
            GetServiceMissBytesMetrics,
            description: "Cache miss bytes per service"
        );

        _meter.CreateObservableGauge(
            "lancache_service_downloads_total",
            GetServiceDownloadsMetrics,
            description: "Total downloads per service"
        );

        _meter.CreateObservableGauge(
            "lancache_service_hit_ratio",
            GetServiceHitRatioMetrics,
            description: "Cache hit ratio per service (0-1)"
        );

        _meter.CreateObservableGauge(
            "lancache_service_active_downloads",
            GetServiceActiveDownloadsMetrics,
            description: "Active downloads per service"
        );

        // ============================================
        // PER-CLIENT METRICS (top clients with labels)
        // ============================================
        _meter.CreateObservableGauge(
            "lancache_client_bytes_total",
            GetClientBytesMetrics,
            description: "Total bytes served per client (top 10)"
        );

        _meter.CreateObservableGauge(
            "lancache_client_downloads_total",
            GetClientDownloadsMetrics,
            description: "Total downloads per client (top 10)"
        );

        _logger.LogInformation("LancacheMetricsService initialization complete");
    }

    // Service metrics measurement providers
    private IEnumerable<Measurement<long>> GetServiceBytesMetrics()
    {
        foreach (var kvp in _serviceMetrics)
        {
            yield return new Measurement<long>(
                Interlocked.Read(ref kvp.Value.TotalBytes),
                new KeyValuePair<string, object?>("service", kvp.Key)
            );
        }
    }

    private IEnumerable<Measurement<long>> GetServiceHitBytesMetrics()
    {
        foreach (var kvp in _serviceMetrics)
        {
            yield return new Measurement<long>(
                Interlocked.Read(ref kvp.Value.HitBytes),
                new KeyValuePair<string, object?>("service", kvp.Key)
            );
        }
    }

    private IEnumerable<Measurement<long>> GetServiceMissBytesMetrics()
    {
        foreach (var kvp in _serviceMetrics)
        {
            yield return new Measurement<long>(
                Interlocked.Read(ref kvp.Value.MissBytes),
                new KeyValuePair<string, object?>("service", kvp.Key)
            );
        }
    }

    private IEnumerable<Measurement<long>> GetServiceDownloadsMetrics()
    {
        foreach (var kvp in _serviceMetrics)
        {
            yield return new Measurement<long>(
                Interlocked.Read(ref kvp.Value.Downloads),
                new KeyValuePair<string, object?>("service", kvp.Key)
            );
        }
    }

    private IEnumerable<Measurement<double>> GetServiceHitRatioMetrics()
    {
        foreach (var kvp in _serviceMetrics)
        {
            yield return new Measurement<double>(
                BitConverter.Int64BitsToDouble(Interlocked.Read(ref kvp.Value.HitRatioBits)),
                new KeyValuePair<string, object?>("service", kvp.Key)
            );
        }
    }

    private IEnumerable<Measurement<long>> GetServiceActiveDownloadsMetrics()
    {
        foreach (var kvp in _serviceMetrics)
        {
            yield return new Measurement<long>(
                Interlocked.Read(ref kvp.Value.ActiveDownloads),
                new KeyValuePair<string, object?>("service", kvp.Key)
            );
        }
    }

    // Client metrics measurement providers
    private IEnumerable<Measurement<long>> GetClientBytesMetrics()
    {
        foreach (var kvp in _clientMetrics)
        {
            yield return new Measurement<long>(
                Interlocked.Read(ref kvp.Value.TotalBytes),
                new KeyValuePair<string, object?>("client", kvp.Key)
            );
        }
    }

    private IEnumerable<Measurement<long>> GetClientDownloadsMetrics()
    {
        foreach (var kvp in _clientMetrics)
        {
            yield return new Measurement<long>(
                Interlocked.Read(ref kvp.Value.Downloads),
                new KeyValuePair<string, object?>("client", kvp.Key)
            );
        }
    }

    /// <summary>
    /// Background task to periodically update metric values from database
    /// Updates every 15 seconds for near real-time monitoring
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LancacheMetricsService background task started");

        // Wait for app to initialize
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        int updateCount = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                updateCount++;
                await UpdateMetricsAsync(stoppingToken);

                // Update timestamp
                Interlocked.Exchange(ref _lastUpdateTimestamp, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                // Log every 40 updates (~10 minutes at 15s interval)
                if (updateCount % 40 == 0)
                {
                    _logger.LogDebug(
                        "Metrics updated - Downloads: {Downloads}, Services: {Services}, ActiveDownloads: {Active}",
                        _totalDownloads, _serviceMetrics.Count, _activeDownloads
                    );
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update metrics");
            }

            // Update every 15 seconds for responsive dashboards
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }

        _logger.LogInformation("LancacheMetricsService background task stopped");
    }

    private async Task UpdateMetricsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cacheService = scope.ServiceProvider.GetRequiredService<CacheManagementService>();

        // ============================================
        // CACHE STORAGE METRICS
        // ============================================
        try
        {
            var cacheInfo = cacheService.GetCacheInfo();
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
        var totalDownloads = await context.Downloads.CountAsync(cancellationToken);
        Interlocked.Exchange(ref _totalDownloads, totalDownloads);

        var totals = await context.Downloads
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalBytes = g.Sum(d => (long?)d.CacheHitBytes + d.CacheMissBytes) ?? 0,
                HitBytes = g.Sum(d => (long?)d.CacheHitBytes) ?? 0,
                MissBytes = g.Sum(d => (long?)d.CacheMissBytes) ?? 0,
                MaxSize = g.Max(d => (long?)d.CacheHitBytes + d.CacheMissBytes) ?? 0
            })
            .FirstOrDefaultAsync(cancellationToken);

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

        // ============================================
        // ACTIVITY METRICS (currently active)
        // ============================================
        var fiveMinutesAgo = DateTime.UtcNow.AddMinutes(-5);

        // Use IsActive flag for truly active downloads
        var activeDownloadsData = await context.Downloads
            .Where(d => d.IsActive || d.EndTimeUtc >= fiveMinutesAgo)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Count = g.Count(),
                UniqueClients = g.Select(d => d.ClientIp).Distinct().Count(),
                BytesInProgress = g.Sum(d => (long?)d.CacheHitBytes + d.CacheMissBytes) ?? 0
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (activeDownloadsData != null)
        {
            Interlocked.Exchange(ref _activeDownloads, activeDownloadsData.Count);
            Interlocked.Exchange(ref _activeClients, activeDownloadsData.UniqueClients);
        }

        // Calculate throughput from recent downloads (last minute)
        var oneMinuteAgo = DateTime.UtcNow.AddMinutes(-1);
        var recentBytes = await context.Downloads
            .Where(d => d.EndTimeUtc >= oneMinuteAgo)
            .SumAsync(d => (long?)d.CacheHitBytes + d.CacheMissBytes, cancellationToken) ?? 0;
        Interlocked.Exchange(ref _currentBytesPerSecond, recentBytes / 60);

        // ============================================
        // PER-SERVICE METRICS
        // ============================================
        var serviceStats = await context.Downloads
            .GroupBy(d => d.Service.ToLower())
            .Select(g => new
            {
                Service = g.Key,
                TotalBytes = g.Sum(d => (long?)d.CacheHitBytes + d.CacheMissBytes) ?? 0,
                HitBytes = g.Sum(d => (long?)d.CacheHitBytes) ?? 0,
                MissBytes = g.Sum(d => (long?)d.CacheMissBytes) ?? 0,
                Downloads = g.LongCount(),
                ActiveCount = g.Count(d => d.IsActive || d.EndTimeUtc >= fiveMinutesAgo)
            })
            .ToListAsync(cancellationToken);

        // Clear old services that no longer exist
        var currentServices = serviceStats.Select(s => s.Service).ToHashSet();
        foreach (var key in _serviceMetrics.Keys.ToList())
        {
            if (!currentServices.Contains(key))
            {
                _serviceMetrics.TryRemove(key, out _);
            }
        }

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
        var topClients = await context.Downloads
            .GroupBy(d => d.ClientIp)
            .Select(g => new
            {
                ClientIp = g.Key,
                TotalBytes = g.Sum(d => (long?)d.CacheHitBytes + d.CacheMissBytes) ?? 0,
                HitBytes = g.Sum(d => (long?)d.CacheHitBytes) ?? 0,
                MissBytes = g.Sum(d => (long?)d.CacheMissBytes) ?? 0,
                Downloads = g.LongCount()
            })
            .OrderByDescending(c => c.TotalBytes)
            .Take(10)
            .ToListAsync(cancellationToken);

        // Clear old clients
        var currentClients = topClients.Select(c => c.ClientIp).ToHashSet();
        foreach (var key in _clientMetrics.Keys.ToList())
        {
            if (!currentClients.Contains(key))
            {
                _clientMetrics.TryRemove(key, out _);
            }
        }

        // Update client metrics
        foreach (var client in topClients)
        {
            var metrics = _clientMetrics.GetOrAdd(client.ClientIp, _ => new ClientMetrics());
            Interlocked.Exchange(ref metrics.TotalBytes, client.TotalBytes);
            Interlocked.Exchange(ref metrics.HitBytes, client.HitBytes);
            Interlocked.Exchange(ref metrics.MissBytes, client.MissBytes);
            Interlocked.Exchange(ref metrics.Downloads, client.Downloads);
        }
    }
}
