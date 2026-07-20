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
    private readonly DatasourceCapabilityService _capabilityService;
    private readonly IPathResolver _pathResolver;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly NginxLogRotationService _nginxLogRotationService;
    private readonly DatasourceService _datasourceService;
    private readonly IStateService _stateService;
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
    private IReadOnlyList<DatasourceCacheSizeResolution>? _cachedDatasourceCacheSizes;
    private DateTime _configuredCacheSizeLastChecked = DateTime.MinValue;
    private readonly TimeSpan _configuredCacheSizeCacheTime = TimeSpan.FromMinutes(5);
    private readonly object _configuredCacheSizeLock = new();
    private readonly SemaphoreSlim _configuredCacheSizeRefreshLock = new(1, 1);
    private long _configuredCacheSizeGeneration;
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

    /// <summary>
    /// Run-stable display flag for the active tracked cache-file scan. Lifecycle events are always
    /// emitted so recovery works, but a silent automatic scan leaves this false so the recovery
    /// endpoint (GET /api/cache/size/scan/status) can decline to resurrect a card on page reload
    /// instead of leaving it stuck once the silent terminal arrives. Null while no scan is running.
    /// </summary>
    public bool? CurrentCacheSizeScanShowNotification { get; private set; }

    public CacheManagementService(
        IConfiguration configuration,
        ILogger<CacheManagementService> logger,
        IPathResolver pathResolver,
        RustProcessHelper rustProcessHelper,
        NginxLogRotationService nginxLogRotationService,
        DatasourceService datasourceService,
        IStateService stateService,
        IDbContextFactory<AppDbContext> dbContextFactory,
        GameCacheDetectionService gameCacheDetectionService,
        IUnifiedOperationTracker operationTracker,
        ISignalRNotificationService notifications,
        ILancacheEnvFileReader envFileReader,
        IOperationConflictChecker conflictChecker,
        DatasourceCapabilityService capabilityService)
    {
        _capabilityService = capabilityService;
        _logger = logger;
        _pathResolver = pathResolver;
        _rustProcessHelper = rustProcessHelper;
        _nginxLogRotationService = nginxLogRotationService;
        _datasourceService = datasourceService;
        _stateService = stateService;
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
            // Windows development has no Linux cache mount, but persisted scan availability is
            // still applied below so zero files and no scan remain distinct.
            if (!OperatingSystemDetector.IsWindows)
            {
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
                    _logger.LogWarning("Mount point does not exist: {MountPoint}", mountPoint);
                }
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
        info.HasCacheScan = true;
        await ApplyScanMayBeStaleAsync(info);
    }

    /// <summary>
    /// Sets <see cref="CacheInfo.ScanMayBeStale"/> using live usage and the cached file-scan baseline.
    /// </summary>
    public async Task ApplyScanMayBeStaleAsync(CacheInfo info)
    {
        await LoadCachedScanAsync();

        var usedBytesByMountAtScan = _cachedCacheScan?.UsedCacheSizeByMountAtScan;
        if (usedBytesByMountAtScan is { Count: > 0 })
        {
            var currentUsedBytesByMount = ReadCacheMountUsage(usedBytesByMountAtScan.Keys);
            info.ScanMayBeStale = IsAnyMountUsageStale(
                usedBytesByMountAtScan,
                currentUsedBytesByMount);
            return;
        }

        info.ScanMayBeStale = CacheScanStaleCalculator.IsAnyScanStale(
            info.UsedCacheSize,
            _cachedCacheScan?.UsedCacheSizeAtScan);
    }
    
    /// <summary>
    /// Gets the sum of known configured limits across enabled datasources.
    /// Manual values take precedence over Docker and .env detection.
    /// </summary>
    private async Task<long> GetConfiguredCacheSizeAsync()
    {
        var resolutions = await GetDatasourceCacheSizeResolutionsAsync();
        var configuredSize = SumKnownConfiguredSizes(resolutions);
        if (configuredSize == 0)
        {
            return 0;
        }

        return AddFullDiskCapacities(resolutions, configuredSize);
    }

    /// <summary>
    /// Returns each enabled datasource's effective configured limit and its origin.
    /// </summary>
    public async Task<IReadOnlyList<DatasourceCacheSizeResolution>> GetDatasourceCacheSizeResolutionsAsync()
    {
        lock (_configuredCacheSizeLock)
        {
            if (_cachedConfiguredCacheSize.HasValue
                && _cachedDatasourceCacheSizes != null
                && DateTime.UtcNow - _configuredCacheSizeLastChecked < _configuredCacheSizeCacheTime)
            {
                return _cachedDatasourceCacheSizes;
            }
        }

        await _configuredCacheSizeRefreshLock.WaitAsync();
        try
        {
            lock (_configuredCacheSizeLock)
            {
                if (_cachedConfiguredCacheSize.HasValue
                    && _cachedDatasourceCacheSizes != null
                    && DateTime.UtcNow - _configuredCacheSizeLastChecked < _configuredCacheSizeCacheTime)
                {
                    return _cachedDatasourceCacheSizes;
                }
            }

            var generation = Volatile.Read(ref _configuredCacheSizeGeneration);
            var detectedBytes = await ReadCacheSizeFromDockerAsync();
            var detectedSource = CacheSizeSource.Docker;
            if (detectedBytes == 0)
            {
                detectedBytes = ReadCacheSizeFromEnvFile();
                detectedSource = detectedBytes > 0 ? CacheSizeSource.Env : CacheSizeSource.FullDisk;
            }

            var resolutions = ResolveDatasourceCacheSizes(
                _datasourceService.GetDatasources(),
                _stateService.GetDatasourceCacheSizeOverrides(),
                detectedBytes,
                detectedSource);
            var configuredSize = SumKnownConfiguredSizes(resolutions);

            lock (_configuredCacheSizeLock)
            {
                if (generation == Volatile.Read(ref _configuredCacheSizeGeneration))
                {
                    _cachedDatasourceCacheSizes = resolutions;
                    _cachedConfiguredCacheSize = configuredSize;
                    _configuredCacheSizeLastChecked = DateTime.UtcNow;
                }
            }

            return resolutions;
        }
        finally
        {
            _configuredCacheSizeRefreshLock.Release();
        }
    }

    /// <summary>
    /// Expires configured-size state after a datasource override changes.
    /// </summary>
    public void InvalidateConfiguredCacheSize()
    {
        lock (_configuredCacheSizeLock)
        {
            Interlocked.Increment(ref _configuredCacheSizeGeneration);
            _cachedConfiguredCacheSize = null;
            _cachedDatasourceCacheSizes = null;
            _configuredCacheSizeLastChecked = DateTime.MinValue;
        }
    }

    internal static IReadOnlyList<DatasourceCacheSizeResolution> ResolveDatasourceCacheSizes(
        IEnumerable<ResolvedDatasource> datasources,
        IReadOnlyDictionary<string, long> overrides,
        long detectedBytes,
        CacheSizeSource detectedSource)
    {
        var normalizedOverrides = new Dictionary<string, long>(overrides, StringComparer.OrdinalIgnoreCase);
        var resolutions = new List<DatasourceCacheSizeResolution>();

        foreach (var datasource in datasources.Where(datasource => datasource.Enabled))
        {
            if (normalizedOverrides.TryGetValue(datasource.Name, out var overrideBytes) && overrideBytes > 0)
            {
                resolutions.Add(new DatasourceCacheSizeResolution(
                    datasource.Name,
                    overrideBytes,
                    overrideBytes,
                    CacheSizeSource.Manual));
                continue;
            }

            if (detectedBytes > 0 && detectedSource is CacheSizeSource.Docker or CacheSizeSource.Env)
            {
                resolutions.Add(new DatasourceCacheSizeResolution(
                    datasource.Name,
                    null,
                    detectedBytes,
                    detectedSource));
                continue;
            }

            resolutions.Add(new DatasourceCacheSizeResolution(
                datasource.Name,
                null,
                0,
                CacheSizeSource.FullDisk));
        }

        return resolutions.AsReadOnly();
    }

    internal static long SumKnownConfiguredSizes(IEnumerable<DatasourceCacheSizeResolution> resolutions)
    {
        var manualTotal = 0L;
        // Docker/.env CACHE_DISK_SIZE is one global cache limit shared by every auto-detected
        // datasource, so it is counted ONCE. Summing it per datasource would report N x the limit.
        var autoDetected = 0L;
        foreach (var resolution in resolutions)
        {
            if (resolution.ResolvedBytes <= 0)
            {
                continue;
            }

            if (resolution.Source == CacheSizeSource.Manual)
            {
                if (resolution.ResolvedBytes > long.MaxValue - manualTotal)
                {
                    return long.MaxValue;
                }

                manualTotal += resolution.ResolvedBytes;
            }
            else if (resolution.Source is CacheSizeSource.Docker or CacheSizeSource.Env
                && resolution.ResolvedBytes > autoDetected)
            {
                autoDetected = resolution.ResolvedBytes;
            }
        }

        if (autoDetected > long.MaxValue - manualTotal)
        {
            return long.MaxValue;
        }

        return manualTotal + autoDetected;
    }

    private long AddFullDiskCapacities(
        IEnumerable<DatasourceCacheSizeResolution> resolutions,
        long configuredSize)
    {
        var fullDiskDatasources = resolutions
            .Where(resolution => resolution.Source == CacheSizeSource.FullDisk)
            .Select(resolution => resolution.DatasourceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var countedMounts = new HashSet<string>(CachePathComparer);

        foreach (var datasource in _datasourceService.GetDatasources()
                     .Where(datasource => datasource.Enabled
                         && fullDiskDatasources.Contains(datasource.Name)
                         && !string.IsNullOrWhiteSpace(datasource.CachePath)))
        {
            try
            {
                var cachePath = GetCanonicalCachePath(datasource.CachePath);
                var mountPoint = GetCanonicalCachePath(GetMountPoint(cachePath));
                if (!countedMounts.Add(mountPoint) || !Directory.Exists(mountPoint))
                {
                    continue;
                }

                var capacity = new DriveInfo(mountPoint).TotalSize;
                if (capacity > long.MaxValue - configuredSize)
                {
                    return long.MaxValue;
                }

                configuredSize += capacity;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to read full-disk capacity for datasource '{Name}'",
                    datasource.Name);
            }
        }

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
                    var parsed = CacheSizeParser.TryParse(value, out var parsedSize);
                    if (parsed && parsedSize > 0)
                    {
                        if (!_hasLoggedConfiguredCacheSize)
                        {
                            _logger.LogInformation("Configured cache size: {Value} ({FormattedSize}) from container {ContainerName}",
                                value, FormatBytes(parsedSize), lancacheContainer.Names.FirstOrDefault()?.TrimStart('/'));
                            _hasLoggedConfiguredCacheSize = true;
                        }
                        return parsedSize;
                    }

                    if (!parsed)
                    {
                        _logger.LogWarning("Could not parse cache size value: {Value}", value);
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

            var parsed = CacheSizeParser.TryParse(value, out var parsedSize);
            if (parsed && parsedSize > 0)
            {
                if (!_hasLoggedConfiguredCacheSize)
                {
                    _logger.LogInformation("Configured cache size: {Value} ({FormattedSize}) from .env file: {Path}",
                        value, FormatBytes(parsedSize), _envFileReader.ResolvedPath);
                    _hasLoggedConfiguredCacheSize = true;
                }
                return parsedSize;
            }

            if (!parsed)
            {
                _logger.LogWarning("Could not parse cache size value: {Value}", value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading configured cache size from .env file");
        }

        return 0;
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
                        if (IsSameOrNestedCachePath(path, mountPoint) && mountPoint.Length > bestMatchLength)
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
            if (IsSameOrNestedCachePath(path, cachePath))
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

    private Dictionary<string, long>? GetCacheMountUsage(IEnumerable<string> cachePaths)
    {
        return ReadCacheMountUsage(cachePaths.Select(GetMountPoint));
    }

    private Dictionary<string, long>? ReadCacheMountUsage(IEnumerable<string> mountPaths)
    {
        var usageByMount = new Dictionary<string, long>(CachePathComparer);
        foreach (var mountPath in mountPaths)
        {
            var canonicalMountPath = GetCanonicalCachePath(mountPath);
            if (usageByMount.ContainsKey(canonicalMountPath))
            {
                continue;
            }

            try
            {
                if (!Directory.Exists(canonicalMountPath))
                {
                    _logger.LogWarning(
                        "Cache mount does not exist while reading scan baseline: {MountPoint}",
                        canonicalMountPath);
                    return null;
                }

                var driveInfo = new DriveInfo(canonicalMountPath);
                usageByMount[canonicalMountPath] = driveInfo.TotalSize - driveInfo.AvailableFreeSpace;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to read cache mount usage for scan baseline: {MountPoint}",
                    canonicalMountPath);
                return null;
            }
        }

        return usageByMount;
    }

    internal static bool IsAnyMountUsageStale(
        IReadOnlyDictionary<string, long> usedBytesByMountAtScan,
        IReadOnlyDictionary<string, long>? currentUsedBytesByMount)
    {
        if (currentUsedBytesByMount == null)
        {
            return true;
        }

        foreach (var baseline in usedBytesByMountAtScan)
        {
            var found = false;
            var currentUsedBytes = 0L;
            foreach (var current in currentUsedBytesByMount)
            {
                if (CachePathComparer.Equals(baseline.Key, current.Key))
                {
                    found = true;
                    currentUsedBytes = current.Value;
                    break;
                }
            }

            if (!found || CacheScanStaleCalculator.IsAnyScanStale(currentUsedBytes, baseline.Value))
            {
                return true;
            }
        }

        return false;
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

    /// <summary>
    /// Maps one datasource's local 0-100 progress into the full multi-datasource operation.
    /// Steam, Epic, and named-game removal all use the same execution-plan indexing, so keeping the
    /// scaling here prevents the three removal paths from drifting apart.
    /// </summary>
    private static double ScaleRemovalProgress(
        int completedDatasources,
        int totalConfiguredDatasources,
        double datasourcePercent = 0d)
    {
        var totalDatasources = Math.Max(1, totalConfiguredDatasources);
        return (completedDatasources * 100.0 / totalDatasources)
            + (datasourcePercent / totalDatasources);
    }

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
        Func<ProcessStartInfo> createStartInfo,
        string failedProcessDescription,
        CancellationToken cancellationToken,
        Guid? operationId,
        Func<TProgress, Task>? onProgress,
        Func<RustRemovalProcessResult, Task<TReport>> buildReportAsync)
        where TProgress : class
    {
        // Last-line revalidation shared by every removal flavor (game, Epic, named,
        // service): the early per-method checks run before lock waits, and the evidence
        // can change while the lock is held. This is the check immediately before the
        // native mutation launches.
        var capabilityDenial = _capabilityService.CheckAllCanMapLogicalObjects();
        if (capabilityDenial != null)
        {
            throw new InvalidOperationException(capabilityDenial);
        }

        // Resolve all evidence-dependent launch arguments only after the shared guard.
        // The factory is synchronous so no queue/lock wait can stale the selected scheme
        // before ProcessStartInfo is handed to the process helper.
        var startInfo = createStartInfo();

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

    private async Task SaveCachedScanAsync(
        CacheSizeResponse scanResult,
        long usedCacheSizeAtScan,
        IReadOnlyDictionary<string, long>? usedCacheSizeByMountAtScan = null)
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
            UsedCacheSizeByMountAtScan = usedCacheSizeByMountAtScan?.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                CachePathComparer) ?? new Dictionary<string, long>(CachePathComparer),
            ScannedAtUtc = scannedAtUtc
        };

        await PersistCachedScanAsync(entry);
    }

    private async Task PersistCachedScanAsync(CachedCacheScan entry)
    {
        // In-memory readers should observe the new baseline even if persistence is
        // temporarily unavailable. The next successful scan or refresh retries the file write.
        _cachedCacheScan = entry;

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
            _logger.LogInformation("Persisted cache scan state to {FilePath}", _cachedScanFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist cache scan to {FilePath}", _cachedScanFilePath);
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

    /// <summary>
    /// Resolves the distinct enabled cache roots included in a full scan. The legacy path is used
    /// only when no enabled datasource was resolved.
    /// </summary>
    internal static IReadOnlyList<string> SelectFullScanCachePaths(
        IEnumerable<ResolvedDatasource> datasources,
        string legacyCachePath)
    {
        var configuredPaths = datasources
            .Where(datasource => datasource.Enabled)
            .Select(datasource => datasource.CachePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();

        var candidates = configuredPaths.Count > 0 ? configuredPaths : [legacyCachePath];
        var canonicalPaths = new List<string>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var canonicalPath = GetCanonicalCachePath(candidate);
            if (!canonicalPaths.Contains(canonicalPath, CachePathComparer))
            {
                canonicalPaths.Add(canonicalPath);
            }
        }

        // The scanner is not guaranteed to descend from an outer root into a nested datasource
        // directory, so avoiding a rare nested-config double-count is not worth omitting its files.
        return canonicalPaths;
    }

    private static StringComparer CachePathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison CachePathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string GetCanonicalCachePath(string path)
    {
        var trimmed = path.Trim();
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException or System.Security.SecurityException)
        {
            // An invalid, unsupported, or overlong configured cache path cannot be canonicalized;
            // fall back to the trimmed input so full-scan selection and baseline reads stay
            // deterministic instead of aborting the whole scan.
            return Path.TrimEndingDirectorySeparator(trimmed);
        }

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return Path.TrimEndingDirectorySeparator(fullPath);
        }

        var currentPath = root;
        var segments = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            currentPath = Path.Combine(currentPath, segment);
            try
            {
                var directory = new DirectoryInfo(currentPath);
                if (directory.LinkTarget != null)
                {
                    currentPath = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                        ?? currentPath;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Keep the normalized logical path when physical-link metadata is unavailable.
            }
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentPath));
    }

    private static bool IsSameOrNestedCachePath(string path, string possibleParent)
    {
        if (CachePathComparer.Equals(path, possibleParent))
        {
            return true;
        }

        var parentPrefix = Path.EndsInDirectorySeparator(possibleParent)
            ? possibleParent
            : possibleParent + Path.DirectorySeparatorChar;
        return path.StartsWith(parentPrefix, CachePathComparison);
    }

    /// <summary>
    /// Combines the per-root Rust results into the single response retained by existing API clients.
    /// Deletion estimates are additive because cache clearing handles datasource roots in sequence.
    /// </summary>
    internal static CacheSizeResponse AggregateCacheSizeResponses(
        IReadOnlyCollection<CacheSizeResponse> results)
    {
        if (results.Count == 0)
        {
            throw new ArgumentException("At least one cache-size result is required", nameof(results));
        }

        var preserveSeconds = results.Sum(result => result.EstimatedDeletionTimes.PreserveSeconds);
        var fullSeconds = results.Sum(result => result.EstimatedDeletionTimes.FullSeconds);
        var rsyncSeconds = results.Sum(result => result.EstimatedDeletionTimes.RsyncSeconds);
        var totalBytes = results.Sum(result => result.TotalBytes);

        return new CacheSizeResponse
        {
            TotalBytes = totalBytes,
            TotalFiles = results.Sum(result => result.TotalFiles),
            TotalDirectories = results.Sum(result => result.TotalDirectories),
            HexDirectories = results.Sum(result => result.HexDirectories),
            ScanDurationMs = results.Sum(result => result.ScanDurationMs),
            FormattedSize = FormatBytes(totalBytes),
            Timestamp = DateTime.UtcNow,
            EstimatedDeletionTimes = new EstimatedDeletionTimes
            {
                PreserveSeconds = preserveSeconds,
                FullSeconds = fullSeconds,
                RsyncSeconds = rsyncSeconds,
                PreserveFormatted = FormatEstimatedDuration(preserveSeconds),
                FullFormatted = FormatEstimatedDuration(fullSeconds),
                RsyncFormatted = FormatEstimatedDuration(rsyncSeconds)
            }
        };
    }

    private static string FormatEstimatedDuration(double seconds)
    {
        if (seconds < 1)
        {
            return "< 1 second";
        }

        var totalSeconds = (long)Math.Round(seconds);
        if (totalSeconds < 60)
        {
            return $"{totalSeconds} second{(totalSeconds == 1 ? "" : "s")}";
        }

        var minutes = totalSeconds / 60;
        var remainingSeconds = totalSeconds % 60;
        if (minutes < 60)
        {
            return remainingSeconds == 0 ? $"{minutes} minute{(minutes == 1 ? "" : "s")}" : $"{minutes}m {remainingSeconds}s";
        }

        var hours = minutes / 60;
        var remainingMinutes = minutes % 60;
        return remainingMinutes == 0 ? $"{hours} hour{(hours == 1 ? "" : "s")}" : $"{hours}h {remainingMinutes}m";
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
    private async Task RelayProgressAsync(Guid operationId, CacheSizeScanProgressData progress, bool showNotification = true)
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
            Context: context,
            ShowNotification: showNotification));
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
        IReadOnlyList<string> cachePaths,
        CancellationToken callerToken,
        Action<Guid>? onScanStarted = null,
        bool showNotification = true)
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

        // Stamp the run's visibility before any Started/Progress emit so the recovery endpoint
        // reports the run-stable flag for the whole scan, even if the page reloads mid-run.
        CurrentCacheSizeScanShowNotification = showNotification;

        // CTS ownership: handed to the tracker, which disposes it in CompleteOperation.
        var cts = new CancellationTokenSource();
        Guid operationId = default;
        operationId = _operationTracker.RegisterOperation(
            OperationType.CacheSizeScan,
            "Cache File Scan",
            cts,
            onTerminalCleanup: () =>
            {
                CurrentCacheSizeScanProgressContext = null;
                CurrentCacheSizeScanShowNotification = null;
            },
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
                        Error: "Cancelled by user",
                        ShowNotification: showNotification));
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
                        },
                        ShowNotification: showNotification));
                }

                return _notifications.NotifyAllAsync(SignalREvents.CacheSizeScanComplete, new CacheSizeScanComplete(
                    Success: false,
                    OperationId: operationId,
                    StageKey: "signalr.cacheSizeScan.complete",
                    TotalFiles: 0,
                    TotalBytes: 0,
                    Error: info.Error ?? "Rust cache size binary returned failure",
                    ShowNotification: showNotification));
            });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, cts.Token);

        try
        {
            await _notifications.NotifyAllAsync(SignalREvents.CacheSizeScanStarted, new CacheSizeScanStarted(
                StageKey: "signalr.cacheSizeScan.starting",
                OperationId: operationId,
                ShowNotification: showNotification));
            // Info-level on purpose: NotifyAllAsync logs success only at Debug, so without this
            // line production logs cannot distinguish "Started was emitted but the browser runs a
            // stale bundle" from "Started was never emitted".
            _logger.LogInformation("[CacheSizeScan] Emitted CacheSizeScanStarted for operation {OperationId}", operationId);
            onScanStarted?.Invoke(operationId);

            var results = new List<CacheSizeResponse>(cachePaths.Count);
            var completedDirectories = 0L;
            var completedFiles = 0L;
            var completedBytes = 0L;

            for (var cachePathIndex = 0; cachePathIndex < cachePaths.Count; cachePathIndex++)
            {
                var cachePath = cachePaths[cachePathIndex];
                _logger.LogInformation(
                    "[CacheSizeScan] Scanning cache root {Current}/{Total}: {CachePath}",
                    cachePathIndex + 1,
                    cachePaths.Count,
                    cachePath);

                async Task RelayDatasourceProgressAsync(CacheSizeScanProgressData progress)
                {
                    var overallProgress = new CacheSizeScanProgressData
                    {
                        StageKey = progress.StageKey,
                        PercentComplete = ((cachePathIndex + (progress.PercentComplete / 100.0)) / cachePaths.Count) * 100.0,
                        DirectoriesScanned = completedDirectories + progress.DirectoriesScanned,
                        TotalDirectories = completedDirectories + progress.TotalDirectories,
                        TotalFiles = completedFiles + progress.TotalFiles,
                        TotalBytes = completedBytes + progress.TotalBytes,
                        CalibrationStep = progress.CalibrationStep,
                        CalibrationTotalSteps = progress.CalibrationTotalSteps
                    };
                    await RelayProgressAsync(operationId, overallProgress, showNotification);
                }

                var datasourceResult = await RunCacheSizeScanAsync(
                    cachePath,
                    linked.Token,
                    operationId,
                    onProgress: RelayDatasourceProgressAsync);

                linked.Token.ThrowIfCancellationRequested();
                if (datasourceResult == null)
                {
                    _operationTracker.CompleteOperation(operationId, success: false, error: "Cache size scan failed - see server logs");
                    return null;
                }

                results.Add(datasourceResult);
                completedDirectories += datasourceResult.TotalDirectories;
                completedFiles += datasourceResult.TotalFiles;
                completedBytes += datasourceResult.TotalBytes;
            }

            var result = AggregateCacheSizeResponses(results);

            linked.Token.ThrowIfCancellationRequested();

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
    public Task<Guid?> StartCacheSizeScanInBackgroundAsync(bool showNotification = true)
    {
        var started = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = RunCacheSizeScanInBackgroundAsync(started, showNotification);
        return started.Task;
    }

    private async Task RunCacheSizeScanInBackgroundAsync(TaskCompletionSource<Guid?> started, bool showNotification)
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
                },
                showNotification: showNotification);

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
        Action<Guid>? onScanStarted = null,
        bool showNotification = true)
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

        var allCachePaths = SelectFullScanCachePaths(
            _datasourceService.GetDatasources(),
            _pathResolver.GetCacheDirectory());

        await _scanCacheLock.WaitAsync(cancellationToken);
        try
        {
            // A forced scan is either a queued manual refresh or the scheduled service.
            _logger.LogInformation("Force rescan requested - running fresh cache size scan");
            var freshResult = await RunFullScanAsync(allCachePaths, cancellationToken, onScanStarted, showNotification);
            if (freshResult != null)
            {
                var usedCacheSizeByMount = OperatingSystemDetector.IsWindows
                    ? null
                    : GetCacheMountUsage(allCachePaths);
                var usedCacheSizeAtScan = usedCacheSizeByMount is { Count: > 0 }
                    ? usedCacheSizeByMount.Values.Sum()
                    : (await GetCacheInfoAsync()).UsedCacheSize;
                await SaveCachedScanAsync(
                    freshResult,
                    usedCacheSizeAtScan,
                    usedCacheSizeByMount);
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
        public Dictionary<string, long> UsedCacheSizeByMountAtScan { get; set; } = new();
        public DateTime ScannedAtUtc { get; set; }
    }
}
