using LancacheManager.Application.DTOs;
using LancacheManager.Application.Services;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Security;
using LancacheManager.Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for cache management operations
/// Handles cache information, clearing, corruption detection, and service/game cache management
/// </summary>
[ApiController]
[Route("api/cache")]
public class CacheController : ControllerBase
{
    private readonly CacheManagementService _cacheService;
    private readonly CacheClearingService _cacheClearingService;
    private readonly GameCacheDetectionService _gameCacheDetectionService;
    private readonly RemovalOperationTracker _removalTracker;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CacheController> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly StateRepository _stateService;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly RustProcessHelper _rustProcessHelper;

    public CacheController(
        CacheManagementService cacheService,
        CacheClearingService cacheClearingService,
        GameCacheDetectionService gameCacheDetectionService,
        RemovalOperationTracker removalTracker,
        IConfiguration configuration,
        ILogger<CacheController> logger,
        IPathResolver pathResolver,
        StateRepository stateService,
        IHubContext<DownloadHub> hubContext,
        RustProcessHelper rustProcessHelper)
    {
        _cacheService = cacheService;
        _cacheClearingService = cacheClearingService;
        _gameCacheDetectionService = gameCacheDetectionService;
        _removalTracker = removalTracker;
        _configuration = configuration;
        _logger = logger;
        _pathResolver = pathResolver;
        _stateService = stateService;
        _hubContext = hubContext;
        _rustProcessHelper = rustProcessHelper;
    }

    /// <summary>
    /// GET /api/cache - Get cache information (size, path, etc.)
    /// </summary>
    [HttpGet]
    public IActionResult GetCacheInfo()
    {
        var info = _cacheService.GetCacheInfo();
        return Ok(info);
    }

    /// <summary>
    /// GET /api/cache/permissions - Check cache directory permissions
    /// </summary>
    [HttpGet("permissions")]
    public IActionResult GetDirectoryPermissions()
    {
        var cachePath = _pathResolver.GetCacheDirectory();
        var cacheWritable = _pathResolver.IsCacheDirectoryWritable();

        return Ok(new DirectoryPermission
        {
            Path = cachePath,
            Writable = cacheWritable,
            ReadOnly = !cacheWritable
        });
    }

    /// <summary>
    /// DELETE /api/cache - Clear all cache (all datasources)
    /// RESTful: DELETE is proper method for clearing/removing resources
    /// </summary>
    [HttpDelete]
    [RequireAuth]
    public async Task<IActionResult> ClearAllCache()
    {
        var operationId = await _cacheClearingService.StartCacheClearAsync();
        _logger.LogInformation("Started cache clear operation for all datasources: {OperationId}", operationId);

        return Accepted(new CacheOperationResponse
        {
            Message = "Cache clearing started in background for all datasources",
            OperationId = operationId,
            Status = "running"
        });
    }

    /// <summary>
    /// DELETE /api/cache/datasources/{name} - Clear cache for a specific datasource
    /// RESTful: DELETE is proper method for clearing/removing resources
    /// </summary>
    [HttpDelete("datasources/{name}")]
    [RequireAuth]
    public async Task<IActionResult> ClearDatasourceCache(string name)
    {
        var operationId = await _cacheClearingService.StartCacheClearAsync(name);
        _logger.LogInformation("Started cache clear operation for datasource {Datasource}: {OperationId}", name, operationId);

        return Accepted(new CacheOperationResponse
        {
            Message = $"Cache clearing started for datasource: {name}",
            OperationId = operationId,
            Status = "running"
        });
    }

    /// <summary>
    /// GET /api/cache/operations - List all active cache operations
    /// </summary>
    [HttpGet("operations")]
    public IActionResult GetActiveOperations()
    {
        var operations = _cacheClearingService.GetActiveOperations();
        var isProcessing = operations.Any(op => op.Status != "completed" && op.Status != "failed" && op.Status != "cancelled");
        return Ok(new ActiveOperationsResponse { IsProcessing = isProcessing, Operations = operations });
    }

