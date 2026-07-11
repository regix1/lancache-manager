using System.Diagnostics;
using Docker.DotNet;
using Docker.DotNet.Models;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using static LancacheManager.Infrastructure.Utilities.FormattingUtils;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;


namespace LancacheManager.Core.Services;

public partial class CacheManagementService
{
    private readonly ILogger<CacheManagementService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly NginxLogRotationService _nginxLogRotationService;
    private readonly DatasourceService _datasourceService;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly GameCacheDetectionService _gameCacheDetectionService;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly ISignalRNotificationService _notifications;
    private readonly ILancacheEnvFileReader _envFileReader;
    private readonly IOperationConflictChecker _conflictChecker;
    private readonly DockerClient? _dockerClient;

    // Legacy single-path fields (for backward compatibility)
    private readonly string _cachePath;

    // Lock for thread safety during Rust binary execution
    private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);

    // Dedicated lock for the cache_size Rust binary. The size scan is read-only over the
    // cache directories and never touches nginx log files, so it must NOT hold _cacheLock:
    // the scan runs for minutes (du/find + deletion-speed calibration) and holding the
    // shared lock that long silently starves every _cacheLock user (log removal via
    // ExecuteWithLockAsync, log counts, corruption details, db reset) even though the
    // conflict matrix deliberately ALLOWS log ops to run alongside the scan. Overlap with
    // cache-MUTATING ops is prevented by OperationConflictChecker for tracked scans; the
    // read-only walker tolerates concurrently-deleted files (failed-entry counters).
    private readonly SemaphoreSlim _sizeScanProcessLock = new SemaphoreSlim(1, 1);
    private DateTime? _lastLogWarningTime; // Track last time we logged a warning
    private readonly TimeSpan _logWarningThrottle = TimeSpan.FromMinutes(5); // Only log warnings every 5 minutes
    
    // Cache the configured cache size to avoid repeated Docker API calls
    private long? _cachedConfiguredCacheSize;
    private DateTime _configuredCacheSizeLastChecked = DateTime.MinValue;
    private readonly TimeSpan _configuredCacheSizeCacheTime = TimeSpan.FromMinutes(5);
    private bool _hasLoggedConfiguredCacheSize = false;

    // Cache the Rust binary scan result to avoid re-scanning on every page visit
    private CachedCacheScan? _cachedCacheScan;
    private readonly string _cachedScanFilePath;
    private readonly SemaphoreSlim _scanCacheLock = new SemaphoreSlim(1, 1);

    /// <summary>
    /// Context dictionary of the most recent cache-file-scan progress tick (same shape as the
    /// CacheSizeScanProgress SignalR payload's Context). The unified tracker only stores the stage
    /// key string, so the recovery endpoint (GET /api/cache/size/scan/status) reads this to
    /// interpolate placeholder-bearing keys like signalr.cacheSizeScan.scanning after a page refresh.
    /// Only one tracked full scan runs at a time (_scanCacheLock serializes them).
    /// </summary>
    public Dictionary<string, object?>? CurrentCacheSizeScanProgressContext { get; private set; }

    public CacheManagementService(
        IConfiguration configuration,
        ILogger<CacheManagementService> logger,
        IPathResolver pathResolver,
        RustProcessHelper rustProcessHelper,
        NginxLogRotationService nginxLogRotationService,
        DatasourceService datasourceService,
        IDbContextFactory<AppDbContext> dbContextFactory,
        GameCacheDetectionService gameCacheDetectionService,
        IUnifiedOperationTracker operationTracker,
        ISignalRNotificationService notifications,
        ILancacheEnvFileReader envFileReader,
        IOperationConflictChecker conflictChecker)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _rustProcessHelper = rustProcessHelper;
        _nginxLogRotationService = nginxLogRotationService;
        _datasourceService = datasourceService;
        _dbContextFactory = dbContextFactory;
        _gameCacheDetectionService = gameCacheDetectionService;
        _operationTracker = operationTracker;
        _notifications = notifications;
        _envFileReader = envFileReader;
        _conflictChecker = conflictChecker;

        // Use DatasourceService for paths (with backward compatibility)
        var defaultDatasource = _datasourceService.GetDefaultDatasource();
        if (defaultDatasource != null)
        {
            _cachePath = defaultDatasource.CachePath;
        }
        else
        {
            // Fallback to legacy configuration
            var configCachePath = configuration["LanCache:CachePath"];
            _cachePath = !string.IsNullOrEmpty(configCachePath)
                ? _pathResolver.ResolvePath(configCachePath)
                : _pathResolver.GetCacheDirectory();
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
            if (!OperatingSystemDetector.IsWindows)
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

        _cachedScanFilePath = Path.Combine(_pathResolver.GetStateDirectory(), "cached_cache_scan.json");

        _logger.LogInformation("CacheManagementService initialized with {Count} datasource(s)", _datasourceService.DatasourceCount);
    }

    public async Task<CacheInfo> GetCacheInfoAsync()
    {
        var info = new CacheInfo();

        try
        {
            // For Windows development, skip drive info
            if (OperatingSystemDetector.IsWindows)
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
                info.ConfiguredCacheSize = await GetConfiguredCacheSizeAsync();

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

            await ApplyCachedScanStatsAsync(info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache info");
        }

        return info;
    }

    private async Task ApplyCachedScanStatsAsync(CacheInfo info)
    {
        await LoadCachedScanAsync();

        var cachedScan = _cachedCacheScan?.ScanResult;
        if (cachedScan == null)
        {
            return;
        }

        info.TotalFiles = cachedScan.TotalFiles;
        info.CacheScanTotalBytes = cachedScan.TotalBytes;
        info.CacheScanTimestampUtc = _cachedCacheScan!.ScannedAtUtc;
        await ApplyScanMayBeStaleAsync(info);
    }

    /// <summary>
    /// Sets <see cref="CacheInfo.ScanMayBeStale"/> using live usage and the cached file-scan baseline.
    /// </summary>
    public async Task ApplyScanMayBeStaleAsync(CacheInfo info)
    {
        await LoadCachedScanAsync();
        info.ScanMayBeStale = CacheScanStaleCalculator.IsAnyScanStale(
            info.UsedCacheSize,
            _cachedCacheScan?.UsedCacheSizeAtScan);
    }
    
    /// <summary>
    /// Gets the configured cache size, first from Docker container environment, 
    /// then falling back to .env file. Caches result for 5 minutes.
    /// Supports formats like "4000g", "500G", "2t", "1.5T", etc.
    /// </summary>
    private async Task<long> GetConfiguredCacheSizeAsync()
    {
        // Return cached value if still valid
        if (_cachedConfiguredCacheSize.HasValue &&
            DateTime.UtcNow - _configuredCacheSizeLastChecked < _configuredCacheSizeCacheTime)
        {
            return _cachedConfiguredCacheSize.Value;
        }

        long configuredSize = 0;

        // Method 1: Try to read from Docker container environment
        configuredSize = await ReadCacheSizeFromDockerAsync();

        // Method 2: Fall back to .env file
        if (configuredSize == 0)
        {
            configuredSize = ReadCacheSizeFromEnvFile();
        }

        // Cache the result
        _cachedConfiguredCacheSize = configuredSize;
        _configuredCacheSizeLastChecked = DateTime.UtcNow;

        return configuredSize;
    }
    
    /// <summary>
    /// Reads CACHE_DISK_SIZE from the lancache-monolithic container environment variables.
    /// </summary>
    private async Task<long> ReadCacheSizeFromDockerAsync()
    {
        if (_dockerClient == null)
            return 0;

        try
        {
            // Get running containers
            var containers = await _dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters { All = false });

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
                    var inspect = await _dockerClient.Containers.InspectContainerAsync(container.ID);
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
                var inspect = await _dockerClient.Containers.InspectContainerAsync(lancacheContainer.ID);

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
    private long ReadCacheSizeFromEnvFile()
    {
        try
        {
            // Discovery + parse now live in the shared reader (also used by the Status Check
            // feature for CACHE_DOMAINS_REPO/BRANCH/NOFETCH) so there is one copy of the path list.
            var value = _envFileReader.TryGetValue("CACHE_DISK_SIZE");
            if (string.IsNullOrEmpty(value))
            {
                _logger.LogDebug("No lancache .env file found, or it has no CACHE_DISK_SIZE entry");
                return 0;
            }

            var parsedSize = ParseCacheSize(value);
            if (parsedSize > 0)
            {
                if (!_hasLoggedConfiguredCacheSize)
                {
                    _logger.LogInformation("Configured cache size: {Value} ({FormattedSize}) from .env file: {Path}",
                        value, FormatBytes(parsedSize), _envFileReader.ResolvedPath);
                    _hasLoggedConfiguredCacheSize = true;
                }
                return parsedSize;
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
    public async Task InvalidateServiceCountsAsync()
    {
        // Delete the Rust cache files (global + per-datasource) to force rescan
        var operationsDir = _pathResolver.GetOperationsDirectory();
        var progressFile = Path.Combine(operationsDir, "log_count_progress.json");

        await Task.Run(() =>
        {
            if (File.Exists(progressFile))
            {
                File.Delete(progressFile);
            }

            if (Directory.Exists(operationsDir))
            {
                foreach (var datasourceProgressFile in Directory.GetFiles(operationsDir, "log_count_progress_*.json"))
                {
                    File.Delete(datasourceProgressFile);
                }
            }
        });

        // Single choke point: every writer that changes log contents lands here, so this one
        // broadcast keeps the Log Removal panel counts live for all of them.
        await _notifications.NotifyAllAsync(SignalREvents.ServiceCountsChanged);
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

    public async Task<Dictionary<string, long>> GetServiceLogCountsAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        // Use semaphore to ensure only one Rust process runs at a time
        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            var aggregatedCounts = new Dictionary<string, long>();
            var datasources = _datasourceService.GetDatasources();

            // Process each datasource and aggregate counts
            foreach (var datasource in datasources)
            {
                var dsCounts = await GetLogCountsForDatasourceAsync(datasource.Name, datasource.LogPath, forceRefresh, cancellationToken);

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
    private async Task<Dictionary<string, long>> GetLogCountsForDatasourceAsync(string datasourceName, string logDir, bool forceRefresh, CancellationToken cancellationToken)
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
            _rustProcessHelper.EnsureBinaryExists(rustBinaryPath, "Rust log_manager");

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

            var result = await _rustProcessHelper.ExecuteProcessAsync(startInfo, cancellationToken);

            result.EnsureSuccess("log_manager", datasourceName);

            // Read results from progress file
            var progressData = await _rustProcessHelper.ReadProgressFileAsync<LogCountProgressData>(progressFile);

            if (progressData?.ServiceCounts != null)
            {
                counts = progressData.ServiceCounts.ToDictionary(kvp => kvp.Key, kvp => (long)kvp.Value);
            }
        }
        catch (RustProcessException ex) when (
            (ex.Stderr?.Contains("No such file or directory") == true) ||
            (ex.Stderr?.Contains("os error 2") == true))
        {
            // If the log file doesn't exist, return empty counts instead of throwing
            LogThrottledWarning($"Log file not accessible for datasource '{datasourceName}': {logDir}. Returning empty counts.");
            return counts;
        }
        catch (Exception ex)
        {
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
    /// Get detailed corruption information for a specific service
    /// </summary>
    public async Task<List<CorruptedChunkDetail>> GetCorruptionDetailsAsync(
        string service,
        bool forceRefresh,
        int threshold,
        bool compareToCacheLogs,
        Guid operationId,
        CancellationToken cancellationToken,
        bool detectRedownloads = false)
    {
        // Use semaphore to ensure only one Rust process runs at a time
        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation("[CorruptionDetection] GetCorruptionDetails for service: {Service}, forceRefresh: {ForceRefresh}, detectRedownloads: {DetectRedownloads}",
                service, forceRefresh, detectRedownloads);

            var logDir = _pathResolver.GetLogsDirectory();
            var cacheDir = _cachePath;
            var operationsDir = _pathResolver.GetOperationsDirectory();
            var timezone = Environment.GetEnvironmentVariable("TZ") ?? "UTC";
            var outputJson = Path.Combine(operationsDir, $"corruption_details_{service}_{DateTime.UtcNow:yyyyMMddHHmmss}.json");
            var progressFile = Path.Combine(operationsDir, $"corruption_details_progress_{operationId}.json");

            var rustBinaryPath = _pathResolver.GetRustCorruptionManagerPath();

            _rustProcessHelper.EnsureBinaryExists(rustBinaryPath, "Corruption manager");

            try
            {
                var noCacheCheckFlag = !compareToCacheLogs ? " --no-cache-check" : "";
                var redownloadFlag = detectRedownloads ? " --detect-redownloads" : "";
                var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                    rustBinaryPath,
                    $"detect \"{logDir}\" \"{cacheDir}\" \"{outputJson}\" \"{timezone}\" {threshold}{noCacheCheckFlag}{redownloadFlag} --progress-json \"{progressFile}\" --progress");

                _logger.LogInformation("[CorruptionDetection] Running detect command: {Command} {Args}",
                    rustBinaryPath, startInfo.Arguments);

                var lastReportedPercent = -1.0;
                var result = await _rustProcessHelper.ExecuteTrackedProcessWithProgressAsync<CorruptionDetectionProgressData>(
                    startInfo,
                    operationId,
                    cancellationToken,
                    progressFile,
                    async progressData =>
                    {
                        var percentChanged = Math.Abs(progressData.PercentComplete - lastReportedPercent) >= 5.0;
                        if (!percentChanged && progressData.PercentComplete < 100.0)
                        {
                            return;
                        }

                        lastReportedPercent = progressData.PercentComplete;
                        _operationTracker.UpdateProgress(operationId, progressData.PercentComplete, progressData.Status);

                        await _notifications.NotifyAllAsync(SignalREvents.CorruptionDetailsProgress, new CorruptionDetailsProgress(
                            OperationId: operationId,
                            Service: service,
                            PercentComplete: progressData.PercentComplete,
                            FilesProcessed: progressData.FilesProcessed,
                            TotalFiles: progressData.TotalFiles));
                    },
                    "corruption_manager_detect");

                _logger.LogInformation("[CorruptionDetection] Detect process exit code: {Code}", result.ExitCode);

                if (result.ExitCode != 0)
                {
                    _logger.LogError("[CorruptionDetection] Detect failed with exit code {Code}: {Error}",
                        result.ExitCode, result.Error);
                }

                result.EnsureSuccess("corruption_manager_detect", service);

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

        // Rust writes `stageKey` (i18n translation key) and `context` (a JSON object of
        // substitution vars) on every progress tick. Frontend registry uses these to
        // render per-phase labels. Before these properties existed the values were
        // silently dropped during deserialization and the frontend fell through to a
        // single generic default message for the whole removal.
        [System.Text.Json.Serialization.JsonPropertyName("stageKey")]
        public string StageKey { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("context")]
        public Dictionary<string, object?>? Context { get; set; }

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

        // See GameRemovalProgressData - same stageKey/context passthrough for service removals.
        [System.Text.Json.Serialization.JsonPropertyName("stageKey")]
        public string StageKey { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("context")]
        public Dictionary<string, object?>? Context { get; set; }

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
        public long GameAppId { get; set; }

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
        public List<long> DepotIds { get; set; } = new List<long>();
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

    private sealed record RemovalDatasourceContext(
        ResolvedDatasource Datasource,
        int ExecutionIndex,
        int TotalConfiguredDatasources,
        string OutputJsonPath,
        string ProgressJsonPath);

    private sealed record RemovalExecutionPlan(
        IReadOnlyList<RemovalDatasourceContext> RunnableDatasources,
        int DatasourcesSkipped);

    private sealed record RustRemovalProcessResult(
        ResolvedDatasource Datasource,
        string OutputJsonPath,
        string ProgressJsonPath,
        string StdOut,
        string StdErr);

    private RemovalExecutionPlan PrepareRemovalExecutionPlan(
        string logPrefix,
        string rustBinaryPath,
        string binaryDescription,
        string outputPrefix,
        string progressPrefix,
        string entityToken,
        bool requireWritableLogs)
    {
        _rustProcessHelper.EnsureBinaryExists(rustBinaryPath, binaryDescription);

        var operationsDir = _pathResolver.GetOperationsDirectory();
        Directory.CreateDirectory(operationsDir);

        var allDatasources = _datasourceService.GetDatasources().ToList();
        var runnableDatasources = new List<RemovalDatasourceContext>();
        var sanitizedEntityToken = SanitizeArtifactToken(entityToken);
        var datasourcesSkipped = 0;

        foreach (var datasource in allDatasources)
        {
            if (!CanRunRemovalOnDatasource(logPrefix, datasource, requireWritableLogs))
            {
                datasourcesSkipped++;
                continue;
            }

            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            runnableDatasources.Add(new RemovalDatasourceContext(
                datasource,
                runnableDatasources.Count,
                allDatasources.Count,
                Path.Combine(operationsDir, $"{outputPrefix}_{sanitizedEntityToken}_{datasource.Name}_{timestamp}.json"),
                Path.Combine(operationsDir, $"{progressPrefix}_{sanitizedEntityToken}_{datasource.Name}_{timestamp}.json")));
        }

        return new RemovalExecutionPlan(runnableDatasources, datasourcesSkipped);
    }

    private bool CanRunRemovalOnDatasource(
        string logPrefix,
        ResolvedDatasource datasource,
        bool requireWritableLogs)
    {
        if (!datasource.CacheWritable)
        {
            _logger.LogWarning(
                "{LogPrefix} Skipping datasource '{DatasourceName}': cache directory '{CachePath}' is not writable",
                logPrefix,
                datasource.Name,
                datasource.CachePath);
            return false;
        }

        if (requireWritableLogs && !datasource.LogsWritable)
        {
            _logger.LogWarning(
                "{LogPrefix} Skipping datasource '{DatasourceName}': logs directory '{LogsDir}' is not writable",
                logPrefix,
                datasource.Name,
                datasource.LogPath);
            return false;
        }

        if (!Directory.Exists(datasource.LogPath))
        {
            _logger.LogWarning(
                "{LogPrefix} Skipping datasource '{DatasourceName}': logs directory not found at '{LogsDir}'",
                logPrefix,
                datasource.Name,
                datasource.LogPath);
            return false;
        }

        if (!Directory.Exists(datasource.CachePath))
        {
            _logger.LogWarning(
                "{LogPrefix} Skipping datasource '{DatasourceName}': cache directory not found at '{CachePath}'",
                logPrefix,
                datasource.Name,
                datasource.CachePath);
            return false;
        }

        return true;
    }

    private static string SanitizeArtifactToken(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitizedChars = value
            .Select(ch => invalidChars.Contains(ch) || ch == ' ' || ch == '/' || ch == '\\' ? '_' : ch)
            .ToArray();

        return new string(sanitizedChars);
    }

    private async Task<TReport> RunRustRemovalProcessAsync<TProgress, TReport>(
        string logPrefix,
        RemovalDatasourceContext execution,
        ProcessStartInfo startInfo,
        string failedProcessDescription,
        CancellationToken cancellationToken,
        Guid? operationId,
        Func<TProgress, Task>? onProgress,
        Func<RustRemovalProcessResult, Task<TReport>> buildReportAsync)
        where TProgress : class
    {
        var result = await _rustProcessHelper.ExecuteTrackedProcessWithProgressEventsAsync(
            startInfo,
            operationId,
            cancellationToken,
            onProgress == null
                ? null
                : async _ =>
                {
                    // The stdout event is a zero-latency wake-up only; the removal binaries'
                    // progress-file DTOs are unchanged, so real data still comes from
                    // re-reading the same progress file each tick (mirrors
                    // CacheClearingService's hybrid). The Rust side writes the file before
                    // emitting the stdout event at every checkpoint, so this read is never stale.
                    var progressData = await _rustProcessHelper.ReadProgressFileAsync<TProgress>(execution.ProgressJsonPath);
                    if (progressData == null)
                    {
                        return;
                    }

                    await onProgress(progressData);
                },
            failedProcessDescription);

        _logger.LogInformation(
            "{LogPrefix} Process exit code for datasource '{DatasourceName}': {Code}",
            logPrefix,
            execution.Datasource.Name,
            result.ExitCode);

        if (!string.IsNullOrEmpty(result.Output))
        {
            _logger.LogInformation(
                "{LogPrefix} Process output for datasource '{DatasourceName}':\n{Output}",
                logPrefix,
                execution.Datasource.Name,
                result.Output);
        }

        if (!string.IsNullOrEmpty(result.Error))
        {
            _logger.LogInformation(
                "{LogPrefix} Process stderr for datasource '{DatasourceName}': {Error}",
                logPrefix,
                execution.Datasource.Name,
                result.Error);
        }

        if (result.ExitCode != 0)
        {
            _logger.LogError(
                "{LogPrefix} Failed for datasource '{DatasourceName}' with exit code {Code}: {Error}",
                logPrefix,
                execution.Datasource.Name,
                result.ExitCode,
                result.Error);
        }

        result.EnsureSuccess(failedProcessDescription, execution.Datasource.Name);

        return await buildReportAsync(new RustRemovalProcessResult(
            execution.Datasource,
            execution.OutputJsonPath,
            execution.ProgressJsonPath,
            result.Output,
            result.Error));
    }

    // -------------------------------------------------------------------------
    // Cache Scan Caching
    // -------------------------------------------------------------------------

    /// <summary>
    /// Loads the persisted cache scan from the JSON file into _cachedCacheScan.
    /// Safe to call multiple times - subsequent calls are no-ops if already loaded.
    /// </summary>
    private async Task LoadCachedScanAsync()
    {
        if (_cachedCacheScan != null)
            return;

        try
        {
            if (!File.Exists(_cachedScanFilePath))
                return;

            var json = await File.ReadAllTextAsync(_cachedScanFilePath);
            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            };
            _cachedCacheScan = System.Text.Json.JsonSerializer.Deserialize<CachedCacheScan>(json, options);
            if (_cachedCacheScan != null)
            {
                _logger.LogInformation("Loaded cached cache scan from disk (scanned {ScannedAt:u}, usedBytes={UsedBytes})",
                    _cachedCacheScan.ScannedAtUtc, _cachedCacheScan.UsedCacheSizeAtScan);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to load cached cache scan from {FilePath} - waiting for the next scheduled or manual scan",
                _cachedScanFilePath);
            _cachedCacheScan = null;
        }
    }

    /// <summary>
    /// Persists the scan result to the JSON file and updates the in-memory cache.
    /// </summary>
    private static void SyncScanTimestamp(CacheSizeResponse scanResult, DateTime scannedAtUtc)
    {
        scanResult.Timestamp = scannedAtUtc;
    }

    private static CacheSizeResponse CopyCacheSizeResponse(CacheSizeResponse source, bool isCached)
    {
        var deletionTimes = source.EstimatedDeletionTimes;
        return new CacheSizeResponse
        {
            TotalBytes = source.TotalBytes,
            TotalFiles = source.TotalFiles,
            TotalDirectories = source.TotalDirectories,
            HexDirectories = source.HexDirectories,
            ScanDurationMs = source.ScanDurationMs,
            FormattedSize = source.FormattedSize,
            Timestamp = source.Timestamp,
            IsCached = isCached,
            EstimatedDeletionTimes = new EstimatedDeletionTimes
            {
                PreserveSeconds = deletionTimes.PreserveSeconds,
                FullSeconds = deletionTimes.FullSeconds,
                RsyncSeconds = deletionTimes.RsyncSeconds,
                PreserveFormatted = deletionTimes.PreserveFormatted,
                FullFormatted = deletionTimes.FullFormatted,
                RsyncFormatted = deletionTimes.RsyncFormatted
            }
        };
    }

    private async Task SaveCachedScanAsync(CacheSizeResponse scanResult, long usedCacheSizeAtScan)
    {
        var scannedAtUtc = DateTime.UtcNow;
        SyncScanTimestamp(scanResult, scannedAtUtc);

        var entry = new CachedCacheScan
        {
            // Keep the persisted snapshot independent from the fresh result returned to the
            // scheduler/manual worker. Read callers mark their copy cached and must not race the
            // worker's IsCached=false success classification.
            ScanResult = CopyCacheSizeResponse(scanResult, isCached: false),
            UsedCacheSizeAtScan = usedCacheSizeAtScan,
            ScannedAtUtc = scannedAtUtc
        };

        try
        {
            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                WriteIndented = true
            };
            var json = System.Text.Json.JsonSerializer.Serialize(entry, options);
            var dir = Path.GetDirectoryName(_cachedScanFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(_cachedScanFilePath, json);
            _cachedCacheScan = entry;
            _logger.LogInformation("Saved cache scan result to {FilePath}", _cachedScanFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist cache scan to {FilePath}", _cachedScanFilePath);
            // Still update in-memory even if file write fails
            _cachedCacheScan = entry;
        }
    }

    /// <summary>
    /// Clears the in-memory cached scan and deletes the JSON file.
    /// Call this after cache-clear or removal operations so the next scheduled or manual
    /// refresh produces a new baseline. Ordinary read requests never launch a scan.
    /// </summary>
    public void InvalidateCachedScan()
    {
        _cachedCacheScan = null;
        try
        {
            if (File.Exists(_cachedScanFilePath))
            {
                File.Delete(_cachedScanFilePath);
                _logger.LogInformation("Invalidated cached cache scan (deleted {FilePath})", _cachedScanFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete cached cache scan file {FilePath}", _cachedScanFilePath);
        }
    }

    /// <summary>
    /// Runs the Rust cache-size binary against the given cache path and returns the parsed result,
    /// or null if the binary is not found or the scan fails.
    /// When <paramref name="operationId"/> is set the spawned process is associated with the
    /// tracked operation (universal cancel / force-kill) and, when <paramref name="onProgress"/>
    /// is provided, the binary's progress JSON (written to the output file during the scan and
    /// calibration phases) is polled and relayed to the callback.
    /// </summary>
    private async Task<CacheSizeResponse?> RunCacheSizeScanAsync(
        string cachePath,
        CancellationToken cancellationToken = default,
        Guid? operationId = null,
        Func<CacheSizeScanProgressData, Task>? onProgress = null)
    {
        var rustBinaryPath = _pathResolver.GetRustCacheSizePath();

        if (!File.Exists(rustBinaryPath))
        {
            _logger.LogError("Rust cache-size binary not found at {Path}", rustBinaryPath);
            return null;
        }

        if (!Directory.Exists(cachePath))
        {
            _logger.LogWarning("Cache path does not exist: {CachePath}", cachePath);
            return new CacheSizeResponse
            {
                TotalBytes = 0,
                TotalFiles = 0,
                TotalDirectories = 0,
                HexDirectories = 0,
                ScanDurationMs = 0,
                FormattedSize = FormatBytes(0),
                Timestamp = DateTime.UtcNow,
                EstimatedDeletionTimes = new EstimatedDeletionTimes
                {
                    PreserveSeconds = 0,
                    FullSeconds = 0,
                    RsyncSeconds = 0,
                    PreserveFormatted = "< 1 second",
                    FullFormatted = "< 1 second",
                    RsyncFormatted = "< 1 second"
                }
            };
        }

        // NOT _cacheLock (see _sizeScanProcessLock declaration): only one cache_size process
        // at a time, without starving the log-file ops that share _cacheLock for minutes.
        await _sizeScanProcessLock.WaitAsync(cancellationToken);
        try
        {
            var operationsDir = _pathResolver.GetOperationsDirectory();
            var outputFile = Path.Combine(operationsDir, $"cache_size_{Guid.NewGuid()}.json");

            var startInfo = _rustProcessHelper.CreateProcessStartInfo(rustBinaryPath, $"\"{cachePath}\" \"{outputFile}\" --progress");

            // The Rust binary writes ProgressData ticks to the output file while scanning and
            // calibrating, then overwrites it with the final CacheSizeResult. Progress ticks carry
            // a stageKey; the final result does not, so the relay callback can tell them apart.
            // Hybrid transport (mirrors CacheClearingService): cache_size.rs now emits a live stdout
            // progress event beside each file tick; each event is a zero-latency wake-up that
            // triggers exactly one read of the (Rust-side-unchanged) progress file, replacing the
            // old DefaultProgressPollMs file poll. cancellationToken is threaded through unchanged so
            // the P2-D shutdown-kill path still tears the process down.
            var processResult = await _rustProcessHelper.ExecuteTrackedProcessWithProgressEventsAsync(
                startInfo,
                operationId,
                cancellationToken,
                onProgress == null
                    ? null
                    : async _ =>
                    {
                        var progress = await _rustProcessHelper.ReadProgressFileAsync<CacheSizeScanProgressData>(outputFile);
                        if (progress != null)
                        {
                            await onProgress(progress);
                        }
                    },
                processLabel: "cache_size");

            // Cancel-vs-exit race: tracker cancel kills the process tree BEFORE cancelling the
            // CTS, so the child can die (exit 137 / SIGKILL) and WaitForExit return normally
            // before the token observes cancellation. Classify that as CANCELLED - never as a
            // "Failed to calculate cache size" error (mirrors the eviction scan's
            // ThrowIfCancellationRequested immediately after the Rust run).
            if (cancellationToken.IsCancellationRequested)
            {
                await _rustProcessHelper.DeleteTempFileAsync(outputFile);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!string.IsNullOrWhiteSpace(processResult.Error))
                _logger.LogInformation("Cache size calculation output:\n{Output}", processResult.Error);
            if (!string.IsNullOrWhiteSpace(processResult.Output))
                _logger.LogInformation("Cache size result JSON:\n{Json}", processResult.Output);

            if (processResult.ExitCode != 0)
            {
                _logger.LogError("Cache size calculation failed with exit code {ExitCode}: {Error}",
                    processResult.ExitCode, processResult.Error);
                await _rustProcessHelper.DeleteTempFileAsync(outputFile);
                return null;
            }

            var result = await _rustProcessHelper.ReadProgressFileAsync<CacheSizeResult>(outputFile);
            await _rustProcessHelper.DeleteTempFileAsync(outputFile);

            if (result == null)
            {
                _logger.LogError("Failed to read cache size result from {OutputFile}", outputFile);
                return null;
            }

            return new CacheSizeResponse
            {
                TotalBytes = (long)result.TotalBytes,
                TotalFiles = (long)result.TotalFiles,
                TotalDirectories = (long)result.TotalDirectories,
                HexDirectories = result.HexDirectories,
                ScanDurationMs = (long)result.ScanDurationMs,
                FormattedSize = result.FormattedSize,
                Timestamp = DateTime.UtcNow,
                EstimatedDeletionTimes = new EstimatedDeletionTimes
                {
                    PreserveSeconds = result.EstimatedDeletionTimes.PreserveSeconds,
                    FullSeconds = result.EstimatedDeletionTimes.FullSeconds,
                    RsyncSeconds = result.EstimatedDeletionTimes.RsyncSeconds,
                    PreserveFormatted = result.EstimatedDeletionTimes.PreserveFormatted,
                    FullFormatted = result.EstimatedDeletionTimes.FullFormatted,
                    RsyncFormatted = result.EstimatedDeletionTimes.RsyncFormatted
                }
            };
        }
        finally
        {
            _sizeScanProcessLock.Release();
        }
    }

    // Broadcast gate for RelayProgressAsync. Safe as instance fields: callers hold the scan
    // locks so at most one cache size scan relays progress at a time.
    private long _cacheSizeScanLastEmitTicks = long.MinValue;
    private string? _cacheSizeScanLastEmitStageKey;

    /// <summary>
    /// Relays one Rust progress tick to the tracker + SignalR. Ticks without a stageKey are
    /// skipped: the Rust binary overwrites the progress file with the final result JSON (no
    /// stageKey) when it finishes, and the poller may read that before it stops.
    /// </summary>
    private async Task RelayProgressAsync(Guid operationId, CacheSizeScanProgressData progress)
    {
        if (string.IsNullOrEmpty(progress.StageKey))
        {
            return;
        }

        var context = new Dictionary<string, object?>
        {
            ["directoriesScanned"] = progress.DirectoriesScanned,
            ["totalDirectories"] = progress.TotalDirectories,
            ["totalFiles"] = progress.TotalFiles,
            ["step"] = progress.CalibrationStep,
            ["totalSteps"] = progress.CalibrationTotalSteps
        };
        CurrentCacheSizeScanProgressContext = context;

        _operationTracker.UpdateProgress(operationId, progress.PercentComplete, progress.StageKey);

        // Gate the broadcast (tracker update above stays per-tick for recovery accuracy):
        // rust ticks can arrive many times per second and every emit re-renders every client.
        // Emit on stage change or at most every 250ms; CacheSizeScanComplete carries final state.
        var nowTicks = Environment.TickCount64;
        if (progress.StageKey == _cacheSizeScanLastEmitStageKey &&
            nowTicks - _cacheSizeScanLastEmitTicks < RustProcessHelper.ProgressEmitMinIntervalMs)
        {
            return;
        }
        _cacheSizeScanLastEmitStageKey = progress.StageKey;
        _cacheSizeScanLastEmitTicks = nowTicks;

        await _notifications.NotifyAllAsync(SignalREvents.CacheSizeScanProgress, new CacheSizeScanProgress(
            OperationId: operationId,
            Status: OperationStatus.Running.ToWireString(),
            StageKey: progress.StageKey,
            PercentComplete: progress.PercentComplete,
            DirectoriesScanned: progress.DirectoriesScanned,
            TotalDirectories: progress.TotalDirectories,
            TotalFiles: progress.TotalFiles,
            TotalBytes: progress.TotalBytes,
            Context: context));
    }

    /// <summary>
    /// Runs the full (all-datasources) cache size scan as a VISIBLE tracked operation:
    /// registers a CacheSizeScan operation with the unified tracker (operation registration
    /// happens BEFORE any Started/Progress emit), emits CacheSizeScanStarted/Progress/Complete
    /// SignalR events, and runs the Rust binary on the registered operation's token so the
    /// universal cancel path (/api/operations/{id}/cancel + /force-kill, stdin CANCEL) works.
    /// Deliberately non-silent: the running card explains why other heavy cache operations
    /// are blocked by <see cref="OperationConflictChecker"/> while the scan runs.
    /// Callers must hold _scanCacheLock so at most one tracked scan is registered at a time.
    /// </summary>
    private async Task<CacheSizeResponse?> RunFullScanAsync(
        string cachePath,
        CancellationToken callerToken,
        Action<Guid>? onScanStarted = null)
    {
        // Heavy data ops run one at a time (OperationConflictChecker section 1a). Both scan
        // entry points (the queued manual refresh and the scheduled service) funnel through
        // here before the Rust walker spawns, so this final guard also closes start races.
        // Returning null lets callers retain the last persisted result.
        var scanConflict = await _conflictChecker.CheckAsync(OperationType.CacheSizeScan, ConflictScope.Bulk(), callerToken);
        if (scanConflict != null)
        {
            _logger.LogInformation(
                "Cache size scan skipped: active {ActiveType} operation ({ActiveId}) holds the heavy-op slot",
                scanConflict.ActiveOperationType, scanConflict.ActiveOperationId);
            return null;
        }

        var terminalFiles = 0L;
        var terminalBytes = 0L;
        string? terminalFormattedSize = null;

        // CTS ownership: handed to the tracker, which disposes it in CompleteOperation.
        var cts = new CancellationTokenSource();
        Guid operationId = default;
        operationId = _operationTracker.RegisterOperation(
            OperationType.CacheSizeScan,
            "Cache File Scan",
            cts,
            onTerminalCleanup: () => CurrentCacheSizeScanProgressContext = null,
            onTerminalEmit: info =>
            {
                if (info.Cancelled)
                {
                    return _notifications.NotifyAllAsync(SignalREvents.CacheSizeScanComplete, new CacheSizeScanComplete(
                        Success: false,
                        OperationId: operationId,
                        StageKey: "signalr.cacheSizeScan.complete",
                        TotalFiles: 0,
                        TotalBytes: 0,
                        Error: "Cancelled by user"));
                }

                if (info.Success)
                {
                    return _notifications.NotifyAllAsync(SignalREvents.CacheSizeScanComplete, new CacheSizeScanComplete(
                        Success: true,
                        OperationId: operationId,
                        StageKey: "signalr.cacheSizeScan.complete",
                        TotalFiles: terminalFiles,
                        TotalBytes: terminalBytes,
                        FormattedSize: terminalFormattedSize,
                        Context: new Dictionary<string, object?>
                        {
                            ["totalFiles"] = terminalFiles,
                            ["totalSize"] = terminalFormattedSize ?? FormatBytes(terminalBytes)
                        }));
                }

                return _notifications.NotifyAllAsync(SignalREvents.CacheSizeScanComplete, new CacheSizeScanComplete(
                    Success: false,
                    OperationId: operationId,
                    StageKey: "signalr.cacheSizeScan.complete",
                    TotalFiles: 0,
                    TotalBytes: 0,
                    Error: info.Error ?? "Rust cache size binary returned failure"));
            });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, cts.Token);

        try
        {
            await _notifications.NotifyAllAsync(SignalREvents.CacheSizeScanStarted, new CacheSizeScanStarted(
                StageKey: "signalr.cacheSizeScan.starting",
                OperationId: operationId));
            // Info-level on purpose: NotifyAllAsync logs success only at Debug, so without this
            // line production logs cannot distinguish "Started was emitted but the browser runs a
            // stale bundle" from "Started was never emitted".
            _logger.LogInformation("[CacheSizeScan] Emitted CacheSizeScanStarted for operation {OperationId}", operationId);
            onScanStarted?.Invoke(operationId);

            var result = await RunCacheSizeScanAsync(
                cachePath,
                linked.Token,
                operationId,
                onProgress: progress => RelayProgressAsync(operationId, progress));

            linked.Token.ThrowIfCancellationRequested();

            if (result == null)
            {
                // Terminal CacheSizeScanComplete(error) is emitted by the registered onTerminalEmit closure.
                _operationTracker.CompleteOperation(operationId, success: false, error: "Cache size scan failed - see server logs");
                return null;
            }

            // Capture the totals BY VALUE before completing so the onTerminalEmit closure
            // builds the success payload from real metrics.
            terminalFiles = result.TotalFiles;
            terminalBytes = result.TotalBytes;
            terminalFormattedSize = result.FormattedSize;
            _operationTracker.CompleteOperation(operationId, success: true);
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[CacheSizeScan] Operation {OperationId} was cancelled", operationId);
            // Terminal CacheSizeScanComplete(cancelled) is emitted by the registered onTerminalEmit closure.
            _operationTracker.CompleteOperation(operationId, success: false, error: "Cancelled by user");
            if (callerToken.IsCancellationRequested)
            {
                throw; // preserve the pre-existing contract for host-shutdown / aborted callers
            }
            return null; // user-initiated cancel: surface "no result" instead of an unhandled OCE
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CacheSizeScan] Cache file scan failed");
            _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Starts an explicit full cache-size scan without tying its lifetime to the initiating HTTP
    /// request. Returns as soon as the tracked operation has emitted its Started event; the singleton
    /// service continues the scan, persists the result, and emits <see cref="SignalREvents.CacheScanComplete"/>
    /// after the cached result is ready for readers. Intended for <see cref="IOperationQueue"/> promotion.
    /// </summary>
    public Task<Guid?> StartCacheSizeScanInBackgroundAsync()
    {
        var started = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = RunCacheSizeScanInBackgroundAsync(started);
        return started.Task;
    }

    private async Task RunCacheSizeScanInBackgroundAsync(TaskCompletionSource<Guid?> started)
    {
        Guid? operationId = null;
        try
        {
            var result = await GetCacheSizeAsync(
                force: true,
                datasource: null,
                cancellationToken: CancellationToken.None,
                onScanStarted: id =>
                {
                    operationId = id;
                    started.TrySetResult(id);
                });

            // A last-moment conflict can still make the start path decline after the queue's
            // eligibility check. Returning null lets OperationQueue terminate the waiting card
            // as a failed promotion instead of leaving a ghost operation behind.
            started.TrySetResult(null);

            if (operationId.HasValue && result is { IsCached: false })
            {
                // GetCacheSizeAsync persists the fresh result before returning. This separate event
                // tells cache-data consumers it is now safe to fetch without racing the final save.
                await _notifications.NotifyAllAsync(
                    SignalREvents.CacheScanComplete,
                    new CacheScanComplete(Success: true));
            }
        }
        catch (Exception ex)
        {
            if (!started.TrySetException(ex))
            {
                // The operation already started, so no request/queue caller remains to observe a
                // later cache-persistence or notification failure.
                _logger.LogError(ex, "Background cache size scan failed after operation {OperationId} started", operationId);
            }
        }
    }

    /// <summary>
    /// Returns a cache size result, using the persisted server-side cache for ordinary reads.
    /// Per-datasource scans are always live (not cached).
    /// The full all-datasources scan runs only when <paramref name="force"/> is true. This keeps
    /// cache-size changes and page reads from launching an unqueued full-disk walk; automatic
    /// refreshes belong exclusively to <see cref="CacheSizeScanScheduledService"/>.
    /// </summary>
    public async Task<CacheSizeResponse?> GetCacheSizeAsync(
        bool force = false,
        string? datasource = null,
        CancellationToken cancellationToken = default,
        Action<Guid>? onScanStarted = null)
    {
        // Per-datasource scans are always live - no caching
        if (!string.IsNullOrEmpty(datasource))
        {
            var ds = _datasourceService.GetDatasources()
                .FirstOrDefault(d => d.Name.Equals(datasource, StringComparison.OrdinalIgnoreCase));
            if (ds == null)
                return null;
            return await RunCacheSizeScanAsync(ds.CachePath, cancellationToken);
        }

        // A normal read must stay cheap and must not wait behind a minutes-long active scan.
        // The existing persisted result remains valid as a historical snapshot; callers can
        // inspect ScanMayBeStale separately and the configured schedule refreshes the baseline.
        if (!force)
        {
            await LoadCachedScanAsync();
            var cachedResult = BuildStaleResult();
            if (cachedResult == null)
            {
                _logger.LogDebug("No cached cache-size scan is available; waiting for a scheduled or manual refresh");
            }
            return cachedResult;
        }

        var allCachePath = _pathResolver.GetCacheDirectory();

        await _scanCacheLock.WaitAsync(cancellationToken);
        try
        {
            // A forced scan is either a queued manual refresh or the scheduled service.
            _logger.LogInformation("Force rescan requested - running fresh cache size scan");
            var freshResult = await RunFullScanAsync(allCachePath, cancellationToken, onScanStarted);
            if (freshResult != null)
            {
                var cacheInfo = await GetCacheInfoAsync();
                await SaveCachedScanAsync(freshResult, cacheInfo.UsedCacheSize);
                freshResult.IsCached = false;
                return freshResult;
            }
            // Cancelled/failed force scan: fall back to the last good result (stale is
            // fine) so the dashboard keeps showing data instead of erroring.
            await LoadCachedScanAsync();
            return BuildStaleResult();
        }
        finally
        {
            _scanCacheLock.Release();
        }
    }

    /// <summary>
    /// Returns the last persisted cache scan as a stale (IsCached=true) result without
    /// triggering a scan, or null if none has ever been persisted. Used by the controller as
    /// the fallback when a fresh <see cref="GetCacheSizeAsync"/> call returns null and no scan
    /// is currently active - a previous result beats a 500.
    /// </summary>
    public async Task<CacheSizeResponse?> GetStaleCachedSizeResultAsync()
    {
        await LoadCachedScanAsync();
        return BuildStaleResult();
    }

    /// <summary>
    /// Returns the last good cached scan as a stale (IsCached=true) response, or null when no
    /// cached scan exists. Used when a fresh scan was cancelled or failed so dashboards keep
    /// showing the previous scan's data + timestamp (graceful staleness) instead of an error.
    /// Cancellation never deletes the cached scan file; only cache clear/removals invalidate it.
    /// </summary>
    private CacheSizeResponse? BuildStaleResult()
    {
        if (_cachedCacheScan == null)
        {
            return null;
        }

        // Return a copy: IsCached belongs to this response, not to the shared persisted object.
        var cachedResult = CopyCacheSizeResponse(_cachedCacheScan.ScanResult, isCached: true);
        SyncScanTimestamp(cachedResult, _cachedCacheScan.ScannedAtUtc);
        return cachedResult;
    }

    /// <summary>
    /// Progress tick written by the Rust cache_size binary to the output file while the scan
    /// and calibration phases run. The final result JSON has no stageKey, which is how the
    /// relay distinguishes ticks from the terminal payload.
    /// </summary>
    private class CacheSizeScanProgressData
    {
        [System.Text.Json.Serialization.JsonPropertyName("stageKey")]
        public string? StageKey { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("percentComplete")]
        public double PercentComplete { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("directoriesScanned")]
        public long DirectoriesScanned { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("totalDirectories")]
        public long TotalDirectories { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("totalBytes")]
        public long TotalBytes { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("totalFiles")]
        public long TotalFiles { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("calibrationStep")]
        public int CalibrationStep { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("calibrationTotalSteps")]
        public int CalibrationTotalSteps { get; set; }
    }

    // Helper class for deserializing the Rust cache-size binary output
    private class CacheSizeResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("totalBytes")]
        public ulong TotalBytes { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("totalFiles")]
        public ulong TotalFiles { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("totalDirectories")]
        public ulong TotalDirectories { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("hexDirectories")]
        public int HexDirectories { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("scanDurationMs")]
        public ulong ScanDurationMs { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("estimatedDeletionTimes")]
        public CacheSizeEstimates EstimatedDeletionTimes { get; set; } = new();

        [System.Text.Json.Serialization.JsonPropertyName("formattedSize")]
        public string FormattedSize { get; set; } = string.Empty;
    }

    private class CacheSizeEstimates
    {
        [System.Text.Json.Serialization.JsonPropertyName("preserveSeconds")]
        public double PreserveSeconds { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("fullSeconds")]
        public double FullSeconds { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("rsyncSeconds")]
        public double RsyncSeconds { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("preserveFormatted")]
        public string PreserveFormatted { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("fullFormatted")]
        public string FullFormatted { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("rsyncFormatted")]
        public string RsyncFormatted { get; set; } = string.Empty;
    }

    /// <summary>
    /// Persisted model for the cached Rust cache-size scan.
    /// </summary>
    public class CachedCacheScan
    {
        public CacheSizeResponse ScanResult { get; set; } = new();
        public long UsedCacheSizeAtScan { get; set; }
        public DateTime ScannedAtUtc { get; set; }
    }
}
