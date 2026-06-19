using LancacheManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

public sealed class EvictedDetectionPreservationService
{
    internal async Task<EvictedDetectionUnpreservationResult> UnpreserveAsync(
        AppDbContext context,
        IReadOnlyList<long> steamAppIds,
        IReadOnlyList<string> epicAppIds,
        IReadOnlyList<NamedGameKey> namedGameKeys,
        CancellationToken cancellationToken)
    {
        var distinctSteamAppIds = steamAppIds.Distinct().ToList();
        var distinctEpicAppIds = epicAppIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var distinctNamedKeys = namedGameKeys.Distinct().ToList();

        // Exclude named (Blizzard/Riot) rows (GameAppId==0 && Service set) from the Steam arm so a
        // stray appId 0 can't un-evict named games; named games self-heal via the named arm below.
        var steamGamesUpdated = distinctSteamAppIds.Count == 0
            ? 0
            : await context.CachedGameDetections
                .Where(g => g.IsEvicted && g.EpicAppId == null
                    && !(g.GameAppId == 0 && g.Service != null && g.GameName != null && g.GameName != "")
                    && distinctSteamAppIds.Contains(g.GameAppId))
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

        var namedGamesUpdated = 0;
        if (distinctNamedKeys.Count > 0)
        {
            var namedServices = distinctNamedKeys.Select(k => k.Service).Distinct().ToList();
            // Materialize candidate evicted named rows for the incoming services, then filter the
            // exact (Service, GameName) matches in memory (EF can't translate composite tuple
            // membership over a list).
            var candidateRows = await context.CachedGameDetections
                .Where(g => g.IsEvicted && g.EpicAppId == null && g.GameAppId == 0
                    && g.Service != null && g.GameName != ""
                    && namedServices.Contains(g.Service!.ToLower()))
                .ToListAsync(cancellationToken);

            var targetSet = distinctNamedKeys.Select(k => (k.Service, k.GameName)).ToHashSet();
            foreach (var row in candidateRows)
            {
                if (targetSet.Contains((row.Service!.ToLower(), row.GameName)))
                {
                    row.IsEvicted = false;
                    namedGamesUpdated++;
                }
            }

            if (namedGamesUpdated > 0)
            {
                await context.SaveChangesAsync(cancellationToken);
            }
        }

        return new EvictedDetectionUnpreservationResult(steamGamesUpdated, epicGamesUpdated, namedGamesUpdated);
    }

}

internal readonly record struct EvictedDetectionUnpreservationResult(
    int SteamGamesUpdated,
    int EpicGamesUpdated,
    int NamedGamesUpdated)
{
    public int TotalUpdated => SteamGamesUpdated + EpicGamesUpdated + NamedGamesUpdated;
}
