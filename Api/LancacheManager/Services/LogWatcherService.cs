using LancacheManager.Models;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using LancacheManager.Hubs;

namespace LancacheManager.Services;

public class LogWatcherService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly LogParserService _parser;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LogWatcherService> _logger;
    private readonly IHubContext<DownloadHub> _hubContext;
    private long _lastPosition = -1;
    private readonly string _positionFile = "/data/logposition.txt";
    private readonly string _processingMarker = "/data/bulk_processing.marker";
    private readonly string _logPath;
    private bool _isBulkProcessing = false;
    private readonly List<LogEntry> _batchBuffer = new();
    private DateTime _lastMarkerCheck = DateTime.MinValue;
    private FileSystemWatcher? _markerWatcher;
    private volatile bool _restartProcessing = false;

    public LogWatcherService(
        IServiceProvider serviceProvider,
        LogParserService parser,
        IConfiguration configuration,
        ILogger<LogWatcherService> logger,
        IHubContext<DownloadHub> hubContext)
    {
        _serviceProvider = serviceProvider;
        _parser = parser;
        _configuration = configuration;
        _logger = logger;
        _hubContext = hubContext;
        _logPath = configuration["LanCache:LogPath"] ?? "/logs/access.log";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SetupMarkerWatcher();
        
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
            var directory = Path.GetDirectoryName(_processingMarker) ?? "/data";
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
        _restartProcessing = true;
        _lastMarkerCheck = DateTime.UtcNow;
        
        // Force save position to 0 immediately
        Task.Run(async () => {
            await File.WriteAllTextAsync(_positionFile, "0");
            _logger.LogInformation("Forced position file to 0 for bulk processing");
        }).Wait();
    }

    private async Task InitializePosition()
    {
        // Check for bulk processing marker FIRST
        bool hasBulkProcessingMarker = File.Exists(_processingMarker);
        
        if (hasBulkProcessingMarker)
        {
            _logger.LogInformation("Found bulk processing marker at initialization - FORCING position to 0");
            _lastPosition = 0;
            _isBulkProcessing = true;
            await SavePosition();
            
            var fileInfo = new FileInfo(_logPath);
            _logger.LogInformation($"Bulk processing WILL START FROM BEGINNING: {fileInfo.Length / 1024.0 / 1024.0:F1} MB to process");
            return;
        }
        
        // Only load saved position if NOT bulk processing
        var fileInfo2 = new FileInfo(_logPath);
        _logger.LogInformation($"Log file size: {fileInfo2.Length / 1024.0 / 1024.0:F1} MB");
        
        await LoadPosition();
        
        _logger.LogInformation($"Loaded position: {_lastPosition:N0} (File size: {fileInfo2.Length:N0})");
        
        if (_lastPosition >= 0 && _lastPosition <= fileInfo2.Length)
        {
            var behind = fileInfo2.Length - _lastPosition;
            _logger.LogInformation($"Starting from saved position: {_lastPosition:N0} ({behind / 1024.0 / 1024.0:F1} MB behind)");
            
            if (behind > 100_000_000) // 100MB behind
            {
                _logger.LogInformation($"Large backlog detected: {behind / 1024.0 / 1024.0:F1} MB to process - enabling bulk mode");
                _isBulkProcessing = true;
            }
        }
        else
        {
            var startFromEnd = _configuration.GetValue<bool>("LanCache:StartFromEndOfLog", true);
            
            if (startFromEnd)
            {
                _lastPosition = fileInfo2.Length;
                _logger.LogInformation($"Starting from END of log file (position: {_lastPosition:N0})");
            }
            else
            {
                _lastPosition = 0;
                _logger.LogInformation("Starting from beginning of log file");
                _isBulkProcessing = true;
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
                    await SavePosition();
                }
                
                await ProcessLogFileInternal(stoppingToken);
                retryCount = 0;
                
                // Loop back to start normal processing after bulk
                if (!_isBulkProcessing)
                {
                    _logger.LogInformation("Transitioning from bulk to normal processing mode");
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
        
        // Force position to 0 if bulk processing
        if (isBulkMode)
        {
            _logger.LogInformation($"BULK PROCESSING MODE ACTIVE - Forcing position to 0 (previous position: {_lastPosition})");
            _lastPosition = 0;
            await SavePosition();
        }
        
        // Check if file is empty
        var fileCheck = new FileInfo(_logPath);
        if (fileCheck.Length == 0)
        {
            _logger.LogWarning("Log file is empty (0 bytes), cannot process");
            
            if (isBulkMode)
            {
                _isBulkProcessing = false;
                
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

        var bufferSize = isBulkMode ? 131072 : 8192;
        
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
                _logger.LogInformation($"BULK MODE: Seeking to position 0 (file length: {stream.Length})");
                stream.Seek(0, SeekOrigin.Begin);
                _lastPosition = 0;
                
                // Verify position
                _logger.LogInformation($"Stream position after seek: {stream.Position}");
                
                if (stream.Position != 0)
                {
                    _logger.LogError($"Failed to seek to position 0! Stream is at {stream.Position}");
                    stream.Position = 0;
                    _logger.LogInformation($"Forced stream.Position = 0, now at: {stream.Position}");
                }
                
                // Create initial operation state
                CreateInitialOperationState(stream.Length);
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
        var bulkStartTime = DateTime.UtcNow;
        var lastProgressUpdate = DateTime.UtcNow;
        var lastPositionSave = DateTime.UtcNow;
        var emptyReadCount = 0;
        double lastPercentReported = -1.0;
        var fileLength = stream.Length;

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

                    var entry = _parser.ParseLine(line);
                    
                    if (entry != null && entry.BytesServed > 0)
                    {
                        entriesProcessed++;
                        _batchBuffer.Add(entry);
                        
                        if (isBulkMode && entriesProcessed <= 5)
                        {
                            _logger.LogInformation($"Parsed entry {entriesProcessed}: {entry.Service} - {entry.ClientIp} - {entry.BytesServed} bytes");
                        }
                    }

                    // Process batch when buffer is full
                    var batchSize = isBulkMode ? 500 : 50;
                    if (_batchBuffer.Count >= batchSize)
                    {
                        await ProcessBatch(_batchBuffer.ToList(), !isBulkMode);
                        _batchBuffer.Clear();
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

                    // Save position periodically during bulk processing (every 5 seconds)
                    if (isBulkMode && DateTime.UtcNow - lastPositionSave > TimeSpan.FromSeconds(5))
                    {
                        await SavePosition();
                        lastPositionSave = DateTime.UtcNow;
                    }
                }
                else
                {
                    // No more data available
                    emptyReadCount++;
                    
                    // Process any remaining entries
                    if (_batchBuffer.Count > 0)
                    {
                        _logger.LogInformation($"Processing final batch of {_batchBuffer.Count} entries");
                        await ProcessBatch(_batchBuffer.ToList(), !isBulkMode);
                        _batchBuffer.Clear();
                    }
                    
                    _lastPosition = stream.Position;
                    await SavePosition();
                    
                    // Report final progress for bulk mode
                    if (isBulkMode && entriesProcessed > 0)
                    {
                        var percentComplete = (stream.Position * 100.0) / fileLength;
                        var mbProcessed = stream.Position / (1024.0 * 1024.0);
                        var mbTotal = fileLength / (1024.0 * 1024.0);
                        
                        await SendProgressUpdate(percentComplete, mbProcessed, mbTotal, entriesProcessed, linesProcessed, stream.Position);
                    }
                    
                    // Check if bulk processing is complete
                    if (isBulkMode)
                    {
                        var currentFileInfo = new FileInfo(_logPath);
                        
                        // More accurate completion check
                        var isAtEnd = stream.Position >= currentFileInfo.Length - 100; // Within 100 bytes of end
                        var noMoreData = emptyReadCount > 10;
                        
                        if (isAtEnd || noMoreData)
                        {
                            _logger.LogInformation($"Bulk processing complete check: position={stream.Position}, fileLength={currentFileInfo.Length}, emptyReads={emptyReadCount}");
                            await CompleteBulkProcessing(bulkStartTime, entriesProcessed, linesProcessed, stream.Position, currentFileInfo.Length);
                            return;
                        }
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
                    
                    await Task.Delay(isBulkMode ? 100 : 1000, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing log line at position {stream.Position}");
                await Task.Delay(5000, stoppingToken);
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
        var percentComplete = fileLength > 0 ? (currentPosition * 100.0) / fileLength : 0;
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
            await _hubContext.Clients.All.SendAsync("ProcessingProgress", new {
                percentComplete,
                mbProcessed,
                mbTotal,
                entriesProcessed,
                linesProcessed,
                timestamp = DateTime.UtcNow,
                status = "processing"
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send progress via SignalR");
        }
    }

    private async Task CompleteBulkProcessing(DateTime bulkStartTime, int entriesProcessed, int linesProcessed, long finalPosition, long fileLength)
    {
        var elapsed = DateTime.UtcNow - bulkStartTime;
        _logger.LogInformation($"Bulk processing completed in {elapsed.TotalMinutes:F1} minutes");
        _logger.LogInformation($"Final stats: Processed {entriesProcessed} entries from {linesProcessed} lines");
        _logger.LogInformation($"Final position: {finalPosition} / {fileLength}");
        
        _isBulkProcessing = false;
        
        if (entriesProcessed == 0 && linesProcessed == 0)
        {
            _logger.LogError("BULK PROCESSING FAILED: No lines were read. File may be inaccessible or in wrong format.");
        }
        
        // Clean up marker file
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
        
        // Clear operation state
        CleanupOperationState();
        
        // Send completion notification
        try
        {
            await _hubContext.Clients.All.SendAsync("BulkProcessingComplete", new {
                entriesProcessed,
                linesProcessed,
                elapsed = elapsed.TotalMinutes,
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send completion notification");
        }
    }

    private void CreateInitialOperationState(long fileSize)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var stateService = scope.ServiceProvider.GetRequiredService<OperationStateService>();
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

    private async Task ProcessBatch(List<LogEntry> entries, bool sendRealtimeUpdates)
    {
        if (entries.Count == 0) return;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();

            var grouped = entries.GroupBy(e => new { e.ClientIp, e.Service });

            foreach (var group in grouped)
            {
                try
                {
                    await dbService.ProcessLogEntryBatch(group.ToList(), sendRealtimeUpdates);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error processing batch for {group.Key.ClientIp}/{group.Key.Service}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing batch");
        }
    }

    private async Task SavePosition()
    {
        try
        {
            if (!Directory.Exists("/data"))
            {
                Directory.CreateDirectory("/data");
            }
            
            await File.WriteAllTextAsync(_positionFile, _lastPosition.ToString());
            _logger.LogTrace($"Saved position: {_lastPosition}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving position");
        }
    }

    private async Task LoadPosition()
    {
        try
        {
            if (File.Exists(_positionFile))
            {
                var content = await File.ReadAllTextAsync(_positionFile);
                if (long.TryParse(content, out var position))
                {
                    _lastPosition = position;
                    _logger.LogInformation($"Loaded saved position: {_lastPosition:N0}");
                }
                else
                {
                    _logger.LogWarning($"Invalid position file content: {content}");
                    _lastPosition = -1;
                }
            }
            else
            {
                _logger.LogInformation("No saved position file found");
                _lastPosition = -1;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading position");
            _lastPosition = -1;
        }
    }

    public override void Dispose()
    {
        _markerWatcher?.Dispose();
        base.Dispose();
    }
}