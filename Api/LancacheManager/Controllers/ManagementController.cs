using Microsoft.AspNetCore.Mvc;
using LancacheManager.Services;
using System.IO;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ManagementController : ControllerBase
{
    private readonly CacheManagementService _cacheService;
    private readonly DatabaseService _dbService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ManagementController> _logger;

    public ManagementController(
        CacheManagementService cacheService,
        DatabaseService dbService,
        IConfiguration configuration,
        ILogger<ManagementController> logger)
    {
        _cacheService = cacheService;
        _dbService = dbService;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet("cache")]
    public IActionResult GetCacheInfo()
    {
        var info = _cacheService.GetCacheInfo();
        return Ok(info);
    }

    [HttpDelete("cache")]
    public async Task<IActionResult> ClearCache([FromQuery] string? service = null)
    {
        await _cacheService.ClearCache(service);
        return Ok(new { message = $"Cache cleared for {service ?? "all services"}" });
    }

    [HttpDelete("database")]
    public async Task<IActionResult> ResetDatabase()
    {
        await _dbService.ResetDatabase();
        return Ok(new { message = "Database reset successfully" });
    }

    [HttpPost("reset-logs")]
    public async Task<IActionResult> ResetLogPosition()
    {
        try
        {
            // Clear position file to start from end
            var positionFile = "/data/logposition.txt";
            if (System.IO.File.Exists(positionFile))
            {
                System.IO.File.Delete(positionFile);
            }
            
            // Also reset database
            await _dbService.ResetDatabase();
            
            _logger.LogInformation("Log position and database reset - will start from end of log");
            
            return Ok(new { 
                message = "Log position reset successfully. The application will restart monitoring from the current end of the log file.",
                requiresRestart = true 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting log position");
            return StatusCode(500, new { error = "Failed to reset log position" });
        }
    }

    [HttpPost("process-all-logs")]
    public async Task<IActionResult> ProcessAllLogs()
    {
        try
        {
            // Set position to 0 to process from beginning
            var positionFile = "/data/logposition.txt";
            await System.IO.File.WriteAllTextAsync(positionFile, "0");
            
            // Get log file size for user info
            var logPath = _configuration["LanCache:LogPath"] ?? "/logs/access.log";
            var fileInfo = new FileInfo(logPath);
            var sizeMB = fileInfo.Length / (1024.0 * 1024.0);
            
            _logger.LogInformation($"Set to process entire log file ({sizeMB:F1} MB) from beginning");
            
            return Ok(new { 
                message = $"Will process entire log file ({sizeMB:F1} MB) from the beginning. This may take several minutes.",
                logSizeMB = sizeMB,
                estimatedTimeMinutes = Math.Ceiling(sizeMB / 100), // Rough estimate: 100MB per minute
                requiresRestart = true 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting up full log processing");
            return StatusCode(500, new { error = "Failed to setup full log processing" });
        }
    }
}