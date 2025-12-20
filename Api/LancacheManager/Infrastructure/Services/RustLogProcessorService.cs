using System.Diagnostics;
using System.Text.Json;
using LancacheManager.Application.Services;
using LancacheManager.Data;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Infrastructure.Repositories.Interfaces;
using LancacheManager.Infrastructure.Services.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Service that spawns the Rust log processor and monitors its progress
/// </summary>
public class RustLogProcessorService
{
    private readonly ILogger<RustLogProcessorService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly StateRepository _stateService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ProcessManager _processManager;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly DatasourceService _datasourceService;
    private Process? _rustProcess;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _progressMonitorTask;

    public bool IsProcessing { get; private set; }
    public bool IsSilentMode { get; private set; }

    /// <summary>
    /// Resets the log position to 0 to reprocess all logs (all datasources)
    /// </summary>
    public void ResetLogPosition()
    {
        // Reset all datasource positions
        foreach (var ds in _datasourceService.GetDatasources())
        {
            _stateService.SetLogPosition(ds.Name, 0);
        }
        // Also reset legacy position for backward compatibility
        _stateService.SetLogPosition(0);
        _logger.LogInformation("Log position reset to 0 for all datasources");
    }

    /// <summary>
    /// Resets the log position for a specific datasource
    /// </summary>
    public void ResetLogPosition(string datasourceName)
    {
        _stateService.SetLogPosition(datasourceName, 0);
        _logger.LogInformation("Log position reset to 0 for datasource '{DatasourceName}'", datasourceName);
    }

    /// <summary>
    /// Starts log processing for all configured datasources
    /// </summary>
    public async Task<bool> StartProcessing()
    {
        var datasources = _datasourceService.GetDatasources();

        if (datasources.Count == 0)
        {
            _logger.LogWarning("No datasources configured for log processing");
            return false;
        }

        // Process each datasource sequentially
        var allSuccess = true;
        foreach (var datasource in datasources)
        {
            var logPosition = _stateService.GetLogPosition(datasource.Name);
            _logger.LogInformation("Processing datasource '{DatasourceName}' from position {Position}",
                datasource.Name, logPosition);

            var success = await StartProcessingAsync(datasource.LogPath, logPosition, silentMode: false, datasourceName: datasource.Name);
            if (!success)
            {
                allSuccess = false;
                _logger.LogWarning("Processing failed for datasource '{DatasourceName}'", datasource.Name);
            }
        }

        return allSuccess;
    }

    /// <summary>
    /// Starts log processing (wrapper for StartProcessingAsync)
    /// </summary>
    public Task<bool> StartProcessing(string logFilePath, long startPosition = 0, bool silentMode = false, string? datasourceName = null)
    {
        return StartProcessingAsync(logFilePath, startPosition, silentMode, datasourceName);
    }

    /// <summary>
    /// Gets the current processing status including progress data from Rust
    /// </summary>
    public object GetStatus()
    {
        if (!IsProcessing)
        {
            return new
            {
                isProcessing = false,
                silentMode = false,
                status = "idle"
            };
        }

        // Read progress from Rust progress file
        var dataDirectory = _pathResolver.GetDataDirectory();
        var progressPath = Path.Combine(dataDirectory, "rust_progress.json");

        ProgressData? progress = null;
        try
        {
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

        if (progress == null)
        {
            return new
            {
                isProcessing = true,
                silentMode = IsSilentMode,
                status = "starting"
            };
        }

        // Get log file size for MB calculations
        var logPath = Path.Combine(_pathResolver.GetLogsDirectory(), "access.log");
        var logFileInfo = new FileInfo(logPath);
        var mbTotal = logFileInfo.Exists ? logFileInfo.Length / (1024.0 * 1024.0) : 0;
        var mbProcessed = mbTotal * (progress.PercentComplete / 100.0);

        return new
        {
            isProcessing = true,
            silentMode = IsSilentMode,
            status = progress.Status,
            percentComplete = progress.PercentComplete,
            mbProcessed = Math.Round(mbProcessed, 1),
            mbTotal = Math.Round(mbTotal, 1),
            entriesProcessed = progress.EntriesSaved,
            totalLines = progress.TotalLines,
            message = progress.Message
        };
    }

