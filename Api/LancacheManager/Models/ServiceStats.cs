using System.Text.Json.Serialization;

namespace LancacheManager.Models;

public class ServiceStats
{
    public string Service { get; set; } = string.Empty;
    public long TotalCacheHitBytes { get; set; }
    public long TotalCacheMissBytes { get; set; }
    [JsonInclude]
    public long TotalBytes => TotalCacheHitBytes + TotalCacheMissBytes;

    [JsonInclude]
    public double CacheHitPercent => TotalBytes > 0 ? (TotalCacheHitBytes * 100.0) / TotalBytes : 0;
    public int TotalDownloads { get; set; }

    // UTC timestamps - always stored in UTC for consistent querying
    public DateTime LastActivityUtc { get; set; }

    // Local timestamps - stored in the user's configured timezone for display
    public DateTime LastActivityLocal { get; set; }
}