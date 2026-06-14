using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using ModelCacheClearOperation = LancacheManager.Models.CacheClearOperation;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;

namespace LancacheManager.Core.Services;

public class CacheClearingService : ScheduledBackgroundService
{
    private readonly ISignalRNotificationService _notifications;
    private readonly IPathResolver _pathResolver;
    private readonly StateService _stateService;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly DatasourceService _datasourceService;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private string _cachePath = null!;
    private CacheDeleteMode _deleteMode;
    private Guid? _currentTrackerOperationId;

    // Captured-by-value completion payload for the onTerminalEmit closure. Only one cache-clear
    // operation runs at a time (enforced in StartCacheClearAsync), so a single set is safe. These
    // are written immediately before the corresponding CompleteOperation call so the closure (which
    // fires exactly once inside CompleteOperation) reads the final metrics.
    private string _completionMessage = string.Empty;
    private int _completionDirectoriesProcessed;
    private long _completionFilesDeleted;
    private long _completionBytesDeleted;
    private int _completionDatasourcesCleared;
    private double? _completionDuration;

    protected override string ServiceName => "CacheClearingService";
    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);
    public override bool DefaultRunOnStartup => true;

    public override string ServiceKey => "cacheClearing";

    public CacheClearingService(
        ILogger<CacheClearingService> logger,
        ISignalRNotificationService notifications,
        IConfiguration configuration,
        IPathResolver pathResolver,
        StateService stateService,
        RustProcessHelper rustProcessHelper,
        DatasourceService datasourceService,
        IUnifiedOperationTracker operationTracker,
        IDbContextFactory<AppDbContext> dbContextFactory)
        : base(logger, configuration)
    {
        _notifications = notifications;
        _pathResolver = pathResolver;
        _stateService = stateService;
        _rustProcessHelper = rustProcessHelper;
        _datasourceService = datasourceService;
        _operationTracker = operationTracker;
        _dbContextFactory = dbContextFactory;

        _deleteMode = CacheDeleteMode.Preserve;

        LoadStateOverrides(stateService);
    }

    protected override Task OnStartupAsync(CancellationToken stoppingToken)
    {
        // Resolve cache path at startup instead of in the constructor to avoid
        // blocking DI when datasource resolution depends on external resources
        var primaryCachePath = _datasourceService.ResolvePrimaryCachePath();
        if (primaryCachePath != null)
        {
            _cachePath = primaryCachePath;
            _logger.LogInformation("Using cache path from default datasource: {CachePath}", _cachePath);
        }
        else
        {
            _cachePath = DetectLegacyCachePath(_configuration);
        }

        _logger.LogInformation("CacheClearingService initialized with {Count} datasource(s)", _datasourceService.DatasourceCount);

        LoadOperations();
        return Task.CompletedTask;
    }

    protected override Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        SaveAllOperations();

        // Cancel all active cache clearing operations
        var activeOperations = _operationTracker.GetActiveOperations(OperationType.CacheClearing);
        foreach (var operation in activeOperations)
        {
            _operationTracker.CancelOperation(operation.Id);
        }
    }

    public async Task<Guid?> StartCacheClearAsync(string? datasourceName = null)
    {
        await _startLock.WaitAsync();
        try
        {
            // Use "all" as the key when clearing all datasources
            var trackerKey = datasourceName ?? "all";

            // Check for any active cache clearing operations
            var activeOperations = _operationTracker.GetActiveOperations(OperationType.CacheClearing);
            if (activeOperations.Any())
            {
                var activeOperation = activeOperations.First();
                _logger.LogWarning("Cache clear is already running: {OperationId}", activeOperation.Id);
                return null; // Return null to indicate operation already running
            }

            var cts = new CancellationTokenSource();

            // Register with unified operation tracker for centralized cancellation
            var metadata = new CacheClearingMetrics
            {
                EntityKey = trackerKey,
                DatasourceName = datasourceName
            };
            // operationId is filled in right after RegisterOperation returns; the closure fires later
            // (at completion) so it reads the assigned value by reference-capture of this local.
            var capturedOperationId = Guid.Empty;
            _currentTrackerOperationId = _operationTracker.RegisterOperation(
                OperationType.CacheClearing,
                "Cache Clearing",
                cts,
                metadata,
                onTerminalCleanup: () => { _currentTrackerOperationId = null; },
                onTerminalEmit: info => _notifications.NotifyAllAsync(
                    SignalREvents.CacheClearingComplete,
                    BuildClearCompleteEvent(capturedOperationId, info))
            );
            var operationId = _currentTrackerOperationId.Value;
            capturedOperationId = operationId;

            // Update the initial message
            var initialMessage = datasourceName != null
                ? $"Initializing cache clear for {datasourceName}..."
                : "Initializing cache clear...";
            _operationTracker.UpdateProgress(operationId, 0, initialMessage);

            SaveOperationToState(trackerKey, operationId);

            _logger.LogInformation($"Starting cache clear operation {operationId}" +
                (datasourceName != null ? $" for datasource: {datasourceName}" : " for all datasources"));

            // Start the clear operation on a background thread
            _ = Task.Run(async () => await RunCacheClearAsync(trackerKey, operationId, datasourceName), cts.Token);

            return operationId;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task RunCacheClearAsync(string trackerKey, Guid operationId, string? datasourceName)
    {
        try
        {
            _logger.LogInformation($"Executing cache clear operation {operationId}");

            // Send started event
            await _notifications.NotifyAllAsync(SignalREvents.CacheClearingStarted, new
            {
                OperationId = operationId,
                StageKey = "signalr.cacheClear.initializing"
            });

            _operationTracker.UpdateProgress(operationId, 0, "Checking permissions...");
            await NotifyProgressAsync(operationId);

            // Get datasources to clear (filtered by name if specified)
            var allDatasources = _datasourceService.GetDatasources()
                .Where(ds => ds.Enabled && !string.IsNullOrEmpty(ds.CachePath))
                .ToList();

            // Use cached permission flags (refreshed by DirectoryPermissionMonitor).
            var writableDatasources = allDatasources
                .Where(ds => ds.CacheWritable)
                .ToList();

            if (writableDatasources.Count == 0 && allDatasources.Count > 0)
            {
                var errorMessage = "Cannot clear cache: all cache directories are read-only. " +
                    "This is typically caused by incorrect PUID/PGID settings in your docker-compose.yml. " +
                    $"The lancache container is configured to run as UID/GID {ContainerEnvironment.UidGid} (configured via PUID/PGID environment variables).";

                _logger.LogWarning("[CacheClear] Permission check failed: {Error}", errorMessage);

                // Mark operation as complete (failed) in unified tracker.
                // Terminal CacheClearingComplete (failed) is emitted by the onTerminalEmit closure.
                _operationTracker.CompleteOperation(operationId, success: false, error: errorMessage);
                _currentTrackerOperationId = null;

                await NotifyProgressAsync(operationId);

                SaveOperationToState(trackerKey, operationId);

                return;
            }

            // Log if some datasources are read-only (partial operation warning)
            var readOnlyCount = allDatasources.Count - writableDatasources.Count;
            if (readOnlyCount > 0)
            {
                _logger.LogWarning(
                    "[CacheClear] {ReadOnlyCount} of {TotalCount} datasources are read-only and will be skipped",
                    readOnlyCount, allDatasources.Count);
            }

            List<ResolvedDatasource> datasources;
            if (!string.IsNullOrEmpty(datasourceName))
            {
                // Filter to specific datasource
                datasources = allDatasources
                    .Where(ds => ds.Name.Equals(datasourceName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (!datasources.Any())
                {
                    var errorMessage = $"Datasource '{datasourceName}' not found";

                    // Mark operation as complete (failed) in unified tracker
                    _operationTracker.CompleteOperation(operationId, success: false, error: errorMessage);
                    _currentTrackerOperationId = null;

                    await NotifyProgressAsync(operationId);
                    SaveOperationToState(trackerKey, operationId);

                    return;
                }

                _logger.LogInformation($"Cache clear will process specific datasource: {datasourceName}");
            }
            else
            {
                // Clear all datasources
                datasources = allDatasources;

                if (!datasources.Any())
                {
                    // Fallback to default cache path
                    datasources = new List<ResolvedDatasource>
                    {
                        new ResolvedDatasource { Name = "default", CachePath = _cachePath, Enabled = true }
                    };
                }

                _logger.LogInformation($"Cache clear will process {datasources.Count} datasource(s)");
            }

            // Collect all valid cache paths with their directory counts
            var validCachePaths = new List<(string Name, string Path, int DirCount)>();
            foreach (var ds in datasources)
            {
                if (!Directory.Exists(ds.CachePath))
                {
                    _logger.LogWarning($"Cache path does not exist for datasource {ds.Name}: {ds.CachePath}");
                    continue;
                }

                var cacheSubdirs = Directory.GetDirectories(ds.CachePath)
                    .Where(d =>
                    {
                        var name = Path.GetFileName(d);
                        return name.Length == 2 && IsHex(name);
                    }).ToList();

                if (cacheSubdirs.Any())
                {
                    validCachePaths.Add((ds.Name, ds.CachePath, cacheSubdirs.Count));
                    _logger.LogInformation($"Datasource {ds.Name}: {cacheSubdirs.Count} cache directories at {ds.CachePath}");
                }
                else
                {
                    _logger.LogWarning($"No cache directories found for datasource {ds.Name} at {ds.CachePath}");
                }
            }

            if (!validCachePaths.Any())
            {
                var error = "No cache directories found in any datasource";
                _logger.LogWarning("Cache clear operation {OperationId} failed: {Error}", operationId, error);

                // Mark operation as complete (failed) in unified tracker.
                // Terminal CacheClearingComplete (failed) is emitted by the onTerminalEmit closure.
                _operationTracker.CompleteOperation(operationId, success: false, error: error);
                _currentTrackerOperationId = null;

                await NotifyProgressAsync(operationId);

                SaveOperationToState(trackerKey, operationId);

                return;
            }

            // Calculate total directories across all datasources
            var totalDirectoriesAllDatasources = validCachePaths.Sum(p => p.DirCount);

            // Update total directories in metrics
            _operationTracker.UpdateMetadata(operationId, (object meta) =>
            {
                var metrics = (CacheClearingMetrics)meta;
                metrics.TotalDirectories = totalDirectoriesAllDatasources;
            });

            _logger.LogInformation($"Total cache directories to clear across all datasources: {totalDirectoriesAllDatasources}");

            // Use Rust binary for fast cache clearing
            var operationsDir = _pathResolver.GetOperationsDirectory();
            var rustBinaryPath = _pathResolver.GetRustCacheCleanerPath();

            if (!File.Exists(rustBinaryPath))
            {
                var error = $"Rust cache_cleaner binary not found at {rustBinaryPath}";

                // Mark operation as complete (failed) in unified tracker.
                // Terminal CacheClearingComplete (failed) is emitted by the onTerminalEmit closure.
                _operationTracker.CompleteOperation(operationId, success: false, error: error);
                _currentTrackerOperationId = null;

                await NotifyProgressAsync(operationId);

                SaveOperationToState(trackerKey, operationId);

                return;
            }

            _logger.LogInformation($"Using Rust cache cleaner: {rustBinaryPath}");

            _operationTracker.UpdateProgress(operationId, 0, "Starting cache clear...");
            await NotifyProgressAsync(operationId);
            SaveOperationToState(trackerKey, operationId);

            // Track aggregate totals across all datasources
            var totalBytesDeleted = 0L;
            var totalFilesDeleted = 0L;
            var totalDirsProcessed = 0;
            var dirsProcessedBefore = 0;

            // Get operation for CancellationToken access
            var operation = _operationTracker.GetOperation(operationId);
            var cancellationToken = operation?.CancellationTokenSource?.Token ?? CancellationToken.None;

            // Process each datasource cache path sequentially
            for (var dsIndex = 0; dsIndex < validCachePaths.Count; dsIndex++)
            {
                var (dsName, cachePath, dirCount) = validCachePaths[dsIndex];
                var progressFile = Path.Combine(operationsDir, $"cache_clear_progress_{operationId}_{dsIndex}.json");

                _logger.LogInformation($"Clearing cache for datasource {dsName} ({dsIndex + 1}/{validCachePaths.Count}): {cachePath}");
                var percentSoFar = (double)dsIndex / validCachePaths.Count * 100;
                _operationTracker.UpdateProgress(operationId, percentSoFar, $"Clearing {dsName} cache ({dsIndex + 1}/{validCachePaths.Count})...");
                await NotifyProgressAsync(operationId);

                // Check for cancellation before starting each datasource
                operation = _operationTracker.GetOperation(operationId);
                if (operation?.CancellationTokenSource?.Token.IsCancellationRequested == true)
                {
                    // Mark operation as complete (cancelled) in unified tracker
                    _operationTracker.CompleteOperation(operationId, success: false, error: "Cancelled by user");
                    _currentTrackerOperationId = null;

                    await NotifyProgressAsync(operationId);
                    SaveOperationToState(trackerKey, operationId);

                    return;
                }

                // Build arguments - Rust auto-detects optimal thread count
                var arguments = $"\"{cachePath}\" \"{progressFile}\" {_deleteMode.ToWireString()}";

                var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                    rustBinaryPath,
                    arguments);

                var lastLoggedDirs = 0;
                var lastLogTime = DateTime.UtcNow;

                var result = await _rustProcessHelper.ExecuteTrackedProcessWithProgressAsync<RustCacheProgress>(
                    startInfo,
                    operationId,
                    cancellationToken,
                    progressFile,
                    async progressData =>
                    {
                        var currentDirsProcessed = dirsProcessedBefore + progressData.DirectoriesProcessed;
                        var currentBytesDeleted = totalBytesDeleted + (long)progressData.BytesDeleted;
                        var currentFilesDeleted = totalFilesDeleted + (long)progressData.FilesDeleted;
                        var percentComplete = (double)currentDirsProcessed / totalDirectoriesAllDatasources * 100;

                        _operationTracker.UpdateMetadata(operationId, (object meta) =>
                        {
                            var metricsToUpdate = (CacheClearingMetrics)meta;
                            metricsToUpdate.DirectoriesProcessed = currentDirsProcessed;
                            metricsToUpdate.BytesDeleted = currentBytesDeleted;
                            metricsToUpdate.FilesDeleted = currentFilesDeleted;
                            metricsToUpdate.CurrentStageKey = progressData.StageKey;
                            metricsToUpdate.CurrentContext = progressData.Context;
                        });

                        _operationTracker.UpdateProgress(operationId, percentComplete, progressData.StageKey ?? string.Empty);
                        await NotifyProgressAsync(operationId);

                        var timeSinceLastLog = DateTime.UtcNow - lastLogTime;
                        var dirsChanged = progressData.DirectoriesProcessed != lastLoggedDirs;
                        var shouldLog =
                            (dirsChanged && progressData.DirectoriesProcessed % 5 == 0) ||
                            (dirsChanged && timeSinceLastLog.TotalSeconds >= 3) ||
                            (!dirsChanged && timeSinceLastLog.TotalSeconds >= 30);

                        if (shouldLog)
                        {
                            var activeInfo = progressData.ActiveCount > 0
                                ? $" | Active: {progressData.ActiveCount} [{string.Join(", ", progressData.ActiveDirectories)}]"
                                : "";
                            _logger.LogInformation($"[{GetDeleteModeDisplayName()}] Cache Clear Progress: {percentComplete:F1}% complete - {currentDirsProcessed}/{totalDirectoriesAllDatasources} directories cleared{activeInfo}");
                            lastLoggedDirs = progressData.DirectoriesProcessed;
                            lastLogTime = DateTime.UtcNow;
                        }

                        if (progressData.DirectoriesProcessed % 10 == 0)
                        {
                            SaveOperationToState(trackerKey, operationId);
                        }
                    },
                    "cache_cleaner");

                operation = _operationTracker.GetOperation(operationId);

                // A force-kill (SIGKILL) makes the child exit with 137. If WaitForExitAsync wins the
                // race against token cancellation it returns normally with ExitCode 137 instead of
                // throwing OperationCanceledException. Treat that as cancellation (not a failed clear)
                // so the op is labelled Cancelled, not Failed.
                if (result.ExitCode == 137 &&
                    (cancellationToken.IsCancellationRequested ||
                     operation?.CancellationTokenSource?.Token.IsCancellationRequested == true ||
                     operation?.Cancelled == true))
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (result.ExitCode != 0)
                {
                    throw new Exception($"Rust cache_cleaner failed for {dsName} with exit code {result.ExitCode}: {result.Error}");
                }

                _logger.LogInformation($"[{GetDeleteModeDisplayName()}] Rust cache cleaner output: {result.Output}");

                var finalProgress = await _rustProcessHelper.ReadProgressFileAsync<RustCacheProgress>(progressFile);
                if (finalProgress != null)
                {
                    totalBytesDeleted += (long)finalProgress.BytesDeleted;
                    totalFilesDeleted += (long)finalProgress.FilesDeleted;
                    totalDirsProcessed += finalProgress.DirectoriesProcessed;
                    dirsProcessedBefore = totalDirsProcessed;

                    _operationTracker.UpdateMetadata(operationId, (object meta) =>
                    {
                        var metricsToUpdate = (CacheClearingMetrics)meta;
                        metricsToUpdate.DirectoriesProcessed = totalDirsProcessed;
                        metricsToUpdate.BytesDeleted = totalBytesDeleted;
                        metricsToUpdate.FilesDeleted = totalFilesDeleted;
                    });
                }

                await _rustProcessHelper.DeleteTempFileAsync(progressFile);
                _logger.LogInformation($"Completed clearing {dsName} cache: {finalProgress?.DirectoriesProcessed ?? 0} directories");
            }

            var datasourceNames = string.Join(", ", validCachePaths.Select(p => p.Name));
            var successMessage = validCachePaths.Count > 1
                ? $"Successfully cleared {totalDirsProcessed} cache directories across {validCachePaths.Count} datasources ({datasourceNames})"
                : $"Successfully cleared {totalDirsProcessed} cache directories";

            // Compute duration BEFORE CompleteOperation: the onTerminalEmit closure fires inside
            // CompleteOperation, so the completion metrics must be captured by value first.
            operation = _operationTracker.GetOperation(operationId);
            var duration = operation != null
                ? (DateTime.UtcNow - operation.StartedAt).TotalSeconds
                : 0;

            _completionMessage = successMessage;
            _completionDirectoriesProcessed = totalDirsProcessed;
            _completionFilesDeleted = totalFilesDeleted;
            _completionBytesDeleted = totalBytesDeleted;
            _completionDatasourcesCleared = validCachePaths.Count;
            _completionDuration = duration;

            // Mark operation as complete in unified tracker (emits CacheClearingComplete via onTerminalEmit)
            _operationTracker.CompleteOperation(operationId, success: true);
            _currentTrackerOperationId = null;

            _logger.LogInformation($"Cache clear completed in {duration:F1} seconds - Cleared {totalDirsProcessed} directories across {validCachePaths.Count} datasource(s)");

            // Clear all cached detection results since all cache files were deleted
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
            // Direct DbContext deletes are deliberate: cache clear wipes whole detection tables, not the load/upsert flow GameCacheDetectionDataService owns.
            var gamesDeleted = await dbContext.CachedGameDetections.ExecuteDeleteAsync();
            var servicesDeleted = await dbContext.CachedServiceDetections.ExecuteDeleteAsync();
            var corruptionDeleted = await dbContext.CachedCorruptionDetections.ExecuteDeleteAsync();
            await dbContext.CachedDetectionSummaries
                .Where(s => s.Id == CachedDetectionSummary.SingletonId)
                .ExecuteDeleteAsync();
            _logger.LogInformation("[CacheClearing] Cleared cached detection results: {Games} games, {Services} services, {Corruption} corruption entries",
                gamesDeleted, servicesDeleted, corruptionDeleted);

            await NotifyProgressAsync(operationId);

            // Terminal CacheClearingComplete is emitted by the onTerminalEmit closure inside
            // CompleteOperation above (exactly-once, CompletedFlag-gated).

            SaveOperationToState(trackerKey, operationId);
        }
        catch (OperationCanceledException)
        {
            // Handle cancellation gracefully - this is expected when user cancels
            _logger.LogInformation("Cache clear operation {OperationId} was cancelled by user", operationId);

            // If a universal force-kill already completed this op, the CompletedFlag-gated
            // CompleteOperation below is a no-op and the onTerminalEmit closure does not re-fire.
            if (_operationTracker.GetOperation(operationId)?.Status
                is not (OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled))
            {
                // Mark operation as complete (cancelled) in unified tracker.
                // Terminal CacheClearingComplete (cancelled) is emitted by the onTerminalEmit closure.
                _operationTracker.CompleteOperation(operationId, success: false, error: "Cancelled by user");

                await NotifyProgressAsync(operationId);
            }

            SaveOperationToState(trackerKey, operationId);
        }
        catch (Exception ex)
        {
            // Distinguish between expected failures and unexpected errors
            var isExpectedFailure = ex.Message.Contains("No cache directories found") ||
                                   ex.Message.Contains("Cache path does not exist");

            if (isExpectedFailure)
            {
                _logger.LogWarning("Cache clear operation {OperationId} failed: {Message}", operationId, ex.Message);
            }
            else
            {
                _logger.LogError(ex, "Error in cache clear operation {OperationId}", operationId);
            }

            // Mark operation as complete (failed) in unified tracker.
            // Terminal CacheClearingComplete (failed) is emitted by the onTerminalEmit closure,
            // which reads this error string from OperationTerminalInfo.Error.
            _operationTracker.CompleteOperation(operationId, success: false, error: $"Cache clear failed: {ex.Message}");
            _currentTrackerOperationId = null;

            await NotifyProgressAsync(operationId);

            SaveOperationToState(trackerKey, operationId);
        }
    }

    /// <summary>
    /// Builds the strongly-typed terminal payload for the cache-clear operation. Fires EXACTLY ONCE
    /// from inside CompleteOperation (CompletedFlag-gated) for success, cancel, and error alike.
    /// Completion metrics are captured by value into the _completion* fields immediately before the
    /// corresponding CompleteOperation call; this reads them so the wire payload matches the prior
    /// SendOperationCompleteAsync emits.
    /// </summary>
    private CacheClearComplete BuildClearCompleteEvent(Guid operationId, OperationTerminalInfo info)
    {
        if (info.Cancelled)
        {
            return new CacheClearComplete(
                OperationId: operationId,
                Success: false,
                Status: OperationStatus.Cancelled,
                Message: "Cache clear cancelled by user",
                Cancelled: true);
        }

        if (info.Success)
        {
            return new CacheClearComplete(
                OperationId: operationId,
                Success: true,
                Status: OperationStatus.Completed,
                Message: _completionMessage,
                Cancelled: false,
                FilesDeleted: (int)_completionFilesDeleted,
                DirectoriesProcessed: _completionDirectoriesProcessed,
                BytesDeleted: _completionBytesDeleted,
                DatasourcesCleared: _completionDatasourcesCleared,
                Duration: _completionDuration);
        }

        var error = info.Error ?? "Cache clear failed";
        return new CacheClearComplete(
            OperationId: operationId,
            Success: false,
            Status: OperationStatus.Failed,
            Message: error,
            Cancelled: false,
            Error: error);
    }

    // Helper class for deserializing Rust progress data
    private class RustCacheProgress
    {
        public bool IsProcessing { get; set; }
        public double PercentComplete { get; set; }
        public OperationStatus Status { get; set; } = OperationStatus.Pending;

        [System.Text.Json.Serialization.JsonPropertyName("stageKey")]
        public string? StageKey { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("context")]
        public Dictionary<string, object?>? Context { get; set; }

        public int DirectoriesProcessed { get; set; }
        public int TotalDirectories { get; set; }
        public ulong BytesDeleted { get; set; }
        public ulong FilesDeleted { get; set; }
        public List<string> ActiveDirectories { get; set; } = new();
        public int ActiveCount { get; set; }
    }

    /// <summary>
    /// Detects the cache path using legacy configuration when no datasource is available.
    /// Checks configured paths for directories containing two-character hex-named subdirectories.
    /// </summary>
    private string DetectLegacyCachePath(IConfiguration configuration)
    {
        var possiblePaths = new List<string> { _pathResolver.GetCacheDirectory() };

        var configPath = configuration["LanCache:CachePath"];
        if (!string.IsNullOrEmpty(configPath) && !possiblePaths.Contains(configPath))
        {
            possiblePaths.Insert(0, configPath);
        }

        foreach (var path in possiblePaths)
        {
            if (!Directory.Exists(path))
                continue;

            var dirs = Directory.GetDirectories(path);
            var hasHexDirs = dirs.Any(d =>
            {
                var name = Path.GetFileName(d);
                return name.Length == 2 && IsHex(name);
            });

            if (!hasHexDirs)
                continue;

            _logger.LogInformation("Detected cache path: {CachePath}", path);
            return path;
        }

        var fallback = _pathResolver.GetCacheDirectory();
        _logger.LogWarning("No cache detected, using configured path: {CachePath}", fallback);
        return fallback;
    }

    private bool IsHex(string value)
    {
        return value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
    }

    private async Task NotifyProgressAsync(Guid operationId)
    {
        try
        {
            var operation = _operationTracker.GetOperation(operationId);
            if (operation == null) return;

            // Get metrics from tracker metadata
            var metrics = operation.Metadata as CacheClearingMetrics;

            await _notifications.NotifyAllAsync(SignalREvents.CacheClearingProgress, new
            {
                OperationId = operation.Id,
                PercentComplete = operation.PercentComplete,
                Status = operation.Status,
                StageKey = metrics?.CurrentStageKey,
                Context = metrics?.CurrentContext,
                DirectoriesProcessed = metrics?.DirectoriesProcessed ?? 0,
                TotalDirectories = metrics?.TotalDirectories ?? 0,
                BytesDeleted = metrics?.BytesDeleted ?? 0L,
                FilesDeleted = metrics?.FilesDeleted ?? 0L,
                Error = operation.Status == OperationStatus.Failed ? operation.Message : null
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending cache clear progress notification");
        }
    }

    private void LoadOperations()
    {
        try
        {
            var operations = _stateService.GetCacheClearOperations().ToList();
            var modifiedCount = 0;

            foreach (var op in operations)
            {
                // If operation was running when service stopped, mark it as failed
                // (We can't resume Rust processes after restart)
                if (op.Status == OperationStatus.Pending || op.Status == OperationStatus.Running)
                {
                    op.Status = OperationStatus.Failed;
                    op.Error = "Operation interrupted by service restart";
                    op.EndTime = DateTime.UtcNow;
                    op.Message = op.Error;
                    modifiedCount++;
                    _logger.LogWarning($"Cache clear operation {op.Id} was interrupted by restart, marking as failed");
                }
            }

            // Save any modifications
            if (modifiedCount > 0)
            {
                _stateService.UpdateCacheClearOperations(ops =>
                {
                    ops.Clear();
                    ops.AddRange(operations);
                });
            }

            _logger.LogInformation($"Loaded {operations.Count} persisted cache clear operations ({modifiedCount} marked as failed due to restart)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load persisted operations");
        }
    }

    private void SaveOperationToState(string trackerKey, Guid operationId)
    {
        try
        {
            var operation = _operationTracker.GetOperation(operationId);
            if (operation == null) return;

            var stateOp = new ModelCacheClearOperation
            {
                Id = operationId,
                Status = operation.Status,
                Message = operation.Message,
                Progress = (int)operation.PercentComplete,
                StartTime = operation.StartedAt,
                EndTime = operation.CompletedAt,
                Error = operation.Status == OperationStatus.Failed ? operation.Message : null
            };

            _stateService.UpdateCacheClearOperations(operations =>
            {
                var existing = operations.FirstOrDefault(op => op.Id == operationId);
                if (existing != null)
                {
                    operations.Remove(existing);
                }
                operations.Add(stateOp);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save operation to state");
        }
    }

    private void SaveAllOperations()
    {
        try
        {
            var activeOperations = _operationTracker.GetActiveOperations(OperationType.CacheClearing);
            var newOperations = activeOperations.Select(op => new ModelCacheClearOperation
            {
                Id = op.Id,
                Status = op.Status,
                Message = op.Message,
                Progress = (int)op.PercentComplete,
                StartTime = op.StartedAt,
                EndTime = op.CompletedAt,
                Error = op.Status == OperationStatus.Failed ? op.Message : null
            }).ToList();

            _stateService.UpdateCacheClearOperations(operations =>
            {
                // Merge active operations with existing completed ones
                var completedOps = operations.Where(o =>
                    o.Status == OperationStatus.Completed ||
                    o.Status == OperationStatus.Failed ||
                    o.Status == OperationStatus.Cancelled).ToList();
                operations.Clear();
                operations.AddRange(completedOps);
                operations.AddRange(newOperations);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save all operations to state");
        }
    }

    public CacheClearProgress? GetOperationStatus(Guid operationId)
    {
        // Search through active operations for the one with matching ID
        var operation = _operationTracker.GetOperation(operationId);

        // Also check if it might be a completed operation by checking state
        if (operation == null)
        {
            var stateOps = _stateService.GetCacheClearOperations();
            var stateOp = stateOps.FirstOrDefault(op => op.Id == operationId);
            if (stateOp != null)
            {
                return new CacheClearProgress
                {
                    OperationId = stateOp.Id,
                    Status = stateOp.Status,
                    StatusMessage = stateOp.Message,
                    StartTime = stateOp.StartTime,
                    EndTime = stateOp.EndTime,
                    Error = stateOp.Error,
                    PercentComplete = stateOp.Progress
                };
            }
            return null;
        }

        // Get metrics from tracker metadata
        var metrics = operation.Metadata as CacheClearingMetrics;

        return new CacheClearProgress
        {
            OperationId = operation.Id,
            Status = operation.Status,
            StatusMessage = operation.Message,
            StartTime = operation.StartedAt,
            EndTime = operation.CompletedAt,
            DirectoriesProcessed = metrics?.DirectoriesProcessed ?? 0,
            TotalDirectories = metrics?.TotalDirectories ?? 0,
            BytesDeleted = metrics?.BytesDeleted ?? 0,
            FilesDeleted = metrics?.FilesDeleted ?? 0,
            Error = operation.Status == OperationStatus.Failed ? operation.Message : null,
            PercentComplete = operation.PercentComplete
        };
    }

    /// <summary>
    /// Gets all active cache clear operations (wrapper for GetAllOperations)
    /// </summary>
    public List<CacheClearProgress> GetActiveOperations()
    {
        return _operationTracker.GetActiveOperations(OperationType.CacheClearing)
            .Select(op =>
            {
                var metrics = op.Metadata as CacheClearingMetrics;
                return new CacheClearProgress
                {
                    OperationId = op.Id,
                    Status = op.Status,
                    StatusMessage = op.Message,
                    StartTime = op.StartedAt,
                    EndTime = op.CompletedAt,
                    DirectoriesProcessed = metrics?.DirectoriesProcessed ?? 0,
                    TotalDirectories = metrics?.TotalDirectories ?? 0,
                    BytesDeleted = metrics?.BytesDeleted ?? 0,
                    FilesDeleted = metrics?.FilesDeleted ?? 0,
                    Error = op.Status == OperationStatus.Failed ? op.Message : null,
                    PercentComplete = op.PercentComplete
                };
            }).ToList();
    }

    /// <summary>
    /// Gets cache clear status for a specific operation (wrapper for GetOperationStatus)
    /// </summary>
    public CacheClearProgress? GetCacheClearStatus(Guid operationId)
    {
        return GetOperationStatus(operationId);
    }

    public List<CacheClearProgress> GetAllOperations()
    {
        // Combine active operations from tracker with completed operations from state
        var activeOps = _operationTracker.GetActiveOperations(OperationType.CacheClearing)
            .Select(op =>
            {
                var metrics = op.Metadata as CacheClearingMetrics;
                return new CacheClearProgress
                {
                    OperationId = op.Id,
                    Status = op.Status,
                    StatusMessage = op.Message,
                    StartTime = op.StartedAt,
                    EndTime = op.CompletedAt,
                    DirectoriesProcessed = metrics?.DirectoriesProcessed ?? 0,
                    TotalDirectories = metrics?.TotalDirectories ?? 0,
                    BytesDeleted = metrics?.BytesDeleted ?? 0,
                    FilesDeleted = metrics?.FilesDeleted ?? 0,
                    Error = op.Status == OperationStatus.Failed ? op.Message : null,
                    PercentComplete = op.PercentComplete
                };
            }).ToList();

        // Add completed operations from state that aren't currently active
        var stateOps = _stateService.GetCacheClearOperations()
            .Where(so => !activeOps.Any(ao => ao.OperationId == so.Id))
            .Select(op => new CacheClearProgress
            {
                OperationId = op.Id,
                Status = op.Status,
                StatusMessage = op.Message,
                StartTime = op.StartTime,
                EndTime = op.EndTime,
                Error = op.Error,
                PercentComplete = op.Progress
            }).ToList();

        return activeOps.Concat(stateOps).ToList();
    }

    public void SetDeleteMode(CacheDeleteMode deleteMode)
    {
        _deleteMode = deleteMode;
        _logger.LogInformation($"Cache clear delete mode updated to {deleteMode.ToWireString()}");
    }

    public CacheDeleteMode GetDeleteMode()
    {
        return _deleteMode;
    }

    private string GetDeleteModeDisplayName()
    {
        return _deleteMode.ToDisplayName();
    }

    public async Task<bool> IsRsyncAvailableAsync()
    {
        // Rsync only available on Linux
        if (!OperatingSystemDetector.IsLinux)
        {
            return false;
        }

        try
        {
            // Check if rsync command exists
            var startInfo = _rustProcessHelper.CreateProcessStartInfo("which", "rsync");
            var result = await _rustProcessHelper.ExecuteProcessAsync(startInfo, CancellationToken.None);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

}

public class CacheClearProgress
{
    public Guid OperationId { get; set; }
    public OperationStatus Status { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int DirectoriesProcessed { get; set; }
    public int TotalDirectories { get; set; }
    public long BytesDeleted { get; set; }
    public long FilesDeleted { get; set; }
    public string? Error { get; set; }
    public double PercentComplete { get; set; }
}
