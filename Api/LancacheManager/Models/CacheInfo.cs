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
    public int TotalFiles { get; set; }
    public Dictionary<string, long> ServiceSizes { get; set; } = new();
}
