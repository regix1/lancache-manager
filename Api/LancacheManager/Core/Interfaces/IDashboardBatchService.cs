using LancacheManager.Models.Responses;

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
    /// the database resolves it at each row's own instant. Null or unknown keeps the server clock.
    /// </param>
    Task<DashboardBatchResponse> GetBatchAsync(
        long? startTime,
        long? endTime,
        long? eventId,
        string? timeZoneId,
        CancellationToken ct);

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
