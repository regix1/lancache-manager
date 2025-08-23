namespace LancacheManager.Models;

public class GameStats
{
    public string GameId { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public long TotalCacheHitBytes { get; set; }
    public long TotalCacheMissBytes { get; set; }
    public long TotalBytes => TotalCacheHitBytes + TotalCacheMissBytes;
    public double CacheHitPercent => TotalBytes > 0 ? (TotalCacheHitBytes * 100.0) / TotalBytes : 0;
    public int DownloadCount { get; set; }
    public DateTime LastDownloaded { get; set; }
    public List<string> Clients { get; set; } = new();
}