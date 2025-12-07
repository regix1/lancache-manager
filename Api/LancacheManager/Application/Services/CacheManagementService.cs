using System.Diagnostics;
using System.Text.Json;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Application.Services;

public class CacheManagementService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<CacheManagementService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly ProcessManager _processManager;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly NginxLogRotationService _nginxLogRotationService;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly string _cachePath;
    private readonly string _logPath;

    // Lock for thread safety during Rust binary execution
    private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);
    private DateTime? _lastLogWarningTime; // Track last time we logged a warning
    private readonly TimeSpan _logWarningThrottle = TimeSpan.FromMinutes(5); // Only log warnings every 5 minutes

    public CacheManagementService(IConfiguration configuration, ILogger<CacheManagementService> logger, IPathResolver pathResolver, ProcessManager processManager, RustProcessHelper rustProcessHelper, NginxLogRotationService nginxLogRotationService, IHubContext<DownloadHub> hubContext)
    {
        _configuration = configuration;
        _logger = logger;
        _pathResolver = pathResolver;
        _processManager = processManager;
        _rustProcessHelper = rustProcessHelper;
        _nginxLogRotationService = nginxLogRotationService;
        _hubContext = hubContext;

        // Use PathResolver to get properly resolved paths
        var configCachePath = configuration["LanCache:CachePath"];
        _cachePath = !string.IsNullOrEmpty(configCachePath)
            ? _pathResolver.ResolvePath(configCachePath)
            : _pathResolver.GetCacheDirectory();

        var configLogPath = configuration["LanCache:LogPath"];
        _logPath = !string.IsNullOrEmpty(configLogPath)
            ? _pathResolver.ResolvePath(configLogPath)
            : Path.Combine(_pathResolver.GetLogsDirectory(), "access.log");

        // Check if cache directory exists, create if it doesn't
        if (!Directory.Exists(_cachePath))
        {
            try
            {
                Directory.CreateDirectory(_cachePath);
                _logger.LogInformation($"Created cache directory: {_cachePath}");
            }
            catch (Exception ex)
            {
                var errorMsg = $"Failed to create cache directory: {_cachePath}. Error: {ex.Message}";
                _logger.LogError(ex, errorMsg);
                // Don't throw - allow the service to start but log the error
                // The cache operations will fail appropriately when attempted
            }
        }

        _logger.LogInformation($"CacheManagementService initialized - Cache: {_cachePath}, Logs: {_logPath}");
    }

    public CacheInfo GetCacheInfo()
    {
        var info = new CacheInfo();

        try
        {
            // For Windows development, skip drive info
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                return info;
            }

            // Find the actual mount point for the cache directory
            var mountPoint = GetMountPoint(_cachePath);

            if (Directory.Exists(mountPoint))
            {
                var driveInfo = new DriveInfo(mountPoint);
                info.TotalCacheSize = driveInfo.TotalSize;
                info.FreeCacheSize = driveInfo.AvailableFreeSpace;
                info.UsedCacheSize = info.TotalCacheSize - info.FreeCacheSize;

            }
            else
            {
                _logger.LogWarning($"Mount point does not exist: {mountPoint}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache info");
        }

        return info;
    }

    private string GetMountPoint(string path)
    {
        try
        {
            // Read /proc/mounts to find the correct mount point
            if (File.Exists("/proc/mounts"))
            {
                var mounts = File.ReadAllLines("/proc/mounts");
                var bestMatch = "/";
                var bestMatchLength = 0;

                foreach (var mount in mounts)
                {
                    var parts = mount.Split(' ');
                    if (parts.Length >= 2)
                    {
                        var mountPoint = parts[1];
                        // Check if this mount point is a parent of our path
                        if (path.StartsWith(mountPoint) && mountPoint.Length > bestMatchLength)
                        {
                            bestMatch = mountPoint;
                            bestMatchLength = mountPoint.Length;
                        }
                    }
                }

                return bestMatch;
            }

            // Fallback: check if the cache path is a mount point
            var cachePath = _pathResolver.GetCacheDirectory();
            if (path.StartsWith(cachePath))
            {
                if (Directory.Exists(cachePath) && new DriveInfo(cachePath).TotalSize > 0)
                    return cachePath;
            }

            // Last resort: use root
            return "/";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Error detecting mount point for {path}, using root");
        }

        return "/";
    }

    public string GetCachePath()
    {
        return _cachePath;
    }

    /// <summary>
    /// Invalidate the service counts cache - call this after modifying logs
    /// </summary>
    public async Task InvalidateServiceCountsCache()
    {
        // Delete the Rust cache file to force rescan
        var dataDir = _pathResolver.GetDataDirectory();
        var progressFile = Path.Combine(dataDir, "log_count_progress.json");

        if (File.Exists(progressFile))
        {
            await Task.Run(() => File.Delete(progressFile));
        }
    }

    /// <summary>
    /// Execute an operation while holding the shared lock to prevent concurrent Rust processes.
    /// This ensures only one Rust process accesses the logs/cache at a time, preventing file locking issues.
    /// </summary>
    public async Task<T> ExecuteWithLockAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
    {
        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            return await operation();
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task RemoveServiceFromLogs(string service, CancellationToken cancellationToken = default)
    {
        // Use semaphore to ensure only one Rust process runs at a time
        await _cacheLock.WaitAsync();
        try
        {
            // Invalidate cache since we're modifying logs
            await InvalidateServiceCountsCache();

            if (!File.Exists(_logPath))
            {
                throw new FileNotFoundException($"Log file not found: {_logPath}");
            }

            var logDir = Path.GetDirectoryName(_logPath) ?? _pathResolver.GetLogsDirectory();

            // Check write permissions using PathResolver
            if (!_pathResolver.IsLogsDirectoryWritable())
            {
                var errorMsg = $"Cannot write to logs directory: {logDir}. " +
                              "Directory is mounted read-only. " +
                              "Remove :ro from the logs volume mount in docker-compose.yml to enable log management features.";
                _logger.LogWarning(errorMsg);
                throw new UnauthorizedAccessException(errorMsg);
            }

            // Use Rust binary for fast log filtering
            var dataDir = _pathResolver.GetDataDirectory();

            var operationsDir = _pathResolver.GetOperationsDirectory();
            var progressFile = Path.Combine(operationsDir, "log_remove_progress.json");
            var rustBinaryPath = _pathResolver.GetRustLogManagerPath();

            // Check if Rust binary exists
            _rustProcessHelper.ValidateRustBinaryExists(rustBinaryPath, "Rust log_manager");

            _logger.LogInformation($"Using Rust binary for log filtering: {rustBinaryPath}");

            var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                rustBinaryPath,
                $"remove \"{logDir}\" \"{service}\" \"{progressFile}\"");

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new Exception("Failed to start Rust log_manager process");
                }

                // Read stdout and stderr asynchronously to prevent buffer deadlock
                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                await _processManager.WaitForProcessAsync(process, cancellationToken);

                var output = await outputTask;
                var error = await errorTask;

                if (process.ExitCode != 0)
                {
                    throw new Exception($"Rust log_manager failed with exit code {process.ExitCode}: {error}");
                }

                _logger.LogInformation($"Rust log filtering completed: {output}");
                if (!string.IsNullOrEmpty(error))
                {
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Re-throw with clear message
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error removing {service} from logs with Rust binary");
            throw;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task<Dictionary<string, long>> GetServiceLogCounts(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        // Use semaphore to ensure only one Rust process runs at a time
        await _cacheLock.WaitAsync();
        try
        {
            // Use Rust binary which has its own file-based caching with modification time validation
            // No need for C# in-memory cache - Rust handles it efficiently
            var counts = new Dictionary<string, long>();

            // Extract log directory - Rust will discover all log files (access.log, .1, .2.gz, etc.)
            var logDir = Path.GetDirectoryName(_logPath) ?? _pathResolver.GetLogsDirectory();

            if (!Directory.Exists(logDir))
            {
                LogThrottledWarning($"Log directory not found: {logDir}");
                return counts;
            }

            // Use Rust binary for fast log counting
            var dataDir = _pathResolver.GetDataDirectory();

            // Ensure data directory exists
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
                _logger.LogInformation($"Created data directory: {dataDir}");
            }

            var progressFile = Path.Combine(dataDir, "log_count_progress.json");
            var rustBinaryPath = _pathResolver.GetRustLogManagerPath();

            // Check if Rust binary exists
            _rustProcessHelper.ValidateRustBinaryExists(rustBinaryPath, "Rust log_manager");

            // If forceRefresh is true, delete the cache file to force Rust to rescan
            if (forceRefresh && File.Exists(progressFile))
            {
                _logger.LogInformation("Force refresh - deleting cached progress file: {ProgressFile}", progressFile);
                File.Delete(progressFile);
            }

            var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                rustBinaryPath,
                $"count \"{logDir}\" \"{progressFile}\"");

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new Exception("Failed to start Rust log_manager process");
                }

                // Read stdout and stderr asynchronously to prevent buffer deadlock
                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                await _processManager.WaitForProcessAsync(process, cancellationToken);

                var output = await outputTask;
                var error = await errorTask;

                if (process.ExitCode != 0)
                {
                    throw new Exception($"Rust log_manager failed with exit code {process.ExitCode}: {error}");
                }

                if (!string.IsNullOrEmpty(error))
                {
                }

                // Read results from progress file
                var progressData = await _rustProcessHelper.ReadProgressFileAsync<LogCountProgressData>(progressFile);

                if (progressData != null)
                {
                    try
                    {

                        if (progressData?.ServiceCounts != null)
                        {
                            counts = progressData.ServiceCounts.ToDictionary(kvp => kvp.Key, kvp => (long)kvp.Value);
                            // Rust binary handles caching via file modification time - no need for C# cache
                        }
                        else
                        {
                            LogThrottledWarning($"Rust progress file did not include service counts. Path: {progressFile}");
                        }
                    }
                    catch (JsonException jsonEx)
                    {
                        LogThrottledWarning($"Rust progress file contained invalid JSON. Path: {progressFile}. Error: {jsonEx.Message}");
                        return counts;
                    }
                }
            }

            return counts;
        }
        catch (Exception ex)
        {
            // If the log file doesn't exist, return empty counts instead of throwing
            if (ex.Message.Contains("No such file or directory") || ex.Message.Contains("os error 2"))
            {
                LogThrottledWarning($"Log file not accessible: {_logPath}. Returning empty service counts.");
                return new Dictionary<string, long>();
            }

            // Log the full exception details for debugging
            _logger.LogError(ex, "Error counting service logs with Rust binary. Exception type: {ExceptionType}, Message: {Message}",
                ex.GetType().Name, ex.Message);
            throw;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Logs a warning message, but throttles to only log once every 5 minutes
    /// to prevent log spam when repeatedly called
    /// </summary>
    private void LogThrottledWarning(string message)
    {
        var now = DateTime.UtcNow;
        if (!_lastLogWarningTime.HasValue || (now - _lastLogWarningTime.Value) > _logWarningThrottle)
        {
            _logger.LogWarning(message);
            _lastLogWarningTime = now;
        }
        else
        {
            // Log at debug level so we can still track calls, but don't spam warnings
        }
    }

    // Helper class for deserializing Rust progress data
    private class LogCountProgressData
    {
        [System.Text.Json.Serialization.JsonPropertyName("is_processing")]
        public bool IsProcessing { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("percent_complete")]
        public double PercentComplete { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("lines_processed")]
        public ulong LinesProcessed { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("service_counts")]
        public Dictionary<string, ulong>? ServiceCounts { get; set; }
    }

    // Helper class for corruption summary
    private class CorruptionSummaryData
    {
        [System.Text.Json.Serialization.JsonPropertyName("service_counts")]
        public Dictionary<string, ulong>? ServiceCounts { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("total_corrupted")]
        public ulong TotalCorrupted { get; set; }
    }

    // Helper classes for detailed corruption report
    private class CorruptionReport
    {
        [System.Text.Json.Serialization.JsonPropertyName("corrupted_chunks")]
        public List<CorruptedChunkDetail>? CorruptedChunks { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("summary")]
        public CorruptionSummaryData? Summary { get; set; }
    }

    public class CorruptedChunkDetail
    {
        [System.Text.Json.Serialization.JsonPropertyName("service")]
        public string Service { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("miss_count")]
        public ulong MissCount { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("cache_file_path")]
        public string CacheFilePath { get; set; } = string.Empty;
    }

    /// <summary>
    /// Get corruption summary with caching based on log file modification time
    /// </summary>
    public async Task<Dictionary<string, long>> GetCorruptionSummary(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        // Use semaphore to ensure only one Rust process runs at a time
        await _cacheLock.WaitAsync();
        try
        {
            var logDir = _pathResolver.GetLogsDirectory();
            var cacheDir = _cachePath;
            var timezone = Environment.GetEnvironmentVariable("TZ") ?? "UTC";

            var rustBinaryPath = _pathResolver.GetRustCorruptionManagerPath();

            _rustProcessHelper.ValidateRustBinaryExists(rustBinaryPath, "Corruption manager");

            var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                rustBinaryPath,
                $"summary \"{logDir}\" \"{cacheDir}\" \"{timezone}\"");

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new Exception("Failed to start corruption_manager process");
                }

                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                await _processManager.WaitForProcessAsync(process, cancellationToken);

                var output = await outputTask;
                var error = await errorTask;

                // Only log exit code for debugging (not at Information level to avoid log spam)
                _logger.LogDebug("[CorruptionDetection] Rust process exit code: {Code}", process.ExitCode);

                if (process.ExitCode != 0)
                {
                    _logger.LogError("[CorruptionDetection] Failed with exit code {Code}: {Error}", process.ExitCode, error);
                    throw new Exception($"corruption_manager failed with exit code {process.ExitCode}: {error}");
                }

                // Parse JSON output from Rust binary
                var summaryData = JsonSerializer.Deserialize<CorruptionSummaryData>(output,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (summaryData?.ServiceCounts == null)
                {
                    _logger.LogInformation("[CorruptionDetection] No service counts in result");
                    return new Dictionary<string, long>();
                }

                var finalResult = summaryData.ServiceCounts.ToDictionary(kvp => kvp.Key, kvp => (long)kvp.Value);

                // Only log if corruption was actually found
                if (finalResult.Count > 0)
                {
                    _logger.LogInformation("[CorruptionDetection] Summary generated: {Services}",
                        string.Join(", ", finalResult.Select(kvp => $"{kvp.Key}={kvp.Value}")));
                }

                // No caching needed - Rust binary is fast enough to run on every request
                return finalResult;
            }
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Remove corrupted chunks for a specific service (invalidates cache)
    /// </summary>
    public async Task RemoveCorruptedChunks(string service, CancellationToken cancellationToken = default)
    {
        // Use semaphore to ensure only one Rust process runs at a time
        await _cacheLock.WaitAsync();
        try
        {
            _logger.LogInformation("[CorruptionDetection] RemoveCorruptedChunks for service: {Service}", service);

            var logDir = Path.GetDirectoryName(_logPath) ?? _pathResolver.GetLogsDirectory();
            var cacheDir = _cachePath;

            // Check write permissions for both cache and logs directories
            if (!_pathResolver.IsCacheDirectoryWritable())
            {
                var errorMsg = $"Cannot write to cache directory: {cacheDir}. " +
                              "Directory is mounted read-only. " +
                              "Remove :ro from the cache volume mount in docker-compose.yml to enable corruption removal.";
                _logger.LogWarning(errorMsg);
                throw new UnauthorizedAccessException(errorMsg);
            }

            if (!_pathResolver.IsLogsDirectoryWritable())
            {
                var errorMsg = $"Cannot write to logs directory: {logDir}. " +
                              "Directory is mounted read-only. " +
                              "Remove :ro from the logs volume mount in docker-compose.yml to enable corruption removal.";
                _logger.LogWarning(errorMsg);
                throw new UnauthorizedAccessException(errorMsg);
            }

            var dbPath = _pathResolver.GetDatabasePath();
            var operationsDir = _pathResolver.GetOperationsDirectory();
            var progressPath = Path.Combine(operationsDir, "corruption_removal_progress.json");

            _logger.LogInformation("[CorruptionDetection] Removal params - db: {DbPath}, logDir: {LogDir}, cacheDir: {CacheDir}, progress: {Progress}",
                dbPath, logDir, cacheDir, progressPath);

            // Send start notification via SignalR
            await _hubContext.Clients.All.SendAsync("CorruptionRemovalStarted", new
            {
                service,
                message = $"Starting corruption removal for {service}...",
                timestamp = DateTime.UtcNow
            }, cancellationToken);

            var rustBinaryPath = _pathResolver.GetRustCorruptionManagerPath();

            _rustProcessHelper.ValidateRustBinaryExists(rustBinaryPath, "Corruption manager");

            var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                rustBinaryPath,
                $"remove \"{dbPath}\" \"{logDir}\" \"{cacheDir}\" \"{service}\" \"{progressPath}\"");

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new Exception("Failed to start corruption_manager process");
                }

                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                await _processManager.WaitForProcessAsync(process, cancellationToken);

                var output = await outputTask;
                var error = await errorTask;

                _logger.LogInformation("[CorruptionDetection] Removal process exit code: {Code}", process.ExitCode);
                _logger.LogInformation("[CorruptionDetection] Removal stdout: {Output}", output);
                if (!string.IsNullOrEmpty(error))
                {
                    _logger.LogWarning("[CorruptionDetection] Removal stderr: {Error}", error);
                }

                if (process.ExitCode != 0)
                {
                    _logger.LogError("[CorruptionDetection] Removal failed with exit code {Code}: {Error}", process.ExitCode, error);

                    // Send failure notification via SignalR
                    await _hubContext.Clients.All.SendAsync("CorruptionRemovalComplete", new
                    {
                        success = false,
                        service,
                        message = $"Failed to remove corrupted chunks for {service}",
                        error = error,
                        timestamp = DateTime.UtcNow
                    }, cancellationToken);

                    throw new Exception($"corruption_manager failed with exit code {process.ExitCode}: {error}");
                }

                _logger.LogInformation("[CorruptionDetection] Successfully removed corrupted chunks for {Service}", service);

                // Send success notification via SignalR
                await _hubContext.Clients.All.SendAsync("CorruptionRemovalComplete", new
                {
                    success = true,
                    service,
                    message = $"Successfully removed corrupted chunks for {service}",
                    timestamp = DateTime.UtcNow
                }, cancellationToken);
            }

            // No corruption summary cache to invalidate (Rust runs fresh each time)
            // Invalidate service count cache since corruption removal affects counts
            await InvalidateServiceCountsCache();

            // Signal nginx to reopen log files (prevents monolithic container from losing log access)
            await _nginxLogRotationService.ReopenNginxLogsAsync();
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Get detailed corruption information for a specific service
    /// </summary>
    public async Task<List<CorruptedChunkDetail>> GetCorruptionDetails(string service, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        // Use semaphore to ensure only one Rust process runs at a time
        await _cacheLock.WaitAsync();
        try
        {
            _logger.LogInformation("[CorruptionDetection] GetCorruptionDetails for service: {Service}, forceRefresh: {ForceRefresh}",
                service, forceRefresh);

            var logDir = _pathResolver.GetLogsDirectory();
            var cacheDir = _cachePath;
            var dataDir = _pathResolver.GetDataDirectory();
            var timezone = Environment.GetEnvironmentVariable("TZ") ?? "UTC";
            var outputJson = Path.Combine(dataDir, $"corruption_details_{service}_{DateTime.UtcNow:yyyyMMddHHmmss}.json");

            var rustBinaryPath = _pathResolver.GetRustCorruptionManagerPath();

            _rustProcessHelper.ValidateRustBinaryExists(rustBinaryPath, "Corruption manager");

            try
            {
                var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                    rustBinaryPath,
                    $"detect \"{logDir}\" \"{cacheDir}\" \"{outputJson}\" \"{timezone}\"");

                _logger.LogInformation("[CorruptionDetection] Running detect command: {Command} {Args}",
                    rustBinaryPath, startInfo.Arguments);

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        throw new Exception("Failed to start corruption_manager process");
                    }

                    var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                    var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                    await _processManager.WaitForProcessAsync(process, cancellationToken);

                    var output = await outputTask;
                    var error = await errorTask;

                    _logger.LogInformation("[CorruptionDetection] Detect process exit code: {Code}", process.ExitCode);

                    if (process.ExitCode != 0)
                    {
                        _logger.LogError("[CorruptionDetection] Detect failed with exit code {Code}: {Error}",
                            process.ExitCode, error);
                        throw new Exception($"corruption_manager detect failed with exit code {process.ExitCode}: {error}");
                    }

                    // Read the generated JSON file
                    var report = await _rustProcessHelper.ReadAndCleanupOutputJsonAsync<CorruptionReport>(outputJson, "CorruptionDetection");

                    if (report?.CorruptedChunks == null)
                    {
                        _logger.LogInformation("[CorruptionDetection] No corrupted chunks in report");
                        return new List<CorruptedChunkDetail>();
                    }

                    // Filter by service
                    var serviceDetails = report.CorruptedChunks
                        .Where(chunk => chunk.Service.Equals(service, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    _logger.LogInformation("[CorruptionDetection] Found {Count} corrupted chunks for service {Service}",
                        serviceDetails.Count, service);

                    return serviceDetails;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CorruptionDetection] Error getting corruption details for service {Service}", service);
                throw;
            }
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    // Helper classes for game cache removal
    public class GameCacheRemovalReport
    {
        [System.Text.Json.Serialization.JsonPropertyName("game_app_id")]
        public uint GameAppId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("game_name")]
        public string GameName { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("cache_files_deleted")]
        public int CacheFilesDeleted { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("total_bytes_freed")]
        public ulong TotalBytesFreed { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("empty_dirs_removed")]
        public int EmptyDirsRemoved { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("log_entries_removed")]
        public ulong LogEntriesRemoved { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("depot_ids")]
        public List<uint> DepotIds { get; set; } = new List<uint>();
    }

    public class ServiceCacheRemovalReport
    {
        [System.Text.Json.Serialization.JsonPropertyName("service_name")]
        public string ServiceName { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("cache_files_deleted")]
        public int CacheFilesDeleted { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("total_bytes_freed")]
        public ulong TotalBytesFreed { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("log_entries_removed")]
        public ulong LogEntriesRemoved { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("database_entries_deleted")]
        public int DatabaseEntriesDeleted { get; set; }
    }

    /// <summary>
    /// Remove all cache files for a specific game
    /// </summary>
    public async Task<GameCacheRemovalReport> RemoveGameFromCache(uint gameAppId, CancellationToken cancellationToken = default)
    {
        await _cacheLock.WaitAsync();
        try
        {
            _logger.LogInformation("[GameRemoval] Starting game cache removal for AppID {AppId}", gameAppId);

            // Check write permissions for cache directory
            if (!_pathResolver.IsCacheDirectoryWritable())
            {
                var errorMsg = $"Cannot write to cache directory: {_cachePath}. " +
                              "Directory is mounted read-only. " +
                              "Remove :ro from the cache volume mount in docker-compose.yml to enable game cache removal.";
                _logger.LogWarning(errorMsg);
                throw new UnauthorizedAccessException(errorMsg);
            }

            var dataDir = _pathResolver.GetDataDirectory();
            var dbPath = _pathResolver.GetDatabasePath();
            var logsDir = _pathResolver.GetLogsDirectory();
            var outputJson = Path.Combine(dataDir, $"game_removal_{gameAppId}_{DateTime.UtcNow:yyyyMMddHHmmss}.json");

            var rustBinaryPath = _pathResolver.GetRustGameRemoverPath();

            _rustProcessHelper.ValidateRustBinaryExists(rustBinaryPath, "Game cache remover");

            if (!File.Exists(dbPath))
            {
                var errorMsg = $"Database not found at {dbPath}";
                _logger.LogError(errorMsg);
                throw new FileNotFoundException(errorMsg);
            }

            if (!Directory.Exists(logsDir))
            {
                var errorMsg = $"Logs directory not found at {logsDir}";
                _logger.LogError(errorMsg);
                throw new DirectoryNotFoundException(errorMsg);
            }

            var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                rustBinaryPath,
                $"\"{dbPath}\" \"{logsDir}\" \"{_cachePath}\" {gameAppId} \"{outputJson}\"");

            _logger.LogInformation("[GameRemoval] Running removal: {Binary} {Args}", rustBinaryPath, startInfo.Arguments);

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new Exception("Failed to start game_cache_remover process");
                }

                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                await _processManager.WaitForProcessAsync(process, cancellationToken);

                var output = await outputTask;
                var error = await errorTask;

                _logger.LogInformation("[GameRemoval] Process exit code: {Code}", process.ExitCode);

                // Log stdout (completion messages and summary)
                if (!string.IsNullOrEmpty(output))
                {
                    _logger.LogInformation("[GameRemoval] Process output:\n{Output}", output);
                }

                // Log stderr (diagnostic messages)
                if (!string.IsNullOrEmpty(error))
                {
                    _logger.LogInformation("[GameRemoval] Process stderr: {Error}", error);
                }

                if (process.ExitCode != 0)
                {
                    _logger.LogError("[GameRemoval] Failed with exit code {Code}: {Error}", process.ExitCode, error);
                    throw new Exception($"game_cache_remover failed with exit code {process.ExitCode}: {error}");
                }

                // Read the generated JSON file
                var report = await _rustProcessHelper.ReadAndCleanupOutputJsonAsync<GameCacheRemovalReport>(outputJson, "GameRemoval");

                _logger.LogInformation("[GameRemoval] Removed {Files} files ({Bytes} bytes) for game {AppId}",
                    report.CacheFilesDeleted, report.TotalBytesFreed, gameAppId);

                // Signal nginx to reopen log files (prevents monolithic container from losing log access)
                await _nginxLogRotationService.ReopenNginxLogsAsync();

                return report;
            }
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Remove all cache files for a specific service
    /// </summary>
    public async Task<ServiceCacheRemovalReport> RemoveServiceFromCache(string serviceName, CancellationToken cancellationToken = default)
    {
        await _cacheLock.WaitAsync();
        try
        {
            _logger.LogInformation("[ServiceRemoval] Starting service cache removal for '{Service}'", serviceName);

            // Check write permissions for cache and logs directories
            if (!_pathResolver.IsCacheDirectoryWritable())
            {
                var errorMsg = $"Cannot write to cache directory: {_cachePath}. " +
                              "Directory is mounted read-only. " +
                              "Remove :ro from the cache volume mount in docker-compose.yml to enable service cache removal.";
                _logger.LogWarning(errorMsg);
                throw new UnauthorizedAccessException(errorMsg);
            }

            var dataDir = _pathResolver.GetDataDirectory();
            var dbPath = _pathResolver.GetDatabasePath();
            var logsDir = _pathResolver.GetLogsDirectory();

            if (!_pathResolver.IsLogsDirectoryWritable())
            {
                var errorMsg = $"Cannot write to logs directory: {logsDir}. " +
                              "Directory is mounted read-only. " +
                              "Remove :ro from the logs volume mount in docker-compose.yml to enable service cache removal.";
                _logger.LogWarning(errorMsg);
                throw new UnauthorizedAccessException(errorMsg);
            }
            var progressPath = Path.Combine(dataDir, $"service_removal_{serviceName}_{DateTime.UtcNow:yyyyMMddHHmmss}.json");

            var rustBinaryPath = _pathResolver.GetRustServiceRemoverPath();

            _rustProcessHelper.ValidateRustBinaryExists(rustBinaryPath, "Service remover");

            if (!File.Exists(dbPath))
            {
                var errorMsg = $"Database not found at {dbPath}";
                _logger.LogError(errorMsg);
                throw new FileNotFoundException(errorMsg);
            }

            if (!Directory.Exists(logsDir))
            {
                var errorMsg = $"Logs directory not found at {logsDir}";
                _logger.LogError(errorMsg);
                throw new DirectoryNotFoundException(errorMsg);
            }

            var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                rustBinaryPath,
                $"\"{dbPath}\" \"{logsDir}\" \"{_cachePath}\" \"{serviceName}\" \"{progressPath}\"");

            _logger.LogInformation("[ServiceRemoval] Running removal: {Binary} {Args}", rustBinaryPath, startInfo.Arguments);

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new Exception("Failed to start service_remover process");
                }

                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                await _processManager.WaitForProcessAsync(process, cancellationToken);

                var output = await outputTask;
                var error = await errorTask;

                _logger.LogInformation("[ServiceRemoval] Process exit code: {Code}", process.ExitCode);

                // Log stdout (completion messages and summary)
                if (!string.IsNullOrEmpty(output))
                {
                    _logger.LogInformation("[ServiceRemoval] Process output:\n{Output}", output);
                }

                // Log stderr (diagnostic messages)
                if (!string.IsNullOrEmpty(error))
                {
                    _logger.LogInformation("[ServiceRemoval] Process stderr: {Error}", error);
                }

                if (process.ExitCode != 0)
                {
                    _logger.LogError("[ServiceRemoval] Failed with exit code {Code}: {Error}", process.ExitCode, error);
                    throw new Exception($"service_remover failed with exit code {process.ExitCode}: {error}");
                }

                // Read the progress JSON file for the final report
                // The progress file contains the final status with all stats
                if (!File.Exists(progressPath))
                {
                    throw new FileNotFoundException($"Progress file not found: {progressPath}");
                }

                var progressJson = await File.ReadAllTextAsync(progressPath, cancellationToken);

                // Parse to get the message which contains the summary
                // For now, return a basic report - the Rust binary writes progress, not a full report
                var report = new ServiceCacheRemovalReport
                {
                    ServiceName = serviceName,
                    // These will be extracted from stderr output
                    CacheFilesDeleted = 0,
                    TotalBytesFreed = 0,
                    LogEntriesRemoved = 0,
                    DatabaseEntriesDeleted = 0
                };

                // Parse statistics from stderr output
                if (!string.IsNullOrEmpty(error))
                {
                    ExtractServiceRemovalStats(error, report);
                }

                _logger.LogInformation("[ServiceRemoval] Removed {Files} files ({Bytes} bytes) for service '{Service}'",
                    report.CacheFilesDeleted, report.TotalBytesFreed, serviceName);

                // Clean up progress file
                await _rustProcessHelper.DeleteTemporaryFileAsync(progressPath);

                // Signal nginx to reopen log files (prevents monolithic container from losing log access)
                await _nginxLogRotationService.ReopenNginxLogsAsync();

                return report;
            }
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private static void ExtractServiceRemovalStats(string stderr, ServiceCacheRemovalReport report)
    {
        // Extract statistics from stderr output
        // Format: "Cache files deleted: 123"
        var cacheFilesMatch = System.Text.RegularExpressions.Regex.Match(stderr, @"Cache files deleted:\s*(\d+)");
        if (cacheFilesMatch.Success && int.TryParse(cacheFilesMatch.Groups[1].Value, out var cacheFiles))
        {
            report.CacheFilesDeleted = cacheFiles;
        }

        // Format: "Bytes freed: 1.23 GB" or "Bytes freed: 123.45 MB"
        var bytesMatch = System.Text.RegularExpressions.Regex.Match(stderr, @"Bytes freed:\s*([\d.]+)\s*(GB|MB)");
        if (bytesMatch.Success && double.TryParse(bytesMatch.Groups[1].Value, out var bytes))
        {
            var unit = bytesMatch.Groups[2].Value;
            report.TotalBytesFreed = unit == "GB"
                ? (ulong)(bytes * 1_073_741_824.0)
                : (ulong)(bytes * 1_048_576.0);
        }

        // Format: "Log entries removed: 456"
        var logEntriesMatch = System.Text.RegularExpressions.Regex.Match(stderr, @"Log entries removed:\s*(\d+)");
        if (logEntriesMatch.Success && ulong.TryParse(logEntriesMatch.Groups[1].Value, out var logEntries))
        {
            report.LogEntriesRemoved = logEntries;
        }

        // Format: "Database entries deleted: 789"
        var dbEntriesMatch = System.Text.RegularExpressions.Regex.Match(stderr, @"Database entries deleted:\s*(\d+)");
        if (dbEntriesMatch.Success && int.TryParse(dbEntriesMatch.Groups[1].Value, out var dbEntries))
        {
            report.DatabaseEntriesDeleted = dbEntries;
        }
    }
}
