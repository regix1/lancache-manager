using System.Diagnostics;
using System.Text.Json;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Hubs;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Models;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Service that spawns the Rust log processor and monitors its progress
/// </summary>
public class RustLogProcessorService
{
    private readonly ILogger<RustLogProcessorService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly ISignalRNotificationService _notifications;
    private readonly StateService _stateService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ProcessManager _processManager;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly DatasourceService _datasourceService;
    private readonly IUnifiedOperationTracker _operationTracker;
    private Process? _rustProcess;
    private CancellationTokenSource? _cancellationTokenSource;
    private string? _currentOperationId;
    private Task? _progressMonitorTask;
    private readonly SemaphoreSlim _startLock = new(1, 1);

    public bool IsProcessing { get; private set; }
    public bool IsSilentMode { get; private set; }
    public bool IsCancelling { get; private set; }

    /// <summary>
    /// Cancels any ongoing log processing operation
    /// </summary>
    /// <returns>True if cancellation was requested, false if no operation was running</returns>
    public bool CancelProcessing()
    {
        if (!IsProcessing || _cancellationTokenSource == null)
        {
            _logger.LogWarning("No log processing operation to cancel");
            return false;
        }

        // If cancellation is already in progress, return true (idempotent)
        // This prevents 404 errors when user clicks cancel button multiple times
        if (IsCancelling)
        {
            _logger.LogDebug("Cancellation already in progress for log processing");
            return true;
        }

        IsCancelling = true;
        _logger.LogInformation("Cancellation requested for log processing");
        
        try
        {
            _cancellationTokenSource.Cancel();
            
            // Kill the Rust process if it's running
            if (_rustProcess != null && !_rustProcess.HasExited)
            {
                _logger.LogInformation("Killing Rust log processor process");
                _rustProcess.Kill(entireProcessTree: true);
            }
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during log processing cancellation");
            return false;
        }
    }

    /// <summary>
    /// Force kills the log processing operation
    /// </summary>
    public async Task<bool> ForceKillProcessingAsync()
    {
        if (!IsProcessing)
        {
            _logger.LogWarning("No log processing operation to kill");
            return false;
        }

        _logger.LogWarning("Force killing log processing operation");
        
        try
        {
            // Force cancel the token
            _cancellationTokenSource?.Cancel();
            
            // Kill the process tree
            if (_rustProcess != null && !_rustProcess.HasExited)
            {
                _rustProcess.Kill(entireProcessTree: true);
                await _rustProcess.WaitForExitAsync();
            }
            
            IsProcessing = false;
            IsCancelling = false;

            // Send cancellation notification
            await _notifications.NotifyAllAsync(SignalREvents.LogProcessingComplete, new
            {
                OperationId = _currentOperationId,
                Success = false,
                Status = OperationStatus.Cancelled,
                Message = "Log processing was cancelled",
                Cancelled = true
            });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during force kill of log processing");
            return false;
        }
    }

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
        var operationsDir = _pathResolver.GetOperationsDirectory();
        var defaultDatasourceName = _datasourceService.GetDefaultDatasource()?.Name ?? "default";
        var progressPath = Path.Combine(operationsDir, $"rust_progress_{defaultDatasourceName}.json");
        var legacyProgressPath = Path.Combine(operationsDir, "rust_progress.json");

        ProgressData? progress = null;
        try
        {
            if (!File.Exists(progressPath) && File.Exists(legacyProgressPath))
            {
                progressPath = legacyProgressPath;
            }

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
        ISignalRNotificationService notifications,
        StateService stateService,
        IServiceProvider serviceProvider,
        ProcessManager processManager,
        RustProcessHelper rustProcessHelper,
        DatasourceService datasourceService,
        IUnifiedOperationTracker operationTracker)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _notifications = notifications;
        _stateService = stateService;
        _serviceProvider = serviceProvider;
        _processManager = processManager;
        _rustProcessHelper = rustProcessHelper;
        _datasourceService = datasourceService;
        _operationTracker = operationTracker;
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
        await _startLock.WaitAsync();
        try
        {
            if (IsProcessing)
            {
                _logger.LogWarning("Rust log processor is already running");
                return false;
            }

            IsProcessing = true;
            IsSilentMode = silentMode;
        }
        finally
        {
            _startLock.Release();
        }

