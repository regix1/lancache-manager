using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

public sealed partial class GameCacheDetectionDataService
{
    public async Task RemoveGameFromCacheAsync(long gameAppId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var game = await dbContext.CachedGameDetections
            .FirstOrDefaultAsync(g => g.GameAppId == gameAppId, cancellationToken);

        if (game == null)
        {
            return;
        }

        dbContext.CachedGameDetections.Remove(game);
        await dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "[GameDetection] Removed game {AppId} ({GameName}) from cache",
            gameAppId,
            game.GameName);
    }

    public async Task RemoveServiceFromCacheAsync(
        string serviceName,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var normalizedServiceName = serviceName.ToLower();
        var service = await dbContext.CachedServiceDetections
            .FirstOrDefaultAsync(s => s.ServiceName.ToLower() == normalizedServiceName, cancellationToken);

        if (service == null)
        {
            return;
        }

        dbContext.CachedServiceDetections.Remove(service);
        await dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("[GameDetection] Removed service '{ServiceName}' from cache", serviceName);
    }

    /// <summary>
    /// Removes the cached detection row for a named (Blizzard/Riot/Xbox) game whose identity is
    /// (Service, GameName) with both Steam and Epic ids null. The named twin of
    /// <see cref="RemoveGameFromCacheAsync"/> (Steam, keyed by GameAppId) and
    /// <see cref="RemoveServiceFromCacheAsync"/>. Service is matched case-insensitively because the
    /// Xbox cache-split stores <c>Service='xbox'</c> lowercase while callers may pass mixed case.
    /// </summary>
    public async Task RemoveNamedGameFromCacheAsync(
        string service,
        string gameName,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var normalizedServiceName = service.ToLower();
        var rows = await dbContext.CachedGameDetections
            .Where(g => g.GameAppId == 0
                && g.EpicAppId == null
                && g.Service != null
                && g.Service.ToLower() == normalizedServiceName
                && g.GameName == gameName)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return;
        }

        dbContext.CachedGameDetections.RemoveRange(rows);
        await dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "[GameDetection] Removed named game '{Service}' / '{GameName}' from cache ({Count} row(s))",
            service, gameName, rows.Count);
    }

    /// <summary>
    /// Removes the cached detection row for an Epic game. Epic detection rows carry a non-null
    /// EpicAppId; removal is keyed by GameName, mirroring the Rust <c>cache_epic_remove</c> delete
    /// (<c>DELETE ... WHERE "GameName" = $1 AND "EpicAppId" IS NOT NULL</c>). The Epic twin of
    /// <see cref="RemoveGameFromCacheAsync"/> (Steam) and <see cref="RemoveNamedGameFromCacheAsync"/>.
    /// </summary>
    public async Task RemoveEpicGameFromCacheAsync(
        string gameName,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await dbContext.CachedGameDetections
            .Where(g => g.EpicAppId != null && g.GameName == gameName)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return;
        }

        dbContext.CachedGameDetections.RemoveRange(rows);
        await dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "[GameDetection] Removed Epic game '{GameName}' from cache ({Count} row(s))",
            gameName, rows.Count);
    }
}
