namespace LancacheManager.Models;

public class Download
{
    public int Id { get; set; }
    public string Service { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string App { get; set; } = "unknown";
    public string Depot { get; set; } = "unknown";
    public string ClientIp { get; set; } = string.Empty;
    public long CacheHitBytes { get; set; }
    public long CacheMissBytes { get; set; }
    public double CacheHitPercent => TotalBytes > 0 ? (CacheHitBytes * 100.0) / TotalBytes : 0;
    public long TotalBytes => CacheHitBytes + CacheMissBytes;
    public bool IsActive { get; set; }
    public string Status { get; set; } = "In Progress";
}