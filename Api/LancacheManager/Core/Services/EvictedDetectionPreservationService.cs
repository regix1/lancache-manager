using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
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

    internal async Task<EvictedDetectionPreservationResult> PreserveAsync(
        AppDbContext context,
        IReadOnlyList<Download> evictedDownloads,
        CancellationToken cancellationToken)
    {
        var evictedGameGroups = evictedDownloads
            .Where(d => d.GameAppId != null || d.EpicAppId != null)
            .GroupBy(d => new { d.GameAppId, d.EpicAppId })
            .Select(g => g.First())
            .ToList();

        var evictedServiceGroups = evictedDownloads
            .Where(d => d.GameAppId == null
                     && d.EpicAppId == null
                     && !string.IsNullOrWhiteSpace(d.Service))
            .GroupBy(d => d.Service!.ToLowerInvariant())
            .Select(g => g.First())
            .ToList();

        if (evictedGameGroups.Count == 0 && evictedServiceGroups.Count == 0)
        {
            return new EvictedDetectionPreservationResult(0, 0);
        }

        var steamAppIds = evictedGameGroups
            .Where(d => d.EpicAppId == null && d.GameAppId != null)
            .Select(d => d.GameAppId!.Value)
            .Distinct()
            .ToList();
        var epicAppIds = evictedGameGroups
            .Where(d => d.EpicAppId != null)
            .Select(d => d.EpicAppId!)
            .Distinct()
            .ToList();
        var serviceKeys = evictedServiceGroups
            .Select(d => d.Service!.ToLowerInvariant())
            .Distinct()
            .ToList();

        var existingSteamGames = steamAppIds.Count == 0
            ? new Dictionary<long, CachedGameDetection>()
            : await context.CachedGameDetections
                .Where(c => c.EpicAppId == null && steamAppIds.Contains(c.GameAppId))
                .ToDictionaryAsync(c => c.GameAppId, cancellationToken);

        var existingEpicGames = epicAppIds.Count == 0
            ? new Dictionary<string, CachedGameDetection>()
            : await context.CachedGameDetections
                .Where(c => c.EpicAppId != null && epicAppIds.Contains(c.EpicAppId!))
                .ToDictionaryAsync(c => c.EpicAppId!, cancellationToken);

        var existingServices = serviceKeys.Count == 0
            ? new Dictionary<string, CachedServiceDetection>(StringComparer.OrdinalIgnoreCase)
            : (await context.CachedServiceDetections
                .Where(s => serviceKeys.Contains(s.ServiceName.ToLower()))
                .ToListAsync(cancellationToken))
                .ToDictionary(s => s.ServiceName, StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        var gamesUpserted = 0;
        var servicesUpserted = 0;

        foreach (var representative in evictedGameGroups)
        {
            CachedGameDetection? existing = null;
            if (representative.EpicAppId != null)
            {
                existingEpicGames.TryGetValue(representative.EpicAppId, out existing);
            }
            else if (representative.GameAppId != null)
            {
                existingSteamGames.TryGetValue(representative.GameAppId.Value, out existing);
            }

            if (existing != null)
            {
                existing.IsEvicted = true;
                existing.CacheFilesFound = 0;
                existing.TotalSizeBytes = 0;
                if (!string.IsNullOrEmpty(representative.GameName))
                {
                    existing.GameName = representative.GameName;
                }

                if (representative.Service != null)
                {
                    existing.Service = representative.Service;
                }

                existing.LastDetectedUtc = now;
            }
            else
            {
                context.CachedGameDetections.Add(new CachedGameDetection
                {
                    GameAppId = representative.GameAppId ?? 0,
                    GameName = representative.GameName ?? string.Empty,
                    EpicAppId = representative.EpicAppId,
                    Service = representative.Service,
                    CacheFilesFound = 0,
                    TotalSizeBytes = 0,
                    IsEvicted = true,
                    DatasourcesJson = $"[\"{representative.Datasource}\"]",
                    LastDetectedUtc = now,
                    CreatedAtUtc = now
                });
            }

            gamesUpserted++;
        }

        foreach (var representative in evictedServiceGroups)
        {
            var normalizedKey = representative.Service!.ToLowerInvariant();
            if (existingServices.TryGetValue(normalizedKey, out var existing))
            {
                existing.IsEvicted = true;
                existing.CacheFilesFound = 0;
                existing.TotalSizeBytes = 0;
                existing.LastDetectedUtc = now;
            }
            else
            {
                context.CachedServiceDetections.Add(new CachedServiceDetection
                {
                    ServiceName = representative.Service!,
                    CacheFilesFound = 0,
                    TotalSizeBytes = 0,
                    SampleUrlsJson = "[]",
                    CacheFilePathsJson = "[]",
                    DatasourcesJson = $"[\"{representative.Datasource}\"]",
                    IsEvicted = true,
                    LastDetectedUtc = now,
                    CreatedAtUtc = now
                });
            }

            servicesUpserted++;
        }

        await context.SaveChangesAsync(cancellationToken);
        return new EvictedDetectionPreservationResult(gamesUpserted, servicesUpserted);
    }
}

internal readonly record struct EvictedDetectionPreservationResult(
    int GamesUpserted,
    int ServicesUpserted);

internal readonly record struct EvictedDetectionUnpreservationResult(
    int SteamGamesUpdated,
    int EpicGamesUpdated)
{
    public int TotalUpdated => SteamGamesUpdated + EpicGamesUpdated;
}
