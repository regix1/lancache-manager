using LancacheManager.Models;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using LancacheManager.Hubs;
using LancacheManager.Services;

namespace LancacheManager.Services;

public class LogWatcherService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly LogProcessingService _processingService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LogWatcherService> _logger;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly IPathResolver _pathResolver;
    private readonly StateService _stateService;
    private long _lastPosition = -1;
    private readonly string _processingMarker;
    private readonly string _logPath;
    private bool _isBulkProcessing = false;
    private DateTime _lastMarkerCheck = DateTime.MinValue;
    private FileSystemWatcher? _markerWatcher;
    private volatile bool _restartProcessing = false;
    private DateTime _bulkProcessingStartTime = default; // Track when bulk processing started
    private volatile bool _processingComplete = false;

    public LogWatcherService(
        IServiceProvider serviceProvider,
        LogProcessingService processingService,
        IConfiguration configuration,
        ILogger<LogWatcherService> logger,
        IHubContext<DownloadHub> hubContext,
        IPathResolver pathResolver,
        StateService stateService)
    {
        _serviceProvider = serviceProvider;
        _processingService = processingService;
        _configuration = configuration;
        _logger = logger;
        _hubContext = hubContext;
        _pathResolver = pathResolver;
        _stateService = stateService;

        var dataDirectory = _pathResolver.GetDataDirectory();
        _processingMarker = Path.Combine(dataDirectory, "processing.marker");
        _logPath = configuration["LanCache:LogPath"] ?? Path.Combine(_pathResolver.GetLogsDirectory(), "access.log");

        _logger.LogInformation($"LogWatcherService using log path: {_logPath}");
        _logger.LogInformation($"Processing marker: {_processingMarker}");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // This service is only started manually via the API endpoint
        _logger.LogInformation("LogWatcherService started for manual log processing");


        SetupMarkerWatcher();

        _processingComplete = false;
        _isBulkProcessing = true;
        _processingService.SetBulkProcessingMode(true);

        // Wait for log file to exist
        int waitAttempts = 0;
        while (!File.Exists(_logPath) && !stoppingToken.IsCancellationRequested)
        {
            waitAttempts++;
            _logger.LogInformation($"Waiting for log file: {_logPath} (attempt {waitAttempts})");

            if (File.Exists(_processingMarker))
            {
                _logger.LogInformation("Processing marker found while waiting for log file");
            }

            var waitTime = Math.Min(10000 * waitAttempts, 60000);
            await Task.Delay(waitTime, stoppingToken);

            if (waitAttempts == 10)
            {
                _logger.LogWarning($"Log file still not found after {waitAttempts} attempts");
                var logDir = Path.GetDirectoryName(_logPath);
                if (Directory.Exists(logDir))
                {
                    var files = Directory.GetFiles(logDir, "*.log");
                    _logger.LogInformation($"Found {files.Length} .log files in {logDir}: {string.Join(", ", files.Take(5))}");
                }
            }
        }

        if (stoppingToken.IsCancellationRequested)
            return;

        await InitializePosition();
        await ProcessLogFile(stoppingToken);
    }

    private void SetupMarkerWatcher()
    {
        try
        {
            var directory = Path.GetDirectoryName(_processingMarker) ?? _pathResolver.GetDataDirectory();
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _markerWatcher = new FileSystemWatcher(directory)
            {
                Filter = Path.GetFileName(_processingMarker),
                NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };

            _markerWatcher.Created += OnMarkerCreated;
            _markerWatcher.Changed += OnMarkerCreated;
            
            _logger.LogInformation("Marker file watcher initialized");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to setup marker watcher");
        }
    }

    private void OnMarkerCreated(object sender, FileSystemEventArgs e)
    {
        if (_isBulkProcessing)
        {
            _logger.LogDebug("Already bulk processing, ignoring duplicate marker event");
            return;
        }
        
        _logger.LogInformation("Processing marker detected via file watcher - FORCING bulk processing from position 0");
        _lastPosition = 0;
        _isBulkProcessing = true;
        _processingService.SetBulkProcessingMode(true);
        _restartProcessing = true;
        _lastMarkerCheck = DateTime.UtcNow;
        
        // Force save position to 0 immediately
        Task.Run(() => {
            _stateService.SetLogPosition(0);
            _logger.LogInformation("Forced position to 0 for bulk processing");
        }).Wait();
    }

    private async Task InitializePosition()
    {
        var fileInfo = new FileInfo(_logPath);
        _logger.LogInformation($"Log file size: {fileInfo.Length / 1024.0 / 1024.0:F1} MB");

        // Check for bulk processing marker
        bool hasBulkProcessingMarker = File.Exists(_processingMarker);

        if (hasBulkProcessingMarker)
        {
            _logger.LogInformation("Found bulk processing marker from previous session");

            // Try to read marker data to see if this is a manual bulk processing request
            bool isManualBulkRequest = false;
            try
            {
                if (File.Exists(_processingMarker))
                {
                    var markerContent = await File.ReadAllTextAsync(_processingMarker);
                    var markerData = System.Text.Json.JsonSerializer.Deserialize<dynamic>(markerContent);
                    // If marker exists and we can read it, this might be from a manual request
                    isManualBulkRequest = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read processing marker, treating as stale");
            }

            // Load saved position even if marker exists
            await LoadPosition();

            if (isManualBulkRequest && _lastPosition == 0)
            {
                // This appears to be a fresh manual bulk processing request
                _logger.LogInformation("Manual bulk processing detected - starting from beginning");
                _lastPosition = 0;
                _isBulkProcessing = true;
                _processingService.SetBulkProcessingMode(true);
                await SavePosition();
                return;
            }
            else if (_lastPosition > 0 && _lastPosition < fileInfo.Length)
            {
                // We have a valid saved position, resume from there
                var behind = fileInfo.Length - _lastPosition;
                _logger.LogInformation($"Resuming bulk processing from saved position: {_lastPosition:N0} ({behind / 1024.0 / 1024.0:F1} MB remaining)");
                _isBulkProcessing = true;
                _processingService.SetBulkProcessingMode(true);
                return;
            }
            else
            {
                // Invalid position or completed processing, clean up marker
                _logger.LogInformation("Cleaning up stale processing marker");
                try
                {
                    File.Delete(_processingMarker);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete stale processing marker");
                }
            }
        }
        else
        {
            // No marker exists, load normal position
            await LoadPosition();
        }

        _logger.LogInformation($"Loaded position: {_lastPosition:N0} (File size: {fileInfo.Length:N0})");

        if (_lastPosition >= 0 && _lastPosition <= fileInfo.Length)
        {
            var behind = fileInfo.Length - _lastPosition;
            _logger.LogInformation($"Starting from saved position: {_lastPosition:N0} ({behind / 1024.0 / 1024.0:F1} MB behind)");

            if (behind > 100_000_000) // 100MB behind
            {
                _logger.LogInformation($"Large backlog detected: {behind / 1024.0 / 1024.0:F1} MB to process - enabling bulk mode");
                _isBulkProcessing = true;
                _processingService.SetBulkProcessingMode(true);
            }
        }
        else
        {
            var startFromEnd = _configuration.GetValue<bool>("LanCache:StartFromEndOfLog", true);

            if (startFromEnd)
            {
                _lastPosition = fileInfo.Length;
                _logger.LogInformation($"Starting from END of log file (position: {_lastPosition:N0})");
            }
            else
            {
                _lastPosition = 0;
                _logger.LogInformation("Starting from beginning of log file");
                _isBulkProcessing = true;
                _processingService.SetBulkProcessingMode(true);
            }

            await SavePosition();
        }
    }

    private async Task ProcessLogFile(CancellationToken stoppingToken)
    {
        int retryCount = 0;
        const int maxRetries = 10;
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var wasBulkProcessing = _isBulkProcessing;

                // Check for restart signal from file watcher
                if (_restartProcessing)
                {
                    _restartProcessing = false;
                    _logger.LogInformation("Restarting processing loop due to marker file");
                }
                
                // Check for marker BEFORE starting processing
                if (File.Exists(_processingMarker) && !_isBulkProcessing)
                {
                    _logger.LogInformation("Marker file detected - forcing bulk processing restart");
                    _lastPosition = 0;
                    _isBulkProcessing = true;
                    _processingService.SetBulkProcessingMode(true);
                    await SavePosition();
                }

                await ProcessLogFileInternal(stoppingToken);
                retryCount = 0;

                if (_processingComplete)
                {
                    _logger.LogInformation("Processing completed - exiting LogWatcherService loop");
                    break;
                }

                // Loop back to start normal processing after bulk
                if (wasBulkProcessing && !_isBulkProcessing)
                {
                    if (_processingComplete)
                    {
                        _logger.LogInformation("Processing completed during bulk to normal transition - exiting");
                        break;
                    }
                    _logger.LogInformation("Transitioning from bulk to normal processing mode");
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }
            }
            catch (Exception ex)
            {
                retryCount++;
                _logger.LogError(ex, $"Error in main processing loop (attempt {retryCount}/{maxRetries})");
                
                if (retryCount >= maxRetries)
                {
                    _logger.LogError("Max retries reached, waiting 60 seconds before retrying");
                    await Task.Delay(60000, stoppingToken);
                    retryCount = 0;
                }
                else
                {
                    var delay = Math.Min(5000 * retryCount, 30000);
                    _logger.LogInformation($"Retrying in {delay/1000} seconds...");
                    await Task.Delay(delay, stoppingToken);
                }
            }
        }
    }

    private async Task ProcessLogFileInternal(CancellationToken stoppingToken)
    {
        // Store bulk processing state locally to prevent race conditions
        bool isBulkMode = _isBulkProcessing;

        var resumePosition = _lastPosition;
        if (isBulkMode)
        {
            if (resumePosition <= 0)
            {
                _logger.LogInformation("BULK PROCESSING MODE ACTIVE - starting full reprocess from beginning of log");
            }
            else
            {
                _logger.LogInformation($"BULK PROCESSING MODE ACTIVE - resuming from saved position {resumePosition:N0}");
            }
        }

        // Check if file is empty
        var fileCheck = new FileInfo(_logPath);
        if (fileCheck.Length == 0)
        {
            _logger.LogWarning("Log file is empty (0 bytes), cannot process");

            if (isBulkMode)
            {
                _isBulkProcessing = false;
                try
                {
                    await _processingService.CompleteBulkProcessingAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Bulk completion cancelled while handling empty log file");
                }

                if (File.Exists(_processingMarker))
                {
                    File.Delete(_processingMarker);
                    _logger.LogInformation("Removed bulk processing marker (empty file)");
                }

                CleanupOperationState();

                await _hubContext.Clients.All.SendAsync("ProcessingError", new {
                    error = "Log file is empty",
                    message = "No data to process"
                });
            }
            
            // Wait for file to have content
            while (fileCheck.Length == 0 && !stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Waiting for log file to have content...");
                await Task.Delay(10000, stoppingToken);
                fileCheck = new FileInfo(_logPath);
            }
            
            if (stoppingToken.IsCancellationRequested)
                return;
                
            _logger.LogInformation($"Log file now has content: {fileCheck.Length} bytes");
        }

        var bufferSize = isBulkMode ? 16384 : 4096; // Reduced buffer sizes to prevent memory issues
        
        // Open the log file
        FileStream? stream = null;
        StreamReader? reader = null;
        int openAttempts = 0;
        
        while (stream == null && openAttempts < 10 && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                openAttempts++;
                stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize);
                reader = new StreamReader(stream, Encoding.UTF8, true, bufferSize);
                _logger.LogInformation($"Successfully opened log file on attempt {openAttempts}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to open log file on attempt {openAttempts}: {ex.Message}");
                await Task.Delay(5000, stoppingToken);
            }
        }
        
        if (stream == null || reader == null)
        {
            throw new Exception($"Could not open log file after {openAttempts} attempts");
        }

        try
        {
            // Position the stream
            if (isBulkMode)
            {
                var targetPosition = Math.Max(0, Math.Min(resumePosition, stream.Length));
                _logger.LogInformation($"BULK MODE: Seeking to position {targetPosition:N0} (file length: {stream.Length})");
                stream.Seek(targetPosition, SeekOrigin.Begin);

                if (targetPosition > 0)
                {
                    await reader.ReadLineAsync(); // Skip partial line when resuming
                }

                _bulkProcessingStartTime = DateTime.UtcNow;

                if (targetPosition == 0)
                {
                    CreateInitialOperationState(stream.Length);
                }
                else
                {
                    var percentComplete = stream.Length > 0 ? (targetPosition * 100.0) / stream.Length : 0;
                    var mbProcessed = targetPosition / (1024.0 * 1024.0);
                    var mbTotal = stream.Length / (1024.0 * 1024.0);
                    UpdateOperationState(percentComplete, mbProcessed, mbTotal, 0, 0, targetPosition);
                }
            }
            else if (_lastPosition > 0 && _lastPosition < stream.Length)
            {
                _logger.LogInformation($"Normal mode: Seeking to saved position {_lastPosition:N0}");
                stream.Seek(_lastPosition, SeekOrigin.Begin);

                if (_lastPosition > 0)
                {
                    await reader.ReadLineAsync(); // Skip partial line
                }
            }
            else if (_lastPosition >= stream.Length)
            {
                _logger.LogInformation($"Position {_lastPosition} is at/past end of file {stream.Length}, seeking to end");
                stream.Seek(0, SeekOrigin.End);
            }
            else
            {
                _logger.LogInformation("Starting from beginning of file (position 0)");
                stream.Seek(0, SeekOrigin.Begin);
            }

            // Process the file
            await ProcessFileContent(reader, stream, isBulkMode, stoppingToken);
        }
        finally
        {
            reader?.Dispose();
            stream?.Dispose();
        }
    }

    private async Task ProcessFileContent(StreamReader reader, FileStream stream, bool isBulkMode, CancellationToken stoppingToken)
    {
        var linesProcessed = 0;
        var entriesProcessed = 0;
        var lastProgressUpdate = DateTime.UtcNow;
        var lastPositionSave = DateTime.UtcNow;
        var emptyReadCount = 0;
        double lastPercentReported = -1.0;
        var fileLength = stream.Length;
        int consecutiveFailures = 0; // Track consecutive enqueue failures for adaptive backpressure

        _logger.LogInformation($"Starting to read from position {stream.Position} (bulk={isBulkMode}, file size={stream.Length})");

        while (!stoppingToken.IsCancellationRequested && !_restartProcessing)
        {
            try
            {
                var line = await reader.ReadLineAsync();
                
                if (!string.IsNullOrEmpty(line))
                {
                    emptyReadCount = 0;
                    linesProcessed++;

                    // Log first few lines in bulk mode for debugging
                    if (isBulkMode && linesProcessed <= 5)
                    {
                        _logger.LogInformation($"Processing line {linesProcessed}: {line.Substring(0, Math.Min(100, line.Length))}...");
                    }

                    // Send line to processing service for parsing and batching
                    var enqueued = await _processingService.EnqueueLogLine(line);

                    if (enqueued)
                    {
                        entriesProcessed++;
                        consecutiveFailures = 0; // Reset failure counter on success

                        if (isBulkMode && entriesProcessed <= 5)
                        {
                            _logger.LogInformation($"Enqueued line {entriesProcessed} for processing");
                        }
                    }
                    else
                    {
                        consecutiveFailures++;

                        // Adaptive backpressure: start with small delays and increase exponentially
                        var baseDelay = isBulkMode ? 50 : 25; // Base delay in milliseconds
                        var maxDelay = isBulkMode ? 2000 : 1000; // Maximum delay
                        var delay = Math.Min(baseDelay * Math.Pow(2, Math.Min(consecutiveFailures - 1, 6)), maxDelay);

                        if (consecutiveFailures == 1)
                        {
                            _logger.LogWarning($"Failed to enqueue log line at position {stream.Position}, will retry. Starting adaptive backpressure (delay: {delay}ms)");
                        }
                        else if (consecutiveFailures % 10 == 0)
                        {
                            _logger.LogWarning($"Persistent enqueue failures ({consecutiveFailures} consecutive), delay: {delay}ms, position: {stream.Position}");
                        }

                        await Task.Delay((int)delay, stoppingToken);

                        // CRITICAL FIX: Seek back to retry this line
                        // We need to re-read the failed line, not skip it
                        stream.Seek(_lastPosition, SeekOrigin.Begin);
                        reader.DiscardBufferedData();
                        continue; // Retry reading from the saved position
                    }

                    // Add yielding every 1000 lines in bulk mode to prevent resource starvation
                    if (isBulkMode && linesProcessed % 1000 == 0)
                    {
                        await Task.Yield(); // Let other tasks run
                    }

                    // CRITICAL FIX: Update position after processing lines, not just when reading is done
                    _lastPosition = stream.Position;

                    // Progress reporting for bulk processing
                    if (isBulkMode)
                    {
                        var progressResult = await ReportProgressIfNeeded(stream, linesProcessed, entriesProcessed, 
                            lastProgressUpdate, lastPercentReported, fileLength);
                        lastProgressUpdate = progressResult.lastUpdate;
                        lastPercentReported = progressResult.lastPercent;
                    }

                    // Save position more frequently to handle force-close scenarios
                    // Save every 5 seconds in bulk mode, every 10 seconds in normal mode
                    var saveInterval = isBulkMode ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(10);
                    if (DateTime.UtcNow - lastPositionSave > saveInterval)
                    {
                        await SavePosition();
                        lastPositionSave = DateTime.UtcNow;
                    }

                    // Additionally save every 100 entries in bulk mode for better recovery
                    if (isBulkMode && entriesProcessed % 100 == 0 && entriesProcessed > 0)
                    {
                        await SavePosition();
                    }
                }
                else
                {
                    // No more data available
                    emptyReadCount++;
                    
                    // The LogProcessingService will handle remaining entries via its batch timeout
                    
                    _lastPosition = stream.Position;
                    await SavePosition();
                    
                    // Report progress for bulk mode
                    if (isBulkMode && entriesProcessed > 0)
                    {
                        var rawPercent = (stream.Position * 100.0) / fileLength;
                        // Don't artificially cap progress - let it reach 100% naturally
                        var percentComplete = rawPercent;
                        var mbProcessed = stream.Position / (1024.0 * 1024.0);
                        var mbTotal = fileLength / (1024.0 * 1024.0);

                        await SendProgressUpdate(percentComplete, mbProcessed, mbTotal, entriesProcessed, linesProcessed, stream.Position);
                    }
                    
                    // Check if bulk processing is complete
                    var currentFileInfo = new FileInfo(_logPath);

                    // More accurate completion check
                    var isAtEnd = stream.Position >= currentFileInfo.Length; // At or past end of file
                    var nearEnd = stream.Position >= currentFileInfo.Length - 100; // Within 100 bytes of end
                    var noMoreData = emptyReadCount > 10;

                    // Complete if we're at the end, or near the end with no more data
                    if (isAtEnd || (nearEnd && emptyReadCount > 3) || noMoreData)
                    {
                        _logger.LogInformation($"Bulk processing complete check: position={stream.Position}, fileLength={currentFileInfo.Length}, emptyReads={emptyReadCount}");
                        await CompleteBulkProcessing(entriesProcessed, linesProcessed, stream.Position, currentFileInfo.Length);
                        return;
                    }

                    // Check for new data
                    var latestFileInfo = new FileInfo(_logPath);
                    if (latestFileInfo.Length > fileLength)
                    {
                        _logger.LogDebug($"File grew from {fileLength} to {latestFileInfo.Length}");
                        fileLength = latestFileInfo.Length;
                        emptyReadCount = 0;
                        continue;
                    }
                    
                    try
                    {
                        await Task.Delay(isBulkMode ? 100 : 1000, stoppingToken); // Reduced bulk mode delay for better performance
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected during shutdown
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing log line at position {stream.Position}");
                try
                {
                    await Task.Delay(5000, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown
                    break;
                }
            }
        }
        
        if (_restartProcessing)
        {
            _logger.LogInformation("Exiting ProcessFileContent due to restart signal");
        }
    }

    private async Task<(DateTime lastUpdate, double lastPercent)> ReportProgressIfNeeded(
        FileStream stream, int linesProcessed, int entriesProcessed,
        DateTime lastProgressUpdate, double lastPercentReported, long fileLength)
    {
        var currentPosition = stream.Position;
        var rawPercent = fileLength > 0 ? (currentPosition * 100.0) / fileLength : 0;
        var percentComplete = rawPercent; // Don't cap - let it reach 100% naturally
        var shouldReport = false;

        // Report if: 500 lines processed, 2 seconds elapsed, or 1% progress change
        if (linesProcessed % 500 == 0 ||
            DateTime.UtcNow - lastProgressUpdate > TimeSpan.FromSeconds(2) ||
            Math.Abs(percentComplete - lastPercentReported) >= 1.0)
        {
            shouldReport = true;
        }

        if (shouldReport)
        {
            var mbProcessed = currentPosition / (1024.0 * 1024.0);
            var mbTotal = fileLength / (1024.0 * 1024.0);

            _logger.LogInformation($"Progress: {percentComplete:F1}% ({mbProcessed:F1}/{mbTotal:F1} MB) - {entriesProcessed} entries from {linesProcessed} lines");

            await SendProgressUpdate(percentComplete, mbProcessed, mbTotal, entriesProcessed, linesProcessed, currentPosition);

            return (DateTime.UtcNow, percentComplete);
        }

        return (lastProgressUpdate, lastPercentReported);
    }

    private async Task SendProgressUpdate(double percentComplete, double mbProcessed, double mbTotal, 
        int entriesProcessed, int linesProcessed, long currentPosition)
    {
        // Update operation state
        UpdateOperationState(percentComplete, mbProcessed, mbTotal, entriesProcessed, linesProcessed, currentPosition);
        
        // Send SignalR notification
        try
        {
            var progressData = new {
                percentComplete,
                mbProcessed,
                mbTotal,
                entriesProcessed,
                linesProcessed,
                timestamp = DateTime.UtcNow,
                status = "processing"
            };
            
            await _hubContext.Clients.All.SendAsync("ProcessingProgress", progressData);
            _logger.LogTrace($"Sent SignalR progress update: {percentComplete:F1}%");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send progress via SignalR");
        }
    }

    private async Task CompleteBulkProcessing(int entriesProcessed, int linesProcessed, long finalPosition, long fileLength)
    {
        var elapsed = DateTime.UtcNow - _bulkProcessingStartTime;
        _logger.LogInformation($"File reading completed in {elapsed.TotalMinutes:F1} minutes");
        _logger.LogInformation($"Final stats: Read {entriesProcessed} entries from {linesProcessed} lines");
        _logger.LogInformation($"Final position: {finalPosition} / {fileLength}");

        // Calculate final values FIRST
        var mbProcessed = fileLength / (1024.0 * 1024.0);
        var mbTotal = fileLength / (1024.0 * 1024.0);

        // Skip the finalizing update - we'll go straight to 100% completion

        // Wait for LogProcessingService to finish
        _logger.LogInformation("Waiting for LogProcessingService to finish processing all queued entries...");
        _isBulkProcessing = false;
        try
        {
            await _processingService.CompleteBulkProcessingAsync();
            _logger.LogInformation("LogProcessingService completed successfully - all entries processed");
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Bulk completion cancellation observed while finalizing log processing");
        }
        _bulkProcessingStartTime = default;

        // NOW send the final 100% progress update via SignalR
        try
        {
            var finalProgressData = new {
                percentComplete = 100.0,
                mbProcessed,
                mbTotal,
                entriesProcessed,
                linesProcessed,
                timestamp = DateTime.UtcNow,
                status = "complete"
            };

            await _hubContext.Clients.All.SendAsync("ProcessingProgress", finalProgressData);
            _logger.LogInformation("Sent final 100% progress update via SignalR with data: {@ProgressData}", finalProgressData);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send final progress update via SignalR");
        }

        // NOW mark operation as complete with ALL data fields
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var stateService = scope.ServiceProvider.GetRequiredService<OperationStateService>();
            stateService.UpdateState("activeLogProcessing", new Dictionary<string, object>
            {
                { "isProcessing", false },
                { "status", "complete" },
                { "percentComplete", 100.0 },
                { "mbProcessed", mbProcessed },
                { "mbTotal", mbTotal },
                { "entriesProcessed", entriesProcessed },
                { "linesProcessed", linesProcessed },
                { "currentPosition", finalPosition },
                { "completedAt", DateTime.UtcNow }
            });
            _logger.LogInformation("Marked operation state as complete with full data");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mark operation as complete");
        }

        if (entriesProcessed == 0 && linesProcessed == 0)
        {
            _logger.LogError("BULK PROCESSING FAILED: No lines were read. File may be inaccessible or in wrong format.");
        }

        if (File.Exists(_processingMarker))
        {
            try
            {
                File.Delete(_processingMarker);
                _logger.LogInformation("Removed bulk processing marker");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove processing marker");
            }
        }

        // Don't immediately cleanup - let the UI see the completion status
        // The state will be cleaned up on the next processing run or after expiration
        // CleanupOperationState();

        // Send completion notification first (without depot mapping count since we haven't done it yet)
        try
        {
            await _hubContext.Clients.All.SendAsync("BulkProcessingComplete", new {
                entriesProcessed,
                linesProcessed,
                elapsed = elapsed.TotalMinutes,
                depotMappingsProcessed = 0,  // Will be processed separately after this
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send completion notification");
        }

        _logger.LogInformation($"Log processing complete. {entriesProcessed} entries processed.");

        _processingComplete = true;

        // Don't automatically run depot mapping - let the user trigger it manually when needed
        _logger.LogInformation("Log processing finished. Depot mapping can be triggered manually if needed.");
    }

    private async Task<int> RunDepotPostProcessingAsync()
    {
        // Send SignalR notification that depot mapping is starting
        try
        {
            await _hubContext.Clients.All.SendAsync("DepotMappingStarted", new
            {
                message = "Starting depot mapping post-processing...",
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send depot mapping start notification");
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
            var processed = await dbService.PostProcessDepotMappings();
            _logger.LogInformation($"Depot mapping post-processing completed automatically. Updated {processed} downloads.");
            return processed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to automatically post-process depot mappings after log processing");
            try
            {
                await _hubContext.Clients.All.SendAsync("DepotPostProcessingFailed", new {
                    error = ex.Message,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception hubEx)
            {
                _logger.LogTrace(hubEx, "Failed to publish depot post-processing error to clients");
            }
            return 0;
        }
    }

    private void CreateInitialOperationState(long fileSize)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var stateService = scope.ServiceProvider.GetRequiredService<OperationStateService>();

            // Clear any existing operation first to prevent duplicates or stale operations
            stateService.RemoveState("activeLogProcessing");

            var operationState = new OperationState
            {
                Key = "activeLogProcessing",
                Type = "log_processing",
                Status = "processing",
                Message = "Starting to process log file",
                Data = new Dictionary<string, object>
                {
                    { "startTime", DateTime.UtcNow },
                    { "fileSize", fileSize },
                    { "percentComplete", 0 },
                    { "status", "processing" },
                    { "isProcessing", true }
                },
                ExpiresAt = DateTime.UtcNow.AddHours(24)
            };
            stateService.SaveState("activeLogProcessing", operationState);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create initial operation state");
        }
    }

    private void UpdateOperationState(double percentComplete, double mbProcessed, double mbTotal, 
        int entriesProcessed, int linesProcessed, long currentPosition)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var stateService = scope.ServiceProvider.GetRequiredService<OperationStateService>();
            stateService.UpdateState("activeLogProcessing", new Dictionary<string, object>
            {
                { "percentComplete", percentComplete },
                { "mbProcessed", mbProcessed },
                { "mbTotal", mbTotal },
                { "entriesProcessed", entriesProcessed },
                { "linesProcessed", linesProcessed },
                { "currentPosition", currentPosition },
                { "isProcessing", true },
                { "status", "processing" }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update operation state");
        }
    }

    private void CleanupOperationState()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var stateService = scope.ServiceProvider.GetRequiredService<OperationStateService>();
            stateService.RemoveState("activeLogProcessing");
            _logger.LogInformation("Cleared operation state");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear operation state");
        }
    }

    // ProcessBatch is now handled by LogProcessingService for better parallelism and performance

    private Task SavePosition()
    {
        try
        {
            _stateService.SetLogPosition(_lastPosition);
            _logger.LogTrace($"Saved position: {_lastPosition}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving position");
        }
        return Task.CompletedTask;
    }

    private Task LoadPosition()
    {
        try
        {
            _lastPosition = _stateService.GetLogPosition();
            _logger.LogInformation($"Loaded saved position: {_lastPosition:N0}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading position");
            _lastPosition = -1;
        }
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation($"LogWatcherService stopping, current position: {_lastPosition:N0}");

        // Save current position multiple times to ensure it's persisted
        await SavePosition();
        _logger.LogInformation($"Position saved: {_lastPosition:N0}");

        // Force a second save with a small delay to ensure it's written to disk
        await Task.Delay(100);
        await SavePosition();
        _logger.LogInformation($"Position confirmed saved: {_lastPosition:N0}");

        // Dispose marker watcher
        _markerWatcher?.Dispose();

        // Call base implementation
        await base.StopAsync(cancellationToken);

        _logger.LogInformation("LogWatcherService stopped successfully with position: {0:N0}", _lastPosition);
    }


    public override void Dispose()
    {
        // Clean up any active log processing operation
        try
        {
            // Check if service provider is still available before using it
            if (_serviceProvider != null)
            {
                using var scope = _serviceProvider.CreateScope();
                if (scope != null)
                {
                    var stateService = scope.ServiceProvider.GetService<OperationStateService>();
                    if (stateService != null)
                    {
                        stateService.RemoveState("activeLogProcessing");
                        _logger.LogInformation("Cleared operation state during dispose");
                    }
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Service provider already disposed, skip cleanup
            _logger.LogDebug("Service provider already disposed, skipping operation state cleanup");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear operation state during dispose");
        }

        _markerWatcher?.Dispose();
        base.Dispose();
    }

}