    public RustLogProcessorService(
        ILogger<RustLogProcessorService> logger,
        IPathResolver pathResolver,
        IHubContext<DownloadHub> hubContext,
        StateRepository stateService,
        IServiceProvider serviceProvider,
        ProcessManager processManager,
        RustProcessHelper rustProcessHelper,
        DatasourceService datasourceService)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _hubContext = hubContext;
        _stateService = stateService;
        _serviceProvider = serviceProvider;
        _processManager = processManager;
        _rustProcessHelper = rustProcessHelper;
        _datasourceService = datasourceService;
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

        [System.Text.Json.Serialization.JsonPropertyName("warnings")]
        public List<string> Warnings { get; set; } = new();

        [System.Text.Json.Serialization.JsonPropertyName("errors")]
        public List<string> Errors { get; set; } = new();
    }

    public async Task<bool> StartProcessingAsync(string logFilePath, long startPosition = 0, bool silentMode = false, string? datasourceName = null)
    {
        if (IsProcessing)
        {
            _logger.LogWarning("Rust log processor is already running");
            return false;
        }

        // Use default datasource name if not specified
        datasourceName ??= _datasourceService.GetDefaultDatasource()?.Name ?? "default";

        try
        {
            IsProcessing = true;
            IsSilentMode = silentMode;
            _cancellationTokenSource = new CancellationTokenSource();

            var dataDirectory = _pathResolver.GetDataDirectory();
            var dbPath = Path.Combine(dataDirectory, "LancacheManager.db");
            var progressPath = Path.Combine(dataDirectory, $"rust_progress_{datasourceName}.json");
            var rustExecutablePath = _pathResolver.GetRustLogProcessorPath();

            // Determine if logFilePath is a directory or file path
            // If it's already a directory, use it directly; otherwise extract directory from file path
            var logDirectory = Directory.Exists(logFilePath)
                ? logFilePath  // It's already a directory
                : (Path.GetDirectoryName(logFilePath) ?? _pathResolver.GetLogsDirectory());  // Extract from file path

            // Delete old progress file
            if (File.Exists(progressPath))
            {
                File.Delete(progressPath);
            }

            _logger.LogInformation("Starting Rust log processor");
            _logger.LogInformation($"Database: {dbPath}");
            _logger.LogInformation($"Log directory: {logDirectory}");
            _logger.LogInformation($"Progress file: {progressPath}");
            _logger.LogInformation($"Start position: {startPosition}");

            // Auto-import PICS data if database is sparse but JSON file exists
            // Depot mappings should be set up via initialization flow before log processing
            // Check depot count asynchronously without blocking startup
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var depotCount = await context.SteamDepotMappings.CountAsync();
                    _logger.LogInformation("Starting log processing with {DepotCount} depot mappings available", depotCount);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to check depot count before log processing");
                }
            });

            // Start Rust process
            // Now passing log directory instead of single file path
            // Rust processor will discover all access.log* files (including .1, .2, .gz, .zst)
            // Pass auto_map_depots flag: Always 1 to map depots during processing (avoids showing "Unknown Game" in Active tab)
            // This ensures downloads are properly mapped before appearing in the UI
            // Pass datasource name for multi-datasource support (records will be tagged with this name)
            var autoMapDepots = 1;
            var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                rustExecutablePath,
                $"\"{dbPath}\" \"{logDirectory}\" \"{progressPath}\" {startPosition} {autoMapDepots} \"{datasourceName}\"",
                Path.GetDirectoryName(rustExecutablePath));

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

            // Monitor stdout and stderr - track tasks for proper cleanup
            var (stdoutTask, stderrTask) = _rustProcessHelper.CreateOutputMonitoringTasks(_rustProcess, "Rust log processor");

            // Send initial progress notification to show UI immediately
            if (!silentMode)
            {
                await _hubContext.Clients.All.SendAsync("ProcessingProgress", new
                {
                    totalLines = 0,
                    linesParsed = 0,
                    entriesSaved = 0,
                    percentComplete = 0.0,
                    status = "starting",
                    message = "Starting log processing...",
                    mbProcessed = 0.0,
                    mbTotal = 0.0,
                    entriesProcessed = 0,
                    linesProcessed = 0,
                    timestamp = DateTime.UtcNow
                });

                // Start progress monitoring task
                _progressMonitorTask = Task.Run(async () => await MonitorProgressAsync(progressPath, _cancellationTokenSource.Token));
            }

            // Track start time for minimum display duration
            var startTime = DateTime.UtcNow;

            // Wait for process to complete with graceful cancellation handling
            await _processManager.WaitForProcessAsync(_rustProcess, _cancellationTokenSource.Token);

            var exitCode = _rustProcess.ExitCode;
            _logger.LogInformation($"Rust processor exited with code {exitCode}");

            // Wait for stdout/stderr reading tasks to complete
            await _rustProcessHelper.WaitForOutputTasksAsync(stdoutTask, stderrTask, TimeSpan.FromSeconds(5));

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
                    // Save position AND total lines per-datasource for multi-datasource support
                    // Total lines comes from Rust to avoid C# recounting all log files
                    _stateService.SetLogPosition(datasourceName!, finalProgress.LinesParsed);
                    _stateService.SetLogTotalLines(datasourceName!, finalProgress.TotalLines);

                    // Mark that logs have been processed at least once to enable guest mode
                    _stateService.SetHasProcessedLogs(true);

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

                // Invalidate cache for new entries
                // Rust processor automatically maps depots during processing (auto_map_depots = 1)
                // We still need to fetch game images from Steam API after processing
                if (finalProgress?.EntriesSaved > 0)
                {
                    // Auto-tag new downloads to active events IMMEDIATELY for live monitoring
                    // This must happen BEFORE the UI refresh so downloads show with their event tags
                    if (silentMode)
                    {
                        await AutoTagNewDownloadsAsync();
                    }

                    // These can run in background as they're not critical for the UI refresh
                    _ = Task.Run(async () =>
                    {
                        await InvalidateCacheAsync(silentMode);

                        // Rust mapped the depot IDs to game names during processing, but we still need to fetch images
                        await FetchMissingGameImagesAsync();
                    });
                }

                if (!silentMode)
                {
                    // Set IsProcessing to false BEFORE the delay so polling can detect completion
                    // This is critical for the initialization wizard step 5 to detect completion
                    IsProcessing = false;

                    // Ensure minimum display duration of 2 seconds for UI visibility BEFORE sending completion
                    // This prevents the progress UI from disappearing before users can see it
                    var elapsed = DateTime.UtcNow - startTime;
                    var minDisplayDuration = TimeSpan.FromSeconds(2);
                    _logger.LogInformation("Processing completed in {Elapsed}ms (minimum display duration: {MinDuration}ms)",
                        elapsed.TotalMilliseconds, minDisplayDuration.TotalMilliseconds);

                    if (elapsed < minDisplayDuration)
                    {
                        var remainingDelay = minDisplayDuration - elapsed;
                        _logger.LogInformation("Delaying completion signal by {Delay}ms for UI visibility",
                            remainingDelay.TotalMilliseconds);
                        await Task.Delay(remainingDelay);
                        _logger.LogInformation("Delay complete, sending completion signal now");
                    }
                    else
                    {
                        _logger.LogInformation("No delay needed, processing took longer than minimum duration");
                    }

                    // Calculate final elapsed time after delay
                    var finalElapsed = DateTime.UtcNow - startTime;

                    // Now send completion signal after the delay
                    await _hubContext.Clients.All.SendAsync("FastProcessingComplete", new
                    {
                        success = true,
                        message = "Log processing completed successfully",
                        entriesProcessed = finalProgress?.EntriesSaved ?? 0,
                        linesProcessed = finalProgress?.LinesParsed ?? 0,
                        elapsed = Math.Round(finalElapsed.TotalMinutes, 1),
                        timestamp = DateTime.UtcNow
                    });
                }
                else
                {
                    // In silent mode, we can set IsProcessing to false immediately
                    IsProcessing = false;

                    // Send a lightweight notification that downloads have been updated
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
                // Error is already logged, no need for SignalR notification as frontend uses FastProcessingComplete
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting Rust log processor");
            // Error is already logged, no need for SignalR notification as frontend uses FastProcessingComplete
            return false;
        }
        finally
        {
            IsProcessing = false;
            IsSilentMode = false;
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

            var loggedWarnings = new HashSet<string>();
            var loggedErrors = new HashSet<string>();

            while (!cancellationToken.IsCancellationRequested)
            {
                // Poll for progress updates every 500ms (faster polling for better responsiveness)
                await Task.Delay(500, cancellationToken);

                var progress = await ReadProgressFileAsync(progressPath);
                if (progress != null)
                {
                    // Log any new warnings
                    foreach (var warning in progress.Warnings)
                    {
                        if (loggedWarnings.Add(warning))
                        {
                            _logger.LogWarning("[Rust] {Warning}", warning);
                        }
                    }

                    // Log any new errors
                    foreach (var error in progress.Errors)
                    {
                        if (loggedErrors.Add(error))
                        {
                            _logger.LogError("[Rust] {Error}", error);
                        }
                    }
                }

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
        return await _rustProcessHelper.ReadProgressFileAsync<ProgressData>(progressPath);
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
            _logger.LogError(ex, "Error sending refresh notification after log processing");
        }
    }

    /// <summary>
    /// Fetch missing game images for downloads that have GameAppId but no GameImageUrl
    /// This is called after live log processing where Rust mapped depot IDs to game names
    /// but couldn't fetch images (requires Steam API call)
    /// </summary>
    private async Task FetchMissingGameImagesAsync()
    {
        try
        {

            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var steamService = scope.ServiceProvider.GetRequiredService<SteamService>();

            // Find downloads that have GameAppId but missing image
            var downloadsNeedingImages = await context.Downloads
                .Where(d => d.GameAppId.HasValue && string.IsNullOrEmpty(d.GameImageUrl))
                .Take(50) // Limit to avoid API rate limits
                .ToListAsync();

            if (downloadsNeedingImages.Count == 0)
            {
                return;
            }

            _logger.LogInformation("Fetching images for {Count} downloads", downloadsNeedingImages.Count);

            int updated = 0;
            foreach (var download in downloadsNeedingImages)
            {
                try
                {
                    var gameInfo = await steamService.GetGameInfoAsync(download.GameAppId!.Value);
                    if (gameInfo != null && !string.IsNullOrEmpty(gameInfo.HeaderImage))
                    {
                        download.GameImageUrl = gameInfo.HeaderImage;

                        // Also update game name if it's more accurate from API
                        if (!string.IsNullOrEmpty(gameInfo.Name))
                        {
                            download.GameName = gameInfo.Name;
                        }

                        updated++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch game info for app {AppId}", download.GameAppId);
                }
            }

            if (updated > 0)
            {
                await context.SaveChangesAsync();
                _logger.LogInformation("Updated {Count} downloads with game images", updated);

                await _hubContext.Clients.All.SendAsync("DownloadsRefresh", new
                {
                    timestamp = DateTime.UtcNow
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching missing game images - this is non-critical");
        }
    }

    /// <summary>
    /// Auto-tag newly processed downloads to any currently active events
    /// </summary>
    private async Task AutoTagNewDownloadsAsync()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var eventsRepository = scope.ServiceProvider.GetRequiredService<IEventsRepository>();

            var taggedCount = await eventsRepository.AutoTagDownloadsForActiveEventsAsync();
            if (taggedCount > 0)
            {
                _logger.LogInformation("Auto-tagged {Count} downloads to active events", taggedCount);

                // Notify clients that downloads have been updated with event tags
                await _hubContext.Clients.All.SendAsync("DownloadsRefresh", new
                {
                    timestamp = DateTime.UtcNow
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error auto-tagging downloads to events - this is non-critical");
        }
    }

}