using System.Diagnostics.Metrics;
using LancacheManager.Data;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Services;

/// <summary>
/// Service for collecting and exposing LanCache-specific metrics to Prometheus/Grafana
/// Uses OpenTelemetry Metrics API for instrumentation
/// </summary>
public class LancacheMetricsService
{
    private readonly Meter _meter;
    private readonly Counter<long> _downloadsTotal;
    private readonly Counter<long> _downloadsByService;
    private readonly Counter<long> _bytesServedTotal;
    private readonly Counter<long> _bytesServedByService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LancacheMetricsService> _logger;

    // Observables for gauges (values that can go up and down)
    private long _cacheSize;
    private int _activeDownloads;
    private int _activeClients;
    private long _totalDownloads;
    private long _totalBytesServed;

    public LancacheMetricsService(IServiceScopeFactory scopeFactory, ILogger<LancacheMetricsService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        // Create a meter for LanCache metrics
        _meter = new Meter("LancacheManager", "1.0.0");

        // Counters - monotonically increasing values
        _downloadsTotal = _meter.CreateCounter<long>(
            "lancache_downloads_total",
            unit: "downloads",
            description: "Total number of downloads processed"
        );

        _downloadsByService = _meter.CreateCounter<long>(
            "lancache_downloads_by_service",
            unit: "downloads",
            description: "Downloads processed per service (steam, epic, etc.)"
        );

        _bytesServedTotal = _meter.CreateCounter<long>(
            "lancache_bytes_served_total",
            unit: "bytes",
            description: "Total bytes served from cache"
        );

        _bytesServedByService = _meter.CreateCounter<long>(
            "lancache_bytes_served_by_service",
            unit: "bytes",
            description: "Bytes served per service (steam, epic, etc.)"
        );

        // Observable Gauges - current state values
        _meter.CreateObservableGauge(
            "lancache_cache_size_bytes",
            () => _cacheSize,
            unit: "bytes",
            description: "Current cache size in bytes"
        );

        _meter.CreateObservableGauge(
            "lancache_active_downloads",
            () => _activeDownloads,
            unit: "downloads",
            description: "Number of currently active downloads"
        );

        _meter.CreateObservableGauge(
            "lancache_active_clients",
            () => _activeClients,
            unit: "clients",
            description: "Number of unique active client IPs"
        );

        _meter.CreateObservableGauge(
            "lancache_total_downloads",
            () => _totalDownloads,
            unit: "downloads",
            description: "Total downloads in database"
        );

        _meter.CreateObservableGauge(
            "lancache_total_bytes_served",
            () => _totalBytesServed,
            unit: "bytes",
            description: "Total bytes served (all time)"
        );

        // Start background task to update gauges
        Task.Run(async () => await UpdateGaugesAsync());
    }

    /// <summary>
    /// Record a new download
    /// </summary>
    public void RecordDownload(string service, long bytes)
    {
        try
        {
            _downloadsTotal.Add(1);
            _downloadsByService.Add(1, new KeyValuePair<string, object?>("service", service));

            if (bytes > 0)
            {
                _bytesServedTotal.Add(bytes);
                _bytesServedByService.Add(bytes, new KeyValuePair<string, object?>("service", service));
            }

            _logger.LogDebug("Recorded download: service={Service}, bytes={Bytes}", service, bytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record download metric");
        }
    }

    /// <summary>
    /// Update cache size metric
    /// </summary>
    public void UpdateCacheSize(long sizeInBytes)
    {
        Interlocked.Exchange(ref _cacheSize, sizeInBytes);
    }

    /// <summary>
    /// Update active downloads count
    /// </summary>
    public void UpdateActiveDownloads(int count)
    {
        Interlocked.Exchange(ref _activeDownloads, count);
    }

    /// <summary>
    /// Update active clients count
    /// </summary>
    public void UpdateActiveClients(int count)
    {
        Interlocked.Exchange(ref _activeClients, count);
    }

    /// <summary>
    /// Background task to periodically update gauge values from database
    /// Runs every 30 seconds to keep metrics fresh
    /// </summary>
    private async Task UpdateGaugesAsync()
    {
        while (true)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30));

                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // Get total downloads count
                var totalDownloads = await context.Downloads.CountAsync();
                Interlocked.Exchange(ref _totalDownloads, totalDownloads);

                // Get total bytes served
                var totalBytes = await context.Downloads.SumAsync(d => (long?)(d.CacheHitBytes + d.CacheMissBytes)) ?? 0;
                Interlocked.Exchange(ref _totalBytesServed, totalBytes);

                // Get active downloads (in last 5 minutes)
                var fiveMinutesAgo = DateTime.UtcNow.AddMinutes(-5);
                var activeCount = await context.Downloads
                    .Where(d => d.EndTimeUtc >= fiveMinutesAgo)
                    .CountAsync();
                Interlocked.Exchange(ref _activeDownloads, activeCount);

                // Get unique client IPs (in last hour)
                var oneHourAgo = DateTime.UtcNow.AddHours(-1);
                var activeClientsCount = await context.Downloads
                    .Where(d => d.EndTimeUtc >= oneHourAgo)
                    .Select(d => d.ClientIp)
                    .Distinct()
                    .CountAsync();
                Interlocked.Exchange(ref _activeClients, activeClientsCount);

                _logger.LogDebug(
                    "Updated metrics: downloads={Downloads}, bytes={Bytes}, active={Active}, clients={Clients}",
                    totalDownloads, totalBytes, activeCount, activeClientsCount
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update gauge metrics");
            }
        }
    }
}
