using LancacheManager.Core.Services;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    private readonly GameCacheDetectionService _gameCacheDetectionService;

    public GameDetectionController(
        GameCacheDetectionService gameCacheDetectionService)
    {
        _gameCacheDetectionService = gameCacheDetectionService;
    }

    /// <summary>
    /// Returns evicted games — games whose cache files have been removed from disk
    /// by nginx eviction, but still exist in the Downloads database with IsEvicted=true.
    /// </summary>
    [HttpGet("evicted-games")]
    public async Task<IActionResult> GetEvictedGamesAsync()
    {
        var cachedResults = await _gameCacheDetectionService.GetCachedDetectionAsync();

        if (cachedResults?.Games == null)
        {
            return Ok(Array.Empty<GameCacheInfo>());
        }

        var evictedGames = cachedResults.Games
            .Where(game => game.IsEvicted)
            .ToList();

        return Ok(evictedGames);
    }
}
