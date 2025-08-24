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
    private long _lastPosition = -1; // Start with -1 to distinguish from 0
    private readonly string _positionFile = "/data/logposition.txt";
    private readonly string _processingMarker = "/data/bulk_processing.marker";
    private int _consecutiveEmptyReads = 0;
    private bool _isPreloading = false;
    private readonly List<LogEntry> _preloadBuffer = new();
    private bool _isBulkProcessing = false;

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
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var logPath = _configuration["LanCache:LogPath"] ?? "/logs/access.log";
        
        // Wait for file to exist
        while (!File.Exists(logPath) && !stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation($"Waiting for log file: {logPath}");
            await Task.Delay(10000, stoppingToken);
        }

        var fileInfo = new FileInfo(logPath);
        
        // Check for bulk processing marker FIRST
        bool hasBulkProcessingMarker = File.Exists(_processingMarker);
        
        // Load saved position
        await LoadPosition();
        
        // Determine starting position based on marker and position
        if (hasBulkProcessingMarker)
        {
            // Bulk processing was requested
            if (_lastPosition <= 1 || _lastPosition == -1)
            {
                // Start from beginning for bulk processing
                _lastPosition = 0;
                _logger.LogInformation("Bulk processing marker found - starting from beginning of log file");
                _logger.LogInformation($"This will process {fileInfo.Length:N0} bytes ({fileInfo.Length / 1024.0 / 1024.0:F1} MB)");
                _isBulkProcessing = true;
                _isPreloading = true;
            }
            else if (_lastPosition < fileInfo.Length)
            {
                // Resume bulk processing from saved position
                _logger.LogInformation($"Resuming bulk processing from position: {_lastPosition:N0}");
                _logger.LogInformation($"Remaining: {(fileInfo.Length - _lastPosition) / 1024.0 / 1024.0:F1} MB to process");
                _isBulkProcessing = true;
                _isPreloading = true;
            }
            else
            {
                // Bulk processing complete, remove marker
                _logger.LogInformation("Bulk processing complete - removing marker");
                try
                {
                    File.Delete(_processingMarker);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error removing bulk processing marker");
                }
                _isBulkProcessing = false;
                _isPreloading = false;
            }
        }
        else if (_lastPosition >= 0 && _lastPosition <= fileInfo.Length)
        {
            // Normal operation - use saved position
            _logger.LogInformation($"Resuming from saved position: {_lastPosition:N0}");
            
            // Check if we're far behind (more than 100MB to process)
            if (fileInfo.Length - _lastPosition > 100_000_000)
            {
                _logger.LogInformation($"Large backlog detected: {(fileInfo.Length - _lastPosition) / 1024.0 / 1024.0:F1} MB to process");
                _isBulkProcessing = true;
                _isPreloading = true;
            }
        }
        else
        {
            // No valid position saved or position is beyond file - start from end
            var startFromEnd = _configuration.GetValue<bool>("LanCache:StartFromEndOfLog", true);
            
            if (startFromEnd)
            {
                _lastPosition = fileInfo.Length;
                _logger.LogInformation($"Starting from END of log file at position {_lastPosition:N0}");
                _logger.LogInformation($"Will only process NEW log entries from this point forward");
            }
            else
            {
                _lastPosition = 0;
                _logger.LogInformation("Starting from beginning of log file");
                _isBulkProcessing = true;
                _isPreloading = true;
            }
        }
        
        await SavePosition();
        
        // Start processing
        await ProcessLogFile(logPath, stoppingToken);
    }

    private async Task ProcessLogFile(string path, CancellationToken stoppingToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096);

        // Seek to last position
        if (_lastPosition > 0 && _lastPosition <= stream.Length)
        {
            stream.Seek(_lastPosition, SeekOrigin.Begin);
            
            // If we're not at the end, skip the partial line
            if (_lastPosition < stream.Length)
            {
                await reader.ReadLineAsync();
            }
            
            if (_isBulkProcessing)
            {
                _logger.LogInformation($"Starting bulk processing from position {_lastPosition:N0}");
            }
            else
            {
                _logger.LogInformation($"Monitoring for new entries from position {_lastPosition:N0}");
            }
        }
        else if (_lastPosition == 0)
        {
            // Starting from beginning
            _logger.LogInformation("Starting from beginning of file");
        }
        else if (_lastPosition > stream.Length)
        {
            // File was rotated or truncated, start from beginning
            _lastPosition = 0;
            _logger.LogWarning("Log file appears to have been rotated, starting from beginning");
        }

        var batchEntries = new List<LogEntry>();
        var linesProcessed = 0;
        var totalLinesProcessed = 0;
        var lastReportTime = DateTime.UtcNow;
        var bulkStartTime = DateTime.UtcNow;
        var lastSaveTime = DateTime.UtcNow;
        var entriesProcessed = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var line = await reader.ReadLineAsync();
                
                if (line != null)
                {
                    _consecutiveEmptyReads = 0;
                    linesProcessed++;
                    totalLinesProcessed++;

                    var entry = _parser.ParseLine(line);
                    if (entry != null && entry.BytesServed > 1024) // Only process entries > 1KB
                    {
                        entriesProcessed++;
                        
                        if (_isBulkProcessing)
                        {
                            _preloadBuffer.Add(entry);
                        }
                        else
                        {
                            batchEntries.Add(entry);
                        }
                    }

                    // During bulk processing, use larger batches
                    var batchSize = _isBulkProcessing ? 1000 : 50;
                    var linesBatchSize = _isBulkProcessing ? 5000 : 100;

                    // Process batch when ready
                    if (!_isBulkProcessing && (batchEntries.Count >= batchSize || linesProcessed >= linesBatchSize))
                    {
                        if (batchEntries.Count > 0)
                        {
                            await ProcessBatch(batchEntries, true); // Send real-time updates
                            batchEntries.Clear();
                        }
                        
                        _lastPosition = stream.Position;
                        await SavePosition();
                        linesProcessed = 0;
                    }

                    // During bulk processing, process in larger chunks
                    if (_isBulkProcessing && _preloadBuffer.Count >= batchSize)
                    {
                        _logger.LogInformation($"Processing batch of {_preloadBuffer.Count} entries (position: {stream.Position:N0}/{stream.Length:N0})");
                        await ProcessBatch(_preloadBuffer.ToList(), false); // No SignalR updates during bulk
                        _preloadBuffer.Clear();
                        
                        _lastPosition = stream.Position;
                        
                        // Save position every 30 seconds during bulk processing
                        if (DateTime.UtcNow - lastSaveTime > TimeSpan.FromSeconds(30))
                        {
                            await SavePosition();
                            lastSaveTime = DateTime.UtcNow;
                            
                            var percentComplete = (stream.Position * 100.0) / stream.Length;
                            var mbProcessed = stream.Position / (1024.0 * 1024.0);
                            var mbTotal = stream.Length / (1024.0 * 1024.0);
                            var elapsed = DateTime.UtcNow - bulkStartTime;
                            var rate = mbProcessed / elapsed.TotalSeconds;
                            
                            _logger.LogInformation($"Bulk processing: {percentComplete:F1}% complete ({mbProcessed:F1}/{mbTotal:F1} MB) at {rate:F1} MB/s");
                            _logger.LogInformation($"Processed {entriesProcessed} download entries from {totalLinesProcessed} lines");
                        }
                    }

                    // Report progress periodically
                    if (DateTime.UtcNow - lastReportTime > TimeSpan.FromSeconds(30))
                    {
                        if (_isBulkProcessing)
                        {
                            var percentComplete = (stream.Position * 100.0) / stream.Length;
                            _logger.LogInformation($"Progress: {percentComplete:F1}% - {entriesProcessed} entries from {totalLinesProcessed} lines");
                        }
                        else
                        {
                            _logger.LogDebug($"Processed {linesProcessed} lines, position: {_lastPosition:N0}");
                        }
                        lastReportTime = DateTime.UtcNow;
                    }
                }
                else
                {
                    // No new data - end of current file content
                    
                    // Process any remaining buffered entries
                    if (_isBulkProcessing && _preloadBuffer.Count > 0)
                    {
                        _logger.LogInformation($"Processing final batch of {_preloadBuffer.Count} entries");
                        await ProcessBatch(_preloadBuffer.ToList(), false);
                        _preloadBuffer.Clear();
                    }
                    
                    // Check if bulk processing is complete
                    if (_isBulkProcessing && stream.Position >= stream.Length - 100)
                    {
                        var bulkTime = DateTime.UtcNow - bulkStartTime;
                        _logger.LogInformation($"Bulk processing completed in {bulkTime.TotalMinutes:F1} minutes");
                        _logger.LogInformation($"Total: Processed {entriesProcessed} download entries from {totalLinesProcessed} lines");
                        
                        _isBulkProcessing = false;
                        _isPreloading = false;
                        
                        // Remove the bulk processing marker
                        if (File.Exists(_processingMarker))
                        {
                            try
                            {
                                File.Delete(_processingMarker);
                                _logger.LogInformation("Removed bulk processing marker");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error removing bulk processing marker");
                            }
                        }
                        
                        // Notify clients that bulk processing is complete
                        await _hubContext.Clients.All.SendAsync("BulkProcessingComplete");
                    }
                    
                    // Process any remaining batch entries
                    if (batchEntries.Count > 0)
                    {
                        await ProcessBatch(batchEntries, true);
                        batchEntries.Clear();
                    }

                    _lastPosition = stream.Position;
                    await SavePosition();

                    _consecutiveEmptyReads++;
                    
                    // Only log "waiting" message occasionally
                    if (_consecutiveEmptyReads % 60 == 1) // Every minute
                    {
                        _logger.LogDebug($"Waiting for new log entries... (position: {_lastPosition:N0})");
                    }

                    // Check if file has grown
                    var currentFileInfo = new FileInfo(path);
                    if (currentFileInfo.Length > stream.Length)
                    {
                        // File has grown, continue immediately
                        continue;
                    }

                    await Task.Delay(1000, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing log line");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task ProcessBatch(List<LogEntry> entries, bool sendRealtimeUpdates)
    {
        if (entries.Count == 0) return;

        _logger.LogDebug($"Processing batch of {entries.Count} log entries (realtime: {sendRealtimeUpdates})");

        using var scope = _serviceProvider.CreateScope();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();

        // Group entries by client and service to reduce database operations
        var groupedEntries = entries.GroupBy(e => new { e.ClientIp, e.Service });

        foreach (var group in groupedEntries)
        {
            try
            {
                // Process all entries for this client/service combination
                await dbService.ProcessLogEntryBatch(group.ToList(), sendRealtimeUpdates);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing batch for {group.Key.ClientIp}/{group.Key.Service}");
            }
        }
    }

    private async Task SavePosition()
    {
        try
        {
            var dir = Path.GetDirectoryName(_positionFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
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
                    _logger.LogWarning($"Could not parse position file content: {content}");
                }
            }
            else
            {
                _logger.LogInformation("No saved position file found");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading position");
        }
    }
}