    /// <summary>
    /// GET /api/cache/operations/{id}/status - Get status of specific cache clear operation
    /// </summary>
    [HttpGet("operations/{id}/status")]
    public IActionResult GetCacheClearStatus(string id)
    {
        var status = _cacheClearingService.GetCacheClearStatus(id);

        if (status == null)
        {
            return NotFound(new NotFoundResponse { Error = "Cache clear operation not found", OperationId = id });
        }

        return Ok(status);
    }

    /// <summary>
    /// DELETE /api/cache/operations/{id} - Cancel a running cache clear operation
    /// RESTful: DELETE is proper method for cancelling/removing operations
    /// </summary>
    [HttpDelete("operations/{id}")]
    [RequireAuth]
    public IActionResult CancelCacheClear(string id)
    {
        var result = _cacheClearingService.CancelCacheClear(id);

        if (!result)
        {
            return NotFound(new NotFoundResponse { Error = "Cache clear operation not found or already completed", OperationId = id });
        }

        return Ok(new CacheOperationResponse { Message = "Cache clear operation cancelled successfully", OperationId = id });
    }

    /// <summary>
    /// POST /api/cache/operations/{id}/kill - Force kill a cache clear operation's process
    /// Used as fallback when graceful cancellation fails
    /// </summary>
    [HttpPost("operations/{id}/kill")]
    [RequireAuth]
    public async Task<IActionResult> ForceKillCacheClear(string id)
    {
        var result = await _cacheClearingService.ForceKillOperation(id);

        if (!result)
        {
            return NotFound(new NotFoundResponse { Error = "Cache clear operation not found or no process to kill", OperationId = id });
        }

        return Ok(new CacheOperationResponse { Message = "Cache clear operation force killed successfully", OperationId = id });
    }

    /// <summary>
    /// GET /api/cache/corruption/summary - Get corruption summary for all services
    /// </summary>
    [HttpGet("corruption/summary")]
    public async Task<IActionResult> GetCorruptionSummary([FromQuery] bool forceRefresh = false)
    {
        var summary = await _cacheService.GetCorruptionSummary(forceRefresh);
        return Ok(summary);
    }

    /// <summary>
    /// GET /api/cache/services/{name}/corruption - Get detailed corruption info for specific service
    /// Returns array of corrupted chunks with URLs, miss counts, and cache file paths
    /// </summary>
    [HttpGet("services/{service}/corruption")]
    public async Task<IActionResult> GetCorruptionDetails(string service, [FromQuery] bool forceRefresh = false)
    {
        var details = await _cacheService.GetCorruptionDetails(service, forceRefresh);
        return Ok(details);
    }

