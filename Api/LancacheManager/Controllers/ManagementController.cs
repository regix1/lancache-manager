using Microsoft.AspNetCore.Mvc;
using LancacheManager.Services;
using LancacheManager.Security;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ManagementController : ControllerBase
{
    private readonly CacheManagementService _cacheService;
    private readonly DatabaseService _dbService;
    private readonly CacheClearingService _cacheClearingService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ManagementController> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly StateService _stateService;
    private readonly LogWatcherService _logWatcherService;
    private readonly LogProcessingService _logProcessingService;
    private static CancellationTokenSource? _processingCancellation;
    private static DateTime? _processingStartTime;

    public ManagementController(
        CacheManagementService cacheService,
        DatabaseService dbService,
        CacheClearingService cacheClearingService,
        IConfiguration configuration,
        ILogger<ManagementController> logger,
        IPathResolver pathResolver,
        StateService stateService,
        LogWatcherService logWatcherService,
        LogProcessingService logProcessingService)
    {
        _cacheService = cacheService;
        _dbService = dbService;
        _cacheClearingService = cacheClearingService;
        _configuration = configuration;
        _logger = logger;
        _pathResolver = pathResolver;
        _stateService = stateService;
        _logWatcherService = logWatcherService;
        _logProcessingService = logProcessingService;

        // Ensure data directory exists
        var dataDirectory = _pathResolver.GetDataDirectory();
        if (!Directory.Exists(dataDirectory))
        {
            try
            {
                Directory.CreateDirectory(dataDirectory);
                _logger.LogInformation($"Created data directory: {dataDirectory}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create data directory: {dataDirectory}");
            }
        }
    }

    [HttpGet("cache")]
    public IActionResult GetCacheInfo()
    {
        var info = _cacheService.GetCacheInfo();
        return Ok(info);
    }

    // New async endpoint for clearing all cache
    [HttpPost("cache/clear-all")]
    [RequireAuth]
    public async Task<IActionResult> ClearAllCache()
    {
        try
        {
            // Start the background operation
            var operationId = await _cacheClearingService.StartCacheClearAsync();
            
            _logger.LogInformation($"Started cache clear operation: {operationId}");
            
            return Ok(new { 
                message = "Cache clearing started in background",
                operationId = operationId,
                status = "running"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting cache clear operation");
            return StatusCode(500, new { error = "Failed to start cache clearing", details = ex.Message });
        }
    }

    // Get status of cache clearing operation
    [HttpGet("cache/clear-status/{operationId}")]
    public IActionResult GetClearStatus(string operationId)
    {
        var status = _cacheClearingService.GetOperationStatus(operationId);
        
        if (status == null)
        {
            return NotFound(new { error = "Operation not found" });
        }
        
        return Ok(status);
    }

    // Cancel cache clearing operation
    [HttpPost("cache/clear-cancel/{operationId}")]
    [RequireAuth]
    public IActionResult CancelClearOperation(string operationId)
    {
        var cancelled = _cacheClearingService.CancelOperation(operationId);
        
        if (!cancelled)
        {
            return NotFound(new { error = "Operation not found or already completed" });
        }
        
        return Ok(new { message = "Operation cancelled", operationId = operationId });
    }

    // Legacy endpoint for compatibility
    [HttpDelete("cache")]
    [RequireAuth]
    public async Task<IActionResult> ClearCache([FromQuery] string? service = null)
    {
        try
        {
            if (string.IsNullOrEmpty(service))
            {
                // Use the new async method for clearing all cache
                var operationId = await _cacheClearingService.StartCacheClearAsync();
                return Ok(new { 
                    message = "Cache clearing started in background",
                    operationId = operationId,
                    status = "running"
                });
            }
            else
            {
                // For specific service, remove from logs instead
                await _cacheService.RemoveServiceFromLogs(service);
                return Ok(new { message = $"Removed {service} entries from logs" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error in clear cache operation");
            return StatusCode(500, new { error = "Failed to clear cache", details = ex.Message });
        }
    }

    [HttpDelete("database")]
    [RequireAuth]
    public async Task<IActionResult> ResetDatabase()
    {
        await _dbService.ResetDatabase();
        return Ok(new { message = "Database reset successfully" });
    }

    [HttpPost("reset-logs")]
    [RequireAuth]
    public async Task<IActionResult> ResetLogPosition([FromQuery] bool clearDatabase = false)
    {
        try
        {
            // Clear position to start from end
            _stateService.SetLogPosition(0);
            
            // Only reset database if explicitly requested
            if (clearDatabase)
            {
                await _dbService.ResetDatabase();
            }
            
            _logger.LogInformation("Log position reset - will start from end of log");
            
            return Ok(new { 
                message = "Log position reset successfully. Will start monitoring from the current end of the log file.",
                requiresRestart = false,
                databaseCleared = clearDatabase
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting log position");
            return StatusCode(500, new { error = "Failed to reset log position" });
        }
    }

    [HttpPost("process-all-logs")]
    [RequireAuth]
    public async Task<IActionResult> ProcessAllLogs([FromServices] OperationStateService stateService)
    {
        try
        {
            // Cancel any existing processing
            _processingCancellation?.Cancel();
            await Task.Delay(1000);
            
            _processingCancellation = new CancellationTokenSource();
            
            // Check if log file exists
            var logPath = Path.Combine(_pathResolver.GetLogsDirectory(), "access.log");
            if (!System.IO.File.Exists(logPath))
            {
                _logger.LogWarning($"Log file not found at: {logPath}");
                return Ok(new {
                    message = $"Log file not found at: {logPath}",
                    logSizeMB = 0,
                    estimatedTimeMinutes = 0,
                    requiresRestart = false,
                    status = "no_log_file"
                });
            }

            // Get log file size
            var fileInfo = new FileInfo(logPath);
            var sizeMB = fileInfo.Length / (1024.0 * 1024.0);
            
            // CHECK IF FILE IS EMPTY
            if (fileInfo.Length == 0)
            {
                _logger.LogWarning($"Log file is empty (0 bytes): {logPath}");
                return Ok(new { 
                    message = "Log file is empty. No data to process.",
                    logSizeMB = 0,
                    estimatedTimeMinutes = 0,
                    requiresRestart = false,
                    status = "empty_file"
                });
            }
            
            // CHECK IF FILE IS TOO SMALL (less than 100 bytes probably means no real data)
            if (fileInfo.Length < 100)
            {
                _logger.LogWarning($"Log file is very small ({fileInfo.Length} bytes): {logPath}");
                return Ok(new { 
                    message = $"Log file only contains {fileInfo.Length} bytes. Likely no game data yet.",
                    logSizeMB = sizeMB,
                    estimatedTimeMinutes = 0,
                    requiresRestart = false,
                    status = "insufficient_data"
                });
            }
            
            _logger.LogInformation($"Starting full log processing: {sizeMB:F1} MB");
            
            // Delete old marker first
            var processingMarker = Path.Combine(_pathResolver.GetDataDirectory(), "processing.marker");
            if (System.IO.File.Exists(processingMarker))
            {
                System.IO.File.Delete(processingMarker);
                await Task.Delay(100);
            }

            // Set position to 0
            _stateService.SetLogPosition(0);
            
            // Create marker with file size info
            var markerData = new
            {
                startTime = DateTime.UtcNow,
                startPosition = 0L,
                fileSize = fileInfo.Length,
                triggerType = "manual",
                requestId = Guid.NewGuid().ToString()
            };
            
            await System.IO.File.WriteAllTextAsync(processingMarker,
                System.Text.Json.JsonSerializer.Serialize(markerData));

            _processingStartTime = DateTime.UtcNow;

            // Start the log processing services for manual processing
            // (They are no longer auto-started, only run on manual trigger)
            try
            {
                _logger.LogInformation("Starting LogProcessingService for manual log processing");
                await _logProcessingService.StartAsync(_processingCancellation.Token);

                _logger.LogInformation("Starting LogWatcherService for manual log processing");
                await _logWatcherService.StartAsync(_processingCancellation.Token);
            }
            catch (Exception serviceEx)
            {
                _logger.LogWarning(serviceEx, "Error starting log processing services, they may already be running");
            }

            // Create operation state
            var operationState = new OperationState
            {
                Key = "activeLogProcessing",
                Type = "log_processing",
                Status = "processing",
                Message = "Processing entire log file from beginning",
                Data = new Dictionary<string, object>
                {
                    { "startTime", DateTime.UtcNow },
                    { "startPosition", 0L },
                    { "fileSize", fileInfo.Length },
                    { "percentComplete", 0 },
                    { "status", "processing" },
                    { "requestId", markerData.requestId }
                },
                ExpiresAt = DateTime.UtcNow.AddHours(24)
            };
            stateService.SaveState("activeLogProcessing", operationState);
            
            return Ok(new { 
                message = $"Processing entire log file ({sizeMB:F1} MB) from the beginning...",
                logSizeMB = sizeMB,
                estimatedTimeMinutes = Math.Max(1, Math.Ceiling(sizeMB / 100)),
                requiresRestart = false,
                status = "processing",
                requestId = markerData.requestId
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting up full log processing");
            return StatusCode(500, new { error = $"Failed to setup full log processing: {ex.Message}" });
        }
    }

    [HttpPost("cancel-processing")]
    [RequireAuth]
    public async Task<IActionResult> CancelProcessing()
    {
        try
        {
            // Signal cancellation
            _processingCancellation?.Cancel();

            // Stop the log processing services (this will save position in StopAsync)
            _logger.LogInformation("Stopping log processing services");
            await _logWatcherService.StopAsync(CancellationToken.None);
            await _logProcessingService.StopAsync(CancellationToken.None);

            // Give services a moment to save their state
            await Task.Delay(500);

            // Get the saved position after services have stopped
            var currentPosition = _stateService.GetLogPosition();
            _logger.LogInformation($"Processing cancelled at position {currentPosition:N0}, position preserved for resume");

            // Remove processing marker AFTER services have stopped and saved position
            var processingMarker = Path.Combine(_pathResolver.GetDataDirectory(), "processing.marker");
            if (System.IO.File.Exists(processingMarker))
            {
                System.IO.File.Delete(processingMarker);
                _logger.LogInformation("Removed processing marker");
            }

            return Ok(new {
                message = $"Processing cancelled at position {currentPosition:N0}. Position saved for resume.",
                currentPosition = currentPosition,
                requiresRestart = false
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling processing");
            return StatusCode(500, new { error = "Failed to cancel processing" });
        }
    }

    [HttpPost("post-process-depot-mappings")]
    [RequireAuth]
    public async Task<IActionResult> PostProcessDepotMappings()
    {
        try
        {
            _logger.LogInformation("Starting depot mapping post-processing");

            var mappingsProcessed = await _dbService.PostProcessDepotMappings();

            return Ok(new {
                message = $"Depot mapping post-processing completed. Processed {mappingsProcessed} downloads.",
                mappingsProcessed,
                status = "completed"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during depot mapping post-processing");
            return StatusCode(500, new { error = $"Failed to post-process depot mappings: {ex.Message}" });
        }
    }

    [HttpGet("processing-status")]
    public async Task<IActionResult> GetProcessingStatus()
    {
        try
        {
            // First check if marker exists
            var processingMarker = Path.Combine(_pathResolver.GetDataDirectory(), "processing.marker");
            bool markerExists = System.IO.File.Exists(processingMarker);

            // Then check position file
            long currentPosition = 0;
            long totalSize = 0;

            var logPath = Path.Combine(_pathResolver.GetLogsDirectory(), "access.log");
            if (System.IO.File.Exists(logPath))
            {
                var fileInfo = new FileInfo(logPath);
                totalSize = fileInfo.Length;
            }

            currentPosition = _stateService.GetLogPosition();
            
            // If no marker and position is at 0, we're not processing
            if (!markerExists && currentPosition == 0)
            {
                return Ok(new { 
                    isProcessing = false,
                    message = "Not processing",
                    currentPosition = 0,
                    totalSize = totalSize
                });
            }
            
            // If marker exists or position > 0, we might be processing
            if (markerExists || currentPosition > 0)
            {
                var percentComplete = totalSize > 0 ? (currentPosition * 100.0) / totalSize : 0;
                
                // Check if we're actually at the end
                if (currentPosition >= totalSize - 1000 && !markerExists)
                {
                    return Ok(new { 
                        isProcessing = false,
                        message = "Processing complete",
                        percentComplete = 100,
                        mbProcessed = totalSize / (1024.0 * 1024.0),
                        mbTotal = totalSize / (1024.0 * 1024.0)
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
                        processingRate = currentPosition / elapsed.TotalSeconds;
                        if (processingRate > 0)
                        {
                            var remainingBytes = totalSize - currentPosition;
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
                    totalSize,
                    percentComplete,
                    mbProcessed = currentPosition / (1024.0 * 1024.0),
                    mbTotal = totalSize / (1024.0 * 1024.0),
                    processingRate = processingRate / (1024.0 * 1024.0), // MB/s
                    estimatedTime,
                    message = $"Processing log file... {percentComplete:F1}% complete",
                    status = "processing",
                    markerExists
                });
            }
            
            return Ok(new { 
                isProcessing = false,
                message = "Not processing" 
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
    [RequireAuth]
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
                backupFile = $"{Path.Combine(_pathResolver.GetLogsDirectory(), "access.log")}.bak"
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

    [HttpGet("cache/clear-operations")]
    public IActionResult GetAllClearOperations()
    {
        try
        {
            var operations = _cacheClearingService.GetAllOperations();
            return Ok(operations);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache clear operations");
            return StatusCode(500, new { error = "Failed to get operations", message = ex.Message });
        }
    }

    [HttpGet("cache/active-operations")]
    public IActionResult GetActiveClearOperations()
    {
        try
        {
            var operations = _cacheClearingService.GetAllOperations()
                .Where(op => op.Status == "Running" || op.Status == "Preparing")
                .ToList();
            
            return Ok(new 
            { 
                hasActive = operations.Any(),
                operations = operations
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting active cache clear operations");
            return StatusCode(500, new { error = "Failed to get active operations", message = ex.Message });
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
                logPath = Path.Combine(_pathResolver.GetLogsDirectory(), "access.log"),
                services
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting configuration");
            return Ok(new {
                cachePath = _pathResolver.GetCacheDirectory(),
                logPath = Path.Combine(_pathResolver.GetLogsDirectory(), "access.log"),
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
            var logsDir = _pathResolver.GetLogsDirectory();
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
                
                // Count cache directories and estimate size
                try
                {
                    var dirs = Directory.GetDirectories(cachePath)
                        .Where(d => {
                            var name = Path.GetFileName(d);
                            return name.Length == 2 && IsHex(name);
                        })
                        .ToList();
                    
                    results["cacheDirectoryCount"] = dirs.Count;
                    
                    // Sample a few directories to estimate total size
                    if (dirs.Count > 0)
                    {
                        long totalFiles = 0;
                        long totalSize = 0;
                        int sampleCount = Math.Min(3, dirs.Count);
                        
                        for (int i = 0; i < sampleCount; i++)
                        {
                            var dir = dirs[i];
                            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                            totalFiles += files.Length;
                            
                            foreach (var file in files.Take(100)) // Sample first 100 files
                            {
                                try
                                {
                                    var fi = new FileInfo(file);
                                    totalSize += fi.Length;
                                }
                                catch { }
                            }
                        }
                        
                        // Extrapolate
                        if (sampleCount > 0)
                        {
                            var avgFilesPerDir = totalFiles / sampleCount;
                            var estimatedTotalFiles = avgFilesPerDir * dirs.Count;
                            results["estimatedCacheFiles"] = estimatedTotalFiles;
                            
                            if (totalFiles > 0)
                            {
                                var avgFileSize = totalSize / Math.Min(totalFiles, 100 * sampleCount);
                                var estimatedTotalSize = avgFileSize * estimatedTotalFiles;
                                results["estimatedCacheSizeGB"] = estimatedTotalSize / (1024.0 * 1024.0 * 1024.0);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    results["cacheSizeError"] = ex.Message;
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
            var dataDirectory = _pathResolver.GetDataDirectory();
            results["dataDirectory"] = dataDirectory;
            results["dataExists"] = Directory.Exists(dataDirectory);

            if (Directory.Exists(dataDirectory))
            {
                var testFile = Path.Combine(dataDirectory, ".write_test");
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
            var logPath = Path.Combine(_pathResolver.GetLogsDirectory(), "access.log");
            results["logFile"] = logPath;
            results["logFileExists"] = System.IO.File.Exists(logPath);

            if (System.IO.File.Exists(logPath))
            {
                var fileInfo = new FileInfo(logPath);
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

    [HttpGet("setup-status")]
    public IActionResult GetSetupStatus()
    {
        try
        {
            var isSetupCompleted = _stateService.GetSetupCompleted();
            return Ok(new { isSetupCompleted });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking setup status");
            return Ok(new { isSetupCompleted = false });
        }
    }

    [HttpPost("mark-setup-completed")]
    public IActionResult MarkSetupCompleted()
    {
        try
        {
            _stateService.SetSetupCompleted(true);
            _logger.LogInformation("Setup marked as completed");
            return Ok(new { success = true, message = "Setup marked as completed" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking setup as completed");
            return StatusCode(500, new { success = false, message = "Failed to mark setup as completed" });
        }
    }

    [HttpGet("settings")]
    public IActionResult GetSettings()
    {
        try
        {
            var settingsFile = Path.Combine(_pathResolver.GetDataDirectory(), "settings.json");

            if (!System.IO.File.Exists(settingsFile))
            {
                // Create default settings
                var defaultSettings = CreateDefaultSettings();
                System.IO.File.WriteAllText(settingsFile, System.Text.Json.JsonSerializer.Serialize(defaultSettings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return Ok(defaultSettings);
            }

            var settingsJson = System.IO.File.ReadAllText(settingsFile);
            var settings = System.Text.Json.JsonSerializer.Deserialize<object>(settingsJson);
            return Ok(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading settings");
            return StatusCode(500, new { error = "Failed to read settings" });
        }
    }

    [HttpPost("settings")]
    public IActionResult UpdateSettings([FromBody] object settings)
    {
        try
        {
            var settingsFile = Path.Combine(_pathResolver.GetDataDirectory(), "settings.json");
            var settingsJson = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(settingsFile, settingsJson);
            _logger.LogInformation("Settings updated successfully");
            return Ok(new { success = true, message = "Settings updated successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating settings");
            return StatusCode(500, new { success = false, message = "Failed to update settings" });
        }
    }

    private object CreateDefaultSettings()
    {
        return new
        {
            app = new
            {
                name = "Lancache Manager",
                version = "1.0.0",
                theme = "dark",
                language = "en",
                autoRefresh = true,
                refreshInterval = 30000,
                showNotifications = true
            },
            dashboard = new
            {
                defaultTimeRange = "24h",
                showClientStats = true,
                showServiceStats = true,
                showDownloads = true,
                maxRecentDownloads = 100,
                autoUpdateCharts = true
            },
            cache = new
            {
                clearConfirmation = true,
                showOperationProgress = true,
                defaultClearType = "selective"
            },
            logs = new
            {
                defaultLogLevel = "info",
                maxLogLines = 1000,
                autoScroll = true,
                showTimestamps = true,
                bulkProcessingBatchSize = 10000
            },
            depot = new
            {
                autoUpdateMappings = true,
                preferCloudData = true,
                cacheTimeout = 3600000
            },
            ui = new
            {
                compactMode = false,
                showTooltips = true,
                animationsEnabled = true,
                sidebarCollapsed = false
            }
        };
    }

    private bool IsHex(string value)
    {
        return value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
    }
}

// Request model for removing service
public class RemoveServiceRequest
{
    public string Service { get; set; } = string.Empty;
}