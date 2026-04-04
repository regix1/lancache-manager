using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Utilities;

public static class DownloadQueryExtensions
{
    private const string PrefillToken = "prefill";

    public static IQueryable<Download> ApplyEvictedFilter(this IQueryable<Download> query, string evictedMode)
    {
        if (evictedMode == EvictedDataModes.Hide || evictedMode == EvictedDataModes.Remove)
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

    public static IQueryable<Download> ApplyPrefillFilter(this IQueryable<Download> query)
    {
        return query
            .Where(d => d.ClientIp == null || d.ClientIp.ToLower() != PrefillToken)
            .Where(d => d.Datasource == null || d.Datasource.ToLower() != PrefillToken);
    }

    public static IQueryable<Download> ApplyEventFilter(this IQueryable<Download> query, List<long> eventIds, HashSet<long>? eventDownloadIds)
    {
        if (eventIds.Count == 0 || eventDownloadIds == null)
            return query;

        // Filter to only downloads that are tagged to the events
        return query.Where(d => eventDownloadIds.Contains((long)d.Id));
    }
}
