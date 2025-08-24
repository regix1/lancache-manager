using Microsoft.AspNetCore.Mvc;
using LancacheManager.Services;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ManagementController : ControllerBase
{
    private readonly CacheManagementService _cacheService;
    private readonly DatabaseService _dbService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ManagementController> _logger;
    private static CancellationTokenSource? _processingCancellation;
    private static DateTime? _processingStartTime;
    
    // Fixed Linux paths
    private const string DATA_DIRECTORY = "/data";
    private const string LOG_PATH = "/logs/access.log";
    private const string POSITION_FILE = "/data/logposition.txt";
    private const string PROCESSING_MARKER = "/data/bulk_processing.marker";
    private const string DATABASE_PATH = "/data/lancache.db";

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
        
        // Ensure data directory exists
        if (!Directory.Exists(DATA_DIRECTORY))
        {
            try
            {
                Directory.CreateDirectory(DATA_DIRECTORY);
                _logger.LogInformation($"Created data directory: {DATA_DIRECTORY}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create data directory: {DATA_DIRECTORY}");
            }
        }
    }

    [HttpGet("cache")]
    public IActionResult GetCacheInfo()
    {
        var info = _cacheService.GetCacheInfo();
        return Ok(info);
    }

    // New endpoint for clearing all cache
    [HttpPost("cache/clear-all")]
    public async Task<IActionResult> ClearAllCache()
    {
        try
        {
            await _cacheService.ClearAllCache();
            return Ok(new { message = "All cache cleared successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing all cache");
            return StatusCode(500, new { error = "Failed to clear cache", details = ex.Message });
        }
    }

    // Legacy endpoint for compatibility
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
            if (System.IO.File.Exists(POSITION_FILE))
            {
                System.IO.File.Delete(POSITION_FILE);
            }
            
            // Also reset database
            await _dbService.ResetDatabase();
            
            _logger.LogInformation("Log position and database reset - will start from end of log");
            
            return Ok(new { 
                message = "Log position reset successfully. Will start monitoring from the current end of the log file.",
                requiresRestart = false 
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
            // Cancel any existing processing
            _processingCancellation?.Cancel();
            _processingCancellation = new CancellationTokenSource();
            
            // Set position to 0 to process from beginning
            await System.IO.File.WriteAllTextAsync(POSITION_FILE, "0");
            
            // Create marker file with metadata
            var markerData = new
            {
                startTime = DateTime.UtcNow,
                startPosition = 0L,
                triggerType = "manual"
            };
            await System.IO.File.WriteAllTextAsync(PROCESSING_MARKER, 
                System.Text.Json.JsonSerializer.Serialize(markerData));
            
            _processingStartTime = DateTime.UtcNow;
            
            // Check if log file exists
            if (!System.IO.File.Exists(LOG_PATH))
            {
                _logger.LogWarning($"Log file not found at: {LOG_PATH}");
                return Ok(new { 
                    message = $"Log file not found at: {LOG_PATH}. Waiting for logs...",
                    logSizeMB = 0,
                    estimatedTimeMinutes = 0,
                    requiresRestart = false,
                    status = "no_log_file"
                });
            }
            
            // Get log file size for user info
            var fileInfo = new FileInfo(LOG_PATH);
            var sizeMB = fileInfo.Length / (1024.0 * 1024.0);
            
            _logger.LogInformation($"Set to process entire log file ({sizeMB:F1} MB) from beginning");
            
            return Ok(new { 
                message = $"Processing entire log file ({sizeMB:F1} MB) from the beginning...",
                logSizeMB = sizeMB,
                estimatedTimeMinutes = Math.Ceiling(sizeMB / 100), // Rough estimate: 100MB per minute
                requiresRestart = false,
                status = "processing"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting up full log processing");
            return StatusCode(500, new { error = $"Failed to setup full log processing: {ex.Message}" });
        }
    }

    [HttpPost("cancel-processing")]
    public async Task<IActionResult> CancelProcessing()
    {
        try
        {
            // Signal cancellation
            _processingCancellation?.Cancel();
            
            // Remove processing marker
            if (System.IO.File.Exists(PROCESSING_MARKER))
            {
                System.IO.File.Delete(PROCESSING_MARKER);
            }
            
            // Set position to end of file to stop processing
            if (System.IO.File.Exists(LOG_PATH))
            {
                var fileInfo = new FileInfo(LOG_PATH);
                await System.IO.File.WriteAllTextAsync(POSITION_FILE, fileInfo.Length.ToString());
                _logger.LogInformation($"Processing cancelled, position set to end of file");
            }
            else
            {
                if (System.IO.File.Exists(POSITION_FILE))
                {
                    System.IO.File.Delete(POSITION_FILE);
                }
                _logger.LogInformation("Processing cancelled, position cleared");
            }
            
            return Ok(new { 
                message = "Processing cancelled. Resuming normal monitoring.",
                requiresRestart = false 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling processing");
            return StatusCode(500, new { error = "Failed to cancel processing" });
        }
    }

    [HttpGet("processing-status")]
    public async Task<IActionResult> GetProcessingStatus()
    {
        try
        {
            // Check if processing marker exists
            if (!System.IO.File.Exists(PROCESSING_MARKER))
            {
                return Ok(new { 
                    isProcessing = false,
                    message = "Not processing" 
                });
            }
            
            // Check if log file exists
            if (!System.IO.File.Exists(LOG_PATH))
            {
                return Ok(new { 
                    isProcessing = false,
                    message = "Log file not found",
                    error = $"Log file not found at: {LOG_PATH}"
                });
            }
            
            // Read marker data
            var markerContent = await System.IO.File.ReadAllTextAsync(PROCESSING_MARKER);
            
            long currentPosition = 0;
            if (System.IO.File.Exists(POSITION_FILE))
            {
                var posContent = await System.IO.File.ReadAllTextAsync(POSITION_FILE);
                long.TryParse(posContent, out currentPosition);
            }
            
            var fileInfo = new FileInfo(LOG_PATH);
            
            // Calculate actual progress
            var bytesProcessed = currentPosition;
            var percentComplete = fileInfo.Length > 0 ? (bytesProcessed * 100.0) / fileInfo.Length : 0;
            
            // Check if we're at the end
            if (currentPosition >= fileInfo.Length - 1000) // Within 1KB of end
            {
                // We're at the end, processing is complete
                if (System.IO.File.Exists(PROCESSING_MARKER))
                {
                    System.IO.File.Delete(PROCESSING_MARKER);
                }
                
                return Ok(new { 
                    isProcessing = false,
                    message = "Processing complete",
                    percentComplete = 100,
                    mbProcessed = fileInfo.Length / (1024.0 * 1024.0),
                    mbTotal = fileInfo.Length / (1024.0 * 1024.0)
                });
            }
            
            // Calculate processing rate
            double processingRate = 0;
            string estimatedTime = "calculating...";
            
            if (_processingStartTime.HasValue && currentPosition > 0)
            {
                var elapsed = DateTime.UtcNow - _processingStartTime.Value;
                if (elapsed.TotalSeconds > 0)
                {
                    processingRate = bytesProcessed / elapsed.TotalSeconds; // bytes per second
                    if (processingRate > 0)
                    {
                        var remainingBytes = fileInfo.Length - currentPosition;
                        var remainingSeconds = remainingBytes / processingRate;
                        var remainingMinutes = Math.Ceiling(remainingSeconds / 60);
                        estimatedTime = remainingMinutes > 60 
                            ? $"{remainingMinutes / 60:F1} hours" 
                            : $"{remainingMinutes} minutes";
                    }
                }
            }
            
            return Ok(new { 
                isProcessing = true,
                currentPosition,
                totalSize = fileInfo.Length,
                percentComplete,
                mbProcessed = currentPosition / (1024.0 * 1024.0),
                mbTotal = fileInfo.Length / (1024.0 * 1024.0),
                processingRate = processingRate / (1024.0 * 1024.0), // MB/s
                estimatedTime,
                message = $"Processing log file... {percentComplete:F1}% complete",
                status = "processing"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting processing status");
            return Ok(new { isProcessing = false, error = ex.Message });
        }
    }

    // New endpoint to remove service entries from log file
    [HttpPost("logs/remove-service")]
    public async Task<IActionResult> RemoveServiceFromLogs([FromBody] RemoveServiceRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(request.Service))
            {
                return BadRequest(new { error = "Service name is required" });
            }

            await _cacheService.RemoveServiceFromLogs(request.Service);
            
            return Ok(new { 
                message = $"Successfully removed {request.Service} entries from log file",
                backupFile = $"{LOG_PATH}.bak"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error removing {request.Service} from logs");
            return StatusCode(500, new { error = $"Failed to remove {request.Service} from logs", details = ex.Message });
        }
    }

    // New endpoint to get service log counts
    [HttpGet("logs/service-counts")]
    public async Task<IActionResult> GetServiceLogCounts()
    {
        try
        {
            var counts = await _cacheService.GetServiceLogCounts();
            return Ok(counts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting service log counts");
            return StatusCode(500, new { error = "Failed to get service log counts" });
        }
    }

    // Get configuration info
    [HttpGet("config")]
    public async Task<IActionResult> GetConfig()
    {
        try
        {
            var services = await _cacheService.GetServicesFromLogs();
            var cachePath = _cacheService.GetCachePath();
            
            return Ok(new { 
                cachePath,
                logPath = LOG_PATH,
                services
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting configuration");
            return Ok(new { 
                cachePath = "/mnt/cache/cache",
                logPath = "/logs/access.log",
                services = new[] { "steam", "epic", "origin", "blizzard", "wsus", "riot" }
            });
        }
    }

    // Debug endpoint to check permissions
    [HttpGet("debug/permissions")]
    public IActionResult CheckPermissions()
    {
        var results = new Dictionary<string, object>();
        
        // Check /logs directory
        try
        {
            var logsDir = Path.GetDirectoryName(LOG_PATH) ?? "/logs";
            results["logsDirectory"] = logsDir;
            results["logsExists"] = Directory.Exists(logsDir);
            
            if (Directory.Exists(logsDir))
            {
                var testFile = Path.Combine(logsDir, ".write_test");
                try
                {
                    System.IO.File.WriteAllText(testFile, "test");
                    System.IO.File.Delete(testFile);
                    results["logsWritable"] = true;
                }
                catch (Exception ex)
                {
                    results["logsWritable"] = false;
                    results["logsError"] = ex.Message;
                }
            }
        }
        catch (Exception ex)
        {
            results["logsCheckError"] = ex.Message;
        }
        
        // Check /cache directory
        try
        {
            var cachePath = _cacheService.GetCachePath();
            results["cacheDirectory"] = cachePath;
            results["cacheExists"] = Directory.Exists(cachePath);
            
            if (Directory.Exists(cachePath))
            {
                var testFile = Path.Combine(cachePath, ".write_test");
                try
                {
                    System.IO.File.WriteAllText(testFile, "test");
                    System.IO.File.Delete(testFile);
                    results["cacheWritable"] = true;
                }
                catch (Exception ex)
                {
                    results["cacheWritable"] = false;
                    results["cacheError"] = ex.Message;
                }
            }
        }
        catch (Exception ex)
        {
            results["cacheCheckError"] = ex.Message;
        }
        
        // Check /data directory
        try
        {
            results["dataDirectory"] = DATA_DIRECTORY;
            results["dataExists"] = Directory.Exists(DATA_DIRECTORY);
            
            if (Directory.Exists(DATA_DIRECTORY))
            {
                var testFile = Path.Combine(DATA_DIRECTORY, ".write_test");
                try
                {
                    System.IO.File.WriteAllText(testFile, "test");
                    System.IO.File.Delete(testFile);
                    results["dataWritable"] = true;
                }
                catch (Exception ex)
                {
                    results["dataWritable"] = false;
                    results["dataError"] = ex.Message;
                }
            }
        }
        catch (Exception ex)
        {
            results["dataCheckError"] = ex.Message;
        }
        
        // Check log file
        try
        {
            results["logFile"] = LOG_PATH;
            results["logFileExists"] = System.IO.File.Exists(LOG_PATH);
            
            if (System.IO.File.Exists(LOG_PATH))
            {
                var fileInfo = new FileInfo(LOG_PATH);
                results["logFileSize"] = fileInfo.Length;
                results["logFileReadOnly"] = fileInfo.IsReadOnly;
                results["logFileLastModified"] = fileInfo.LastWriteTimeUtc;
            }
        }
        catch (Exception ex)
        {
            results["logFileError"] = ex.Message;
        }
        
        // Environment info
        results["user"] = Environment.UserName;
        results["userId"] = Environment.GetEnvironmentVariable("USER") ?? "unknown";
        results["workingDirectory"] = Environment.CurrentDirectory;
        
        return Ok(results);
    }
}

// Request model for removing service
public class RemoveServiceRequest
{
    public string Service { get; set; } = string.Empty;
}