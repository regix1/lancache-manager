using LancacheManager.Core.Constants;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Utilities;

public static class DownloadQueryExtensions
{
    public static IQueryable<Download> ApplyEvictedFilter(this IQueryable<Download> query, string evictedMode)
    {
        if (evictedMode == EvictedDataMode.Hide.ToWireString() || evictedMode == EvictedDataMode.Remove.ToWireString())
        {
            return query.Where(d => !d.IsEvicted);
        }
        return query;
    }

    public static IQueryable<Download> ApplyHiddenClientFilter(this IQueryable<Download> query, List<string> hiddenClientIps)
    {
        query = query.ApplyPrefillFilter();

        if (hiddenClientIps.Count == 0)
        {
            return query;
        }

        return query.Where(d => !hiddenClientIps.Contains(d.ClientIp));
    }

    /// <summary>
    /// Hides inactive zero-byte sessions from download lists. These are metadata-only polls or
    /// aborted connections (Windows Update produces them constantly): they carry no transfer
    /// data, contribute nothing to any byte-based aggregation, and are deliberately neutral in
    /// eviction, so a list entry for one is a permanent "0 B" row that matches nothing else in
    /// the UI. Active downloads always pass because every live session starts at zero bytes.
    /// </summary>
    public static IQueryable<Download> ApplyEmptySessionFilter(this IQueryable<Download> query)
    {
        return query.Where(d => d.IsActive || d.CacheHitBytes > 0 || d.CacheMissBytes > 0);
    }

    public static IQueryable<Download> ApplyPrefillFilter(this IQueryable<Download> query)
    {
        return query
            .Where(d => d.ClientIp == null || d.ClientIp.ToLower() != DownloadKindConstants.PrefillToken)
            .Where(d => d.Datasource == null || d.Datasource.ToLower() != DownloadKindConstants.PrefillToken);
    }

    public static IQueryable<Download> ApplyEventFilter(this IQueryable<Download> query, List<long> eventIds, HashSet<long>? eventDownloadIds)
    {
        if (eventIds.Count == 0 || eventDownloadIds == null)
            return query;

        // Filter to only downloads that are tagged to the events
        return query.Where(d => eventDownloadIds.Contains((long)d.Id));
    }
}
