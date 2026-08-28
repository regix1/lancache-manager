using LancacheManager.Models;

namespace LancacheManager.Core;

/// <summary>
/// Builds <see cref="CachedDetectionResponse"/> using persisted disk-summary totals.
/// </summary>
public static class CachedDetectionResponseBuilder
{
    public static CachedDetectionResponse BuildEmpty() =>
        new() { HasCachedResults = false };

    /// <summary>
    /// Builds a cached detection response for API/dashboard consumers.
    /// </summary>
    /// <param name="slimForDashboard">
    /// When true, projects games/services into slim DTOs (dashboard batch).
    /// When false, returns full <see cref="GameCacheInfo"/> / <see cref="ServiceCacheInfo"/> lists.
    /// </param>
    /// <param name="diskSummary">
    /// Persisted deduplicated on-disk totals from the last detection scan refresh.
    /// </param>
    /// <param name="unmappedServices">
    /// Per-service totals for cache files no detection row claims. Dropped on the slim shape: the
    /// dashboard derives its own unmapped figure by subtraction, and the two are different
    /// quantities that legitimately disagree.
    /// </param>
    public static CachedDetectionResponse Build(
        IReadOnlyList<GameCacheInfo> games,
        IReadOnlyList<ServiceCacheInfo>? services,
        int totalServicesDetected,
        DateTime lastDetectionUtc,
        bool slimForDashboard,
        IdentifiedCacheAggregate? diskSummary,
        DateTime? summaryComputedAtUtc = null,
        bool detectionStale = false,
        IReadOnlyList<UnmappedService>? unmappedServices = null)
    {
        var activeGamesCount = games.Count(g => !g.IsEvicted && g.CacheFilesFound > 0);
        // Synthetics-only LoadDetectionAsync supplies an explicit zero aggregate. A null here
        // means persisted detection rows without a derived summary — keep that integrity fault loud.
        var summary = diskSummary ?? throw new InvalidOperationException(
            "Cached detection rows exist without a derived disk summary");

        object? responseGames = slimForDashboard
            ? games.Select(g => new DashboardGameSummary
            {
                GameAppId = g.GameAppId,
                GameName = g.GameName,
                CacheFilesFound = g.CacheFilesFound,
                TotalSizeBytes = g.TotalSizeBytes,
                Service = g.Service,
                ImageUrl = g.ImageUrl,
                EpicAppId = g.EpicAppId,
                IsEvicted = g.IsEvicted,
                EvictedDownloadsCount = g.EvictedDownloadsCount
            }).ToList()
            : games;

        object? responseServices = slimForDashboard
            ? (services ?? []).Select(s => new DashboardServiceSummary
            {
                ServiceName = s.ServiceName,
                CacheFilesFound = s.CacheFilesFound,
                TotalSizeBytes = s.TotalSizeBytes,
                IsEvicted = s.IsEvicted,
                EvictedDownloadsCount = s.EvictedDownloadsCount
            }).ToList()
            : services;

        return new CachedDetectionResponse
        {
            HasCachedResults = true,
            Games = responseGames,
            Services = responseServices,
            TotalGamesDetected = activeGamesCount,
            TotalServicesDetected = totalServicesDetected,
            LastDetectionTime = lastDetectionUtc.ToString("o"),
            GamesOnDiskBytes = summary.GameBytes,
            GamesOnDiskCount = summary.ActiveGameCount,
            IdentifiedCacheBytes = summary.TotalBytes,
            IdentifiedServiceBytes = summary.ServiceBytes,
            DetectionSummaryComputedAt = summaryComputedAtUtc?.ToString("o"),
            DetectionStale = detectionStale,
            UnmappedServices = slimForDashboard ? null : unmappedServices
        };
    }
}
