using Microsoft.AspNetCore.Mvc;
using LancacheManager.Services;
using LancacheManager.Models;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ManagementController : ControllerBase
{
    private readonly CacheManagementService _cacheService;
    private readonly DatabaseService _dbService;
    private readonly ILogger<ManagementController> _logger;

    public ManagementController(
        CacheManagementService cacheService,
        DatabaseService dbService,
        ILogger<ManagementController> logger)
    {
        _cacheService = cacheService;
        _dbService = dbService;
        _logger = logger;
    }

    /// <summary>
    /// Clear cache for a specific service or all services
    /// </summary>
    [HttpPost("cache/clear")]
    public async Task<ActionResult> ClearCache([FromBody] ManagementAction action)
    {
        try
        {
            var success = await _cacheService.ClearCache(action.Service);
            if (success)
            {
                _logger.LogInformation("Cache cleared for service: {Service}", action.Service ?? "all");
                return Ok(new { message = $"Cache cleared successfully for {action.Service ?? "all services"}" });
            }
            
            return StatusCode(500, new { error = "Failed to clear cache" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing cache");
            return StatusCode(500, new { error = "Failed to clear cache" });
        }
    }

    /// <summary>
    /// Reset database (clear all statistics)
    /// </summary>
    [HttpPost("database/reset")]
    public async Task<ActionResult> ResetDatabase()
    {
        try
        {
            await _dbService.ResetDatabase();
            _logger.LogInformation("Database reset successfully");
            return Ok(new { message = "Database reset successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting database");
            return StatusCode(500, new { error = "Failed to reset database" });
        }
    }

    /// <summary>
    /// Get system status
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult> GetStatus()
    {
        try
        {
            var cacheInfo = _cacheService.GetCacheInfo();
            var activeDownloads = await _dbService.GetActiveDownloads();
            var clientStats = await _dbService.GetClientStats();
            var serviceStats = await _dbService.GetServiceStats();
            
            var status = new
            {
                Cache = new
                {
                    TotalSize = cacheInfo.TotalCacheSize,
                    UsedSize = cacheInfo.UsedCacheSize,
                    FreeSize = cacheInfo.FreeCacheSize,
                    UsagePercent = cacheInfo.UsagePercent
                },
                Statistics = new
                {
                    ActiveDownloads = activeDownloads.Count,
                    TotalClients = clientStats.Count,
                    TotalServices = serviceStats.Count
                },
                Timestamp = DateTime.UtcNow
            };
            
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting system status");
            return StatusCode(500, new { error = "Failed to retrieve system status" });
        }
    }
}