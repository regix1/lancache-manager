namespace LancacheManager.Models;

public class GameDownloadDetails
{
    public int DownloadId { get; set; }
    public string Service { get; set; } = string.Empty;
    public uint? AppId { get; set; }
    public string GameName { get; set; } = "Unknown";
    public string? GameType { get; set; }
    public string? HeaderImage { get; set; }
    public string? Description { get; set; }
    public long TotalBytes { get; set; }
    public long CacheHitBytes { get; set; }
    public long CacheMissBytes { get; set; }
    public double CacheHitPercent { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string ClientIp { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}