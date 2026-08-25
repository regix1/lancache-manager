namespace LancacheManager.Models;

/// <summary>
/// Response for dashboard stats
/// </summary>
public class DashboardStatsResponse
{
    // All-time metrics
    public long TotalBandwidthSaved { get; set; }
    public long TotalAddedToCache { get; set; }
    public long TotalServed { get; set; }
    public double CacheHitRatio { get; set; }

    // Current status
    public int ActiveDownloads { get; set; }
    public int UniqueClients { get; set; }
    public string TopService { get; set; } = string.Empty;

    // Period-specific metrics
    public DashboardPeriodStats Period { get; set; } = new();

    // Service breakdown
    public List<ServiceBreakdownItem> ServiceBreakdown { get; set; } = new();

    public DateTime LastUpdated { get; set; }
}

/// <summary>
/// Period-specific stats for dashboard
/// </summary>
public class DashboardPeriodStats
{
    public string Duration { get; set; } = string.Empty;

    /// <summary>
    /// Start of the queried period, mirrored from the request's startTime. Null when the request
    /// carried no time filter, in which case Duration is "all" and the metrics cover every download.
    /// </summary>
    public DateTime? Since { get; set; }
    public long BandwidthSaved { get; set; }
    public long AddedToCache { get; set; }
    public long TotalServed { get; set; }
    public double HitRatio { get; set; }
    public int Downloads { get; set; }
}

/// <summary>
/// Service breakdown item for dashboard
/// </summary>
public class ServiceBreakdownItem
{
    public string Service { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public double Percentage { get; set; }
}

/// <summary>
/// Response for hourly activity data (Peak Usage Hours widget)
/// </summary>
public class HourlyActivityResponse
{
    /// <summary>
    /// Activity data for each hour of the day (0-23)
    /// </summary>
    public List<HourlyActivityItem> Hours { get; set; } = new();

    /// <summary>
    /// Hour with the most downloads (0-23)
    /// </summary>
    public int PeakHour { get; set; }

    /// <summary>
    /// Total downloads in the period
    /// </summary>
    public int TotalDownloads { get; set; }

    /// <summary>
    /// Total bytes served in the period
    /// </summary>
    public long TotalBytesServed { get; set; }

    /// <summary>
    /// Days the period spans, rounded up: the divisor behind the per-hour averages
    /// </summary>
    public int DaysInPeriod { get; set; } = 1;

    /// <summary>
    /// Start of the data range (Unix timestamp)
    /// </summary>
    public long? PeriodStart { get; set; }

    /// <summary>
    /// End of the data range (Unix timestamp)
    /// </summary>
    public long? PeriodEnd { get; set; }

    /// <summary>
    /// Time period for this data
    /// </summary>
    public string Period { get; set; } = string.Empty;
}

/// <summary>
/// Activity data for a single hour
/// </summary>
public class HourlyActivityItem
{
    /// <summary>
    /// Hour of day (0-23)
    /// </summary>
    public int Hour { get; set; }

    /// <summary>
    /// Number of downloads that started in this hour (total across all days in period)
    /// </summary>
    public int Downloads { get; set; }

    /// <summary>
    /// Average downloads per day for this hour (Downloads / DaysInPeriod)
    /// </summary>
    public double AvgDownloads { get; set; }

    /// <summary>
    /// Bytes served during this hour across all days in the period. A download's bytes are spread
    /// over the hours it was active and clipped to the selected range, not credited whole to the
    /// hour it started in.
    /// </summary>
    public long BytesServed { get; set; }

    /// <summary>
    /// Average bytes served per day for this hour (BytesServed / DaysInPeriod)
    /// </summary>
    public long AvgBytesServed { get; set; }

    /// <summary>
    /// Cache hit share of <see cref="BytesServed"/>
    /// </summary>
    public long CacheHitBytes { get; set; }