        // Use default datasource name if not specified
        datasourceName ??= _datasourceService.GetDefaultDatasource()?.Name ?? "default";

        try
        {
            _cancellationTokenSource = new CancellationTokenSource();
            
            // Register the operation with the unified tracker
            _currentOperationId = _operationTracker.RegisterOperation(
                OperationType.LogProcessing,
                "Log Processing",
                _cancellationTokenSource
            );

            var dbPath = _pathResolver.GetDatabasePath();
            var operationsDir = _pathResolver.GetOperationsDirectory();
            var progressPath = Path.Combine(operationsDir, $"rust_progress_{datasourceName}.json");
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

            // Send started event
            if (!silentMode)
            {
                await _notifications.NotifyAllAsync(SignalREvents.LogProcessingStarted, new
                {
                    OperationId = _currentOperationId,
                    Message = "Starting log processing..."
                });
            }

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
                await _notifications.NotifyAllAsync(SignalREvents.LogProcessingProgress, new
                {
                    OperationId = _currentOperationId,
                    PercentComplete = 0.0,
                    Status = OperationStatus.Running,
                    Message = "Starting log processing...",
                    TotalLines = 0,
                    LinesParsed = 0,
                    EntriesSaved = 0,
                    MbProcessed = 0.0,
                    MbTotal = 0.0
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
                
                // Complete the operation with cancellation status
                if (_currentOperationId != null)
                {
                    _operationTracker.CompleteOperation(_currentOperationId, false, "Operation was cancelled");
                }
                
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
                        await _notifications.NotifyAllAsync(SignalREvents.LogProcessingProgress, new
                        {
                            OperationId = _currentOperationId,
                            PercentComplete = 100.0,
                            Status = OperationStatus.Completed,
                            Message = "Processing complete",
                            TotalLines = finalProgress.TotalLines,
                            LinesParsed = finalProgress.LinesParsed,
                            EntriesSaved = finalProgress.EntriesSaved,
                            MbProcessed = Math.Round(mbTotal, 1),
                            MbTotal = Math.Round(mbTotal, 1)
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
                    // Run for BOTH silent and interactive mode to prevent duplicate grouping issues
                    await AutoTagNewDownloadsAsync();

                    // NOTE: We no longer broadcast NewDownloads directly - the frontend relies on
                    // DownloadsRefresh event which triggers a database fetch. This ensures all data
                    // (downloads, stats, aggregates) comes from the same source and stays in sync.

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
                    await _notifications.NotifyAllAsync(SignalREvents.LogProcessingComplete, new
                    {
                        OperationId = _currentOperationId,
                        Success = true,
                        Status = OperationStatus.Completed,
                        Message = "Log processing completed successfully",
                        Cancelled = false,
                        EntriesProcessed = finalProgress?.EntriesSaved ?? 0,
                        LinesProcessed = finalProgress?.LinesParsed ?? 0,
                        Elapsed = Math.Round(finalElapsed.TotalMinutes, 1)
                    });
                }
                else
                {
                    // In silent mode, we can set IsProcessing to false immediately
                    IsProcessing = false;

                    // Send a lightweight notification that downloads have been updated
                    // This allows the frontend to refresh active downloads without progress bars
                    await _notifications.NotifyAllAsync(SignalREvents.DownloadsRefresh, new
                    {
                        entriesProcessed = finalProgress?.EntriesSaved ?? 0,
                        timestamp = DateTime.UtcNow
                    });
                }

                // Complete the operation successfully
                if (_currentOperationId != null)
                {
                    _operationTracker.CompleteOperation(_currentOperationId, true);
                }

                return true;
            }
            else
            {
                // Non-zero exit code but not cancelled - this is an actual error
                _logger.LogError("Rust processor failed with exit code {ExitCode}", exitCode);

                if (!silentMode)
                {
                    // Send failure notification so frontend doesn't get stuck
                    await _notifications.NotifyAllAsync(SignalREvents.LogProcessingComplete, new
                    {
                        OperationId = _currentOperationId,
                        Success = false,
                        Status = OperationStatus.Failed,
                        Message = $"Log processing failed with exit code {exitCode}",
                        Cancelled = false,
                        EntriesProcessed = 0,
                        LinesProcessed = 0
                    });
                }

                // Complete the operation with error
                if (_currentOperationId != null)
                {
                    _operationTracker.CompleteOperation(_currentOperationId, false, $"Rust processor failed with exit code {exitCode}");
                }

                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting Rust log processor");

            if (!silentMode)
            {
                // Send failure notification so frontend doesn't get stuck
                await _notifications.NotifyAllAsync(SignalREvents.LogProcessingComplete, new
                {
                    OperationId = _currentOperationId,
                    Success = false,
                    Status = OperationStatus.Failed,
                    Message = $"Log processing error: {ex.Message}",
                    Cancelled = false,
                    EntriesProcessed = 0,
                    LinesProcessed = 0
                });
            }

            // Complete the operation with error
            if (_currentOperationId != null)
            {
                _operationTracker.CompleteOperation(_currentOperationId, false, ex.Message);
            }

            return false;
        }
        finally
        {
            IsProcessing = false;
            IsSilentMode = false;
            IsCancelling = false;
            _currentOperationId = null;
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

                    // Update the unified operation tracker with progress
                    if (_currentOperationId != null)
                    {
                        _operationTracker.UpdateProgress(_currentOperationId, progress.PercentComplete, progress.Message);
                    }

                    // Send progress update via SignalR with standardized format
                    await _notifications.NotifyAllAsync(SignalREvents.LogProcessingProgress, new
                    {
                        OperationId = _currentOperationId,
                        PercentComplete = progress.PercentComplete,
                        Status = OperationStatus.Running,
                        Message = progress.Message,
                        TotalLines = progress.TotalLines,
                        LinesParsed = progress.LinesParsed,
                        EntriesSaved = progress.EntriesSaved,
                        MbProcessed = Math.Round(mbProcessed, 1),
                        MbTotal = Math.Round(mbTotal, 1)
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
            _logger.LogError(ex, "Error monitoring Rust progress");
        }
    }

    private async Task<ProgressData?> ReadProgressFileAsync(string progressPath)
    {
        return await _rustProcessHelper.ReadProgressFileAsync<ProgressData>(progressPath);
    }

    /// <summary>
    /// Invalidate cache after log processing
    /// NOTE: This method no longer sends DownloadsRefresh events to avoid duplicates.
    /// The main completion handler (StartProcessingAsync) sends the single DownloadsRefresh event.
    /// </summary>
    private async Task InvalidateCacheAsync(bool silentMode)
    {
        try
        {
            // Wait a moment to ensure all database writes are complete
            await Task.Delay(500);

            // NOTE: We no longer send DownloadsRefresh here to avoid duplicate events.
            // The main completion handler already sends DownloadsRefresh for silent mode (line 604)
            // or LogProcessingComplete for non-silent mode which triggers UI refresh.
            _logger.LogDebug("Cache invalidation complete (silentMode={SilentMode})", silentMode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during cache invalidation after log processing");
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

                // NOTE: We no longer send DownloadsRefresh here to avoid duplicate events.
                // The main completion handler already sends DownloadsRefresh for silent mode
                // or LogProcessingComplete for non-silent mode which triggers UI refresh.
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
            var eventsService = scope.ServiceProvider.GetRequiredService<IEventsService>();

            var taggedCount = await eventsService.AutoTagDownloadsForActiveEventsAsync();
            if (taggedCount > 0)
            {
                _logger.LogInformation("Auto-tagged {Count} downloads to active events", taggedCount);

                // NOTE: We no longer send DownloadsRefresh here to avoid duplicate events.
                // The main completion handler already sends DownloadsRefresh for silent mode
                // or LogProcessingComplete for non-silent mode which triggers UI refresh.
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error auto-tagging downloads to events - this is non-critical");
        }
    }

}
