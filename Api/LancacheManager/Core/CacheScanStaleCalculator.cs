namespace LancacheManager.Core;

/// <summary>
/// Shared logic for deciding when cache scan data is likely outdated relative to
/// live mount-point usage. Used for cache file scans and rescan triggers.
/// </summary>
public static class CacheScanStaleCalculator
{
    /// <summary>Allow minor timing/rounding differences before flagging stale.</summary>
    public const double ToleranceRatio = 0.02;

    /// <summary>Minimum mount-usage drift before flagging/rescanning regardless of ratio.</summary>
    public const long MinimumUsageDriftBytes = 50L * 1024 * 1024 * 1024;

    /// <summary>
    /// Returns true when mount usage has drifted significantly since the last cache file scan.
    /// </summary>
    /// <param name="currentUsedBytes">Live mount-point used space.</param>
    /// <param name="usedBytesAtScan">Mount usage recorded when the cache file scan last ran.</param>
    public static bool IsAnyScanStale(long currentUsedBytes, long? usedBytesAtScan = null)
    {
        if (usedBytesAtScan is long baseline && IsUsageDriftStale(currentUsedBytes, baseline))
        {
            return true;
        }

        return false;
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
