namespace LancacheManager.Models;

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

    /// <summary>
    /// Byte and row totals over every download in the requested range, ignoring the service and
    /// client filters. Null when that sub-query failed.
    /// </summary>
    public object? DownloadTotals { get; set; }

    /// <summary>
    /// The same totals narrowed by the service and client filters the request carried. Null when
    /// that sub-query failed.
    /// </summary>
    public object? FilteredDownloadTotals { get; set; }

    /// <summary>Every service the download filters can offer. Null when that sub-query failed.</summary>
    public object? ServiceOptions { get; set; }

    /// <summary>Every client address the download filters can offer. Null when that sub-query failed.</summary>
    public object? ClientOptions { get; set; }

    /// <summary>The bounded recent-activity slice. Null when that sub-query failed.</summary>
    public object? RecentDownloads { get; set; }

    /// <summary>Game/service detection results. Null when that sub-query failed.</summary>
    public object? Detection { get; set; }

    /// <summary>Sparkline series for the requested range. Null when that sub-query failed.</summary>
    public object? Sparklines { get; set; }

    /// <summary>Hourly activity buckets for the requested range. Null when that sub-query failed.</summary>
    public object? HourlyActivity { get; set; }

    /// <summary>Point-in-time cache snapshot. Null when that sub-query failed.</summary>
    public object? CacheSnapshot { get; set; }
}

/// <summary>
/// What the download surfaces used to add up row by row over the whole table. Hit and miss bytes
/// stay separate because the hit rate is drawn from both, and the count travels with them because
/// it comes out of the same GROUP BY rather than a second round trip. Sent twice, once over
/// everything and once narrowed by the filters, because the two readers sit on different pages and
/// one number cannot answer both.
/// </summary>
public class DownloadTotals
{
    public long CacheHitBytes { get; set; }
    public long CacheMissBytes { get; set; }
    public int Count { get; set; }
}

/// <summary>
/// One entry of the service filter dropdown. The name is the raw one the log parser wrote, never
/// the folded display name: the client folds aliases itself and shows a folded group whenever any
/// of its members qualifies, so folding here would decide that for it.
/// </summary>
public class ServiceFilterOption
{
    public string Service { get; set; } = string.Empty;

    /// <summary>
    /// Whether this service ever cached a download over a megabyte. False marks a service that
    /// only ever carried metadata, which the dropdown lists below its own divider.
    /// </summary>
    public bool HasLargeFiles { get; set; }
}
