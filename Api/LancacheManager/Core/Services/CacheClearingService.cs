using System.Diagnostics;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using ModelCacheClearOperation = LancacheManager.Models.CacheClearOperation;

namespace LancacheManager.Core.Services;

public class CacheClearingService : ScheduledBackgroundService
{
    private readonly ISignalRNotificationService _notifications;
    private readonly IPathResolver _pathResolver;
    private readonly StateService _stateService;
    private readonly ProcessManager _processManager;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly DatasourceService _datasourceService;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private string _cachePath = null!;
    private string _deleteMode;
    private string? _currentTrackerOperationId;

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
        ProcessManager processManager,
        RustProcessHelper rustProcessHelper,
        DatasourceService datasourceService,
        IUnifiedOperationTracker operationTracker,
        IDbContextFactory<AppDbContext> dbContextFactory)
        : base(logger, configuration)
    {
        _notifications = notifications;
        _pathResolver = pathResolver;
        _stateService = stateService;
        _processManager = processManager;
        _rustProcessHelper = rustProcessHelper;
        _datasourceService = datasourceService;
        _operationTracker = operationTracker;
        _dbContextFactory = dbContextFactory;

        _deleteMode = "preserve";

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

        LoadPersistedOperations();
        return Task.CompletedTask;
    }

    protected override Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        CleanupOldOperations();
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        SaveAllOperationsToState();

        // Cancel all active cache clearing operations
        var activeOperations = _operationTracker.GetActiveOperations(OperationType.CacheClearing);
        foreach (var operation in activeOperations)
        {
            _operationTracker.CancelOperation(operation.Id);
        }
    }

    public async Task<string?> StartCacheClearAsync(string? datasourceName = null)
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
            _currentTrackerOperationId = _operationTracker.RegisterOperation(
                OperationType.CacheClearing,
                "Cache Clearing",
                cts,
                metadata
            );
            var operationId = _currentTrackerOperationId;

            // Update the initial message
            var initialMessage = datasourceName != null
                ? $"Initializing cache clear for {datasourceName}..."
                : "Initializing cache clear...";
            _operationTracker.UpdateProgress(operationId, 0, initialMessage);

            SaveOperationToState(trackerKey, operationId);

            _logger.LogInformation($"Starting cache clear operation {operationId}" +
                (datasourceName != null ? $" for datasource: {datasourceName}" : " for all datasources"));

            // Start the clear operation on a background thread
            _ = Task.Run(async () => await ExecuteCacheClearAsync(trackerKey, operationId, datasourceName), cts.Token);

            return operationId;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task ExecuteCacheClearAsync(string trackerKey, string operationId, string? datasourceName)
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

            // CRITICAL: Check write permissions BEFORE proceeding
            // This prevents operations from failing partway through due to permission issues
            var writableDatasources = allDatasources
                .Where(ds => _pathResolver.IsDirectoryWritable(ds.CachePath))
                .ToList();

            if (writableDatasources.Count == 0 && allDatasources.Count > 0)
            {
                var errorMessage = "Cannot clear cache: all cache directories are read-only. " +
                    "This is typically caused by incorrect PUID/PGID settings in your docker-compose.yml. " +
                    $"The lancache container is configured to run as UID/GID {ContainerEnvironment.UidGid} (configured via PUID/PGID environment variables).";

                _logger.LogWarning("[CacheClear] Permission check failed: {Error}", errorMessage);

                // Mark operation as complete (failed) in unified tracker
                _operationTracker.CompleteOperation(operationId, success: false, error: errorMessage);
                _currentTrackerOperationId = null;

                await NotifyProgressAsync(operationId);

                await _notifications.SendOperationCompleteAsync(
                    SignalREvents.CacheClearingComplete, operationId,
                    success: false, message: errorMessage, cancelled: false,
                    new { Error = errorMessage });

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

                // Mark operation as complete (failed) in unified tracker
                _operationTracker.CompleteOperation(operationId, success: false, error: error);
                _currentTrackerOperationId = null;

                await NotifyProgressAsync(operationId);

                await _notifications.SendOperationCompleteAsync(
                    SignalREvents.CacheClearingComplete, operationId,
                    success: false, message: error, cancelled: false,
                    new { Error = error });

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

                // Mark operation as complete (failed) in unified tracker
                _operationTracker.CompleteOperation(operationId, success: false, error: error);
                _currentTrackerOperationId = null;

                await NotifyProgressAsync(operationId);

                // Send completion notification
                await _notifications.SendOperationCompleteAsync(
                    SignalREvents.CacheClearingComplete, operationId,
                    success: false, message: error, cancelled: false,
                    new { Error = error });

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
                var arguments = $"\"{cachePath}\" \"{progressFile}\" {_deleteMode}";

                var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                    rustBinaryPath,
                    arguments);

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        throw new Exception($"Failed to start Rust cache_cleaner process for datasource {dsName}");
                    }

                    // Store process reference for force kill capability
                    operation = _operationTracker.GetOperation(operationId);
                    if (operation != null)
                    {
                        operation.AssociatedProcess = process;
                    }

                    // Track last logged values for console output
                    var lastLoggedDirs = 0;
                    var lastLogTime = DateTime.UtcNow;

                    // Poll the progress file while the process runs
                    var pollTask = Task.Run(async () =>
                    {
                        while (!process.HasExited)
                        {
                            await Task.Delay(500);

                            operation = _operationTracker.GetOperation(operationId);
                            if (operation?.CancellationTokenSource?.Token.IsCancellationRequested == true)
                            {
                                process.Kill();

                                // Mark operation as complete (cancelled) in unified tracker
                                _operationTracker.CompleteOperation(operationId, success: false, error: "Cancelled by user");
                                _currentTrackerOperationId = null;

                                await NotifyProgressAsync(operationId);
                                SaveOperationToState(trackerKey, operationId);

                                return;
                            }

                            var progressData = await _rustProcessHelper.ReadProgressFileAsync<RustCacheProgress>(progressFile);

                            if (progressData != null)
                            {
                                // Calculate aggregate progress across all datasources
                                var currentDirsProcessed = dirsProcessedBefore + progressData.DirectoriesProcessed;
                                var currentBytesDeleted = totalBytesDeleted + (long)progressData.BytesDeleted;
                                var currentFilesDeleted = totalFilesDeleted + (long)progressData.FilesDeleted;
                                var percentComplete = (double)currentDirsProcessed / totalDirectoriesAllDatasources * 100;

                                // Update metrics — forward StageKey/Context from Rust progress
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

                                // Log progress to console when:
                                // 1. Every 5 directories, OR
                                // 2. Every 30 seconds (if stuck on same directory)
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
                            }
                        }
                    });

                    // Read output asynchronously
                    operation = _operationTracker.GetOperation(operationId);
                    var token = operation?.CancellationTokenSource?.Token ?? CancellationToken.None;
                    var outputTask = process.StandardOutput.ReadToEndAsync(token);
                    var errorTask = process.StandardError.ReadToEndAsync(token);

                    await _processManager.WaitForProcessAsync(process, token);
                    await pollTask;

                    var output = await outputTask;
                    var error = await errorTask;

                    // Get updated operation status
                    operation = _operationTracker.GetOperation(operationId);

                    // Exit code 137 = SIGKILL (from cancellation) - don't treat as error
                    if (process.ExitCode == 137 && operation?.Status == OperationStatus.Cancelled)
                    {
                        _logger.LogInformation("Cache clear cancelled by user");
                        return; // Already handled by cancellation logic
                    }

                    if (process.ExitCode != 0)
                    {
                        throw new Exception($"Rust cache_cleaner failed for {dsName} with exit code {process.ExitCode}: {error}");
                    }

                    _logger.LogInformation($"[{GetDeleteModeDisplayName()}] Rust cache cleaner output: {output}");

                    // Read final progress for this datasource
                    var finalProgress = await _rustProcessHelper.ReadProgressFileAsync<RustCacheProgress>(progressFile);
                    if (finalProgress != null)
                    {
                        totalBytesDeleted += (long)finalProgress.BytesDeleted;
                        totalFilesDeleted += (long)finalProgress.FilesDeleted;
                        totalDirsProcessed += finalProgress.DirectoriesProcessed;
                        dirsProcessedBefore = totalDirsProcessed;

                        // Update metrics with totals so far
                        _operationTracker.UpdateMetadata(operationId, (object meta) =>
                        {
                            var metricsToUpdate = (CacheClearingMetrics)meta;
                            metricsToUpdate.DirectoriesProcessed = totalDirsProcessed;
                            metricsToUpdate.BytesDeleted = totalBytesDeleted;
                            metricsToUpdate.FilesDeleted = totalFilesDeleted;
                        });
                    }

                    // Clean up progress file
                    await _rustProcessHelper.DeleteTemporaryFileAsync(progressFile);

                    _logger.LogInformation($"Completed clearing {dsName} cache: {finalProgress?.DirectoriesProcessed ?? 0} directories");
                }
            }

            var datasourceNames = string.Join(", ", validCachePaths.Select(p => p.Name));
            var successMessage = validCachePaths.Count > 1
                ? $"Successfully cleared {totalDirsProcessed} cache directories across {validCachePaths.Count} datasources ({datasourceNames})"
                : $"Successfully cleared {totalDirsProcessed} cache directories";

            // Mark operation as complete in unified tracker
            _operationTracker.CompleteOperation(operationId, success: true);
            _currentTrackerOperationId = null;

            operation = _operationTracker.GetOperation(operationId);
            var duration = operation?.CompletedAt.HasValue == true
                ? (operation.CompletedAt.Value - operation.StartedAt).TotalSeconds
                : 0;
            
            _logger.LogInformation($"Cache clear completed in {duration:F1} seconds - Cleared {totalDirsProcessed} directories across {validCachePaths.Count} datasource(s)");

            // Clear all cached detection results since all cache files were deleted
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
            var gamesDeleted = await dbContext.CachedGameDetections.ExecuteDeleteAsync();
            var servicesDeleted = await dbContext.CachedServiceDetections.ExecuteDeleteAsync();
            var corruptionDeleted = await dbContext.CachedCorruptionDetections.ExecuteDeleteAsync();
            _logger.LogInformation("[CacheClearing] Cleared cached detection results: {Games} games, {Services} services, {Corruption} corruption entries",
                gamesDeleted, servicesDeleted, corruptionDeleted);

            await NotifyProgressAsync(operationId);

            // Send completion notification
            await _notifications.SendOperationCompleteAsync(
                SignalREvents.CacheClearingComplete, operationId,
                success: true, message: successMessage, cancelled: false,
                new { DirectoriesProcessed = totalDirsProcessed, FilesDeleted = totalFilesDeleted, BytesDeleted = totalBytesDeleted, DatasourcesCleared = validCachePaths.Count, Duration = duration });

            SaveOperationToState(trackerKey, operationId);
        }
        catch (OperationCanceledException)
        {
            // Handle cancellation gracefully - this is expected when user cancels
            _logger.LogInformation("Cache clear operation {OperationId} was cancelled by user", operationId);

            // Mark operation as complete (cancelled) in unified tracker
            _operationTracker.CompleteOperation(operationId, success: false, error: "Cancelled by user");
            _currentTrackerOperationId = null;

            await NotifyProgressAsync(operationId);

            // Send cancellation notification
            await _notifications.SendOperationCompleteAsync(
                SignalREvents.CacheClearingComplete, operationId,
                success: false, message: "Cache clear cancelled by user", cancelled: true);

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

            // Mark operation as complete (failed) in unified tracker
            _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);
            _currentTrackerOperationId = null;

            await NotifyProgressAsync(operationId);

            // Send failure notification
            await _notifications.SendOperationCompleteAsync(
                SignalREvents.CacheClearingComplete, operationId,
                success: false, message: $"Cache clear failed: {ex.Message}", cancelled: false,
                new { Error = ex.Message });

            SaveOperationToState(trackerKey, operationId);
        }
    }

    // Helper class for deserializing Rust progress data
    private class RustCacheProgress
    {
        public bool IsProcessing { get; set; }
        public double PercentComplete { get; set; }
        public string Status { get; set; } = "";

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

    private async Task NotifyProgressAsync(string operationId)
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

    private void LoadPersistedOperations()
    {
        try
        {
            var operations = _stateService.GetCacheClearOperations().ToList();
            var modifiedCount = 0;

            foreach (var op in operations)
            {
                // If operation was running when service stopped, mark it as failed
                // (We can't resume Rust processes after restart)
                if (op.Status == "preparing" || op.Status == "running")
                {
                    op.Status = "failed";
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

    private void SaveOperationToState(string trackerKey, string operationId)
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

    private void SaveAllOperationsToState()
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

    public CacheClearProgress? GetOperationStatus(string operationId)
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
    public CacheClearProgress? GetCacheClearStatus(string operationId)
    {
        return GetOperationStatus(operationId);
    }

    /// <summary>
    /// Cancels a cache clear operation (wrapper for CancelOperation)
    /// </summary>
    public bool CancelCacheClear(string operationId)
    {
        return CancelOperation(operationId);
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

    public bool CancelOperation(string operationId)
    {
        _logger.LogInformation($"Cancelling cache clear operation {operationId}");
        return _operationTracker.CancelOperation(operationId);
    }

    /// <summary>
    /// Force kills the Rust process for a cache clear operation.
    /// Used as fallback when graceful cancellation fails.
    /// </summary>
    public async Task<bool> ForceKillOperationAsync(string operationId)
    {
        _logger.LogWarning($"Force killing cache clear operation {operationId}");

        try
        {
            // Use the unified tracker to force kill the operation
            var killed = _operationTracker.ForceKillOperation(operationId);

            if (killed)
            {
                // Wait briefly for the process to exit
                await Task.Delay(500);

                // Notify via SignalR
                await _notifications.SendOperationCompleteAsync(
                    SignalREvents.CacheClearingComplete, operationId,
                    success: false, message: "Cache clear operation force killed", cancelled: true);

                // Extract tracker key from metadata if available
                var operation = _operationTracker.GetOperation(operationId);
                var metadata = operation?.Metadata as CacheClearingMetrics;
                var trackerKey = metadata?.EntityKey ?? "all";
                SaveOperationToState(trackerKey, operationId);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error force killing operation {operationId}");
            return false;
        }

        return false;
    }


    public void SetDeleteMode(string deleteMode)
    {
        if (deleteMode != "preserve" && deleteMode != "full" && deleteMode != "rsync")
        {
            throw new ArgumentException("Delete mode must be 'preserve', 'full', or 'rsync'", nameof(deleteMode));
        }

        _deleteMode = deleteMode;
        _logger.LogInformation($"Cache clear delete mode updated to {deleteMode}");
    }

    public string GetDeleteMode()
    {
        return _deleteMode;
    }

    private string GetDeleteModeDisplayName()
    {
        return _deleteMode switch
        {
            "preserve" => "Preserve",
            "full" => "Remove All",
            "rsync" => "Rsync",
            _ => _deleteMode
        };
    }

    public bool IsRsyncAvailable()
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

            using var process = Process.Start(startInfo);
            if (process == null) return false;

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private void CleanupOldOperations()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);

            // Clean up old operations from state service
            var stateOps = _stateService.GetCacheClearOperations().ToList();
            var toRemove = stateOps
                .Where(op => op.EndTime.HasValue && op.EndTime.Value < cutoff)
                .Select(op => op.Id)
                .ToList();

            if (toRemove.Count > 0)
            {
                foreach (var id in toRemove)
                {
                    _stateService.RemoveCacheClearOperation(id);
                }
                _logger.LogDebug("Cleaned up {Count} old cache clear operations from state", toRemove.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up old operations");
        }
    }
}

public class CacheClearProgress
{
    public string OperationId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
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
