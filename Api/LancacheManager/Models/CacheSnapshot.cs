namespace LancacheManager.Models;

/// <summary>
/// Periodic snapshot of cache size for historical tracking.
/// Allows showing estimated used space for past time periods.
/// </summary>
public class CacheSnapshot
{
    public int Id { get; set; }

    /// <summary>
    /// Timestamp when this snapshot was recorded (UTC)
    /// </summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>
    /// Used cache size in bytes at the time of snapshot
    /// </summary>
    public long UsedCacheSize { get; set; }

    /// <summary>
    /// Total cache capacity in bytes at the time of snapshot
    /// </summary>
    public long TotalCacheSize { get; set; }
}
