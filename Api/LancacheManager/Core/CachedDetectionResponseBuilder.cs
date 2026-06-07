using LancacheManager.Models;
using LancacheManager.Models.Responses;

namespace LancacheManager.Core;

/// <summary>
/// Builds <see cref="CachedDetectionResponse"/> with deduplicated games-on-disk aggregates.
/// </summary>
public static class CachedDetectionResponseBuilder
{
    public static CachedDetectionResponse BuildEmpty() =>
        new() { HasCachedResults = false };

    /// <param name="slimForDashboard">
    /// When true, projects games/services into slim DTOs (dashboard batch).
    /// When false, returns full <see cref="GameCacheInfo"/> / <see cref="ServiceCacheInfo"/> lists.
    /// </param>
    public static CachedDetectionResponse Build(
        IReadOnlyList<GameCacheInfo> games,
        IReadOnlyList<ServiceCacheInfo>? services,
        int totalServicesDetected,
        DateTime lastDetectionUtc,
        long usedCacheSizeBytes,
        bool slimForDashboard)
    {
        var gamesOnDisk = GamesOnDiskCalculator.Compute(games, usedCacheSizeBytes);
        var activeGamesCount = games.Count(g => !g.IsEvicted);

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
            GamesOnDiskBytes = gamesOnDisk.TotalBytes,
            GamesOnDiskCount = gamesOnDisk.ActiveGameCount,
            GamesOnDiskMayBeStale = gamesOnDisk.MayBeStale
        };
    }
}
