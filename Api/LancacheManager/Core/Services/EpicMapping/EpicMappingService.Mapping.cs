using LancacheManager.Hubs;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services.EpicMapping;

public partial class EpicMappingService
{
    /// <summary>
    /// Resolve Epic downloads that don't have game names yet.
    /// Matches download URLs against stored EpicCdnPatterns.
    /// Called after log processing to identify Epic game downloads.
    /// </summary>
    public async Task<int> ResolveDownloadsAsync(CancellationToken ct = default)
    {
        using var db = _dbContextFactory.CreateDbContext();
        const string epicServicePattern = "%epic%";

        // Service names are normalized to lowercase during log processing, so a lowercase LIKE
        // pattern preserves the intended matching while staying SQL-translatable.
        // Empty counts as unresolved, not as resolved. A download stamped with an empty id by an
        // older pattern row is not an Epic identity to GamesOnDiskCalculator.GetDownloadGameKey,
        // so a null-only test would leave it permanently stuck: never re-resolved here, yet still
        // treated as Epic by the layers that test the column against null.
        // A row that kept its app id but lost its name is a candidate too. Clearing every GameName
        // (a full Steam scan, or the mapping reset arm) left Epic rows with an id and no name, and
        // cache detection needs both columns filled to bucket the row, so re-offer them here.
        // Count the candidates before loading them: the log pass calls this after every run, and on
        // a cache with no Epic traffic there is never anything to name, so the common case costs one
        // count instead of a tracked load plus the well-known pattern seed.
        var unresolvedCount = await db.Downloads
            .CountAsync(d => EF.Functions.Like(d.Service, epicServicePattern) && (string.IsNullOrEmpty(d.EpicAppId) || d.GameName == null) && d.LastUrl != null, ct);

        if (unresolvedCount == 0)
        {
            _logger.LogInformation("No unresolved Epic downloads to match against CDN patterns");
            return 0;
        }

        // Seed well-known non-game patterns (Epic Games Launcher etc.) so launcher chunks
        // get labeled instead of falling through as Unknown. Idempotent.
        await EnsureWellKnownPatternsAsync(ct);

        var unresolvedDownloads = await db.Downloads
            .Where(d => EF.Functions.Like(d.Service, epicServicePattern) && (string.IsNullOrEmpty(d.EpicAppId) || d.GameName == null) && d.LastUrl != null)
            .ToListAsync(ct);

        if (unresolvedDownloads.Count == 0)
        {
            // A concurrent ingest pass can stamp the last candidate between the count and this load.
            return 0;
        }

        // An id without a name is counted as unresolved below, so keep it out of this number or the
        // two buckets in the same log line describe the same rows.
        var alreadyMapped = await db.Downloads
            .CountAsync(d => EF.Functions.Like(d.Service, epicServicePattern) && !string.IsNullOrEmpty(d.EpicAppId) && d.GameName != null, ct);
        var nullUrls = await db.Downloads
            .CountAsync(d => EF.Functions.Like(d.Service, epicServicePattern) && d.LastUrl == null, ct);

        _logger.LogInformation(
            "Epic downloads diagnostic: AlreadyMapped={Mapped}, NullUrl={NullUrl}, Unresolved={Unresolved}",
            alreadyMapped, nullUrls, unresolvedDownloads.Count);

        var sampleService = unresolvedDownloads.FirstOrDefault()?.Service;
        var sampleUrl = unresolvedDownloads.FirstOrDefault()?.LastUrl;
        if (sampleService != null)
            _logger.LogInformation("Sample Epic service name: '{Service}'", sampleService);
        if (sampleUrl != null)
            _logger.LogInformation("Sample Epic download URL: '{Url}'", sampleUrl);

        // Sort patterns by ChunkBaseUrl length descending so the longest (most specific) pattern matches first.
        // Patterns stored before MergeCdnPatternsAsync rejected empty app ids are excluded: matching one
        // stamps its empty AppId onto the download, which no layer agrees how to bucket.
        var patterns = await db.EpicCdnPatterns
            .Where(p => !string.IsNullOrEmpty(p.AppId))
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
}
