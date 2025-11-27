using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Serialization;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Application.Services;

public class CacheClearingService : IHostedService
{
    private readonly ILogger<CacheClearingService> _logger;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly IConfiguration _configuration;
    private readonly IPathResolver _pathResolver;
    private readonly StateRepository _stateService;
    private readonly ProcessManager _processManager;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly ConcurrentDictionary<string, CacheClearOperation> _operations = new();
    private readonly string _cachePath;
    private Timer? _cleanupTimer;
    private string _deleteMode;

    public CacheClearingService(
        ILogger<CacheClearingService> logger,
        IHubContext<DownloadHub> hubContext,
        IConfiguration configuration,
        IPathResolver pathResolver,
        StateRepository stateService,
        ProcessManager processManager,
        RustProcessHelper rustProcessHelper)
    {
        _logger = logger;
        _hubContext = hubContext;
        _configuration = configuration;
        _pathResolver = pathResolver;
        _stateService = stateService;
        _processManager = processManager;
        _rustProcessHelper = rustProcessHelper;

        // Read delete mode from configuration
        _deleteMode = configuration.GetValue<string>("CacheClear:DeleteMode", "preserve") ?? "preserve";

        // Determine cache path - check most likely locations first
        var possiblePaths = new List<string> { _pathResolver.GetCacheDirectory() };

        // Add config override if different
        var configPath = configuration["LanCache:CachePath"];
        if (!string.IsNullOrEmpty(configPath) && !possiblePaths.Contains(configPath))
        {
            possiblePaths.Insert(0, configPath);  // Config path takes priority
        }

        foreach (var path in possiblePaths)
        {
            if (Directory.Exists(path))
            {
                // Check if it has hex directories (00-ff) to confirm it's a cache
                var dirs = Directory.GetDirectories(path);
                if (dirs.Any(d =>
                {
                    var name = Path.GetFileName(d);
                    return name.Length == 2 && IsHex(name);
                }))
                {
                    _cachePath = path;
                    _logger.LogInformation($"Detected cache path: {_cachePath}");
                    break;
                }
            }
        }

        // If no valid cache found, use path resolver default
        if (string.IsNullOrEmpty(_cachePath))
        {
            _cachePath = _pathResolver.GetCacheDirectory();
            _logger.LogWarning($"No cache detected, using configured path: {_cachePath}");
        }

        _logger.LogInformation($"CacheClearingService initialized with cache path: {_cachePath}");
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

    public Task<string> StartCacheClearAsync()
    {
        var operationId = Guid.NewGuid().ToString();
        var operation = new CacheClearOperation
        {
            Id = operationId,
            StartTime = DateTime.UtcNow,
            Status = ClearStatus.Preparing,
            StatusMessage = "Initializing cache clear...",
            CancellationTokenSource = new CancellationTokenSource()
        };

        _operations[operationId] = operation;
        SaveOperationToState(operation);

        _logger.LogInformation($"Starting cache clear operation {operationId}");

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

            // Check write permissions for cache directory
            if (!_pathResolver.IsCacheDirectoryWritable())
            {
                operation.Status = ClearStatus.Failed;
                operation.Error = $"Cannot write to cache directory: {_cachePath}. Directory is mounted read-only. Remove :ro from the cache volume mount in docker-compose.yml.";
                operation.EndTime = DateTime.UtcNow;
                _logger.LogWarning("Cache clear operation {OperationId} failed: {Error}", operation.Id, operation.Error);
                await NotifyProgress(operation);

                // Send completion notification
                await _hubContext.Clients.All.SendAsync("CacheClearComplete", new
                {
                    success = false,
                    message = operation.Error,
                    error = operation.Error,
                    timestamp = DateTime.UtcNow
                });

                SaveOperationToState(operation);
                return;
            }

            if (!Directory.Exists(_cachePath))
            {
                operation.Status = ClearStatus.Failed;
                operation.Error = $"Cache path does not exist: {_cachePath}";
                operation.EndTime = DateTime.UtcNow;
                _logger.LogWarning("Cache clear operation {OperationId} failed: {Error}", operation.Id, operation.Error);
                await NotifyProgress(operation);

                // Send completion notification
                await _hubContext.Clients.All.SendAsync("CacheClearComplete", new
                {
                    success = false,
                    message = operation.Error,
                    error = operation.Error,
                    timestamp = DateTime.UtcNow
                });

                SaveOperationToState(operation);
                return;
            }

            // Validate cache directory has cache subdirectories
            var cacheSubdirs = Directory.GetDirectories(_cachePath)
                .Where(d =>
                {
                    var name = Path.GetFileName(d);
                    return name.Length == 2 && IsHex(name);
                }).ToList();

            if (!cacheSubdirs.Any())
            {
                operation.Status = ClearStatus.Failed;
                operation.Error = $"No cache directories found in {_cachePath}";
                operation.EndTime = DateTime.UtcNow;
                _logger.LogWarning("Cache clear operation {OperationId} failed: {Error}", operation.Id, operation.Error);
                await NotifyProgress(operation);

                // Send completion notification
                await _hubContext.Clients.All.SendAsync("CacheClearComplete", new
                {
                    success = false,
                    message = operation.Error,
                    error = operation.Error,
                    timestamp = DateTime.UtcNow
                });

                SaveOperationToState(operation);
                return;
            }

            // Use Rust binary for fast cache clearing
            var dataDir = _pathResolver.GetDataDirectory();
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
            }

            var progressFile = Path.Combine(dataDir, $"cache_clear_progress_{operation.Id}.json");
            var rustBinaryPath = _pathResolver.GetRustCacheCleanerPath();

            if (!File.Exists(rustBinaryPath))
            {
                operation.Status = ClearStatus.Failed;
                operation.Error = $"Rust cache_cleaner binary not found at {rustBinaryPath}";
                operation.EndTime = DateTime.UtcNow;
                await NotifyProgress(operation);

                // Send completion notification
                await _hubContext.Clients.All.SendAsync("CacheClearComplete", new
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

            // Build arguments - Rust auto-detects optimal thread count
            var arguments = $"\"{_cachePath}\" \"{progressFile}\" {_deleteMode}";

            var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                rustBinaryPath,
                arguments);

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new Exception("Failed to start Rust cache_cleaner process");
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
                            operation.DirectoriesProcessed = progressData.DirectoriesProcessed;
                            operation.TotalDirectories = progressData.TotalDirectories;
                            operation.BytesDeleted = (long)progressData.BytesDeleted;
                            operation.FilesDeleted = (long)progressData.FilesDeleted;
                            operation.PercentComplete = progressData.PercentComplete;
                            operation.StatusMessage = progressData.Message;

                            await NotifyProgress(operation);

                            // Log progress to console when:
                            // 1. Every 5 directories, OR
                            // 2. Every 30 seconds (if stuck on same directory)
                            var timeSinceLastLog = DateTime.UtcNow - lastLogTime;
                            var dirsChanged = operation.DirectoriesProcessed != lastLoggedDirs;
                            var shouldLog =
                                (dirsChanged && operation.DirectoriesProcessed % 5 == 0) ||
                                (dirsChanged && timeSinceLastLog.TotalSeconds >= 3) ||
                                (!dirsChanged && timeSinceLastLog.TotalSeconds >= 30);

                            if (shouldLog)
                            {
                                var activeInfo = progressData.ActiveCount > 0
                                    ? $" | Active: {progressData.ActiveCount} [{string.Join(", ", progressData.ActiveDirectories)}]"
                                    : "";
                                _logger.LogInformation($"Cache Clear Progress: {operation.PercentComplete:F1}% complete - {operation.DirectoriesProcessed}/{operation.TotalDirectories} directories cleared{activeInfo}");
                                lastLoggedDirs = operation.DirectoriesProcessed;
                                lastLogTime = DateTime.UtcNow;
                            }

                            if (operation.DirectoriesProcessed % 10 == 0)
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
                    throw new Exception($"Rust cache_cleaner failed with exit code {process.ExitCode}: {error}");
                }

                _logger.LogInformation($"Rust cache cleaner output: {output}");
                if (!string.IsNullOrEmpty(error))
                {
                }

                // Read final progress
                var finalProgress = await _rustProcessHelper.ReadProgressFileAsync<RustCacheProgress>(progressFile);
                if (finalProgress != null)
                {
                    operation.BytesDeleted = (long)finalProgress.BytesDeleted;
                    operation.FilesDeleted = (long)finalProgress.FilesDeleted;
                    operation.DirectoriesProcessed = finalProgress.DirectoriesProcessed;
                    operation.TotalDirectories = finalProgress.TotalDirectories;
                }

                // Clean up progress file and process reference
                await _rustProcessHelper.DeleteTemporaryFileAsync(progressFile);
                operation.RustProcess = null;

                operation.Status = ClearStatus.Completed;
                operation.StatusMessage = $"Successfully cleared {operation.DirectoriesProcessed} cache directories";
                operation.EndTime = DateTime.UtcNow;
                operation.PercentComplete = 100;

                var duration = operation.EndTime.Value - operation.StartTime;
                _logger.LogInformation($"Cache clear completed in {duration.TotalSeconds:F1} seconds - Cleared {operation.DirectoriesProcessed} directories");

                await NotifyProgress(operation);

                // Send completion notification
                await _hubContext.Clients.All.SendAsync("CacheClearComplete", new
                {
                    success = true,
                    message = $"Successfully cleared {operation.DirectoriesProcessed} cache directories",
                    directoriesProcessed = operation.DirectoriesProcessed,
                    filesDeleted = operation.FilesDeleted,
                    bytesDeleted = operation.BytesDeleted,
                    duration = duration.TotalSeconds,
                    timestamp = DateTime.UtcNow
                });

                SaveOperationToState(operation);
            }
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
            await _hubContext.Clients.All.SendAsync("CacheClearComplete", new
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
            await _hubContext.Clients.All.SendAsync("CacheClearComplete", new
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
                Status = operation.Status.ToString(),
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

            await _hubContext.Clients.All.SendAsync("CacheClearProgress", progress);

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
                    var status = Enum.Parse<ClearStatus>(op.Status);
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
            var stateOp = new StateRepository.CacheClearOperation
            {
                Id = operation.Id,
                Status = operation.Status.ToString(),
                Message = operation.StatusMessage,
                Progress = (int)operation.PercentComplete,
                StartTime = operation.StartTime,
                EndTime = operation.EndTime,
                Error = operation.Error
            };

            _stateService.UpdateState(state =>
            {
                var existing = state.CacheClearOperations.FirstOrDefault(o => o.Id == operation.Id);
                if (existing != null)
                {
                    state.CacheClearOperations.Remove(existing);
                }
                state.CacheClearOperations.Add(stateOp);
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
            var operations = _operations.Values.Select(op => new StateRepository.CacheClearOperation
            {
                Id = op.Id,
                Status = op.Status.ToString(),
                Message = op.StatusMessage,
                Progress = (int)op.PercentComplete,
                StartTime = op.StartTime,
                EndTime = op.EndTime,
                Error = op.Error
            }).ToList();

            _stateService.UpdateState(state =>
            {
                state.CacheClearOperations = operations;
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
                Status = operation.Status.ToString(),
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
            Status = op.Status.ToString(),
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
                await _hubContext.Clients.All.SendAsync("CacheClearComplete", new
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