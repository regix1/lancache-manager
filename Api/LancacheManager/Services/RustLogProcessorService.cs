using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using LancacheManager.Hubs;

namespace LancacheManager.Services;

/// <summary>
/// Service that spawns the Rust log processor and monitors its progress
/// </summary>
public class RustLogProcessorService
{
    private readonly ILogger<RustLogProcessorService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly StateService _stateService;
    private Process? _rustProcess;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _progressMonitorTask;

    public bool IsProcessing { get; private set; }

    public RustLogProcessorService(
        ILogger<RustLogProcessorService> logger,
        IPathResolver pathResolver,
        IHubContext<DownloadHub> hubContext,
        StateService stateService)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _hubContext = hubContext;
        _stateService = stateService;
    }

    public class ProgressData
    {
        [System.Text.Json.Serialization.JsonPropertyName("total_lines")]
        public long TotalLines { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("lines_parsed")]
        public long LinesParsed { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("entries_saved")]
        public long EntriesSaved { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("percent_complete")]
        public double PercentComplete { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }
    }

    public async Task<bool> StartProcessingAsync(string logFilePath, long startPosition = 0)
    {
        if (IsProcessing)
        {
            _logger.LogWarning("Rust log processor is already running");
            return false;
        }

        try
        {
            IsProcessing = true;
            _cancellationTokenSource = new CancellationTokenSource();

            var dataDirectory = _pathResolver.GetDataDirectory();
            var dbPath = Path.Combine(dataDirectory, "LancacheManager.db");
            var progressPath = Path.Combine(dataDirectory, "rust_progress.json");
            var rustExecutablePath = Path.Combine(AppContext.BaseDirectory, "rust-processor", "lancache_processor.exe");

            // Delete old progress file
            if (File.Exists(progressPath))
            {
                File.Delete(progressPath);
            }

            _logger.LogInformation("Starting Rust log processor");
            _logger.LogInformation($"Database: {dbPath}");
            _logger.LogInformation($"Log file: {logFilePath}");
            _logger.LogInformation($"Progress file: {progressPath}");
            _logger.LogInformation($"Start position: {startPosition}");

            // Start Rust process
            var startInfo = new ProcessStartInfo
            {
                FileName = rustExecutablePath,
                Arguments = $"\"{dbPath}\" \"{logFilePath}\" \"{progressPath}\" {startPosition}",
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

            // Monitor stdout
            _ = Task.Run(async () =>
            {
                while (!_rustProcess.StandardOutput.EndOfStream)
                {
                    var line = await _rustProcess.StandardOutput.ReadLineAsync();
                    if (!string.IsNullOrEmpty(line))
                    {
                        _logger.LogInformation($"[Rust] {line}");
                    }
                }
            });

            // Monitor stderr
            _ = Task.Run(async () =>
            {
                while (!_rustProcess.StandardError.EndOfStream)
                {
                    var line = await _rustProcess.StandardError.ReadLineAsync();
                    if (!string.IsNullOrEmpty(line))
                    {
                        _logger.LogError($"[Rust Error] {line}");
                    }
                }
            });

            // Start progress monitoring task
            _progressMonitorTask = Task.Run(async () => await MonitorProgressAsync(progressPath, _cancellationTokenSource.Token));

            // Wait for process to complete
            await _rustProcess.WaitForExitAsync(_cancellationTokenSource.Token);

            var exitCode = _rustProcess.ExitCode;
            _logger.LogInformation($"Rust processor exited with code {exitCode}");

            // Stop the progress monitoring task immediately
            _cancellationTokenSource.Cancel();
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

            if (exitCode == 0)
            {
                // Read final progress and send completion with actual data
                var finalProgress = await ReadProgressFileAsync(progressPath);
                if (finalProgress != null)
                {
                    _stateService.SetLogPosition(finalProgress.LinesParsed);

                    // Get log file size for MB calculation
                    var logPath = Path.Combine(_pathResolver.GetLogsDirectory(), "access.log");
                    var logFileInfo = new FileInfo(logPath);
                    var mbTotal = logFileInfo.Exists ? logFileInfo.Length / (1024.0 * 1024.0) : 0;

                    // Send final progress update with 100% and complete status
                    await _hubContext.Clients.All.SendAsync("ProcessingProgress", new
                    {
                        totalLines = finalProgress.TotalLines,
                        linesParsed = finalProgress.LinesParsed,
                        entriesSaved = finalProgress.EntriesSaved,
                        percentComplete = 100.0,
                        status = "complete",
                        message = "Processing complete",
                        mbProcessed = Math.Round(mbTotal, 1),
                        mbTotal = Math.Round(mbTotal, 1),
                        entriesProcessed = finalProgress.EntriesSaved,
                        linesProcessed = finalProgress.LinesParsed,
                        timestamp = DateTime.UtcNow
                    });
                }

                await _hubContext.Clients.All.SendAsync("BulkProcessingComplete", new
                {
                    success = true,
                    message = "Log processing completed successfully",
                    entriesProcessed = finalProgress?.EntriesSaved ?? 0,
                    linesProcessed = finalProgress?.LinesParsed ?? 0,
                    elapsed = 0.0,
                    depotMappingsProcessed = 0,
                    timestamp = DateTime.UtcNow
                });

                return true;
            }
            else
            {
                await _hubContext.Clients.All.SendAsync("ProcessingComplete", new
                {
                    success = false,
                    message = $"Log processing failed with exit code {exitCode}",
                    timestamp = DateTime.UtcNow
                });

                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting Rust log processor");
            await _hubContext.Clients.All.SendAsync("ProcessingComplete", new
            {
                success = false,
                message = $"Log processing failed: {ex.Message}",
                timestamp = DateTime.UtcNow
            });
            return false;
        }
        finally
        {
            IsProcessing = false;
            _cancellationTokenSource?.Dispose();
            _rustProcess?.Dispose();
        }
    }

    private async Task MonitorProgressAsync(string progressPath, CancellationToken cancellationToken)
    {
        try
        {
            // Get log file size once for MB calculations
            var logPath = Path.Combine(_pathResolver.GetLogsDirectory(), "access.log");
            var logFileInfo = new FileInfo(logPath);
            var mbTotal = logFileInfo.Exists ? logFileInfo.Length / (1024.0 * 1024.0) : 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken); // Poll every second

                var progress = await ReadProgressFileAsync(progressPath);
                if (progress != null)
                {
                    // Calculate MB processed based on percentage
                    var mbProcessed = mbTotal * (progress.PercentComplete / 100.0);

                    // Send progress update via SignalR with all fields React expects
                    await _hubContext.Clients.All.SendAsync("ProcessingProgress", new
                    {
                        totalLines = progress.TotalLines,
                        linesParsed = progress.LinesParsed,
                        entriesSaved = progress.EntriesSaved,
                        percentComplete = progress.PercentComplete,
                        status = progress.Status,
                        message = progress.Message,
                        mbProcessed = Math.Round(mbProcessed, 1),
                        mbTotal = Math.Round(mbTotal, 1),
                        entriesProcessed = progress.EntriesSaved,
                        entriesQueued = progress.EntriesSaved,
                        linesProcessed = progress.LinesParsed,
                        timestamp = progress.Timestamp
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
            _logger.LogError(ex, "Error monitoring Rust progress");
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

            var json = await File.ReadAllTextAsync(progressPath);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            return JsonSerializer.Deserialize<ProgressData>(json, options);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to read progress file (may not exist yet)");
            return null;
        }
    }

    public async Task StopProcessingAsync()
    {
        if (!IsProcessing || _rustProcess == null)
        {
            return;
        }

        try
        {
            _logger.LogInformation("Stopping Rust log processor");
            _cancellationTokenSource?.Cancel();

            if (_rustProcess != null && !_rustProcess.HasExited)
            {
                _rustProcess.Kill(true); // Kill process tree
                await _rustProcess.WaitForExitAsync();
            }

            if (_progressMonitorTask != null)
            {
                await _progressMonitorTask;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping Rust log processor");
        }
        finally
        {
            IsProcessing = false;
        }
    }
}