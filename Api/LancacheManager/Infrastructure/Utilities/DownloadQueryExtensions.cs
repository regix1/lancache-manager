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
        if (hiddenClientIps.Count == 0)
        {
            return query;
        }

        return query.Where(d => !hiddenClientIps.Contains(d.ClientIp));
    }

    /// <summary>
    /// Drops downloads from clients excluded from stats calculations only (they stay visible in
    /// lists). Shared by the stats endpoint and the dashboard batch.
    /// </summary>
    public static IQueryable<Download> ApplyStatsExcludedClientFilter(this IQueryable<Download> query, List<string> statsExcludedOnlyIps)
    {
        if (statsExcludedOnlyIps.Count == 0)
        {
            return query;
        }

        return query.Where(d => !statsExcludedOnlyIps.Contains(d.ClientIp));
    }

    /// <summary>
    /// Bounds a query to an optional unix-seconds window on <c>StartTimeUtc</c>. Either bound may
    /// be absent; an absent bound leaves that side open, matching the live/all-time views.
    /// </summary>
    public static IQueryable<Download> ApplyTimeRange(this IQueryable<Download> query, long? startTime, long? endTime)
    {
        if (startTime.HasValue)
        {
            var startDate = startTime.Value.FromUnixSeconds();
            query = query.Where(d => d.StartTimeUtc >= startDate);
        }

        if (endTime.HasValue)
        {
            var endDate = endTime.Value.FromUnixSeconds();
            query = query.Where(d => d.StartTimeUtc <= endDate);
        }

        return query;
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

    /// <summary>
    /// Hides the two service tags that name no service. Both are placeholders the log parser
    /// substitutes for a hostname it could not use: <c>localhost</c> for traffic tagged 127.x, and
    /// <c>ip-address</c> for a client that reached the cache by address instead of by name. Neither
    /// can ever be attributed to files on disk, because a cache filename hashes the service
    /// identifier nginx wrote and these two replace that identifier rather than repeat it, so a
    /// service row for either is a name with nothing behind it.
    ///
    /// Aggregate service rows only. Their individual downloads and their client IPs stay visible,
    /// which is how a client misconfigured to use an address remains noticeable.
    /// </summary>
    public static IQueryable<Download> ApplyPlaceholderServiceFilter(this IQueryable<Download> query)
    {
        return query.Where(d => !_placeholderServices.Contains(d.Service.ToLower()));
    }

    private static readonly string[] _placeholderServices = ["localhost", "ip-address"];

    public static IQueryable<Download> ApplyEventFilter(this IQueryable<Download> query, List<long> eventIds, HashSet<long>? eventDownloadIds)
    {
        if (eventIds.Count == 0 || eventDownloadIds == null)
            return query;

        // Filter to only downloads that are tagged to the events
        return query.Where(d => eventDownloadIds.Contains((long)d.Id));
    }
}
