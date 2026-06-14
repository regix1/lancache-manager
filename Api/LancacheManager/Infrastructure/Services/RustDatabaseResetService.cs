using System.Text.Json;
using LancacheManager.Models;
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
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly IUnifiedOperationTracker _operationTracker;
    private CancellationTokenSource? _cancellationTokenSource;
    private Guid? _currentTrackerOperationId;
    private readonly SemaphoreSlim _startLock = new(1, 1);

    public bool IsProcessing { get; private set; }
    public Guid? CurrentOperationId => _currentTrackerOperationId;

    public RustDatabaseResetService(
        ILogger<RustDatabaseResetService> logger,
        IPathResolver pathResolver,
        ISignalRNotificationService notifications,
        CacheManagementService cacheManagementService,
        RustProcessHelper rustProcessHelper,
        IUnifiedOperationTracker operationTracker)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _notifications = notifications;
        _cacheManagementService = cacheManagementService;
        _rustProcessHelper = rustProcessHelper;
        _operationTracker = operationTracker;
    }

    /// <summary>
    /// Strongly-typed SignalR progress payload for database reset. Built from the polled Rust
    /// <see cref="ProgressData"/> so we never forward that raw DTO over SignalR: its
    /// <c>[JsonPropertyName("stage_key")]</c> would override the global camelCase policy and the
    /// frontend would receive <c>stage_key</c> for db-reset while every other op emits <c>stageKey</c>
    /// (D-rust-2). This record carries no JsonPropertyName attributes, so the global camelCase policy
    /// serializes <c>StageKey</c> as <c>stageKey</c> like all other progress events.
    /// </summary>
    public record DatabaseResetProgress(
        Guid? OperationId,
        bool IsProcessing,
        double PercentComplete,
        string Status,
        string StageKey,
        int TablesCleared,
        int TotalTables,
        int FilesDeleted,
        DateTime Timestamp,
        Dictionary<string, object?>? Context = null);

    public class ProgressData
    {
        [System.Text.Json.Serialization.JsonPropertyName("isProcessing")]
        public bool IsProcessing { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("percentComplete")]
        public double PercentComplete { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("stage_key")]
        public string StageKey { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("context")]
        public Dictionary<string, object?> Context { get; set; } = new();

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
    public Task<bool> StartResetAsync()
    {
        return RunResetAsync();
    }

    /// <summary>
    /// Gets the current database reset status including progress data from Rust
    /// </summary>
    public object GetResetStatus()
    {
        if (!IsProcessing)
        {
            return new
            {
                isProcessing = false,
                status = "idle",
                operationId = _currentTrackerOperationId
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
                stageKey = "signalr.dbReset.starting",
                operationId = _currentTrackerOperationId
            };
        }

        return new
        {
            isProcessing = true,
            status = progress.Status,
            percentComplete = progress.PercentComplete,
            stageKey = progress.StageKey,
            context = progress.Context,
            tablesCleared = progress.TablesCleared,
            totalTables = progress.TotalTables,
            filesDeleted = progress.FilesDeleted,
            operationId = _currentTrackerOperationId
        };
    }

    public async Task<bool> RunResetAsync()
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

            // Register the operation with the unified tracker.
            // onTerminalCleanup is the safety net for the universal force-kill path: it mirrors the worker
            // finally (lines 322-325) so service-local busy/identity state is reset even if the worker
            // does not unwind (IsProcessing is the restart gate at line 144-148).
            // onTerminalEmit fires the typed DatabaseResetComplete event EXACTLY ONCE (CompletedFlag-gated)
            // for the normal success/error path AND the universal force-kill/cancel path. The legacy
            // DatabaseResetProgress(status) completion emits are preserved (frontend Complete handler is PR4).
            Guid trackerOperationId = default;
            trackerOperationId = _operationTracker.RegisterOperation(
                OperationType.DatabaseReset,
                "Database Reset",
                _cancellationTokenSource,
                onTerminalCleanup: () =>
                {
                    IsProcessing = false;
                    _currentTrackerOperationId = null;
                    _cancellationTokenSource?.Dispose();
                    _cancellationTokenSource = null;
                },
                onTerminalEmit: info => info.Cancelled
                    ? _notifications.NotifyAllAsync(
                        SignalREvents.DatabaseResetComplete,
                        new SignalRNotifications.DatabaseResetComplete(
                            OperationId: trackerOperationId,
                            Success: false,
                            StageKey: "signalr.dbReset.cancelled",
                            Status: OperationStatus.Cancelled,
                            Cancelled: true,
                            Error: info.Error))
                    : info.Success
                        ? _notifications.NotifyAllAsync(
                            SignalREvents.DatabaseResetComplete,
                            new SignalRNotifications.DatabaseResetComplete(
                                OperationId: trackerOperationId,
                                Success: true,
                                StageKey: "signalr.dbReset.complete",
                                Status: OperationStatus.Completed))
                        : _notifications.NotifyAllAsync(
                            SignalREvents.DatabaseResetComplete,
                            new SignalRNotifications.DatabaseResetComplete(
                                OperationId: trackerOperationId,
                                Success: false,
                                StageKey: "signalr.dbReset.failed",
                                Status: OperationStatus.Failed,
                                Error: info.Error,
                                Context: new Dictionary<string, object?> { ["errorDetail"] = info.Error }))
            );
            _currentTrackerOperationId = trackerOperationId;

            // Send started event
            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetStarted, new
            {
                OperationId = _currentTrackerOperationId,
                StageKey = "signalr.dbReset.starting"
            });

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
            _logger.LogInformation($"Progress file: {progressPath}");

            // Send initial progress
            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
            {
                OperationId = _currentTrackerOperationId,
                isProcessing = true,
                percentComplete = 0.0,
                status = "starting",
                StageKey = "signalr.dbReset.starting",
                tablesCleared = 0,
                totalTables = 4,
                filesDeleted = 0,
                timestamp = DateTime.UtcNow
            });

            // Wrap Rust process execution in shared lock to prevent concurrent database access
            // This prevents database connection issues and SignalR disconnects during reset
            var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                rustExecutablePath,
                $"\"{dataDirectory}\" \"{progressPath}\"",
                Path.GetDirectoryName(rustExecutablePath));

            return await _cacheManagementService.ExecuteWithLockAsync(async () =>
            {
                var result = await _rustProcessHelper.ExecuteTrackedProcessWithProgressAsync<ProgressData>(
                    startInfo,
                    _currentTrackerOperationId,
                    _cancellationTokenSource.Token,
                    progressPath,
                    async progress => await _notifications.NotifyAllAsync(
                        SignalREvents.DatabaseResetProgress,
                        ToProgressPayload(progress)),
                    processLabel: "database_reset");

                var exitCode = result.ExitCode;
                _logger.LogInformation($"rust database reset exited with code {exitCode}");

                if (exitCode == 0)
                {
                    // Give Rust process a moment to write final progress file
                    await Task.Delay(200);

                    // Read and send final progress before stopping monitoring
                    var finalProgress = await ReadProgressFileAsync(progressPath);
                    if (finalProgress != null)
                    {
                        await _notifications.NotifyAllAsync(
                            SignalREvents.DatabaseResetProgress,
                            ToProgressPayload(finalProgress));
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
                            StageKey = "signalr.dbReset.complete",
                            timestamp = DateTime.UtcNow
                        });
                    }

                    if (_currentTrackerOperationId.HasValue)
                    {
                        _operationTracker.CompleteOperation(_currentTrackerOperationId.Value, success: true);
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
                        status = OperationStatus.Failed,
                        StageKey = "signalr.dbReset.failedExitCode",
                        Context = new Dictionary<string, object?> { ["exitCode"] = exitCode },
                        timestamp = DateTime.UtcNow
                    });

                    if (_currentTrackerOperationId.HasValue)
                    {
                        _operationTracker.CompleteOperation(_currentTrackerOperationId.Value, success: false, error: $"Database reset failed with exit code {exitCode}");
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
                StageKey = "signalr.dbReset.cancelled",
                timestamp = DateTime.UtcNow
            });

            if (_currentTrackerOperationId.HasValue)
            {
                _operationTracker.CompleteOperation(_currentTrackerOperationId.Value, success: false, error: "Cancelled by user");
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
                status = OperationStatus.Failed,
                StageKey = "signalr.dbReset.failed",
                Context = new Dictionary<string, object?> { ["errorDetail"] = ex.Message },
                timestamp = DateTime.UtcNow
            });

            if (_currentTrackerOperationId.HasValue)
            {
                _operationTracker.CompleteOperation(_currentTrackerOperationId.Value, success: false, error: ex.Message);
            }

            return false;
        }
        finally
        {
            IsProcessing = false;
            _currentTrackerOperationId = null;
            _cancellationTokenSource?.Dispose();
        }
    }

    private async Task<ProgressData?> ReadProgressFileAsync(string progressPath)
    {
        return await _rustProcessHelper.ReadProgressFileAsync<ProgressData>(progressPath);
    }

    /// <summary>
    /// Projects the polled Rust <see cref="ProgressData"/> into the camelCase-serializing
    /// <see cref="DatabaseResetProgress"/> record (stamping the current operation id), so the SignalR
    /// payload emits <c>stageKey</c> instead of the DTO's attribute-forced <c>stage_key</c> (D-rust-2).
    /// </summary>
    private DatabaseResetProgress ToProgressPayload(ProgressData progress) =>
        new(
            OperationId: _currentTrackerOperationId,
            IsProcessing: progress.IsProcessing,
            PercentComplete: progress.PercentComplete,
            Status: progress.Status,
            StageKey: progress.StageKey,
            TablesCleared: progress.TablesCleared,
            TotalTables: progress.TotalTables,
            FilesDeleted: progress.FilesDeleted,
            Timestamp: progress.Timestamp == default ? DateTime.UtcNow : progress.Timestamp,
            Context: progress.Context);
}