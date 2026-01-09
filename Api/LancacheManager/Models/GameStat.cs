namespace LancacheManager.Models;

/// <summary>
/// DTO for game statistics results
/// </summary>
public class GameStat
{
    public string GameName { get; set; } = "";
    public int GameAppId { get; set; }
    public int TotalDownloads { get; set; }
    public long TotalBytes { get; set; }
    public long CacheHitBytes { get; set; }
    public long CacheMissBytes { get; set; }
    public int UniqueClients { get; set; }
}
