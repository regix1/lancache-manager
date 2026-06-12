using LancacheManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

public sealed class EvictedDetectionPreservationService
{
    internal async Task<EvictedDetectionUnpreservationResult> UnpreserveAsync(
        AppDbContext context,
        IReadOnlyList<long> steamAppIds,
        IReadOnlyList<string> epicAppIds,
        CancellationToken cancellationToken)
    {
        var distinctSteamAppIds = steamAppIds.Distinct().ToList();
        var distinctEpicAppIds = epicAppIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var steamGamesUpdated = distinctSteamAppIds.Count == 0
            ? 0
            : await context.CachedGameDetections
                .Where(g => g.IsEvicted && g.EpicAppId == null && distinctSteamAppIds.Contains(g.GameAppId))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(g => g.IsEvicted, false),
                    cancellationToken);

        var epicGamesUpdated = distinctEpicAppIds.Count == 0
            ? 0
            : await context.CachedGameDetections
                .Where(g => g.IsEvicted && g.EpicAppId != null && distinctEpicAppIds.Contains(g.EpicAppId!))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(g => g.IsEvicted, false),
                    cancellationToken);

        return new EvictedDetectionUnpreservationResult(steamGamesUpdated, epicGamesUpdated);
    }

}

internal readonly record struct EvictedDetectionUnpreservationResult(
    int SteamGamesUpdated,
    int EpicGamesUpdated)
{
    public int TotalUpdated => SteamGamesUpdated + EpicGamesUpdated;
}
