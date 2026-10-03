using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Hubs;
using LancacheManager.Core.Interfaces;
using LancacheManager.Models;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Service that spawns the Rust log_manager for removing service entries from logs
/// Runs as background task with progress tracking to prevent HTTP timeouts
/// </summary>
public class RustLogRemovalService
{
    private readonly ILogger<RustLogRemovalService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly ISignalRNotificationService _notifications;
    private readonly CacheManagementService _cacheManagementService;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly NginxLogRotationService _nginxLogRotationService;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly IStateService _stateService;
    private readonly OperationStateService _operationStateService;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Guid? _currentTrackerOperationId;
    private TaskCompletionSource<Guid>? _operationRegisteredTcs;
    private LogRemovalCurrentProgress? _currentProgress;
    private long _progressRevision;

    private readonly DatasourceService _datasourceService;

    public bool IsProcessing { get; private set; }
    public string? CurrentService { get; private set; }
    public Guid? CurrentOperationId { get; private set; }
    public string? CurrentDatasource { get; private set; }

    /// <summary>
    /// Starts per-datasource service removal in the background and returns the operation id as soon as it is registered.
    /// </summary>
    public Task<Guid?> StartRemovalForDatasourceInBackgroundAsync(string service, string datasourceName)
    {
        return StartRemovalInBackgroundAsync(() => StartRemovalForDatasourceAsync(service, datasourceName));
    }

