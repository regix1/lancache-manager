using System.Diagnostics;
using System.Text.Json;
using LancacheManager.Application.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Service that spawns the rust database reset service and monitors its progress
/// </summary>
public class RustDatabaseResetService
{
    private readonly ILogger<RustDatabaseResetService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly CacheManagementService _cacheManagementService;
    private readonly ProcessManager _processManager;
    private readonly RustProcessHelper _rustProcessHelper;
    private Process? _rustProcess;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _progressMonitorTask;

    public bool IsProcessing { get; private set; }

    public RustDatabaseResetService(
        ILogger<RustDatabaseResetService> logger,
        IPathResolver pathResolver,
        IHubContext<DownloadHub> hubContext,
        CacheManagementService cacheManagementService,
        ProcessManager processManager,
        RustProcessHelper rustProcessHelper)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _hubContext = hubContext;
        _cacheManagementService = cacheManagementService;
        _processManager = processManager;
        _rustProcessHelper = rustProcessHelper;
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

    /// <summary>
    /// Starts database reset operation (wrapper for StartResetAsync)
    /// </summary>
    public Task<bool> StartDatabaseResetAsync()
    {
        return StartResetAsync();
    }

    /// <summary>
    /// Gets the current database reset status
    /// </summary>
    public object GetDatabaseResetStatus()
    {
        return new
        {
            isProcessing = IsProcessing,
            status = IsProcessing ? "running" : "idle"
        };
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

            // Wrap Rust process execution in shared lock to prevent concurrent database access
            // This prevents database connection issues and SignalR disconnects during reset
            return await _cacheManagementService.ExecuteWithLockAsync(async () =>
            {
                // Start Rust process
                var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                    rustExecutablePath,
                    $"\"{dbPath}\" \"{dataDirectory}\" \"{progressPath}\"",
                    Path.GetDirectoryName(rustExecutablePath));

                _rustProcess = Process.Start(startInfo);

                if (_rustProcess == null)
                {
                    throw new Exception("Failed to start rust database reset process");
                }

                // Monitor stdout and stderr - track tasks for proper cleanup
                var (stdoutTask, stderrTask) = _rustProcessHelper.CreateOutputMonitoringTasks(_rustProcess, "Rust database reset");

                // Start progress monitoring task
                _progressMonitorTask = Task.Run(async () => await MonitorProgressAsync(progressPath, _cancellationTokenSource.Token));

                // Wait for process to complete with graceful cancellation handling
                await _processManager.WaitForProcessAsync(_rustProcess, _cancellationTokenSource.Token);

                var exitCode = _rustProcess.ExitCode;
                _logger.LogInformation($"rust database reset exited with code {exitCode}");

                // Wait for stdout/stderr reading tasks to complete
                await _rustProcessHelper.WaitForOutputTasksAsync(stdoutTask, stderrTask, TimeSpan.FromSeconds(5));

                if (exitCode == 0)
                {
                    // Give Rust process a moment to write final progress file
                    await Task.Delay(200);

                    // Read and send final progress before stopping monitoring
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
                }

                // Stop the progress monitoring task after sending final progress
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
            }, _cancellationTokenSource.Token); // Close ExecuteWithLockAsync lambda
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
        return await _rustProcessHelper.ReadProgressFileAsync<ProgressData>(progressPath);
    }
}
