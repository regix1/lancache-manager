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
    private readonly DatasourceService _datasourceService;

    // Legacy single-path fields (for backward compatibility)
    private readonly string _cachePath;
    private readonly string _logPath;

    // Lock for thread safety during Rust binary execution
    private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);
    private DateTime? _lastLogWarningTime; // Track last time we logged a warning
    private readonly TimeSpan _logWarningThrottle = TimeSpan.FromMinutes(5); // Only log warnings every 5 minutes

    public CacheManagementService(
        IConfiguration configuration,
        ILogger<CacheManagementService> logger,
        IPathResolver pathResolver,
        ProcessManager processManager,
        RustProcessHelper rustProcessHelper,
        NginxLogRotationService nginxLogRotationService,
        IHubContext<DownloadHub> hubContext,
        DatasourceService datasourceService)
    {
        _configuration = configuration;
        _logger = logger;
        _pathResolver = pathResolver;
        _processManager = processManager;
        _rustProcessHelper = rustProcessHelper;
        _nginxLogRotationService = nginxLogRotationService;
        _hubContext = hubContext;
        _datasourceService = datasourceService;

        // Use DatasourceService for paths (with backward compatibility)
        var defaultDatasource = _datasourceService.GetDefaultDatasource();
        if (defaultDatasource != null)
        {
            _cachePath = defaultDatasource.CachePath;
            _logPath = defaultDatasource.LogFilePath;
        }
        else
        {
            // Fallback to legacy configuration
            var configCachePath = configuration["LanCache:CachePath"];
            _cachePath = !string.IsNullOrEmpty(configCachePath)
                ? _pathResolver.ResolvePath(configCachePath)
                : _pathResolver.GetCacheDirectory();

            var configLogPath = configuration["LanCache:LogPath"];
            _logPath = !string.IsNullOrEmpty(configLogPath)
                ? _pathResolver.ResolvePath(configLogPath)
                : Path.Combine(_pathResolver.GetLogsDirectory(), "access.log");
        }

        // Check if cache directories exist for all datasources
        foreach (var ds in _datasourceService.GetDatasources())
        {
            if (!Directory.Exists(ds.CachePath))
            {
                try
                {
                    Directory.CreateDirectory(ds.CachePath);
                    _logger.LogInformation("Created cache directory for datasource '{Name}': {Path}", ds.Name, ds.CachePath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create cache directory for datasource '{Name}': {Path}", ds.Name, ds.CachePath);
                }
            }
        }

        _logger.LogInformation("CacheManagementService initialized with {Count} datasource(s)", _datasourceService.DatasourceCount);
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
        var operationsDir = _pathResolver.GetOperationsDirectory();
        var progressFile = Path.Combine(operationsDir, "log_count_progress.json");

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

            var datasources = _datasourceService.GetDatasources();
            var rustBinaryPath = _pathResolver.GetRustLogManagerPath();

            // Check if Rust binary exists
            _rustProcessHelper.ValidateRustBinaryExists(rustBinaryPath, "Rust log_manager");

            _logger.LogInformation("Using Rust binary for log filtering across {Count} datasource(s): {Path}",
                datasources.Count, rustBinaryPath);

            // Process each datasource
            foreach (var datasource in datasources)
            {
                var logDir = datasource.LogPath;

                if (!Directory.Exists(logDir))
                {
                    _logger.LogWarning("Log directory not found for datasource '{Name}': {Path}, skipping",
                        datasource.Name, logDir);
                    continue;
                }

                // Check write permissions for this datasource's logs
                if (!datasource.LogsWritable)
                {
                    _logger.LogWarning("Logs directory is read-only for datasource '{Name}': {Path}, skipping",
                        datasource.Name, logDir);
                    continue;
                }

                var operationsDir = _pathResolver.GetOperationsDirectory();
                var progressFile = Path.Combine(operationsDir, $"log_remove_progress_{datasource.Name}.json");

                _logger.LogInformation("Removing {Service} entries from datasource '{DatasourceName}' logs: {LogDir}",
                    service, datasource.Name, logDir);

                var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                    rustBinaryPath,
                    $"remove \"{logDir}\" \"{service}\" \"{progressFile}\"");

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        throw new Exception($"Failed to start Rust log_manager process for datasource '{datasource.Name}'");
                    }

                    // Read stdout and stderr asynchronously to prevent buffer deadlock
                    var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                    var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                    await _processManager.WaitForProcessAsync(process, cancellationToken);

                    var output = await outputTask;
                    var error = await errorTask;

                    if (process.ExitCode != 0)
                    {
                        throw new Exception($"Rust log_manager failed for datasource '{datasource.Name}' with exit code {process.ExitCode}: {error}");
                    }

                    _logger.LogInformation("Rust log filtering completed for datasource '{DatasourceName}': {Output}",
                        datasource.Name, output);
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
            _logger.LogError(ex, "Error removing {Service} from logs with Rust binary", service);
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
            var aggregatedCounts = new Dictionary<string, long>();
            var datasources = _datasourceService.GetDatasources();

            // Process each datasource and aggregate counts
            foreach (var datasource in datasources)
            {
                var dsCounts = await GetServiceLogCountsForDatasource(datasource.Name, datasource.LogPath, forceRefresh, cancellationToken);

                // Aggregate counts
                foreach (var kvp in dsCounts)
                {
                    if (aggregatedCounts.ContainsKey(kvp.Key))
                    {
                        aggregatedCounts[kvp.Key] += kvp.Value;
                    }
                    else
                    {
                        aggregatedCounts[kvp.Key] = kvp.Value;
                    }
                }
            }

            return aggregatedCounts;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Get service log counts for a specific datasource.
    /// </summary>
    private async Task<Dictionary<string, long>> GetServiceLogCountsForDatasource(string datasourceName, string logDir, bool forceRefresh, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, long>();

        if (!Directory.Exists(logDir))
        {
            LogThrottledWarning($"Log directory not found for datasource '{datasourceName}': {logDir}");
            return counts;
        }

        try
        {
            // Use Rust binary for fast log counting
            var operationsDir = _pathResolver.GetOperationsDirectory();
            var progressFile = Path.Combine(operationsDir, $"log_count_progress_{datasourceName}.json");
            var rustBinaryPath = _pathResolver.GetRustLogManagerPath();

            // Check if Rust binary exists
            _rustProcessHelper.ValidateRustBinaryExists(rustBinaryPath, "Rust log_manager");

            // If forceRefresh is true, delete the cache file to force Rust to rescan
            if (forceRefresh && File.Exists(progressFile))
            {
                _logger.LogDebug("Force refresh - deleting cached progress file for datasource '{DatasourceName}': {ProgressFile}",
                    datasourceName, progressFile);
                File.Delete(progressFile);
            }

            var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                rustBinaryPath,
                $"count \"{logDir}\" \"{progressFile}\"");

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new Exception($"Failed to start Rust log_manager process for datasource '{datasourceName}'");
                }

                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                await _processManager.WaitForProcessAsync(process, cancellationToken);

                var output = await outputTask;
                var error = await errorTask;

                if (process.ExitCode != 0)
                {
                    throw new Exception($"Rust log_manager failed for datasource '{datasourceName}' with exit code {process.ExitCode}: {error}");
                }

                // Read results from progress file
                var progressData = await _rustProcessHelper.ReadProgressFileAsync<LogCountProgressData>(progressFile);

                if (progressData?.ServiceCounts != null)
                {
                    counts = progressData.ServiceCounts.ToDictionary(kvp => kvp.Key, kvp => (long)kvp.Value);
                }
            }
        }
        catch (Exception ex)
        {
            // If the log file doesn't exist, return empty counts instead of throwing
            if (ex.Message.Contains("No such file or directory") || ex.Message.Contains("os error 2"))
            {
                LogThrottledWarning($"Log file not accessible for datasource '{datasourceName}': {logDir}. Returning empty counts.");
                return counts;
            }

            _logger.LogError(ex, "Error counting service logs for datasource '{DatasourceName}'", datasourceName);
            throw;
        }

        return counts;
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
    /// Get corruption summary with caching based on log file modification time.
    /// Aggregates corruption from all configured datasources.
    /// </summary>
    public async Task<Dictionary<string, long>> GetCorruptionSummary(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        // Use semaphore to ensure only one Rust process runs at a time
        await _cacheLock.WaitAsync();
        try
        {
            var aggregatedCounts = new Dictionary<string, long>();
            var datasources = _datasourceService.GetDatasources();
            var timezone = Environment.GetEnvironmentVariable("TZ") ?? "UTC";
            var rustBinaryPath = _pathResolver.GetRustCorruptionManagerPath();

            _rustProcessHelper.ValidateRustBinaryExists(rustBinaryPath, "Corruption manager");

            // Process each datasource and aggregate corruption counts
            foreach (var datasource in datasources)
            {
                var dsCounts = await GetCorruptionSummaryForDatasource(datasource.LogPath, datasource.CachePath, timezone, rustBinaryPath, cancellationToken);

                // Aggregate counts
                foreach (var kvp in dsCounts)
                {
                    if (aggregatedCounts.ContainsKey(kvp.Key))
                    {
                        aggregatedCounts[kvp.Key] += kvp.Value;
                    }
                    else
                    {
                        aggregatedCounts[kvp.Key] = kvp.Value;
                    }
                }
            }

            // Only log if corruption was actually found
            if (aggregatedCounts.Count > 0)
            {
                _logger.LogInformation("[CorruptionDetection] Aggregated summary: {Services}",
                    string.Join(", ", aggregatedCounts.Select(kvp => $"{kvp.Key}={kvp.Value}")));
            }

            return aggregatedCounts;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Get corruption summary for a specific datasource.
    /// </summary>
    private async Task<Dictionary<string, long>> GetCorruptionSummaryForDatasource(string logDir, string cacheDir, string timezone, string rustBinaryPath, CancellationToken cancellationToken)
    {
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
                return new Dictionary<string, long>();
            }

            return summaryData.ServiceCounts.ToDictionary(kvp => kvp.Key, kvp => (long)kvp.Value);
        }
    }

    /// <summary>
    /// Remove corrupted chunks for a specific service (invalidates cache)
    /// Processes all datasources to remove corruption from all log/cache pairs.
    /// </summary>
    public async Task RemoveCorruptedChunks(string service, CancellationToken cancellationToken = default)
    {
        // Use semaphore to ensure only one Rust process runs at a time
        await _cacheLock.WaitAsync();
        try
        {
            _logger.LogInformation("[CorruptionDetection] RemoveCorruptedChunks for service: {Service}", service);

            var datasources = _datasourceService.GetDatasources();
            var dbPath = _pathResolver.GetDatabasePath();
            var operationsDir = _pathResolver.GetOperationsDirectory();
            var rustBinaryPath = _pathResolver.GetRustCorruptionManagerPath();

            _rustProcessHelper.ValidateRustBinaryExists(rustBinaryPath, "Corruption manager");

            // Send start notification via SignalR
            await _hubContext.Clients.All.SendAsync("CorruptionRemovalStarted", new
            {
                service,
                message = $"Starting corruption removal for {service} across {datasources.Count} datasource(s)...",
                timestamp = DateTime.UtcNow
            }, cancellationToken);

            var processedCount = 0;
            var skippedCount = 0;

            // Process each datasource
            foreach (var datasource in datasources)
            {
                var logDir = datasource.LogPath;
                var cacheDir = datasource.CachePath;

                // Check write permissions for this datasource
                if (!datasource.CacheWritable)
                {
                    _logger.LogWarning("[CorruptionDetection] Cache is read-only for datasource '{Name}': {Path}, skipping",
                        datasource.Name, cacheDir);
                    skippedCount++;
                    continue;
                }

                if (!datasource.LogsWritable)
                {
                    _logger.LogWarning("[CorruptionDetection] Logs are read-only for datasource '{Name}': {Path}, skipping",
                        datasource.Name, logDir);
                    skippedCount++;
                    continue;
                }

                if (!Directory.Exists(logDir))
                {
                    _logger.LogWarning("[CorruptionDetection] Log directory not found for datasource '{Name}': {Path}, skipping",
                        datasource.Name, logDir);
                    skippedCount++;
                    continue;
                }

                var progressPath = Path.Combine(operationsDir, $"corruption_removal_{datasource.Name}_{Guid.NewGuid()}.json");

                _logger.LogInformation("[CorruptionDetection] Processing datasource '{DatasourceName}' - logDir: {LogDir}, cacheDir: {CacheDir}",
                    datasource.Name, logDir, cacheDir);

                var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                    rustBinaryPath,
                    $"remove \"{dbPath}\" \"{logDir}\" \"{cacheDir}\" \"{service}\" \"{progressPath}\"");

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        throw new Exception($"Failed to start corruption_manager process for datasource '{datasource.Name}'");
                    }

                    var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                    var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                    await _processManager.WaitForProcessAsync(process, cancellationToken);

                    var output = await outputTask;
                    var error = await errorTask;

                    _logger.LogInformation("[CorruptionDetection] Removal process exit code for '{DatasourceName}': {Code}",
                        datasource.Name, process.ExitCode);

                    if (!string.IsNullOrEmpty(error))
                    {
                        _logger.LogWarning("[CorruptionDetection] Removal stderr for '{DatasourceName}': {Error}",
                            datasource.Name, error);
                    }

                    if (process.ExitCode != 0)
                    {
                        _logger.LogError("[CorruptionDetection] Removal failed for datasource '{DatasourceName}' with exit code {Code}: {Error}",
                            datasource.Name, process.ExitCode, error);

                        // Send failure notification via SignalR
                        await _hubContext.Clients.All.SendAsync("CorruptionRemovalComplete", new
                        {
                            success = false,
                            service,
                            message = $"Failed to remove corrupted chunks for {service} in datasource '{datasource.Name}'",
                            error = error,
                            timestamp = DateTime.UtcNow
                        }, cancellationToken);

                        throw new Exception($"corruption_manager failed for datasource '{datasource.Name}' with exit code {process.ExitCode}: {error}");
                    }

                    processedCount++;
                    _logger.LogInformation("[CorruptionDetection] Successfully removed corrupted chunks for {Service} in datasource '{DatasourceName}'",
                        service, datasource.Name);
                }
            }

            _logger.LogInformation("[CorruptionDetection] Corruption removal complete - processed {Processed} datasource(s), skipped {Skipped}",
                processedCount, skippedCount);

            // Send success notification via SignalR
            await _hubContext.Clients.All.SendAsync("CorruptionRemovalComplete", new
            {
                success = true,
                service,
                message = $"Successfully removed corrupted chunks for {service}",
                timestamp = DateTime.UtcNow
            }, cancellationToken);

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
            var operationsDir = _pathResolver.GetOperationsDirectory();
            var timezone = Environment.GetEnvironmentVariable("TZ") ?? "UTC";
            var outputJson = Path.Combine(operationsDir, $"corruption_details_{service}_{DateTime.UtcNow:yyyyMMddHHmmss}.json");

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

                    // Read the generated JSON file (keep for operation history)
                    var report = await _rustProcessHelper.ReadOutputJsonAsync<CorruptionReport>(outputJson, "CorruptionDetection");

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

            var operationsDir = _pathResolver.GetOperationsDirectory();
            var dbPath = _pathResolver.GetDatabasePath();
            var logsDir = _pathResolver.GetLogsDirectory();
            var outputJson = Path.Combine(operationsDir, $"game_removal_{gameAppId}_{DateTime.UtcNow:yyyyMMddHHmmss}.json");

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

                // Read the generated JSON file (keep for operation history)
                var report = await _rustProcessHelper.ReadOutputJsonAsync<GameCacheRemovalReport>(outputJson, "GameRemoval");

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

            var operationsDir = _pathResolver.GetOperationsDirectory();
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
            var progressPath = Path.Combine(operationsDir, $"service_removal_{serviceName}_{DateTime.UtcNow:yyyyMMddHHmmss}.json");

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
