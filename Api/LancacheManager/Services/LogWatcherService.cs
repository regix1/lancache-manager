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

    private async Task InitializePosition()
    {
        var fileInfo = new FileInfo(_logPath);
        
        // Check for bulk processing marker
        bool hasBulkProcessingMarker = File.Exists(_processingMarker);
        
        // Load saved position
        await LoadPosition();
        
        // Determine starting position
        if (hasBulkProcessingMarker)
        {
            if (_lastPosition <= 0 || _lastPosition >= fileInfo.Length)
            {
                // Start from beginning for bulk processing
                _lastPosition = 0;
                _logger.LogInformation($"Bulk processing: starting from beginning ({fileInfo.Length / 1024.0 / 1024.0:F1} MB to process)");
            }
            else
            {
                // Resume bulk processing
                _logger.LogInformation($"Bulk processing: resuming from {_lastPosition:N0} ({(fileInfo.Length - _lastPosition) / 1024.0 / 1024.0:F1} MB remaining)");
            }
            _isBulkProcessing = true;
        }
        else if (_lastPosition >= 0 && _lastPosition <= fileInfo.Length)
        {
            // Normal operation - use saved position
            _logger.LogInformation($"Starting from saved position: {_lastPosition:N0}");
            
            // Auto-enable bulk mode if we're far behind
            if (fileInfo.Length - _lastPosition > 100_000_000) // 100MB behind
            {
                _logger.LogInformation($"Large backlog detected: {(fileInfo.Length - _lastPosition) / 1024.0 / 1024.0:F1} MB to process");
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
        var bufferSize = _isBulkProcessing ? 65536 : 4096; // Larger buffer for bulk processing
        using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, bufferSize);

        // Seek to last position
        if (_lastPosition > 0)
        {
            stream.Seek(_lastPosition, SeekOrigin.Begin);
            
            // Skip partial line if not at end
            if (_lastPosition < stream.Length)
            {
                await reader.ReadLineAsync();
            }
        }

        var lastReportTime = DateTime.UtcNow;
        var lastSaveTime = DateTime.UtcNow;
        var linesProcessed = 0;
        var entriesProcessed = 0;
        var bulkStartTime = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var line = await reader.ReadLineAsync();
                
                if (!string.IsNullOrEmpty(line))
                {
                    _consecutiveEmptyReads = 0;
                    linesProcessed++;

                    var entry = _parser.ParseLine(line);
                    if (entry != null && entry.BytesServed > 1024) // Only process entries > 1KB
                    {
                        entriesProcessed++;
                        _batchBuffer.Add(entry);
                    }

                    // Process batch when ready
                    var batchSize = _isBulkProcessing ? 1000 : 50;
                    
                    if (_batchBuffer.Count >= batchSize)
                    {
                        await ProcessBatch(_batchBuffer.ToList(), !_isBulkProcessing);
                        _batchBuffer.Clear();
                    }

                    // Save position periodically
                    if (DateTime.UtcNow - lastSaveTime > TimeSpan.FromSeconds(_isBulkProcessing ? 10 : 30))
                    {
                        _lastPosition = stream.Position;
                        await SavePosition();
                        lastSaveTime = DateTime.UtcNow;
                    }

                    // Report progress
                    if (DateTime.UtcNow - lastReportTime > TimeSpan.FromSeconds(10))
                    {
                        if (_isBulkProcessing)
                        {
                            var percentComplete = (stream.Position * 100.0) / stream.Length;
                            var mbProcessed = stream.Position / (1024.0 * 1024.0);
                            var mbTotal = stream.Length / (1024.0 * 1024.0);
                            
                            _logger.LogInformation($"Bulk processing: {percentComplete:F1}% ({mbProcessed:F1}/{mbTotal:F1} MB) - {entriesProcessed} entries from {linesProcessed} lines");
                        }
                        else
                        {
                            _logger.LogDebug($"Processed {linesProcessed} lines, {entriesProcessed} entries");
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
                            
                            // Notify clients
                            await _hubContext.Clients.All.SendAsync("BulkProcessingComplete");
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