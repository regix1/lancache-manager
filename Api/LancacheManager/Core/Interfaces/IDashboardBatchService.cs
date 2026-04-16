using LancacheManager.Models.Responses;

namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Shared compute service behind <c>GET /api/dashboard/batch</c>. Lives as a singleton so
/// a background warmer can pre-populate the underlying IMemoryCache on startup and so the
/// controller becomes a thin pass-through.
/// </summary>
public interface IDashboardBatchService
{
    Task<DashboardBatchResponse> GetBatchAsync(
        long? startTime,
        long? endTime,
        long? eventId,
        CancellationToken ct);
}
