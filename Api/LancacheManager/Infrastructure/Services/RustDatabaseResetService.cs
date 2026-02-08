using System.Diagnostics;
using System.Text.Json;
using LancacheManager.Core.Models;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Service that spawns the rust database reset service and monitors its progress
/// </summary>
public class RustDatabaseResetService
{
    private readonly ILogger<RustDatabaseResetService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly ISignalRNotificationService _notifications;
    private readonly CacheManagementService _cacheManagementService;
    private readonly ProcessManager _processManager;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly IUnifiedOperationTracker _operationTracker;
    private Process? _rustProcess;
    private CancellationTokenSource? _cancellationTokenSource;
    private string? _currentTrackerOperationId;
    private Task? _progressMonitorTask;
    private readonly SemaphoreSlim _startLock = new(1, 1);

    public bool IsProcessing { get; private set; }
    public string? CurrentOperationId => _currentTrackerOperationId;

    public RustDatabaseResetService(
        ILogger<RustDatabaseResetService> logger,
        IPathResolver pathResolver,
        ISignalRNotificationService notifications,
        CacheManagementService cacheManagementService,
        ProcessManager processManager,
        RustProcessHelper rustProcessHelper,
        IUnifiedOperationTracker operationTracker)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _notifications = notifications;
        _cacheManagementService = cacheManagementService;
        _processManager = processManager;
        _rustProcessHelper = rustProcessHelper;
        _operationTracker = operationTracker;
    }

    public class ProgressData
    {
        [System.Text.Json.Serialization.JsonPropertyName("isProcessing")]
        public bool IsProcessing { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("percentComplete")]
        public double PercentComplete { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("tablesCleared")]
        public int TablesCleared { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("totalTables")]
        public int TotalTables { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("filesDeleted")]
        public int FilesDeleted { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Starts database reset operation (wrapper for StartResetAsync)
    /// </summary>
    public Task<bool> StartDatabaseResetAsync()
    {
        return StartResetAsync();
    }

    /// <summary>
    /// Gets the current database reset status including progress data from Rust
    /// </summary>
    public object GetDatabaseResetStatus()
    {
        if (!IsProcessing)
        {
            return new
            {
                isProcessing = false,
                status = "idle"
            };
        }

        // Read progress from Rust progress file
        var operationsDir = _pathResolver.GetOperationsDirectory();
        var progressPath = Path.Combine(operationsDir, "reset_progress.json");

        ProgressData? progress = null;
        try
        {
            if (File.Exists(progressPath))
            {
                var json = File.ReadAllText(progressPath);
                progress = JsonSerializer.Deserialize<ProgressData>(json);
            }
        }
        catch
        {
            // Ignore read errors - file may be being written
        }

        if (progress == null)
        {
            return new
            {
                isProcessing = true,
                status = "starting",
                message = "Starting database reset..."
            };
        }

        return new
        {
            isProcessing = true,
            status = progress.Status,
            percentComplete = progress.PercentComplete,
            message = progress.Message,
            tablesCleared = progress.TablesCleared,
            totalTables = progress.TotalTables,
            filesDeleted = progress.FilesDeleted
        };
    }

    public async Task<bool> StartResetAsync()
    {
        await _startLock.WaitAsync();
        try
        {
            if (IsProcessing)
            {
                _logger.LogWarning("rust database reset is already running");
                return false;
            }

            IsProcessing = true;
        }
        finally
        {
            _startLock.Release();
        }

        try
        {
            _cancellationTokenSource = new CancellationTokenSource();

            // Register the operation with the unified tracker
            _currentTrackerOperationId = _operationTracker.RegisterOperation(
                OperationType.DatabaseReset,
                "Database Reset",
                _cancellationTokenSource
            );

            // Send started event
            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetStarted, new
            {
                OperationId = _currentTrackerOperationId,
                Message = "Starting database reset..."
            });

            var dbPath = _pathResolver.GetDatabasePath();
            var dataDirectory = _pathResolver.GetDataDirectory();
            var operationsDir = _pathResolver.GetOperationsDirectory();
            var progressPath = Path.Combine(operationsDir, "reset_progress.json");
            var rustExecutablePath = _pathResolver.GetRustDatabaseResetPath();

            // Delete old progress file
            if (File.Exists(progressPath))
            {
                File.Delete(progressPath);
            }

            _logger.LogInformation("Starting rust database reset");
            _logger.LogInformation($"Database: {dbPath}");
            _logger.LogInformation($"Progress file: {progressPath}");

            // Send initial progress
            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
            {
                OperationId = _currentTrackerOperationId,
                isProcessing = true,
                percentComplete = 0.0,
                status = "starting",
                message = "Starting database reset...",
                tablesCleared = 0,
                totalTables = 4,
                filesDeleted = 0,
                timestamp = DateTime.UtcNow
            });

            // Wrap Rust process execution in shared lock to prevent concurrent database access
            // This prevents database connection issues and SignalR disconnects during reset
            return await _cacheManagementService.ExecuteWithLockAsync(async () =>
            {
                // Start Rust process
                var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                    rustExecutablePath,
                    $"\"{dbPath}\" \"{dataDirectory}\" \"{progressPath}\"",
                    Path.GetDirectoryName(rustExecutablePath));

                _rustProcess = Process.Start(startInfo);

                if (_rustProcess == null)
                {
                    throw new Exception("Failed to start rust database reset process");
                }

                // Monitor stdout and stderr - track tasks for proper cleanup
                var (stdoutTask, stderrTask) = _rustProcessHelper.CreateOutputMonitoringTasks(_rustProcess, "Rust database reset");

                // Start progress monitoring task
                _progressMonitorTask = Task.Run(async () => await MonitorProgressAsync(progressPath, _cancellationTokenSource.Token));

                // Wait for process to complete with graceful cancellation handling
                await _processManager.WaitForProcessAsync(_rustProcess, _cancellationTokenSource.Token);

                var exitCode = _rustProcess.ExitCode;
                _logger.LogInformation($"rust database reset exited with code {exitCode}");

                // Wait for stdout/stderr reading tasks to complete
                await _rustProcessHelper.WaitForOutputTasksAsync(stdoutTask, stderrTask, TimeSpan.FromSeconds(5));

                if (exitCode == 0)
                {
                    // Give Rust process a moment to write final progress file
                    await Task.Delay(200);

                    // Read and send final progress before stopping monitoring
                    var finalProgress = await ReadProgressFileAsync(progressPath);
                    if (finalProgress != null)
                    {
                        await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, finalProgress);
                    }
                    else
                    {
                        // Fallback completion message
                        await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                        {
                            OperationId = _currentTrackerOperationId,
                            isProcessing = false,
                            percentComplete = 100.0,
                            status = OperationStatus.Completed,
                            message = "Database reset completed successfully",
                            timestamp = DateTime.UtcNow
                        });
                    }

                    if (!string.IsNullOrEmpty(_currentTrackerOperationId))
                    {
                        _operationTracker.CompleteOperation(_currentTrackerOperationId, success: true);
                    }
                }

                // Stop the progress monitoring task after sending final progress
                _cancellationTokenSource.Cancel();
                if (_progressMonitorTask != null)
                {
                    try
                    {
                        await _progressMonitorTask;
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected
                    }
                }

                if (exitCode == 0)
                {
                    return true;
                }
                else
                {
                    await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                    {
                        OperationId = _currentTrackerOperationId,
                        isProcessing = false,
                        percentComplete = 0.0,
                        status = "error",
                        message = $"Database reset failed with exit code {exitCode}",
                        timestamp = DateTime.UtcNow
                    });

                    if (!string.IsNullOrEmpty(_currentTrackerOperationId))
                    {
                        _operationTracker.CompleteOperation(_currentTrackerOperationId, success: false, error: $"Database reset failed with exit code {exitCode}");
                    }

                    return false;
                }
            }, _cancellationTokenSource.Token); // Close ExecuteWithLockAsync lambda
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Database reset was cancelled by user");
            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
            {
                OperationId = _currentTrackerOperationId,
                isProcessing = false,
                percentComplete = 0.0,
                status = OperationStatus.Cancelled,
                message = "Database reset was cancelled",
                timestamp = DateTime.UtcNow
            });

            if (!string.IsNullOrEmpty(_currentTrackerOperationId))
            {
                _operationTracker.CompleteOperation(_currentTrackerOperationId, success: false, error: "Cancelled by user");
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting rust database reset");
            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
            {
                OperationId = _currentTrackerOperationId,
                isProcessing = false,
                percentComplete = 0.0,
                status = "error",
                message = $"Database reset failed: {ex.Message}",
                timestamp = DateTime.UtcNow
            });

            if (!string.IsNullOrEmpty(_currentTrackerOperationId))
            {
                _operationTracker.CompleteOperation(_currentTrackerOperationId, success: false, error: ex.Message);
            }

            return false;
        }
        finally
        {
            IsProcessing = false;
            _currentTrackerOperationId = null;
            _cancellationTokenSource?.Dispose();
            _rustProcess?.Dispose();
        }
    }

    private async Task MonitorProgressAsync(string progressPath, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(500, cancellationToken); // Poll every 500ms for faster updates

                var progress = await ReadProgressFileAsync(progressPath);
                if (progress != null)
                {
                    // Send progress update via SignalR
                    await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, progress);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error monitoring rust database reset progress");
        }
    }

    private async Task<ProgressData?> ReadProgressFileAsync(string progressPath)
    {
        return await _rustProcessHelper.ReadProgressFileAsync<ProgressData>(progressPath);
    }
}