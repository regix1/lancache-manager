using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;

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
    /// Number of distinct days in the queried period
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
    /// Total bytes served in this hour (total across all days in period)
    /// </summary>
    public long BytesServed { get; set; }

    /// <summary>
    /// Average bytes served per day for this hour
    /// </summary>
    public long AvgBytesServed { get; set; }

    /// <summary>
    /// Cache hit bytes in this hour
    /// </summary>
    public long CacheHitBytes { get; set; }

    /// <summary>
    /// Cache miss bytes in this hour
    /// </summary>
    public long CacheMissBytes { get; set; }
}

/// <summary>
/// Response for cache growth data over time
/// </summary>
public class CacheGrowthResponse
{
    /// <summary>
    /// Data points showing cache growth over time
    /// </summary>
    public List<CacheGrowthDataPoint> DataPoints { get; set; } = new();

    /// <summary>
    /// Current total cache size (used space)
    /// </summary>
    public long CurrentCacheSize { get; set; }

    /// <summary>
    /// Total cache capacity
    /// </summary>
    public long TotalCapacity { get; set; }

    /// <summary>
    /// Average daily growth in bytes
    /// </summary>
    public long AverageDailyGrowth { get; set; }

    /// <summary>
    /// Trend direction: up, down, or stable
    /// </summary>
    public string Trend { get; set; } = "stable";

    /// <summary>
    /// Percentage change over the period
    /// </summary>
    public double PercentChange { get; set; }

    /// <summary>
    /// Estimated days until cache is full (null if not growing or already full)
    /// </summary>
    public int? EstimatedDaysUntilFull { get; set; }

    /// <summary>
    /// Time period for this data
    /// </summary>
    public string Period { get; set; } = string.Empty;

    /// <summary>
    /// True if the actual cache size is less than cumulative downloads,
    /// indicating data was deleted (cache was cleared/cleaned)
    /// </summary>
    public bool HasDataDeletion { get; set; }

    /// <summary>
    /// Estimated bytes that were deleted from cache
    /// (difference between cumulative downloads and actual cache size)
    /// </summary>
    public long EstimatedBytesDeleted { get; set; }

    /// <summary>
    /// Net average daily growth (accounting for deletions)
    /// Can be negative if cache is shrinking
    /// </summary>
    public long NetAverageDailyGrowth { get; set; }

    /// <summary>
    /// True if the cache was essentially cleared (very small relative to historical downloads).
    /// When true, percentChange is not meaningful and growth rate shows download rate.
    /// </summary>
    public bool CacheWasCleared { get; set; }
}

/// <summary>
/// Single data point for cache growth
/// </summary>
public class CacheGrowthDataPoint : IUtcMarkable
{
    /// <summary>
    /// Timestamp for this data point
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Cumulative cache miss bytes (new data added) up to this point
    /// </summary>
    public long CumulativeCacheMissBytes { get; set; }

    /// <summary>
    /// Growth from previous data point
    /// </summary>
    public long GrowthFromPrevious { get; set; }

    public void MarkDateTimesAsUtc()
    {
        Timestamp = Timestamp.AsUtc();
    }
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
}

/// <summary>
/// Response for stats exclusions
/// </summary>
public class StatsExclusionsResponse
{
    public List<string> Ips { get; set; } = new();
}
