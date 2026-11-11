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

    /// <summary>
    /// Drops the rows the client settings keep off a reader: downloads from hidden clients, from
    /// clients excluded from stats only (they stay visible in lists), and prefill rows a prefill rule
    /// covers (its address, or any address when empty; its services, or every service when empty).
    /// A reader passes the lists it applies and <c>[]</c> for each part it does not. Added to cache
    /// figures pass no prefill rules: a prefill still fills the cache.
    /// </summary>
    public static IQueryable<Download> ApplyClientFilters(
        this IQueryable<Download> query,
        List<string> hiddenClientIps,
        List<string> statsExcludedOnlyIps,
        List<ClientExclusionRule> prefillRules)
    {
        if (hiddenClientIps.Count > 0)
        {
            query = query.Where(d => !hiddenClientIps.Contains(d.ClientIp));
        }

        if (statsExcludedOnlyIps.Count > 0)
        {
            query = query.Where(d => !statsExcludedOnlyIps.Contains(d.ClientIp));
        }

        foreach (var rule in prefillRules)
        {
            var ip = rule.Ip;
            var services = rule.Services.SelectMany(platform => platform.ToRawServices()).ToList();
            query = query.Where(d => !(d.IsPrefill && (ip == "" || d.ClientIp == ip) && (services.Count == 0 || services.Contains(d.Service))));
        }
        return query;
    }

    /// <summary>
    /// Bounds a query to an optional unix-seconds window. A download is in the window when it was
    /// active in it; the start-time arm keeps rows whose end was never set. This matches the
    /// sparkline, hourly, and event-compare queries. Either bound may be absent.
    /// </summary>
    public static IQueryable<Download> ApplyTimeRange(this IQueryable<Download> query, long? startTime, long? endTime)
    {
        if (startTime.HasValue)
        {
            var startDate = startTime.Value.FromUnixSeconds();
            query = query.Where(d => d.EndTimeUtc >= startDate || d.StartTimeUtc >= startDate);
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
