namespace LancacheManager.Models.Responses;

/// <summary>
/// Response shape for the dashboard batch endpoint.
/// Each field is nullable to support partial failure (null = sub-query failed).
/// </summary>
public class DashboardBatchResponse
{
    /// <summary>Cache size and disk info. Null when that sub-query failed.</summary>
    public CacheInfo? Cache { get; set; }

    /// <summary>Per-client stats for the requested range. Null when that sub-query failed.</summary>
    public object? Clients { get; set; }

    /// <summary>Per-service stats for the requested range. Null when that sub-query failed.</summary>
    public object? Services { get; set; }

    /// <summary>Top-line dashboard totals for the requested range. Null when that sub-query failed.</summary>
    public object? Dashboard { get; set; }

    /// <summary>The download list for the requested range. Null when that sub-query failed.</summary>
    public object? Downloads { get; set; }

    /// <summary>Game/service detection results. Null when that sub-query failed.</summary>
    public object? Detection { get; set; }

    /// <summary>Sparkline series for the requested range. Null when that sub-query failed.</summary>
    public object? Sparklines { get; set; }

    /// <summary>Hourly activity buckets for the requested range. Null when that sub-query failed.</summary>
    public object? HourlyActivity { get; set; }

    /// <summary>Point-in-time cache snapshot. Null when that sub-query failed.</summary>
    public object? CacheSnapshot { get; set; }
}
