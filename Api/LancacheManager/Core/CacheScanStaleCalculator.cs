namespace LancacheManager.Core;

/// <summary>
/// Shared logic for deciding when cache scan data is likely outdated relative to
/// live mount-point usage. Used for game detection totals, cache file scans, and rescan triggers.
/// </summary>
public static class CacheScanStaleCalculator
{
    /// <summary>Allow scan totals up to 2% above live used space before flagging stale.</summary>
    public const double ToleranceRatio = 0.02;

    /// <summary>Minimum mount-usage drift before flagging/rescanning regardless of ratio.</summary>
    public const long MinimumUsageDriftBytes = 50L * 1024 * 1024 * 1024;

    /// <summary>
    /// Returns true when any scan-derived cache metric is likely outdated vs live mount usage.
    /// </summary>
    /// <param name="currentUsedBytes">Live mount-point used space.</param>
    /// <param name="scanAggregateBytes">
    /// Optional deduplicated game-detection total to compare against <paramref name="currentUsedBytes"/>.
    /// </param>
    /// <param name="usedBytesAtScan">
    /// Optional mount usage recorded when the cache file scan last ran.
    /// </param>
    public static bool IsAnyScanStale(
        long currentUsedBytes,
        long? scanAggregateBytes = null,
        long? usedBytesAtScan = null)
    {
        if (scanAggregateBytes is long aggregate && IsScanAggregateStale(aggregate, currentUsedBytes))
        {
            return true;
        }

        if (usedBytesAtScan is long baseline && IsUsageDriftStale(currentUsedBytes, baseline))
        {
            return true;
        }

        return false;
    }

    private static bool IsScanAggregateStale(long scanAggregateBytes, long currentUsedBytes)
    {
        if (currentUsedBytes <= 0)
        {
            return false;
        }

        return scanAggregateBytes > currentUsedBytes * (1 + ToleranceRatio);
    }

    private static bool IsUsageDriftStale(long currentUsedBytes, long baselineUsedBytes)
    {
        if (baselineUsedBytes <= 0 && currentUsedBytes <= 0)
        {
            return false;
        }

        var baseline = Math.Max(baselineUsedBytes, 1L);
        var delta = Math.Abs(currentUsedBytes - baselineUsedBytes);
        var ratioThreshold = (long)(baseline * ToleranceRatio);
        return delta >= Math.Max(ratioThreshold, MinimumUsageDriftBytes);
    }
}
