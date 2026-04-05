using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.AspNetCore.Mvc;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;
using Microsoft.AspNetCore.Authorization;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for game cache detection and management
/// Handles game detection operations and game-specific cache removal
/// </summary>
[ApiController]
[Route("api/games")]
[Authorize(Policy = "AdminOnly")]
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
    /// Checks cache and logs directory write permissions.
    /// Returns a BadRequest IActionResult with PUID/PGID error message if not writable, or null if writable.
    /// </summary>
    private BadRequestObjectResult? EnsureDirectoriesWritable(string operationDescription)
    {
        var cacheWritable = _pathResolver.IsCacheDirectoryWritable();
        var logsWritable = _pathResolver.IsLogsDirectoryWritable();

        if (cacheWritable && logsWritable)
            return null;

        var errors = new List<string>();
        if (!cacheWritable) errors.Add("cache directory is read-only");
        if (!logsWritable) errors.Add("logs directory is read-only");

        var errorMessage = $"Cannot {operationDescription}: {string.Join(" and ", errors)}. " +
            "This is typically caused by incorrect PUID/PGID settings in your docker-compose.yml. " +
            $"The lancache container is configured to run as UID/GID {ContainerEnvironment.UidGid} (configured via PUID/PGID environment variables).";

        return BadRequest(new ErrorResponse { Error = errorMessage });
    }

    /// <summary>
    /// DELETE /api/games/{appId} - Remove game from cache
    /// RESTful: DELETE is proper method for removing resources
    /// </summary>
    [HttpDelete("{appId}")]
    public async Task<IActionResult> RemoveGameFromCacheAsync(long appId)
    {
        // CRITICAL: Check write permissions BEFORE starting the operation
        var permissionError = EnsureDirectoriesWritable("remove game from cache");
        if (permissionError != null)
        {
            _logger.LogWarning("[RemoveGameFromCache] Permission check failed for AppId {AppId}", appId);
            return permissionError;
        }

        _logger.LogInformation("Starting background game removal for AppId: {AppId}", appId);

        // Get game name for tracking
        var cachedResults = await _gameCacheDetectionService.GetCachedDetectionAsync();
        var gameName = cachedResults?.Games?.FirstOrDefault(g => g.GameAppId == appId)?.GameName ?? $"Game {appId}";

        return await StartRemovalOperationAsync(
            entityKey: appId.ToString(),
            displayName: gameName,
            operationLabel: $"Game Removal: {gameName}",
            appId: appId,
            removeFunc: (CancellationToken ct, Func<double, string, int, long, Task> onProgress) =>
                _cacheManagementService.RemoveGameFromCacheAsync(appId, ct, onProgress),
            onSuccess: async (long _) => await _gameCacheDetectionService.RemoveGameFromCacheAsync(appId),
            responseMessage: $"Started removal of game {appId} from cache");
    }

    /// <summary>
    /// DELETE /api/games/epic/{gameName} - Remove Epic game by name
    /// Uses the Rust cache_epic_remove binary to delete cache files, log entries,
    /// and database records - same three-step process as Steam game removal.
    /// </summary>
    [HttpDelete("epic/{gameName}")]
    public async Task<IActionResult> RemoveEpicGameFromCacheAsync(string gameName)
    {
        // Check write permissions before starting
        var permissionError = EnsureDirectoriesWritable("remove Epic game from cache");
        if (permissionError != null)
        {
            _logger.LogWarning("[RemoveEpicGame] Permission check failed for '{GameName}'", gameName);
            return permissionError;
        }

        _logger.LogInformation("Starting background Epic game removal for: {GameName}", gameName);

        return await StartRemovalOperationAsync(
            entityKey: $"epic-{gameName}",
            displayName: gameName,
            operationLabel: $"Epic Game Removal: {gameName}",
            appId: 0,
            removeFunc: (CancellationToken ct, Func<double, string, int, long, Task> onProgress) =>
                _cacheManagementService.RemoveEpicGameFromCacheAsync(gameName, ct, onProgress),
            onSuccess: null,
            responseMessage: $"Started removal of Epic game {gameName} from cache");
    }

    /// <summary>
    /// Shared background removal wrapper for both Steam and Epic game removal.
    /// Handles: tracker registration, Started event, Task.Run with progress/complete/error/cancel.
    /// </summary>
    /// <param name="entityKey">Tracker entity key (e.g., "123" for Steam, "epic-GameName" for Epic)</param>
    /// <param name="displayName">Game display name for notifications</param>
    /// <param name="operationLabel">Operation label for the tracker (e.g., "Game Removal: Halo")</param>
    /// <param name="appId">Steam AppId (0 for Epic games)</param>
    /// <param name="removeFunc">The actual removal function that accepts cancellation token and progress callback</param>
    /// <param name="onSuccess">Optional callback after successful removal (e.g., Steam removes from detection cache)</param>
    /// <param name="responseMessage">Message for the HTTP Accepted response</param>
    private async Task<IActionResult> StartRemovalOperationAsync(
        string entityKey,
        string displayName,
        string operationLabel,
        long appId,
        Func<CancellationToken, Func<double, string, int, long, Task>, Task<CacheManagementService.GameCacheRemovalReport>> removeFunc,
        Func<long, Task>? onSuccess,
        string responseMessage)
    {
        var cancellationTokenSource = new CancellationTokenSource();

        // Register with unified operation tracker for centralized cancellation and tracking
        var removalMetrics = new RemovalMetrics { EntityKey = entityKey, EntityName = displayName };
        var operationId = _operationTracker.RegisterOperation(
            OperationType.GameRemoval,
            operationLabel,
            cancellationTokenSource,
            removalMetrics
        );

        // Send GameRemovalStarted event
        await _notifications.NotifyAllAsync(SignalREvents.GameRemovalStarted,
            new GameRemovalStarted(operationId, appId, displayName, $"Starting removal of {displayName}...", DateTime.UtcNow));

        // Fire-and-forget background removal with SignalR notification
        var cancellationToken = cancellationTokenSource.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Send starting notification
                await _notifications.NotifyAllAsync(SignalREvents.GameRemovalProgress,
                    new GameRemovalProgress(operationId, appId, displayName, "starting", $"Starting removal of {displayName}..."));
                _operationTracker.UpdateProgress(operationId, 0, $"Starting removal of {displayName}...");

                cancellationToken.ThrowIfCancellationRequested();

                // Call with progress callback that sends live SignalR updates
                var report = await removeFunc(cancellationToken,
                    async (double percentComplete, string message, int filesDeleted, long bytesFreed) =>
                    {
                        await _notifications.NotifyAllAsync(SignalREvents.GameRemovalProgress,
                            new GameRemovalProgress(operationId, appId, displayName, "removing_cache", message, percentComplete, filesDeleted, bytesFreed));
                        _operationTracker.UpdateProgress(operationId, percentComplete, message);
                        _operationTracker.UpdateMetadata(operationId, (object m) =>
                        {
                            var metrics = (RemovalMetrics)m;
                            metrics.FilesDeleted = filesDeleted;
                            metrics.BytesFreed = bytesFreed;
                        });
                    });

                cancellationToken.ThrowIfCancellationRequested();

                // Send finalizing progress update
                await _notifications.NotifyAllAsync(SignalREvents.GameRemovalProgress,
                    new GameRemovalProgress(operationId, appId, displayName, "complete", "Finalizing removal...", 100.0, report.CacheFilesDeleted, (long)report.TotalBytesFreed));
                _operationTracker.UpdateProgress(operationId, 100.0, "Finalizing removal...");
                _operationTracker.UpdateMetadata(operationId, (object m) =>
                {
                    var metrics = (RemovalMetrics)m;
                    metrics.FilesDeleted = report.CacheFilesDeleted;
                    metrics.BytesFreed = (long)report.TotalBytesFreed;
                });

                // Run platform-specific success callback (e.g., Steam removes from detection cache)
                if (onSuccess != null)
                {
                    await onSuccess(appId);
                }

                _logger.LogInformation("Game removal completed for {EntityKey} ({DisplayName}) - Deleted {Files} files, freed {Bytes} bytes",
                    entityKey, displayName, report.CacheFilesDeleted, report.TotalBytesFreed);

                // Mark operation as complete in unified tracker
                _operationTracker.CompleteOperation(operationId, success: true);

                // Send SignalR notification on success
                await _notifications.NotifyAllAsync(SignalREvents.GameRemovalComplete,
                    new GameRemovalComplete(true, operationId, appId, displayName, $"Successfully removed {displayName} from cache", report.CacheFilesDeleted, (long)report.TotalBytesFreed, report.LogEntriesRemoved));
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Game removal cancelled for {EntityKey}", entityKey);

                _operationTracker.CompleteOperation(operationId, success: false, error: "Cancelled by user");

                await _notifications.NotifyAllAsync(SignalREvents.GameRemovalComplete,
                    new GameRemovalComplete(false, operationId, appId, displayName, $"Removal of {displayName} was cancelled"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during game removal for {EntityKey}", entityKey);

                await _notifications.NotifyAllAsync(SignalREvents.GameRemovalProgress,
                    new GameRemovalProgress(operationId, appId, displayName, "error", $"Error removing {displayName}: {ex.Message}", 0.0));

                _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);

                await _notifications.NotifyAllAsync(SignalREvents.GameRemovalComplete,
                    new GameRemovalComplete(false, operationId, appId, StageKey: "signalr.gameRemove.error.fatal"));
            }
        }, cancellationToken);

        return Accepted(new GameRemovalStartResponse
        {
            Message = responseMessage,
            OperationId = operationId,
            AppId = appId.ToString(),
            GameName = displayName,
            Status = "running"
        });
    }

    /// <summary>
    /// GET /api/games/{appId}/removal-status - Get status of game removal operation
    /// Used for restoring progress on page refresh
    /// </summary>
    [HttpGet("{appId}/removal-status")]
    public IActionResult GetGameRemovalStatus(long appId)
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
                    GameAppId = long.TryParse(metrics?.EntityKey, out var parsedAppId) ? parsedAppId : 0,
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
    public async Task<IActionResult> DetectGamesAsync([FromQuery] bool forceRefresh = false)
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
    [AllowAnonymous]
    [HttpGet("detect/cached")]
    public async Task<IActionResult> GetCachedDetectionResultsAsync()
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

        var games = cachedResults.Games ?? [];

        // Always include evicted games in the response so the frontend can display them
        // in the Evicted Games section. The EvictedDataMode controls frontend display
        // behavior, not API data availability — stripping here would make evicted games
        // invisible to the frontend even when the user wants to see them.
        // TotalGamesDetected excludes evicted games so the "N games detected" count
        // reflects only active (non-evicted) games on disk.
        var activeGamesCount = games.Count(g => !g.IsEvicted);

        return Ok(new CachedDetectionResponse
        {
            HasCachedResults = true,
            Games = games,
            Services = cachedResults.Services,
            TotalGamesDetected = activeGamesCount,
            TotalServicesDetected = cachedResults.TotalServicesDetected,
            LastDetectionTime = lastDetectionTimeUtc.ToString("o") // ISO 8601 format with UTC indicator
        });
    }


}
