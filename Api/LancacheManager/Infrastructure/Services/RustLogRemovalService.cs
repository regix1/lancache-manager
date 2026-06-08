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

    private readonly DatasourceService _datasourceService;

    public bool IsProcessing { get; private set; }
    public string? CurrentService { get; private set; }
    public Guid? CurrentOperationId { get; private set; }
    public string? CurrentDatasource { get; private set; }

    /// <summary>
    /// Starts service removal operation (wrapper for StartRemovalAsync)
    /// </summary>
    public Task<bool> StartServiceRemovalAsync(string service)
    {
        return StartRemovalAsync(service);
    }

    /// <summary>
    /// Starts service removal in the background and returns the operation id as soon as it is registered.
    /// </summary>
    public Task<Guid?> StartServiceRemovalInBackgroundAsync(string service)
    {
        return StartRemovalInBackgroundAsync(() => StartRemovalAsync(service));
    }

    /// <summary>
    /// Starts per-datasource service removal in the background and returns the operation id as soon as it is registered.
    /// </summary>
    public Task<Guid?> StartServiceRemovalForDatasourceInBackgroundAsync(string service, string datasourceName)
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
    /// Starts service removal operation for a specific datasource
    /// </summary>
    public Task<bool> StartServiceRemovalForDatasourceAsync(string service, string datasourceName)
    {
        return StartRemovalForDatasourceAsync(service, datasourceName);
    }

    /// <summary>
    /// Gets the removal status including isProcessing and service fields
    /// </summary>
    public object GetRemovalStatus()
    {
        ProgressData? progress = null;
        try
        {
            var operationsDir = _pathResolver.GetOperationsDirectory();
            var progressPath = Path.Combine(operationsDir, "log_remove_progress.json");
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
            stageKey = progress?.StageKey ?? ""
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

    public async Task<bool> StartRemovalAsync(string service)
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

            // Register with unified operation tracker for centralized cancellation
            _currentTrackerOperationId = _operationTracker.RegisterOperation(
                OperationType.LogRemoval,
                "Log Removal",
                _cancellationTokenSource,
                new { service }
            );
            NotifyOperationRegistered();

            var datasources = _datasourceService.GetDatasources();

            if (datasources.Count == 0)
            {
                _logger.LogWarning("No datasources configured for log removal");

                await _notifications.SendOperationCompleteAsync(
                    SignalREvents.LogRemovalComplete, _currentTrackerOperationId,
                    success: false, message: "No datasources configured for log removal", cancelled: false,
                    new { Service = service });

                if (_currentTrackerOperationId.HasValue)
                {
                    _operationTracker.CompleteOperation(_currentTrackerOperationId.Value, success: false, error: "No datasources configured");
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
                    var arguments = $"remove \"{logDir}\" \"{service}\" \"{dsProgressPath}\"";
                    _logger.LogInformation("Rust arguments for datasource '{DatasourceName}': {Arguments}",
                        datasource.Name, arguments);

                    var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                        rustExecutablePath,
                        arguments,
                        Path.GetDirectoryName(rustExecutablePath));

                    await _notifications.NotifyAllAsync(SignalREvents.LogRemovalProgress, new
                    {
                        OperationId = _currentTrackerOperationId,
                        PercentComplete = (double)datasourcesProcessed / datasources.Count * 100.0,
                        Status = OperationStatus.Running,
                        StageKey = "signalr.logRemoval.processingDatasource",
                        Context = new Dictionary<string, object?> { ["service"] = service, ["datasourceName"] = datasource.Name },
                        FilesProcessed = totalFilesProcessed,
                        LinesProcessed = totalLinesProcessed,
                        LinesRemoved = totalLinesRemoved,
                        Service = service,
                        Datasource = datasource.Name
                    });

                    var result = await _rustProcessHelper.ExecuteTrackedProcessWithProgressAsync<ProgressData>(
                        startInfo,
                        _currentTrackerOperationId,
                        _cancellationTokenSource.Token,
                        dsProgressPath,
                        progress => SendLogRemovalProgressAsync(progress, service, datasource.Name),
                        processLabel: "log_removal");

                    var exitCode = result.ExitCode;
                    _logger.LogInformation("Rust log_manager exited with code {ExitCode} for datasource '{DatasourceName}'",
                        exitCode, datasource.Name);

                    if (WasLogRemovalCancelled())
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
                    if (WasLogRemovalCancelled())
                    {
                        await CompleteLogRemovalCancelledAsync(service);
                        return false;
                    }

                    _logger.LogWarning("Log removal failed for datasource '{DatasourceName}', continuing with remaining datasources",
                        datasource.Name);
                }

                datasourcesProcessed++;
            }

            if (WasLogRemovalCancelled())
            {
                await CompleteLogRemovalCancelledAsync(service);
                return false;
            }

            _logger.LogInformation(
                "Log removal across all datasources complete: {Processed} processed, {Skipped} skipped, totalLinesRemoved={TotalLinesRemoved}",
                datasourcesProcessed, datasourcesSkipped, totalLinesRemoved);

            if (allSuccess && datasourcesProcessed > 0)
            {
                // Invalidate service counts cache so UI refreshes
                await _cacheManagementService.InvalidateServiceCountsCacheAsync();

                // Signal nginx to reopen log files (prevents monolithic container from losing log access)
                await _nginxLogRotationService.ReopenNginxLogsAsync();

                // Clean up database records for this service
                var dbCleanupResult = await CleanupDatabaseRecordsAsync(service);

                // Send completion notification
                var logMessage = $"Successfully removed {service} entries from {datasourcesProcessed} datasource(s)";
                var message = dbCleanupResult.Success
                    ? $"{logMessage}. Database: {dbCleanupResult.Message}"
                    : $"{logMessage}. Database cleanup: {dbCleanupResult.Message}";

                await _notifications.SendOperationCompleteAsync(
                    SignalREvents.LogRemovalComplete, _currentTrackerOperationId,
                    success: true, message: message, cancelled: false,
                    new { FilesProcessed = totalFilesProcessed, LinesProcessed = totalLinesProcessed, LinesRemoved = totalLinesRemoved, DatabaseRecordsDeleted = dbCleanupResult.TotalDeleted, Service = service });

                _logger.LogInformation(
                    "Log removal completed successfully for {Service}: {DatasourcesProcessed} datasource(s), Removed {LinesRemoved} of {LinesProcessed} lines, {DbRecords} database records",
                    service, datasourcesProcessed, totalLinesRemoved, totalLinesProcessed, dbCleanupResult.TotalDeleted);

                // Mark operation as complete in unified tracker
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

                await _notifications.SendOperationCompleteAsync(
                    SignalREvents.LogRemovalComplete, _currentTrackerOperationId,
                    success: false, message: skipMessage, cancelled: false,
                    new { Service = service });

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

                await _notifications.SendOperationCompleteAsync(
                    SignalREvents.LogRemovalComplete, _currentTrackerOperationId,
                    success: false, message: failMessage, cancelled: false,
                    new { FilesProcessed = totalFilesProcessed, LinesProcessed = totalLinesProcessed, LinesRemoved = totalLinesRemoved, Service = service });

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

            await _notifications.SendOperationCompleteAsync(
                SignalREvents.LogRemovalComplete, _currentTrackerOperationId,
                success: false, message: $"Service removal for {service} was cancelled", cancelled: true,
                new { Service = service });

            // Mark operation as cancelled in unified tracker
            if (_currentTrackerOperationId.HasValue)
            {
                _operationTracker.CompleteOperation(_currentTrackerOperationId.Value, success: false, error: "Cancelled by user");
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during log removal for {Service}", service);

            // Send error notification
            try
            {
                await _notifications.SendOperationCompleteAsync(
                    SignalREvents.LogRemovalComplete, _currentTrackerOperationId,
                    success: false, message: $"Error during log removal: {ex.Message}", cancelled: false,
                    new { Service = service });
            }
            catch (Exception notifyEx) { _logger.LogWarning(notifyEx, "Failed to send operation complete notification"); }

            // Mark operation as failed in unified tracker
            if (_currentTrackerOperationId.HasValue)
            {
                _operationTracker.CompleteOperation(_currentTrackerOperationId.Value, success: false, error: ex.Message);
            }

            return false;
        }
        finally
        {
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
    public async Task<bool> StartRemovalForDatasourceAsync(string service, string datasourceName)
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

            // Register with unified operation tracker for centralized cancellation
            _currentTrackerOperationId = _operationTracker.RegisterOperation(
                OperationType.LogRemoval,
                "Log Removal",
                _cancellationTokenSource,
                new { service, datasourceName }
            );
            NotifyOperationRegistered();

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
                var arguments = $"remove \"{logDir}\" \"{service}\" \"{progressPath}\"";
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

                var result = await _rustProcessHelper.ExecuteTrackedProcessWithProgressAsync<ProgressData>(
                    startInfo,
                    _currentTrackerOperationId,
                    _cancellationTokenSource.Token,
                    progressPath,
                    progress => SendLogRemovalProgressAsync(progress, service, datasourceName),
                    processLabel: "log_removal");

                var exitCode = result.ExitCode;
                _logger.LogInformation("Rust log_manager exited with code {ExitCode} for datasource {Datasource}", exitCode, datasourceName);

                if (WasLogRemovalCancelled())
                {
                    await CompleteLogRemovalCancelledAsync(service, datasourceName);
                    return false;
                }

                if (exitCode == 0)
                {
                    await _cacheManagementService.InvalidateServiceCountsCacheAsync();
                    await _nginxLogRotationService.ReopenNginxLogsAsync();

                    // Note: Database cleanup is not datasource-specific, so we skip it for per-datasource removal
                    // The user would need to remove from all datasources to clean up DB records

                    var finalProgress = await ReadProgressFileAsync(progressPath);
                    await _notifications.SendOperationCompleteAsync(
                        SignalREvents.LogRemovalComplete, _currentTrackerOperationId,
                        success: true, message: finalProgress?.StageKey ?? $"Successfully removed {service} entries from {datasourceName}", cancelled: false,
                        new { FilesProcessed = finalProgress?.FilesProcessed ?? 0, LinesProcessed = finalProgress?.LinesProcessed ?? 0, LinesRemoved = finalProgress?.LinesRemoved ?? 0, Service = service, Datasource = datasourceName });

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
                    await _notifications.SendOperationCompleteAsync(
                        SignalREvents.LogRemovalComplete, _currentTrackerOperationId,
                        success: false, message: $"Failed to remove {service} entries from {datasourceName}", cancelled: false,
                        new { Service = service, Datasource = datasourceName });

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

            await CompleteLogRemovalCancelledAsync(service, datasourceName);

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during log removal for {Service} in {Datasource}", service, datasourceName);

            try
            {
                await _notifications.SendOperationCompleteAsync(
                    SignalREvents.LogRemovalComplete, _currentTrackerOperationId,
                    success: false, message: $"Error during log removal: {ex.Message}", cancelled: false,
                    new { Service = service, Datasource = datasourceName });
            }
            catch (Exception notifyEx) { _logger.LogWarning(notifyEx, "Failed to send operation complete notification"); }

            // Mark operation as failed in unified tracker
            if (_currentTrackerOperationId.HasValue)
            {
                _operationTracker.CompleteOperation(_currentTrackerOperationId.Value, success: false, error: ex.Message);
            }

            return false;
        }
        finally
        {
            IsProcessing = false;
            CurrentService = null;
            CurrentDatasource = null;
            _currentTrackerOperationId = null;
            _cancellationTokenSource?.Dispose();
        }
    }

    private Task SendLogRemovalProgressAsync(ProgressData progress, string service, string datasourceName)
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

        return _notifications.NotifyAllAsync(SignalREvents.LogRemovalProgress, new
        {
            OperationId = _currentTrackerOperationId,
            progress.PercentComplete,
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

    private bool WasLogRemovalCancelled()
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

    private async Task CompleteLogRemovalCancelledAsync(string service, string? datasourceName = null)
    {
        var message = datasourceName != null
            ? $"Service removal for {service} in {datasourceName} was cancelled"
            : $"Service removal for {service} was cancelled";

        await _notifications.SendOperationCompleteAsync(
            SignalREvents.LogRemovalComplete, _currentTrackerOperationId,
            success: false, message: message, cancelled: true,
            new { Service = service, Datasource = datasourceName });

        if (_currentTrackerOperationId.HasValue)
        {
            _operationTracker.CompleteOperation(_currentTrackerOperationId.Value, success: false, error: "Cancelled by user");
        }
    }

    private async Task<ProgressData?> ReadProgressFileAsync(string progressPath)
    {
        return await _rustProcessHelper.ReadProgressFileAsync<ProgressData>(progressPath);
    }

    public async Task<ProgressData?> GetProgressAsync()
    {
        var operationsDir = _pathResolver.GetOperationsDirectory();
        var progressPath = Path.Combine(operationsDir, "log_remove_progress.json");
        return await ReadProgressFileAsync(progressPath);
    }

    /// <summary>
    /// Cancels the current service removal operation gracefully.
    /// Delegates to UnifiedOperationTracker for centralized cancellation.
    /// </summary>
    /// <remarks>
    /// This method exists for backward compatibility.
    /// Prefer using OperationsController.CancelOperation with operationId for new code.
    /// </remarks>
    public bool CancelOperation()
    {
        if (_currentTrackerOperationId.HasValue)
        {
            _logger.LogInformation("Cancelling service removal via UnifiedOperationTracker: {OperationId}", _currentTrackerOperationId);
            return _operationTracker.CancelOperation(_currentTrackerOperationId.Value);
        }

        // Fallback: Cancel directly if tracker ID not available
        if (!IsProcessing || _cancellationTokenSource == null)
        {
            return false;
        }

        // If cancellation is already requested, return true (idempotent)
        // This prevents 404 errors when user clicks cancel button multiple times
        if (_cancellationTokenSource.IsCancellationRequested)
        {
            _logger.LogDebug("Cancellation already in progress for service removal: {Service}", CurrentService);
            return true;
        }

        _logger.LogInformation("Cancelling service removal operation for {Service}", CurrentService);
        _cancellationTokenSource.Cancel();
        return true;
    }

    /// <summary>
    /// Force kills the Rust process for service removal.
    /// Used as fallback when graceful cancellation fails.
    /// </summary>
    public async Task<bool> ForceKillOperationAsync()
    {
        if (!IsProcessing)
        {
            return false;
        }

        _logger.LogWarning("Force killing service removal operation for {Service}", CurrentService);

        try
        {
            if (_currentTrackerOperationId.HasValue)
            {
                _operationTracker.ForceKillOperation(_currentTrackerOperationId.Value);
            }
            else
            {
                _cancellationTokenSource?.Cancel();
            }

            await Task.Delay(500);

            await _notifications.SendOperationCompleteAsync(
                SignalREvents.LogRemovalComplete, _currentTrackerOperationId,
                success: false, message: $"Service removal for {CurrentService} was cancelled", cancelled: true,
                new { Service = CurrentService });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error force killing service removal for {Service}", CurrentService);
            return false;
        }
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
    private async Task<DatabaseCleanupResult> CleanupDatabaseRecordsAsync(string service)
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
