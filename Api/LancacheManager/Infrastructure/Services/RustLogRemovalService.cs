using System.Diagnostics;
using LancacheManager.Application.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.Interfaces;
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
    private Process? _rustProcess;
    private CancellationTokenSource? _cancellationTokenSource;

    public bool IsProcessing { get; private set; }
    public string? CurrentService { get; private set; }

    public RustLogRemovalService(
        ILogger<RustLogRemovalService> logger,
        IPathResolver pathResolver,
        IHubContext<DownloadHub> hubContext,
        CacheManagementService cacheManagementService)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _hubContext = hubContext;
        _cacheManagementService = cacheManagementService;
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

                var startInfo = new ProcessStartInfo
                {
                    FileName = rustExecutablePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(rustExecutablePath)
                };

                _rustProcess = Process.Start(startInfo);

                if (_rustProcess == null)
                {
                    throw new Exception("Failed to start Rust process");
                }

                // Monitor stdout - track task for proper cleanup
                var stdoutTask = Task.Run(async () =>
                {
                    while (!_rustProcess.StandardOutput.EndOfStream)
                    {
                        var line = await _rustProcess.StandardOutput.ReadLineAsync();
                        if (!string.IsNullOrEmpty(line))
                        {
                            _logger.LogInformation("[Rust] {Line}", line);
                        }
                    }
                });

                // Monitor stderr - log errors/warnings for debugging
                var stderrTask = Task.Run(async () =>
                {
                    while (!_rustProcess.StandardError.EndOfStream)
                    {
                        var line = await _rustProcess.StandardError.ReadLineAsync();
                        if (!string.IsNullOrEmpty(line))
                        {
                            _logger.LogWarning("[Rust stderr] {Line}", line);
                        }
                    }
                });

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

                // Wait for process to complete
                await _rustProcess.WaitForExitAsync(_cancellationTokenSource.Token);

                var exitCode = _rustProcess.ExitCode;
                _logger.LogInformation("Rust log_manager exited with code {ExitCode}", exitCode);

                // Wait for stdout/stderr reading tasks to complete
                try
                {
                    await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("Timeout waiting for stdout/stderr tasks to complete");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error waiting for stdout/stderr tasks");
                }

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
                    // Small delay to ensure file operations are fully complete
                    // (Windows file system may need a moment to finalize writes)
                    await Task.Delay(100);

                    // Invalidate service counts cache so UI refreshes
                    await _cacheManagementService.InvalidateServiceCountsCache();

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
            _cancellationTokenSource?.Dispose();
            _rustProcess?.Dispose();
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
        try
        {
            if (!File.Exists(progressPath))
            {
                return null;
            }

            string json;
            using (var fileStream = new FileStream(progressPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fileStream))
            {
                json = await reader.ReadToEndAsync();
            }

            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            return System.Text.Json.JsonSerializer.Deserialize<ProgressData>(json, options);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to read progress file (may not exist yet)");
            return null;
        }
    }

    public async Task<ProgressData?> GetProgressAsync()
    {
        var dataDirectory = _pathResolver.GetDataDirectory();
        var progressPath = Path.Combine(dataDirectory, "log_remove_progress.json");
        return await ReadProgressFileAsync(progressPath);
    }
}
