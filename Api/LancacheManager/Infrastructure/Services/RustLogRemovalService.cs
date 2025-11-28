using System.Diagnostics;
using LancacheManager.Application.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.AspNetCore.SignalR;

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
    private Process? _rustProcess;
    private CancellationTokenSource? _cancellationTokenSource;

    public bool IsProcessing { get; private set; }
    public string? CurrentService { get; private set; }
    public string? CurrentOperationId { get; private set; }

    /// <summary>
    /// Starts service removal operation (wrapper for StartRemovalAsync)
    /// </summary>
    public Task<bool> StartServiceRemovalAsync(string service)
    {
        return StartRemovalAsync(service);
    }

    /// <summary>
    /// Gets the removal status including isProcessing and service fields
    /// </summary>
    public object GetRemovalStatus()
    {
        ProgressData? progress = null;
        try
        {
            var dataDirectory = _pathResolver.GetDataDirectory();
            var progressPath = Path.Combine(dataDirectory, "log_remove_progress.json");
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
        NginxLogRotationService nginxLogRotationService)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _hubContext = hubContext;
        _cacheManagementService = cacheManagementService;
        _processManager = processManager;
        _rustProcessHelper = rustProcessHelper;
        _nginxLogRotationService = nginxLogRotationService;
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

            var dataDirectory = _pathResolver.GetDataDirectory();
            var logDir = _pathResolver.GetLogsDirectory();
            var progressPath = Path.Combine(dataDirectory, "log_remove_progress.json");
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

                    // Send completion notification
                    var finalProgress = await ReadProgressFileAsync(progressPath);
                    var message = finalProgress?.Message ?? $"Successfully removed {service} entries from logs";

                    await _hubContext.Clients.All.SendAsync("LogRemovalComplete", new
                    {
                        success = true,
                        message,
                        filesProcessed = finalProgress?.FilesProcessed ?? 0,
                        linesProcessed = finalProgress?.LinesProcessed ?? 0,
                        linesRemoved = finalProgress?.LinesRemoved ?? 0,
                        service
                    });

                    _logger.LogInformation("Log removal completed successfully for {Service}: Removed {LinesRemoved} of {LinesProcessed} lines",
                        service, finalProgress?.LinesRemoved ?? 0, finalProgress?.LinesProcessed ?? 0);
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
        var dataDirectory = _pathResolver.GetDataDirectory();
        var progressPath = Path.Combine(dataDirectory, "log_remove_progress.json");
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
}
