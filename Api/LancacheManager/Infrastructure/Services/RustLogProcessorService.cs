using System.Diagnostics;
using System.Text.Json;
using LancacheManager.Application.Services;
using LancacheManager.Data;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Repositories;
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
    private Process? _rustProcess;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _progressMonitorTask;

    public bool IsProcessing { get; private set; }

    public RustLogProcessorService(
        ILogger<RustLogProcessorService> logger,
        IPathResolver pathResolver,
        IHubContext<DownloadHub> hubContext,
        StateRepository stateService,
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

        [System.Text.Json.Serialization.JsonPropertyName("warnings")]
        public List<string> Warnings { get; set; } = new();

        [System.Text.Json.Serialization.JsonPropertyName("errors")]
        public List<string> Errors { get; set; } = new();
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
            var autoMapDepots = 1;
            var startInfo = new ProcessStartInfo
            {
                FileName = rustExecutablePath,
                Arguments = $"\"{dbPath}\" \"{logDirectory}\" \"{progressPath}\" {startPosition} {autoMapDepots}",
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

            // Monitor stdout - track task for proper cleanup
            var stdoutTask = Task.Run(async () =>
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

            // Monitor stderr - discard output to prevent buffer issues, track task for proper cleanup
            var stderrTask = Task.Run(async () =>
            {
                while (!_rustProcess.StandardError.EndOfStream)
                {
                    await _rustProcess.StandardError.ReadLineAsync();
                }
            });

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

            // Wait for process to complete
            await _rustProcess.WaitForExitAsync(_cancellationTokenSource.Token);

            var exitCode = _rustProcess.ExitCode;
            _logger.LogInformation($"Rust processor exited with code {exitCode}");

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

                // Invalidate cache for new entries (start in background)
                // Rust processor automatically maps depots during processing (auto_map_depots = 1)
                // We still need to fetch game images from Steam API after processing
                if (finalProgress?.EntriesSaved > 0)
                {
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
                    await _hubContext.Clients.All.SendAsync("BulkProcessingComplete", new
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
                // Error is already logged, no need for SignalR notification as frontend uses BulkProcessingComplete
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting Rust log processor");
            // Error is already logged, no need for SignalR notification as frontend uses BulkProcessingComplete
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
        try
        {
            if (!File.Exists(progressPath))
            {
                return null;
            }

            // Use FileStream with FileShare.ReadWrite to allow other processes to access the file
            string json;
            using (var fileStream = new FileStream(progressPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fileStream))
            {
                json = await reader.ReadToEndAsync();
            }

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

                // Invalidate cache again to show the images
                var statsCache = scope.ServiceProvider.GetRequiredService<StatsCache>();
                statsCache.InvalidateDownloads();

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

}