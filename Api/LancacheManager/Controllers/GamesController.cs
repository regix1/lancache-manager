using LancacheManager.Application.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.Interfaces;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

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
    private readonly CacheManagementService _cacheManagementService;
    private readonly RemovalOperationTracker _removalTracker;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly ILogger<GamesController> _logger;
    private readonly IPathResolver _pathResolver;

    public GamesController(
        GameCacheDetectionService gameCacheDetectionService,
        CacheManagementService cacheManagementService,
        RemovalOperationTracker removalTracker,
        IHubContext<DownloadHub> hubContext,
        ILogger<GamesController> logger,
        IPathResolver pathResolver)
    {
        _gameCacheDetectionService = gameCacheDetectionService;
        _cacheManagementService = cacheManagementService;
        _removalTracker = removalTracker;
        _hubContext = hubContext;
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

            // Get game name for tracking
            var cachedResults = _gameCacheDetectionService.GetCachedDetectionAsync().GetAwaiter().GetResult();
            var gameName = cachedResults?.Games?.FirstOrDefault(g => g.GameAppId == appId)?.GameName ?? $"Game {appId}";

            // Start tracking this removal operation
            _removalTracker.StartGameRemoval(appId, gameName);

            // Fire-and-forget background removal with SignalR notification
            _ = Task.Run(async () =>
            {
                try
                {
                    // Send progress update
                    await _hubContext.Clients.All.SendAsync("GameRemovalProgress", new
                    {
                        gameAppId = appId,
                        gameName,
                        status = "removing_cache",
                        message = $"Deleting cache files for {gameName}..."
                    });
                    _removalTracker.UpdateGameRemoval(appId, "removing_cache", $"Deleting cache files for {gameName}...");

                    // Use CacheManagementService which actually deletes files via Rust binary
                    var report = await _cacheManagementService.RemoveGameFromCache((uint)appId);

                    // Send progress update
                    await _hubContext.Clients.All.SendAsync("GameRemovalProgress", new
                    {
                        gameAppId = appId,
                        gameName,
                        status = "removing_database",
                        message = $"Updating database...",
                        filesDeleted = report.CacheFilesDeleted,
                        bytesFreed = report.TotalBytesFreed
                    });
                    _removalTracker.UpdateGameRemoval(appId, "removing_database", "Updating database...", report.CacheFilesDeleted, (long)report.TotalBytesFreed);

                    // Also remove from detection cache so it doesn't show in UI
                    await _gameCacheDetectionService.RemoveGameFromCacheAsync((uint)appId);

                    _logger.LogInformation("Game removal completed for AppId: {AppId} - Deleted {Files} files, freed {Bytes} bytes",
                        appId, report.CacheFilesDeleted, report.TotalBytesFreed);

                    // Complete tracking
                    _removalTracker.CompleteGameRemoval(appId, true, report.CacheFilesDeleted, (long)report.TotalBytesFreed);

                    // Send SignalR notification on success
                    await _hubContext.Clients.All.SendAsync("GameRemovalComplete", new
                    {
                        success = true,
                        gameAppId = appId,
                        gameName,
                        filesDeleted = report.CacheFilesDeleted,
                        bytesFreed = report.TotalBytesFreed,
                        logEntriesRemoved = report.LogEntriesRemoved,
                        message = $"Successfully removed {gameName} from cache"
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during game removal for AppId: {AppId}", appId);

                    // Complete tracking with error
                    _removalTracker.CompleteGameRemoval(appId, false, error: ex.Message);

                    // Send SignalR notification on failure
                    await _hubContext.Clients.All.SendAsync("GameRemovalComplete", new
                    {
                        success = false,
                        gameAppId = appId,
                        message = $"Failed to remove game {appId}: {ex.Message}"
                    });
                }
            });

            return Accepted(new
            {
                message = $"Started removal of game {appId} from cache",
                appId,
                gameName,
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
    /// GET /api/games/{appId}/removal-status - Get status of game removal operation
    /// Used for restoring progress on page refresh
    /// </summary>
    [HttpGet("{appId}/removal-status")]
    public IActionResult GetGameRemovalStatus(int appId)
    {
        var operation = _removalTracker.GetGameRemovalStatus(appId);
        if (operation == null)
        {
            return Ok(new { isProcessing = false });
        }

        return Ok(new
        {
            isProcessing = operation.Status == "running",
            status = operation.Status,
            message = operation.Message,
            gameName = operation.Name,
            filesDeleted = operation.FilesDeleted,
            bytesFreed = operation.BytesFreed,
            startedAt = operation.StartedAt,
            error = operation.Error
        });
    }

    /// <summary>
    /// GET /api/games/removals/active - Get all active game removal operations
    /// Used for universal recovery on page refresh
    /// </summary>
    [HttpGet("removals/active")]
    public IActionResult GetActiveGameRemovals()
    {
        var operations = _removalTracker.GetActiveGameRemovals();
        return Ok(new
        {
            hasActiveOperations = operations.Any(),
            operations = operations.Select(o => new
            {
                gameAppId = int.Parse(o.Id),
                gameName = o.Name,
                status = o.Status,
                message = o.Message,
                filesDeleted = o.FilesDeleted,
                bytesFreed = o.BytesFreed,
                startedAt = o.StartedAt
            })
        });
    }

    /// <summary>
    /// POST /api/games/detect - Start game detection in cache
    /// Note: POST is acceptable as this starts an asynchronous operation
    /// </summary>
    [HttpPost("detect")]
    [RequireAuth]
    public IActionResult DetectGames([FromQuery] bool forceRefresh = false)
    {
        try
        {
            // forceRefresh=true means full scan (incremental=false)
            // forceRefresh=false means quick scan (incremental=true)
            var incremental = !forceRefresh;
            var operationId = _gameCacheDetectionService.StartDetectionAsync(incremental);
            _logger.LogInformation("Started game detection operation: {OperationId} (forceRefresh={ForceRefresh}, incremental={Incremental})", operationId, forceRefresh, incremental);

            return Accepted(new
            {
                message = forceRefresh ? "Full scan started" : "Incremental scan started",
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
                return Ok(new { hasActiveOperation = false, operation = (object?)null });
            }

            return Ok(new { hasActiveOperation = true, operation = activeOperation });
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
                // Return success with hasCachedResults: false instead of 404
                return Ok(new { hasCachedResults = false });
            }

            // Return in the format expected by frontend
            // Ensure StartTime is treated as UTC for proper timezone conversion on frontend
            var lastDetectionTimeUtc = DateTime.SpecifyKind(cachedResults.StartTime, DateTimeKind.Utc);

            return Ok(new
            {
                hasCachedResults = true,
                games = cachedResults.Games,
                services = cachedResults.Services,
                totalGamesDetected = cachedResults.TotalGamesDetected,
                totalServicesDetected = cachedResults.TotalServicesDetected,
                lastDetectionTime = lastDetectionTimeUtc.ToString("o") // ISO 8601 format with UTC indicator
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cached detection results");
            return StatusCode(500, new { error = "Failed to get cached detection results" });
        }
    }
}
