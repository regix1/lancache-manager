using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services.EpicMapping;

public partial class EpicMappingService
{
    /// <summary>
    /// Resolve Epic downloads that don't have game names yet.
    /// Matches download URLs against stored EpicCdnPatterns.
    /// Called after log processing to identify Epic game downloads.
    /// </summary>
    public async Task<int> ResolveEpicDownloadsAsync(CancellationToken ct = default)
    {
        using var db = _dbContextFactory.CreateDbContext();

        var allEpicDownloads = await db.Downloads
            .Where(d => d.Service.ToLower().Contains("epic"))
            .ToListAsync(ct);

        if (allEpicDownloads.Count == 0)
        {
            var distinctServices = await db.Downloads
                .Select(d => d.Service)
                .Distinct()
                .ToListAsync(ct);
            _logger.LogWarning(
                "No downloads found with 'epic' in Service name. Distinct service names in DB: [{Services}]",
                string.Join(", ", distinctServices));
            return 0;
        }

        var alreadyMapped = allEpicDownloads.Count(d => d.GameName != null);
        var nullUrls = allEpicDownloads.Count(d => d.LastUrl == null);
        var unresolvedDownloads = allEpicDownloads
            .Where(d => d.GameName == null && d.LastUrl != null)
            .ToList();

        _logger.LogInformation(
            "Epic downloads diagnostic: Total={Total}, AlreadyMapped={Mapped}, NullUrl={NullUrl}, Unresolved={Unresolved}",
            allEpicDownloads.Count, alreadyMapped, nullUrls, unresolvedDownloads.Count);

        var sampleService = allEpicDownloads.FirstOrDefault()?.Service;
        var sampleUrl = allEpicDownloads.FirstOrDefault(d => d.LastUrl != null)?.LastUrl;
        if (sampleService != null)
            _logger.LogInformation("Sample Epic service name: '{Service}'", sampleService);
        if (sampleUrl != null)
            _logger.LogInformation("Sample Epic download URL: '{Url}'", sampleUrl);

        if (unresolvedDownloads.Count == 0)
            return 0;

        var patterns = await db.EpicCdnPatterns.ToListAsync(ct);
        if (patterns.Count == 0)
        {
            _logger.LogWarning(
                "No Epic CDN patterns available for resolution. {Count} unresolved downloads exist but cannot be matched. " +
                "Log in with Epic in the Integrations section to collect CDN patterns.",
                unresolvedDownloads.Count);
            return 0;
        }

        var samplePattern = patterns.FirstOrDefault();
        if (samplePattern != null)
            _logger.LogInformation("Sample CDN pattern: ChunkBaseUrl='{Url}', AppId='{AppId}'",
                samplePattern.ChunkBaseUrl, samplePattern.AppId);

        var gameMappings = await db.EpicGameMappings.ToDictionaryAsync(m => m.AppId, ct);

        _logger.LogDebug("Loaded {PatternCount} CDN patterns and {MappingCount} game mappings for resolution", patterns.Count, gameMappings.Count);

        var resolvedCount = 0;
        var unmatchedSampleLogged = false;
        foreach (var download in unresolvedDownloads)
        {
            if (string.IsNullOrEmpty(download.LastUrl)) continue;

            var matchingPattern = patterns.FirstOrDefault(p =>
                download.LastUrl.Contains(p.ChunkBaseUrl.TrimEnd('/')));

            if (matchingPattern != null)
            {
                download.EpicAppId = matchingPattern.AppId;
                download.GameName = matchingPattern.Name;

                if (gameMappings.TryGetValue(matchingPattern.AppId, out var gameMapping))
                {
                    download.GameName = gameMapping.Name;
                }

                _logger.LogTrace("Resolved Epic download to game: {GameName} (AppId: {AppId})", download.GameName, matchingPattern.AppId);
                resolvedCount++;
            }
            else if (!unmatchedSampleLogged)
            {
                _logger.LogWarning("No CDN pattern matched download URL: '{Url}'", download.LastUrl);
                unmatchedSampleLogged = true;
            }
        }

        if (resolvedCount > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Resolved {Count}/{Total} Epic downloads to game names",
                resolvedCount, unresolvedDownloads.Count);

            // Notify frontend to refresh downloads so resolved game names appear in the UI
            await _notifications.NotifyAllAsync(SignalREvents.DownloadsRefresh, new
            {
                source = "epic-download-resolution",
                resolvedCount
            });
        }
        else
        {
            _logger.LogWarning(
                "0 of {Count} unresolved Epic downloads matched any of {PatternCount} CDN patterns. " +
                "URL format may not match stored patterns.",
                unresolvedDownloads.Count, patterns.Count);
        }

        return resolvedCount;
    }

    /// <summary>
    /// Try to resolve an Epic CDN URL to a game name using stored patterns.
    /// </summary>
    public async Task<EpicGameMapping?> ResolveGameFromUrlAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        using var db = _dbContextFactory.CreateDbContext();

        var patterns = await db.EpicCdnPatterns.ToListAsync(ct);
        var matchingPattern = patterns.FirstOrDefault(p => url.Contains(p.ChunkBaseUrl.TrimEnd('/')));

        if (matchingPattern == null) return null;

        return await db.EpicGameMappings
            .FirstOrDefaultAsync(m => m.AppId == matchingPattern.AppId, ct);
    }
}
