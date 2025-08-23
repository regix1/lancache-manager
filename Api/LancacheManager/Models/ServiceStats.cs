namespace LancacheManager.Models;

public class ServiceStats
{
    public string Service { get; set; } = string.Empty;
    public long TotalCacheHitBytes { get; set; }
    public long TotalCacheMissBytes { get; set; }
    public long TotalBytes => TotalCacheHitBytes + TotalCacheMissBytes;
    public double CacheHitPercent => TotalBytes > 0 ? (TotalCacheHitBytes * 100.0) / TotalBytes : 0;
    public int TotalDownloads { get; set; }
    public DateTime LastActivity { get; set; }
}