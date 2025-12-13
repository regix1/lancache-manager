using System.Diagnostics;
using LancacheManager.Application.Services;
using LancacheManager.Data;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.AspNetCore.SignalR;
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
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly CacheManagementService _cacheManagementService;
    private readonly ProcessManager _processManager;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly NginxLogRotationService _nginxLogRotationService;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly StatsCache _statsCache;
    private Process? _rustProcess;
    private CancellationTokenSource? _cancellationTokenSource;

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
        IHubContext<DownloadHub> hubContext,
        CacheManagementService cacheManagementService,
        ProcessManager processManager,
        RustProcessHelper rustProcessHelper,
        NginxLogRotationService nginxLogRotationService,
        IDbContextFactory<AppDbContext> dbContextFactory,
        StatsCache statsCache,
        DatasourceService datasourceService)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _hubContext = hubContext;
        _cacheManagementService = cacheManagementService;
        _processManager = processManager;
        _rustProcessHelper = rustProcessHelper;
        _nginxLogRotationService = nginxLogRotationService;
        _dbContextFactory = dbContextFactory;
        _statsCache = statsCache;
        _datasourceService = datasourceService;
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
        if (IsProcessing)
        {
            _logger.LogWarning("Log removal is already running for service: {CurrentService}", CurrentService);
            return false;
        }

        try
        {
            IsProcessing = true;
            CurrentService = service;
            _cancellationTokenSource = new CancellationTokenSource();

            var operationsDir = _pathResolver.GetOperationsDirectory();
            var logDir = _pathResolver.GetLogsDirectory();
            var progressPath = Path.Combine(operationsDir, "log_remove_progress.json");
            var rustExecutablePath = _pathResolver.GetRustLogManagerPath();

            // Delete old progress file
            if (File.Exists(progressPath))
            {
                File.Delete(progressPath);
            }

            _logger.LogInformation("Starting Rust log removal for service: {Service}", service);
            _logger.LogInformation("Log directory: {LogDir}", logDir);
            _logger.LogInformation("Progress file: {ProgressPath}", progressPath);
            _logger.LogInformation("Rust executable: {Executable}", rustExecutablePath);

            // Wrap Rust process execution in shared lock to prevent concurrent access to log files
            // This prevents "Failed to persist temp file" errors when other processes are reading logs
            return await _cacheManagementService.ExecuteWithLockAsync(async () =>
            {
                // Start Rust process
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

                // Monitor stdout and stderr - track tasks for proper cleanup
                var (stdoutTask, stderrTask) = _rustProcessHelper.CreateOutputMonitoringTasks(_rustProcess, "Rust log removal");

                // Send initial progress notification
                await _hubContext.Clients.All.SendAsync("LogRemovalProgress", new
                {
                    filesProcessed = 0,
                    linesProcessed = 0,
                    linesRemoved = 0,
                    percentComplete = 0.0,
                    status = "starting",
                    message = $"Starting removal of {service} entries from logs...",
                    service
                });

                // Start progress monitoring task
                var progressTask = Task.Run(async () => await MonitorProgressAsync(progressPath, service, _cancellationTokenSource.Token));

                // Wait for process to complete with graceful cancellation handling
                await _processManager.WaitForProcessAsync(_rustProcess, _cancellationTokenSource.Token);

                var exitCode = _rustProcess.ExitCode;
                _logger.LogInformation("Rust log_manager exited with code {ExitCode}", exitCode);

                // Wait for stdout/stderr reading tasks to complete
                await _rustProcessHelper.WaitForOutputTasksAsync(stdoutTask, stderrTask, TimeSpan.FromSeconds(5));

                // Stop the progress monitoring task
                _cancellationTokenSource.Cancel();
                try
                {
                    await progressTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }

                if (exitCode == 0)
                {
                    // Invalidate service counts cache so UI refreshes
                    await _cacheManagementService.InvalidateServiceCountsCache();

                    // Signal nginx to reopen log files (prevents monolithic container from losing log access)
                    await _nginxLogRotationService.ReopenNginxLogsAsync();

                    // Clean up database records for this service
                    var dbCleanupResult = await CleanupDatabaseRecordsAsync(service);

                    // Send completion notification
                    var finalProgress = await ReadProgressFileAsync(progressPath);
                    var logMessage = finalProgress?.Message ?? $"Successfully removed {service} entries from logs";
                    var message = dbCleanupResult.Success
                        ? $"{logMessage}. Database: {dbCleanupResult.Message}"
                        : $"{logMessage}. Database cleanup: {dbCleanupResult.Message}";

                    await _hubContext.Clients.All.SendAsync("LogRemovalComplete", new
                    {
                        success = true,
                        message,
                        filesProcessed = finalProgress?.FilesProcessed ?? 0,
                        linesProcessed = finalProgress?.LinesProcessed ?? 0,
                        linesRemoved = finalProgress?.LinesRemoved ?? 0,
                        databaseRecordsDeleted = dbCleanupResult.TotalDeleted,
                        service
                    });

                    _logger.LogInformation("Log removal completed successfully for {Service}: Removed {LinesRemoved} of {LinesProcessed} lines, {DbRecords} database records",
                        service, finalProgress?.LinesRemoved ?? 0, finalProgress?.LinesProcessed ?? 0, dbCleanupResult.TotalDeleted);
                    return true;
                }
                else
                {
                    // Send failure notification
                    await _hubContext.Clients.All.SendAsync("LogRemovalComplete", new
                    {
                        success = false,
                        message = $"Failed to remove {service} entries from logs",
                        service
                    });

                    _logger.LogError("Log removal failed for {Service} with exit code {ExitCode}", service, exitCode);
                    return false;
                }
            }, _cancellationTokenSource.Token); // Close ExecuteWithLockAsync lambda
        }
        catch (OperationCanceledException)
        {
            // Handle cancellation gracefully
            _logger.LogInformation("Service removal for {Service} was cancelled by user", service);

            await _hubContext.Clients.All.SendAsync("LogRemovalComplete", new
            {
                success = false,
                message = $"Service removal for {service} was cancelled",
                cancelled = true,
                service
            });

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during log removal for {Service}", service);

            // Send error notification
            try
            {
                await _hubContext.Clients.All.SendAsync("LogRemovalComplete", new
                {
                    success = false,
                    message = $"Error during log removal: {ex.Message}",
                    service
                });
            }
            catch { }

            return false;
        }
        finally
        {
            IsProcessing = false;
            CurrentService = null;
            CurrentDatasource = null;
            _rustProcess = null;
            _cancellationTokenSource?.Dispose();
        }
    }

    /// <summary>
    /// Starts service removal for a specific datasource only
    /// </summary>
    public async Task<bool> StartRemovalForDatasourceAsync(string service, string datasourceName)
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

        try
        {
            IsProcessing = true;
            CurrentService = service;
            CurrentDatasource = datasourceName;
            _cancellationTokenSource = new CancellationTokenSource();

            var operationsDir = _pathResolver.GetOperationsDirectory();
            var logDir = datasource.LogPath;
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

                await _hubContext.Clients.All.SendAsync("LogRemovalProgress", new
                {
                    filesProcessed = 0,
                    linesProcessed = 0,
                    linesRemoved = 0,
                    percentComplete = 0.0,
                    status = "starting",
                    message = $"Starting removal of {service} entries from {datasourceName}...",
                    service,
                    datasource = datasourceName
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
                    await _hubContext.Clients.All.SendAsync("LogRemovalComplete", new
                    {
                        success = true,
                        message = finalProgress?.Message ?? $"Successfully removed {service} entries from {datasourceName}",
                        filesProcessed = finalProgress?.FilesProcessed ?? 0,
                        linesProcessed = finalProgress?.LinesProcessed ?? 0,
                        linesRemoved = finalProgress?.LinesRemoved ?? 0,
                        service,
                        datasource = datasourceName
                    });

                    _logger.LogInformation("Log removal completed for {Service} in datasource {Datasource}: Removed {LinesRemoved} lines",
                        service, datasourceName, finalProgress?.LinesRemoved ?? 0);
                    return true;
                }
                else
                {
                    await _hubContext.Clients.All.SendAsync("LogRemovalComplete", new
                    {
                        success = false,
                        message = $"Failed to remove {service} entries from {datasourceName}",
                        service,
                        datasource = datasourceName
                    });

                    _logger.LogError("Log removal failed for {Service} in datasource {Datasource} with exit code {ExitCode}",
                        service, datasourceName, exitCode);
                    return false;
                }
            }, _cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Service removal for {Service} in {Datasource} was cancelled", service, datasourceName);

            await _hubContext.Clients.All.SendAsync("LogRemovalComplete", new
            {
                success = false,
                message = $"Service removal for {service} in {datasourceName} was cancelled",
                cancelled = true,
                service,
                datasource = datasourceName
            });

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during log removal for {Service} in {Datasource}", service, datasourceName);

            try
            {
                await _hubContext.Clients.All.SendAsync("LogRemovalComplete", new
                {
                    success = false,
                    message = $"Error during log removal: {ex.Message}",
                    service,
                    datasource = datasourceName
                });
            }
            catch { }

            return false;
        }
        finally
        {
            IsProcessing = false;
            CurrentService = null;
            CurrentDatasource = null;
            _rustProcess = null;
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
                    // Send progress update via SignalR
                    await _hubContext.Clients.All.SendAsync("LogRemovalProgress", new
                    {
                        filesProcessed = progress.FilesProcessed,
                        linesProcessed = progress.LinesProcessed,
                        linesRemoved = progress.LinesRemoved,
                        percentComplete = progress.PercentComplete,
                        status = progress.Status,
                        message = progress.Message,
                        service
                    }, cancellationToken);
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
    /// Cancels the current service removal operation gracefully
    /// </summary>
    public bool CancelOperation()
    {
        if (!IsProcessing || _cancellationTokenSource == null)
        {
            return false;
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
            await _hubContext.Clients.All.SendAsync("LogRemovalComplete", new
            {
                success = false,
                message = $"Service removal for {CurrentService} was cancelled",
                cancelled = true,
                service = CurrentService
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
            await _hubContext.Clients.All.SendAsync("LogRemovalProgress", new
            {
                filesProcessed = 0,
                linesProcessed = 0,
                linesRemoved = 0,
                percentComplete = 95.0,
                status = "cleaning_database",
                message = $"Cleaning up database records for {service}...",
                service
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

            // Invalidate caches so UI refreshes with new data
            _statsCache.InvalidateDownloads();

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
