using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Infrastructure.Data;

/// <summary>
/// Shared queries for Steam banner URLs on <see cref="Models.Download.GameImageUrl"/>
/// (latest row per <see cref="Models.Download.GameAppId"/> by <see cref="Models.Download.StartTimeUtc"/>).
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
