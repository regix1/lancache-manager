using LancacheManager.Core.Services;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Infrastructure.Extensions;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Hubs;
using LancacheManager.Core.Interfaces;
using LancacheManager.Models;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Service that spawns the Rust log processor and monitors its progress
/// </summary>
public class RustLogProcessorService
{
    private readonly ILogger<RustLogProcessorService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly ISignalRNotificationService _notifications;
    private readonly StateService _stateService;
    private readonly IServiceProvider _serviceProvider;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly DatasourceService _datasourceService;
    private readonly IUnifiedOperationTracker _operationTracker;
    private CancellationTokenSource? _cancellationTokenSource;
    private Guid? _currentOperationId;
    private string? _currentDatasourceName;
    private string? _currentProgressPath;
    private LogProcessingBatchState? _currentBatch;
    private Task? _progressMonitorTask;
    private readonly SemaphoreSlim _startLock = new(1, 1);

    // The image/banner pass that runs detached after each log pass can take many seconds, and
    // IsProcessing clears before it finishes, so the next tick would start another one and nothing
    // would bound how many are alive. A tick that loses this gate does no work and returns.
    private readonly SemaphoreSlim _postPassLock = new(1, 1);

    private long? _steamNamesDrainedAtMaxId;
    private (long MaxDownloadId, int Catalog, DateTime? CatalogSeenUtc)? _lastEpicImageScan;

    // An image task that loses _postPassLock does no image work. Keep the pending resolve across
    // tasks until one takes the lock and runs the Epic image scan.
    private int _epicRowsResolvedPending;

    // Signaled at the RegisterOperation call inside the processor so StartBackgroundProcessingAsync
    // can return the assigned operationId without polling. Class-field is safe because _startLock
    // gates IsProcessing = true, so there is at most one in-flight start at a time.
    private TaskCompletionSource<Guid>? _operationRegisteredTcs;

    private readonly record struct LogProcessingTerminalMetrics(
        long EntriesProcessed,
        long LinesProcessed,
        double? Elapsed,
        string? Message,
        string? StageKey);

    // A pass sets the run flag; reset-to-end holds the reservation. They are separate fields so no run
    // writer can clear a reservation and releasing a reservation cannot clear a running pass's flag.
    private bool _isProcessingRun;
    private Guid? _processingGateReservation;
    public bool IsProcessing { get => _isProcessingRun || _processingGateReservation is not null; private set => _isProcessingRun = value; }
    public Guid? CurrentOperationId => _currentOperationId;

    public Task<Guid?> StartAllInBackgroundAsync()
    {
        var datasources = _datasourceService.GetDatasources();
        if (datasources.Count == 0)
        {
            _logger.LogWarning("No datasources configured for log processing");
            return Task.FromResult<Guid?>(null);
        }

        return RunBackgroundAsync(
            RunAllDatasourcesAsync,
            "processing all datasources");
    }

