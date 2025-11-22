using LancacheManager.Application.Services;
using LancacheManager.Infrastructure.Services.Interfaces;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for game cache detection and management
/// Handles game detection operations and game-specific cache removal
/// </summary>
[ApiController]
[Route("api/games")]
public class GamesController : ControllerBase
{
    private readonly GameCacheDetectionService _gameCacheDetectionService;
    private readonly ILogger<GamesController> _logger;
    private readonly IPathResolver _pathResolver;

    public GamesController(
        GameCacheDetectionService gameCacheDetectionService,
        ILogger<GamesController> logger,
        IPathResolver pathResolver)
    {
        _gameCacheDetectionService = gameCacheDetectionService;
        _logger = logger;
        _pathResolver = pathResolver;
    }

    /// <summary>
    /// GET /api/games - List games in cache
    /// This could be expanded to return actual game list if needed
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetGames()
    {
        try
        {
            // For now, return cached detection results
            // Could be expanded to scan cache and return actual game list
            var cachedResults = await _gameCacheDetectionService.GetCachedDetectionAsync();
            return Ok(new
            {
                message = "Use GET /api/games/detect/cached for detection results",
                cachedResults
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting games list");
            return StatusCode(500, new { error = "Failed to get games list", details = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/games/{appId} - Get game details (if available)
    /// </summary>
    [HttpGet("{appId}")]
    public async Task<IActionResult> GetGame(int appId)
    {
        try
        {
            var cachedResults = await _gameCacheDetectionService.GetCachedDetectionAsync();
            var gameInfo = cachedResults?.Games?.FirstOrDefault(g => g.GameAppId == appId);

            if (gameInfo == null)
            {
                return NotFound(new { error = $"Game not found: {appId}" });
            }

            return Ok(gameInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting game: {AppId}", appId);
            return StatusCode(500, new { error = $"Failed to get game: {appId}", details = ex.Message });
        }
    }

    /// <summary>
    /// DELETE /api/games/{appId} - Remove game from cache
    /// RESTful: DELETE is proper method for removing resources
    /// </summary>
    [HttpDelete("{appId}")]
    [RequireAuth]
    public IActionResult RemoveGameFromCache(int appId)
    {
        try
        {
            _logger.LogInformation("Starting background game removal for AppId: {AppId}", appId);

            // Fire-and-forget background removal (no progress tracking needed)
            _ = Task.Run(async () =>
            {
                try
                {
                    await _gameCacheDetectionService.RemoveGameFromCacheAsync((uint)appId);
                    _logger.LogInformation("Game removal completed for AppId: {AppId}", appId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during game removal for AppId: {AppId}", appId);
                }
            });

            return Accepted(new
            {
                message = $"Started removal of game {appId} from cache",
                appId,
                status = "running"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting game removal for AppId: {AppId}", appId);
            return StatusCode(500, new
            {
                error = $"Failed to start game removal for AppId: {appId}",
                details = ex.Message
            });
        }
    }

    /// <summary>
    /// POST /api/games/detect - Start game detection in cache
    /// Note: POST is acceptable as this starts an asynchronous operation
    /// </summary>
    [HttpPost("detect")]
    [RequireAuth]
    public IActionResult DetectGames()
    {
        try
        {
            var operationId = _gameCacheDetectionService.StartDetectionAsync();
            _logger.LogInformation("Started game detection operation: {OperationId}", operationId);

            return Accepted(new
            {
                message = "Game detection started",
                operationId,
                status = "running"
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Cannot start game detection - already running");
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting game detection");
            return StatusCode(500, new { error = "Failed to start game detection", details = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/games/detect/active - Get currently running detection operation
    /// </summary>
    [HttpGet("detect/active")]
    public IActionResult GetActiveDetection()
    {
        try
        {
            var activeOperation = _gameCacheDetectionService.GetActiveOperation();

            if (activeOperation == null)
            {
                return NotFound(new { error = "No active detection operation" });
            }

            return Ok(activeOperation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting active detection");
            return StatusCode(500, new { error = "Failed to get active detection" });
        }
    }

    /// <summary>
    /// GET /api/games/detect/{id}/status - Get status of specific detection operation
    /// </summary>
    [HttpGet("detect/{id}/status")]
    public IActionResult GetDetectionStatus(string id)
    {
        try
        {
            var status = _gameCacheDetectionService.GetOperationStatus(id);

            if (status == null)
            {
                return NotFound(new { error = "Detection operation not found", operationId = id });
            }

            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting detection status for operation {OperationId}", id);
            return StatusCode(500, new { error = "Failed to get detection status" });
        }
    }

    /// <summary>
    /// GET /api/games/detect/cached - Get cached detection results
    /// </summary>
    [HttpGet("detect/cached")]
    public async Task<IActionResult> GetCachedDetectionResults()
    {
        try
        {
            var cachedResults = await _gameCacheDetectionService.GetCachedDetectionAsync();

            if (cachedResults == null)
            {
                return NotFound(new { error = "No cached detection results available" });
            }

            return Ok(cachedResults);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cached detection results");
            return StatusCode(500, new { error = "Failed to get cached detection results" });
        }
    }
}
