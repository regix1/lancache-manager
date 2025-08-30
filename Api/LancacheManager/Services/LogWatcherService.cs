using LancacheManager.Models;
using LancacheManager.Services;
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
    private volatile bool _restartProcessing = false; // Add flag to force restart

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
        // Set up file watcher for marker file
        SetupMarkerWatcher();
        
        // Wait for file to exist
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
                _logger.LogWarning($"Log file still not found after {waitAttempts} attempts. Checking parent directory...");
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
        // Prevent multiple triggers
        if (_isBulkProcessing)
        {
            _logger.LogDebug("Already bulk processing, ignoring duplicate marker event");
            return;
        }
        
        _logger.LogInformation("Processing marker detected via file watcher - FORCING bulk processing from position 0");
        _lastPosition = 0;
        _isBulkProcessing = true;
        _restartProcessing = true; // Signal to restart the processing loop
        _lastMarkerCheck = DateTime.UtcNow;
        
        // Force save position to 0 IMMEDIATELY and wait for completion
        Task.Run(async () => {
            await File.WriteAllTextAsync(_positionFile, "0");
            _logger.LogInformation("Forced position file to 0 for bulk processing");
        }).Wait();
    }

    private async Task InitializePosition()
    {
        // Check for bulk processing marker FIRST - this takes absolute priority
        bool hasBulkProcessingMarker = File.Exists(_processingMarker);
        
        if (hasBulkProcessingMarker)
        {
            _logger.LogInformation("Found bulk processing marker at initialization - FORCING position to 0");
            
            // ALWAYS force position to 0 for bulk processing
            _lastPosition = 0;
            _isBulkProcessing = true;
            await SavePosition();
            
            var fileInfo = new FileInfo(_logPath);
            _logger.LogInformation($"Bulk processing WILL START FROM BEGINNING: {fileInfo.Length / 1024.0 / 1024.0:F1} MB to process");
            return; // EXIT HERE - don't load saved position
        }
        
        // ONLY load saved position if NOT bulk processing
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
                    // The flags are already set by OnMarkerCreated
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
                
                // If we just finished bulk processing, loop back to start normal processing
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
        // CRITICAL FIX: Store bulk processing state locally to prevent race conditions
        bool isBulkMode = _isBulkProcessing;
        
        // If bulk processing, FORCE position to 0 regardless of what's in the file
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
                
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var stateService = scope.ServiceProvider.GetRequiredService<OperationStateService>();
                    stateService.RemoveState("activeLogProcessing");
                }
                catch { }
                
                await _hubContext.Clients.All.SendAsync("ProcessingError", new {
                    error = "Log file is empty",
                    message = "No data to process"
                });
            }
            
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
            // CRITICAL FIX: Use the local isBulkMode variable consistently
            if (isBulkMode)
            {
                _logger.LogInformation($"BULK MODE: Seeking to position 0 (file length: {stream.Length})");
                stream.Seek(0, SeekOrigin.Begin);
                _lastPosition = 0;
                
                // Verify we're actually at position 0
                _logger.LogInformation($"Stream position after seek: {stream.Position}");
                
                if (stream.Position != 0)
                {
                    _logger.LogError($"Failed to seek to position 0! Stream is at {stream.Position}");
                    // Force it again
                    stream.Position = 0;
                    _logger.LogInformation($"Forced stream.Position = 0, now at: {stream.Position}");
                }
                
                // Send initial progress notification for bulk processing
                await _hubContext.Clients.All.SendAsync("ProcessingProgress", new {
                    percentComplete = 0,
                    mbProcessed = 0,
                    mbTotal = stream.Length / (1024.0 * 1024.0),
                    entriesProcessed = 0,
                    linesProcessed = 0,
                    timestamp = DateTime.UtcNow,
                    status = "starting"
                });
            }
            else if (_lastPosition > 0 && _lastPosition < stream.Length)
            {
                _logger.LogInformation($"Normal mode: Seeking to saved position {_lastPosition:N0}");
                stream.Seek(_lastPosition, SeekOrigin.Begin);
                
                // Skip partial line if not at beginning
                if (_lastPosition > 0)
                {
                    await reader.ReadLineAsync();
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

            var linesProcessed = 0;
            var entriesProcessed = 0;
            var bulkStartTime = DateTime.UtcNow;
            var lastProgressUpdate = DateTime.UtcNow;
            var emptyReadCount = 0;
            var lastSaveTime = DateTime.UtcNow;

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

                        // Log first few lines when bulk processing for debugging
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

                        var batchSize = isBulkMode ? 500 : 50;
                        
                        if (_batchBuffer.Count >= batchSize)
                        {
                            await ProcessBatch(_batchBuffer.ToList(), !isBulkMode);
                            _batchBuffer.Clear();
                        }

                        // Progress reporting for bulk processing
                        // Report every 500 lines, or every 2 seconds, or if progress changed significantly
                        if (isBulkMode)
                        {
                            var shouldReport = false;
                            var percentComplete = (stream.Position * 100.0) / stream.Length;
                            
                            // Report if: 500 lines processed, 2 seconds elapsed, or 1% progress change
                            if (linesProcessed % 500 == 0 || 
                                DateTime.UtcNow - lastProgressUpdate > TimeSpan.FromSeconds(2) ||
                                Math.Abs(percentComplete - lastPercentReported) >= 1.0)
                            {
                                shouldReport = true;
                            }
                            
                            if (shouldReport)
                            {
                                var mbProcessed = stream.Position / (1024.0 * 1024.0);
                                var mbTotal = stream.Length / (1024.0 * 1024.0);
                                
                                _logger.LogInformation($"Progress: {percentComplete:F1}% ({mbProcessed:F1}/{mbTotal:F1} MB) - {entriesProcessed} entries from {linesProcessed} lines");
                                
                                // Update operation state
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
                                        { "currentPosition", stream.Position },
                                        { "isProcessing", true },
                                        { "status", "processing" }
                                    });
                                }
                                catch (Exception ex) 
                                { 
                                    _logger.LogWarning(ex, "Failed to update operation state");
                                }
                                
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
                                
                                lastProgressUpdate = DateTime.UtcNow;
                                lastPercentReported = percentComplete;
                                _lastPosition = stream.Position;
                                await SavePosition();
                            }
                        }
                    }
                    else
                    {
                        emptyReadCount++;
                        
                        if (_batchBuffer.Count > 0)
                        {
                            _logger.LogInformation($"Processing final batch of {_batchBuffer.Count} entries");
                            await ProcessBatch(_batchBuffer.ToList(), !isBulkMode);
                            _batchBuffer.Clear();
                        }
                        
                        _lastPosition = stream.Position;
                        
                        // Only save position in normal mode or when making progress
                        if (!isBulkMode || entriesProcessed > 0)
                        {
                            await SavePosition();
                        }
                        
                        if (isBulkMode)
                        {
                            var fileInfo = new FileInfo(_logPath);
                            
                            // Check if we've actually reached the end
                            if (stream.Position >= fileInfo.Length - 1000 || emptyReadCount > 10)
                            {
                                var elapsed = DateTime.UtcNow - bulkStartTime;
                                _logger.LogInformation($"Bulk processing completed in {elapsed.TotalMinutes:F1} minutes");
                                _logger.LogInformation($"Final stats: Processed {entriesProcessed} entries from {linesProcessed} lines");
                                _logger.LogInformation($"Final position: {stream.Position} / {fileInfo.Length}");
                                
                                _isBulkProcessing = false;
                                
                                if (entriesProcessed == 0 && linesProcessed == 0)
                                {
                                    _logger.LogError("BULK PROCESSING FAILED: No lines were read. File may be inaccessible or in wrong format.");
                                }
                                
                                // Remove processing marker
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
                                
                                return;
                            }
                        }
                        
                        var currentFileInfo = new FileInfo(_logPath);
                        if (currentFileInfo.Length > stream.Length)
                        {
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
            
            // If we're breaking out due to restart signal, log it
            if (_restartProcessing)
            {
                _logger.LogInformation("Exiting ProcessLogFileInternal due to restart signal");
            }
        }
        finally
        {
            reader?.Dispose();
            stream?.Dispose();
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