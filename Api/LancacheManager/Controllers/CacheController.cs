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
    /// </summary>
    [HttpGet("services/{service}/corruption")]
    public async Task<IActionResult> GetCorruptionDetails(string service, [FromQuery] bool forceRefresh = false)
    {
        try
        {
            var summary = await _cacheService.GetCorruptionSummary(forceRefresh);

            // Summary is a Dictionary<string, long> mapping service names to corruption counts
            if (summary.TryGetValue(service, out var corruptionCount))
            {
                return Ok(new
                {
                    service,
                    corruptedChunks = corruptionCount
                });
            }

            return NotFound(new { error = $"No corruption data found for service: {service}" });
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

            var operationId = Guid.NewGuid().ToString();

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _rustProcessHelper.RunCorruptionManagerAsync(
                        "remove",
                        logsPath,
                        cachePath,
                        service: service,
                        progressFile: Path.Combine(_pathResolver.GetDataDirectory(), $"corruption_removal_{operationId}.json")
                    );

                    if (result.Success)
                    {
                        _logger.LogInformation("Corruption removal completed for service: {Service}", service);
                        await _hubContext.Clients.All.SendAsync("CorruptionRemovalCompleted", new
                        {
                            service,
                            operationId,
                            success = true
                        });
                    }
                    else
                    {
                        _logger.LogError("Corruption removal failed for service {Service}: {Error}", service, result.Error);
                        await _hubContext.Clients.All.SendAsync("CorruptionRemovalCompleted", new
                        {
                            service,
                            operationId,
                            success = false,
                            error = result.Error
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during corruption removal for service: {Service}", service);
                    await _hubContext.Clients.All.SendAsync("CorruptionRemovalCompleted", new
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
            var cachePath = _pathResolver.GetCacheDirectory();
            var serviceDirectory = Path.Combine(cachePath, name.ToLowerInvariant());

            if (!Directory.Exists(serviceDirectory))
            {
                return NotFound(new { error = $"Service cache not found: {name}" });
            }

            Directory.Delete(serviceDirectory, true);
            _logger.LogInformation("Deleted cache directory for service: {Service}", name);

            return Ok(new { message = $"Successfully deleted cache for service: {name}", service = name });
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Permission denied when deleting cache for service: {Service}", name);
            return StatusCode(403, new { error = "Permission denied", details = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting cache for service: {Service}", name);
            return StatusCode(500, new { error = $"Failed to delete cache for service: {name}", details = ex.Message });
        }
    }
}