    private Guid BeginOperation()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        Guid operationId = Guid.Empty;
        operationId = _operationTracker.RegisterOperation(
            OperationType.LogProcessing,
            "Log Processing",
            _cancellationTokenSource,
            onTerminalCleanup: () =>
            {
                if (_currentOperationId != operationId) return;
                _currentOperationId = null;
                _currentDatasourceName = null;
                _currentProgressPath = null;
                _currentBatch = null;
                _cancellationTokenSource = null;
                // Reset the busy/state gates too: the universal force-kill path bypasses
                // EndLogProcessingOperation/ResetState, so without this IsProcessing stays true and
                // blocks the next StartProcessing (guard at the IsProcessing check).
                IsProcessing = false;
            },
            // The batch path is never live ingest, so the terminal SignalR emitter is always wired here.
            onTerminalEmit: BuildTerminalEmit(() => operationId));
        _currentOperationId = operationId;
        _operationRegisteredTcs?.TrySetResult(operationId);
        IsProcessing = true;
        return operationId;
    }

    private Func<OperationTerminalInfo, Task> BuildTerminalEmit(Func<Guid?> getOperationId)
    {
        return info =>
        {
            var operationId = getOperationId();
            var metrics = operationId.HasValue
                && _operationTracker.GetOperation(operationId.Value)?.Metadata is LogProcessingTerminalMetrics current
                ? current : default;

            if (info.Cancelled)
            {
                // Cancellation keeps the accepted counts without reusing a datasource's success text.
                return _notifications.NotifyAllAsync(
                    SignalREvents.LogProcessingComplete,
                    new LogProcessingComplete(
                        OperationId: operationId,
                        Success: false,
                        Status: OperationStatus.Cancelled,
                        Message: "Log processing was cancelled",
                        Cancelled: true,
                        EntriesProcessed: metrics.EntriesProcessed,
                        LinesProcessed: metrics.LinesProcessed,
                        Elapsed: metrics.Elapsed,
                        StageKey: metrics.StageKey));
            }

            if (info.Success)
            {
                return _notifications.NotifyAllAsync(
                    SignalREvents.LogProcessingComplete,
                    new LogProcessingComplete(
                        OperationId: operationId,
                        Success: true,
                        Status: OperationStatus.Completed,
                        Message: metrics.Message ?? "Log processing completed successfully",
                        Cancelled: false,
                        EntriesProcessed: metrics.EntriesProcessed,
                        LinesProcessed: metrics.LinesProcessed,
                        Elapsed: metrics.Elapsed,
                        StageKey: metrics.StageKey));
            }

            return _notifications.NotifyAllAsync(
                SignalREvents.LogProcessingComplete,
                new LogProcessingComplete(
                    OperationId: operationId,
                    Success: false,
                    Status: OperationStatus.Failed,
                    Message: info.Error ?? metrics.Message ?? "Log processing failed",
                    Cancelled: false,
                    EntriesProcessed: metrics.EntriesProcessed,
                    LinesProcessed: metrics.LinesProcessed,
                    Elapsed: metrics.Elapsed,
                    StageKey: metrics.StageKey));
        };
    }

    private void EndOperation()
    {
        IsProcessing = false;
        _currentOperationId = null;
        _currentDatasourceName = null;
        _currentProgressPath = null;
        _currentBatch = null;
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
    }

    internal static double ScaleBatchProgress(LogProcessingBatchState? batch, double childPercent)
    {
        var boundedChildPercent = Math.Clamp(childPercent, 0, 100);
        if (batch is null)
        {
            return boundedChildPercent;
        }

        var scaled = ((batch.CompletedChildren + boundedChildPercent / 100) / batch.ChildCount) * 100;
        batch.PercentComplete = Math.Max(batch.PercentComplete, scaled);
        return batch.PercentComplete;
    }

    internal static void RecordBatchChild(
        LogProcessingBatchState batch,
        string datasourceName,
        bool succeeded,
        LogProcessingProgress? progress)
    {
        batch.EntriesProcessed += Math.Max(batch.CurrentEntriesProcessed, progress?.EntriesSaved ?? 0);
        batch.LinesProcessed += Math.Max(batch.CurrentLinesProcessed, progress?.LinesParsed ?? 0);
        batch.TotalLines += Math.Max(batch.CurrentTotalLines, progress?.TotalLines ?? 0);
        batch.BytesProcessed += Math.Max(batch.CurrentBytesProcessed, progress?.BytesProcessed ?? 0);
        batch.TotalBytes += Math.Max(batch.CurrentTotalBytes, progress?.TotalBytes ?? 0);
        batch.CurrentEntriesProcessed = 0;
        batch.CurrentLinesProcessed = 0;
        batch.CurrentTotalLines = 0;
        batch.CurrentBytesProcessed = 0;
        batch.CurrentTotalBytes = 0;
        batch.CompletedChildren++;
        batch.PercentComplete = Math.Max(
            batch.PercentComplete,
            batch.CompletedChildren * 100.0 / batch.ChildCount);

        if (!succeeded && batch.FailedDatasourceName is null)
        {
            batch.FailedDatasourceName = datasourceName;
        }
    }

    internal void CompleteBatchOperation(
        Guid operationId,
        LogProcessingBatchState batch,
        bool cancelled,
        bool batchFinished)
    {
        if (_operationTracker.GetOperation(operationId)?.Status.IsTerminal() == true)
        {
            return;
        }

        if (_currentOperationId == operationId)
        {
            IsProcessing = false;
            _currentDatasourceName = null;
            _currentProgressPath = null;
            _currentBatch = null;
        }

        if (cancelled)
        {
            var metrics = new LogProcessingTerminalMetrics(
                batch.EntriesProcessed,
                batch.LinesProcessed,
                null,
                "Log processing was cancelled",
                null);
            _operationTracker.CompleteOperation(
                operationId,
                false,
                "Operation was cancelled",
                cancelled: true,
                onCompleting: operation => operation.Metadata = metrics);
            return;
        }

        if (batch.FailedDatasourceName is { } failedDatasourceName)
        {
            var message = $"Log processing failed for datasource '{failedDatasourceName}'";
            var metrics = new LogProcessingTerminalMetrics(
                batch.EntriesProcessed,
                batch.LinesProcessed,
                null,
                message,
                null);
            _operationTracker.CompleteOperation(
                operationId,
                false,
                message,
                onCompleting: operation => operation.Metadata = metrics);
            return;
        }

        if (batchFinished)
        {
            var metrics = new LogProcessingTerminalMetrics(
                batch.EntriesProcessed,
                batch.LinesProcessed,
                null,
                "Log processing completed successfully",
                "signalr.logProcessing.complete");
            _operationTracker.CompleteOperation(
                operationId,
                true,
                onCompleting: operation => operation.Metadata = metrics);
            return;
        }

        var incompleteMetrics = new LogProcessingTerminalMetrics(
            batch.EntriesProcessed,
            batch.LinesProcessed,
            null,
            "Log processing ended without completing; marked failed",
            null);
        _operationTracker.CompleteOperation(
            operationId,
            false,
            "Log processing ended without completing",
            onCompleting: operation => operation.Metadata = incompleteMetrics);
    }

    private async Task<bool> RunAllDatasourcesAsync()
    {
        var datasources = _datasourceService.GetDatasources();
        if (datasources.Count == 0)
        {
            _logger.LogWarning("No datasources configured for log processing");
            return false;
        }

        // Same atomic gate the single-datasource path takes: without it the batch could
        // race a live-monitor start and overwrite the singleton's CTS/operation fields.
        await _startLock.WaitAsync();
        try
        {
            if (IsProcessing)
            {
                _logger.LogWarning("Rust log processor is already running; batch start rejected");
                return false;
            }
            IsProcessing = true;
        }
        finally
        {
            _startLock.Release();
        }

        var batchOperationId = BeginOperation();
        var batch = new LogProcessingBatchState { ChildCount = datasources.Count };
        _currentBatch = batch;
        var batchToken = _operationTracker.GetOperation(batchOperationId)!.CancellationTokenSource!.Token;
        try
        {
            var allSuccess = true;
            for (var i = 0; i < datasources.Count; i++)
            {
                // Stop spawning Rust children for the remaining datasources once the shared
                // operation has been cancelled (csharp-services-1 / P2-E). The in-flight
                // datasource handles its own cancellation via the shared CTS token.
                if (batchToken.IsCancellationRequested
                    || _operationTracker.GetOperation(batchOperationId)?.Status.IsTerminal() == true)
                {
                    _logger.LogInformation("Log processing batch cancelled; skipping remaining datasources");
                    allSuccess = false;
                    break;
                }

                var datasource = datasources[i];
                var logPosition = _stateService.GetLogPosition(datasource.Name);
                _logger.LogInformation("Processing datasource '{DatasourceName}' from position {Position}",
                    datasource.Name, logPosition);

                var success = await StartProcessingAsync(
                    datasource.LogPath,
                    logPosition,
                    datasourceName: datasource.Name,
                    sharedOperationId: batchOperationId,
                    finalizeOperation: false,
                    batch: batch);
                if (!success)
                {
                    allSuccess = false;
                    _logger.LogWarning("Processing failed for datasource '{DatasourceName}'", datasource.Name);
                }
            }

            CompleteBatchOperation(
                batchOperationId,
                batch,
                cancelled: batchToken.IsCancellationRequested,
                batchFinished: true);
            return allSuccess && !batchToken.IsCancellationRequested;
        }
        finally
        {
            // Completion backstop. BeginOperation registered the batch operation, but only the
            // FINAL datasource's run finalizes it - a cancellation break before that iteration,
            // or an exception between registration and the loop (which the background runner
            // swallows with a log line), otherwise leaves the operation active forever, and the
            // operation queue then parks every later run behind the ghost. If nothing reached a
            // terminal state, fail the operation here so the queue can move on.
            if (_operationTracker.GetOperation(batchOperationId)?.Status.IsTerminal() != true)
            {
                CompleteBatchOperation(
                    batchOperationId,
                    batch,
                    cancelled: batchToken.IsCancellationRequested,
                    batchFinished: false);
            }

            if (_currentOperationId == batchOperationId)
            {
                EndOperation();
            }
        }
    }

    public Task<Guid?> StartInBackgroundAsync(string logFilePath, long startPosition = 0, bool liveIngest = false, string? datasourceName = null)
    {
        return RunBackgroundAsync(
            () => StartProcessingAsync(logFilePath, startPosition, liveIngest, datasourceName),
            $"processing datasource '{datasourceName ?? "default"}'");
    }

    private async Task<Guid?> RunBackgroundAsync(Func<Task<bool>> processor, string description)
    {
        var registered = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        _operationRegisteredTcs = registered;

        var backgroundTask = Task.Run(async () =>
        {
            try
            {
                return await processor();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error while {Description}", description);
                return false;
            }
            finally
            {
                // Unblocks the outer wait if the processor exited without reaching RegisterOperation
                // (e.g. IsProcessing guard returned false, or an early exception). Harmless if already set.
                registered.TrySetCanceled();
            }
        });

        await Task.WhenAny(registered.Task, backgroundTask);
        return registered.Task.IsCompletedSuccessfully ? registered.Task.Result : (Guid?)null;
    }

    /// <summary>
    /// Resets the log position to 0 to reprocess all logs (all datasources)
    /// </summary>
    public void ResetLogPosition()
    {
        // Reset all datasource positions, including every per-source-stem checkpoint —
        // leaving stale stem offsets behind would resurrect them via the positions file.
        foreach (var ds in _datasourceService.GetDatasources())
        {
            ClearResume(ds.Name);
            _stateService.SetLogSourcePositions(ds.Name, new Dictionary<string, long>());
            _stateService.SetLogPosition(ds.Name, 0);
        }
        // Also reset legacy position for backward compatibility
        _stateService.SetLogPosition(0);
        _logger.LogInformation("Log position reset to 0 for all datasources");
    }

    /// <summary>
    /// Resets the log position for a specific datasource
    /// </summary>
    public void ResetLogPosition(string datasourceName)
    {
        ClearResume(datasourceName);
        _stateService.SetLogSourcePositions(datasourceName, new Dictionary<string, long>());
        _stateService.SetLogPosition(datasourceName, 0);
        _logger.LogInformation("Log position reset to 0 for datasource '{DatasourceName}'", datasourceName);
    }

    public void ClearResume(string datasourceName)
    {
        var resumePath = Path.Combine(
            _pathResolver.GetOperationsDirectory(),
            $"rust_resume_{datasourceName}.json");
        File.Delete(resumePath);
    }

    /// <summary>
    /// Starts log processing for all configured datasources
    /// </summary>
    public async Task<bool> StartProcessingAsync()
    {
        return await RunAllDatasourcesAsync();
    }

    /// <summary>
    /// Gets the current processing status including progress data from Rust
    /// </summary>
    public LogProcessingStatusResponse GetStatus()
    {
        if (!IsProcessing)
        {
            return new LogProcessingStatusResponse
            {
                IsProcessing = false,
                Status = "idle",
                OperationId = _currentOperationId,
                DatasourceName = null
            };
        }

        // Read progress from the active datasource's progress file.
        if (_currentDatasourceName is null)
        {
            if (_currentBatch is not { } betweenChildren)
            {
                return new LogProcessingStatusResponse
                {
                    IsProcessing = true,
                    Status = "starting",
                    OperationId = _currentOperationId,
                    DatasourceName = null
                };
            }

            return new LogProcessingStatusResponse
            {
                IsProcessing = true,
                Status = "running",
                OperationId = _currentOperationId,
                DatasourceName = null,
                PercentComplete = betweenChildren.PercentComplete,
                MbProcessed = Math.Round(betweenChildren.BytesProcessed / (1024.0 * 1024.0), 1),
                MbTotal = Math.Round(betweenChildren.TotalBytes / (1024.0 * 1024.0), 1),
                EntriesProcessed = betweenChildren.EntriesProcessed,
                TotalLines = betweenChildren.TotalLines
            };
        }

        var operationsDir = _pathResolver.GetOperationsDirectory();
        var progressPath = _currentProgressPath;
        if (progressPath is null)
        {
            return new LogProcessingStatusResponse
            {
                IsProcessing = true,
                Status = "starting",
                OperationId = _currentOperationId,
                DatasourceName = _currentDatasourceName,
                PercentComplete = _currentBatch?.PercentComplete,
                EntriesProcessed = _currentBatch?.EntriesProcessed,
                TotalLines = _currentBatch?.TotalLines
            };
        }
        var legacyProgressPath = Path.Combine(operationsDir, "rust_progress.json");

        LogProcessingProgress? progress = null;
        try
        {
            if (!File.Exists(progressPath) && File.Exists(legacyProgressPath))
            {
                progressPath = legacyProgressPath;
            }

            if (File.Exists(progressPath))
            {
                var json = File.ReadAllText(progressPath);
                progress = System.Text.Json.JsonSerializer.Deserialize<LogProcessingProgress>(json);
            }
        }
        catch
        {
            // Ignore read errors - file may be being written
        }

        if (progress == null)
        {
            return new LogProcessingStatusResponse
            {
                IsProcessing = true,
                Status = "starting",
                OperationId = _currentOperationId,
                DatasourceName = _currentDatasourceName,
                PercentComplete = _currentBatch?.PercentComplete,
                EntriesProcessed = _currentBatch?.EntriesProcessed,
                TotalLines = _currentBatch?.TotalLines
            };
        }

        // The Rust processor reports real byte counts across ALL discovered log files
        // (access.log + rotated + compressed), replacing the old single-file estimate.
        var percentComplete = ScaleBatchProgress(_currentBatch, progress.PercentComplete);
        var entriesProcessed = progress.EntriesSaved;
        var totalLines = progress.TotalLines;
        var bytesProcessed = progress.BytesProcessed;
        var totalBytes = progress.TotalBytes;
        if (_currentBatch is { } batch)
        {
            batch.CurrentEntriesProcessed = Math.Max(batch.CurrentEntriesProcessed, progress.EntriesSaved);
            batch.CurrentLinesProcessed = Math.Max(batch.CurrentLinesProcessed, progress.LinesParsed);
            batch.CurrentTotalLines = Math.Max(batch.CurrentTotalLines, progress.TotalLines);
            batch.CurrentBytesProcessed = Math.Max(batch.CurrentBytesProcessed, progress.BytesProcessed);
            batch.CurrentTotalBytes = Math.Max(batch.CurrentTotalBytes, progress.TotalBytes);
            entriesProcessed = batch.EntriesProcessed + batch.CurrentEntriesProcessed;
            totalLines = batch.TotalLines + batch.CurrentTotalLines;
            bytesProcessed = batch.BytesProcessed + batch.CurrentBytesProcessed;
            totalBytes = batch.TotalBytes + batch.CurrentTotalBytes;
        }

        var mbTotal = totalBytes / (1024.0 * 1024.0);
        var mbProcessed = bytesProcessed / (1024.0 * 1024.0);

        return new LogProcessingStatusResponse
        {
            IsProcessing = true,
            OperationId = _currentOperationId,
            DatasourceName = _currentDatasourceName,
            Status = progress.Status,
            PercentComplete = percentComplete,
            MbProcessed = Math.Round(mbProcessed, 1),
            MbTotal = Math.Round(mbTotal, 1),
            EntriesProcessed = entriesProcessed,
            TotalLines = totalLines,
            StageKey = progress.StageKey
        };
    }

    public RustLogProcessorService(
        ILogger<RustLogProcessorService> logger,
        IPathResolver pathResolver,
        ISignalRNotificationService notifications,
        StateService stateService,
        IServiceProvider serviceProvider,
        RustProcessHelper rustProcessHelper,
        DatasourceService datasourceService,
        IUnifiedOperationTracker operationTracker)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _notifications = notifications;
        _stateService = stateService;
        _serviceProvider = serviceProvider;
        _rustProcessHelper = rustProcessHelper;
        _datasourceService = datasourceService;
        _operationTracker = operationTracker;
    }

    private static readonly string[] _validTerminalStatuses =
    {
        "completed", "completed_with_warnings", "partial", "failed", "cancelled"
    };

    /// <summary>
    /// True when the final progress file is a valid terminal checkpoint from the new
    /// contract writer: known schema version and a recognized terminal status.
    /// </summary>
    private static bool IsValidTerminalCheckpoint(LogProcessingProgress? progress) =>
        progress is { SchemaVersion: 1 } &&
        _validTerminalStatuses.Contains(progress.TerminalStatus);

    /// <summary>
    /// True when a run's terminal checkpoint proves committed download rows exist: a valid
    /// terminal checkpoint with at least one saved entry. Rust commits every batch transaction
    /// before writing the checkpoint, so an event emitted on this predicate can never precede
    /// the rows it announces. Partial and cancelled runs with committed batches qualify; a
    /// missing or invalid checkpoint never does (its entry count cannot be trusted).
    /// </summary>
    public static bool HasCommittedDownloads(LogProcessingProgress? progress) =>
        IsValidTerminalCheckpoint(progress) && progress!.EntriesSaved > 0;

    /// <summary>
    /// True when a run may clear the shared operation state: the busy flags, the current operation
    /// id, the current cancellation source and the datasource/progress paths. This service is a
    /// singleton and the interactive path clears <see cref="IsProcessing"/> before its display
    /// delay, so a second run can pass the re-entry guard and install its own id while the first is
    /// still finishing. Ownership is what separates the three states: the field still holds this
    /// run's id, so this run installed what is there and clears it; the field holds a newer id, so
    /// that run owns the state and clearing it would leave it running with nothing able to cancel
    /// it; or both are absent, which is a run that set the busy flag and never got as far as
    /// registering, and must still clear the flag it set.
    /// </summary>
    internal static bool OwnsOperationState(Guid? currentOperationId, Guid? ownerOperationId) =>
        currentOperationId == ownerOperationId;

    /// <summary>
    /// Emits the committed-boundary <see cref="SignalREvents.DownloadsRefresh"/>: fired once
    /// per run, immediately after terminal-checkpoint validation and BEFORE the auto-tag and
    /// mapping post-passes, so the frontend refetches inserted rows at commit latency instead
    /// of post-pass latency. Must stay on NotifyAllAsync: it bumps the dashboard batch cache
    /// generation before the hub send, which is what guarantees the event-driven refetch can
    /// never be served a stale cached batch. The payload is diagnostic only; the frontend must
    /// not depend on any of its fields.
    /// </summary>
    private async Task NotifyCommittedDownloadsAsync(LogProcessingProgress? finalProgress)
    {
        if (!HasCommittedDownloads(finalProgress))
        {
            return;
        }

        await _notifications.NotifyAllAsync(SignalREvents.DownloadsRefresh, new
        {
            source = "rust-insert-early",
            terminalStatus = finalProgress!.TerminalStatus,
            logEntriesSaved = finalProgress.EntriesSaved,
            runId = finalProgress.RunId,
            timestamp = DateTime.UtcNow
        });
    }

    public async Task<bool> StartProcessingAsync(
        string logFilePath,
        long startPosition = 0,
        bool liveIngest = false,
        string? datasourceName = null,
        Guid? sharedOperationId = null,
        bool finalizeOperation = true,
        LogProcessingBatchState? batch = null)
    {
        await _startLock.WaitAsync();
        try
        {
            if (IsProcessing && sharedOperationId == null)
            {
                _logger.LogWarning("Rust log processor is already running");
                return false;
            }

            if (sharedOperationId == null)
            {
                IsProcessing = true;
            }
        }
        finally
        {
            _startLock.Release();
        }

        datasourceName ??= _datasourceService.GetDefaultDatasource()?.Name ?? "default";

        var shouldFinalizeOperation = finalizeOperation;
        RiotMappingRunReporter? riotMappingRun = null;

        // This run's own operation id, captured where the operation is registered. Every completion
        // below uses it instead of re-reading _currentOperationId: the interactive path clears
        // IsProcessing before it completes its operation, so a live ingest tick can pass the
        // re-entry guard and reassign the field before this run has finished.
        Guid? ownerOperationId = null;
        LogProcessingTerminalMetrics terminalMetrics = default;
        LogProcessingProgress? finalProgress = null;
        var childSucceeded = false;

        try
        {
            if (sharedOperationId != null)
            {
                ownerOperationId = sharedOperationId;
                if (_currentOperationId != sharedOperationId
                    || _operationTracker.GetOperation(sharedOperationId.Value)?.Status.IsTerminal() != false)
                    return false;
            }
            else
            {
                _cancellationTokenSource = new CancellationTokenSource();
                // The local is assigned in the same breath as the field, with nothing between them
                // that can throw. A run that reaches this point always knows its own id, so the
                // teardown checks below can never mistake it for a run that installed nothing.
                ownerOperationId = _operationTracker.RegisterOperation(
                    OperationType.LogProcessing,
                    "Log Processing",
                    _cancellationTokenSource,
                    // Live ingest is started by no one, so it runs Hidden and the tracker sends
                    // nothing for it unless it fails. An interactive run has no notice: a full card.
                    notice: liveIngest ? new RunNotice(NotificationMode.Hidden, RunTrigger.Scheduled) : null,
                    liveIngest: liveIngest,
                    onTerminalCleanup: () =>
                    {
                        // Clear only the state this run installed. A later run can have registered
                        // its own id and cancellation source while this one was finishing, and
                        // clearing those here would leave it running with nothing able to cancel it.
                        if (!OwnsOperationState(_currentOperationId, ownerOperationId))
                        {
                            return;
                        }

                        _currentOperationId = null;
                        _currentDatasourceName = null;
                        _currentProgressPath = null;
                        _cancellationTokenSource = null;
                        // Reset the busy/state gates too: the universal force-kill path bypasses
                        // EndLogProcessingOperation/ResetState, so without this IsProcessing stays true
                        // and blocks the next StartProcessing (guard at the IsProcessing check).
                        IsProcessing = false;
                    },
                    // Live ingest runs about once a second and every LogProcessingComplete refreshes
                    // the dashboard, so only interactive runs wire the emitter. The single terminal
                    // event then fires exactly once from CompleteOperation (success / OCE / force-kill).
                    onTerminalEmit: liveIngest ? null : BuildTerminalEmit(() => ownerOperationId));
                _currentOperationId = ownerOperationId;
                _operationRegisteredTcs?.TrySetResult(ownerOperationId.Value);
            }

            var processingToken = _cancellationTokenSource?.Token
                ?? _operationTracker.GetOperation(ownerOperationId!.Value)?.CancellationTokenSource?.Token
                ?? CancellationToken.None;
            riotMappingRun = new RiotMappingRunReporter(
                _notifications,
                _operationTracker,
                _logger,
                processingToken,
                liveIngest
                    ? new RunNotice(NotificationMode.Hidden, RunTrigger.Scheduled)
                    : new RunNotice(NotificationMode.All, RunTrigger.Manual),
                cancelOwner: () =>
                {
                    if (ownerOperationId.HasValue)
                    {
                        _operationTracker.CancelOperation(ownerOperationId.Value);
                    }
                });

            var operationsDir = _pathResolver.GetOperationsDirectory();
            var progressPath = Path.Combine(operationsDir, $"rust_progress_{datasourceName}.json");
            var rustExecutablePath = _pathResolver.GetRustLogProcessorPath();

            // Per-source positions file: the single source of stem offsets for this run
            // (the CLI start position is ignored by the multi-source processor). When no
            // per-source checkpoint exists yet (pre-upgrade state), migrate the legacy
            // aggregate position onto the access.log stem so monolithic behavior is
            // unchanged; other stems default to 0 inside the processor.
            var sourcePositions = _stateService.GetLogSourcePositions(datasourceName);
            if (sourcePositions.Count == 0)
            {
                var legacyPosition = _stateService.GetLogPosition(datasourceName);
                if (legacyPosition > 0)
                {
                    sourcePositions[LogSourceLayout.MonolithicStem] = legacyPosition;
                }
            }
            var startPositions = new Dictionary<string, long>(sourcePositions);
            var positionsPath = Path.Combine(operationsDir, $"rust_positions_{datasourceName}.json");
            var positionsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                schema_version = 1,
                sources = sourcePositions
            });
            await File.WriteAllTextAsync(positionsPath, positionsJson);

            // Determine if logFilePath is a directory or file path
            // If it's already a directory, use it directly; otherwise extract directory from file path
            var logDirectory = Directory.Exists(logFilePath)
                ? logFilePath  // It's already a directory
                : (Path.GetDirectoryName(logFilePath) ?? _pathResolver.GetLogsDirectory());  // Extract from file path

            // Delete old progress file
            if (File.Exists(progressPath))
            {
                File.Delete(progressPath);
            }

            if (_currentOperationId == ownerOperationId)
            {
                _currentDatasourceName = datasourceName;
                _currentProgressPath = progressPath;
            }

            _logger.LogInformation("Starting Rust log processor");
            _logger.LogInformation("Log directory: {LogDirectory}", logDirectory);
            _logger.LogInformation("Progress file: {ProgressPath}", progressPath);
            _logger.LogInformation("Start position: {StartPosition}", startPosition);

            // Live ingest sends no started event: it runs about once a second, and every browser
            // listener of the LogProcessing events would refresh on each pass.
            if (!liveIngest)
            {
                await _notifications.NotifyAllAsync(SignalREvents.LogProcessingStarted, new
                {
                    OperationId = ownerOperationId,
                    StageKey = "signalr.logProcessing.starting",
                    Context = new Dictionary<string, object?>()
                });
            }

            // A live pass runs about once a second, and this count exists only for the log line.
            if (!liveIngest)
            {
                // Auto-import PICS data if database is sparse but JSON file exists
                // Depot mappings should be set up via initialization flow before log processing
                // Check depot count asynchronously without blocking startup
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var scopedDb = _serviceProvider.CreateScopedDbContext();
                        var depotCount = await scopedDb.DbContext.SteamDepotMappings.CountAsync();
                        _logger.LogInformation("Starting log processing with {DepotCount} depot mappings available", depotCount);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to check depot count before log processing");
                    }
                });
            }

            // Start Rust process
            // Now passing log directory instead of single file path
            // Rust processor will discover all access.log* files (including .1, .2, .gz, .zst)
            // Pass auto_map_depots flag: Always 1 to map depots during processing (avoids showing "Unknown Game" in Active tab)
            // This ensures downloads are properly mapped before appearing in the UI
            // Pass datasource name for multi-datasource support (records will be tagged with this name)
            var autoMapDepots = 1;
            var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                rustExecutablePath,
                $"\"{logDirectory}\" \"{progressPath}\" {startPosition} {autoMapDepots} \"{datasourceName}\" \"{positionsPath}\"",
                Path.GetDirectoryName(rustExecutablePath));

            // Pass TZ environment variable to Rust processor so it uses the correct timezone
            var tz = Environment.GetEnvironmentVariable("TZ");
            if (!string.IsNullOrEmpty(tz))
            {
                startInfo.EnvironmentVariables["TZ"] = tz;
                _logger.LogInformation("Passing TZ={TimeZone} to Rust processor", tz);
            }

            var startTime = DateTime.UtcNow;

            // Snapshotted inside the run callback BEFORE the monitor-stopping Cancel() call
            // below poisons the token: "cancelled" is an intent this host expressed, and the
            // terminal checkpoint alone must not be able to claim it (C# owns intent).
            var cancelRequestedDuringRun = false;

            var exitCode = await _rustProcessHelper.RunTrackedProcessAsync(
                startInfo,
                _currentOperationId,
                processingToken,
                async process =>
                {
                    // log_processor.rs's stdout NDJSON protocol only emits started/complete/failed
                    // lifecycle events (confirmed by reading log_processor.rs directly) — unlike
                    // cache_clear/cache_game_detect it does NOT emit intermediate percent ticks
                    // over stdout, so MonitorProgressAsync's file poll below remains the only
                    // source of granular line/byte progress and stays unchanged. Consuming stdout
                    // via the new structured event parser (instead of the old raw-line stdout logger's
                    // "log every raw JSON line at Info") turns what used to be raw-JSON log spam
                    // into clean, low-noise lifecycle log lines; stderr keeps its own line logger.
                    var stdoutTask = _rustProcessHelper.ConsumeStdoutProgressEventsAsync(
                        process,
                        onProgressEvent: evt =>
                        {
                            _logger.LogInformation("[Rust log processor] {Event} ({StageKey})", evt.Event, evt.StageKey);
                            return Task.CompletedTask;
                        },
                        processLabel: "log_processor",
                        processingToken);

                    var stderrTask = Task.Run(async () =>
                    {
                        string? line;
                        while ((line = await process.StandardError.ReadLineAsync(processingToken)) != null)
                        {
                            if (!string.IsNullOrEmpty(line))
                            {
                                _logger.LogInformation("[Rust log processor stderr] {Line}", line);
                            }
                        }
                    });

                    // The progress monitor gets its OWN linked token: cancelling the shared
                    // operation CTS after a child exits used to make a multi-datasource batch
                    // abort before its remaining datasources ever ran.
                    using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(processingToken);

                    // Same load guard as the started event: a live pass reports no progress.
                    if (!liveIngest)
                    {
                        var startingPercent = ScaleBatchProgress(batch, 0);
                        var startingEntries = batch?.EntriesProcessed ?? 0;
                        var startingLines = batch?.LinesProcessed ?? 0;
                        var startingTotalLines = batch?.TotalLines ?? 0;
                        var startingBytes = batch?.BytesProcessed ?? 0;
                        var startingTotalBytes = batch?.TotalBytes ?? 0;
                        var accepted = false;
                        _operationTracker.UpdateProgress(
                            ownerOperationId!.Value,
                            startingPercent,
                            "signalr.logProcessing.starting",
                            onProgress: operation =>
                            {
                                operation.Metadata = new LogProcessingTerminalMetrics(
                                    startingEntries,
                                    startingLines,
                                    null,
                                    null,
                                    "signalr.logProcessing.starting");
                                accepted = true;
                            });
                        if (accepted)
                        {
                            await _notifications.NotifyAllAsync(SignalREvents.LogProcessingProgress, new
                            {
                                OperationId = ownerOperationId,
                                PercentComplete = startingPercent,
                                Status = OperationStatus.Running,
                                StageKey = "signalr.logProcessing.starting",
                                Context = new Dictionary<string, object?>(),
                                TotalLines = startingTotalLines,
                                LinesParsed = startingLines,
                                EntriesSaved = startingEntries,
                                MbProcessed = Math.Round(startingBytes / (1024.0 * 1024.0), 1),
                                MbTotal = Math.Round(startingTotalBytes / (1024.0 * 1024.0), 1)
                            });
                        }
                    }
                    _progressMonitorTask = Task.Run(
                        async () => await MonitorProgressAsync(
                            progressPath,
                            monitorCts.Token,
                            emitLogProgress: !liveIngest,
                            riotMappingRun,
                            ownerOperationId!.Value,
                            batch));

                    await process.WaitForExitAsync(processingToken);

                    cancelRequestedDuringRun = processingToken.IsCancellationRequested;

                    _logger.LogInformation("Rust processor exited with code {ExitCode}", process.ExitCode);

                    await _rustProcessHelper.AwaitOutputTasksAsync(stdoutTask, stderrTask, TimeSpan.FromSeconds(5));

                    monitorCts.Cancel();
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

                    return process.ExitCode;
                },
                processLabel: "log_processor");

            // The polled progress file is the single authority on the run outcome. The
            // typed terminal_status decides success/warning/partial/failure; "cancelled"
            // additionally requires that this host actually requested cancellation.
            finalProgress = await ReadProgressFileAsync(progressPath);
            var hasTerminalCheckpoint = IsValidTerminalCheckpoint(finalProgress);
            var wasCancelled = cancelRequestedDuringRun &&
                ((hasTerminalCheckpoint && finalProgress!.TerminalStatus == "cancelled") ||
                 finalProgress?.Status == OperationStatus.Cancelled.ToWireString() ||
                 exitCode != 0);

            if (finalProgress is not null)
            {
                await riotMappingRun.ReportAsync(
                    finalProgress.RiotHostsProcessed,
                    finalProgress.RiotHostsMapped,
                    finalProgress.PercentComplete);
            }

            var riotSuccess = exitCode == 0
                && hasTerminalCheckpoint
                && finalProgress!.TerminalStatus is "completed" or "completed_with_warnings";
            // Warnings still count as success, but they stop the run claiming there was nothing to
            // resolve when it in fact tried and got nowhere.
            var riotCompletedCleanly = exitCode == 0
                && hasTerminalCheckpoint
                && finalProgress!.TerminalStatus is "completed";
            var riotError = riotSuccess || wasCancelled
                ? null
                : finalProgress?.TerminalStatus is { Length: > 0 } terminalStatus
                    ? $"Log processing ended with {terminalStatus}"
                    : $"Log processing failed with exit code {exitCode}";
            await riotMappingRun.CompleteAsync(
                riotSuccess,
                wasCancelled,
                // No attribution: this run's token is linked to the host's, so a shutdown mid-run
                // reaches here identically to someone pressing cancel.
                wasCancelled ? null : riotError,
                riotCompletedCleanly);

            // Committed rows become visible NOW, before any cancellation/partial/success
            // branching and before the post-passes below (auto-tag and the Epic/Blizzard/Xbox
            // resolves each emit their own conditional refresh when they change rows). A
            // partial or cancelled run whose earlier batches committed still exposes that
            // data here without ever being reported as a completed operation.
            await NotifyCommittedDownloadsAsync(finalProgress);

            if (wasCancelled)
            {
                _logger.LogInformation("Processing was cancelled (exit code: {ExitCode}, progress: {Progress}%)",
                    exitCode, finalProgress?.PercentComplete ?? 0);

                // Complete the operation with cancellation status. The single terminal
                // LogProcessingComplete event is emitted by the onTerminalEmit closure (interactive
                // ops only); snapshot the final metrics by value first so the closure can read them.
                if (ownerOperationId.HasValue && shouldFinalizeOperation)
                {
                    terminalMetrics = new LogProcessingTerminalMetrics(
                        EntriesProcessed: finalProgress?.EntriesSaved ?? 0,
                        LinesProcessed: finalProgress?.LinesParsed ?? 0,
                        Elapsed: null,
                        Message: "Log processing was cancelled",
                        StageKey: null);
                    _operationTracker.CompleteOperation(ownerOperationId.Value, false, "Operation was cancelled", cancelled: true, onCompleting: operation => operation.Metadata = terminalMetrics);
                }

                return false;
            }

            if (exitCode == 0)
            {
                // Check if Rust reported a failure status despite exit code 0
                // This catches edge cases where errors were logged but the process still exited cleanly
                if (finalProgress?.Status == OperationStatus.Failed.ToWireString() ||
                    (hasTerminalCheckpoint && finalProgress!.TerminalStatus == "failed"))
                {
                    _logger.LogError("Rust processor exited with code 0 but reported failure: {StageKey}", finalProgress!.StageKey);

                    // Snapshot failure metrics for the onTerminalEmit closure, then complete the op
                    // (CompleteOperation fires the single terminal LogProcessingComplete event).
                    if (ownerOperationId.HasValue && shouldFinalizeOperation)
                    {
                        terminalMetrics = new LogProcessingTerminalMetrics(
                            EntriesProcessed: 0,
                            LinesProcessed: finalProgress.LinesParsed,
                            Elapsed: null,
                            Message: finalProgress.StageKey ?? "Log processing failed",
                            StageKey: finalProgress.StageKey);
                        _operationTracker.CompleteOperation(ownerOperationId.Value, false, finalProgress.StageKey, onCompleting: operation => operation.Metadata = terminalMetrics);
                    }

                    return false;
                }

                // Exit code 0 without a valid terminal checkpoint means the processor died
                // between its last progress tick and its terminal write (or an incompatible
                // binary is installed). Positions must NOT be persisted from a non-terminal
                // snapshot, so this is a failure, not a quiet success.
                if (!hasTerminalCheckpoint)
                {
                    _logger.LogError(
                        "Rust processor exited with code 0 but left no valid terminal checkpoint (schema {Schema}, terminal '{Terminal}')",
                        finalProgress?.SchemaVersion ?? 0, finalProgress?.TerminalStatus ?? "<none>");
                    if (ownerOperationId.HasValue && shouldFinalizeOperation)
                    {
                        terminalMetrics = new LogProcessingTerminalMetrics(
                            EntriesProcessed: finalProgress?.EntriesSaved ?? 0,
                            LinesProcessed: finalProgress?.LinesParsed ?? 0,
                            Elapsed: null,
                            Message: "Log processing ended without a valid completion checkpoint",
                            StageKey: null);
                        _operationTracker.CompleteOperation(ownerOperationId.Value, false,
                            "Log processing ended without a valid completion checkpoint", onCompleting: operation => operation.Metadata = terminalMetrics);
                    }
                    return false;
                }

                // A partial run saved what it could but hit per-file errors: positions are
                // persisted for every stem Rust reached, and the operation surfaces as
                // failed-with-detail, never plain success.
                if (finalProgress!.TerminalStatus == "partial")
                {
                    PersistIngestDiagnostics(datasourceName!, finalProgress, startPositions);
                    var mergedPositions = MergedSourcePositions(datasourceName!, finalProgress);
                    if (mergedPositions != null)
                    {
                        // Rust reports what it read for each reached stem. A rotated member error can
                        // lower that value, while a current-file or database error never publishes a
                        // value below the pass's start. Saving the map avoids replaying completed files.
                        _stateService.SetLogSourcePositions(datasourceName!, mergedPositions);
                    }

                    var partialMessage =
                        $"Log processing finished with {finalProgress.FilesWithErrors.Count} file error(s); " +
                        $"{finalProgress.EntriesSaved} entries were saved";
                    _logger.LogError("{Message}: {Files}", partialMessage,
                        string.Join("; ", finalProgress.FilesWithErrors));
                    if (ownerOperationId.HasValue && shouldFinalizeOperation)
                    {
                        terminalMetrics = new LogProcessingTerminalMetrics(
                            EntriesProcessed: finalProgress.EntriesSaved,
                            LinesProcessed: finalProgress.LinesParsed,
                            Elapsed: null,
                            Message: partialMessage,
                            StageKey: null);
                        _operationTracker.CompleteOperation(ownerOperationId.Value, false, partialMessage, onCompleting: operation => operation.Metadata = terminalMetrics);
                    }
                    return false;
                }

                // Success is an ALLOWLIST, not "anything that wasn't rejected": a checkpoint
                // that says cancelled without this host having requested cancellation (or any
                // future terminal value) must never persist positions or report success.
                if (finalProgress.TerminalStatus is not ("completed" or "completed_with_warnings"))
                {
                    PersistIngestDiagnostics(datasourceName!, finalProgress, startPositions);
                    var unexpectedMessage =
                        $"Log processing ended with unexpected status '{finalProgress.TerminalStatus}'";
                    _logger.LogError("{Message}", unexpectedMessage);
                    if (ownerOperationId.HasValue && shouldFinalizeOperation)
                    {
                        terminalMetrics = new LogProcessingTerminalMetrics(
                            EntriesProcessed: finalProgress.EntriesSaved,
                            LinesProcessed: finalProgress.LinesParsed,
                            Elapsed: null,
                            Message: unexpectedMessage,
                            StageKey: null);
                        _operationTracker.CompleteOperation(ownerOperationId.Value, false, unexpectedMessage, onCompleting: operation => operation.Metadata = terminalMetrics);
                    }
                    return false;
                }

                // Normal completion - send completion with actual data
                if (finalProgress != null)
                {
                    var mergedPositions = MergedSourcePositions(datasourceName!, finalProgress);
                    var diagnostics = IngestDiagnosticsFor(finalProgress, startPositions);
                    _stateService.RecordLogIngestPass(
                        datasourceName!,
                        mergedPositions,
                        mergedPositions is null ? null : finalProgress.TotalLines,
                        diagnostics);

                    // A live pass sends no final progress, for the same load reason as its started event.
                    if (!liveIngest)
                    {
                        var finalPercent = ScaleBatchProgress(batch, 100);
                        var finalEntries = (batch?.EntriesProcessed ?? 0) +
                            Math.Max(batch?.CurrentEntriesProcessed ?? 0, finalProgress.EntriesSaved);
                        var finalLines = (batch?.LinesProcessed ?? 0) +
                            Math.Max(batch?.CurrentLinesProcessed ?? 0, finalProgress.LinesParsed);
                        var finalTotalLines = (batch?.TotalLines ?? 0) +
                            Math.Max(batch?.CurrentTotalLines ?? 0, finalProgress.TotalLines);
                        var finalBytes = (batch?.BytesProcessed ?? 0) +
                            Math.Max(batch?.CurrentBytesProcessed ?? 0, finalProgress.BytesProcessed);
                        var finalTotalBytes = (batch?.TotalBytes ?? 0) +
                            Math.Max(batch?.CurrentTotalBytes ?? 0, finalProgress.TotalBytes);

                        await _notifications.NotifyAllAsync(SignalREvents.LogProcessingProgress, new
                        {
                            OperationId = ownerOperationId,
                            PercentComplete = finalPercent,
                            Status = batch is null ? OperationStatus.Completed : OperationStatus.Running,
                            StageKey = "signalr.logProcessing.complete",
                            Context = new Dictionary<string, object?>(),
                            TotalLines = finalTotalLines,
                            LinesParsed = finalLines,
                            EntriesSaved = finalEntries,
                            MbProcessed = Math.Round(finalBytes / (1024.0 * 1024.0), 1),
                            MbTotal = Math.Round(finalTotalBytes / (1024.0 * 1024.0), 1)
                        });
                    }
                }

                // Rust processor automatically maps depots during processing (auto_map_depots = 1);
                // game images are fetched from the Steam API in the background task below.
                if (finalProgress?.EntriesSaved > 0)
                {
                    // Auto-tag new downloads to active events for BOTH live and interactive
                    // passes (prevents duplicate grouping issues). The committed-boundary refresh
                    // above already made the inserted rows visible; the tag pass emits its own
                    // conditional DownloadsRefresh when it changes associations.
                    await AutoTagNewDownloadsAsync();
                }

                // Resolve Epic downloads BEFORE the UI refresh so downloads show with game names.
                // This runs unconditionally (not gated on EntriesSaved > 0) because new CDN patterns
                // may have been added since the last run (e.g., a new user contributed patterns),
                // allowing previously unresolved downloads to be matched. The method itself is
                // efficient and no-ops when there are no unresolved Epic downloads.
                try
                {
                    using var epicScope = _serviceProvider.CreateScope();
                    var epicMappingService = epicScope.ServiceProvider.GetRequiredService<EpicMappingService>();
                    var resolved = await epicMappingService.ResolveDownloadsAsync();
                    if (resolved > 0)
                    {
                        Interlocked.Exchange(ref _epicRowsResolvedPending, 1);
                        _logger.LogInformation("Resolved {Count} Epic downloads to game names after log processing", resolved);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resolve Epic downloads (non-fatal)");
                }

                // Resolve Blizzard / Battle.net downloads the same way. Blizzard games are named
                // from the static, compiled-in TACT catalog at ingest, but downloads ingested
                // before a catalog entry existed stay unnamed; re-running the re-map after each
                // log process names them automatically (mirroring Epic above), so no manual
                // "Apply Now" card is needed. The service singleton no-ops when nothing is
                // unresolved and emits its own DownloadsRefresh when it renames rows.
                try
                {
                    var battleNetMappingService = _serviceProvider.GetRequiredService<LancacheManager.Core.Services.BattleNet.BattleNetMappingService>();
                    // An interactive pass shows the resolve as a background row; a live pass draws
                    // nothing unless it fails.
                    var resolvedBlizzard = await battleNetMappingService.ResolveDownloadsAsync(
                        liveIngest
                            ? new RunNotice(NotificationMode.Hidden, RunTrigger.Scheduled)
                            : new RunNotice(NotificationMode.Silent, RunTrigger.Manual));
                    if (resolvedBlizzard > 0)
                    {
                        _logger.LogInformation("Resolved {Count} Blizzard downloads to game names after log processing", resolvedBlizzard);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resolve Blizzard downloads (non-fatal)");
                }

                // Rust ingest continues rows that ended within five minutes of the log time. The Xbox
                // backfill renames a row only through a conditional update that re-checks that the row
                // is inactive and unnamed at write time.
                try
                {
                    var xboxMappingService = _serviceProvider.GetRequiredService<LancacheManager.Core.Services.Xbox.XboxMappingService>();
                    var resolvedXbox = await xboxMappingService.ResolveDownloadsAsync();
                    if (resolvedXbox > 0)
                    {
                        _logger.LogInformation("Re-tagged {Count} wsus downloads to Xbox titles after log processing", resolvedXbox);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resolve Xbox downloads (non-fatal)");
                }

                // Image fetching can run in background as it's not critical for the UI refresh
                _ = Task.Run(async () =>
                {
                    if (!await _postPassLock.WaitAsync(0))
                    {
                        _logger.LogDebug("Skipping image pass after log processing - one is already running");
                        return;
                    }

                    try
                    {
                        // Brief settle delay so any final DB writes from the Rust process are durable
                        // before we fetch images. (The live dashboard-batch cache is invalidated
                        // synchronously in the finalize step below, before the UI refresh signal —
                        // see InvalidateLiveCache.)
                        await Task.Delay(500);

                        var namedCount = finalProgress?.EntriesSaved > 0
                            ? await FetchMissingGameNamesAsync()
                            : 0;
                        var epicRowsResolved = Interlocked.Exchange(ref _epicRowsResolvedPending, 0) != 0;
                        await FetchMissingEpicImagesAsync(epicRowsResolved);

                        // The Rust processor maps depots itself, so the SteamKit2 mapping trigger never
                        // fires for games it identified. Start a banner pass when one of them has no
                        // stored art yet, instead of leaving it blank until the next scheduled tick.
                        try
                        {
                            using var imageFetchScope = _serviceProvider.CreateScope();
                            var imageFetchService = imageFetchScope.ServiceProvider.GetRequiredService<GameImageFetchService>();
                            await imageFetchService.StartFetchForMissingArtAsync(
                                namedCount > 0,
                                CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Banner fetch trigger after log processing failed (non-fatal)");
                        }
                    }
                    finally
                    {
                        _postPassLock.Release();
                    }
                });

                if (!liveIngest && shouldFinalizeOperation)
                {
                    // Set IsProcessing to false BEFORE completing so polling can detect completion
                    // This is critical for the initialization wizard step 5 to detect completion
                    if (_currentOperationId == ownerOperationId) IsProcessing = false;

                    // Snapshot success metrics for the onTerminalEmit closure (the single terminal
                    // event fires from CompleteOperation below).
                    var finalElapsed = DateTime.UtcNow - startTime;
                    var completionMessage = finalProgress?.TerminalStatus == "completed_with_warnings"
                        ? $"Log processing completed with warnings: {finalProgress.UnparsedLines} unrecognized line(s), " +
                          $"{finalProgress.HintlessHttpDetailedLines} line(s) without a service, " +
                          $"{finalProgress.InvalidEncodingLines} line(s) with invalid encoding"
                        : "Log processing completed successfully";
                    terminalMetrics = new LogProcessingTerminalMetrics(
                        EntriesProcessed: finalProgress?.EntriesSaved ?? 0,
                        LinesProcessed: finalProgress?.LinesParsed ?? 0,
                        Elapsed: Math.Round(finalElapsed.TotalMinutes, 1),
                        Message: completionMessage,
                        StageKey: "signalr.logProcessing.complete");
                }
                else if (shouldFinalizeOperation)
                {
                    // A live pass sets IsProcessing to false immediately. No trailing
                    // refresh here: the committed-boundary DownloadsRefresh already fired, and
                    // the auto-tag/mapping passes emit their own conditional refreshes when
                    // they change rows.
                    if (_currentOperationId == ownerOperationId) IsProcessing = false;
                }

                // Complete the operation successfully
                if (ownerOperationId.HasValue && shouldFinalizeOperation)
                {
                    _operationTracker.CompleteOperation(ownerOperationId.Value, true, onCompleting: operation => operation.Metadata = terminalMetrics);
                }

                childSucceeded = true;
                return true;
            }
            else
            {
                // Non-zero exit code but not cancelled - this is an actual error
                _logger.LogError("Rust processor failed with exit code {ExitCode}", exitCode);

                // Snapshot error metrics for the onTerminalEmit closure, then complete the op
                // (CompleteOperation fires the single terminal LogProcessingComplete event).
                if (ownerOperationId.HasValue && shouldFinalizeOperation)
                {
                    terminalMetrics = new LogProcessingTerminalMetrics(
                        EntriesProcessed: 0,
                        LinesProcessed: 0,
                        Elapsed: null,
                        Message: $"Log processing failed with exit code {exitCode}",
                        StageKey: null);
                    _operationTracker.CompleteOperation(ownerOperationId.Value, false, $"Log processing failed with exit code {exitCode}", onCompleting: operation => operation.Metadata = terminalMetrics);
                }

                return false;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Log processing was cancelled for datasource '{DatasourceName}'", datasourceName);

            // This run's own id. It is null only when the cancellation arrived before the operation
            // was registered; a universal force-kill that already ran the terminal cleanup is caught
            // by the status check below instead.
            var cancelOpId = ownerOperationId;

            // If a universal force-kill already completed this op (or already cleared the id),
            // suppress the duplicate SignalR completion + CompleteOperation so only ONE
            // terminal event is emitted.
            var alreadyTerminal = !cancelOpId.HasValue
                || _operationTracker.GetOperation(cancelOpId.Value)?.Status.IsTerminal() == true;

            if (!alreadyTerminal && cancelOpId.HasValue)
            {
                if (shouldFinalizeOperation)
                {
                    // Snapshot the cancelled message for the onTerminalEmit closure, then complete
                    // (CompleteOperation fires the single terminal LogProcessingComplete event).
                    terminalMetrics = new LogProcessingTerminalMetrics(
                        EntriesProcessed: 0,
                        LinesProcessed: 0,
                        Elapsed: null,
                        Message: "Log processing was cancelled",
                        StageKey: null);
                    _operationTracker.CompleteOperation(cancelOpId.Value, false, "Operation was cancelled", cancelled: true, onCompleting: operation => operation.Metadata = terminalMetrics);
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting Rust log processor");

            // Snapshot error metrics for the onTerminalEmit closure, then complete the op
            // (CompleteOperation fires the single terminal LogProcessingComplete event).
            if (ownerOperationId.HasValue && shouldFinalizeOperation)
            {
                terminalMetrics = new LogProcessingTerminalMetrics(
                    EntriesProcessed: 0,
                    LinesProcessed: 0,
                    Elapsed: null,
                    Message: $"Log processing error: {ex.Message}",
                    StageKey: null);
                _operationTracker.CompleteOperation(ownerOperationId.Value, false, ex.Message, onCompleting: operation => operation.Metadata = terminalMetrics);
            }

            return false;
        }
        finally
        {
            if (riotMappingRun is not null)
            {
                await riotMappingRun.DisposeAsync();
            }

            if (batch is not null)
            {
                RecordBatchChild(batch, datasourceName!, childSucceeded, finalProgress);
            }

            if (shouldFinalizeOperation)
            {
                // Tear down only the state this run installed. A live tick that passed the re-entry
                // guard during the interactive display window has already registered its own
                // operation into the field and its own cancellation source; disposing those here
                // would leave that run with no way to be cancelled. When the field no longer holds
                // this run's id, the newer registration owns them, and a terminal cleanup that
                // already cleared the field has done this teardown itself.
                if (OwnsOperationState(_currentOperationId, ownerOperationId))
                {
                    EndOperation();
                }
            }
            else if (_currentOperationId == ownerOperationId)
            {
                _currentDatasourceName = null;
                _currentProgressPath = null;
            }
        }
    }

    /// <summary>
    /// Holds the processing gate for reset-to-end while it counts the logs and writes the end
    /// positions. Returns null when a pass is running. Registers a hidden tracked LogProcessing
    /// operation, so every path that asks the conflict checker sees the count.
    /// </summary>
    internal async Task<Guid?> TryReserveProcessingGateAsync()
    {
        await _startLock.WaitAsync();
        try
        {
            if (IsProcessing)
            {
                return null;
            }

            var reservationId = _operationTracker.RegisterOperation(
                OperationType.LogProcessing,
                "Reset log position to end",
                new CancellationTokenSource(),
                notice: new RunNotice(NotificationMode.Hidden, RunTrigger.Manual));
            _processingGateReservation = reservationId;
            return reservationId;
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>
    /// Completes the reservation's operation and reopens the gate if this reservation still holds it.
    /// </summary>
    internal void ReleaseProcessingGate(Guid reservationId)
    {
        _startLock.Wait();
        try
        {
            if (_processingGateReservation == reservationId)
            {
                _processingGateReservation = null;
            }
        }
        finally
        {
            _startLock.Release();
        }

        _operationTracker.CompleteOperation(reservationId, success: true);
    }

    private Task MonitorProgressAsync(
        string progressPath,
        CancellationToken cancellationToken,
        bool emitLogProgress,
        RiotMappingRunReporter riotMappingRun,
        Guid operationId,
        LogProcessingBatchState? batch)
    {
        var loggedWarnings = new HashSet<string>();
        var loggedErrors = new HashSet<string>();

        var monitor = new RustProgressMonitor<LogProcessingProgress>(_rustProcessHelper, _logger);
        return monitor.MonitorAsync(progressPath, async (LogProcessingProgress progress) =>
        {
            await riotMappingRun.ReportAsync(
                progress.RiotHostsProcessed,
                progress.RiotHostsMapped,
                progress.PercentComplete);

            if (!emitLogProgress)
            {
                return;
            }

            // Log any new warnings
            foreach (var warning in progress.Warnings)
            {
                if (loggedWarnings.Add(warning))
                {
                    _logger.LogWarning("[Rust] {Warning}", warning);
                }
            }

            // Log any new errors
            foreach (var error in progress.Errors)
            {
                if (loggedErrors.Add(error))
                {
                    _logger.LogError("[Rust] {Error}", error);
                }
            }

            var percentComplete = ScaleBatchProgress(batch, progress.PercentComplete);
            var totalLines = progress.TotalLines;
            var linesProcessed = progress.LinesParsed;
            var entriesProcessed = progress.EntriesSaved;
            var bytesProcessed = progress.BytesProcessed;
            var totalBytes = progress.TotalBytes;
            if (batch is not null)
            {
                batch.CurrentEntriesProcessed = Math.Max(batch.CurrentEntriesProcessed, progress.EntriesSaved);
                batch.CurrentLinesProcessed = Math.Max(batch.CurrentLinesProcessed, progress.LinesParsed);
                batch.CurrentTotalLines = Math.Max(batch.CurrentTotalLines, progress.TotalLines);
                batch.CurrentBytesProcessed = Math.Max(batch.CurrentBytesProcessed, progress.BytesProcessed);
                batch.CurrentTotalBytes = Math.Max(batch.CurrentTotalBytes, progress.TotalBytes);
                totalLines = batch.TotalLines + batch.CurrentTotalLines;
                linesProcessed = batch.LinesProcessed + batch.CurrentLinesProcessed;
                entriesProcessed = batch.EntriesProcessed + batch.CurrentEntriesProcessed;
                bytesProcessed = batch.BytesProcessed + batch.CurrentBytesProcessed;
                totalBytes = batch.TotalBytes + batch.CurrentTotalBytes;
            }

            var mbTotal = totalBytes / (1024.0 * 1024.0);
            var mbProcessed = bytesProcessed / (1024.0 * 1024.0);

            var accepted = false;
            var metrics = new LogProcessingTerminalMetrics(entriesProcessed, linesProcessed,
                null, null, progress.StageKey);
            _operationTracker.UpdateProgress(operationId, percentComplete, progress.StageKey ?? "",
                onProgress: operation =>
                {
                    operation.Metadata = metrics;
                    accepted = true;
                });
            if (!accepted) return;

            // Send progress update via SignalR with standardized format
            await _notifications.NotifyAllAsync(SignalREvents.LogProcessingProgress, new
            {
                OperationId = operationId,
                PercentComplete = percentComplete,
                Status = OperationStatus.Running,
                StageKey = progress.StageKey,
                Context = progress.Context,
                TotalLines = totalLines,
                LinesParsed = linesProcessed,
                EntriesSaved = entriesProcessed,
                MbProcessed = Math.Round(mbProcessed, 1),
                MbTotal = Math.Round(mbTotal, 1)
            });
        }, cancellationToken);
    }

    private async Task<LogProcessingProgress?> ReadProgressFileAsync(string progressPath)
    {
        return await _rustProcessHelper.ReadProgressFileAsync<LogProcessingProgress>(progressPath);
    }

    /// <summary>
    /// Persists the run's diagnostic counters. Warning-clear rule: an anomaly-free run
    /// only overwrites (and thereby clears) previous warnings when it examined meaningful
    /// new input — a no-op run must never launder away a standing warning. "New input" is
    /// judged per stem (one stem growing while another shrinks must still count), and an
    /// incomplete final record is a real observation worth recording too.
    /// </summary>
    private void PersistIngestDiagnostics(
        string datasourceName, LogProcessingProgress progress, Dictionary<string, long> startPositions)
    {
        var diagnostics = IngestDiagnosticsFor(progress, startPositions);
        if (diagnostics != null)
        {
            _stateService.SetLogIngestDiagnostics(datasourceName, diagnostics);
        }
    }

    private static LogIngestDiagnostics? IngestDiagnosticsFor(
        LogProcessingProgress progress,
        Dictionary<string, long> startPositions)
    {
        var examinedNewInput = progress.SourcePositions.Count > 0
            ? progress.SourcePositions.Any(kvp =>
                kvp.Value > startPositions.GetValueOrDefault(kvp.Key, 0))
            : progress.LinesParsed > startPositions.Values.Sum();
        // IncompleteFinalRecords is deliberately NOT an anomaly trigger: a live file is
        // mid-line almost every tick, and letting that overwrite counters would clear a
        // standing warning without meaningful new input. It still persists (and displays)
        // whenever a run qualifies through the conditions below.
        var hasAnomalies = progress.UnparsedLines > 0
            || progress.HintlessHttpDetailedLines > 0
            || progress.InvalidEncodingLines > 0
            || progress.FilesWithErrors.Count > 0;

        if (!hasAnomalies && !examinedNewInput)
        {
            return null;
        }

        return new LogIngestDiagnostics
        {
            Layout = progress.Layout,
            TerminalStatus = progress.TerminalStatus,
            UnparsedLines = progress.UnparsedLines,
            HintlessHttpDetailedLines = progress.HintlessHttpDetailedLines,
            SkippedFallbackLines = progress.SkippedFallbackLines,
            InvalidEncodingLines = progress.InvalidEncodingLines,
            RecognizedIgnoredLines = progress.RecognizedIgnoredLines,
            IncompleteFinalRecords = progress.IncompleteFinalRecords,
            FilesWithErrors = new List<string>(progress.FilesWithErrors),
            LastRunUtc = DateTime.UtcNow
        };
    }

    private Dictionary<string, long>? MergedSourcePositions(
        string datasourceName,
        LogProcessingProgress progress)
    {
        if (progress.SourcePositions.Count == 0)
        {
            return null;
        }

        var mergedPositions = _stateService.GetLogSourcePositions(datasourceName);
        foreach (var (stem, offset) in progress.SourcePositions)
        {
            mergedPositions[stem] = offset;
        }

        return mergedPositions;
    }

    /// <summary>
    /// Fetches game names for downloads that have a GameAppId but no GameName.
    /// Image bytes are fetched exclusively by <see cref="GameImageFetchService"/> (3-tier pipeline).
    /// This method only updates GameName, which GameImageFetchService does not enrich.
    /// </summary>
    private async Task<int> FetchMissingGameNamesAsync()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var maxDownloadId = await context.Downloads.MaxAsync(d => (long?)d.Id) ?? 0;
            if (_steamNamesDrainedAtMaxId == maxDownloadId)
            {
                return 0;
            }

            // Find downloads that have GameAppId but missing game name - image bytes are now
            // fetched exclusively by GameImageFetchService (3-tier pipeline). We only update
            // GameName here, since GameImageFetchService does not enrich Download.GameName.
            var downloadsNeedingName = await context.Downloads
                .Where(d => d.GameAppId.HasValue && string.IsNullOrEmpty(d.GameName))
                .Take(50)
                .ToListAsync();

            if (downloadsNeedingName.Count == 0)
            {
                _steamNamesDrainedAtMaxId = maxDownloadId;
                return 0;
            }

            // Steam returns a name for every app, including a fallback when its lookup fails, so
            // successful rows are never asked twice. A continuation can add an app id to an old row;
            // the next inserted download advances this key and makes that row eligible again.
            var steamService = scope.ServiceProvider.GetRequiredService<SteamService>();
            _logger.LogInformation("Fetching game names for {Count} downloads", downloadsNeedingName.Count);

            int updated = 0;
            foreach (var download in downloadsNeedingName)
            {
                try
                {
                    var gameInfo = await steamService.GetGameInfoAsync(download.GameAppId!.Value);
                    if (gameInfo != null && !string.IsNullOrEmpty(gameInfo.Name))
                    {
                        download.GameName = gameInfo.Name;
                        updated++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch game info for app {AppId}", download.GameAppId);
                }
            }

            if (updated > 0)
            {
                await context.SaveChangesAsync();
                _logger.LogInformation("Updated {Count} downloads with game names", updated);

                // NOTE: We do not trigger GameImageFetchService here - it runs on its own schedule
                // and will fetch image bytes after game detection has completed.
                // NOTE: We do not send DownloadsRefresh here - the main completion handler
                // already sends DownloadsRefresh (live ingest) or LogProcessingComplete (interactive).
            }

            _steamNamesDrainedAtMaxId = downloadsNeedingName.Count < 50
                && updated == downloadsNeedingName.Count
                ? maxDownloadId
                : null;
            return updated;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching missing game names - this is non-critical");
            return 0;
        }
    }

    /// <summary>
    /// Sets GameImageUrl on Epic downloads that have been resolved to games but are missing images.
    /// Looks up image URLs from the EpicGameMappings table.
    /// </summary>
    private async Task FetchMissingEpicImagesAsync(bool epicRowsResolvedThisPass)
    {
        try
        {
            using var scopedDb = _serviceProvider.CreateScopedDbContext();
            var maxDownloadId = await scopedDb.DbContext.Downloads.MaxAsync(d => (long?)d.Id) ?? 0;
            var catalog = await scopedDb.DbContext.EpicGameMappings.CountAsync(m => m.ImageUrl != null);
            var catalogSeenUtc = await scopedDb.DbContext.EpicGameMappings
                .Where(m => m.ImageUrl != null)
                .MaxAsync(m => (DateTime?)m.LastSeenAtUtc);
            var scanKey = (MaxDownloadId: maxDownloadId, Catalog: catalog, CatalogSeenUtc: catalogSeenUtc);
            if (!epicRowsResolvedThisPass && _lastEpicImageScan == scanKey)
            {
                return;
            }

            // Find Epic downloads that have EpicAppId but missing GameImageUrl
            var downloadsNeedingImages = await scopedDb.DbContext.Downloads
                .Where(d => d.EpicAppId != null && string.IsNullOrEmpty(d.GameImageUrl))
                .Take(100)
                .ToListAsync();

            if (downloadsNeedingImages.Count == 0)
            {
                _lastEpicImageScan = scanKey;
                return;
            }

            // Load all Epic game mappings with image URLs for lookup
            var imageLookup = await scopedDb.DbContext.EpicGameMappings
                .Where(m => m.ImageUrl != null)
                .ToDictionaryAsync(m => m.AppId, m => m.ImageUrl);

            var updated = 0;
            foreach (var download in downloadsNeedingImages)
            {
                if (download.EpicAppId != null && imageLookup.TryGetValue(download.EpicAppId, out var imageUrl) && imageUrl != null)
                {
                    download.GameImageUrl = EpicApiDirectClient.EnsureResizeParams(imageUrl);
                    updated++;
                }
            }

            if (updated > 0)
            {
                await scopedDb.DbContext.SaveChangesAsync();
                _logger.LogInformation("Updated {Count} Epic downloads with game images", updated);
            }

            _lastEpicImageScan = updated == 0 ? scanKey : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching missing Epic game images - this is non-critical");
        }
    }

    /// <summary>
    /// Auto-tags newly processed downloads to any currently active events and returns the
    /// tagged count. Best-effort: a failure is logged and reported as zero tags, never as an
    /// operation failure. When rows were tagged, emits its own conditional DownloadsRefresh
    /// AFTER the service has committed the associations — DownloadAssociationsContext listens
    /// to DownloadsRefresh (not LogProcessingComplete), and the committed-boundary refresh
    /// fires before this pass runs, so without this emission a client could cache the newly
    /// inserted rows and never invalidate their association state.
    /// </summary>
    private async Task<int> AutoTagNewDownloadsAsync()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var eventsService = scope.ServiceProvider.GetRequiredService<IEventsService>();

            // AutoTagActiveEventsAsync saves its changes before returning the count, so the
            // refresh below is post-commit. NotifyAllAsync only: the cache generation bump
            // must precede the hub send (never fire-and-forget for DownloadsRefresh).
            var taggedCount = await eventsService.AutoTagActiveEventsAsync();
            if (taggedCount > 0)
            {
                _logger.LogInformation("Auto-tagged {Count} downloads to active events", taggedCount);

                await _notifications.NotifyAllAsync(SignalREvents.DownloadsRefresh, new
                {
                    source = "auto-tag",
                    taggedCount,
                    timestamp = DateTime.UtcNow
                });
            }

            return taggedCount;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error auto-tagging downloads to events - this is non-critical");
            return 0;
        }
    }

}
