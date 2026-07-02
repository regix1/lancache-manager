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

    public static IQueryable<Download> ApplyHiddenClientFilter(this IQueryable<Download> query, List<string> hiddenClientIps, bool excludePrefill = true)
    {
        query = query.ApplyPrefillFilter(excludePrefill);

        if (hiddenClientIps.Count == 0)
        {
            return query;
        }

        return query.Where(d => !hiddenClientIps.Contains(d.ClientIp));
    }

    /// <summary>
    /// Excludes prefill-daemon traffic (Datasource/ClientIp == "prefill", tagged at ingest via the
    /// referer marker) from the query. <paramref name="excludePrefill"/> defaults to true so every
    /// existing call site keeps today's behavior; stats surfaces pass the
    /// <c>ExcludePrefillTrafficFromStats</c> setting and the downloads list passes
    /// <c>!showPrefillTraffic</c> so prefill rows can be re-included on demand.
    /// </summary>
    public static IQueryable<Download> ApplyPrefillFilter(this IQueryable<Download> query, bool excludePrefill = true)
    {
        if (!excludePrefill)
        {
            return query;
        }

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
