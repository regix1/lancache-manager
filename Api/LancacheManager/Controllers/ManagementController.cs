using Microsoft.AspNetCore.Mvc;
using LancacheManager.Services;
using System.IO;
using System.Runtime.InteropServices;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ManagementController : ControllerBase
{
    private readonly CacheManagementService _cacheService;
    private readonly DatabaseService _dbService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ManagementController> _logger;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private static CancellationTokenSource? _processingCancellation;
    private static DateTime? _processingStartTime;
    
    // Cross-platform data directory - not readonly so they can be set in InitializePaths
    private string _dataDirectory = string.Empty;
    private string _logPath = string.Empty;

    public ManagementController(
        CacheManagementService cacheService,
        DatabaseService dbService,
        IConfiguration configuration,
        ILogger<ManagementController> logger,
        IHostApplicationLifetime applicationLifetime)
    {
        _cacheService = cacheService;
        _dbService = dbService;
        _configuration = configuration;
        _logger = logger;
        _applicationLifetime = applicationLifetime;
        
        // Initialize cross-platform paths
        InitializePaths();
    }

    private void InitializePaths()
    {
        // Determine data directory based on platform
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows: Use AppData or local directory
            _dataDirectory = _configuration["DataDirectory"] ?? 
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LancacheManager");
            
            // Windows log path - could be in current directory for development
            _logPath = _configuration["LanCache:LogPath"] ?? 
                Path.Combine(Directory.GetCurrentDirectory(), "logs", "access.log");
        }
        else
        {
            // Linux/Docker: Use standard paths
            _dataDirectory = _configuration["DataDirectory"] ?? "/data";
            _logPath = _configuration["LanCache:LogPath"] ?? "/logs/access.log";
        }
        
        // Ensure data directory exists
        if (!Directory.Exists(_dataDirectory))
        {
            try
            {
                Directory.CreateDirectory(_dataDirectory);
                _logger.LogInformation($"Created data directory: {_dataDirectory}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create data directory: {_dataDirectory}");
            }
        }
    }

    private string GetPositionFilePath() => Path.Combine(_dataDirectory, "logposition.txt");
    private string GetProcessingMarkerPath() => Path.Combine(_dataDirectory, "bulk_processing.marker");
    private string GetDatabasePath() => Path.Combine(_dataDirectory, "lancache.db");

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
            var positionFile = GetPositionFilePath();
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
            // Cancel any existing processing
            _processingCancellation?.Cancel();
            _processingCancellation = new CancellationTokenSource();
            
            // Set position to 0 to process from beginning
            var positionFile = GetPositionFilePath();
            await System.IO.File.WriteAllTextAsync(positionFile, "0");
            
            // Create marker file with metadata
            var processingMarker = GetProcessingMarkerPath();
            var markerData = new
            {
                startTime = DateTime.UtcNow,
                startPosition = 0L,
                triggerType = "manual"
            };
            await System.IO.File.WriteAllTextAsync(processingMarker, 
                System.Text.Json.JsonSerializer.Serialize(markerData));
            
            _processingStartTime = DateTime.UtcNow;
            
            // Check if log file exists
            if (!System.IO.File.Exists(_logPath))
            {
                _logger.LogWarning($"Log file not found at: {_logPath}");
                return Ok(new { 
                    message = $"Log file not found at: {_logPath}. Waiting for logs...",
                    logSizeMB = 0,
                    estimatedTimeMinutes = 0,
                    requiresRestart = false,
                    status = "no_log_file"
                });
            }
            
            // Get log file size for user info
            var fileInfo = new FileInfo(_logPath);
            var sizeMB = fileInfo.Length / (1024.0 * 1024.0);
            
            _logger.LogInformation($"Set to process entire log file ({sizeMB:F1} MB) from beginning");
            
            // The service needs to restart to pick up the new position
            // Schedule a restart in 2 seconds to allow this response to complete
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                _logger.LogInformation("Restarting application to begin log processing...");
                _applicationLifetime.StopApplication();
            });
            
            return Ok(new { 
                message = $"Will process entire log file ({sizeMB:F1} MB) from the beginning. Service is restarting to begin processing...",
                logSizeMB = sizeMB,
                estimatedTimeMinutes = Math.Ceiling(sizeMB / 100), // Rough estimate: 100MB per minute
                requiresRestart = true,
                status = "restarting"
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
            var processingMarker = GetProcessingMarkerPath();
            if (System.IO.File.Exists(processingMarker))
            {
                System.IO.File.Delete(processingMarker);
            }
            
            // Set position to end of file to stop processing
            var positionFile = GetPositionFilePath();
            
            if (System.IO.File.Exists(_logPath))
            {
                var fileInfo = new FileInfo(_logPath);
                // Save current position (end of file) to stop processing
                await System.IO.File.WriteAllTextAsync(positionFile, fileInfo.Length.ToString());
                _logger.LogInformation($"Processing cancelled, position set to end of file");
            }
            else
            {
                // No log file, just clear the position
                if (System.IO.File.Exists(positionFile))
                {
                    System.IO.File.Delete(positionFile);
                }
                _logger.LogInformation("Processing cancelled, position cleared");
            }
            
            // Restart to apply the change
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                _applicationLifetime.StopApplication();
            });
            
            return Ok(new { 
                message = "Processing cancelled. Service will restart and resume normal monitoring.",
                requiresRestart = true 
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
            var processingMarker = GetProcessingMarkerPath();
            var positionFile = GetPositionFilePath();
            
            // Check if processing marker exists
            if (!System.IO.File.Exists(processingMarker))
            {
                return Ok(new { 
                    isProcessing = false,
                    message = "Not processing" 
                });
            }
            
            // Check if log file exists
            if (!System.IO.File.Exists(_logPath))
            {
                return Ok(new { 
                    isProcessing = false,
                    message = "Log file not found",
                    error = $"Log file not found at: {_logPath}"
                });
            }
            
            // Read marker data
            var markerContent = await System.IO.File.ReadAllTextAsync(processingMarker);
            
            long currentPosition = 0;
            if (System.IO.File.Exists(positionFile))
            {
                var posContent = await System.IO.File.ReadAllTextAsync(positionFile);
                long.TryParse(posContent, out currentPosition);
            }
            
            var fileInfo = new FileInfo(_logPath);
            
            // Calculate actual progress - how much we've processed since starting
            var bytesProcessed = currentPosition; // Since we started from 0
            var percentComplete = fileInfo.Length > 0 ? (bytesProcessed * 100.0) / fileInfo.Length : 0;
            
            // Check if we're actually making progress
            if (currentPosition == 0)
            {
                // Still at position 0, might be restarting
                return Ok(new { 
                    isProcessing = true,
                    currentPosition = 0,
                    totalSize = fileInfo.Length,
                    percentComplete = 0,
                    mbProcessed = 0,
                    mbTotal = fileInfo.Length / (1024.0 * 1024.0),
                    message = "Service is restarting to begin processing...",
                    status = "restarting"
                });
            }
            else if (currentPosition >= fileInfo.Length - 1000) // Within 1KB of end
            {
                // We're at the end, processing is complete
                if (System.IO.File.Exists(processingMarker))
                {
                    System.IO.File.Delete(processingMarker);
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
            
            // Get current download count for feedback
            var downloadCount = 0;
            try
            {
                var dbPath = GetDatabasePath();
                if (System.IO.File.Exists(dbPath))
                {
                    // You could query the database here for actual count
                    downloadCount = -1; // Indicator that DB exists but count not available
                }
            }
            catch { }
            
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
                status = "processing",
                downloadCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting processing status");
            return Ok(new { isProcessing = false, error = ex.Message });
        }
    }
}