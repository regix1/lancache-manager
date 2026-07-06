using LancacheManager.Models;

namespace LancacheManager.Core.Services;

/// <summary>
/// Folds <c>xboxlive</c> and <c>microsoft</c> service rows into a single <c>xbox</c> row
/// in per-service breakdown/stats, summing numeric aggregates and re-sorting by bytes descending.
/// <c>wsus</c> is left as its own row (mixed Windows Update traffic).
/// </summary>
internal static class ServiceBreakdownMerger
{
    private static readonly HashSet<string> _xboxAliases =
        new(StringComparer.OrdinalIgnoreCase) { "xboxlive", "microsoft" };

    private const string XboxCanonical = "xbox";

    /// <summary>
    /// Folds xboxlive/microsoft to the canonical "xbox" display name; any other service name
    /// passes through unchanged. Display-only - never write the result back to LogEntries.Service.
    /// </summary>
    public static string NormalizeXboxService(string service)
    {
        return _xboxAliases.Contains(service) ? XboxCanonical : service;
    }

    /// <summary>
    /// Merges xboxlive/microsoft rows into xbox for <see cref="ServiceBreakdownItem"/> lists.
    /// Percentages are summed (valid because all rows share the same period total as denominator).
    /// </summary>
    public static List<ServiceBreakdownItem> MergeXboxRows(List<ServiceBreakdownItem> rows)
    {
        var toMerge = rows
            .Where(r => r.Service.Equals(XboxCanonical, StringComparison.OrdinalIgnoreCase)
                     || _xboxAliases.Contains(r.Service))
            .ToList();

        if (toMerge.Count == 0) return rows;
        if (toMerge.Count == 1 && toMerge[0].Service.Equals(XboxCanonical, StringComparison.OrdinalIgnoreCase))
            return rows;

        var rest = rows
            .Where(r => !r.Service.Equals(XboxCanonical, StringComparison.OrdinalIgnoreCase)
                     && !_xboxAliases.Contains(r.Service))
            .ToList();

        rest.Add(new ServiceBreakdownItem
        {
            Service = XboxCanonical,
            Bytes = toMerge.Sum(r => r.Bytes),
            Percentage = toMerge.Sum(r => r.Percentage)
        });

        return rest.OrderByDescending(r => r.Bytes).ToList();
    }

    /// <summary>
    /// Merges xboxlive/microsoft rows into xbox for <see cref="ServiceStats"/> lists.
    /// Hit/miss bytes and download counts are summed; LastActivity timestamps take the maximum.
    /// </summary>
    public static List<ServiceStats> MergeXboxRows(List<ServiceStats> rows)
    {
        var toMerge = rows
            .Where(r => r.Service.Equals(XboxCanonical, StringComparison.OrdinalIgnoreCase)
                     || _xboxAliases.Contains(r.Service))
            .ToList();

        if (toMerge.Count == 0) return rows;
        if (toMerge.Count == 1 && toMerge[0].Service.Equals(XboxCanonical, StringComparison.OrdinalIgnoreCase))
            return rows;

        var rest = rows
            .Where(r => !r.Service.Equals(XboxCanonical, StringComparison.OrdinalIgnoreCase)
                     && !_xboxAliases.Contains(r.Service))
            .ToList();

        rest.Add(new ServiceStats
        {
            Service = XboxCanonical,
            TotalCacheHitBytes = toMerge.Sum(r => r.TotalCacheHitBytes),
            TotalCacheMissBytes = toMerge.Sum(r => r.TotalCacheMissBytes),
            TotalDownloads = toMerge.Sum(r => r.TotalDownloads),
            LastActivityUtc = toMerge.Max(r => r.LastActivityUtc),
            LastActivityLocal = toMerge.Max(r => r.LastActivityLocal)
        });

        return rest.OrderByDescending(r => r.TotalCacheHitBytes + r.TotalCacheMissBytes).ToList();
    }
}
