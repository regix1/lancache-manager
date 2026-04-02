using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Infrastructure.Data;

/// <summary>
/// Shared queries for Steam banner URLs on <see cref="Models.Download.GameImageUrl"/>
/// (latest row per <see cref="Models.Download.GameAppId"/> by <see cref="Models.Download.StartTimeUtc"/>).
///
/// <b>Writers</b> that populate <c>Download.GameImageUrl</c> (source of truth for <see cref="Services.GameImageFetchService"/>):
/// <list type="bullet">
///   <item>
///     <c>SteamKit2Service.Mapping.cs</c> — 4 sites in <c>UpdateDownloadsWithDepotMappingsAsync</c>:
///     sets <c>gameInfo.HeaderImage</c> for mapped depots, or <c>null</c> for unmapped/fallback cases.
///   </item>
///   <item>
///     <c>EpicMappingService.Persistence.cs</c> — <c>RefreshEpicImageUrlsAsync</c>:
///     propagates <c>EpicGameMapping.ImageUrl</c> to downloads whose <c>GameImageUrl</c> differs.
///   </item>
///   <item>
///     <c>RustLogProcessorService.cs</c> — <c>FetchMissingEpicGameImagesAsync</c>:
///     back-fills <c>GameImageUrl</c> on Epic downloads that have <c>EpicAppId</c> but no image yet.
///   </item>
///   <item>
///     <c>DatabaseService.cs</c> — bulk clears <c>GameImageUrl</c> to <c>null</c> when purging depot mappings.
///   </item>
///   <item>
///     <c>SteamKit2Service.Mapping.cs</c> — <c>ClearDownloadGameDataAsync</c>:
///     bulk clears <c>GameImageUrl</c> to <c>null</c> before a fresh full PICS scan.
///   </item>
/// </list>
///
/// <b>Readers</b>:
/// <list type="bullet">
///   <item>
///     <see cref="Services.GameImageFetchService"/> — calls <see cref="GetLatestUrlsForSteamAppsAsync"/>
///     to discover which CDN URLs to fetch image bytes from for apps that are missing cached images.
///   </item>
///   <item>
///     <c>GameCacheDetectionService.EnrichGameImageUrlsFromDatabaseAsync</c> — calls
///     <see cref="GetLatestUrlsForSteamAppsAsync"/> to populate <c>GameCacheInfo.ImageUrl</c>
///     on the detection API response (legacy field; frontend now uses the <c>GameImage</c> component
///     which loads images via <c>/api/game-images/{appId}/header</c> proxy instead).
///   </item>
/// </list>
/// </summary>
public static class DownloadGameImageUrlQueries
{
    public static async Task<string?> GetLatestUrlForSteamAppAsync(
        AppDbContext db,
        long gameAppId,
        CancellationToken cancellationToken = default)
    {
        var download = await db.Downloads
            .AsNoTracking()
            .Where(d => d.GameAppId == gameAppId && !string.IsNullOrEmpty(d.GameImageUrl))
            .OrderByDescending(d => d.StartTimeUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return download?.GameImageUrl;
    }

    public static async Task<Dictionary<long, string>> GetLatestUrlsForSteamAppsAsync(
        AppDbContext db,
        IReadOnlyCollection<long> steamAppIds,
        CancellationToken cancellationToken = default)
    {
        if (steamAppIds.Count == 0)
        {
            return new Dictionary<long, string>();
        }

        var rows = await db.Downloads
            .AsNoTracking()
            .Where(d => d.GameAppId != null
                        && steamAppIds.Contains(d.GameAppId.Value)
                        && !string.IsNullOrEmpty(d.GameImageUrl))
            .Select(d => new { d.GameAppId, d.GameImageUrl, d.StartTimeUtc })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(x => x.GameAppId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.StartTimeUtc).First().GameImageUrl!);
    }
}
