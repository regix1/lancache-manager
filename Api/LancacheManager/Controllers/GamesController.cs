using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;

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
    private readonly ISignalRNotificationService _notifications;
    private readonly ILogger<GamesController> _logger;
    private readonly IPathResolver _pathResolver;

    public GamesController(
        GameCacheDetectionService gameCacheDetectionService,
        CacheManagementService cacheManagementService,
        RemovalOperationTracker removalTracker,
        ISignalRNotificationService notifications,
        ILogger<GamesController> logger,
        IPathResolver pathResolver)
    {
        _gameCacheDetectionService = gameCacheDetectionService;
        _cacheManagementService = cacheManagementService;
        _removalTracker = removalTracker;
        _notifications = notifications;
        _logger = logger;
        _pathResolver = pathResolver;
    }

    /// <summary>
    /// DELETE /api/games/{appId} - Remove game from cache
    /// RESTful: DELETE is proper method for removing resources
    /// </summary>
    [HttpDelete("{appId}")]
    [RequireAuth]
    public async Task<IActionResult> RemoveGameFromCache(int appId)
    {
        // CRITICAL: Check write permissions BEFORE starting the operation
        // This prevents operations from failing partway through due to permission issues
        var cacheWritable = _pathResolver.IsCacheDirectoryWritable();
        var logsWritable = _pathResolver.IsLogsDirectoryWritable();

        if (!cacheWritable || !logsWritable)
        {
            var errors = new List<string>();
            if (!cacheWritable) errors.Add("cache directory is read-only");
            if (!logsWritable) errors.Add("logs directory is read-only");

            var errorMessage = $"Cannot remove game from cache: {string.Join(" and ", errors)}. " +
                "This is typically caused by incorrect PUID/PGID settings in your docker-compose.yml. " +
                "The lancache container usually runs as UID/GID 33:33 (www-data).";

            _logger.LogWarning("[RemoveGameFromCache] Permission check failed for AppId {AppId}: {Error}", appId, errorMessage);
            return BadRequest(new ErrorResponse { Error = errorMessage });
        }

        _logger.LogInformation("Starting background game removal for AppId: {AppId}", appId);

        // Get game name for tracking
        var cachedResults = await _gameCacheDetectionService.GetCachedDetectionAsync();
        var gameName = cachedResults?.Games?.FirstOrDefault(g => g.GameAppId == appId)?.GameName ?? $"Game {appId}";

        // Start tracking this removal operation
        _removalTracker.StartGameRemoval(appId, gameName);

        // Fire-and-forget background removal with SignalR notification
        _ = Task.Run(async () =>
        {
            try
            {
                // Send progress update
                await _notifications.NotifyAllAsync(SignalREvents.GameRemovalProgress,
                    new GameRemovalProgress(appId, gameName, "removing_cache", $"Deleting cache files for {gameName}..."));
                _removalTracker.UpdateGameRemoval(appId, "removing_cache", $"Deleting cache files for {gameName}...");

                // Use CacheManagementService which actually deletes files via Rust binary
                var report = await _cacheManagementService.RemoveGameFromCache((uint)appId);

                // Send progress update
                await _notifications.NotifyAllAsync(SignalREvents.GameRemovalProgress,
                    new GameRemovalProgress(appId, gameName, "removing_database", "Updating database...", report.CacheFilesDeleted, (long)report.TotalBytesFreed));
                _removalTracker.UpdateGameRemoval(appId, "removing_database", "Updating database...", report.CacheFilesDeleted, (long)report.TotalBytesFreed);

                // Also remove from detection cache so it doesn't show in UI
                await _gameCacheDetectionService.RemoveGameFromCacheAsync((uint)appId);

                _logger.LogInformation("Game removal completed for AppId: {AppId} - Deleted {Files} files, freed {Bytes} bytes",
                    appId, report.CacheFilesDeleted, report.TotalBytesFreed);

                // Complete tracking
                _removalTracker.CompleteGameRemoval(appId, true, report.CacheFilesDeleted, (long)report.TotalBytesFreed);

                // Send SignalR notification on success
                await _notifications.NotifyAllAsync(SignalREvents.GameRemovalComplete,
                    new GameRemovalComplete(true, appId, gameName, $"Successfully removed {gameName} from cache", report.CacheFilesDeleted, (long)report.TotalBytesFreed, report.LogEntriesRemoved));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during game removal for AppId: {AppId}", appId);

                // Complete tracking with error
                _removalTracker.CompleteGameRemoval(appId, false, error: ex.Message);

                // Send SignalR notification on failure
                await _notifications.NotifyAllAsync(SignalREvents.GameRemovalComplete,
                    new GameRemovalComplete(false, appId, Message: $"Failed to remove game {appId}: {ex.Message}"));
            }
        });

        return Accepted(new GameRemovalStartResponse
        {
            Message = $"Started removal of game {appId} from cache",
            AppId = appId,
            GameName = gameName,
            Status = "running"
        });
    }

    /// <summary>
    /// GET /api/games/{appId}/removal-status - Get status of game removal operation
    /// Used for restoring progress on page refresh
    /// </summary>
    [HttpGet("{appId}/removal-status")]
    [RequireGuestSession]
    public IActionResult GetGameRemovalStatus(int appId)
    {
        var operation = _removalTracker.GetGameRemovalStatus(appId);
        if (operation == null)
        {
            return Ok(new RemovalStatusResponse { IsProcessing = false });
        }

        return Ok(new RemovalStatusResponse
        {
            // Include all non-terminal statuses (running, removing_cache, removing_database, etc.)
            IsProcessing = operation.Status != "complete" && operation.Status != "failed",
            Status = operation.Status,
            Message = operation.Message,
            GameName = operation.Name,
            FilesDeleted = operation.FilesDeleted,
            BytesFreed = operation.BytesFreed,
            StartedAt = operation.StartedAt,
            Error = operation.Error
        });
    }

    /// <summary>
    /// GET /api/games/removals/active - Get all active game removal operations
    /// Used for universal recovery on page refresh
    /// </summary>
    [HttpGet("removals/active")]
    [RequireGuestSession]
    public IActionResult GetActiveGameRemovals()
    {
        var operations = _removalTracker.GetActiveGameRemovals();
        return Ok(new ActiveGameRemovalsResponse
        {
            IsProcessing = operations.Any(),
            Operations = operations.Select(o => new GameRemovalInfo
            {
                GameAppId = int.Parse(o.Id),
                GameName = o.Name,
                Status = o.Status,
                Message = o.Message,
                FilesDeleted = o.FilesDeleted,
                BytesFreed = o.BytesFreed,
                StartedAt = o.StartedAt
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

            return Accepted(new GameDetectionStartResponse
            {
                Message = forceRefresh ? "Full scan started" : "Incremental scan started",
                OperationId = operationId,
                Status = "running"
            });
        }
        catch (InvalidOperationException ex)
        {
            // Specific handling for "already running" case - return 409 Conflict
            _logger.LogWarning(ex, "Cannot start game detection - already running");
            return Conflict(new ConflictResponse { Error = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/games/detect/active - Get currently running detection operation
    /// </summary>
    [HttpGet("detect/active")]
    [RequireGuestSession]
    public IActionResult GetActiveDetection()
    {
        var activeOperation = _gameCacheDetectionService.GetActiveOperation();

        if (activeOperation == null)
        {
            return Ok(new ActiveDetectionResponse { IsProcessing = false, Operation = null });
        }

        return Ok(new ActiveDetectionResponse { IsProcessing = true, Operation = activeOperation });
    }

    /// <summary>
    /// GET /api/games/detect/{id}/status - Get status of specific detection operation
    /// </summary>
    [HttpGet("detect/{id}/status")]
    [RequireGuestSession]
    public IActionResult GetDetectionStatus(string id)
    {
        var status = _gameCacheDetectionService.GetOperationStatus(id);

        if (status == null)
        {
            return NotFound(new NotFoundResponse { Error = "Detection operation not found", OperationId = id });
        }

        return Ok(status);
    }

    /// <summary>
    /// GET /api/games/detect/cached - Get cached detection results
    /// </summary>
    [HttpGet("detect/cached")]
    [RequireGuestSession]
    public async Task<IActionResult> GetCachedDetectionResults()
    {
        var cachedResults = await _gameCacheDetectionService.GetCachedDetectionAsync();

        if (cachedResults == null)
        {
            // Return success with hasCachedResults: false instead of 404
            return Ok(new CachedDetectionResponse { HasCachedResults = false });
        }

        // Return in the format expected by frontend
        // Ensure StartTime is treated as UTC for proper timezone conversion on frontend
        var lastDetectionTimeUtc = cachedResults.StartTime.AsUtc();

        return Ok(new CachedDetectionResponse
        {
            HasCachedResults = true,
            Games = cachedResults.Games,
            Services = cachedResults.Services,
            TotalGamesDetected = cachedResults.TotalGamesDetected,
            TotalServicesDetected = cachedResults.TotalServicesDetected,
            LastDetectionTime = lastDetectionTimeUtc.ToString("o") // ISO 8601 format with UTC indicator
        });
    }

}
