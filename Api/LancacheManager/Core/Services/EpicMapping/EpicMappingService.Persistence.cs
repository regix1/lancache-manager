using LancacheManager.Core.Utilities;
using LancacheManager.Hubs;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services.EpicMapping;

public partial class EpicMappingService
{
    /// <summary>
    /// Merge a list of owned games into the persistent mapping.
    /// Called after successful Epic login when get-owned-games returns.
    /// Thread-safe via SemaphoreSlim.
    /// </summary>
    public async Task<MergeResult> MergeOwnedGamesAsync(
        List<OwnedGame> games,
        string sessionIdHash,
        string source = "prefill-login",
        CancellationToken ct = default)
    {
        await _mergeLock.WaitAsync(ct);
        try
        {
            using var db = _dbContextFactory.CreateDbContext();

            var result = new MergeResult();
            var now = DateTime.UtcNow;

            var existingMappings = await db.EpicGameMappings
                .ToDictionaryAsync(m => m.AppId, ct);

            _logger.LogDebug("Loaded {Count} existing Epic game mappings for merge comparison", existingMappings.Count);

            foreach (var game in games)
            {
                if (string.IsNullOrWhiteSpace(game.AppId))
                    continue;

                if (existingMappings.TryGetValue(game.AppId, out var existing))
                {
                    var changed = false;
                    if (!string.IsNullOrWhiteSpace(game.Name) && existing.Name != game.Name)
                    {
                        existing.Name = game.Name;
                        changed = true;
                    }
                    if (!string.IsNullOrWhiteSpace(game.ImageUrl) && existing.ImageUrl != game.ImageUrl)
                    {
                        _logger.LogInformation("Updating ImageUrl for {AppId} ({Name}): {OldUrl} -> {NewUrl}",
                            game.AppId, game.Name, existing.ImageUrl ?? "null", game.ImageUrl);
                        existing.ImageUrl = game.ImageUrl;
                        changed = true;
                    }

                    // Only update LastSeenAtUtc when it's more than 1 hour old to reduce unnecessary DB writes
                    if ((now - existing.LastSeenAtUtc).TotalHours >= 1)
                    {
                        existing.LastSeenAtUtc = now;
                        changed = true;
                    }

                    if (changed)
                        result.UpdatedGames++;
                    else
                        result.UnchangedGames++;
                }
                else
                {
                    var mapping = new EpicGameMapping
                    {
                        AppId = game.AppId,
                        Name = game.Name,
                        ImageUrl = game.ImageUrl,
                        DiscoveredAtUtc = now,
                        LastSeenAtUtc = now,
                        DiscoveredByHash = sessionIdHash,
                        Source = source
                    };
                    db.EpicGameMappings.Add(mapping);
                    existingMappings[game.AppId] = mapping;
                    result.NewGames++;
                    _logger.LogTrace("New Epic game discovered: {AppId} = {Name}", game.AppId, game.Name);
                }
            }

            await db.SaveChangesAsync(ct);

            result.TotalGames = existingMappings.Count;

            return result;
        }
        finally
        {
            _mergeLock.Release();
        }
    }

