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
    private readonly IStateService _stateService;
    private readonly DatasourceService _datasourceService;
    private CancellationTokenSource? _cancellationTokenSource;
    private Guid? _currentTrackerOperationId;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private ResetCurrentProgress? _currentProgress;
    private long _progressRevision;
    private readonly ProgressEmitGate _progressEmitGate = new();

    public bool IsProcessing { get; private set; }
    public Guid? CurrentOperationId => _currentTrackerOperationId;

    public RustDatabaseResetService(
        ILogger<RustDatabaseResetService> logger,
        IPathResolver pathResolver,
        ISignalRNotificationService notifications,
        CacheManagementService cacheManagementService,
        RustProcessHelper rustProcessHelper,
        IUnifiedOperationTracker operationTracker,
        IStateService stateService,
        DatasourceService datasourceService)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _notifications = notifications;
        _cacheManagementService = cacheManagementService;
        _rustProcessHelper = rustProcessHelper;
        _operationTracker = operationTracker;
        _stateService = stateService;
        _datasourceService = datasourceService;
    }

    /// <summary>
    /// A full reset empties the log-derived tables, so every log checkpoint (per-stem
    /// maps included — they win over the zero scalar via the positions file) must clear
    /// with them or the next run silently skips all history.
    /// </summary>
    private void ResetLogCheckpoints()
    {
        foreach (var ds in _datasourceService.GetDatasources())
        {
            _stateService.SetLogSourcePositions(ds.Name, new Dictionary<string, long>());
            _stateService.SetLogPosition(ds.Name, 0);
            _stateService.SetLogTotalLines(ds.Name, 0);
        }
        _stateService.SetLogPosition(0);
    }

    private sealed record ResetCurrentProgress(
        OperationProgressSnapshot Snapshot,
        DatabaseResetProgress Payload);

    public class ProgressData
    {
        [System.Text.Json.Serialization.JsonPropertyName("isProcessing")]
        public bool IsProcessing { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("percentComplete")]
        public double PercentComplete { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("message")]
        public string? Message { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("stageKey")]
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
    public DatabaseResetStatusResponse GetResetStatus()
    {
        var current = Volatile.Read(ref _currentProgress);
        if (!IsProcessing || current == null)
        {
            return new DatabaseResetStatusResponse
            {
                IsProcessing = false,
                OperationId = _currentTrackerOperationId
            };
        }

        return new DatabaseResetStatusResponse
        {
            IsProcessing = true,
            Status = current.Payload.Status,
            Message = current.Payload.Message,
            PercentComplete = current.Snapshot.PercentComplete,
            StageKey = current.Snapshot.StageKey,
            Context = current.Snapshot.Context,
            TablesCleared = current.Payload.TablesCleared,
            TotalTables = current.Payload.TotalTables,
            FilesDeleted = current.Payload.FilesDeleted,
            OperationId = _currentTrackerOperationId
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
                    Volatile.Write(ref _currentProgress, null);
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
            _progressEmitGate.Reset();

            var initialProgress = new ProgressData
            {
                IsProcessing = true,
                PercentComplete = 0,
                Status = "starting",
                Message = "Starting database reset...",
                StageKey = "signalr.dbReset.starting",
                TablesCleared = 0,
                TotalTables = 4,
                FilesDeleted = 0,
                Timestamp = DateTime.UtcNow
            };
            await ReportProgressAsync(initialProgress, emit: false);

            // Send started event
            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetStarted, new
            {
                OperationId = _currentTrackerOperationId,
                StageKey = "signalr.dbReset.starting",
                Context = Volatile.Read(ref _currentProgress)?.Snapshot.Context
            });
            await ReportProgressAsync(initialProgress);

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

            // Wrap Rust process execution in shared lock to prevent concurrent database access
            // This prevents database connection issues and SignalR disconnects during reset
            var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                rustExecutablePath,
                $"\"{dataDirectory}\" \"{progressPath}\" --progress",
                Path.GetDirectoryName(rustExecutablePath));

            return await _cacheManagementService.ExecuteWithLockAsync(async () =>
            {
                // Hybrid transport (mirrors CacheClearingService): stdout is a zero-latency wake-up;
                // the callback re-reads the durable file containing the same stage/context.
                var result = await _rustProcessHelper.ExecuteTrackedProcessWithProgressEventsAsync(
                    startInfo,
                    _currentTrackerOperationId,
                    _cancellationTokenSource.Token,
                    async _ =>
                    {
                        var progressData = await _rustProcessHelper.ReadProgressFileAsync<ProgressData>(progressPath);
                        if (progressData == null)
                        {
                            return;
                        }

                        await ReportProgressAsync(progressData);
                    },
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
                        await ReportProgressAsync(finalProgress);

                        if (string.Equals(finalProgress.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
                        {
                            if (_currentTrackerOperationId.HasValue)
                            {
                                _operationTracker.CompleteOperation(
                                    _currentTrackerOperationId.Value,
                                    success: false,
                                    error: "Cancelled by user");
                            }

                            return false;
                        }

                        if (string.Equals(finalProgress.Status, "error", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(finalProgress.Status, "failed", StringComparison.OrdinalIgnoreCase))
                        {
                            if (_currentTrackerOperationId.HasValue)
                            {
                                _operationTracker.CompleteOperation(
                                    _currentTrackerOperationId.Value,
                                    success: false,
                                    error: finalProgress.Message ?? "Database reset failed");
                            }

                            return false;
                        }
                    }
                    else
                    {
                        // Fallback completion message
                        await ReportProgressAsync(new ProgressData
                        {
                            IsProcessing = false,
                            PercentComplete = 100.0,
                            Status = "completed",
                            Message = "Database reset completed",
                            StageKey = "signalr.dbReset.complete",
                            TablesCleared = 4,
                            TotalTables = 4,
                            Timestamp = DateTime.UtcNow
                        });
                    }

                    ResetLogCheckpoints();

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
                    await ReportProgressAsync(new ProgressData
                    {
                        IsProcessing = false,
                        PercentComplete = Volatile.Read(ref _currentProgress)?.Snapshot.PercentComplete ?? 0.0,
                        Status = "error",
                        Message = $"Database reset failed with exit code {exitCode}",
                        StageKey = "signalr.dbReset.failedExitCode",
                        Context = new Dictionary<string, object?> { ["exitCode"] = exitCode },
                        Timestamp = DateTime.UtcNow
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
            await ReportProgressAsync(new ProgressData
            {
                IsProcessing = false,
                PercentComplete = Volatile.Read(ref _currentProgress)?.Snapshot.PercentComplete ?? 0.0,
                Status = "cancelled",
                Message = "Database reset was cancelled by user",
                StageKey = "signalr.dbReset.cancelled",
                Timestamp = DateTime.UtcNow
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
            await ReportProgressAsync(new ProgressData
            {
                IsProcessing = false,
                PercentComplete = Volatile.Read(ref _currentProgress)?.Snapshot.PercentComplete ?? 0.0,
                Status = "error",
                Message = $"Database reset failed: {ex.Message}",
                StageKey = "signalr.dbReset.failed",
                Context = new Dictionary<string, object?> { ["errorDetail"] = ex.Message },
                Timestamp = DateTime.UtcNow
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
            Volatile.Write(ref _currentProgress, null);
            _currentTrackerOperationId = null;
            _cancellationTokenSource?.Dispose();
        }
    }

    private async Task<ProgressData?> ReadProgressFileAsync(string progressPath)
    {
        return await _rustProcessHelper.ReadProgressFileAsync<ProgressData>(progressPath);
    }

    private async Task ReportProgressAsync(ProgressData progress, bool emit = true)
    {
        if (string.IsNullOrWhiteSpace(progress.StageKey))
        {
            _logger.LogWarning("Ignoring database reset progress without a stage key");
            return;
        }

        var context = new Dictionary<string, object?>(progress.Context ?? new Dictionary<string, object?>())
        {
            ["tablesCleared"] = progress.TablesCleared,
            ["totalTables"] = progress.TotalTables,
            ["filesDeleted"] = progress.FilesDeleted
        };
        var current = Volatile.Read(ref _currentProgress);
        var isNew = current == null
            || !current.Snapshot.HasSameProgress(progress.StageKey, progress.PercentComplete, context)
            || current.Payload.IsProcessing != progress.IsProcessing
            || current.Payload.Message != progress.Message;
        if (isNew)
        {
            var snapshot = OperationProgressSnapshot.Create(
                progress.StageKey,
                progress.PercentComplete,
                context,
                Interlocked.Increment(ref _progressRevision));
            var payload = new DatabaseResetProgress(
                OperationId: _currentTrackerOperationId,
                IsProcessing: progress.IsProcessing,
                PercentComplete: snapshot.PercentComplete,
                Status: ToOperationStatus(progress),
                StageKey: snapshot.StageKey,
                Message: progress.Message,
                TablesCleared: progress.TotalTables == 0 ? null : progress.TablesCleared,
                TotalTables: progress.TotalTables == 0 ? null : progress.TotalTables,
                FilesDeleted: progress.FilesDeleted,
                Timestamp: progress.Timestamp == default ? DateTime.UtcNow : progress.Timestamp,
                Context: snapshot.Context);
            current = new ResetCurrentProgress(snapshot, payload);
            Volatile.Write(ref _currentProgress, current);
            if (_currentTrackerOperationId.HasValue)
            {
                _operationTracker.UpdateProgress(
                    _currentTrackerOperationId.Value,
                    snapshot.PercentComplete,
                    snapshot.StageKey);
            }
        }

        if (!emit
            || current == null
            || (progress.IsProcessing
                && !_progressEmitGate.ShouldEmit(current.Snapshot.StageKey, current.Snapshot.Revision)))
        {
            return;
        }

        await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, current.Payload);
    }

    private static OperationStatus ToOperationStatus(ProgressData progress) => progress.Status switch
    {
        "completed" => OperationStatus.Completed,
        "cancelled" => OperationStatus.Cancelled,
        "error" or "failed" => OperationStatus.Failed,
        _ => OperationStatus.Running
    };
}
