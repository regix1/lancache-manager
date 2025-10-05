using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using LancacheManager.Hubs;

namespace LancacheManager.Services;

/// <summary>
/// Service that spawns the rust database reset service and monitors its progress
/// </summary>
public class RustDatabaseResetService
{
    private readonly ILogger<RustDatabaseResetService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly IHubContext<DownloadHub> _hubContext;
    private Process? _rustProcess;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _progressMonitorTask;

    public bool IsProcessing { get; private set; }

    public RustDatabaseResetService(
        ILogger<RustDatabaseResetService> logger,
        IPathResolver pathResolver,
        IHubContext<DownloadHub> hubContext)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _hubContext = hubContext;
    }

    public class ProgressData
    {
        [System.Text.Json.Serialization.JsonPropertyName("isProcessing")]
        public bool IsProcessing { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("percentComplete")]
        public double PercentComplete { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("tablesCleared")]
        public int TablesCleared { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("totalTables")]
        public int TotalTables { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("filesDeleted")]
        public int FilesDeleted { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }
    }

    public async Task<bool> StartResetAsync()
    {
        if (IsProcessing)
        {
            _logger.LogWarning("rust database reset is already running");
            return false;
        }

        try
        {
            IsProcessing = true;
            _cancellationTokenSource = new CancellationTokenSource();

            var dataDirectory = _pathResolver.GetDataDirectory();
            var dbPath = Path.Combine(dataDirectory, "LancacheManager.db");
            var progressPath = Path.Combine(dataDirectory, "reset_progress.json");
            var rustExecutablePath = _pathResolver.GetRustDatabaseResetPath();

            // Delete old progress file
            if (File.Exists(progressPath))
            {
                File.Delete(progressPath);
            }

            _logger.LogInformation("Starting rust database reset");
            _logger.LogInformation($"Database: {dbPath}");
            _logger.LogInformation($"Data directory: {dataDirectory}");
            _logger.LogInformation($"Progress file: {progressPath}");

            // Send initial progress
            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
            {
                isProcessing = true,
                percentComplete = 0.0,
                status = "starting",
                message = "Starting database reset...",
                tablesCleared = 0,
                totalTables = 4,
                filesDeleted = 0,
                timestamp = DateTime.UtcNow
            });

            // Start Rust process
            var startInfo = new ProcessStartInfo
            {
                FileName = rustExecutablePath,
                Arguments = $"\"{dbPath}\" \"{dataDirectory}\" \"{progressPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(rustExecutablePath)
            };

            _rustProcess = Process.Start(startInfo);

            if (_rustProcess == null)
            {
                throw new Exception("Failed to start rust database reset process");
            }

            // Monitor stdout
            _ = Task.Run(async () =>
            {
                while (!_rustProcess.StandardOutput.EndOfStream)
                {
                    var line = await _rustProcess.StandardOutput.ReadLineAsync();
                    if (!string.IsNullOrEmpty(line))
                    {
                        _logger.LogInformation($"[rust reset] {line}");
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
                        _logger.LogError($"[rust reset error] {line}");
                    }
                }
            });

            // Start progress monitoring task
            _progressMonitorTask = Task.Run(async () => await MonitorProgressAsync(progressPath, _cancellationTokenSource.Token));

            // Wait for process to complete
            await _rustProcess.WaitForExitAsync(_cancellationTokenSource.Token);

            var exitCode = _rustProcess.ExitCode;
            _logger.LogInformation($"rust database reset exited with code {exitCode}");

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
                // Read final progress and send completion
                var finalProgress = await ReadProgressFileAsync(progressPath);
                if (finalProgress != null)
                {
                    await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", finalProgress);
                }
                else
                {
                    // Fallback completion message
                    await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
                    {
                        isProcessing = false,
                        percentComplete = 100.0,
                        status = "complete",
                        message = "Database reset completed successfully",
                        timestamp = DateTime.UtcNow
                    });
                }

                return true;
            }
            else
            {
                await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
                {
                    isProcessing = false,
                    percentComplete = 0.0,
                    status = "error",
                    message = $"Database reset failed with exit code {exitCode}",
                    timestamp = DateTime.UtcNow
                });

                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting rust database reset");
            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
            {
                isProcessing = false,
                percentComplete = 0.0,
                status = "error",
                message = $"Database reset failed: {ex.Message}",
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
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(500, cancellationToken); // Poll every 500ms for faster updates

                var progress = await ReadProgressFileAsync(progressPath);
                if (progress != null)
                {
                    // Send progress update via SignalR
                    await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", progress, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error monitoring rust database reset progress");
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
}
