namespace LancacheManager.Core.Services;

/// <summary>
/// Summary info about a cached app.
/// </summary>
public class CachedAppInfo
{
    public string AppId { get; set; } = string.Empty;
    public string? AppName { get; set; }
    public int DepotCount { get; set; }
    public long TotalBytes { get; set; }
    public DateTime CachedAtUtc { get; set; }
    public string? CachedBy { get; set; }
}
