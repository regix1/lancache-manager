namespace LancacheManager.Models;

public class CacheInfo
{
    /// <summary>
    /// The effective cache size limit (ConfiguredCacheSize if set, otherwise DriveCapacity)
    /// </summary>
    public long TotalCacheSize { get; set; }
    
    /// <summary>
    /// The configured cache size from CACHE_DISK_SIZE in .env file (0 if not configured)
    /// </summary>
    public long ConfiguredCacheSize { get; set; }
    
    /// <summary>
    /// The actual physical drive capacity
    /// </summary>
    public long DriveCapacity { get; set; }
    
    public long UsedCacheSize { get; set; }
    public long FreeCacheSize { get; set; }
    public double UsagePercent => TotalCacheSize > 0 ? (UsedCacheSize * 100.0) / TotalCacheSize : 0;
    public long TotalFiles { get; set; }
    public Dictionary<string, long> ServiceSizes { get; set; } = new();

    /// <summary>
    /// UTC timestamp of the last Rust cache-size scan used for <see cref="TotalFiles"/>.
    /// </summary>
    public DateTime? CacheScanTimestampUtc { get; set; }

    /// <summary>
    /// True when cache scan data (file scan or game detection) is likely outdated vs live mount usage.
    /// </summary>
    public bool ScanMayBeStale { get; set; }
}