    /// <summary>
    /// DELETE /api/cache/services/{name}/corruption - Remove corrupted chunks for specific service
    /// RESTful: DELETE is proper method for removing resources
    /// </summary>
    [HttpDelete("services/{service}/corruption")]
    [RequireAuth]
    public IActionResult RemoveCorruptedChunks(string service)
    {
        var cachePath = _pathResolver.GetCacheDirectory();
        var logsPath = _pathResolver.GetLogsDirectory();
        var dbPath = _pathResolver.GetDatabasePath();

        var operationId = Guid.NewGuid().ToString();

        // Start tracking this removal operation
        _removalTracker.StartCorruptionRemoval(service, operationId);

        // Send start notification via SignalR
        _ = _hubContext.Clients.All.SendAsync("CorruptionRemovalStarted", new
        {
            service,
            operationId,
            message = $"Starting corruption removal for {service}...",
            timestamp = DateTime.UtcNow
        });

        _ = Task.Run(async () =>
        {
            try
            {
                // Pause LiveLogMonitorService to prevent file locking issues
                await LiveLogMonitorService.PauseAsync();
                _logger.LogInformation("Paused LiveLogMonitorService for corruption removal");

                // Update tracking
                _removalTracker.UpdateCorruptionRemoval(service, "removing", $"Removing corrupted chunks for {service}...");

                try
                {
                    var result = await _rustProcessHelper.RunCorruptionManagerAsync(
                        "remove",
                        logsPath,
                        cachePath,
                        service: service,
                        progressFile: Path.Combine(_pathResolver.GetOperationsDirectory(), $"corruption_removal_{operationId}.json"),
                        databasePath: dbPath
                    );

                    if (result.Success)
                    {
                        _logger.LogInformation("Corruption removal completed for service: {Service}", service);
                        _removalTracker.CompleteCorruptionRemoval(service, true);
                        await _hubContext.Clients.All.SendAsync("CorruptionRemovalComplete", new
                        {
                            service,
                            operationId,
                            success = true,
                            message = $"Successfully removed corrupted chunks for {service}"
                        });
                    }
                    else
                    {
                        _logger.LogError("Corruption removal failed for service {Service}: {Error}", service, result.Error);
                        _removalTracker.CompleteCorruptionRemoval(service, false, result.Error);
                        await _hubContext.Clients.All.SendAsync("CorruptionRemovalComplete", new
                        {
                            service,
                            operationId,
                            success = false,
                            error = result.Error
                        });
                    }
                }
                finally
                {
                    // Always resume LiveLogMonitorService
                    await LiveLogMonitorService.ResumeAsync();
                    _logger.LogInformation("Resumed LiveLogMonitorService after corruption removal");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during corruption removal for service: {Service}", service);
                _removalTracker.CompleteCorruptionRemoval(service, false, ex.Message);
                await _hubContext.Clients.All.SendAsync("CorruptionRemovalComplete", new
                {
                    service,
                    operationId,
                    success = false,
                    error = ex.Message
                });
            }
        });

        return Accepted(new CacheOperationResponse
        {
            Message = $"Started corruption removal for service: {service}",
            Service = service,
            OperationId = operationId,
            Status = "running"
        });
    }

    /// <summary>
    /// GET /api/cache/services/{service}/corruption/status - Get corruption removal status
    /// Used for restoring progress on page refresh
    /// </summary>
    [HttpGet("services/{service}/corruption/status")]
    public IActionResult GetCorruptionRemovalStatus(string service)
    {
        var operation = _removalTracker.GetCorruptionRemovalStatus(service);
        if (operation == null)
        {
            return Ok(new RemovalStatusResponse { IsProcessing = false });
        }

        return Ok(new RemovalStatusResponse
        {
            // Include all non-terminal statuses (running, removing, etc.)
            IsProcessing = operation.Status != "complete" && operation.Status != "failed",
            Status = operation.Status,
            Message = operation.Message,
            OperationId = operation.Id,
            StartedAt = operation.StartedAt,
            Error = operation.Error
        });
    }

    /// <summary>
    /// GET /api/cache/corruption/removals/active - Get all active corruption removal operations
    /// </summary>
    [HttpGet("corruption/removals/active")]
    public IActionResult GetActiveCorruptionRemovals()
    {
        var operations = _removalTracker.GetActiveCorruptionRemovals();
        return Ok(new ActiveCorruptionRemovalsResponse
        {
            IsProcessing = operations.Any(),
            Operations = operations.Select(o => new CorruptionRemovalInfo
            {
                Service = o.Name,
                OperationId = o.Id,
                Status = o.Status,
                Message = o.Message,
                StartedAt = o.StartedAt
            })
        });
    }

    /// <summary>
    /// DELETE /api/cache/services/{name} - Remove specific service from cache
    /// RESTful: DELETE is proper method for removing resources
    /// </summary>
    [HttpDelete("services/{name}")]
    [RequireAuth]
    public IActionResult ClearServiceCache(string name)
    {
        _logger.LogInformation("Starting background service removal for: {Service}", name);

        // Start tracking this removal operation
        _removalTracker.StartServiceRemoval(name);

        // Fire-and-forget background removal with SignalR notification
        _ = Task.Run(async () =>
        {
            try
            {
                // Send progress update
                await _hubContext.Clients.All.SendAsync("ServiceRemovalProgress", new
                {
                    serviceName = name,
                    status = "removing_cache",
                    message = $"Deleting cache files for {name}..."
                });
                _removalTracker.UpdateServiceRemoval(name, "removing_cache", $"Deleting cache files for {name}...");

                // Use CacheManagementService which actually deletes files via Rust binary
                var report = await _cacheService.RemoveServiceFromCache(name);

                // Send progress update
                await _hubContext.Clients.All.SendAsync("ServiceRemovalProgress", new
                {
                    serviceName = name,
                    status = "removing_database",
                    message = $"Updating database...",
                    filesDeleted = report.CacheFilesDeleted,
                    bytesFreed = report.TotalBytesFreed
                });
                _removalTracker.UpdateServiceRemoval(name, "removing_database", "Updating database...", report.CacheFilesDeleted, (long)report.TotalBytesFreed);

                // Also remove from detection cache so it doesn't show in UI
                await _gameCacheDetectionService.RemoveServiceFromCacheAsync(name);

                _logger.LogInformation("Service removal completed for: {Service} - Deleted {Files} files, freed {Bytes} bytes",
                    name, report.CacheFilesDeleted, report.TotalBytesFreed);

                // Complete tracking
                _removalTracker.CompleteServiceRemoval(name, true, report.CacheFilesDeleted, (long)report.TotalBytesFreed);

                // Send SignalR notification on success
                await _hubContext.Clients.All.SendAsync("ServiceRemovalComplete", new
                {
                    success = true,
                    serviceName = name,
                    filesDeleted = report.CacheFilesDeleted,
                    bytesFreed = report.TotalBytesFreed,
                    logEntriesRemoved = report.LogEntriesRemoved,
                    message = $"Successfully removed {name} service from cache"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during service removal for: {Service}", name);

                // Complete tracking with error
                _removalTracker.CompleteServiceRemoval(name, false, error: ex.Message);

                // Send SignalR notification on failure
                await _hubContext.Clients.All.SendAsync("ServiceRemovalComplete", new
                {
                    success = false,
                    serviceName = name,
                    message = $"Failed to remove {name} service: {ex.Message}"
                });
            }
        });

        return Accepted(new CacheOperationResponse
        {
            Message = $"Started removal of {name} service from cache",
            ServiceName = name,
            Status = "running"
        });
    }

    /// <summary>
    /// GET /api/cache/services/{name}/removal-status - Get service removal status
    /// Used for restoring progress on page refresh
    /// </summary>
    [HttpGet("services/{name}/removal-status")]
    public IActionResult GetServiceRemovalStatus(string name)
    {
        var operation = _removalTracker.GetServiceRemovalStatus(name);
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
            FilesDeleted = operation.FilesDeleted,
            BytesFreed = operation.BytesFreed,
            StartedAt = operation.StartedAt,
            Error = operation.Error
        });
    }

    /// <summary>
    /// GET /api/cache/services/removals/active - Get all active service removal operations
    /// </summary>
    [HttpGet("services/removals/active")]
    public IActionResult GetActiveServiceRemovals()
    {
        var operations = _removalTracker.GetActiveServiceRemovals();
        return Ok(new ActiveServiceRemovalsResponse
        {
            IsProcessing = operations.Any(),
            Operations = operations.Select(o => new ServiceRemovalInfo
            {
                ServiceName = o.Name,
                Status = o.Status,
                Message = o.Message,
                FilesDeleted = o.FilesDeleted,
                BytesFreed = o.BytesFreed,
                StartedAt = o.StartedAt
            })
        });
    }

    /// <summary>
    /// GET /api/cache/removals/active - Get all active removal operations (games, services, corruption)
    /// Used for universal recovery on page refresh
    /// </summary>
    [HttpGet("removals/active")]
    public IActionResult GetAllActiveRemovals()
    {
        var status = _removalTracker.GetAllActiveRemovals();
        return Ok(new AllActiveRemovalsResponse
        {
            IsProcessing = status.HasActiveOperations,
            GameRemovals = status.GameRemovals.Select(o => new GameRemovalInfo
            {
                GameAppId = int.Parse(o.Id),
                GameName = o.Name,
                Status = o.Status,
                Message = o.Message,
                FilesDeleted = o.FilesDeleted,
                BytesFreed = o.BytesFreed,
                StartedAt = o.StartedAt
            }),
            ServiceRemovals = status.ServiceRemovals.Select(o => new ServiceRemovalInfo
            {
                ServiceName = o.Name,
                Status = o.Status,
                Message = o.Message,
                FilesDeleted = o.FilesDeleted,
                BytesFreed = o.BytesFreed,
                StartedAt = o.StartedAt
            }),
            CorruptionRemovals = status.CorruptionRemovals.Select(o => new CorruptionRemovalInfo
            {
                Service = o.Name,
                OperationId = o.Id,
                Status = o.Status,
                Message = o.Message,
                StartedAt = o.StartedAt
            })
        });
    }
}