    private async Task<Guid?> StartRemovalInBackgroundAsync(Func<Task<bool>> processor)
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
                _logger.LogError(ex, "Unhandled error during log removal");
                return false;
            }
            finally
            {
                registered.TrySetCanceled();
            }
        });

        await Task.WhenAny(registered.Task, backgroundTask);
        return registered.Task.IsCompletedSuccessfully ? registered.Task.Result : (Guid?)null;
    }

    private void NotifyOperationRegistered()
    {
        if (_currentTrackerOperationId.HasValue)
        {
            CurrentOperationId = _currentTrackerOperationId;
            _operationRegisteredTcs?.TrySetResult(_currentTrackerOperationId.Value);
        }
    }

    /// <summary>
    /// Gets the removal status including isProcessing and service fields
    /// </summary>
    public LogRemovalStatusResponse GetRemovalStatus()
    {
        var progress = Volatile.Read(ref _currentProgress);
        return new LogRemovalStatusResponse
        {
            IsProcessing = IsProcessing,
            Service = CurrentService,
            Datasource = progress?.Datasource,
            OperationId = _currentTrackerOperationId,
            FilesProcessed = progress?.FilesProcessed ?? 0,
            LinesProcessed = progress?.LinesProcessed ?? 0,
            LinesRemoved = progress?.LinesRemoved ?? 0,
            PercentComplete = progress?.Snapshot.PercentComplete,
            Status = IsProcessing ? OperationStatus.Running : null,
            StageKey = progress?.Snapshot.StageKey,
            Context = progress?.Snapshot.Context ?? new Dictionary<string, object?>()
        };
    }

    public RustLogRemovalService(
        ILogger<RustLogRemovalService> logger,
        IPathResolver pathResolver,
        ISignalRNotificationService notifications,
        CacheManagementService cacheManagementService,
        RustProcessHelper rustProcessHelper,
        NginxLogRotationService nginxLogRotationService,
        IDbContextFactory<AppDbContext> dbContextFactory,
        DatasourceService datasourceService,
        IUnifiedOperationTracker operationTracker,
        IStateService stateService,
        OperationStateService operationStateService)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _notifications = notifications;
        _cacheManagementService = cacheManagementService;
        _rustProcessHelper = rustProcessHelper;
        _nginxLogRotationService = nginxLogRotationService;
        _datasourceService = datasourceService;
        _operationTracker = operationTracker;
        _stateService = stateService;
        _operationStateService = operationStateService;
    }

    public Task RestoreRepairAsync(
        OperationRepair repair,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var retained = repair.LogRemoval
            ?? throw new InvalidDataException(
                $"Operation repair {repair.Id} has no log-removal metrics.");
        var metrics = new LogRemovalCompletionMetrics(
            repair.Id,
            retained.Service,
            $"Removed {retained.Service} entries",
            $"Log removal for {retained.Service} failed",
            $"Service removal for {retained.Service} was cancelled",
            retained.FilesProcessed,
            retained.LinesProcessed,
            retained.LinesRemoved,
            retained.DatabaseRecordsDeleted,
            retained.Datasource,
            retained.StageKey);
        var source = new CancellationTokenSource();
        var restored = _operationTracker.TryRestoreOperation(
            repair.Id,
            OperationType.LogRemoval,
            repair.Name,
            source,
            new RemovalMetrics
            {
                EntityKind = "service",
                EntityKey = retained.Service.ToLowerInvariant(),
                EntityName = retained.Service
            },
            onTerminalEmit: BuildTerminalEmit(
                () => repair.Id,
                () => metrics,
                () => null),
            startedAt: repair.StartedAt,
            notice: repair.Notice,
            ownerCompletes: true);
        if (!restored)
        {
            source.Dispose();
        }
        return Task.CompletedTask;
    }

    public async Task ResumeRepairAsync(
        OperationRepair repair,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (repair.Type != OperationType.LogRemoval || repair.LogRemoval is null)
        {
            throw new InvalidDataException(
                $"Operation repair {repair.Id} has no log-removal contract.");
        }

        // A log step that started and never kept its positions runs again here, after the repair
        // reset those positions. It takes no cache lock: a removal job can hold that lock while its
        // StartWorkAsync waits for this repair.
        foreach (var source in repair.Sources.Where(OperationStateService.LogStepUnfinished))
        {
            var datasource = _datasourceService.GetDatasource(source.Datasource)
                ?? throw new InvalidDataException(
                    $"Datasource {source.Datasource} is unavailable for operation repair {repair.Id}.");
            var step = await RunLogRemovalStepAsync(
                repair.Id,
                repair.LogRemoval.Service,
                datasource,
                onProgress: null,
                cancellationToken);
            // A failed attempt throws so the repair retries it instead of completing with the step unfinished.
            step.Result.EnsureSuccess("log_service_manager", source.Datasource, cancellationToken);
            if (!step.Reopen.Success)
            {
                throw new IOException(step.Reopen.ErrorMessage!);
            }
        }
    }

    private OperationRepair BuildRepair(
        Guid operationId,
        string service,
        IReadOnlyCollection<ResolvedDatasource> datasources,
        string? datasource = null)
    {
        return new OperationRepair
        {
            Id = operationId,
            Type = OperationType.LogRemoval,
            Name = "Log Removal",
            StartedAt = DateTime.UtcNow,
            LogRemoval = new LogRemovalRepair
            {
                Service = service,
                Datasource = datasource
            },
            Sources = datasources.Select(source => new OperationRepairSource
            {
                Datasource = source.Name,
                LogRoot = source.LogPath,
                ResetLogPositions = true,
                RefreshDownloads = true
            }).ToList()
        };
    }

    private async Task FinishRepairAsync(
        Guid operationId,
        LogRemovalCompletionMetrics metrics,
        bool success,
        bool cancelled,
        string? error)
    {
        // The metrics ride in the outcome save, so one failed write takes the outcome's background
        // retry instead of throwing before the run reaches its terminal.
        await _operationStateService.FinishRepairAsync(
            operationId,
            success,
            cancelled,
            error,
            repair => repair.LogRemoval = new LogRemovalRepair
            {
                Service = metrics.Service,
                Datasource = metrics.Datasource,
                FilesProcessed = metrics.FilesProcessed,
                LinesProcessed = metrics.LinesProcessed,
                LinesRemoved = metrics.LinesRemoved,
                DatabaseRecordsDeleted = metrics.DatabaseRecordsDeleted,
                StageKey = metrics.StageKey
            });
    }

    private async Task SaveSourceAsync(
        Guid operationId,
        string datasource,
        LogRemovalCompletionMetrics metrics)
    {
        await _operationStateService.SaveRepairAsync(
            operationId,
            repair =>
            {
                var source = repair.Sources.Single(candidate =>
                    string.Equals(
                        candidate.Datasource,
                        datasource,
                        StringComparison.OrdinalIgnoreCase));
                source.NativeCompletionAccepted = true;
                repair.LogRemoval = new LogRemovalRepair
                {
                    Service = metrics.Service,
                    Datasource = metrics.Datasource,
                    FilesProcessed = metrics.FilesProcessed,
                    LinesProcessed = metrics.LinesProcessed,
                    LinesRemoved = metrics.LinesRemoved,
                    DatabaseRecordsDeleted = metrics.DatabaseRecordsDeleted,
                    StageKey = metrics.StageKey
                };
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Builds this run's terminal event from its winning metrics and accepted progress.
    /// </summary>
    private Func<OperationTerminalInfo, Task> BuildTerminalEmit(Func<Guid?> getOperationId,
        Func<LogRemovalCompletionMetrics> getMetrics,
        Func<LogRemovalCurrentProgress?> getProgress)
    {
        return info =>
        {
            var metrics = getMetrics();
            var progress = getProgress();
            if (progress != null)
                metrics = metrics with
                {
                    FilesProcessed = progress.FilesProcessed,
                    LinesProcessed = progress.LinesProcessed,
                    LinesRemoved = progress.LinesRemoved,
                    StageKey = progress.Snapshot.StageKey
                };
            var status = info.Cancelled
                ? OperationStatus.Cancelled
                : info.Success
                    ? OperationStatus.Completed
                    : OperationStatus.Failed;
            var message = info.Cancelled
                ? metrics.CancelMessage
                : info.Success
                    ? metrics.SuccessMessage
                    : (info.Error ?? metrics.FailureMessage);

            return _notifications.NotifyAllAsync(
                SignalREvents.LogRemovalComplete,
                new SignalRNotifications.LogRemovalComplete(
                    OperationId: getOperationId() ?? metrics.OperationId,
                    Success: info.Success,
                    Status: status,
                    Message: message,
                    Service: metrics.Service,
                    Cancelled: info.Cancelled,
                    FilesProcessed: metrics.FilesProcessed,
                    LinesProcessed: metrics.LinesProcessed,
                    LinesRemoved: metrics.LinesRemoved,
                    DatabaseRecordsDeleted: metrics.DatabaseRecordsDeleted,
                    Datasource: metrics.Datasource,
                    StageKey: metrics.StageKey,
                    Context: new Dictionary<string, object?>
                    {
                        ["service"] = metrics.Service,
                        ["datasourceName"] = metrics.Datasource,
                        ["linesRemoved"] = metrics.LinesRemoved
                    }));
        };
    }

    /// <summary>
    /// Starts service removal for a specific datasource only
    /// </summary>
    private async Task<bool> StartRemovalForDatasourceAsync(string service, string datasourceName)
    {
        // Sanitize user-provided inputs to prevent process argument injection
        service = RustProcessHelper.SanitizeProcessArgument(service);
        datasourceName = RustProcessHelper.SanitizeProcessArgument(datasourceName);

        string logDir;

        await _startLock.WaitAsync();
        try
        {
            if (IsProcessing)
            {
                _logger.LogWarning("Log removal is already running for service: {CurrentService}", CurrentService);
                return false;
            }

            var datasource = _datasourceService.GetDatasource(datasourceName);
            if (datasource == null)
            {
                _logger.LogError("Datasource '{DatasourceName}' not found", datasourceName);
                return false;
            }

            if (!datasource.LogsWritable)
            {
                _logger.LogError("Logs directory is read-only for datasource '{DatasourceName}'", datasourceName);
                return false;
            }

            logDir = datasource.LogPath;
            IsProcessing = true;
            CurrentService = service;
            CurrentDatasource = datasourceName;
        }
        finally
        {
            _startLock.Release();
        }

        Guid? operationId = null;
        var progressEmitGate = new ProgressEmitGate();
        var cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;
        var completionMetrics = new LogRemovalCompletionMetrics(
            Guid.Empty, service, $"Removed {service} entries",
            $"Log removal for {service} failed",
            $"Service removal for {service} was cancelled", Datasource: datasourceName);
        var publishedMetrics = completionMetrics;
        LogRemovalCurrentProgress? currentProgress = null;
        Action<LogRemovalCurrentProgress> publishProgress = value => currentProgress = value;
        var repairPrepared = false;
        string? completionError = null;

        try
        {
            _cancellationTokenSource = cancellationTokenSource;

            // Register with unified operation tracker for centralized cancellation.
            // Service-scoped metadata so OperationConflictChecker.DeriveScope yields
            // ConflictScope.Service(service): EntityKind="service" + lowercased EntityKey
            // match ConflictScope.Service(service) (Ordinal compare requires identical casing).
            // onTerminalEmit emits the terminal LogRemovalComplete event EXACTLY ONCE from inside
            // CompleteOperation (CompletedFlag-gated), so no terminal NotifyAll/SendOperationComplete
            // is issued directly from the success / cancel / error paths below.
            operationId = _operationTracker.RegisterOperation(
                OperationType.LogRemoval,
                "Log Removal",
                _cancellationTokenSource,
                new RemovalMetrics
                {
                    EntityKind = "service",
                    EntityKey = service.ToLowerInvariant(),
                    EntityName = service
                },
                onTerminalCleanup: () =>
                {
                    if (_currentTrackerOperationId != operationId) return;
                    IsProcessing = false;
                    Volatile.Write(ref _currentProgress, null);
                    CurrentService = null;
                    CurrentDatasource = null;
                    _currentTrackerOperationId = null;
                    _cancellationTokenSource = null;
                },
                onTerminalEmit: BuildTerminalEmit(() => operationId, () => publishedMetrics, () => currentProgress),
                ownerCompletes: true
            );
            _currentTrackerOperationId = operationId;
            NotifyOperationRegistered();
            var initialProgress = CaptureLogRemovalProgress(
                "signalr.logRemoval.starting.single",
                0,
                new Dictionary<string, object?>
                {
                    ["service"] = service,
                    ["datasourceName"] = datasourceName
                },
                0,
                0,
                0,
                datasourceName);
            _operationTracker.UpdateProgress(
                operationId.Value,
                initialProgress.Snapshot.PercentComplete,
                initialProgress.Snapshot.StageKey, onProgress: _ =>
                {
                    publishProgress(initialProgress);
                    if (_currentTrackerOperationId == operationId)
                        Volatile.Write(ref _currentProgress, initialProgress);
                });

            // Seed the completion payload (incl. operation id + datasource) so the onTerminalEmit
            // closure always has the service name and sensible default messages.
            completionMetrics = new LogRemovalCompletionMetrics(
                OperationId: operationId.Value,
                Service: service,
                SuccessMessage: $"Successfully removed {service} entries from {datasourceName}",
                FailureMessage: $"Failed to remove {service} entries from {datasourceName}",
                CancelMessage: $"Service removal for {service} in {datasourceName} was cancelled",
                Datasource: datasourceName);

            var repairDatasource = _datasourceService.GetDatasource(datasourceName)
                ?? throw new InvalidOperationException(
                    $"Datasource '{datasourceName}' is no longer configured");
            await _operationStateService.PrepareRepairAsync(
                BuildRepair(
                    operationId.Value,
                    service,
                    new[] { repairDatasource },
                    datasourceName),
                cancellationToken);
            repairPrepared = true;

            _logger.LogInformation("Starting Rust log removal for service: {Service} in datasource: {Datasource}", service, datasourceName);
            _logger.LogInformation("Log directory: {LogDir}", logDir);

            var succeeded = await _cacheManagementService.ExecuteWithLockAsync(async () =>
            {
                var selectedDatasource = _datasourceService.GetDatasource(datasourceName)
                    ?? throw new InvalidOperationException(
                        $"Datasource '{datasourceName}' is no longer configured");

                await _notifications.NotifyAllAsync(SignalREvents.LogRemovalStarted, new
                {
                    OperationId = operationId,
                    StageKey = "signalr.logRemoval.starting.single",
                    Context = new Dictionary<string, object?> { ["service"] = service, ["datasourceName"] = datasourceName }
                });

                await ReportProgressAsync(operationId!.Value, publishProgress, progressEmitGate,
                    "signalr.logRemoval.starting.single",
                    0,
                    new Dictionary<string, object?>
                    {
                        ["service"] = service,
                        ["datasourceName"] = datasourceName
                    },
                    0,
                    0,
                    0,
                    service,
                    datasourceName);

                // StartWorkAsync can wait for another operation's repair, and that repair may need
                // the log lock, so the step takes the lock only after this returns.
                await _operationStateService.StartWorkAsync(
                    operationId!.Value,
                    datasourceName,
                    cancellationToken);
                var step = await RunLogRemovalStepAsync(
                    operationId!.Value,
                    service,
                    selectedDatasource,
                    progress => SendProgressAsync(operationId!.Value, publishProgress, progressEmitGate,
                        progress,
                        service,
                        datasourceName,
                        completedFiles: 0,
                        completedLines: 0,
                        completedRemoved: 0),
                    cancellationToken);
                var exitCode = step.Result.ExitCode;

                if (WasCancelled(operationId, cancellationToken))
                {
                    if (!step.Reopen.Success)
                    {
                        _logger.LogError(
                            "Cancelled log removal also failed nginx reopen: {Error}",
                            step.Reopen.ErrorMessage);
                    }
                    throw new OperationCanceledException(cancellationToken);
                }

                // A failing child also fails its reopen check (it publishes an incomplete result or none), so
                // the check's error decides the outcome only for a child that exited 0.
                if (exitCode == 0 && !step.Reopen.Success)
                {
                    completionMetrics = completionMetrics with
                    {
                        FailureMessage = step.Reopen.ErrorMessage!
                    };
                    completionError = step.Reopen.ErrorMessage;
                    return false;
                }

                if (exitCode == 0)
                {
                    // Log removal rewrites access.log only; the database rows stay.

                    var finalProgress = step.Progress;

                    // Capture final metrics for the onTerminalEmit closure; terminal
                    // LogRemovalComplete is emitted inside CompleteOperation.
                    completionMetrics = completionMetrics with
                    {
                        FilesProcessed = finalProgress?.FilesProcessed ?? 0,
                        LinesProcessed = finalProgress?.LinesProcessed ?? 0,
                        LinesRemoved = finalProgress?.LinesRemoved ?? 0,
                        StageKey = string.IsNullOrEmpty(finalProgress?.StageKey) ? null : finalProgress.StageKey
                    };

                    if (step.OtherLogsGone.Count > 0)
                    {
                        var otherLogs = string.Join(", ", step.OtherLogsGone);
                        _logger.LogWarning(
                            "Log removal for {Service} in datasource {Datasource} found other logs deleted outside the app ({Logs}); their series will be read again",
                            service,
                            datasourceName,
                            otherLogs);
                        // Set before CompleteOperation, whose ending row carries it as the warning.
                        _operationTracker.UpdateMetadata(
                            operationId!.Value,
                            (object meta) => ((RemovalMetrics)meta).OtherLogsGone = otherLogs);
                    }

                    _logger.LogInformation("Log removal completed for {Service} in datasource {Datasource}: Removed {LinesRemoved} lines",
                        service, datasourceName, finalProgress?.LinesRemoved ?? 0);
                    await SaveSourceAsync(
                        operationId!.Value,
                        datasourceName,
                        completionMetrics);
                    return true;
                }
                else
                {
                    if (!step.Reopen.Success)
                    {
                        _logger.LogWarning(
                            "nginx reopen after the failed log removal for {Service} in datasource {Datasource} also failed: {Error}",
                            service,
                            datasourceName,
                            step.Reopen.ErrorMessage);
                    }

                    // Terminal LogRemovalComplete (error) is emitted via onTerminalEmit inside CompleteOperation.
                    completionMetrics = completionMetrics with
                    {
                        FailureMessage = $"Failed to remove {service} entries from {datasourceName}"
                    };

                    _logger.LogError("Log removal failed for {Service} in datasource {Datasource} with exit code {ExitCode}",
                        service, datasourceName, exitCode);
                    // The child writes its reason into its progress file before it exits; a child killed
                    // before that leaves none.
                    completionError = step.Progress is { Status: "error" or "failed", Message: { Length: > 0 } reason }
                        ? $"Failed to remove {service} entries from {datasourceName} (exit code {exitCode}): {reason}"
                        : $"Failed to remove {service} entries from {datasourceName} (exit code {exitCode})";
                    return false;
                }
            }, cancellationToken);

            await FinishRepairAsync(
                operationId.Value,
                completionMetrics,
                success: succeeded,
                cancelled: false,
                error: completionError);
            _operationTracker.CompleteOperation(
                operationId.Value,
                success: succeeded,
                error: completionError,
                onCompleting: _ =>
                {
                    publishedMetrics = completionMetrics;
                    currentProgress = null;
                });
            return succeeded;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Service removal for {Service} in {Datasource} was cancelled", service, datasourceName);

            await CompleteCancelledAsync(
                operationId,
                completionMetrics,
                repairPrepared);

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during log removal for {Service} in {Datasource}", service, datasourceName);

            // Terminal LogRemovalComplete (error) is emitted via onTerminalEmit inside CompleteOperation.
            completionMetrics = completionMetrics with
            {
                FailureMessage = $"Error during log removal: {ex.Message}"
            };

            // Mark operation as failed in unified tracker
            if (operationId.HasValue)
            {
                if (repairPrepared)
                {
                    await FinishRepairAsync(
                        operationId.Value,
                        completionMetrics,
                        success: false,
                        cancelled: false,
                        error: ex.Message);
                }
                _operationTracker.CompleteOperation(operationId.Value, success: false, error: ex.Message,
                        onCompleting: _ => { publishedMetrics = completionMetrics; currentProgress = null; });
            }

            return false;
        }
        finally
        {
            // GUARANTEE terminality: if the worker is torn down (host shutdown, an exception thrown
            // before any explicit CompleteOperation, a mid-await teardown inside ExecuteWithLockAsync)
            // the tracker entry would otherwise stay Running forever and 409-block every future
            // LogRemoval. CompleteOperation is idempotent via the Interlocked CompletedFlag, so this is
            // a no-op when a happy/cancel/error path already completed it (in which case
            // onTerminalCleanup has already nulled _currentTrackerOperationId and this guard is skipped).
            var leakedOperationId = operationId;
            if (leakedOperationId.HasValue && !IsOperationAlreadyTerminal(operationId))
            {
                if (repairPrepared)
                {
                    await FinishRepairAsync(
                        leakedOperationId.Value,
                        completionMetrics,
                        success: false,
                        cancelled: false,
                        error: "Log removal ended without reaching a terminal state");
                }
                _operationTracker.CompleteOperation(
                    leakedOperationId.Value,
                    success: false,
                    error: "Log removal ended without reaching a terminal state");
            }

            if (_currentTrackerOperationId == operationId)
            {
            IsProcessing = false;
            Volatile.Write(ref _currentProgress, null);
            CurrentService = null;
            CurrentDatasource = null;
            _currentTrackerOperationId = null;
            _cancellationTokenSource = null;
            }
        }
    }

    /// <summary>
    /// Rewrites one datasource's logs for a service while holding the log file lock. The
    /// datasource's log positions are kept only when the child exits 0 and nginx reopens; any other
    /// end leaves the source started and not kept, so the repair runs this step again. The removal
    /// job runs it inside the cache lock and the repair runs it without.
    /// </summary>
    private async Task<(ProcessExecutionResult Result, LogRemovalProgress? Progress, LogRotationResult Reopen, IReadOnlyList<string> OtherLogsGone)> RunLogRemovalStepAsync(
        Guid operationId,
        string service,
        ResolvedDatasource datasource,
        Func<LogRemovalProgress, Task>? onProgress,
        CancellationToken cancellationToken)
    {
        await using (await _operationStateService.LockLogFilesAsync(
            operationId,
            OperationType.LogRemoval,
            LogFileLockKind.Rewrite,
            cancellationToken))
        {
            // Prepared under the lock, so another step's rename cannot change the files it binds to. With no
            // log file left the check binds none, so the child refuses a log file that appears before it
            // scans the folder.
            await using var reopenCheck = await _nginxLogRotationService.PrepareReopenCheckAsync(
                new[] { datasource },
                NginxLogRotationService.GetAffectedLogPaths(datasource),
                expectsPublication: true,
                cancellationToken);
            _nginxLogRotationService.ValidateReopenCheck(reopenCheck);

            // A cancel up to here has not touched the logs, so the repair has nothing to finish.
            cancellationToken.ThrowIfCancellationRequested();
            await _operationStateService.MarkLogRewriteStartedAsync(operationId, datasource.Name);

            var progressPath = Path.Combine(
                _pathResolver.GetOperationsDirectory(),
                $"log_remove_progress_{datasource.Name}.json");
            // A file left by an earlier run would otherwise be read as this run's result.
            if (File.Exists(progressPath))
            {
                File.Delete(progressPath);
            }
            var rustExecutablePath = _pathResolver.GetRustLogManagerPath();
            var arguments = $"remove \"{datasource.LogPath}\" \"{service}\" \"{progressPath}\" --progress";
            var stemPositionsPath = await _stateService.WriteStemPositionsTempFileAsync(datasource.Name);
            if (stemPositionsPath != null)
            {
                arguments += $" --stem-positions \"{stemPositionsPath}\"";
            }
            _logger.LogInformation("Rust arguments: {Arguments}", arguments);

            var start = _rustProcessHelper.CreateProcessStartInfo(
                rustExecutablePath,
                arguments,
                Path.GetDirectoryName(rustExecutablePath));
            NginxLogRotationService.AttachPublicationCheck(reopenCheck, start);

            // Hybrid transport (mirrors CacheClearingService): the stdout progress event is a
            // zero-latency wake-up; the progress-file DTO is unchanged, so the callback still
            // re-reads it for the real data on every tick.
            ProcessExecutionResult result;
            try
            {
                result = await _rustProcessHelper.ExecuteTrackedProcessWithProgressEventsAsync(
                    start,
                    operationId,
                    cancellationToken,
                    onProgress is null
                        ? null
                        : async (RustProgressEvent _) =>
                        {
                            var tick = await ReadProgressFileAsync(progressPath);
                            if (tick == null)
                            {
                                return;
                            }

                            await onProgress(tick);
                        },
                    processLabel: "log_removal");
            }
            catch (Exception error)
            {
                var failedReopen = await _nginxLogRotationService.CompleteReopenCheckAsync(
                    reopenCheck,
                    physicalChange: true,
                    CancellationToken.None);
                if (!failedReopen.Success)
                {
                    // A canceled child stops before it publishes. The record already holds the
                    // started step, so the repair redoes it and reopens nginx then.
                    if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning(
                            "Could not reopen nginx after a canceled log step: {Error}",
                            failedReopen.ErrorMessage);
                        throw;
                    }
                    throw new AggregateException(
                        error,
                        new IOException(failedReopen.ErrorMessage!));
                }
                throw;
            }
            finally
            {
                if (stemPositionsPath != null)
                {
                    await _rustProcessHelper.DeleteTempFileAsync(stemPositionsPath);
                }
            }

            _logger.LogInformation("Rust log_manager exited with code {ExitCode} for datasource {Datasource}", result.ExitCode, datasource.Name);

            if (!string.IsNullOrWhiteSpace(result.Output))
            {
                _logger.LogInformation("[Rust log removal] {Output}", result.Output);
            }

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                _logger.LogInformation("[Rust log removal stderr] {Error}", result.Error);
            }

            var removalProgress = await ReadProgressFileAsync(progressPath);
            // Read before the reopen: nginx recreates a log it writes by a fixed path, so after the reopen
            // a file deleted outside the app is back, empty, and would no longer read as gone.
            var goneBeforeReopen = reopenCheck.AffectedPaths.Where(path => !File.Exists(path)).ToList();
            // A cancel that lands after the child exited must still move nginx onto the rewritten files.
            var reopen = await _nginxLogRotationService.CompleteReopenCheckAsync(
                reopenCheck,
                physicalChange: removalProgress?.LinesRemoved > 0 || result.ExitCode != 0,
                CancellationToken.None);

            // Monolithic sources are REWRITTEN in place (not deleted), so their saved
            // positions must come back by the removed already-read lines - including on
            // a failed exit, where completed files already lost their lines.
            if (removalProgress?.LinesRemovedByStem is { Count: > 0 } removedMap)
            {
                _stateService.ReduceLogPositionsAfterPurge(
                    datasource.Name,
                    removalProgress.LinesRemovedBeforePositionByStem ?? removedMap,
                    removedMap);
            }

            IReadOnlyList<string> otherLogsGone = Array.Empty<string>();
            if (result.ExitCode == 0 && reopen.Success)
            {
                // Bare-metal removal deletes the service's whole source file series; the
                // deleted stems' checkpoints must not survive the files.
                var serviceStems = LancacheManager.Core.Services.LogSourceLayout.StemsForService(service);
                _stateService.ClearLogSourcePositions(datasource.Name, serviceStems);
                // A bound log of another series that something outside the app deleted during the step was
                // not this removal's to delete, and the child left it unchanged. A saved position counts
                // lines across the whole series, so a vanished file shifts it past lines never read; that
                // series is read again from its first line.
                otherLogsGone = goneBeforeReopen
                    .Select(path => Path.GetFileName(path))
                    .Where(name => LancacheManager.Core.Services.LogSourceLayout.LogicalStem(name) is { } stem
                        && !serviceStems.Contains(stem))
                    .ToList();
                _stateService.ClearLogSourcePositions(
                    datasource.Name,
                    otherLogsGone.Select(name => LancacheManager.Core.Services.LogSourceLayout.LogicalStem(name)!));
                await _operationStateService.MarkLogPositionsKeptAsync(operationId, datasource.Name);
            }

            try
            {
                // The positions and the log files changed; the count program recounts a log newer than
                // its saved counts, so a failure here must not fail the removal.
                await _cacheManagementService.InvalidateServiceCountsAsync();
            }
            catch (Exception countsError)
            {
                _logger.LogError(
                    countsError,
                    "Failed to refresh the service counts after the log removal of operation {OperationId}",
                    operationId);
            }

            return (result, removalProgress, reopen, otherLogsGone);
        }
    }

    /// <summary>
    /// Forwards a per-datasource Rust progress tick to the UI. The Rust log_manager reports
    /// <see cref="RustProgressBase.PercentComplete"/> as 0-100 for the CURRENT datasource only, so when
    /// removing across multiple datasources the raw value would reset to a low number at every
    /// datasource boundary (a visible jump). To keep the outer card moving smoothly we scale the inner
    /// percent into this datasource's band: datasource <paramref name="datasourceIndex"/> of
    /// <paramref name="datasourceCount"/> maps inner 0-100% into
    /// [index/count, (index+1)/count] * <paramref name="ceiling"/>. For the single-datasource path
    /// (index 0, count 1) the band is [0, ceiling] with ceiling 100, so the inner percent passes
    /// through unchanged.
    /// </summary>
    private Task SendProgressAsync(
        Guid operationId,
        Action<LogRemovalCurrentProgress> publishProgress,
        ProgressEmitGate progressEmitGate,
        LogRemovalProgress progress,
        string service,
        string datasourceName,
        int completedFiles,
        long completedLines,
        long completedRemoved,
        int datasourceIndex = 0,
        int datasourceCount = 1,
        double ceiling = 100.0)
    {
        // The Rust log_manager binary doesn't receive the datasource name (only the
        // log directory + service), so its progress JSON context omits `datasourceName`.
        // i18n templates like "signalr.logRemoval.processingDatasource" render `{{datasourceName}}`
        // as an empty string when the context is missing the key, producing
        // "Removing localhost entries from datasource ''..." in the UI.
        // Enrich the context here so every forwarded progress event carries it.
        var enrichedContext = progress.Context != null
            ? new Dictionary<string, object?>(progress.Context)
            : new Dictionary<string, object?>();
        if (!enrichedContext.ContainsKey("datasourceName"))
        {
            enrichedContext["datasourceName"] = datasourceName;
        }
        // Same gap for {{service}}: the Rust binary's progress JSON has no context,
        // so stage templates like "signalr.logRemoval.removing" rendered the
        // placeholder literally until enriched here.
        if (!enrichedContext.ContainsKey("service"))
        {
            enrichedContext["service"] = service;
        }

        var scaledPercent = ScaleIntoBand(progress.PercentComplete, datasourceIndex, datasourceCount, ceiling);
        var cumulative = AddCumulativeCounters(
            completedFiles,
            completedLines,
            completedRemoved,
            progress.FilesProcessed,
            progress.LinesProcessed,
            progress.LinesRemoved);
        return ReportProgressAsync(operationId, publishProgress, progressEmitGate,
            string.IsNullOrWhiteSpace(progress.StageKey)
                ? "signalr.logRemoval.removing"
                : progress.StageKey,
            scaledPercent,
            enrichedContext,
            cumulative.Files,
            cumulative.Lines,
            cumulative.Removed,
            service,
            datasourceName);
    }

    internal static (int Files, long Lines, long Removed) AddCumulativeCounters(
        int completedFiles,
        long completedLines,
        long completedRemoved,
        int currentFiles,
        long currentLines,
        long currentRemoved) =>
        (
            checked(completedFiles + currentFiles),
            checked(completedLines + currentLines),
            checked(completedRemoved + currentRemoved));

    private LogRemovalCurrentProgress CaptureLogRemovalProgress(
        string stageKey,
        double percentComplete,
        IReadOnlyDictionary<string, object?> context,
        int filesProcessed,
        long linesProcessed,
        long linesRemoved,
        string? datasource)
    {
        var completeContext = new Dictionary<string, object?>(context)
        {
            ["filesProcessed"] = filesProcessed,
            ["linesProcessed"] = linesProcessed,
            ["linesRemoved"] = linesRemoved
        };
        var snapshot = OperationProgressSnapshot.Create(
            stageKey,
            percentComplete,
            completeContext,
            Interlocked.Increment(ref _progressRevision));
        var current = new LogRemovalCurrentProgress(
            snapshot,
            filesProcessed,
            linesProcessed,
            linesRemoved,
            datasource);
        return current;
    }

    private async Task ReportProgressAsync(
        Guid operationId,
        Action<LogRemovalCurrentProgress> publishProgress,
        ProgressEmitGate progressEmitGate,
        string stageKey,
        double percentComplete,
        IReadOnlyDictionary<string, object?> context,
        int filesProcessed,
        long linesProcessed,
        long linesRemoved,
        string service,
        string? datasource)
    {
        var enrichedContext = new Dictionary<string, object?>(context)
        {
            ["service"] = service
        };
        if (datasource != null)
        {
            enrichedContext["datasourceName"] = datasource;
        }

        var previous = _currentTrackerOperationId == operationId ? Volatile.Read(ref _currentProgress) : null;
        var candidateContext = new Dictionary<string, object?>(enrichedContext)
        {
            ["filesProcessed"] = filesProcessed,
            ["linesProcessed"] = linesProcessed,
            ["linesRemoved"] = linesRemoved
        };
        var current = previous != null && previous.Snapshot.HasSameProgress(stageKey, percentComplete, candidateContext)
            ? previous
            : CaptureLogRemovalProgress(stageKey, percentComplete, enrichedContext,
                filesProcessed, linesProcessed, linesRemoved, datasource);
        var accepted = false;
        _operationTracker.UpdateProgress(operationId, current.Snapshot.PercentComplete, current.Snapshot.StageKey,
            onProgress: _ =>
            {
                publishProgress(current);
                if (_currentTrackerOperationId == operationId)
                    Volatile.Write(ref _currentProgress, current);
                accepted = true;
            });
        if (!accepted || !progressEmitGate.ShouldEmit(current.Snapshot.StageKey, current.Snapshot.Revision))
            return;

        await _notifications.NotifyAllAsync(SignalREvents.LogRemovalProgress, new
        {
            OperationId = operationId,
            current.Snapshot.PercentComplete,
            Status = OperationStatus.Running,
            current.Snapshot.StageKey,
            current.Snapshot.Context,
            current.FilesProcessed,
            current.LinesProcessed,
            current.LinesRemoved,
            Service = service,
            Datasource = datasource
        });
    }

    /// <summary>
    /// Maps an inner 0-100 percent for datasource <paramref name="index"/> of <paramref name="count"/>
    /// into that datasource's slice of the overall [0, <paramref name="ceiling"/>] range:
    /// [index/count, (index+1)/count] * ceiling. Guards against a zero/negative count and clamps the
    /// inner percent to 0-100 so a stray Rust value can't push the outer bar outside its band.
    /// </summary>
    internal static double ScaleIntoBand(double innerPercent, int index, int count, double ceiling)
    {
        var clampedInner = double.IsFinite(innerPercent)
            ? Math.Clamp(innerPercent, 0.0, 100.0)
            : 0.0;

        if (count <= 1)
        {
            return clampedInner / 100.0 * ceiling;
        }

        var bandStart = (double)index / count * ceiling;
        var bandWidth = ceiling / count;
        return bandStart + (clampedInner / 100.0 * bandWidth);
    }

    /// <summary>
    /// True when the tracker has already driven this operation to a terminal state
    /// (e.g. a universal force-kill completed it). Used to suppress duplicate terminal
    /// SignalR emits + CompleteOperation calls from the OCE catch blocks.
    /// A null id means the terminal cleanup callback already ran (which nulls the id),
    /// so it is also treated as already-terminal.
    /// </summary>
    private bool IsOperationAlreadyTerminal(Guid? opId)
    {
        if (!opId.HasValue)
        {
            return true;
        }

        return _operationTracker.GetOperation(opId.Value)?.Status.IsTerminal() == true;
    }

    private bool WasCancelled(Guid? operationId, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return true;
        var operation = operationId.HasValue ? _operationTracker.GetOperation(operationId.Value) : null;
        return operation?.Cancelled == true || operation?.Status == OperationStatus.Cancelling;
    }

    private async Task CompleteCancelledAsync(
        Guid? operationId,
        LogRemovalCompletionMetrics metrics,
        bool repairPrepared)
    {
        if (operationId.HasValue)
        {
            if (repairPrepared)
            {
                await FinishRepairAsync(
                    operationId.Value,
                    metrics,
                    success: false,
                    cancelled: true,
                    error: null);
            }
            _operationTracker.CompleteOperation(operationId.Value, success: false, cancelled: true);
        }
    }

    private async Task<LogRemovalProgress?> ReadProgressFileAsync(string progressPath)
    {
        return await _rustProcessHelper.ReadProgressFileAsync<LogRemovalProgress>(progressPath);
    }
}
