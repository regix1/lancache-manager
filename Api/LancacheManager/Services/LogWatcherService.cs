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
    private FileSystemWatcher? _markerWatcher;

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
        _logger.LogInformation("Processing marker detected via file watcher - triggering bulk processing");
        _lastPosition = 0;
        _isBulkProcessing = true;
        _lastMarkerCheck = DateTime.UtcNow;
    }

    private async Task<bool> CheckForProcessingMarker()
    {
        try
        {
            // Check every 2 seconds for marker
            if (DateTime.UtcNow - _lastMarkerCheck < TimeSpan.FromSeconds(2))
                return false;
                
            _lastMarkerCheck = DateTime.UtcNow;
            
            bool markerExists = File.Exists(_processingMarker);
            
            if (markerExists && !_isBulkProcessing)
            {
                _logger.LogInformation("Found bulk processing marker - checking position");
                
                // Read the position file to confirm it's been reset
                if (File.Exists(_positionFile))
                {
                    var content = await File.ReadAllTextAsync(_positionFile);
                    if (long.TryParse(content, out var position))
                    {
                        if (position == 0)
                        {
                            _logger.LogInformation("Position confirmed at 0 - starting bulk processing");
                            _lastPosition = 0;
                            _isBulkProcessing = true;
                            return true;
                        }
                        else
                        {
                            _logger.LogWarning($"Marker exists but position is {position}, not 0");
                        }
                    }
                }
                else
                {
                    // No position file, start from beginning
                    _logger.LogInformation("No position file found with marker - starting from beginning");
                    _lastPosition = 0;
                    _isBulkProcessing = true;
                    await SavePosition();
                    return true;
                }
            }
            else if (!markerExists && _isBulkProcessing)
            {
                _logger.LogInformation("Bulk processing marker removed - returning to normal mode");
                _isBulkProcessing = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking for processing marker");
        }
        
        return false;
    }

    private async Task InitializePosition()
    {
        var fileInfo = new FileInfo(_logPath);
        
        _logger.LogInformation($"Log file size: {fileInfo.Length / 1024.0 / 1024.0:F1} MB");
        
        // Check for bulk processing marker FIRST
        bool hasBulkProcessingMarker = File.Exists(_processingMarker);
        
        if (hasBulkProcessingMarker)
        {
            _logger.LogInformation("Found bulk processing marker at initialization");
            
            // Force position to 0 for bulk processing
            _lastPosition = 0;
            _isBulkProcessing = true;
            await SavePosition();
            
            _logger.LogInformation($"Bulk processing mode enabled: {fileInfo.Length / 1024.0 / 1024.0:F1} MB to process");
            return;
        }
        
        // Normal initialization - load saved position
        await LoadPosition();
        
        _logger.LogInformation($"Loaded position: {_lastPosition:N0} (File size: {fileInfo.Length:N0})");
        
        if (_lastPosition >= 0 && _lastPosition <= fileInfo.Length)
        {
            var behind = fileInfo.Length - _lastPosition;
            _logger.LogInformation($"Starting from saved position: {_lastPosition:N0} ({behind / 1024.0 / 1024.0:F1} MB behind)");
            
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
            
            await SavePosition();
        }
    }

    private async Task ProcessLogFile(CancellationToken stoppingToken)
    {
        int retryCount = 0;
        const int maxRetries = 3;
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Check for marker changes more aggressively
                await CheckForProcessingMarker();
                
                await ProcessLogFileInternal(stoppingToken);
                retryCount = 0; // Reset retry count on success
            }
            catch (Exception ex)
            {
                retryCount++;
                _logger.LogError(ex, $"Error in main processing loop (attempt {retryCount}/{maxRetries})");
                
                if (retryCount >= maxRetries)
                {
                    _logger.LogError("Max retries reached, waiting 30 seconds before retrying");
                    await Task.Delay(30000, stoppingToken);
                    retryCount = 0;
                }
                else
                {
                    await Task.Delay(5000, stoppingToken);
                }
            }
        }
    }

    private async Task ProcessLogFileInternal(CancellationToken stoppingToken)
    {
        var bufferSize = _isBulkProcessing ? 65536 : 4096;
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
            stream.Seek(0, SeekOrigin.End);
        }

        var lastReportTime = DateTime.UtcNow;
        var lastSaveTime = DateTime.UtcNow;
        var linesProcessed = 0;
        var entriesProcessed = 0;
        var bulkStartTime = DateTime.UtcNow;
        var lastProgressUpdate = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Check for marker changes frequently during processing
                if (DateTime.UtcNow - _lastMarkerCheck > TimeSpan.FromSeconds(1))
                {
                    await CheckForProcessingMarker();
                }

                var line = await reader.ReadLineAsync();
                
                if (!string.IsNullOrEmpty(line))
                {
                    _consecutiveEmptyReads = 0;
                    linesProcessed++;

                    var entry = _parser.ParseLine(line);
                    
                    if (entry != null && entry.BytesServed > 0)
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

                    // Save position more frequently during bulk processing
                    if (_isBulkProcessing && DateTime.UtcNow - lastSaveTime > TimeSpan.FromSeconds(5))
                    {
                        _lastPosition = stream.Position;
                        await SavePosition();
                        lastSaveTime = DateTime.UtcNow;
                    }

                    // Report progress more frequently
                    if (_isBulkProcessing && DateTime.UtcNow - lastProgressUpdate > TimeSpan.FromSeconds(1))
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
                            
                            var state = stateService.GetState("activeLogProcessing");
                            if (state != null)
                            {
                                stateService.UpdateState("activeLogProcessing", new Dictionary<string, object>
                                {
                                    { "percentComplete", percentComplete },
                                    { "mbProcessed", mbProcessed },
                                    { "mbTotal", mbTotal },
                                    { "entriesProcessed", entriesProcessed },
                                    { "linesProcessed", linesProcessed }
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to update operation state");
                        }
                        
                        // Send progress update to clients
                        await _hubContext.Clients.All.SendAsync("ProcessingProgress", new {
                            percentComplete,
                            mbProcessed,
                            mbTotal,
                            entriesProcessed,
                            linesProcessed,
                            timestamp = DateTime.UtcNow
                        });
                        
                        lastProgressUpdate = DateTime.UtcNow;
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
                            
                            return; // Exit to restart in normal mode
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

    public override void Dispose()
    {
        _markerWatcher?.Dispose();
        base.Dispose();
    }
}