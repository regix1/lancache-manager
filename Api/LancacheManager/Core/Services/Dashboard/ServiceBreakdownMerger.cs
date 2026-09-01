using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

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
    /// Every raw <c>LogEntries.Service</c> value that folds to the canonical "xbox" display
    /// name. Used to expand a folded "xbox" filter into a raw-value IN-list for EF queries,
    /// since LINQ can't translate <see cref="NormalizeXboxService"/> itself into SQL.
    /// </summary>
    public static readonly IReadOnlyList<string> XboxRawServiceNames =
        new[] { XboxCanonical }.Concat(_xboxAliases).ToList();

    /// <summary>
    /// Folds xboxlive/microsoft to the canonical "xbox" display name; any other service name
    /// passes through unchanged. Display-only - never write the result back to LogEntries.Service.
    /// </summary>
    public static string NormalizeXboxService(string service)
    {
        return _xboxAliases.Contains(service) ? XboxCanonical : service;
    }

    /// <summary>
    /// The title the grouped Downloads views put on a nameless service's row: "Epic Games" for
    /// Epic, and for every other service the capitalized service name followed by "Downloads".
    /// Mirrors Web/src/components/features/downloads/DownloadsTab.tsx so an alphabetical sort here
    /// orders what the reader sees rather than the bare key the row carries. The browser translates
    /// the "Downloads" half, so the two orders still part company outside English.
    /// </summary>
    public static string ServiceGroupTitle(string service)
    {
        var display = NormalizeXboxService(service);
        if (string.Equals(display, "epicgames", StringComparison.OrdinalIgnoreCase))
        {
            return "Epic Games";
        }

        // Download.Service defaults to the empty string, so a row can reach here with nothing to
        // capitalize.
        var capitalized = display.Length > 0
            ? char.ToUpperInvariant(display[0]) + display[1..]
            : display;
        return $"{capitalized} Downloads";
    }

    /// <summary>
    /// The per-service byte breakdown for a period: grouped in SQL over the given downloads
    /// query, placeholder services filtered, xbox aliases folded. The one shared query behind
    /// the dashboard batch and the stats endpoint.
    /// </summary>
    public static async Task<List<ServiceBreakdownItem>> QueryMergedAsync(
        IQueryable<Download> downloadsQuery,
        long periodTotal,
        CancellationToken ct)
    {
        return MergeXboxRows(await downloadsQuery
            .ApplyPlaceholderServiceFilter()
            .GroupBy(d => d.Service)
            .Select(g => new ServiceBreakdownItem
            {
                Service = g.Key,
                Bytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                Percentage = periodTotal > 0
                    ? (g.Sum(d => d.CacheHitBytes + d.CacheMissBytes) * 100.0) / periodTotal
                    : 0
            })
            .OrderByDescending(s => s.Bytes)
            .ToListAsync(ct));
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
            LastActivityUtc = toMerge.Max(r => r.LastActivityUtc)
        });

        return rest.OrderByDescending(r => r.TotalCacheHitBytes + r.TotalCacheMissBytes).ToList();
    }
}
