namespace LancacheManager.Core.Services;

/// <summary>
/// Summary of cache snapshots for a time range.
/// </summary>
public class CacheSnapshotSummary
{
    public long StartUsedSize { get; set; }
    public long EndUsedSize { get; set; }
    public long AverageUsedSize { get; set; }
    public long TotalCacheSize { get; set; }
    public int SnapshotCount { get; set; }
    public bool IsEstimate { get; set; }
}
