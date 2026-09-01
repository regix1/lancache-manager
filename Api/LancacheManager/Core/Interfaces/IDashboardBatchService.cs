using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Shared compute service behind <c>GET /api/dashboard/batch</c>. Lives as a singleton so
/// a background warmer can pre-populate the underlying IMemoryCache on startup and so the
/// controller becomes a thin pass-through.
/// </summary>
public interface IDashboardBatchService
{
    /// <summary>
    /// Every dashboard data set for one time range, computed in parallel and cached per range.
    /// </summary>
    /// <param name="timeZoneId">
    /// The IANA zone the hourly buckets are grouped on. Named rather than sent as one offset, so
    /// the database resolves it at each row's own instant. Null or unknown falls back to the zone
    /// the server reports as its own.
    /// </param>
    /// <param name="includeClientHostnames">
    /// Whether this reader may be shown the reverse-DNS name of each client instead of its address.
    /// Part of the cache key rather than a filter on the way out: one cached body is handed to
    /// every reader, and a name arrives in the same field as a nickname, so a reader who may not
    /// see names has to be served a body that never carried any.
    /// </param>
    /// <param name="service">
    /// Narrows the filtered download totals and the recent slice to one service, named by the
    /// folded key the dropdown uses, so an "xbox" selection covers every raw alias behind it. Null
    /// or "all" leaves them over every service. The plain download totals ignore this. Sits after
    /// the token because the startup warmer asks for the unfiltered entry and passes neither.
    /// </param>
    /// <param name="client">
    /// Narrows the same two sections to the given client addresses, comma-separated because a
    /// dropdown entry can name a client group covering several. Null or "all" leaves them over
    /// every client.
    /// </param>
    Task<DashboardBatchResponse> GetBatchAsync(
        long? startTime,
        long? endTime,
        long? eventId,
        string? timeZoneId,
        bool includeClientHostnames,
        CancellationToken ct,
        string? service = null,
        string? client = null);

    Task<EventCompareResponse> GetEventCompareAsync(
        IReadOnlyList<long> eventIds,
        CancellationToken ct);

    /// <summary>
    /// Immediately expire all cached LIVE batch responses (every evicted-mode key variant) so the
    /// next read re-queries the DB. Call after a write that changes live download data.
    /// </summary>
    void InvalidateLiveCache();

    /// <summary>
    /// Invalidates the detection slice in every cached batch response, including fixed ranges.
    /// </summary>
    void InvalidateDetectionCache();

    /// <summary>
    /// Invalidates every dashboard batch variant after a successful cache clear.
    /// Cache totals and detection-derived slices can both change.
    /// </summary>
    void InvalidateAllCache();
}
