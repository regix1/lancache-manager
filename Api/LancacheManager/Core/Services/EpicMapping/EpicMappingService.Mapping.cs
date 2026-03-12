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

        // Push unresolved filter into the DB query to avoid loading all Epic downloads into memory
        var unresolvedDownloads = await db.Downloads
            .Where(d => d.Service.ToLower().Contains("epic") && d.EpicAppId == null && d.LastUrl != null)
            .ToListAsync(ct);

        if (unresolvedDownloads.Count == 0)
        {
            // Check if there are any Epic downloads at all for diagnostics
            var totalEpicCount = await db.Downloads
                .CountAsync(d => d.Service.ToLower().Contains("epic"), ct);

            if (totalEpicCount == 0)
            {
                var distinctServices = await db.Downloads
                    .Select(d => d.Service)
                    .Distinct()
                    .ToListAsync(ct);
                _logger.LogWarning(
                    "No downloads found with 'epic' in Service name. Distinct service names in DB: [{Services}]",
                    string.Join(", ", distinctServices));
            }

            return 0;
        }

        var alreadyMapped = await db.Downloads
            .CountAsync(d => d.Service.ToLower().Contains("epic") && d.EpicAppId != null, ct);
        var nullUrls = await db.Downloads
            .CountAsync(d => d.Service.ToLower().Contains("epic") && d.LastUrl == null, ct);

        _logger.LogInformation(
            "Epic downloads diagnostic: AlreadyMapped={Mapped}, NullUrl={NullUrl}, Unresolved={Unresolved}",
            alreadyMapped, nullUrls, unresolvedDownloads.Count);

        var sampleService = unresolvedDownloads.FirstOrDefault()?.Service;
        var sampleUrl = unresolvedDownloads.FirstOrDefault()?.LastUrl;
        if (sampleService != null)
            _logger.LogInformation("Sample Epic service name: '{Service}'", sampleService);
        if (sampleUrl != null)
            _logger.LogInformation("Sample Epic download URL: '{Url}'", sampleUrl);

        // Sort patterns by ChunkBaseUrl length descending so the longest (most specific) pattern matches first
        var patterns = await db.EpicCdnPatterns
            .OrderByDescending(p => p.ChunkBaseUrl.Length)
            .ToListAsync(ct);
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

        var patterns = await db.EpicCdnPatterns
            .OrderByDescending(p => p.ChunkBaseUrl.Length)
            .ToListAsync(ct);
        var matchingPattern = patterns.FirstOrDefault(p => url.Contains(p.ChunkBaseUrl.TrimEnd('/')));

        if (matchingPattern == null) return null;

        return await db.EpicGameMappings
            .FirstOrDefaultAsync(m => m.AppId == matchingPattern.AppId, ct);
    }
}
