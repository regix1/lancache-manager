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
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IUnifiedOperationTracker _operationTracker;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Guid? _currentTrackerOperationId;
    private TaskCompletionSource<Guid>? _operationRegisteredTcs;

    // Completion payload captured BY VALUE just before each CompleteOperation call so the
    // onTerminalEmit closure (fired exactly once inside CompleteOperation) can build the typed
    // LogRemovalComplete record. Only one removal runs at a time (IsProcessing/_startLock guard),
    // so a single shared holder is safe.
    private LogRemovalCompletionMetrics _completionMetrics;

    private readonly DatasourceService _datasourceService;

    public bool IsProcessing { get; private set; }
    public string? CurrentService { get; private set; }
    public Guid? CurrentOperationId { get; private set; }
    public string? CurrentDatasource { get; private set; }

    /// <summary>
    /// Starts service removal in the background and returns the operation id as soon as it is registered.
    /// </summary>
    public Task<Guid?> StartRemovalInBackgroundAsync(string service)
    {
        return StartRemovalInBackgroundAsync(() => StartRemovalAsync(service));
    }

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
    public object GetRemovalStatus()
    {
        ProgressData? progress = null;
        var datasourceName = CurrentDatasource;
        if (datasourceName != null)
        {
            try
            {
                var operationsDir = _pathResolver.GetOperationsDirectory();
                var progressPath = Path.Combine(operationsDir, $"log_remove_progress_{datasourceName}.json");
                if (File.Exists(progressPath))
                {
                    var json = File.ReadAllText(progressPath);
                    progress = System.Text.Json.JsonSerializer.Deserialize<ProgressData>(json);
                }
            }
            catch
            {
                // Ignore read errors - file may be being written
            }
        }

        // Return object with isProcessing and service fields that frontend expects
        return new
        {
            isProcessing = IsProcessing,
            service = CurrentService,
            operationId = _currentTrackerOperationId,
            filesProcessed = progress?.FilesProcessed ?? 0,
            linesProcessed = progress?.LinesProcessed ?? 0,
            linesRemoved = progress?.LinesRemoved ?? 0,
            percentComplete = progress?.PercentComplete ?? 0,
            status = progress?.Status ?? (IsProcessing ? "starting" : "idle"),
            stageKey = progress?.StageKey ?? "",
            // Recovery renders i18n.t(stageKey, context); stage templates like
            // "signalr.logRemoval.removing" interpolate {{service}}, so the
            // context must carry it or the placeholder renders literally.
            context = new Dictionary<string, object?> { ["service"] = CurrentService }
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
        IUnifiedOperationTracker operationTracker)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _notifications = notifications;
        _cacheManagementService = cacheManagementService;
        _rustProcessHelper = rustProcessHelper;
        _nginxLogRotationService = nginxLogRotationService;
        _dbContextFactory = dbContextFactory;
        _datasourceService = datasourceService;
        _operationTracker = operationTracker;
    }

    /// <summary>
    /// Terminal completion payload captured by value before CompleteOperation so the
    /// onTerminalEmit closure can emit the typed LogRemovalComplete record.
    /// </summary>
    private readonly record struct LogRemovalCompletionMetrics(
        Guid OperationId,
        string Service,
        string SuccessMessage,
        string FailureMessage,
        string CancelMessage,
        int FilesProcessed = 0,
        long LinesProcessed = 0,
        long LinesRemoved = 0,
        int DatabaseRecordsDeleted = 0,
        string? Datasource = null,
        string? StageKey = null);

    /// <summary>
    /// Builds the onTerminalEmit closure that emits the terminal LogRemovalComplete event EXACTLY
    /// ONCE from inside CompleteOperation (CompletedFlag-gated). Reads the completion payload from
    /// _completionMetrics (captured by value just before each CompleteOperation call, including the
    /// operation id). Branches on info.Cancelled / info.Success / else(error).
    /// </summary>
    private Func<OperationTerminalInfo, Task> BuildTerminalEmit()
    {
        return info =>
        {
            var metrics = _completionMetrics;
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
                    OperationId: metrics.OperationId,
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

    public class ProgressData
    {
        [System.Text.Json.Serialization.JsonPropertyName("files_processed")]
        public int FilesProcessed { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("lines_processed")]
        public long LinesProcessed { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("lines_removed")]
        public long LinesRemoved { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("percent_complete")]
        public double PercentComplete { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("stage_key")]
        public string StageKey { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("context")]
        public Dictionary<string, object?> Context { get; set; } = new();
    }

    private async Task<bool> StartRemovalAsync(string service)
    {
        // Sanitize user-provided service name to prevent process argument injection
        service = RustProcessHelper.SanitizeProcessArgument(service);

        await _startLock.WaitAsync();
        try
        {
            if (IsProcessing)
            {
                _logger.LogWarning("Log removal is already running for service: {CurrentService}", CurrentService);
                return false;
            }

            IsProcessing = true;
            CurrentService = service;
        }
        finally
        {
            _startLock.Release();
        }

        try
        {
            _cancellationTokenSource = new CancellationTokenSource();

            // Register with unified operation tracker for centralized cancellation.
            // Service-scoped metadata so OperationConflictChecker.DeriveScope yields
            // ConflictScope.Service(service): EntityKind="service" + lowercased EntityKey
            // match ConflictScope.Service(service) (Ordinal compare requires identical casing).
            // onTerminalEmit emits the terminal LogRemovalComplete event EXACTLY ONCE from inside
            // CompleteOperation (CompletedFlag-gated), so no terminal NotifyAll/SendOperationComplete
            // is issued directly from the success / cancel / error paths below.
            _currentTrackerOperationId = _operationTracker.RegisterOperation(
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
                    CurrentService = null;
                    CurrentDatasource = null;
                    _currentTrackerOperationId = null;
                    _cancellationTokenSource?.Dispose();
                    _cancellationTokenSource = null;
                },
                onTerminalEmit: BuildTerminalEmit()
            );
            NotifyOperationRegistered();

            // Seed the completion payload (incl. operation id) so the onTerminalEmit closure always
            // has the service name and sensible default messages even on early/leaked terminal paths.
            _completionMetrics = new LogRemovalCompletionMetrics(
                OperationId: _currentTrackerOperationId.Value,
                Service: service,
                SuccessMessage: $"Removed {service} entries",
                FailureMessage: $"Log removal for {service} failed",
                CancelMessage: $"Service removal for {service} was cancelled");

            var datasources = _datasourceService.GetDatasources();

            if (datasources.Count == 0)
            {
                _logger.LogWarning("No datasources configured for log removal");

                // Terminal LogRemovalComplete is emitted via onTerminalEmit inside CompleteOperation.
                _completionMetrics = _completionMetrics with
                {
                    FailureMessage = "No datasources configured for log removal"
                };

                if (_currentTrackerOperationId.HasValue)
                {
                    _operationTracker.CompleteOperation(_currentTrackerOperationId.Value, success: false, error: "No datasources configured for log removal");
                }

                return false;
            }

            var operationsDir = _pathResolver.GetOperationsDirectory();
            var progressPath = Path.Combine(operationsDir, "log_remove_progress.json");
            var rustExecutablePath = _pathResolver.GetRustLogManagerPath();

            // Delete old progress file
            if (File.Exists(progressPath))
            {
                File.Delete(progressPath);
            }

            _logger.LogInformation("Starting Rust log removal for service: {Service} across {DatasourceCount} datasource(s)",
                service, datasources.Count);
            _logger.LogInformation("Rust executable: {Executable}", rustExecutablePath);

            // Send started event
            await _notifications.NotifyAllAsync(SignalREvents.LogRemovalStarted, new
            {
                OperationId = _currentTrackerOperationId,
                StageKey = "signalr.logRemoval.starting.default",
                Context = new Dictionary<string, object?> { ["service"] = service }
            });

            // Send initial progress notification
            await _notifications.NotifyAllAsync(SignalREvents.LogRemovalProgress, new
            {
                OperationId = _currentTrackerOperationId,
                PercentComplete = 0.0,
                Status = OperationStatus.Running,
                StageKey = "signalr.logRemoval.starting.multi",
                Context = new Dictionary<string, object?> { ["service"] = service, ["datasourceCount"] = datasources.Count },
                FilesProcessed = 0,
                LinesProcessed = 0,
                LinesRemoved = 0,
                Service = service
            });

            // Process each datasource sequentially
            var totalFilesProcessed = 0;
            long totalLinesProcessed = 0;
            long totalLinesRemoved = 0;
            var datasourcesProcessed = 0;
            var datasourcesSkipped = 0;
            var allSuccess = true;
            string? lastStageKey = null;

            foreach (ResolvedDatasource datasource in datasources)
            {
                // Check for cancellation between datasources
                _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                var logDir = datasource.LogPath;

                // Skip datasources where the log directory doesn't exist
                if (!Directory.Exists(logDir))
                {
                    _logger.LogWarning("Skipping datasource '{DatasourceName}': log directory does not exist at {LogDir}",
                        datasource.Name, logDir);
                    datasourcesSkipped++;
                    continue;
                }

                // Skip datasources where logs are read-only
                if (!datasource.LogsWritable)
                {
                    _logger.LogWarning("Skipping datasource '{DatasourceName}': log directory is read-only at {LogDir}",
                        datasource.Name, logDir);
                    datasourcesSkipped++;
                    continue;
                }

                _logger.LogInformation("Processing datasource '{DatasourceName}': Log directory={LogDir}",
                    datasource.Name, logDir);
                CurrentDatasource = datasource.Name;

                // Use a datasource-specific progress file so concurrent monitoring doesn't clash
                var dsProgressPath = Path.Combine(operationsDir, $"log_remove_progress_{datasource.Name}.json");
                if (File.Exists(dsProgressPath))
                {
                    File.Delete(dsProgressPath);
                }

                // Wrap Rust process execution in shared lock to prevent concurrent access to log files
                // This prevents "Failed to persist temp file" errors when other processes are reading logs
                var dsSuccess = await _cacheManagementService.ExecuteWithLockAsync(async () =>
                {
                    // Start Rust process for this datasource
                    var arguments = $"remove \"{logDir}\" \"{service}\" \"{dsProgressPath}\" --progress";
                    _logger.LogInformation("Rust arguments for datasource '{DatasourceName}': {Arguments}",
                        datasource.Name, arguments);

                    var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                        rustExecutablePath,
                        arguments,
                        Path.GetDirectoryName(rustExecutablePath));

                    await _notifications.NotifyAllAsync(SignalREvents.LogRemovalProgress, new
                    {
                        OperationId = _currentTrackerOperationId,
                        // Band START for this datasource (bottom of its slice of [0, ceiling]); the inner
                        // file ticks below fill upward from here so there is no jump at the boundary.
                        PercentComplete = (double)datasourcesProcessed / datasources.Count * MultiDatasourceFileCeiling,
                        Status = OperationStatus.Running,
                        StageKey = "signalr.logRemoval.processingDatasource",
                        Context = new Dictionary<string, object?> { ["service"] = service, ["datasourceName"] = datasource.Name },
                        FilesProcessed = totalFilesProcessed,
                        LinesProcessed = totalLinesProcessed,
                        LinesRemoved = totalLinesRemoved,
                        Service = service,
                        Datasource = datasource.Name
                    });

                    // Hybrid transport (mirrors CacheClearingService): the stdout progress event is a
                    // zero-latency wake-up; log_service_manager.rs's progress-file DTO is unchanged, so
                    // the callback still re-reads it for the real data on every tick.
                    var result = await _rustProcessHelper.ExecuteTrackedProcessWithProgressEventsAsync(
                        startInfo,
                        _currentTrackerOperationId,
                        _cancellationTokenSource.Token,
                        async _ =>
                        {
                            var progressData = await _rustProcessHelper.ReadProgressFileAsync<ProgressData>(dsProgressPath);
                            if (progressData == null)
                            {
                                return;
                            }

                            // Scale this datasource's inner 0-100% into its band so the outer card moves
                            // smoothly across datasources instead of jumping at each boundary. The bands
                            // fill [0, MultiDatasourceFileCeiling]; DB cleanup owns the remaining top slice.
                            await SendProgressAsync(progressData, service, datasource.Name, datasourcesProcessed, datasources.Count, MultiDatasourceFileCeiling);
                        },
                        processLabel: "log_removal");

                    var exitCode = result.ExitCode;
                    _logger.LogInformation("Rust log_manager exited with code {ExitCode} for datasource '{DatasourceName}'",
                        exitCode, datasource.Name);

                    if (WasCancelled())
                    {
                        return false;
                    }

                    if (!string.IsNullOrWhiteSpace(result.Output))
                    {
                        _logger.LogInformation("[Rust log removal] {Output}", result.Output);
                    }

                    if (!string.IsNullOrWhiteSpace(result.Error))
                    {
                        _logger.LogInformation("[Rust log removal stderr] {Error}", result.Error);
                    }

                    // Accumulate progress from this datasource
                    var dsProgress = await ReadProgressFileAsync(dsProgressPath);
                    if (dsProgress != null)
                    {
                        totalFilesProcessed += dsProgress.FilesProcessed;
                        totalLinesProcessed += dsProgress.LinesProcessed;
                        totalLinesRemoved += dsProgress.LinesRemoved;
                        if (!string.IsNullOrEmpty(dsProgress.StageKey))
                        {
                            lastStageKey = dsProgress.StageKey;
                        }
                    }

                    if (exitCode != 0)
                    {
                        _logger.LogError("Log removal failed for {Service} in datasource '{DatasourceName}' with exit code {ExitCode}",
                            service, datasource.Name, exitCode);
                        return false;
                    }

                    _logger.LogInformation("Log removal completed for datasource '{DatasourceName}': Removed {LinesRemoved} lines",
                        datasource.Name, dsProgress?.LinesRemoved ?? 0);
                    return true;
                }, _cancellationTokenSource.Token);

                if (!dsSuccess)
                {
                    allSuccess = false;
                    if (WasCancelled())
                    {
                        await CompleteCancelledAsync(service);
                        return false;
                    }

                    _logger.LogWarning("Log removal failed for datasource '{DatasourceName}', continuing with remaining datasources",
                        datasource.Name);
                }

                datasourcesProcessed++;
            }

            if (WasCancelled())
            {
                await CompleteCancelledAsync(service);
                return false;
            }

            _logger.LogInformation(
                "Log removal across all datasources complete: {Processed} processed, {Skipped} skipped, totalLinesRemoved={TotalLinesRemoved}",
                datasourcesProcessed, datasourcesSkipped, totalLinesRemoved);

            if (allSuccess && datasourcesProcessed > 0)
            {
                // Invalidate service counts cache so UI refreshes
                await _cacheManagementService.InvalidateServiceCountsAsync();

                // Signal nginx to reopen log files (prevents monolithic container from losing log access)
                await _nginxLogRotationService.ReopenNginxLogsAsync();

                // Clean up database records for this service
                var dbCleanupResult = await CleanupDbRecordsAsync(service);

                // Build completion message + capture final metrics for the onTerminalEmit closure.
                var logMessage = $"Successfully removed {service} entries from {datasourcesProcessed} datasource(s)";
                var message = dbCleanupResult.Success
                    ? $"{logMessage}. Database: {dbCleanupResult.Message}"
                    : $"{logMessage}. Database cleanup: {dbCleanupResult.Message}";

                _completionMetrics = _completionMetrics with
                {
                    SuccessMessage = message,
                    FilesProcessed = totalFilesProcessed,
                    LinesProcessed = totalLinesProcessed,
                    LinesRemoved = totalLinesRemoved,
                    DatabaseRecordsDeleted = dbCleanupResult.TotalDeleted,
                    StageKey = lastStageKey
                };

                _logger.LogInformation(
                    "Log removal completed successfully for {Service}: {DatasourcesProcessed} datasource(s), Removed {LinesRemoved} of {LinesProcessed} lines, {DbRecords} database records",
                    service, datasourcesProcessed, totalLinesRemoved, totalLinesProcessed, dbCleanupResult.TotalDeleted);

                // Terminal LogRemovalComplete is emitted via onTerminalEmit inside CompleteOperation.
                if (_currentTrackerOperationId.HasValue)
                {
                    _operationTracker.CompleteOperation(_currentTrackerOperationId.Value, success: true);
                }

                return true;
            }
            else if (datasourcesProcessed == 0 && datasourcesSkipped > 0)
            {
                // All datasources were skipped (read-only or non-existent)
                var skipMessage = $"All {datasourcesSkipped} datasource(s) were skipped (read-only or missing log directories)";
                _logger.LogWarning(skipMessage);

                // Terminal LogRemovalComplete is emitted via onTerminalEmit inside CompleteOperation.
                _completionMetrics = _completionMetrics with { FailureMessage = skipMessage };

                if (_currentTrackerOperationId.HasValue)
                {
                    _operationTracker.CompleteOperation(_currentTrackerOperationId.Value, success: false, error: skipMessage);
                }

                return false;
            }
            else
            {
                // Some datasources failed
                var failMessage = $"Log removal for {service} completed with errors across datasources";

                // Terminal LogRemovalComplete is emitted via onTerminalEmit inside CompleteOperation.
                _completionMetrics = _completionMetrics with
                {
                    FailureMessage = failMessage,
                    FilesProcessed = totalFilesProcessed,
                    LinesProcessed = totalLinesProcessed,
                    LinesRemoved = totalLinesRemoved
                };

                _logger.LogError("Log removal failed for {Service}: some datasources had errors", service);

                if (_currentTrackerOperationId.HasValue)
                {
                    _operationTracker.CompleteOperation(_currentTrackerOperationId.Value, success: false, error: failMessage);
                }

                return false;
            }
        }
        catch (OperationCanceledException)
        {
            // Handle cancellation gracefully
            _logger.LogInformation("Service removal for {Service} was cancelled by user", service);

            // If a universal force-kill already completed this op, suppress the duplicate
            // CompleteOperation so only ONE terminal event is emitted. The terminal
            // LogRemovalComplete (cancelled) is emitted via onTerminalEmit inside CompleteOperation.
            if (!IsOperationAlreadyTerminal())
            {
                // Mark operation as cancelled in unified tracker
                if (_currentTrackerOperationId.HasValue)
                {
                    _operationTracker.CompleteOperation(_currentTrackerOperationId.Value, success: false, error: "Cancelled by user");
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during log removal for {Service}", service);

            // Terminal LogRemovalComplete (error) is emitted via onTerminalEmit inside CompleteOperation.
            _completionMetrics = _completionMetrics with
            {
                FailureMessage = $"Error during log removal: {ex.Message}"
            };

            // Mark operation as failed in unified tracker
            if (_currentTrackerOperationId.HasValue)
            {
                _operationTracker.CompleteOperation(_currentTrackerOperationId.Value, success: false, error: ex.Message);
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
            var leakedOperationId = _currentTrackerOperationId;
            if (leakedOperationId.HasValue && !IsOperationAlreadyTerminal())
            {
                _operationTracker.CompleteOperation(
                    leakedOperationId.Value,
                    success: false,
                    error: "Log removal ended without reaching a terminal state");
            }

            IsProcessing = false;
            CurrentService = null;
            CurrentDatasource = null;
            _currentTrackerOperationId = null;
            _cancellationTokenSource?.Dispose();
        }
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

        try
        {
            _cancellationTokenSource = new CancellationTokenSource();

            // Register with unified operation tracker for centralized cancellation.
            // Service-scoped metadata so OperationConflictChecker.DeriveScope yields
            // ConflictScope.Service(service): EntityKind="service" + lowercased EntityKey
            // match ConflictScope.Service(service) (Ordinal compare requires identical casing).
            // onTerminalEmit emits the terminal LogRemovalComplete event EXACTLY ONCE from inside
            // CompleteOperation (CompletedFlag-gated), so no terminal NotifyAll/SendOperationComplete
            // is issued directly from the success / cancel / error paths below.
            _currentTrackerOperationId = _operationTracker.RegisterOperation(
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
                    CurrentService = null;
                    CurrentDatasource = null;
                    _currentTrackerOperationId = null;
                    _cancellationTokenSource?.Dispose();
                    _cancellationTokenSource = null;
                },
                onTerminalEmit: BuildTerminalEmit()
            );
            NotifyOperationRegistered();

            // Seed the completion payload (incl. operation id + datasource) so the onTerminalEmit
            // closure always has the service name and sensible default messages.
            _completionMetrics = new LogRemovalCompletionMetrics(
                OperationId: _currentTrackerOperationId.Value,
                Service: service,
                SuccessMessage: $"Successfully removed {service} entries from {datasourceName}",
                FailureMessage: $"Failed to remove {service} entries from {datasourceName}",
                CancelMessage: $"Service removal for {service} in {datasourceName} was cancelled",
                Datasource: datasourceName);

            var operationsDir = _pathResolver.GetOperationsDirectory();
            var progressPath = Path.Combine(operationsDir, $"log_remove_progress_{datasourceName}.json");
            var rustExecutablePath = _pathResolver.GetRustLogManagerPath();

            // Delete old progress file
            if (File.Exists(progressPath))
            {
                File.Delete(progressPath);
            }

            _logger.LogInformation("Starting Rust log removal for service: {Service} in datasource: {Datasource}", service, datasourceName);
            _logger.LogInformation("Log directory: {LogDir}", logDir);

            return await _cacheManagementService.ExecuteWithLockAsync(async () =>
            {
                var arguments = $"remove \"{logDir}\" \"{service}\" \"{progressPath}\" --progress";
                _logger.LogInformation("Rust arguments: {Arguments}", arguments);

                var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                    rustExecutablePath,
                    arguments,
                    Path.GetDirectoryName(rustExecutablePath));

                await _notifications.NotifyAllAsync(SignalREvents.LogRemovalStarted, new
                {
                    OperationId = _currentTrackerOperationId,
                    StageKey = "signalr.logRemoval.starting.single",
                    Context = new Dictionary<string, object?> { ["service"] = service, ["datasourceName"] = datasourceName }
                });

                await _notifications.NotifyAllAsync(SignalREvents.LogRemovalProgress, new
                {
                    OperationId = _currentTrackerOperationId,
                    PercentComplete = 0.0,
                    Status = OperationStatus.Running,
                    StageKey = "signalr.logRemoval.starting.single",
                    Context = new Dictionary<string, object?> { ["service"] = service, ["datasourceName"] = datasourceName },
                    FilesProcessed = 0,
                    LinesProcessed = 0,
                    LinesRemoved = 0,
                    Service = service,
                    Datasource = datasourceName
                });

                // Hybrid transport (mirrors CacheClearingService): the stdout progress event is a
                // zero-latency wake-up; the progress-file DTO is unchanged, so the callback still
                // re-reads it for the real data on every tick.
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

                        await SendProgressAsync(progressData, service, datasourceName);
                    },
                    processLabel: "log_removal");

                var exitCode = result.ExitCode;
                _logger.LogInformation("Rust log_manager exited with code {ExitCode} for datasource {Datasource}", exitCode, datasourceName);

                if (WasCancelled())
                {
                    await CompleteCancelledAsync(service, datasourceName);
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(result.Output))
                {
                    _logger.LogInformation("[Rust log removal] {Output}", result.Output);
                }

                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    _logger.LogInformation("[Rust log removal stderr] {Error}", result.Error);
                }

                if (exitCode == 0)
                {
                    await _cacheManagementService.InvalidateServiceCountsAsync();
                    await _nginxLogRotationService.ReopenNginxLogsAsync();

                    // Note: Database cleanup is not datasource-specific, so we skip it for per-datasource removal
                    // The user would need to remove from all datasources to clean up DB records

                    var finalProgress = await ReadProgressFileAsync(progressPath);

                    // Capture final metrics for the onTerminalEmit closure; terminal
                    // LogRemovalComplete is emitted inside CompleteOperation.
                    _completionMetrics = _completionMetrics with
                    {
                        FilesProcessed = finalProgress?.FilesProcessed ?? 0,
                        LinesProcessed = finalProgress?.LinesProcessed ?? 0,
                        LinesRemoved = finalProgress?.LinesRemoved ?? 0,
                        StageKey = string.IsNullOrEmpty(finalProgress?.StageKey) ? null : finalProgress.StageKey
                    };

                    _logger.LogInformation("Log removal completed for {Service} in datasource {Datasource}: Removed {LinesRemoved} lines",
                        service, datasourceName, finalProgress?.LinesRemoved ?? 0);

                    // Mark operation as complete in unified tracker
                    if (_currentTrackerOperationId.HasValue)
                    {
                        _operationTracker.CompleteOperation(_currentTrackerOperationId.Value, success: true);
                    }
                    return true;
                }
                else
                {
                    // Terminal LogRemovalComplete (error) is emitted via onTerminalEmit inside CompleteOperation.
                    _completionMetrics = _completionMetrics with
                    {
                        FailureMessage = $"Failed to remove {service} entries from {datasourceName}"
                    };

                    _logger.LogError("Log removal failed for {Service} in datasource {Datasource} with exit code {ExitCode}",
                        service, datasourceName, exitCode);

                    // Mark operation as failed in unified tracker
                    if (_currentTrackerOperationId.HasValue)
                    {
                        _operationTracker.CompleteOperation(_currentTrackerOperationId.Value, success: false, error: $"Exit code {exitCode}");
                    }
                    return false;
                }
            }, _cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Service removal for {Service} in {Datasource} was cancelled", service, datasourceName);

            // If a universal force-kill already completed this op, suppress the duplicate
            // SignalR completion + CompleteOperation so only ONE terminal event is emitted.
            if (!IsOperationAlreadyTerminal())
            {
                await CompleteCancelledAsync(service, datasourceName);
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during log removal for {Service} in {Datasource}", service, datasourceName);

            // Terminal LogRemovalComplete (error) is emitted via onTerminalEmit inside CompleteOperation.
            _completionMetrics = _completionMetrics with
            {
                FailureMessage = $"Error during log removal: {ex.Message}"
            };

            // Mark operation as failed in unified tracker
            if (_currentTrackerOperationId.HasValue)
            {
                _operationTracker.CompleteOperation(_currentTrackerOperationId.Value, success: false, error: ex.Message);
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
            var leakedOperationId = _currentTrackerOperationId;
            if (leakedOperationId.HasValue && !IsOperationAlreadyTerminal())
            {
                _operationTracker.CompleteOperation(
                    leakedOperationId.Value,
                    success: false,
                    error: "Log removal ended without reaching a terminal state");
            }

            IsProcessing = false;
            CurrentService = null;
            CurrentDatasource = null;
            _currentTrackerOperationId = null;
            _cancellationTokenSource?.Dispose();
        }
    }

    /// <summary>
    /// Upper bound (percent) of the file-processing phase when removing across MULTIPLE datasources.
    /// The datasource bands fill [0, MultiDatasourceFileCeiling]; the remaining
    /// [MultiDatasourceFileCeiling, 100] is reserved for the post-loop database cleanup phase (which
    /// emits 95% — see <see cref="CleanupDbRecordsAsync"/>) and final completion, so progress
    /// never steps backward from a full 100% band into the 95% cleanup tick.
    /// </summary>
    private const double MultiDatasourceFileCeiling = 95.0;

    /// <summary>
    /// Forwards a per-datasource Rust progress tick to the UI. The Rust log_manager reports
    /// <see cref="ProgressData.PercentComplete"/> as 0-100 for the CURRENT datasource only, so when
    /// removing across multiple datasources the raw value would reset to a low number at every
    /// datasource boundary (a visible jump). To keep the outer card moving smoothly we scale the inner
    /// percent into this datasource's band: datasource <paramref name="datasourceIndex"/> of
    /// <paramref name="datasourceCount"/> maps inner 0-100% into
    /// [index/count, (index+1)/count] * <paramref name="ceiling"/>. For the single-datasource path
    /// (index 0, count 1) the band is [0, ceiling] with ceiling 100, so the inner percent passes
    /// through unchanged.
    /// </summary>
    private Task SendProgressAsync(
        ProgressData progress,
        string service,
        string datasourceName,
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

        return _notifications.NotifyAllAsync(SignalREvents.LogRemovalProgress, new
        {
            OperationId = _currentTrackerOperationId,
            PercentComplete = scaledPercent,
            Status = OperationStatus.Running,
            StageKey = progress.StageKey,
            Context = enrichedContext,
            progress.FilesProcessed,
            progress.LinesProcessed,
            progress.LinesRemoved,
            Service = service,
            Datasource = datasourceName
        });
    }

    /// <summary>
    /// Maps an inner 0-100 percent for datasource <paramref name="index"/> of <paramref name="count"/>
    /// into that datasource's slice of the overall [0, <paramref name="ceiling"/>] range:
    /// [index/count, (index+1)/count] * ceiling. Guards against a zero/negative count and clamps the
    /// inner percent to 0-100 so a stray Rust value can't push the outer bar outside its band.
    /// </summary>
    private static double ScaleIntoBand(double innerPercent, int index, int count, double ceiling)
    {
        var clampedInner = Math.Clamp(innerPercent, 0.0, 100.0);

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
    private bool IsOperationAlreadyTerminal()
    {
        var opId = _currentTrackerOperationId;
        if (!opId.HasValue)
        {
            return true;
        }

        return _operationTracker.GetOperation(opId.Value)?.Status
            is (OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled);
    }

    private bool WasCancelled()
    {
        if (_cancellationTokenSource?.IsCancellationRequested == true)
        {
            return true;
        }

        if (_currentTrackerOperationId.HasValue)
        {
            var op = _operationTracker.GetOperation(_currentTrackerOperationId.Value);
            if (op?.Cancelled == true || op?.Status == OperationStatus.Cancelling)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Drives the operation to a cancelled terminal state. The terminal LogRemovalComplete
    /// (cancelled) event is emitted via onTerminalEmit inside CompleteOperation; this only
    /// captures the cancel message and calls CompleteOperation.
    /// </summary>
    private Task CompleteCancelledAsync(string service, string? datasourceName = null)
    {
        var message = datasourceName != null
            ? $"Service removal for {service} in {datasourceName} was cancelled"
            : $"Service removal for {service} was cancelled";

        _completionMetrics = _completionMetrics with { CancelMessage = message };

        if (_currentTrackerOperationId.HasValue)
        {
            _operationTracker.CompleteOperation(_currentTrackerOperationId.Value, success: false, error: "Cancelled by user");
        }

        return Task.CompletedTask;
    }

    private async Task<ProgressData?> ReadProgressFileAsync(string progressPath)
    {
        return await _rustProcessHelper.ReadProgressFileAsync<ProgressData>(progressPath);
    }

    public async Task<ProgressData?> GetProgressAsync()
    {
        var datasourceName = CurrentDatasource;
        if (datasourceName == null)
        {
            return null;
        }

        var operationsDir = _pathResolver.GetOperationsDirectory();
        var progressPath = Path.Combine(operationsDir, $"log_remove_progress_{datasourceName}.json");
        return await ReadProgressFileAsync(progressPath);
    }

    /// <summary>
    /// Result of database cleanup operation
    /// </summary>
    private class DatabaseCleanupResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int TotalDeleted { get; set; }
        public int LogEntriesDeleted { get; set; }
        public int DownloadsDeleted { get; set; }
        public int ServiceStatsDeleted { get; set; }
    }

    /// <summary>
    /// Cleans up database records for a removed service.
    /// Deletes LogEntries, Downloads, and ServiceStats for the specified service.
    /// </summary>
    private async Task<DatabaseCleanupResult> CleanupDbRecordsAsync(string service)
    {
        var result = new DatabaseCleanupResult();

        try
        {
            _logger.LogInformation("Starting database cleanup for service: {Service}", service);

            // Send progress update
            await _notifications.NotifyAllAsync(SignalREvents.LogRemovalProgress, new
            {
                OperationId = _currentTrackerOperationId,
                PercentComplete = 95.0,
                Status = OperationStatus.Running,
                StageKey = "signalr.logRemoval.cleaningDatabase",
                Context = new Dictionary<string, object?> { ["service"] = service },
                FilesProcessed = 0,
                LinesProcessed = 0,
                LinesRemoved = 0,
                Service = service
            });

            // Use a new DbContext from factory for this operation
            await using var context = await _dbContextFactory.CreateDbContextAsync();

            // Service names in the database are stored in lowercase
            var serviceLower = service.ToLowerInvariant();

            // Delete LogEntries for this service first (foreign key constraint)
            // LogEntries reference Downloads, so delete them first
            result.LogEntriesDeleted = await context.LogEntries
                .Where(le => le.Service.ToLower() == serviceLower)
                .ExecuteDeleteAsync();
            _logger.LogInformation("Deleted {Count} LogEntries for service {Service}", result.LogEntriesDeleted, service);

            // Delete Downloads for this service
            result.DownloadsDeleted = await context.Downloads
                .Where(d => d.Service.ToLower() == serviceLower)
                .ExecuteDeleteAsync();
            _logger.LogInformation("Deleted {Count} Downloads for service {Service}", result.DownloadsDeleted, service);

            // Delete ServiceStats for this service
            result.ServiceStatsDeleted = await context.ServiceStats
                .Where(s => s.Service.ToLower() == serviceLower)
                .ExecuteDeleteAsync();
            _logger.LogInformation("Deleted {Count} ServiceStats for service {Service}", result.ServiceStatsDeleted, service);

            result.TotalDeleted = result.LogEntriesDeleted + result.DownloadsDeleted + result.ServiceStatsDeleted;
            result.Success = true;
            result.Message = $"Deleted {result.DownloadsDeleted} downloads, {result.LogEntriesDeleted} log entries, {result.ServiceStatsDeleted} service stats";

            _logger.LogInformation("Database cleanup completed for service {Service}: {Message}", service, result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during database cleanup for service {Service}", service);
            result.Success = false;
            result.Message = $"Error: {ex.Message}";
        }

        return result;
    }
}
