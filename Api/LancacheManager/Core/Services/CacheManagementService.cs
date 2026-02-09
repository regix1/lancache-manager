using System.Diagnostics;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using static LancacheManager.Infrastructure.Utilities.FormattingUtils;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;


namespace LancacheManager.Core.Services;

public class CacheManagementService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<CacheManagementService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly ProcessManager _processManager;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly NginxLogRotationService _nginxLogRotationService;
    private readonly ISignalRNotificationService _notifications;
    private readonly DatasourceService _datasourceService;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly DockerClient? _dockerClient;

    // Legacy single-path fields (for backward compatibility)
    private readonly string _cachePath;
    private readonly string _logPath;

    // Lock for thread safety during Rust binary execution
    private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);
    private DateTime? _lastLogWarningTime; // Track last time we logged a warning
    private readonly TimeSpan _logWarningThrottle = TimeSpan.FromMinutes(5); // Only log warnings every 5 minutes
    
    // Cache the configured cache size to avoid repeated Docker API calls
    private long? _cachedConfiguredCacheSize;
    private DateTime _configuredCacheSizeLastChecked = DateTime.MinValue;
    private readonly TimeSpan _configuredCacheSizeCacheTime = TimeSpan.FromMinutes(5);
    private bool _hasLoggedConfiguredCacheSize = false;

    public CacheManagementService(
        IConfiguration configuration,
        ILogger<CacheManagementService> logger,
        IPathResolver pathResolver,
        ProcessManager processManager,
        RustProcessHelper rustProcessHelper,
        NginxLogRotationService nginxLogRotationService,
        ISignalRNotificationService notifications,
        DatasourceService datasourceService,
        IDbContextFactory<AppDbContext> dbContextFactory)
    {
        _configuration = configuration;
        _logger = logger;
        _pathResolver = pathResolver;
        _processManager = processManager;
        _rustProcessHelper = rustProcessHelper;
        _nginxLogRotationService = nginxLogRotationService;
        _notifications = notifications;
        _datasourceService = datasourceService;
        _dbContextFactory = dbContextFactory;

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

            // Also ensure logs directory exists
            if (!Directory.Exists(ds.LogPath))
            {
                try
                {
                    Directory.CreateDirectory(ds.LogPath);
                    _logger.LogInformation("Created logs directory for datasource '{Name}': {Path}", ds.Name, ds.LogPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create logs directory for datasource '{Name}': {Path}", ds.Name, ds.LogPath);
                }
            }
        }

        // Initialize Docker client for reading container configuration
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                var dockerUri = new Uri("unix:///var/run/docker.sock");
                if (File.Exists("/var/run/docker.sock"))
                {
                    _dockerClient = new DockerClientConfiguration(dockerUri).CreateClient();
                    _logger.LogDebug("Docker client initialized for reading lancache container configuration");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Docker client not available - will fall back to .env file for cache size configuration");
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
                info.DriveCapacity = driveInfo.TotalSize;
                info.FreeCacheSize = driveInfo.AvailableFreeSpace;
                
                // Try to read configured cache size from lancache .env file
                info.ConfiguredCacheSize = GetConfiguredCacheSize();
                
                // Use configured size if available, otherwise use drive capacity
                info.TotalCacheSize = info.ConfiguredCacheSize > 0 
                    ? info.ConfiguredCacheSize 
                    : info.DriveCapacity;
                
                // Calculate used space - this is always based on actual drive usage
                info.UsedCacheSize = info.DriveCapacity - info.FreeCacheSize;
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
    
    /// <summary>
    /// Gets the configured cache size, first from Docker container environment, 
    /// then falling back to .env file. Caches result for 5 minutes.
    /// Supports formats like "4000g", "500G", "2t", "1.5T", etc.
    /// </summary>
    private long GetConfiguredCacheSize()
    {
        // Return cached value if still valid
        if (_cachedConfiguredCacheSize.HasValue && 
            DateTime.UtcNow - _configuredCacheSizeLastChecked < _configuredCacheSizeCacheTime)
        {
            return _cachedConfiguredCacheSize.Value;
        }
        
        long configuredSize = 0;
        
        // Method 1: Try to read from Docker container environment
        configuredSize = GetConfiguredCacheSizeFromDocker();
        
        // Method 2: Fall back to .env file
        if (configuredSize == 0)
        {
            configuredSize = GetConfiguredCacheSizeFromEnvFile();
        }
        
        // Cache the result
        _cachedConfiguredCacheSize = configuredSize;
        _configuredCacheSizeLastChecked = DateTime.UtcNow;
        
        return configuredSize;
    }
    
    /// <summary>
    /// Reads CACHE_DISK_SIZE from the lancache-monolithic container environment variables.
    /// </summary>
    private long GetConfiguredCacheSizeFromDocker()
    {
        if (_dockerClient == null)
            return 0;
            
        try
        {
            // Get running containers
            var containers = _dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters { All = false }).GetAwaiter().GetResult();
            
            // Priority 1: Find by lancachenet/monolithic image (most reliable)
            var lancacheContainer = containers.FirstOrDefault(c =>
                c.Image?.Contains("lancachenet/monolithic", StringComparison.OrdinalIgnoreCase) ?? false);
            
            // Priority 2: Find by "monolithic" in container name
            lancacheContainer ??= containers.FirstOrDefault(c =>
                c.Names.Any(n => n.Contains("monolithic", StringComparison.OrdinalIgnoreCase)));
            
            // Priority 3: Scan all containers with "lancache" in name (but not dns/sniproxy)
            // and find one that has CACHE_DISK_SIZE set
            if (lancacheContainer == null)
            {
                var lancacheContainers = containers.Where(c =>
                    c.Names.Any(n => 
                        n.Contains("lancache", StringComparison.OrdinalIgnoreCase) &&
                        !n.Contains("dns", StringComparison.OrdinalIgnoreCase) &&
                        !n.Contains("sniproxy", StringComparison.OrdinalIgnoreCase)));
                
                foreach (var container in lancacheContainers)
                {
                    var inspect = _dockerClient.Containers.InspectContainerAsync(container.ID).GetAwaiter().GetResult();
                    var envVar = inspect.Config.Env?
                        .FirstOrDefault(e => e.StartsWith("CACHE_DISK_SIZE=", StringComparison.OrdinalIgnoreCase));
                    
                    if (!string.IsNullOrEmpty(envVar))
                    {
                        lancacheContainer = container;
                        break;
                    }
                }
            }
            
            if (lancacheContainer != null)
            {
                // Inspect the container to get environment variables
                var inspect = _dockerClient.Containers.InspectContainerAsync(lancacheContainer.ID).GetAwaiter().GetResult();
                
                var cacheDiskSizeEnv = inspect.Config.Env?
                    .FirstOrDefault(e => e.StartsWith("CACHE_DISK_SIZE=", StringComparison.OrdinalIgnoreCase));
                
                if (!string.IsNullOrEmpty(cacheDiskSizeEnv))
                {
                    var value = cacheDiskSizeEnv.Substring("CACHE_DISK_SIZE=".Length);
                    var parsedSize = ParseCacheSize(value);
                    if (parsedSize > 0)
                    {
                        if (!_hasLoggedConfiguredCacheSize)
                        {
                            _logger.LogInformation("Configured cache size: {Value} ({FormattedSize}) from container {ContainerName}",
                                value, FormatBytes(parsedSize), lancacheContainer.Names.FirstOrDefault()?.TrimStart('/'));
                            _hasLoggedConfiguredCacheSize = true;
                        }
                        return parsedSize;
                    }
                }
                else
                {
                    _logger.LogDebug("CACHE_DISK_SIZE not set in lancache container environment");
                }
            }
            else
            {
                _logger.LogDebug("No lancache-monolithic container found");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error reading CACHE_DISK_SIZE from Docker container");
        }
        
        return 0;
    }
    
    /// <summary>
    /// Reads the CACHE_DISK_SIZE setting from the lancache .env file.
    /// </summary>
    private long GetConfiguredCacheSizeFromEnvFile()
    {
        try
        {
            // Check for configured .env file path, or use common locations
            var envFilePath = _configuration["LanCache:EnvFilePath"];
            
            if (string.IsNullOrEmpty(envFilePath))
            {
                // Try common locations relative to cache path
                var possiblePaths = new[]
                {
                    "/srv/lancache/.env",
                    "/opt/lancache/.env", 
                    "/lancache/.env",
                    Path.Combine(Path.GetDirectoryName(_cachePath) ?? "", ".env"),
                    Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(_cachePath) ?? "") ?? "", ".env")
                };
                
                foreach (var path in possiblePaths)
                {
                    if (File.Exists(path))
                    {
                        envFilePath = path;
                        _logger.LogDebug("Found lancache .env file at: {Path}", path);
                        break;
                    }
                }
            }
            
            if (string.IsNullOrEmpty(envFilePath) || !File.Exists(envFilePath))
            {
                _logger.LogDebug("No lancache .env file found");
                return 0;
            }
            
            // Read and parse the .env file
            var lines = File.ReadAllLines(envFilePath);
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (trimmedLine.StartsWith("CACHE_DISK_SIZE=", StringComparison.OrdinalIgnoreCase))
                {
                    var value = trimmedLine.Substring("CACHE_DISK_SIZE=".Length).Trim();
                    var parsedSize = ParseCacheSize(value);
                    if (parsedSize > 0)
                    {
                        if (!_hasLoggedConfiguredCacheSize)
                        {
                            _logger.LogInformation("Configured cache size: {Value} ({FormattedSize}) from .env file: {Path}",
                                value, FormatBytes(parsedSize), envFilePath);
                            _hasLoggedConfiguredCacheSize = true;
                        }
                        return parsedSize;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading configured cache size from .env file");
        }
        
        return 0;
    }
    
    /// <summary>
    /// Parses cache size strings like "4000g", "500G", "2t", "1.5T", "500m", etc.
    /// Returns size in bytes.
    /// </summary>
    private long ParseCacheSize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;
            
        value = value.Trim().ToLowerInvariant();
        
        // Remove any quotes
        value = value.Trim('"', '\'');
        
        // Try to parse the numeric part and unit
        var numericPart = "";
        var unit = "";
        
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsDigit(value[i]) || value[i] == '.')
            {
                numericPart += value[i];
            }
            else
            {
                unit = value.Substring(i).Trim();
                break;
            }
        }
        
        // Use InvariantCulture to parse decimal values like "1.5T" correctly
        // regardless of the system's locale (e.g., German locales use comma as decimal separator)
        if (!double.TryParse(numericPart, System.Globalization.NumberStyles.Float, 
            System.Globalization.CultureInfo.InvariantCulture, out var numericValue))
        {
            _logger.LogWarning("Could not parse cache size value: {Value}", value);
            return 0;
        }
        
        // Convert to bytes based on unit
        return unit switch
        {
            "t" or "tb" => (long)(numericValue * 1024L * 1024L * 1024L * 1024L),
            "g" or "gb" => (long)(numericValue * 1024L * 1024L * 1024L),
            "m" or "mb" => (long)(numericValue * 1024L * 1024L),
            "k" or "kb" => (long)(numericValue * 1024L),
            "" or "b" => (long)numericValue,
            _ => (long)numericValue // Assume bytes if unknown unit
        };
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
    /// Get detailed corruption information for a specific service
    /// </summary>
    public async Task<List<CorruptedChunkDetail>> GetCorruptionDetails(string service, bool forceRefresh = false, int threshold = 3, bool compareToCacheLogs = true, CancellationToken cancellationToken = default)
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
                var noCacheCheckFlag = !compareToCacheLogs ? " --no-cache-check" : "";
                var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                    rustBinaryPath,
                    $"detect \"{logDir}\" \"{cacheDir}\" \"{outputJson}\" \"{timezone}\" {threshold}{noCacheCheckFlag}");

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

    // Helper class for deserializing game removal progress data from Rust
    private class GameRemovalProgressData
    {
        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("percentComplete")]
        public double PercentComplete { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("filesProcessed")]
        public int FilesProcessed { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("totalFiles")]
        public int TotalFiles { get; set; }
    }

    // Helper class for deserializing service removal progress data from Rust
    private class ServiceRemovalProgressData
    {
        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("percentComplete")]
        public double PercentComplete { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("filesProcessed")]
        public int FilesProcessed { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("totalFiles")]
        public int TotalFiles { get; set; }
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
    /// Remove all cache files for a specific game across all datasources
    /// </summary>
    public async Task<GameCacheRemovalReport> RemoveGameFromCache(uint gameAppId, CancellationToken cancellationToken = default, Func<double, string, int, long, Task>? onProgress = null)
    {
        await _cacheLock.WaitAsync();
        try
        {
            _logger.LogInformation("[GameRemoval] Starting game cache removal for AppID {AppId}", gameAppId);

            var operationsDir = _pathResolver.GetOperationsDirectory();
            var dbPath = _pathResolver.GetDatabasePath();
            var rustBinaryPath = _pathResolver.GetRustGameRemoverPath();

            _rustProcessHelper.ValidateRustBinaryExists(rustBinaryPath, "Game cache remover");

            if (!File.Exists(dbPath))
            {
                var errorMsg = $"Database not found at {dbPath}";
                _logger.LogError(errorMsg);
                throw new FileNotFoundException(errorMsg);
            }

            var datasources = _datasourceService.GetDatasources();
            var aggregatedReport = new GameCacheRemovalReport
            {
                GameAppId = gameAppId
            };

            int datasourcesProcessed = 0;
            int datasourcesSkipped = 0;

            foreach (ResolvedDatasource datasource in datasources)
            {
                string dsLogsDir = datasource.LogPath;
                string dsCachePath = datasource.CachePath;

                // Check if cache directory is writable for this datasource
                if (!_pathResolver.IsDirectoryWritable(dsCachePath))
                {
                    _logger.LogWarning(
                        "[GameRemoval] Skipping datasource '{DatasourceName}': cache directory '{CachePath}' is not writable",
                        datasource.Name, dsCachePath);
                    datasourcesSkipped++;
                    continue;
                }

                // Check if logs directory exists for this datasource
                if (!Directory.Exists(dsLogsDir))
                {
                    _logger.LogWarning(
                        "[GameRemoval] Skipping datasource '{DatasourceName}': logs directory not found at '{LogsDir}'",
                        datasource.Name, dsLogsDir);
                    datasourcesSkipped++;
                    continue;
                }

                // Check if cache directory exists for this datasource
                if (!Directory.Exists(dsCachePath))
                {
                    _logger.LogWarning(
                        "[GameRemoval] Skipping datasource '{DatasourceName}': cache directory not found at '{CachePath}'",
                        datasource.Name, dsCachePath);
                    datasourcesSkipped++;
                    continue;
                }

                var outputJson = Path.Combine(operationsDir,
                    $"game_removal_{gameAppId}_{datasource.Name}_{DateTime.UtcNow:yyyyMMddHHmmss}.json");
                var progressJson = Path.Combine(operationsDir,
                    $"game_removal_progress_{gameAppId}_{datasource.Name}_{DateTime.UtcNow:yyyyMMddHHmmss}.json");

                var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                    rustBinaryPath,
                    $"\"{dbPath}\" \"{dsLogsDir}\" \"{dsCachePath}\" {gameAppId} \"{outputJson}\" \"{progressJson}\"");

                _logger.LogInformation("[GameRemoval] Running removal for datasource '{DatasourceName}': {Binary} {Args}",
                    datasource.Name, rustBinaryPath, startInfo.Arguments);

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        throw new Exception($"Failed to start game_cache_remover process for datasource '{datasource.Name}'");
                    }

                    // Poll the progress file while the process runs
                    using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var pollTask = Task.Run(async () =>
                    {
                        while (!process.HasExited && !pollCts.Token.IsCancellationRequested)
                        {
                            await Task.Delay(500, pollCts.Token).ConfigureAwait(false);

                            try
                            {
                                var progressData = await _rustProcessHelper.ReadProgressFileAsync<GameRemovalProgressData>(progressJson);
                                if (progressData != null && onProgress != null)
                                {
                                    await onProgress(progressData.PercentComplete, progressData.Message, progressData.FilesProcessed, 0);
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                // Ignore transient read errors (file may be mid-write)
                                _logger.LogDebug("[GameRemoval] Progress file read error (transient): {Error}", ex.Message);
                            }
                        }
                    }, pollCts.Token);

                    var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                    var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                    await _processManager.WaitForProcessAsync(process, cancellationToken);

                    // Stop the polling task
                    pollCts.Cancel();
                    try { await pollTask; } catch (OperationCanceledException) { /* expected */ }

                    var output = await outputTask;
                    var error = await errorTask;

                    _logger.LogInformation("[GameRemoval] Process exit code for datasource '{DatasourceName}': {Code}",
                        datasource.Name, process.ExitCode);

                    // Log stdout (completion messages and summary)
                    if (!string.IsNullOrEmpty(output))
                    {
                        _logger.LogInformation("[GameRemoval] Process output for datasource '{DatasourceName}':\n{Output}",
                            datasource.Name, output);
                    }

                    // Log stderr (diagnostic messages)
                    if (!string.IsNullOrEmpty(error))
                    {
                        _logger.LogInformation("[GameRemoval] Process stderr for datasource '{DatasourceName}': {Error}",
                            datasource.Name, error);
                    }

                    if (process.ExitCode != 0)
                    {
                        _logger.LogError("[GameRemoval] Failed for datasource '{DatasourceName}' with exit code {Code}: {Error}",
                            datasource.Name, process.ExitCode, error);
                        throw new Exception($"game_cache_remover failed for datasource '{datasource.Name}' with exit code {process.ExitCode}: {error}");
                    }

                    // Read the generated JSON file (keep for operation history)
                    var dsReport = await _rustProcessHelper.ReadOutputJsonAsync<GameCacheRemovalReport>(outputJson, "GameRemoval");

                    // Send final progress update from the report
                    if (onProgress != null)
                    {
                        await onProgress(100, "Complete", dsReport.CacheFilesDeleted, (long)dsReport.TotalBytesFreed);
                    }

                    // Aggregate results from this datasource
                    aggregatedReport.CacheFilesDeleted += dsReport.CacheFilesDeleted;
                    aggregatedReport.TotalBytesFreed += dsReport.TotalBytesFreed;
                    aggregatedReport.EmptyDirsRemoved += dsReport.EmptyDirsRemoved;
                    aggregatedReport.LogEntriesRemoved += dsReport.LogEntriesRemoved;
                    if (!string.IsNullOrEmpty(dsReport.GameName))
                    {
                        aggregatedReport.GameName = dsReport.GameName;
                    }
                    foreach (uint depotId in dsReport.DepotIds)
                    {
                        if (!aggregatedReport.DepotIds.Contains(depotId))
                        {
                            aggregatedReport.DepotIds.Add(depotId);
                        }
                    }

                    datasourcesProcessed++;

                    _logger.LogInformation(
                        "[GameRemoval] Datasource '{DatasourceName}': removed {Files} files ({Bytes} bytes) for game {AppId}",
                        datasource.Name, dsReport.CacheFilesDeleted, dsReport.TotalBytesFreed, gameAppId);
                }
            }

            _logger.LogInformation(
                "[GameRemoval] Completed for AppID {AppId}: {Processed} datasource(s) processed, {Skipped} skipped. " +
                "Total: {Files} files removed, {Bytes} bytes freed",
                gameAppId, datasourcesProcessed, datasourcesSkipped,
                aggregatedReport.CacheFilesDeleted, aggregatedReport.TotalBytesFreed);

            // Remove this game from cached game detection results so page reload shows correct data
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
            await dbContext.CachedGameDetections
                .Where(CachedGameDetection => CachedGameDetection.GameAppId == gameAppId)
                .ExecuteDeleteAsync();
            _logger.LogInformation("[GameRemoval] Removed cached game detection entry for AppID: {AppId}", gameAppId);

            // Invalidate service counts cache since logs were modified
            await InvalidateServiceCountsCache();

            // Signal nginx to reopen log files (prevents monolithic container from losing log access)
            await _nginxLogRotationService.ReopenNginxLogsAsync();

            return aggregatedReport;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Remove all cache files for a specific service across all datasources
    /// </summary>
    public async Task<ServiceCacheRemovalReport> RemoveServiceFromCache(string serviceName, CancellationToken cancellationToken = default, Func<double, string, int, long, Task>? onProgress = null)
    {
        await _cacheLock.WaitAsync();
        try
        {
            _logger.LogInformation("[ServiceRemoval] Starting service cache removal for '{Service}'", serviceName);

            var operationsDir = _pathResolver.GetOperationsDirectory();
            var dbPath = _pathResolver.GetDatabasePath();
            var rustBinaryPath = _pathResolver.GetRustServiceRemoverPath();

            _rustProcessHelper.ValidateRustBinaryExists(rustBinaryPath, "Service remover");

            if (!File.Exists(dbPath))
            {
                var errorMsg = $"Database not found at {dbPath}";
                _logger.LogError(errorMsg);
                throw new FileNotFoundException(errorMsg);
            }

            var datasources = _datasourceService.GetDatasources();
            var aggregatedReport = new ServiceCacheRemovalReport
            {
                ServiceName = serviceName
            };

            int datasourcesProcessed = 0;
            int datasourcesSkipped = 0;

            foreach (ResolvedDatasource datasource in datasources)
            {
                string dsLogsDir = datasource.LogPath;
                string dsCachePath = datasource.CachePath;

                // Check if cache directory is writable for this datasource
                if (!_pathResolver.IsDirectoryWritable(dsCachePath))
                {
                    _logger.LogWarning(
                        "[ServiceRemoval] Skipping datasource '{DatasourceName}': cache directory '{CachePath}' is not writable",
                        datasource.Name, dsCachePath);
                    datasourcesSkipped++;
                    continue;
                }

                // Check if logs directory is writable for this datasource
                if (!_pathResolver.IsDirectoryWritable(dsLogsDir))
                {
                    _logger.LogWarning(
                        "[ServiceRemoval] Skipping datasource '{DatasourceName}': logs directory '{LogsDir}' is not writable",
                        datasource.Name, dsLogsDir);
                    datasourcesSkipped++;
                    continue;
                }

                // Check if logs directory exists for this datasource
                if (!Directory.Exists(dsLogsDir))
                {
                    _logger.LogWarning(
                        "[ServiceRemoval] Skipping datasource '{DatasourceName}': logs directory not found at '{LogsDir}'",
                        datasource.Name, dsLogsDir);
                    datasourcesSkipped++;
                    continue;
                }

                // Check if cache directory exists for this datasource
                if (!Directory.Exists(dsCachePath))
                {
                    _logger.LogWarning(
                        "[ServiceRemoval] Skipping datasource '{DatasourceName}': cache directory not found at '{CachePath}'",
                        datasource.Name, dsCachePath);
                    datasourcesSkipped++;
                    continue;
                }

                var outputJson = Path.Combine(operationsDir,
                    $"service_removal_output_{serviceName}_{datasource.Name}_{DateTime.UtcNow:yyyyMMddHHmmss}.json");
                var progressPath = Path.Combine(operationsDir,
                    $"service_removal_{serviceName}_{datasource.Name}_{DateTime.UtcNow:yyyyMMddHHmmss}.json");

                var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                    rustBinaryPath,
                    $"\"{dbPath}\" \"{dsLogsDir}\" \"{dsCachePath}\" \"{serviceName}\" \"{outputJson}\" \"{progressPath}\"");

                _logger.LogInformation("[ServiceRemoval] Running removal for datasource '{DatasourceName}': {Binary} {Args}",
                    datasource.Name, rustBinaryPath, startInfo.Arguments);

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        throw new Exception($"Failed to start service_remover process for datasource '{datasource.Name}'");
                    }

                    // Poll the progress file while the process runs
                    using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var pollTask = Task.Run(async () =>
                    {
                        while (!process.HasExited && !pollCts.Token.IsCancellationRequested)
                        {
                            await Task.Delay(500, pollCts.Token).ConfigureAwait(false);

                            try
                            {
                                var progressData = await _rustProcessHelper.ReadProgressFileAsync<ServiceRemovalProgressData>(progressPath);
                                if (progressData != null && onProgress != null)
                                {
                                    await onProgress(progressData.PercentComplete, progressData.Message, progressData.FilesProcessed, 0);
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                // Ignore transient read errors (file may be mid-write)
                                _logger.LogDebug("[ServiceRemoval] Progress file read error (transient): {Error}", ex.Message);
                            }
                        }
                    }, pollCts.Token);

                    var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                    var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                    await _processManager.WaitForProcessAsync(process, cancellationToken);

                    // Stop the polling task
                    pollCts.Cancel();
                    try { await pollTask; } catch (OperationCanceledException) { /* expected */ }

                    var output = await outputTask;
                    var error = await errorTask;

                    _logger.LogInformation("[ServiceRemoval] Process exit code for datasource '{DatasourceName}': {Code}",
                        datasource.Name, process.ExitCode);

                    // Log stdout (completion messages and summary)
                    if (!string.IsNullOrEmpty(output))
                    {
                        _logger.LogInformation("[ServiceRemoval] Process output for datasource '{DatasourceName}':\n{Output}",
                            datasource.Name, output);
                    }

                    // Log stderr (diagnostic messages)
                    if (!string.IsNullOrEmpty(error))
                    {
                        _logger.LogInformation("[ServiceRemoval] Process stderr for datasource '{DatasourceName}': {Error}",
                            datasource.Name, error);
                    }

                    if (process.ExitCode != 0)
                    {
                        _logger.LogError("[ServiceRemoval] Failed for datasource '{DatasourceName}' with exit code {Code}: {Error}",
                            datasource.Name, process.ExitCode, error);
                        throw new Exception($"service_remover failed for datasource '{datasource.Name}' with exit code {process.ExitCode}: {error}");
                    }

                    // Parse statistics from stderr output for this datasource
                    var dsReport = new ServiceCacheRemovalReport { ServiceName = serviceName };
                    if (!string.IsNullOrEmpty(error))
                    {
                        ExtractServiceRemovalStats(error, dsReport);
                    }

                    // Send final progress update from the report
                    if (onProgress != null)
                    {
                        await onProgress(100, "Complete", dsReport.CacheFilesDeleted, (long)dsReport.TotalBytesFreed);
                    }

                    // Aggregate results from this datasource
                    aggregatedReport.CacheFilesDeleted += dsReport.CacheFilesDeleted;
                    aggregatedReport.TotalBytesFreed += dsReport.TotalBytesFreed;
                    aggregatedReport.LogEntriesRemoved += dsReport.LogEntriesRemoved;
                    aggregatedReport.DatabaseEntriesDeleted += dsReport.DatabaseEntriesDeleted;

                    datasourcesProcessed++;

                    _logger.LogInformation(
                        "[ServiceRemoval] Datasource '{DatasourceName}': removed {Files} files ({Bytes} bytes) for service '{Service}'",
                        datasource.Name, dsReport.CacheFilesDeleted, dsReport.TotalBytesFreed, serviceName);

                    // Clean up progress file for this datasource
                    await _rustProcessHelper.DeleteTemporaryFileAsync(progressPath);
                }
            }

            _logger.LogInformation(
                "[ServiceRemoval] Completed for service '{Service}': {Processed} datasource(s) processed, {Skipped} skipped. " +
                "Total: {Files} files removed, {Bytes} bytes freed",
                serviceName, datasourcesProcessed, datasourcesSkipped,
                aggregatedReport.CacheFilesDeleted, aggregatedReport.TotalBytesFreed);

            // Remove this service from cached service detection results so page reload shows correct data
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
            await dbContext.CachedServiceDetections
                .Where(CachedServiceDetection => CachedServiceDetection.ServiceName == serviceName)
                .ExecuteDeleteAsync();
            _logger.LogInformation("[ServiceRemoval] Removed cached service detection entry for: {Service}", serviceName);

            // Invalidate service counts cache since logs were modified
            await InvalidateServiceCountsCache();

            // Signal nginx to reopen log files (prevents monolithic container from losing log access)
            await _nginxLogRotationService.ReopenNginxLogsAsync();

            return aggregatedReport;
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
