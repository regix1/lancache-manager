namespace LancacheManager.Models;

public class CacheInfo
{
    public long TotalCacheSize { get; set; }
    public long UsedCacheSize { get; set; }
    public long FreeCacheSize { get; set; }
    public double UsagePercent => TotalCacheSize > 0 ? (UsedCacheSize * 100.0) / TotalCacheSize : 0;
    public int TotalFiles { get; set; }
    public Dictionary<string, long> ServiceSizes { get; set; } = new();
}