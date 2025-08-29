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
    private int _consecutiveEmptyReads = 0;
    private bool _isBulkProcessing = false;
    private readonly List<LogEntry> _batchBuffer = new();
    private DateTime _lastMarkerCheck = DateTime.MinValue;

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
        // Wait for file to exist
        while (!File.Exists(_logPath) && !stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation($"Waiting for log file: {_logPath}");
            await Task.Delay(10000, stoppingToken);
        }

        if (stoppingToken.IsCancellationRequested)
            return;

        await InitializePosition();
        await ProcessLogFile(stoppingToken);
    }

    private async Task<bool> CheckForProcessingMarker()
    {
        // Check every 5 seconds for new processing marker
        if (DateTime.UtcNow - _lastMarkerCheck < TimeSpan.FromSeconds(5))
            return false;
            
        _lastMarkerCheck = DateTime.UtcNow;
        
        if (File.Exists(_processingMarker) && !_isBulkProcessing)
        {
            _logger.LogInformation("Found new bulk processing marker - restarting from beginning");
            
            // Load the current position to see if we need to restart
            if (File.Exists(_positionFile))
            {
                var content = await File.ReadAllTextAsync(_positionFile);
                if (long.TryParse(content, out var position) && position == 0)
                {
                    // Position was reset to 0, start bulk processing
                    _lastPosition = 0;
                    _isBulkProcessing = true;
                    _logger.LogInformation("Starting bulk processing from beginning");
                    return true; // Signal to restart processing
                }
            }
        }
        else if (!File.Exists(_processingMarker) && _isBulkProcessing)
        {
            // Marker was removed, stop bulk processing
            _logger.LogInformation("Bulk processing marker removed - returning to normal mode");
            _isBulkProcessing = false;
        }
        
        return false;
    }

    private async Task InitializePosition()
    {
        var fileInfo = new FileInfo(_logPath);
        
        _logger.LogInformation($"Log file size: {fileInfo.Length / 1024.0 / 1024.0:F1} MB");
        
        // Check for bulk processing marker
        bool hasBulkProcessingMarker = File.Exists(_processingMarker);
        
        if (hasBulkProcessingMarker)
        {
            _logger.LogInformation("Found bulk processing marker - will process entire log");
        }
        
        // Load saved position
        await LoadPosition();
        
        _logger.LogInformation($"Loaded position: {_lastPosition:N0} (File size: {fileInfo.Length:N0})");
        
        // Determine starting position
        if (hasBulkProcessingMarker)
        {
            // Always start from 0 when marker exists, regardless of saved position
            _lastPosition = 0;
            _logger.LogInformation($"Bulk processing: starting from beginning ({fileInfo.Length / 1024.0 / 1024.0:F1} MB to process)");
            _isBulkProcessing = true;
            await SavePosition(); // Save immediately to ensure consistency
        }
        else if (_lastPosition >= 0 && _lastPosition <= fileInfo.Length)
        {
            // Normal operation - use saved position
            var behind = fileInfo.Length - _lastPosition;
            _logger.LogInformation($"Starting from saved position: {_lastPosition:N0} ({behind / 1024.0 / 1024.0:F1} MB behind)");
            
            // Auto-enable bulk mode if we're far behind
            if (behind > 100_000_000) // 100MB behind
            {
                _logger.LogInformation($"Large backlog detected: {behind / 1024.0 / 1024.0:F1} MB to process - enabling bulk mode");
                _isBulkProcessing = true;
            }
        }
        else
        {
            // No valid position - start from end by default
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
            }
        }
        
        await SavePosition();
    }

    private async Task ProcessLogFile(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Check for new processing marker periodically
                if (await CheckForProcessingMarker())
                {
                    // Restart processing from the beginning
                    _logger.LogInformation("Restarting log processing due to marker");
                }
                
                await ProcessLogFileInternal(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in main processing loop");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task ProcessLogFileInternal(CancellationToken stoppingToken)
    {
        var bufferSize = _isBulkProcessing ? 65536 : 4096; // Larger buffer for bulk processing
        using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, bufferSize);

        // Seek to last position
        if (_lastPosition > 0 && _lastPosition < stream.Length)
        {
            stream.Seek(_lastPosition, SeekOrigin.Begin);
            
            // Skip partial line if not at end
            if (_lastPosition < stream.Length)
            {
                await reader.ReadLineAsync();
            }
        }
        else if (_lastPosition >= stream.Length)
        {
            // Position is at or past end, go to end
            stream.Seek(0, SeekOrigin.End);
        }

        var lastReportTime = DateTime.UtcNow;
        var lastSaveTime = DateTime.UtcNow;
        var linesProcessed = 0;
        var entriesProcessed = 0;
        var bulkStartTime = DateTime.UtcNow;
        var debugLineCount = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Check for marker changes periodically
                if (await CheckForProcessingMarker())
                {
                    _logger.LogInformation("Processing marker changed, restarting processing");
                    return; // Exit this method to restart
                }

                var line = await reader.ReadLineAsync();
                
                if (!string.IsNullOrEmpty(line))
                {
                    _consecutiveEmptyReads = 0;
                    linesProcessed++;

                    // Debug logging for first few lines
                    if (_isBulkProcessing && debugLineCount < 5)
                    {
                        debugLineCount++;
                        _logger.LogWarning($"DEBUG Line {debugLineCount}: {line.Substring(0, Math.Min(150, line.Length))}...");
                    }

                    var entry = _parser.ParseLine(line);
                    
                    // Debug logging for parsing results
                    if (_isBulkProcessing && debugLineCount <= 5)
                    {
                        if (entry != null)
                        {
                            _logger.LogWarning($"DEBUG Parsed: Service={entry.Service}, Client={entry.ClientIp}, Bytes={entry.BytesServed}, Status={entry.CacheStatus}");
                        }
                        else
                        {
                            _logger.LogWarning($"DEBUG: Line {linesProcessed} did NOT parse");
                        }
                    }

                    // Replace with better debugging:
                    if (entry != null && entry.BytesServed > 0) // Process all entries with bytes
                    {
                        entriesProcessed++;
                        _batchBuffer.Add(entry);
                        
                        // Debug logging when we add to buffer
                        if (_isBulkProcessing && entriesProcessed <= 5)
                        {
                            _logger.LogWarning($"DEBUG: Added entry {entriesProcessed} to buffer (size now: {_batchBuffer.Count})");
                        }
                    }
                    else if (_isBulkProcessing && linesProcessed % 10000 == 0)
                    {
                        // Log parsing failures periodically
                        _logger.LogWarning($"Progress: {linesProcessed} lines read, {entriesProcessed} entries parsed ({(entriesProcessed * 100.0 / linesProcessed):F1}% success rate)");
                        if (entry == null && !string.IsNullOrEmpty(line))
                        {
                            _logger.LogWarning($"Failed to parse line {linesProcessed}: {line.Substring(0, Math.Min(200, line.Length))}");
                        }
                    }

                    // Process batch when ready
                    var batchSize = _isBulkProcessing ? 1000 : 50;
                    
                    if (_batchBuffer.Count >= batchSize)
                    {
                        await ProcessBatch(_batchBuffer.ToList(), !_isBulkProcessing);
                        _batchBuffer.Clear();
                        
                        // Force position save after each batch during bulk processing
                        if (_isBulkProcessing)
                        {
                            _lastPosition = stream.Position;
                            await SavePosition();
                        }
                    }

                    // Save position periodically
                    if (DateTime.UtcNow - lastSaveTime > TimeSpan.FromSeconds(_isBulkProcessing ? 10 : 30))
                    {
                        _lastPosition = stream.Position;
                        await SavePosition();
                        lastSaveTime = DateTime.UtcNow;
                    }

                    // Report progress - more frequent during bulk processing
                    var progressInterval = _isBulkProcessing ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(10);
                    if (DateTime.UtcNow - lastReportTime > progressInterval)
                    {
                        if (_isBulkProcessing)
                        {
                            var percentComplete = (stream.Position * 100.0) / stream.Length;
                            var mbProcessed = stream.Position / (1024.0 * 1024.0);
                            var mbTotal = stream.Length / (1024.0 * 1024.0);
                            
                            _logger.LogInformation($"Bulk processing: {percentComplete:F1}% ({mbProcessed:F1}/{mbTotal:F1} MB) - {entriesProcessed} entries from {linesProcessed} lines");
                            
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
                                    { "linesProcessed", linesProcessed }
                                });
                            }
                            catch { }
                            
                            // Send progress update to clients
                            await _hubContext.Clients.All.SendAsync("ProcessingProgress", new {
                                percentComplete,
                                mbProcessed,
                                mbTotal,
                                entriesProcessed,
                                linesProcessed,
                                timestamp = DateTime.UtcNow
                            });
                        }
                        else
                        {
                            if (linesProcessed > 0)
                            {
                                _logger.LogDebug($"Processed {linesProcessed} lines, {entriesProcessed} entries");
                            }
                        }
                        lastReportTime = DateTime.UtcNow;
                    }
                }
                else
                {
                    // End of current file content
                    
                    // Process remaining buffer
                    if (_batchBuffer.Count > 0)
                    {
                        await ProcessBatch(_batchBuffer.ToList(), !_isBulkProcessing);
                        _batchBuffer.Clear();
                    }
                    
                    // Update position
                    _lastPosition = stream.Position;
                    await SavePosition();
                    
                    // Check if bulk processing is complete
                    if (_isBulkProcessing)
                    {
                        var fileInfo = new FileInfo(_logPath);
                        if (stream.Position >= fileInfo.Length - 1000) // Within 1KB of end
                        {
                            var elapsed = DateTime.UtcNow - bulkStartTime;
                            _logger.LogInformation($"Bulk processing completed in {elapsed.TotalMinutes:F1} minutes");
                            _logger.LogInformation($"Processed {entriesProcessed} entries from {linesProcessed} lines");
                            
                            _isBulkProcessing = false;
                            
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
                            
                            // Clean up operation state
                            try
                            {
                                using var scope = _serviceProvider.CreateScope();
                                var stateService = scope.ServiceProvider.GetRequiredService<OperationStateService>();
                                stateService.RemoveState("activeLogProcessing");
                            }
                            catch { }
                            
                            // Notify clients
                            await _hubContext.Clients.All.SendAsync("BulkProcessingComplete");
                            
                            // Return to restart the loop in normal mode
                            return;
                        }
                    }
                    
                    _consecutiveEmptyReads++;
                    
                    // Check if file has grown
                    var currentFileInfo = new FileInfo(_logPath);
                    if (currentFileInfo.Length > stream.Length)
                    {
                        continue; // File has grown, continue reading
                    }
                    
                    // Wait for new data
                    await Task.Delay(_isBulkProcessing ? 100 : 1000, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing log line at position {stream.Position}");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task ProcessBatch(List<LogEntry> entries, bool sendRealtimeUpdates)
    {
        if (entries.Count == 0) return;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();

            // Group by client and service for efficiency
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
}