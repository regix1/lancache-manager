using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for game detection queries.
/// Provides endpoints for evicted games and other detection-related lookups.
/// </summary>
[ApiController]
[Route("api/game-detection")]
[Authorize]
public class GameDetectionController : ControllerBase
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger<GameDetectionController> _logger;

    public GameDetectionController(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ILogger<GameDetectionController> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Returns evicted games — games whose cache files have been removed from disk
    /// by nginx eviction, but still exist in the Downloads database with IsEvicted=true.
    /// </summary>
    [HttpGet("evicted-games")]
    public async Task<IActionResult> GetEvictedGamesAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var evictedDownloads = await dbContext.Downloads
            .AsNoTracking()
            .Where(d => d.IsEvicted && (d.GameAppId != null || d.EpicAppId != null))
            .ToListAsync();

        if (evictedDownloads.Count == 0)
        {
            return Ok(Array.Empty<GameCacheInfo>());
        }

        // Group by game identity: GameAppId for Steam, EpicAppId for Epic
        var grouped = evictedDownloads
            .GroupBy(d => GetGameGroupKey(d))
            .Select(g =>
            {
                var first = g.First();
                var service = first.Service?.ToLowerInvariant() ?? "steam";
                var isEpic = service == "epicgames";

                return new GameCacheInfo
                {
                    GameAppId = first.GameAppId ?? first.DepotId ?? 0,
                    GameName = first.GameName ?? "Unknown Game",
                    CacheFilesFound = g.Count(),
                    TotalSizeBytes = (ulong)g.Sum(d => d.TotalBytes > 0 ? d.TotalBytes : 0),
                    DepotIds = g.Where(d => d.DepotId.HasValue)
                                .Select(d => (uint)d.DepotId!.Value)
                                .Distinct()
                                .ToList(),
                    SampleUrls = new List<string>(),
                    CacheFilePaths = new List<string>(),
                    Datasources = g.Select(d => d.Datasource).Distinct().ToList(),
                    Service = service,
                    ImageUrl = first.GameImageUrl,
                    EpicAppId = isEpic ? first.EpicAppId : null,
                    IsEvicted = true
                };
            })
            .ToList();

        return Ok(grouped);
    }

    /// <summary>
    /// Creates a grouping key for a download based on its game identity.
    /// Steam games use GameAppId, Epic games use EpicAppId.
    /// Only called for downloads that have GameAppId or EpicAppId set.
    /// </summary>
    private static string GetGameGroupKey(Download d)
    {
        if (!string.IsNullOrEmpty(d.EpicAppId))
            return $"epic:{d.EpicAppId}";

        if (d.GameAppId.HasValue && d.GameAppId.Value > 0)
            return $"steam:{d.GameAppId.Value}";

        // Fallback: should not be reached given the query filter, but group by depot if somehow reached
        return $"depot:{d.DepotId ?? d.Id}";
    }
}
