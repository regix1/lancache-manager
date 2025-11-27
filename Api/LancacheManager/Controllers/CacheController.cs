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
        try
        {
            var cachePath = _pathResolver.GetCacheDirectory();
            var cacheWritable = _pathResolver.IsCacheDirectoryWritable();

            return Ok(new
            {
                path = cachePath,
                writable = cacheWritable,
                readOnly = !cacheWritable
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking cache directory permissions");
            return StatusCode(500, new { error = "Failed to check directory permissions", details = ex.Message });
        }
    }

    /// <summary>
    /// DELETE /api/cache - Clear all cache
    /// RESTful: DELETE is proper method for clearing/removing resources
    /// </summary>
    [HttpDelete]
    [RequireAuth]
    public async Task<IActionResult> ClearAllCache()
    {
        try
        {
            var operationId = await _cacheClearingService.StartCacheClearAsync();
            _logger.LogInformation("Started cache clear operation: {OperationId}", operationId);

            return Accepted(new
            {
                message = "Cache clearing started in background",
                operationId,
                status = "running"
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting cache clear");
            return StatusCode(500, new { error = "Failed to start cache clear", details = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/cache/operations - List all active cache operations
    /// </summary>
    [HttpGet("operations")]
    public IActionResult GetActiveOperations()
    {
        try
        {
            var operations = _cacheClearingService.GetActiveOperations();
            return Ok(new { operations });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting active cache operations");
            return StatusCode(500, new { error = "Failed to get active operations" });
        }
    }

    /// <summary>
    /// GET /api/cache/operations/{id}/status - Get status of specific cache clear operation
    /// </summary>
    [HttpGet("operations/{id}/status")]
    public IActionResult GetCacheClearStatus(string id)
    {
        try
        {
            var status = _cacheClearingService.GetCacheClearStatus(id);

            if (status == null)
            {
                return NotFound(new { error = "Cache clear operation not found", operationId = id });
            }

            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache clear status for operation {OperationId}", id);
            return StatusCode(500, new { error = "Failed to get cache clear status" });
        }
    }

    /// <summary>
    /// DELETE /api/cache/operations/{id} - Cancel a running cache clear operation
    /// RESTful: DELETE is proper method for cancelling/removing operations
    /// </summary>
    [HttpDelete("operations/{id}")]
    [RequireAuth]
    public IActionResult CancelCacheClear(string id)
    {
        try
        {
            var result = _cacheClearingService.CancelCacheClear(id);

            if (!result)
            {
                return NotFound(new { error = "Cache clear operation not found or already completed", operationId = id });
            }

            return Ok(new { message = "Cache clear operation cancelled successfully", operationId = id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling cache clear operation {OperationId}", id);
            return StatusCode(500, new { error = "Failed to cancel cache clear operation" });
        }
    }

    /// <summary>
    /// POST /api/cache/operations/{id}/kill - Force kill a cache clear operation's process
    /// Used as fallback when graceful cancellation fails
    /// </summary>
    [HttpPost("operations/{id}/kill")]
    [RequireAuth]
    public async Task<IActionResult> ForceKillCacheClear(string id)
    {
        try
        {
            var result = await _cacheClearingService.ForceKillOperation(id);

            if (!result)
            {
                return NotFound(new { error = "Cache clear operation not found or no process to kill", operationId = id });
            }

            return Ok(new { message = "Cache clear operation force killed successfully", operationId = id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error force killing cache clear operation {OperationId}", id);
            return StatusCode(500, new { error = "Failed to force kill cache clear operation" });
        }
    }

    /// <summary>
    /// GET /api/cache/corruption/summary - Get corruption summary for all services
    /// </summary>
    [HttpGet("corruption/summary")]
    public async Task<IActionResult> GetCorruptionSummary([FromQuery] bool forceRefresh = false)
    {
        try
        {
            var summary = await _cacheService.GetCorruptionSummary(forceRefresh);
            return Ok(summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting corruption summary");
            return StatusCode(500, new
            {
                error = "Failed to get corruption summary",
                details = ex.Message
            });
        }
    }

    /// <summary>
    /// GET /api/cache/services/{name}/corruption - Get detailed corruption info for specific service
    /// Returns array of corrupted chunks with URLs, miss counts, and cache file paths
    /// </summary>
    [HttpGet("services/{service}/corruption")]
    public async Task<IActionResult> GetCorruptionDetails(string service, [FromQuery] bool forceRefresh = false)
    {
        try
        {
            // Get detailed corruption information (not just counts)
            var details = await _cacheService.GetCorruptionDetails(service, forceRefresh);
            return Ok(details);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting corruption details for service: {Service}", service);
            return StatusCode(500, new
            {
                error = $"Failed to get corruption details for service: {service}",
                details = ex.Message
            });
        }
    }

    /// <summary>
    /// DELETE /api/cache/services/{name}/corruption - Remove corrupted chunks for specific service
    /// RESTful: DELETE is proper method for removing resources
    /// </summary>
    [HttpDelete("services/{service}/corruption")]
    [RequireAuth]
    public IActionResult RemoveCorruptedChunks(string service)
    {
        try
        {
            var cachePath = _pathResolver.GetCacheDirectory();
            var logsPath = _pathResolver.GetLogsDirectory();
            var dbPath = _pathResolver.GetDatabasePath();

            var operationId = Guid.NewGuid().ToString();

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

                    try
                    {
                        var result = await _rustProcessHelper.RunCorruptionManagerAsync(
                            "remove",
                            logsPath,
                            cachePath,
                            service: service,
                            progressFile: Path.Combine(_pathResolver.GetDataDirectory(), $"corruption_removal_{operationId}.json"),
                            databasePath: dbPath
                        );

                        if (result.Success)
                        {
                            _logger.LogInformation("Corruption removal completed for service: {Service}", service);
                            await _hubContext.Clients.All.SendAsync("CorruptionRemovalComplete", new
                            {
                                service,
                                operationId,
                                success = true
                            });
                        }
                        else
                        {
                            _logger.LogError("Corruption removal failed for service {Service}: {Error}", service, result.Error);
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
                    await _hubContext.Clients.All.SendAsync("CorruptionRemovalComplete", new
                    {
                        service,
                        operationId,
                        success = false,
                        error = ex.Message
                    });
                }
            });

            return Accepted(new
            {
                message = $"Started corruption removal for service: {service}",
                service,
                operationId,
                status = "running"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting corruption removal for service: {Service}", service);
            return StatusCode(500, new
            {
                error = $"Failed to start corruption removal for service: {service}",
                details = ex.Message
            });
        }
    }

    /// <summary>
    /// DELETE /api/cache/services/{name} - Remove specific service from cache
    /// RESTful: DELETE is proper method for removing resources
    /// </summary>
    [HttpDelete("services/{name}")]
    [RequireAuth]
    public IActionResult ClearServiceCache(string name)
    {
        try
        {
            _logger.LogInformation("Starting background service removal for: {Service}", name);

            // Fire-and-forget background removal with SignalR notification
            _ = Task.Run(async () =>
            {
                try
                {
                    // Use CacheManagementService which actually deletes files via Rust binary
                    var report = await _cacheService.RemoveServiceFromCache(name);

                    // Also remove from detection cache so it doesn't show in UI
                    await _gameCacheDetectionService.RemoveServiceFromCacheAsync(name);

                    _logger.LogInformation("Service removal completed for: {Service} - Deleted {Files} files, freed {Bytes} bytes",
                        name, report.CacheFilesDeleted, report.TotalBytesFreed);

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

                    // Send SignalR notification on failure
                    await _hubContext.Clients.All.SendAsync("ServiceRemovalComplete", new
                    {
                        success = false,
                        serviceName = name,
                        message = $"Failed to remove {name} service: {ex.Message}"
                    });
                }
            });

            return Accepted(new
            {
                message = $"Started removal of {name} service from cache",
                serviceName = name,
                status = "running"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting service removal for: {Service}", name);
            return StatusCode(500, new
            {
                error = $"Failed to start service removal for: {name}",
                details = ex.Message
            });
        }
    }
}
