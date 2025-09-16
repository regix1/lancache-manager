using System.Text.Json.Serialization;

namespace LancacheManager.Models;

public class ClientStats
{
    public string ClientIp { get; set; } = string.Empty;
    public long TotalCacheHitBytes { get; set; }
    public long TotalCacheMissBytes { get; set; }
    [JsonInclude]
    public long TotalBytes => TotalCacheHitBytes + TotalCacheMissBytes;

    [JsonInclude]
    public double CacheHitPercent => TotalBytes > 0 ? (TotalCacheHitBytes * 100.0) / TotalBytes : 0;
    public int TotalDownloads { get; set; }
    public DateTime LastSeen { get; set; }
}