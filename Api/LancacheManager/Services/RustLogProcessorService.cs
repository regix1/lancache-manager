using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using LancacheManager.Data;
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
    private readonly IServiceProvider _serviceProvider;
    private Process? _rustProcess;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _progressMonitorTask;

    public bool IsProcessing { get; private set; }

    public RustLogProcessorService(
        ILogger<RustLogProcessorService> logger,
        IPathResolver pathResolver,
        IHubContext<DownloadHub> hubContext,
        StateService stateService,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _hubContext = hubContext;
        _stateService = stateService;
        _serviceProvider = serviceProvider;
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

    public async Task<bool> StartProcessingAsync(string logFilePath, long startPosition = 0, bool silentMode = false)
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
            var rustExecutablePath = _pathResolver.GetRustLogProcessorPath();

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

            // Auto-import PICS data if database is sparse but JSON file exists
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var depotCount = await context.SteamDepotMappings.CountAsync();

                if (depotCount < 1000) // Threshold: fewer than 1000 mappings means database is sparse
                {
                    var jsonPath = Path.Combine(dataDirectory, "pics_depot_mappings.json");

                    if (File.Exists(jsonPath))
                    {
                        _logger.LogInformation("Database has {DepotCount} depot mappings but PICS JSON exists - auto-importing before log processing...", depotCount);

                        using var importScope = _serviceProvider.CreateScope();
                        var picsDataService = importScope.ServiceProvider.GetRequiredService<PicsDataService>();
                        await picsDataService.ImportJsonDataToDatabaseAsync();

                        var newCount = await context.SteamDepotMappings.CountAsync();
                        _logger.LogInformation("Auto-import complete: {NewCount} depot mappings now available for log processing", newCount);
                    }
                    else
                    {
                        _logger.LogWarning("Database is sparse ({DepotCount} mappings) and no PICS JSON file found - log processing may have incomplete game mappings", depotCount);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-import PICS data before log processing (non-critical, continuing...)");
            }

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

            // Pass TZ environment variable to Rust processor so it uses the correct timezone
            var tz = Environment.GetEnvironmentVariable("TZ");
            if (!string.IsNullOrEmpty(tz))
            {
                startInfo.EnvironmentVariables["TZ"] = tz;
                _logger.LogInformation($"Passing TZ={tz} to Rust processor");
            }

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

            // Start progress monitoring task (skip if silent mode)
            if (!silentMode)
            {
                _progressMonitorTask = Task.Run(async () => await MonitorProgressAsync(progressPath, _cancellationTokenSource.Token));
            }

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

            // Check if this was a cancellation by looking at exit code and progress
            // Exit code 1 typically indicates cancellation or error
            var finalProgress = await ReadProgressFileAsync(progressPath);
            var wasCancelled = finalProgress?.Status == "cancelled" ||
                              (exitCode != 0 && finalProgress?.PercentComplete < 100);

            if (wasCancelled)
            {
                // Process was cancelled - don't send success/completion messages
                _logger.LogInformation("Processing was cancelled (exit code: {ExitCode}, progress: {Progress}%)",
                    exitCode, finalProgress?.PercentComplete ?? 0);
                return false;
            }

            if (exitCode == 0)
            {
                // Normal completion - send completion with actual data
                if (finalProgress != null)
                {
                    _stateService.SetLogPosition(finalProgress.LinesParsed);

                    // Only send SignalR notifications if not in silent mode
                    if (!silentMode)
                    {
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
                }

                // Invalidate cache for new entries (depot mapping now happens in Rust)
                if (finalProgress?.EntriesSaved > 0)
                {
                    _logger.LogDebug("Invalidating cache for {EntriesCount} new entries (depot mapping handled in Rust)", finalProgress.EntriesSaved);
                    _ = Task.Run(async () => await InvalidateCacheAsync(silentMode));
                }

                if (!silentMode)
                {
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
                }
                else
                {
                    // In silent mode, send a lightweight notification that downloads have been updated
                    // This allows the frontend to refresh active downloads without progress bars
                    await _hubContext.Clients.All.SendAsync("DownloadsRefresh", new
                    {
                        entriesProcessed = finalProgress?.EntriesSaved ?? 0,
                        timestamp = DateTime.UtcNow
                    });
                }

                return true;
            }
            else
            {
                // Non-zero exit code but not cancelled - this is an actual error
                if (!silentMode)
                {
                    await _hubContext.Clients.All.SendAsync("ProcessingComplete", new
                    {
                        success = false,
                        message = $"Log processing failed with exit code {exitCode}",
                        timestamp = DateTime.UtcNow
                    });
                }

                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting Rust log processor");

            if (!silentMode)
            {
                await _hubContext.Clients.All.SendAsync("ProcessingComplete", new
                {
                    success = false,
                    message = $"Log processing failed: {ex.Message}",
                    timestamp = DateTime.UtcNow
                });
            }

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

    /// <summary>
    /// Invalidate cache and refresh UI after log processing
    /// </summary>
    private async Task InvalidateCacheAsync(bool silentMode)
    {
        try
        {
            // Wait a moment to ensure all database writes are complete
            await Task.Delay(500);

            using var scope = _serviceProvider.CreateScope();
            var statsCache = scope.ServiceProvider.GetRequiredService<StatsCache>();

            // Invalidate cache to refresh UI with newly imported downloads
            statsCache.InvalidateDownloads();

            // In silent mode, send a refresh notification so the UI updates
            if (silentMode)
            {
                await _hubContext.Clients.All.SendAsync("DownloadsRefresh", new
                {
                    timestamp = DateTime.UtcNow
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invalidating cache after log processing");
        }
    }

}