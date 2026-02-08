using System.Diagnostics;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Hubs;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Models;
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
    private readonly ProcessManager _processManager;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly NginxLogRotationService _nginxLogRotationService;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IUnifiedOperationTracker _operationTracker;
    private Process? _rustProcess;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private string? _currentTrackerOperationId;

    private readonly DatasourceService _datasourceService;

    public bool IsProcessing { get; private set; }
    public string? CurrentService { get; private set; }
    public string? CurrentOperationId { get; private set; }
    public string? CurrentDatasource { get; private set; }

    /// <summary>
    /// Starts service removal operation (wrapper for StartRemovalAsync)
    /// </summary>
    public Task<bool> StartServiceRemovalAsync(string service)
    {
        return StartRemovalAsync(service);
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
            filesProcessed = progress?.FilesProcessed ?? 0,
            linesProcessed = progress?.LinesProcessed ?? 0,
            linesRemoved = progress?.LinesRemoved ?? 0,
            percentComplete = progress?.PercentComplete ?? 0,
            status = progress?.Status ?? (IsProcessing ? "starting" : "idle"),
            message = progress?.Message ?? ""
        };
    }

    public RustLogRemovalService(
        ILogger<RustLogRemovalService> logger,
        IPathResolver pathResolver,
        ISignalRNotificationService notifications,
        CacheManagementService cacheManagementService,
        ProcessManager processManager,
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
        _processManager = processManager;
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

        [System.Text.Json.Serialization.JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    public async Task<bool> StartRemovalAsync(string service)
    {
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
                OperationType.ServiceRemoval,
                "Service Removal",
                _cancellationTokenSource,
                new { service }
            );

            var datasources = _datasourceService.GetDatasources();

            if (datasources.Count == 0)
            {
                _logger.LogWarning("No datasources configured for log removal");

                await _notifications.NotifyAllAsync(SignalREvents.LogRemovalComplete, new
                {
                    OperationId = _currentTrackerOperationId,
                    Success = false,
                    Status = OperationStatus.Failed,
                    Message = "No datasources configured for log removal",
                    Cancelled = false,
                    Service = service
                });

                if (!string.IsNullOrEmpty(_currentTrackerOperationId))
                {
                    _operationTracker.CompleteOperation(_currentTrackerOperationId, success: false, error: "No datasources configured");
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
                Message = $"Starting removal of {service} entries from logs..."
            });

            // Send initial progress notification
            await _notifications.NotifyAllAsync(SignalREvents.LogRemovalProgress, new
            {
                OperationId = _currentTrackerOperationId,
                PercentComplete = 0.0,
                Status = OperationStatus.Running,
                Message = $"Starting removal of {service} entries from {datasources.Count} datasource(s)...",
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

                    _rustProcess = Process.Start(startInfo);

                    if (_rustProcess == null)
                    {
                        throw new Exception($"Failed to start Rust process for datasource '{datasource.Name}'");
                    }

                    // Monitor stdout and stderr - track tasks for proper cleanup
                    var (stdoutTask, stderrTask) = _rustProcessHelper.CreateOutputMonitoringTasks(_rustProcess, "Rust log removal");

                    // Send datasource-level progress notification
                    await _notifications.NotifyAllAsync(SignalREvents.LogRemovalProgress, new
                    {
                        OperationId = _currentTrackerOperationId,
                        PercentComplete = (double)datasourcesProcessed / datasources.Count * 100.0,
                        Status = OperationStatus.Running,
                        Message = $"Removing {service} entries from datasource '{datasource.Name}'...",
                        FilesProcessed = totalFilesProcessed,
                        LinesProcessed = totalLinesProcessed,
                        LinesRemoved = totalLinesRemoved,
                        Service = service,
                        Datasource = datasource.Name
                    });

                    // Start progress monitoring task using the datasource-specific progress file
                    using var dsMonitorCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);
                    var progressTask = Task.Run(
                        async () => await MonitorProgressAsync(dsProgressPath, service, dsMonitorCts.Token));

                    // Wait for process to complete with graceful cancellation handling
                    await _processManager.WaitForProcessAsync(_rustProcess, _cancellationTokenSource.Token);

                    var exitCode = _rustProcess.ExitCode;
                    _logger.LogInformation("Rust log_manager exited with code {ExitCode} for datasource '{DatasourceName}'",
                        exitCode, datasource.Name);

                    // Wait for stdout/stderr reading tasks to complete
                    await _rustProcessHelper.WaitForOutputTasksAsync(stdoutTask, stderrTask, TimeSpan.FromSeconds(5));

                    // Stop the progress monitoring task for this datasource
                    dsMonitorCts.Cancel();
                    try
                    {
                        await progressTask;
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected
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
                    _logger.LogWarning("Log removal failed for datasource '{DatasourceName}', continuing with remaining datasources",
                        datasource.Name);
                }

                datasourcesProcessed++;
            }

            _logger.LogInformation(
                "Log removal across all datasources complete: {Processed} processed, {Skipped} skipped, totalLinesRemoved={TotalLinesRemoved}",
                datasourcesProcessed, datasourcesSkipped, totalLinesRemoved);

            if (allSuccess && datasourcesProcessed > 0)
            {
                // Invalidate service counts cache so UI refreshes
                await _cacheManagementService.InvalidateServiceCountsCache();

                // Signal nginx to reopen log files (prevents monolithic container from losing log access)
                await _nginxLogRotationService.ReopenNginxLogsAsync();

                // Clean up database records for this service
                var dbCleanupResult = await CleanupDatabaseRecordsAsync(service);

                // Send completion notification
                var logMessage = $"Successfully removed {service} entries from {datasourcesProcessed} datasource(s)";
                var message = dbCleanupResult.Success
                    ? $"{logMessage}. Database: {dbCleanupResult.Message}"
                    : $"{logMessage}. Database cleanup: {dbCleanupResult.Message}";

                await _notifications.NotifyAllAsync(SignalREvents.LogRemovalComplete, new
                {
                    OperationId = _currentTrackerOperationId,
                    Success = true,
                    Status = OperationStatus.Completed,
                    Message = message,
                    Cancelled = false,
                    FilesProcessed = totalFilesProcessed,
                    LinesProcessed = totalLinesProcessed,
                    LinesRemoved = totalLinesRemoved,
                    DatabaseRecordsDeleted = dbCleanupResult.TotalDeleted,
                    Service = service
                });

                _logger.LogInformation(
                    "Log removal completed successfully for {Service}: {DatasourcesProcessed} datasource(s), Removed {LinesRemoved} of {LinesProcessed} lines, {DbRecords} database records",
                    service, datasourcesProcessed, totalLinesRemoved, totalLinesProcessed, dbCleanupResult.TotalDeleted);

                // Mark operation as complete in unified tracker
                if (!string.IsNullOrEmpty(_currentTrackerOperationId))
                {
                    _operationTracker.CompleteOperation(_currentTrackerOperationId, success: true);
                }

                return true;
            }
            else if (datasourcesProcessed == 0 && datasourcesSkipped > 0)
            {
                // All datasources were skipped (read-only or non-existent)
                var skipMessage = $"All {datasourcesSkipped} datasource(s) were skipped (read-only or missing log directories)";
                _logger.LogWarning(skipMessage);

                await _notifications.NotifyAllAsync(SignalREvents.LogRemovalComplete, new
                {
                    OperationId = _currentTrackerOperationId,
                    Success = false,
                    Status = OperationStatus.Failed,
                    Message = skipMessage,
                    Cancelled = false,
                    Service = service
                });

                if (!string.IsNullOrEmpty(_currentTrackerOperationId))
                {
                    _operationTracker.CompleteOperation(_currentTrackerOperationId, success: false, error: skipMessage);
                }

                return false;
            }
            else
            {
                // Some datasources failed
                var failMessage = $"Log removal for {service} completed with errors across datasources";

                await _notifications.NotifyAllAsync(SignalREvents.LogRemovalComplete, new
                {
                    OperationId = _currentTrackerOperationId,
                    Success = false,
                    Status = OperationStatus.Failed,
                    Message = failMessage,
                    Cancelled = false,
                    FilesProcessed = totalFilesProcessed,
                    LinesProcessed = totalLinesProcessed,
                    LinesRemoved = totalLinesRemoved,
                    Service = service
                });

                _logger.LogError("Log removal failed for {Service}: some datasources had errors", service);

                if (!string.IsNullOrEmpty(_currentTrackerOperationId))
                {
                    _operationTracker.CompleteOperation(_currentTrackerOperationId, success: false, error: failMessage);
                }

                return false;
            }
        }
        catch (OperationCanceledException)
        {
            // Handle cancellation gracefully
            _logger.LogInformation("Service removal for {Service} was cancelled by user", service);

            await _notifications.NotifyAllAsync(SignalREvents.LogRemovalComplete, new
            {
                OperationId = _currentTrackerOperationId,
                Success = false,
                Status = OperationStatus.Cancelled,
                Message = $"Service removal for {service} was cancelled",
                Cancelled = true,
                Service = service
            });

            // Mark operation as cancelled in unified tracker
            if (!string.IsNullOrEmpty(_currentTrackerOperationId))
            {
                _operationTracker.CompleteOperation(_currentTrackerOperationId, success: false, error: "Cancelled by user");
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during log removal for {Service}", service);

            // Send error notification
            try
            {
                await _notifications.NotifyAllAsync(SignalREvents.LogRemovalComplete, new
                {
                    OperationId = _currentTrackerOperationId,
                    Success = false,
                    Status = OperationStatus.Failed,
                    Message = $"Error during log removal: {ex.Message}",
                    Cancelled = false,
                    Service = service
                });
            }
            catch { }

            // Mark operation as failed in unified tracker
            if (!string.IsNullOrEmpty(_currentTrackerOperationId))
            {
                _operationTracker.CompleteOperation(_currentTrackerOperationId, success: false, error: ex.Message);
            }

            return false;
        }
        finally
        {
            IsProcessing = false;
            CurrentService = null;
            CurrentDatasource = null;
            _rustProcess = null;
            _currentTrackerOperationId = null;
            _cancellationTokenSource?.Dispose();
        }
    }

    /// <summary>
    /// Starts service removal for a specific datasource only
    /// </summary>
    public async Task<bool> StartRemovalForDatasourceAsync(string service, string datasourceName)
    {
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
                OperationType.ServiceRemoval,
                "Service Removal",
                _cancellationTokenSource,
                new { service, datasourceName }
            );

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

                _rustProcess = Process.Start(startInfo);

                if (_rustProcess == null)
                {
                    throw new Exception("Failed to start Rust process");
                }

                var (stdoutTask, stderrTask) = _rustProcessHelper.CreateOutputMonitoringTasks(_rustProcess, "Rust log removal");

                // Send started event
                await _notifications.NotifyAllAsync(SignalREvents.LogRemovalStarted, new
                {
                    OperationId = _currentTrackerOperationId,
                    Message = $"Starting removal of {service} entries from {datasourceName}..."
                });

                await _notifications.NotifyAllAsync(SignalREvents.LogRemovalProgress, new
                {
                    OperationId = _currentTrackerOperationId,
                    PercentComplete = 0.0,
                    Status = OperationStatus.Running,
                    Message = $"Starting removal of {service} entries from {datasourceName}...",
                    FilesProcessed = 0,
                    LinesProcessed = 0,
                    LinesRemoved = 0,
                    Service = service,
                    Datasource = datasourceName
                });

                var progressTask = Task.Run(async () => await MonitorProgressAsync(progressPath, service, _cancellationTokenSource.Token));

                await _processManager.WaitForProcessAsync(_rustProcess, _cancellationTokenSource.Token);

                var exitCode = _rustProcess.ExitCode;
                _logger.LogInformation("Rust log_manager exited with code {ExitCode} for datasource {Datasource}", exitCode, datasourceName);

                await _rustProcessHelper.WaitForOutputTasksAsync(stdoutTask, stderrTask, TimeSpan.FromSeconds(5));

                _cancellationTokenSource.Cancel();
                try { await progressTask; } catch (OperationCanceledException) { }

                if (exitCode == 0)
                {
                    await _cacheManagementService.InvalidateServiceCountsCache();
                    await _nginxLogRotationService.ReopenNginxLogsAsync();

                    // Note: Database cleanup is not datasource-specific, so we skip it for per-datasource removal
                    // The user would need to remove from all datasources to clean up DB records

                    var finalProgress = await ReadProgressFileAsync(progressPath);
                    await _notifications.NotifyAllAsync(SignalREvents.LogRemovalComplete, new
                    {
                        OperationId = _currentTrackerOperationId,
                        Success = true,
                        Status = OperationStatus.Completed,
                        Message = finalProgress?.Message ?? $"Successfully removed {service} entries from {datasourceName}",
                        Cancelled = false,
                        FilesProcessed = finalProgress?.FilesProcessed ?? 0,
                        LinesProcessed = finalProgress?.LinesProcessed ?? 0,
                        LinesRemoved = finalProgress?.LinesRemoved ?? 0,
                        Service = service,
                        Datasource = datasourceName
                    });

                    _logger.LogInformation("Log removal completed for {Service} in datasource {Datasource}: Removed {LinesRemoved} lines",
                        service, datasourceName, finalProgress?.LinesRemoved ?? 0);

                    // Mark operation as complete in unified tracker
                    if (!string.IsNullOrEmpty(_currentTrackerOperationId))
                    {
                        _operationTracker.CompleteOperation(_currentTrackerOperationId, success: true);
                    }
                    return true;
                }
                else
                {
                    await _notifications.NotifyAllAsync(SignalREvents.LogRemovalComplete, new
                    {
                        OperationId = _currentTrackerOperationId,
                        Success = false,
                        Status = OperationStatus.Failed,
                        Message = $"Failed to remove {service} entries from {datasourceName}",
                        Cancelled = false,
                        Service = service,
                        Datasource = datasourceName
                    });

                    _logger.LogError("Log removal failed for {Service} in datasource {Datasource} with exit code {ExitCode}",
                        service, datasourceName, exitCode);

                    // Mark operation as failed in unified tracker
                    if (!string.IsNullOrEmpty(_currentTrackerOperationId))
                    {
                        _operationTracker.CompleteOperation(_currentTrackerOperationId, success: false, error: $"Exit code {exitCode}");
                    }
                    return false;
                }
            }, _cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Service removal for {Service} in {Datasource} was cancelled", service, datasourceName);

            await _notifications.NotifyAllAsync(SignalREvents.LogRemovalComplete, new
            {
                OperationId = _currentTrackerOperationId,
                Success = false,
                Status = OperationStatus.Cancelled,
                Message = $"Service removal for {service} in {datasourceName} was cancelled",
                Cancelled = true,
                Service = service,
                Datasource = datasourceName
            });

            // Mark operation as cancelled in unified tracker
            if (!string.IsNullOrEmpty(_currentTrackerOperationId))
            {
                _operationTracker.CompleteOperation(_currentTrackerOperationId, success: false, error: "Cancelled by user");
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during log removal for {Service} in {Datasource}", service, datasourceName);

            try
            {
                await _notifications.NotifyAllAsync(SignalREvents.LogRemovalComplete, new
                {
                    OperationId = _currentTrackerOperationId,
                    Success = false,
                    Status = OperationStatus.Failed,
                    Message = $"Error during log removal: {ex.Message}",
                    Cancelled = false,
                    Service = service,
                    Datasource = datasourceName
                });
            }
            catch { }

            // Mark operation as failed in unified tracker
            if (!string.IsNullOrEmpty(_currentTrackerOperationId))
            {
                _operationTracker.CompleteOperation(_currentTrackerOperationId, success: false, error: ex.Message);
            }

            return false;
        }
        finally
        {
            IsProcessing = false;
            CurrentService = null;
            CurrentDatasource = null;
            _rustProcess = null;
            _currentTrackerOperationId = null;
            _cancellationTokenSource?.Dispose();
        }
    }

    private async Task MonitorProgressAsync(string progressPath, string service, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(500, cancellationToken);

                var progress = await ReadProgressFileAsync(progressPath);
                if (progress != null)
                {
                    // Send progress update via SignalR with standardized format
                    await _notifications.NotifyAllAsync(SignalREvents.LogRemovalProgress, new
                    {
                        OperationId = _currentTrackerOperationId,
                        PercentComplete = progress.PercentComplete,
                        Status = OperationStatus.Running,
                        Message = progress.Message,
                        FilesProcessed = progress.FilesProcessed,
                        LinesProcessed = progress.LinesProcessed,
                        LinesRemoved = progress.LinesRemoved,
                        Service = service
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error monitoring Rust log removal progress");
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
        if (!string.IsNullOrEmpty(_currentTrackerOperationId))
        {
            _logger.LogInformation("Cancelling service removal via UnifiedOperationTracker: {OperationId}", _currentTrackerOperationId);
            return _operationTracker.CancelOperation(_currentTrackerOperationId);
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
    public async Task<bool> ForceKillOperation()
    {
        if (!IsProcessing)
        {
            return false;
        }

        _logger.LogWarning("Force killing service removal operation for {Service}", CurrentService);

        try
        {
            // First cancel the token
            _cancellationTokenSource?.Cancel();

            // Kill the Rust process if it exists and is still running
            if (_rustProcess != null && !_rustProcess.HasExited)
            {
                _logger.LogWarning("Killing Rust log_manager process (PID: {ProcessId}) for service {Service}",
                    _rustProcess.Id, CurrentService);
                _rustProcess.Kill(entireProcessTree: true);

                // Wait briefly for the process to exit
                await Task.Delay(500);

                if (!_rustProcess.HasExited)
                {
                    _logger.LogError("Process did not exit after Kill() for service {Service}", CurrentService);
                }
            }

            // Send cancellation notification
            await _notifications.NotifyAllAsync(SignalREvents.LogRemovalComplete, new
            {
                OperationId = _currentTrackerOperationId,
                Success = false,
                Status = OperationStatus.Cancelled,
                Message = $"Service removal for {CurrentService} was cancelled",
                Cancelled = true,
                Service = CurrentService
            });

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
                Message = $"Cleaning up database records for {service}...",
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
