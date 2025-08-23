namespace LancacheManager.Models;

public class Download
{
    public int Id { get; set; }
    public string Service { get; set; } = string.Empty;
    public string ClientIp { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public long CacheHitBytes { get; set; }
    public long CacheMissBytes { get; set; }
    public long TotalBytes => CacheHitBytes + CacheMissBytes;
    public double CacheHitPercent => TotalBytes > 0 ? (CacheHitBytes * 100.0) / TotalBytes : 0;
    public bool IsActive { get; set; }
}