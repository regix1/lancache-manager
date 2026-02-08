using LancacheManager.Models;
using LancacheManager.Core.Models;
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
    private readonly ISignalRNotificationService _notifications;
    private readonly ILogger<GamesController> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly IUnifiedOperationTracker _operationTracker;

    public GamesController(
        GameCacheDetectionService gameCacheDetectionService,
        CacheManagementService cacheManagementService,
        ISignalRNotificationService notifications,
        ILogger<GamesController> logger,
        IPathResolver pathResolver,
        IUnifiedOperationTracker operationTracker)
    {
        _gameCacheDetectionService = gameCacheDetectionService;
        _cacheManagementService = cacheManagementService;
        _notifications = notifications;
        _logger = logger;
        _pathResolver = pathResolver;
        _operationTracker = operationTracker;
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

        // Create a CancellationTokenSource for cancel support
        var cancellationTokenSource = new CancellationTokenSource();

        // Register with unified operation tracker for centralized cancellation and tracking
        var removalMetrics = new RemovalMetrics { EntityKey = appId.ToString(), EntityName = gameName };
        var operationId = _operationTracker.RegisterOperation(
            OperationType.GameRemoval,
            $"Game Removal: {gameName}",
            cancellationTokenSource,
            removalMetrics
        );

        // Send GameRemovalStarted event
        await _notifications.NotifyAllAsync(SignalREvents.GameRemovalStarted,
            new GameRemovalStarted(operationId, appId, gameName, $"Starting removal of {gameName}...", DateTime.UtcNow));

        // Fire-and-forget background removal with SignalR notification
        var cancellationToken = cancellationTokenSource.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Send starting notification
                await _notifications.NotifyAllAsync(SignalREvents.GameRemovalProgress,
                    new GameRemovalProgress(operationId, appId, gameName, "starting", $"Starting removal of {gameName}..."));
                _operationTracker.UpdateProgress(operationId, 0, $"Starting removal of {gameName}...");

                cancellationToken.ThrowIfCancellationRequested();

                // Call with progress callback that sends live SignalR updates
                var report = await _cacheManagementService.RemoveGameFromCache((uint)appId, cancellationToken,
                    async (percentComplete, message, filesDeleted, bytesFreed) =>
                    {
                        await _notifications.NotifyAllAsync(SignalREvents.GameRemovalProgress,
                            new GameRemovalProgress(operationId, appId, gameName, "removing_cache", message, percentComplete, filesDeleted, bytesFreed));
                        _operationTracker.UpdateProgress(operationId, percentComplete, message);
                        _operationTracker.UpdateMetadata(operationId, m =>
                        {
                            var metrics = (RemovalMetrics)m;
                            metrics.FilesDeleted = filesDeleted;
                            metrics.BytesFreed = bytesFreed;
                        });
                    });

                cancellationToken.ThrowIfCancellationRequested();

                // Send finalizing progress update
                await _notifications.NotifyAllAsync(SignalREvents.GameRemovalProgress,
                    new GameRemovalProgress(operationId, appId, gameName, "complete", "Finalizing removal...", 100.0, report.CacheFilesDeleted, (long)report.TotalBytesFreed));
                _operationTracker.UpdateProgress(operationId, 100.0, "Finalizing removal...");
                _operationTracker.UpdateMetadata(operationId, m =>
                {
                    var metrics = (RemovalMetrics)m;
                    metrics.FilesDeleted = report.CacheFilesDeleted;
                    metrics.BytesFreed = (long)report.TotalBytesFreed;
                });

                // Also remove from detection cache so it doesn't show in UI
                await _gameCacheDetectionService.RemoveGameFromCacheAsync((uint)appId);

                _logger.LogInformation("Game removal completed for AppId: {AppId} - Deleted {Files} files, freed {Bytes} bytes",
                    appId, report.CacheFilesDeleted, report.TotalBytesFreed);

                // Mark operation as complete in unified tracker
                _operationTracker.CompleteOperation(operationId, success: true);

                // Send SignalR notification on success
                await _notifications.NotifyAllAsync(SignalREvents.GameRemovalComplete,
                    new GameRemovalComplete(true, operationId, appId, gameName, $"Successfully removed {gameName} from cache", report.CacheFilesDeleted, (long)report.TotalBytesFreed, report.LogEntriesRemoved));
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Game removal cancelled for AppId: {AppId}", appId);

                // Mark operation as complete (cancelled) in unified tracker
                _operationTracker.CompleteOperation(operationId, success: false, error: "Cancelled by user");

                // Send SignalR notification on cancellation
                await _notifications.NotifyAllAsync(SignalREvents.GameRemovalComplete,
                    new GameRemovalComplete(false, operationId, appId, gameName, $"Removal of {gameName} was cancelled"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during game removal for AppId: {AppId}", appId);

                // Send error status notification
                await _notifications.NotifyAllAsync(SignalREvents.GameRemovalProgress,
                    new GameRemovalProgress(operationId, appId, gameName, "error", $"Error removing {gameName}: {ex.Message}", 0.0));

                // Mark operation as complete (failed) in unified tracker
                _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);

                // Send SignalR notification on failure
                await _notifications.NotifyAllAsync(SignalREvents.GameRemovalComplete,
                    new GameRemovalComplete(false, operationId, appId, Message: $"Failed to remove game {appId}: {ex.Message}"));
            }
        }, cancellationToken);

        return Accepted(new GameRemovalStartResponse
        {
            Message = $"Started removal of game {appId} from cache",
            OperationId = operationId,
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
        var operation = _operationTracker.GetOperationByEntityKey(OperationType.GameRemoval, appId.ToString());
        if (operation == null)
        {
            return Ok(new RemovalStatusResponse { IsProcessing = false });
        }

        var metrics = operation.Metadata as RemovalMetrics;
        return Ok(new RemovalStatusResponse
        {
            IsProcessing = operation.Status != OperationStatus.Completed && operation.Status != OperationStatus.Failed,
            Status = operation.Status,
            Message = operation.Message,
            GameName = metrics?.EntityName,
            FilesDeleted = metrics?.FilesDeleted ?? 0,
            BytesFreed = metrics?.BytesFreed ?? 0,
            StartedAt = operation.StartedAt,
            OperationId = operation.Id
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
        var operations = _operationTracker.GetActiveOperations(OperationType.GameRemoval);
        return Ok(new ActiveGameRemovalsResponse
        {
            IsProcessing = operations.Any(),
            Operations = operations.Select(op =>
            {
                var metrics = op.Metadata as RemovalMetrics;
                return new GameRemovalInfo
                {
                    GameAppId = int.TryParse(metrics?.EntityKey, out var parsedAppId) ? parsedAppId : 0,
                    GameName = metrics?.EntityName ?? op.Name,
                    Status = op.Status,
                    Message = op.Message,
                    FilesDeleted = metrics?.FilesDeleted ?? 0,
                    BytesFreed = metrics?.BytesFreed ?? 0,
                    StartedAt = op.StartedAt
                };
            })
        });
    }

    /// <summary>
    /// POST /api/games/detect - Start game detection in cache
    /// Note: POST is acceptable as this starts an asynchronous operation
    /// </summary>
    [HttpPost("detect")]
    [RequireAuth]
    public async Task<IActionResult> DetectGames([FromQuery] bool forceRefresh = false)
    {
        try
        {
            // forceRefresh=true means full scan (incremental=false)
            // forceRefresh=false means quick scan (incremental=true)
            var incremental = !forceRefresh;
            var operationId = await _gameCacheDetectionService.StartDetectionAsync(incremental);

            if (operationId == null)
            {
                // Already running - return 409 Conflict
                return Conflict(new ConflictResponse { Error = "Game detection is already running" });
            }

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
        // GameDetectionMetrics.StartTime carries the last detection timestamp from DB
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
