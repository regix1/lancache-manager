using Microsoft.AspNetCore.Mvc;
using LancacheManager.Services;
using LancacheManager.Models;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StatsController : ControllerBase
{
    private readonly DatabaseService _dbService;
    private readonly CacheManagementService _cacheService;
    private readonly ILogger<StatsController> _logger;

    public StatsController(
        DatabaseService dbService,
        CacheManagementService cacheService,
        ILogger<StatsController> logger)
    {
        _dbService = dbService;
        _cacheService = cacheService;
        _logger = logger;
    }

    /// <summary>
    /// Get overall system statistics
    /// </summary>
    [HttpGet("overview")]
    public async Task<ActionResult<object>> GetOverview()
    {
        try
        {
            var cacheInfo = _cacheService.GetCacheInfo();
            var activeDownloads = await _dbService.GetActiveDownloads();
            dynamic recentStats = await _dbService.GetRecentStats(24); // Use dynamic to access properties
            
            var overview = new
            {
                Cache = new
                {
                    TotalSize = cacheInfo.TotalCacheSize,
                    UsedSize = cacheInfo.UsedCacheSize,
                    FreeSize = cacheInfo.FreeCacheSize,
                    UsagePercent = cacheInfo.UsagePercent,
                    TotalFiles = cacheInfo.TotalFiles
                },
                Downloads = new
                {
                    Active = activeDownloads.Count,
                    Last24Hours = (int)recentStats.DownloadCount,
                    TotalBytesServed = (long)recentStats.TotalBytes,
                    CacheHitRate = (double)recentStats.CacheHitRate
                },
                TopServices = recentStats.TopServices,
                TopClients = recentStats.TopClients
            };
            
            return Ok(overview);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting overview stats");
            return StatusCode(500, new { error = "Failed to retrieve overview statistics" });
        }
    }

    /// <summary>
    /// Get client statistics
    /// </summary>
    [HttpGet("clients")]
    public async Task<ActionResult<List<ClientStats>>> GetClientStats(
        [FromQuery] string? sortBy = "totalBytes",
        [FromQuery] int? top = null)
    {
        try
        {
            var stats = await _dbService.GetClientStats(sortBy);
            
            if (top.HasValue)
            {
                stats = stats.Take(top.Value).ToList();
            }
            
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting client stats");
            return StatusCode(500, new { error = "Failed to retrieve client statistics" });
        }
    }

    /// <summary>
    /// Get statistics for a specific client
    /// </summary>
    [HttpGet("clients/{clientIp}")]
    public async Task<ActionResult<object>> GetClientDetails(string clientIp)
    {
        try
        {
            var clientStats = await _dbService.GetClientById(clientIp);
            if (clientStats == null)
            {
                return NotFound(new { error = "Client not found" });
            }

            var recentDownloads = await _dbService.GetDownloadsByClient(clientIp, 10);
            var topGames = await _dbService.GetTopGamesForClient(clientIp, 10);
            
            var details = new
            {
                Stats = clientStats,
                RecentDownloads = recentDownloads,
                TopGames = topGames
            };
            
            return Ok(details);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting client details for {ClientIp}", clientIp);
            return StatusCode(500, new { error = "Failed to retrieve client details" });
        }
    }

    /// <summary>
    /// Get service statistics
    /// </summary>
    [HttpGet("services")]
    public async Task<ActionResult<List<ServiceStats>>> GetServiceStats(
        [FromQuery] string? sortBy = "totalBytes",
        [FromQuery] int? top = null)
    {
        try
        {
            var stats = await _dbService.GetServiceStats(sortBy);
            
            if (top.HasValue)
            {
                stats = stats.Take(top.Value).ToList();
            }
            
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting service stats");
            return StatusCode(500, new { error = "Failed to retrieve service statistics" });
        }
    }

    /// <summary>
    /// Get statistics for a specific service
    /// </summary>
    [HttpGet("services/{service}")]
    public async Task<ActionResult<object>> GetServiceDetails(string service)
    {
        try
        {
            var serviceStats = await _dbService.GetServiceById(service);
            if (serviceStats == null)
            {
                return NotFound(new { error = "Service not found" });
            }

            var cacheInfo = _cacheService.GetCacheInfo();
            var serviceSize = cacheInfo.ServiceSizes.GetValueOrDefault(service, 0);
            var topGames = await _dbService.GetTopGames(10, service, 30);
            var recentDownloads = await _dbService.GetDownloadsByService(service, 10);
            
            var details = new
            {
                Stats = serviceStats,
                CacheSize = serviceSize,
                TopGames = topGames,
                RecentDownloads = recentDownloads
            };
            
            return Ok(details);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting service details for {Service}", service);
            return StatusCode(500, new { error = "Failed to retrieve service details" });
        }
    }

    /// <summary>
    /// Get cache statistics
    /// </summary>
    [HttpGet("cache")]
    public ActionResult<CacheInfo> GetCacheStats()
    {
        try
        {
            var info = _cacheService.GetCacheInfo();
            return Ok(info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache stats");
            return StatusCode(500, new { error = "Failed to retrieve cache statistics" });
        }
    }

    /// <summary>
    /// Get cache hit rate statistics over time
    /// </summary>
    [HttpGet("cache/hit-rate")]
    public async Task<ActionResult<object>> GetCacheHitRate(
        [FromQuery] int days = 7,
        [FromQuery] string interval = "hour")
    {
        try
        {
            var hitRate = await _dbService.GetCacheHitRateTrends(days, interval);
            return Ok(hitRate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache hit rate");
            return StatusCode(500, new { error = "Failed to retrieve cache hit rate" });
        }
    }

    /// <summary>
    /// Get bandwidth usage statistics
    /// </summary>
    [HttpGet("bandwidth")]
    public async Task<ActionResult<object>> GetBandwidthStats(
        [FromQuery] int hours = 24,
        [FromQuery] string interval = "hour")
    {
        try
        {
            var bandwidth = await _dbService.GetBandwidthStats(hours, interval);
            return Ok(bandwidth);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting bandwidth stats");
            return StatusCode(500, new { error = "Failed to retrieve bandwidth statistics" });
        }
    }

    /// <summary>
    /// Get data savings statistics
    /// </summary>
    [HttpGet("savings")]
    public async Task<ActionResult<object>> GetSavingsStats([FromQuery] int days = 30)
    {
        try
        {
            var savings = await _dbService.GetSavingsStats(days);
            return Ok(savings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting savings stats");
            return StatusCode(500, new { error = "Failed to retrieve savings statistics" });
        }
    }
}