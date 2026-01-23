using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Serialization;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Platform;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using ModelCacheClearOperation = LancacheManager.Models.CacheClearOperation;

namespace LancacheManager.Core.Services;

public class CacheClearingService : IHostedService
{
    private readonly ILogger<CacheClearingService> _logger;
    private readonly ISignalRNotificationService _notifications;
    private readonly IPathResolver _pathResolver;
    private readonly StateService _stateService;
    private readonly ProcessManager _processManager;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly DatasourceService _datasourceService;
    private readonly ConcurrentDictionary<string, CacheClearOperation> _operations = new();
    private readonly string _cachePath;
    private Timer? _cleanupTimer;
    private string _deleteMode;

    public CacheClearingService(
        ILogger<CacheClearingService> logger,
        ISignalRNotificationService notifications,
        IConfiguration configuration,
        IPathResolver pathResolver,
        StateService stateService,
        ProcessManager processManager,
        RustProcessHelper rustProcessHelper,
        DatasourceService datasourceService)
    {
        _logger = logger;
        _notifications = notifications;
        _pathResolver = pathResolver;
        _stateService = stateService;
        _processManager = processManager;
        _rustProcessHelper = rustProcessHelper;
        _datasourceService = datasourceService;

        _deleteMode = "preserve";

        // Use DatasourceService for default cache path
        var defaultDatasource = _datasourceService.GetDefaultDatasource();
        if (defaultDatasource != null)
        {
            _cachePath = defaultDatasource.CachePath;
            _logger.LogInformation("Using cache path from default datasource: {CachePath}", _cachePath);
        }
        else
        {
            // Fallback to legacy configuration
            var possiblePaths = new List<string> { _pathResolver.GetCacheDirectory() };

            var configPath = configuration["LanCache:CachePath"];
            if (!string.IsNullOrEmpty(configPath) && !possiblePaths.Contains(configPath))
            {
                possiblePaths.Insert(0, configPath);
            }

            foreach (var path in possiblePaths)
            {
                if (Directory.Exists(path))
                {
                    var dirs = Directory.GetDirectories(path);
                    if (dirs.Any(d =>
                    {
                        var name = Path.GetFileName(d);
                        return name.Length == 2 && IsHex(name);
                    }))
                    {
                        _cachePath = path;
                        _logger.LogInformation("Detected cache path: {CachePath}", _cachePath);
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(_cachePath))
            {
                _cachePath = _pathResolver.GetCacheDirectory();
                _logger.LogWarning("No cache detected, using configured path: {CachePath}", _cachePath);
            }
        }

        _logger.LogInformation("CacheClearingService initialized with {Count} datasource(s)", _datasourceService.DatasourceCount);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        LoadPersistedOperations();
        _cleanupTimer = new Timer(CleanupOldOperations, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer?.Dispose();
        SaveAllOperationsToState();

        foreach (var operation in _operations.Values)
        {
            operation.CancellationTokenSource?.Cancel();
        }

        return Task.CompletedTask;
    }

    public Task<string> StartCacheClearAsync(string? datasourceName = null)
    {
        var operationId = Guid.NewGuid().ToString();
        var operation = new CacheClearOperation
        {
            Id = operationId,
            StartTime = DateTime.UtcNow,
            Status = ClearStatus.Preparing,
            StatusMessage = datasourceName != null
                ? $"Initializing cache clear for {datasourceName}..."
                : "Initializing cache clear...",
            CancellationTokenSource = new CancellationTokenSource(),
            DatasourceName = datasourceName
        };

        _operations[operationId] = operation;
        SaveOperationToState(operation);

        _logger.LogInformation($"Starting cache clear operation {operationId}" +
            (datasourceName != null ? $" for datasource: {datasourceName}" : " for all datasources"));

        // Start the clear operation on a background thread
        _ = Task.Run(async () => await ExecuteCacheClear(operation), operation.CancellationTokenSource.Token);

        return Task.FromResult(operationId);
    }

    private async Task ExecuteCacheClear(CacheClearOperation operation)
    {
        try
        {
            _logger.LogInformation($"Executing cache clear operation {operation.Id}");

            operation.Status = ClearStatus.Preparing;
            operation.StatusMessage = "Checking permissions...";
            await NotifyProgress(operation);

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
                    "The lancache container usually runs as UID/GID 33:33 (www-data).";

                _logger.LogWarning("[CacheClear] Permission check failed: {Error}", errorMessage);
                operation.Status = ClearStatus.Failed;
                operation.Error = errorMessage;
                operation.EndTime = DateTime.UtcNow;
                await NotifyProgress(operation);

                await _notifications.NotifyAllAsync(SignalREvents.CacheClearComplete, new
                {
                    success = false,
                    message = errorMessage,
                    error = errorMessage,
                    timestamp = DateTime.UtcNow
                });

                SaveOperationToState(operation);
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
            if (!string.IsNullOrEmpty(operation.DatasourceName))
            {
                // Filter to specific datasource
                datasources = allDatasources
                    .Where(ds => ds.Name.Equals(operation.DatasourceName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (!datasources.Any())
                {
                    operation.Status = ClearStatus.Failed;
                    operation.Error = $"Datasource '{operation.DatasourceName}' not found";
                    operation.EndTime = DateTime.UtcNow;
                    await NotifyProgress(operation);
                    SaveOperationToState(operation);
                    return;
                }

                _logger.LogInformation($"Cache clear will process specific datasource: {operation.DatasourceName}");
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
                operation.Status = ClearStatus.Failed;
                operation.Error = "No cache directories found in any datasource";
                operation.EndTime = DateTime.UtcNow;
                _logger.LogWarning("Cache clear operation {OperationId} failed: {Error}", operation.Id, operation.Error);
                await NotifyProgress(operation);

                await _notifications.NotifyAllAsync(SignalREvents.CacheClearComplete, new
                {
                    success = false,
                    message = operation.Error,
                    error = operation.Error,
                    timestamp = DateTime.UtcNow
                });

                SaveOperationToState(operation);
                return;
            }

            // Calculate total directories across all datasources
            var totalDirectoriesAllDatasources = validCachePaths.Sum(p => p.DirCount);
            operation.TotalDirectories = totalDirectoriesAllDatasources;

            _logger.LogInformation($"Total cache directories to clear across all datasources: {totalDirectoriesAllDatasources}");

            // Use Rust binary for fast cache clearing
            var operationsDir = _pathResolver.GetOperationsDirectory();
            var rustBinaryPath = _pathResolver.GetRustCacheCleanerPath();

            if (!File.Exists(rustBinaryPath))
            {
                operation.Status = ClearStatus.Failed;
                operation.Error = $"Rust cache_cleaner binary not found at {rustBinaryPath}";
                operation.EndTime = DateTime.UtcNow;
                await NotifyProgress(operation);

                // Send completion notification
                await _notifications.NotifyAllAsync(SignalREvents.CacheClearComplete, new
                {
                    success = false,
                    message = operation.Error,
                    error = operation.Error,
                    timestamp = DateTime.UtcNow
                });

                SaveOperationToState(operation);
                return;
            }

            _logger.LogInformation($"Using Rust cache cleaner: {rustBinaryPath}");

            operation.Status = ClearStatus.Running;
            await NotifyProgress(operation);
            SaveOperationToState(operation);

            // Track aggregate totals across all datasources
            var totalBytesDeleted = 0L;
            var totalFilesDeleted = 0L;
            var totalDirsProcessed = 0;
            var dirsProcessedBefore = 0;

            // Process each datasource cache path sequentially
            for (var dsIndex = 0; dsIndex < validCachePaths.Count; dsIndex++)
            {
                var (dsName, cachePath, dirCount) = validCachePaths[dsIndex];
                var progressFile = Path.Combine(operationsDir, $"cache_clear_progress_{operation.Id}_{dsIndex}.json");

                _logger.LogInformation($"Clearing cache for datasource {dsName} ({dsIndex + 1}/{validCachePaths.Count}): {cachePath}");
                operation.StatusMessage = $"Clearing {dsName} cache ({dsIndex + 1}/{validCachePaths.Count})...";
                await NotifyProgress(operation);

                // Check for cancellation before starting each datasource
                if (operation.CancellationTokenSource?.Token.IsCancellationRequested == true)
                {
                    operation.Status = ClearStatus.Cancelled;
                    operation.StatusMessage = "Cancelled by user";
                    operation.EndTime = DateTime.UtcNow;
                    await NotifyProgress(operation);
                    SaveOperationToState(operation);
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
                    operation.RustProcess = process;

                    // Track last logged values for console output
                    var lastLoggedDirs = 0;
                    var lastLogTime = DateTime.UtcNow;

                    // Poll the progress file while the process runs
                    var pollTask = Task.Run(async () =>
                    {
                        while (!process.HasExited)
                        {
                            await Task.Delay(500);

                            if (operation.CancellationTokenSource?.Token.IsCancellationRequested == true)
                            {
                                process.Kill();
                                operation.Status = ClearStatus.Cancelled;
                                operation.StatusMessage = "Cancelled by user";
                                operation.EndTime = DateTime.UtcNow;
                                await NotifyProgress(operation);
                                SaveOperationToState(operation);
                                return;
                            }

                            var progressData = await _rustProcessHelper.ReadProgressFileAsync<RustCacheProgress>(progressFile);

                            if (progressData != null)
                            {
                                // Calculate aggregate progress across all datasources
                                operation.DirectoriesProcessed = dirsProcessedBefore + progressData.DirectoriesProcessed;
                                operation.BytesDeleted = totalBytesDeleted + (long)progressData.BytesDeleted;
                                operation.FilesDeleted = totalFilesDeleted + (long)progressData.FilesDeleted;
                                operation.PercentComplete = (double)operation.DirectoriesProcessed / operation.TotalDirectories * 100;
                                operation.StatusMessage = $"[{GetDeleteModeDisplayName()}] {progressData.Message}";

                                await NotifyProgress(operation);

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
                                    _logger.LogInformation($"[{GetDeleteModeDisplayName()}] Cache Clear Progress: {operation.PercentComplete:F1}% complete - {operation.DirectoriesProcessed}/{operation.TotalDirectories} directories cleared{activeInfo}");
                                    lastLoggedDirs = progressData.DirectoriesProcessed;
                                    lastLogTime = DateTime.UtcNow;
                                }

                                if (progressData.DirectoriesProcessed % 10 == 0)
                                {
                                    SaveOperationToState(operation);
                                }
                            }
                        }
                    });

                    // Read output asynchronously
                    var outputTask = process.StandardOutput.ReadToEndAsync(operation.CancellationTokenSource?.Token ?? CancellationToken.None);
                    var errorTask = process.StandardError.ReadToEndAsync(operation.CancellationTokenSource?.Token ?? CancellationToken.None);

                    await _processManager.WaitForProcessAsync(process, operation.CancellationTokenSource?.Token ?? CancellationToken.None);
                    await pollTask;

                    var output = await outputTask;
                    var error = await errorTask;

                    // Exit code 137 = SIGKILL (from cancellation) - don't treat as error
                    if (process.ExitCode == 137 && operation.Status == ClearStatus.Cancelled)
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
                    }

                    // Clean up progress file and process reference
                    await _rustProcessHelper.DeleteTemporaryFileAsync(progressFile);
                    operation.RustProcess = null;

                    _logger.LogInformation($"Completed clearing {dsName} cache: {finalProgress?.DirectoriesProcessed ?? 0} directories");
                }
            }

            // Update final totals
            operation.BytesDeleted = totalBytesDeleted;
            operation.FilesDeleted = totalFilesDeleted;
            operation.DirectoriesProcessed = totalDirsProcessed;

            operation.Status = ClearStatus.Completed;
            var datasourceNames = string.Join(", ", validCachePaths.Select(p => p.Name));
            operation.StatusMessage = validCachePaths.Count > 1
                ? $"Successfully cleared {operation.DirectoriesProcessed} cache directories across {validCachePaths.Count} datasources ({datasourceNames})"
                : $"Successfully cleared {operation.DirectoriesProcessed} cache directories";
            operation.EndTime = DateTime.UtcNow;
            operation.PercentComplete = 100;

            var duration = operation.EndTime.Value - operation.StartTime;
            _logger.LogInformation($"Cache clear completed in {duration.TotalSeconds:F1} seconds - Cleared {operation.DirectoriesProcessed} directories across {validCachePaths.Count} datasource(s)");

            await NotifyProgress(operation);

            // Send completion notification
            await _notifications.NotifyAllAsync(SignalREvents.CacheClearComplete, new
            {
                success = true,
                message = operation.StatusMessage,
                directoriesProcessed = operation.DirectoriesProcessed,
                filesDeleted = operation.FilesDeleted,
                bytesDeleted = operation.BytesDeleted,
                datasourcesCleared = validCachePaths.Count,
                duration = duration.TotalSeconds,
                timestamp = DateTime.UtcNow
            });

            SaveOperationToState(operation);
        }
        catch (OperationCanceledException)
        {
            // Handle cancellation gracefully - this is expected when user cancels
            _logger.LogInformation("Cache clear operation {OperationId} was cancelled by user", operation.Id);

            operation.Status = ClearStatus.Cancelled;
            operation.StatusMessage = "Cancelled by user";
            operation.EndTime = DateTime.UtcNow;
            operation.RustProcess = null;
            await NotifyProgress(operation);

            // Send cancellation notification
            await _notifications.NotifyAllAsync(SignalREvents.CacheClearComplete, new
            {
                success = false,
                message = "Cache clear cancelled by user",
                cancelled = true,
                operationId = operation.Id,
                timestamp = DateTime.UtcNow
            });

            SaveOperationToState(operation);
        }
        catch (Exception ex)
        {
            // Distinguish between expected failures and unexpected errors
            var isExpectedFailure = ex.Message.Contains("No cache directories found") ||
                                   ex.Message.Contains("Cache path does not exist");

            if (isExpectedFailure)
            {
                _logger.LogWarning("Cache clear operation {OperationId} failed: {Message}", operation.Id, ex.Message);
            }
            else
            {
                _logger.LogError(ex, "Error in cache clear operation {OperationId}", operation.Id);
            }

            operation.Status = ClearStatus.Failed;
            operation.Error = ex.Message;
            operation.StatusMessage = $"Failed: {ex.Message}";
            operation.EndTime = DateTime.UtcNow;
            operation.RustProcess = null;
            await NotifyProgress(operation);

            // Send failure notification
            await _notifications.NotifyAllAsync(SignalREvents.CacheClearComplete, new
            {
                success = false,
                message = $"Cache clear failed: {ex.Message}",
                error = ex.Message,
                timestamp = DateTime.UtcNow
            });

            SaveOperationToState(operation);
        }
    }

    // Helper class for deserializing Rust progress data
    private class RustCacheProgress
    {
        public bool IsProcessing { get; set; }
        public double PercentComplete { get; set; }
        public string Status { get; set; } = "";
        public string Message { get; set; } = "";
        public int DirectoriesProcessed { get; set; }
        public int TotalDirectories { get; set; }
        public ulong BytesDeleted { get; set; }
        public ulong FilesDeleted { get; set; }
        public List<string> ActiveDirectories { get; set; } = new();
        public int ActiveCount { get; set; }
    }

    private bool IsHex(string value)
    {
        return value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
    }

    private async Task NotifyProgress(CacheClearOperation operation)
    {
        try
        {
            var progress = new CacheClearProgress
            {
                OperationId = operation.Id,
                Status = operation.Status.ToString().ToLowerInvariant(),
                StatusMessage = operation.StatusMessage,
                StartTime = operation.StartTime,
                EndTime = operation.EndTime,
                DirectoriesProcessed = operation.DirectoriesProcessed,
                TotalDirectories = operation.TotalDirectories,
                BytesDeleted = operation.BytesDeleted,
                FilesDeleted = operation.FilesDeleted,
                Error = operation.Error,
                PercentComplete = operation.PercentComplete
            };

            await _notifications.NotifyAllAsync(SignalREvents.CacheClearProgress, progress);

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

            foreach (var op in operations)
            {
                // Keep all operations from last 24 hours for status queries
                if (op.StartTime > DateTime.UtcNow.AddHours(-24))
                {
                    var status = Enum.Parse<ClearStatus>(op.Status, ignoreCase: true);
                    var error = op.Error;
                    var endTime = op.EndTime;
                    var statusMessage = op.Message;

                    // If operation was running when service stopped, mark it as failed
                    // (We can't resume Rust processes after restart)
                    if (status == ClearStatus.Preparing || status == ClearStatus.Running)
                    {
                        status = ClearStatus.Failed;
                        error = "Operation interrupted by service restart";
                        endTime = DateTime.UtcNow;
                        statusMessage = error;
                        _logger.LogWarning($"Cache clear operation {op.Id} was interrupted by restart, marking as failed");
                    }

                    var cacheClearOp = new CacheClearOperation
                    {
                        Id = op.Id,
                        StartTime = op.StartTime,
                        EndTime = endTime,
                        Status = status,
                        StatusMessage = statusMessage,
                        Error = error,
                        PercentComplete = op.Progress
                    };
                    _operations[op.Id] = cacheClearOp;

                    // Update state if we modified it
                    if (status == ClearStatus.Failed && op.Status != "Failed")
                    {
                        SaveOperationToState(cacheClearOp);
                    }
                }
            }

            _logger.LogInformation($"Loaded {_operations.Count} persisted cache clear operations");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load persisted operations");
        }
    }

    private void SaveOperationToState(CacheClearOperation operation)
    {
        try
        {
            var stateOp = new ModelCacheClearOperation
            {
                Id = operation.Id,
                Status = operation.Status.ToString().ToLowerInvariant(),
                Message = operation.StatusMessage,
                Progress = (int)operation.PercentComplete,
                StartTime = operation.StartTime,
                EndTime = operation.EndTime,
                Error = operation.Error
            };

            _stateService.UpdateCacheClearOperations(operations =>
            {
                var existing = operations.FirstOrDefault(o => o.Id == operation.Id);
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
            var newOperations = _operations.Values.Select(op => new ModelCacheClearOperation
            {
                Id = op.Id,
                Status = op.Status.ToString().ToLowerInvariant(),
                Message = op.StatusMessage,
                Progress = (int)op.PercentComplete,
                StartTime = op.StartTime,
                EndTime = op.EndTime,
                Error = op.Error
            }).ToList();

            _stateService.UpdateCacheClearOperations(operations =>
            {
                operations.Clear();
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
        if (_operations.TryGetValue(operationId, out var operation))
        {
            return new CacheClearProgress
            {
                OperationId = operation.Id,
                Status = operation.Status.ToString().ToLowerInvariant(),
                StatusMessage = operation.StatusMessage,
                StartTime = operation.StartTime,
                EndTime = operation.EndTime,
                DirectoriesProcessed = operation.DirectoriesProcessed,
                TotalDirectories = operation.TotalDirectories,
                BytesDeleted = operation.BytesDeleted,
                FilesDeleted = operation.FilesDeleted,
                Error = operation.Error,
                PercentComplete = operation.PercentComplete
            };
        }

        return null;
    }

    /// <summary>
    /// Gets all active cache clear operations (wrapper for GetAllOperations)
    /// </summary>
    public List<CacheClearProgress> GetActiveOperations()
    {
        return GetAllOperations();
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
        return _operations.Values.Select(op => new CacheClearProgress
        {
            OperationId = op.Id,
            Status = op.Status.ToString().ToLowerInvariant(),
            StatusMessage = op.StatusMessage,
            StartTime = op.StartTime,
            EndTime = op.EndTime,
            DirectoriesProcessed = op.DirectoriesProcessed,
            TotalDirectories = op.TotalDirectories,
            BytesDeleted = op.BytesDeleted,
            FilesDeleted = op.FilesDeleted,
            Error = op.Error,
            PercentComplete = op.PercentComplete
        }).ToList();
    }

    public bool CancelOperation(string operationId)
    {
        if (_operations.TryGetValue(operationId, out var operation))
        {
            _logger.LogInformation($"Cancelling cache clear operation {operationId}");

            operation.CancellationTokenSource?.Cancel();

            // Don't immediately mark as cancelled - let the operation handle it
            // This prevents race conditions

            return true;
        }

        return false;
    }

    /// <summary>
    /// Force kills the Rust process for a cache clear operation.
    /// Used as fallback when graceful cancellation fails.
    /// </summary>
    public async Task<bool> ForceKillOperation(string operationId)
    {
        if (_operations.TryGetValue(operationId, out var operation))
        {
            _logger.LogWarning($"Force killing cache clear operation {operationId}");

            try
            {
                // First cancel the token to signal the polling task to stop
                operation.CancellationTokenSource?.Cancel();

                // Kill the Rust process if it exists and is still running
                if (operation.RustProcess != null && !operation.RustProcess.HasExited)
                {
                    _logger.LogWarning($"Killing Rust cache_cleaner process (PID: {operation.RustProcess.Id}) for operation {operationId}");
                    operation.RustProcess.Kill(entireProcessTree: true);

                    // Wait briefly for the process to exit
                    await Task.Delay(500);

                    if (!operation.RustProcess.HasExited)
                    {
                        _logger.LogError($"Process did not exit after Kill() for operation {operationId}");
                    }
                }

                // Update operation status
                operation.Status = ClearStatus.Cancelled;
                operation.StatusMessage = "Force killed by user";
                operation.EndTime = DateTime.UtcNow;

                // Notify via SignalR
                await _notifications.NotifyAllAsync(SignalREvents.CacheClearComplete, new
                {
                    success = false,
                    message = "Cache clear operation force killed",
                    operationId,
                    cancelled = true,
                    timestamp = DateTime.UtcNow
                });

                SaveOperationToState(operation);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error force killing operation {operationId}");
                return false;
            }
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
        // Rsync only available on Linux (check using existing IPathResolver detection)
        if (_pathResolver is not LinuxPathResolver)
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

    private void CleanupOldOperations(object? state)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            var toRemove = _operations
                .Where(kvp => kvp.Value.EndTime.HasValue && kvp.Value.EndTime.Value < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in toRemove)
            {
                if (_operations.TryRemove(key, out var operation))
                {
                    operation.CancellationTokenSource?.Dispose();
                }
            }

            if (toRemove.Count > 0)
            {

                // Remove from state service as well
                foreach (var key in toRemove)
                {
                    _stateService.RemoveCacheClearOperation(key);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up old operations");
        }
    }
}

public class CacheClearOperation
{
    public string Id { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public ClearStatus Status { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public string? Error { get; set; }
    public int TotalDirectories { get; set; }
    public int DirectoriesProcessed { get; set; }
    public long BytesDeleted { get; set; }
    public long FilesDeleted { get; set; }
    public double PercentComplete { get; set; }

    /// <summary>
    /// Optional: Name of the specific datasource to clear (null = all datasources)
    /// </summary>
    public string? DatasourceName { get; set; }

    [JsonIgnore]
    public CancellationTokenSource? CancellationTokenSource { get; set; }

    [JsonIgnore]
    public Process? RustProcess { get; set; }
}

public enum ClearStatus
{
    Preparing,
    Running,
    Completed,
    Failed,
    Cancelled
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
