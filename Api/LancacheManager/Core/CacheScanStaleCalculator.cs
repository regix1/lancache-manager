namespace LancacheManager.Core;

/// <summary>
/// Shared logic for deciding when mount usage has drifted since the last cache file scan.
/// </summary>
public static class CacheScanStaleCalculator
{
    /// <summary>Relative drift threshold (2% of baseline mount usage).</summary>
    public const double ToleranceRatio = 0.02;

    /// <summary>
    /// Absolute drift threshold for large caches where 2% would be impractically high.
    /// Stale when drift exceeds 2% OR this absolute minimum — whichever is reached first.
    /// </summary>
    public const long MinimumUsageDriftBytes = 50L * 1024 * 1024 * 1024;

    /// <summary>
    /// Returns true when live mount usage has drifted significantly since the last cache file scan.
    /// </summary>
    public static bool IsMountStale(long currentUsedBytes, long? usedBytesAtScan = null)
    {
        if (usedBytesAtScan is long baseline && IsUsageDriftStale(currentUsedBytes, baseline))
        {
            return true;
        }

        return false;
    }

    /// <summary>Backward-compatible alias.</summary>
    public static bool IsAnyScanStale(long currentUsedBytes, long? usedBytesAtScan = null) =>
        IsMountStale(currentUsedBytes, usedBytesAtScan);

    private static bool IsUsageDriftStale(long currentUsedBytes, long baselineUsedBytes)
    {
        if (baselineUsedBytes <= 0 && currentUsedBytes <= 0)
        {
            return false;
        }

        var baseline = Math.Max(baselineUsedBytes, 1L);
        var delta = Math.Abs(currentUsedBytes - baselineUsedBytes);
        var ratioThreshold = (long)(baseline * ToleranceRatio);
        return delta >= ratioThreshold || delta >= MinimumUsageDriftBytes;
    }
}