    /// <summary>
    /// Get all known Epic game mappings from the database.
    /// </summary>
    public async Task<List<EpicGameMapping>> GetAllMappingsAsync(CancellationToken ct = default)
    {
        using var db = _dbContextFactory.CreateDbContext();
        return await db.EpicGameMappings
            .AsNoTracking()
            .OrderBy(m => m.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Get mapping statistics (total games, last updated, etc.)
    /// </summary>
    public async Task<EpicMappingStats> GetStatsAsync(CancellationToken ct = default)
    {
        using var db = _dbContextFactory.CreateDbContext();
        var mappings = db.EpicGameMappings;

        if (!await mappings.AnyAsync(ct))
        {
            return new EpicMappingStats
            {
                CdnPatterns = await db.EpicCdnPatterns.CountAsync(ct)
            };
        }

        return new EpicMappingStats
        {
            TotalGames = await mappings.CountAsync(ct),
            LastUpdatedUtc = await mappings.MaxAsync(m => m.LastSeenAtUtc, ct),
            OldestGameUtc = await mappings.MinAsync(m => m.DiscoveredAtUtc, ct),
            DistinctSources = await mappings.Select(m => m.Source).Distinct().CountAsync(ct),
            CdnPatterns = await db.EpicCdnPatterns.CountAsync(ct)
        };
    }

    /// <summary>
    /// Re-fetches catalog metadata for all known Epic games and updates ImageUrl if changed.
    /// Called during periodic catalog refresh to keep images current.
    /// </summary>
    public async Task<int> RefreshGameImagesAsync(string accessToken, CancellationToken ct = default)
    {
        using var db = _dbContextFactory.CreateDbContext();

        // Get all games that could benefit from an image refresh
        var games = await db.EpicGameMappings.ToListAsync(ct);
        if (games.Count == 0) return 0;

        _logger.LogDebug("Refreshing images for {Count} Epic games", games.Count);

        var updated = 0;
        // Process in batches to avoid overwhelming the API
        var batchSize = 25;
        for (var i = 0; i < games.Count; i += batchSize)
        {
            if (ct.IsCancellationRequested) break;

            var batch = games.Skip(i).Take(batchSize).ToList();
            foreach (var game in batch)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    // We don't have namespace/catalogItemId stored in EpicGameMappings,
                    // so we can't call the catalog API per-game directly.
                    // Instead, images are already refreshed during GetOwnedGamesAsync()
                    // which is called in RefreshCatalogAsync(). This method serves as
                    // a secondary pass to update any downloads that reference these images.
                    // For now, just ensure all Downloads with EpicAppId get the current ImageUrl.

                    if (!string.IsNullOrEmpty(game.ImageUrl))
                    {
                        var downloadsToUpdate = await db.Downloads
                            .Where(d => d.EpicAppId == game.AppId && (d.GameImageUrl == null || d.GameImageUrl != game.ImageUrl))
                            .ToListAsync(ct);

                        foreach (var download in downloadsToUpdate)
                        {
                            download.GameImageUrl = game.ImageUrl;
                            _logger.LogInformation("Propagating Epic image to Download: EpicAppId={AppId}, ImageUrl={Url}",
                                game.AppId, game.ImageUrl);
                            updated++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogTrace(ex, "Failed to refresh image for Epic game {AppId}", game.AppId);
                }
            }
        }

        if (updated > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Updated {Count} download image URLs from Epic game mappings", updated);
        }

        return updated;
    }

    /// <summary>
    /// Public method for on-demand image URL refresh. Called by GameImagesController
    /// when user clicks "Refresh game banners". Lighter than full RefreshCatalogAsync -
    /// only re-fetches owned games (which updates ImageUrl via GetBestImageUrl) and
    /// propagates to Downloads table.
    /// </summary>
    public async Task<int> RefreshImageUrlsAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("=== RefreshImageUrlsAsync START ===");

        if (!_isAuthenticated || _currentTokens == null)
        {
            _logger.LogWarning("Cannot refresh image URLs - not authenticated");
            return 0;
        }

        // Refresh token if expired
        if (_currentTokens.ExpiresAt <= DateTime.UtcNow)
        {
            _logger.LogInformation("Access token expired, refreshing before image URL update...");
            try
            {
                var tokens = await _epicApiClient.RefreshTokenAsync(_currentTokens.RefreshToken, ct);
                _currentTokens = tokens;
                _logger.LogInformation("Token refreshed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Token refresh failed during image URL refresh");
                return 0;
            }
        }

        // Re-fetch owned games (this calls GetBestImageUrl which selects correct landscape images)
        _logger.LogInformation("Fetching owned games from Epic catalog...");
        var games = await _epicApiClient.GetOwnedGamesAsync(_currentTokens.AccessToken, ct);
        _logger.LogInformation("Got {Count} games from Epic catalog", games.Count);

        if (games.Count == 0)
        {
            _logger.LogWarning("No games returned from Epic catalog");
            return 0;
        }

        // Log a sample of image URLs for diagnosis
        foreach (var game in games.Take(5))
        {
            _logger.LogInformation("Sample game: {Name} ({AppId}) -> ImageUrl: {Url}",
                game.Name, game.AppId, game.ImageUrl ?? "NULL");
        }

        // Merge into DB (updates ImageUrl where changed)
        var sessionHash = CryptoUtils.ComputeAnonymousHash("image-refresh");
        var result = await MergeOwnedGamesAsync(games, sessionHash, "image-refresh", ct);
        _logger.LogInformation("Merge result: {New} new, {Updated} updated, {Unchanged} unchanged",
            result.NewGames, result.UpdatedGames, result.UnchangedGames);

        // Also propagate to Downloads table
        try
        {
            var imageUpdates = await RefreshGameImagesAsync(_currentTokens.AccessToken, ct);
            _logger.LogInformation("Propagated {Count} image URLs to Downloads table", imageUpdates);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to propagate image URLs to Downloads table");
        }

        var totalUpdated = result.NewGames + result.UpdatedGames;

        // Notify frontend of image updates
        await _notifications.NotifyAllAsync(SignalREvents.EpicGameMappingsUpdated, new
        {
            totalGames = result.TotalGames,
            source = "image-refresh"
        });

        _logger.LogInformation("=== RefreshImageUrlsAsync END === Updated {Count} games", totalUpdated);

        return totalUpdated;
    }

    /// <summary>
    /// Search games by name (case-insensitive partial match).
    /// </summary>
    public async Task<List<EpicGameMapping>> SearchAsync(string query, CancellationToken ct = default)
    {
        using var db = _dbContextFactory.CreateDbContext();
        var lowerQuery = query.ToLowerInvariant();
        return await db.EpicGameMappings
            .AsNoTracking()
            .Where(m => m.Name.ToLower().Contains(lowerQuery) || m.AppId.ToLower().Contains(lowerQuery))
            .OrderBy(m => m.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Merge CDN patterns into the persistent mapping.
    /// Called after get-cdn-info returns from the daemon.
    /// </summary>
    public async Task MergeCdnPatternsAsync(List<CdnInfo> cdnInfos, CancellationToken ct = default)
    {
        if (cdnInfos.Count == 0) return;

        await _mergeLock.WaitAsync(ct);
        try
        {
            using var db = _dbContextFactory.CreateDbContext();
            var now = DateTime.UtcNow;

            var existingPatterns = await db.EpicCdnPatterns
                .ToDictionaryAsync(p => p.ChunkBaseUrl, ct);

            var newCount = 0;
            var updatedCount = 0;

            foreach (var info in cdnInfos)
            {
                if (string.IsNullOrWhiteSpace(info.ChunkBaseUrl)) continue;

                if (existingPatterns.TryGetValue(info.ChunkBaseUrl, out var existing))
                {
                    existing.LastSeenAtUtc = now;
                    if (!string.IsNullOrWhiteSpace(info.Name))
                        existing.Name = info.Name;
                    updatedCount++;
                }
                else
                {
                    var newPattern = new EpicCdnPattern
                    {
                        AppId = info.AppId,
                        Name = info.Name,
                        CdnHost = info.CdnHost,
                        ChunkBaseUrl = info.ChunkBaseUrl,
                        DiscoveredAtUtc = now,
                        LastSeenAtUtc = now
                    };
                    db.EpicCdnPatterns.Add(newPattern);
                    existingPatterns[info.ChunkBaseUrl] = newPattern;
                    newCount++;
                }
            }

            await db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Epic CDN pattern merge: {New} new, {Updated} updated, {Total} total",
                newCount, updatedCount, newCount + updatedCount + (existingPatterns.Count - updatedCount));
        }
        finally
        {
            _mergeLock.Release();
        }
    }
}

/// <summary>
/// Result of a merge operation.
/// </summary>
public class MergeResult
{
    public int NewGames { get; set; }
    public int UpdatedGames { get; set; }
    public int UnchangedGames { get; set; }
    public int TotalGames { get; set; }
}

/// <summary>
/// Statistics about the Epic game mapping database.
/// </summary>
public class EpicMappingStats
{
    public int TotalGames { get; set; }
    public DateTime? LastUpdatedUtc { get; set; }
    public DateTime? OldestGameUtc { get; set; }
    public int DistinctSources { get; set; }
    public int CdnPatterns { get; set; }
}
