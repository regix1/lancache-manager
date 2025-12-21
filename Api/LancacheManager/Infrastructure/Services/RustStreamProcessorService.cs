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
/// Service that spawns the Rust stream processor and monitors its progress.
/// Processes stream-access.log files for bandwidth/speed data.
/// </summary>
public class RustStreamProcessorService
{
    private readonly ILogger<RustStreamProcessorService> _logger;
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

    public RustStreamProcessorService(
        ILogger<RustStreamProcessorService> logger,
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
    }

    /// <summary>
    /// Resets the stream log position to 0 to reprocess all stream logs (all datasources)
    /// </summary>
    public void ResetStreamLogPosition()
    {
        foreach (var ds in _datasourceService.GetDatasources())
        {
            _stateService.SetStreamLogPosition(ds.Name, 0);
        }
        _stateService.SetStreamLogPosition(0);
        _logger.LogInformation("Stream log position reset to 0 for all datasources");
    }

    /// <summary>
    /// Resets the stream log position for a specific datasource
    /// </summary>
    public void ResetStreamLogPosition(string datasourceName)
    {
        _stateService.SetStreamLogPosition(datasourceName, 0);
        _logger.LogInformation("Stream log position reset to 0 for datasource '{DatasourceName}'", datasourceName);
    }

    /// <summary>
    /// Starts stream log processing for all configured datasources
    /// </summary>
    public async Task<bool> StartProcessing()
    {
        var datasources = _datasourceService.GetDatasources();

        if (datasources.Count == 0)
        {
            _logger.LogWarning("No datasources configured for stream log processing");
            return false;
        }

        var allSuccess = true;
        foreach (var datasource in datasources)
        {
            var logPosition = _stateService.GetStreamLogPosition(datasource.Name);
            _logger.LogInformation("Processing stream logs for datasource '{DatasourceName}' from position {Position}",
                datasource.Name, logPosition);

            var success = await StartProcessingAsync(datasource.LogPath, logPosition, datasourceName: datasource.Name);
            if (!success)
            {
                allSuccess = false;
                _logger.LogWarning("Stream processing failed for datasource '{DatasourceName}'", datasource.Name);
            }
        }

        return allSuccess;
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
                status = "idle"
            };
        }

        var dataDirectory = _pathResolver.GetDataDirectory();
        var progressPath = Path.Combine(dataDirectory, "stream_progress.json");

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
            // Ignore read errors
        }

        if (progress == null)
        {
            return new
            {
                isProcessing = true,
                status = "starting"
            };
        }

        return new
        {
            isProcessing = true,
            status = progress.Status,
            percentComplete = progress.PercentComplete,
            entriesProcessed = progress.EntriesSaved,
            totalLines = progress.TotalLines,
            message = progress.Message
        };
    }

    public async Task<bool> StartProcessingAsync(string logFilePath, long startPosition = 0, bool silentMode = false, string? datasourceName = null)
    {
        if (IsProcessing)
        {
            if (!silentMode)
            {
                _logger.LogWarning("Stream processor is already running");
            }
            return false;
        }

        datasourceName ??= _datasourceService.GetDefaultDatasource()?.Name ?? "default";

        try
        {
            IsProcessing = true;
            _cancellationTokenSource = new CancellationTokenSource();

            var dataDirectory = _pathResolver.GetDataDirectory();
            var dbPath = Path.Combine(dataDirectory, "LancacheManager.db");
            var progressPath = Path.Combine(dataDirectory, $"stream_progress_{datasourceName}.json");
            var rustExecutablePath = _pathResolver.GetRustStreamProcessorPath();

            var logDirectory = Directory.Exists(logFilePath)
                ? logFilePath
                : (Path.GetDirectoryName(logFilePath) ?? _pathResolver.GetLogsDirectory());

            if (File.Exists(progressPath))
            {
                File.Delete(progressPath);
            }

            if (!silentMode)
            {
                _logger.LogInformation("Starting Rust stream processor");
                _logger.LogInformation($"Database: {dbPath}");
                _logger.LogInformation($"Log directory: {logDirectory}");
                _logger.LogInformation($"Progress file: {progressPath}");
                _logger.LogInformation($"Start position: {startPosition}");
            }

            var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                rustExecutablePath,
                $"\"{dbPath}\" \"{logDirectory}\" \"{progressPath}\" {startPosition} \"{datasourceName}\"",
                Path.GetDirectoryName(rustExecutablePath));

            var tz = Environment.GetEnvironmentVariable("TZ");
            if (!string.IsNullOrEmpty(tz))
            {
                startInfo.EnvironmentVariables["TZ"] = tz;
                _logger.LogInformation($"Passing TZ={tz} to Rust stream processor");
            }

            _rustProcess = Process.Start(startInfo);

            if (_rustProcess == null)
            {
                throw new Exception("Failed to start Rust stream process");
            }

            var (stdoutTask, stderrTask) = _rustProcessHelper.CreateOutputMonitoringTasks(_rustProcess, "Rust stream processor");

            if (!silentMode)
            {
                await _hubContext.Clients.All.SendAsync("StreamProcessingProgress", new
                {
                    totalLines = 0,
                    linesParsed = 0,
                    entriesSaved = 0,
                    percentComplete = 0.0,
                    status = "starting",
                    message = "Starting stream log processing...",
                    timestamp = DateTime.UtcNow
                });

                _progressMonitorTask = Task.Run(async () => await MonitorProgressAsync(progressPath, _cancellationTokenSource.Token));
            }

            await _processManager.WaitForProcessAsync(_rustProcess, _cancellationTokenSource.Token);

            var exitCode = _rustProcess.ExitCode;
            if (!silentMode)
            {
                _logger.LogInformation($"Stream processor exited with code {exitCode}");
            }

            await _rustProcessHelper.WaitForOutputTasksAsync(stdoutTask, stderrTask, TimeSpan.FromSeconds(5));

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

            var finalProgress = await ReadProgressFileAsync(progressPath);

            if (exitCode == 0)
            {
                if (finalProgress != null)
                {
                    _stateService.SetStreamLogPosition(datasourceName!, finalProgress.LinesParsed);

                    if (!silentMode)
                    {
                        await _hubContext.Clients.All.SendAsync("StreamProcessingProgress", new
                        {
                            totalLines = finalProgress.TotalLines,
                            linesParsed = finalProgress.LinesParsed,
                            entriesSaved = finalProgress.EntriesSaved,
                            percentComplete = 100.0,
                            status = "complete",
                            message = "Stream processing complete",
                            timestamp = DateTime.UtcNow
                        });
                    }
                }

                // Correlate stream sessions with downloads after processing
                if (finalProgress?.EntriesSaved > 0)
                {
                    _ = Task.Run(async () => await CorrelateStreamSessionsAsync());
                }

                if (!silentMode)
                {
                    await _hubContext.Clients.All.SendAsync("StreamProcessingComplete", new
                    {
                        success = true,
                        message = "Stream log processing completed successfully",
                        entriesProcessed = finalProgress?.EntriesSaved ?? 0,
                        timestamp = DateTime.UtcNow
                    });
                }

                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting Rust stream processor");
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
                await Task.Delay(500, cancellationToken);

                var progress = await ReadProgressFileAsync(progressPath);

                if (progress != null)
                {
                    await _hubContext.Clients.All.SendAsync("StreamProcessingProgress", new
                    {
                        totalLines = progress.TotalLines,
                        linesParsed = progress.LinesParsed,
                        entriesSaved = progress.EntriesSaved,
                        percentComplete = progress.PercentComplete,
                        status = progress.Status,
                        message = progress.Message,
                        timestamp = progress.Timestamp
                    }, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error monitoring stream progress");
        }
    }

    private async Task<ProgressData?> ReadProgressFileAsync(string progressPath)
    {
        return await _rustProcessHelper.ReadProgressFileAsync<ProgressData>(progressPath);
    }

    /// <summary>
    /// Correlate stream sessions with downloads based on IP, time window, and service
    /// </summary>
    private async Task CorrelateStreamSessionsAsync()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Find uncorrelated stream sessions
            var uncorrelatedSessions = await context.StreamSessions
                .Where(s => s.DownloadId == null)
                .OrderBy(s => s.SessionEndUtc)
                .Take(1000)
                .ToListAsync();

            if (uncorrelatedSessions.Count == 0)
            {
                return;
            }

            _logger.LogInformation("Correlating {Count} stream sessions with downloads", uncorrelatedSessions.Count);

            int correlated = 0;
            foreach (var session in uncorrelatedSessions)
            {
                // Find a download that matches:
                // - Same client IP
                // - Download time window overlaps with stream session
                // - Same datasource
                var matchingDownload = await context.Downloads
                    .Where(d =>
                        d.ClientIp == session.ClientIp &&
                        d.Datasource == session.Datasource &&
                        d.StartTimeUtc <= session.SessionEndUtc &&
                        d.EndTimeUtc >= session.SessionStartUtc)
                    .OrderByDescending(d => d.StartTimeUtc)
                    .FirstOrDefaultAsync();

                if (matchingDownload != null)
                {
                    session.DownloadId = matchingDownload.Id;
                    correlated++;
                }
            }

            if (correlated > 0)
            {
                await context.SaveChangesAsync();
                _logger.LogInformation("Correlated {Count} stream sessions with downloads", correlated);

                // Update download speed aggregates
                await UpdateDownloadSpeedAggregatesAsync(context);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error correlating stream sessions - this is non-critical");
        }
    }

    /// <summary>
    /// Update speed aggregates on downloads from their correlated stream sessions
    /// </summary>
    private async Task UpdateDownloadSpeedAggregatesAsync(AppDbContext context)
    {
        try
        {
            // Get downloads that have stream sessions but no speed data
            var downloadsToUpdate = await context.Downloads
                .Where(d => d.DownloadSpeedBps == null)
                .Where(d => context.StreamSessions.Any(s => s.DownloadId == d.Id))
                .Take(500)
                .ToListAsync();

            foreach (var download in downloadsToUpdate)
            {
                var sessions = await context.StreamSessions
                    .Where(s => s.DownloadId == download.Id)
                    .ToListAsync();

                if (sessions.Count > 0)
                {
                    var totalBytesSent = sessions.Sum(s => s.BytesSent);
                    var totalBytesReceived = sessions.Sum(s => s.BytesReceived);
                    var totalDuration = sessions.Sum(s => s.DurationSeconds);

                    download.DownloadSpeedBps = totalDuration > 0 ? totalBytesSent / totalDuration : null;
                    download.UploadSpeedBps = totalDuration > 0 ? totalBytesReceived / totalDuration : null;
                    download.SessionDurationSeconds = totalDuration;
                    download.StreamSessionCount = sessions.Count;
                }
            }

            await context.SaveChangesAsync();
            _logger.LogInformation("Updated speed aggregates for {Count} downloads", downloadsToUpdate.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error updating download speed aggregates - this is non-critical");
        }
    }
}