    /// <summary>
    /// Cache miss share of <see cref="BytesServed"/>
    /// </summary>
    public long CacheMissBytes { get; set; }
}

/// <summary>
/// Response containing sparkline data for dashboard stat cards
/// </summary>
public class SparklineDataResponse
{
    /// <summary>
    /// Sparkline data for bandwidth saved metric
    /// </summary>
    public SparklineMetric BandwidthSaved { get; set; } = new();

    /// <summary>
    /// Sparkline data for cache hit ratio metric
    /// </summary>
    public SparklineMetric CacheHitRatio { get; set; } = new();

    /// <summary>
    /// Sparkline data for total served metric
    /// </summary>
    public SparklineMetric TotalServed { get; set; } = new();

    /// <summary>
    /// Sparkline data for added to cache metric
    /// </summary>
    public SparklineMetric AddedToCache { get; set; } = new();

    /// <summary>
    /// Time period for this data
    /// </summary>
    public string Period { get; set; } = string.Empty;

    /// <summary>
    /// UTC bucket width used to build the series. 15, 30, 60, 180, or 1440.
    /// </summary>
    public int BucketMinutes { get; set; }

    /// <summary>
    /// Unix seconds (UTC) for each point, in the same order as the metric arrays.
    /// </summary>
    public List<long> BucketStarts { get; set; } = new();
}

/// <summary>
/// Sparkline data for a single metric
/// </summary>
public class SparklineMetric
{
    /// <summary>
    /// Actual data points for the sparkline (values only, ordered by time).
    /// </summary>
    public List<double> Data { get; set; } = new();

    /// <summary>
    /// Trend direction: up, down, or stable.
    /// Based on comparing recent values to earlier values.
    /// </summary>
    public string Trend { get; set; } = "stable";
}

/// <summary>
/// Response for cache snapshot summary
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
    public DateTime? NextSnapshotUtc { get; set; }
}

/// <summary>
/// Response for stats exclusions.
/// <see cref="Ips"/> is the legacy stats-excluded-only list kept for backward compatibility.
/// <see cref="Rules"/> is the full mode-aware rule set (hide + exclude).
/// </summary>
public class StatsExclusionsResponse
{
    public List<string> Ips { get; set; } = new();
    public List<ClientExclusionRule> Rules { get; set; } = new();
}

/// <summary>
/// Response for eviction settings (display mode, scan notifications, orphan pruning).
/// </summary>
public class EvictionSettingsResponse
{
    public string EvictedDataMode { get; set; } = string.Empty;
    public bool EvictionScanNotifications { get; set; }
    public bool PruneOrphanedDownloads { get; set; }
}

/// <summary>
/// Response for a manually started eviction scan.
/// </summary>
public class EvictionScanStartedResponse
{
    /// <summary>
    /// The started scan's operation id. Null when the scan could not start immediately and was
    /// parked in the wait-queue instead (the queued-operation response is returned in that case).
    /// </summary>
    public Guid? OperationId { get; set; }
}

/// <summary>
/// Response for clearing the evicted flag on every download.
/// </summary>
public class EvictionResetResponse
{
    public int Reset { get; set; }
}

/// <summary>
/// Response for the eviction scan's current progress.
/// </summary>
public class EvictionScanStatusResponse
{
    public bool IsProcessing { get; set; }
    public bool SilentMode { get; set; }
    public bool ShowNotification { get; set; }
    public OperationStatus Status { get; set; }
    public double PercentComplete { get; set; }
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// i18n key for the current progress stage. Null when no scan is currently running.
    /// </summary>
    public string? StageKey { get; set; }

    /// <summary>
    /// Placeholder values (e.g. totalProcessed/totalEstimate) for <see cref="StageKey"/>. Null when
    /// no scan is currently running.
    /// </summary>
    public object? Context { get; set; }

    /// <summary>
    /// The running scan's operation id. Null when no scan is currently running.
    /// </summary>
    public Guid? OperationId { get; set; }
}
