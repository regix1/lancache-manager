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
    private long _lastPosition = 0;
    private readonly string _positionFile = "/data/logposition.txt";
    private int _consecutiveEmptyReads = 0;
    private bool _isPreloading = true;
    private readonly List<LogEntry> _preloadBuffer = new();

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

        // ALWAYS start from the end of the file
        var fileInfo = new FileInfo(logPath);
        _lastPosition = fileInfo.Length;
        
        _logger.LogInformation($"Starting from END of log file at position {_lastPosition:N0}");
        _logger.LogInformation($"Will only process NEW log entries from this point forward");
        
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
            
            _logger.LogInformation($"Monitoring for new entries from position {_lastPosition:N0}");
        }
        else if (_lastPosition > stream.Length)
        {
            // File was rotated or truncated, start from beginning
            _lastPosition = 0;
            _logger.LogWarning("Log file appears to have been rotated, starting from beginning");
        }

        var batchEntries = new List<LogEntry>();
        var linesProcessed = 0;
        var lastReportTime = DateTime.UtcNow;
        var preloadStartTime = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var line = await reader.ReadLineAsync();
                
                if (line != null)
                {
                    _consecutiveEmptyReads = 0;
                    linesProcessed++;

                    var entry = _parser.ParseLine(line);
                    if (entry != null && entry.BytesServed > 1024) // Only process entries > 1KB
                    {
                        if (_isPreloading)
                        {
                            _preloadBuffer.Add(entry);
                        }
                        else
                        {
                            batchEntries.Add(entry);
                        }
                    }

                    // Process batch every 100 lines or 50 entries
                    if (!_isPreloading && (batchEntries.Count >= 50 || linesProcessed >= 100))
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

                    // Report progress every 30 seconds
                    if (DateTime.UtcNow - lastReportTime > TimeSpan.FromSeconds(30))
                    {
                        _logger.LogDebug($"Processed {linesProcessed} lines, position: {_lastPosition:N0}");
                        lastReportTime = DateTime.UtcNow;
                    }
                }
                else
                {
                    // No new data - end of current file content
                    
                    // If preloading, process everything we've buffered
                    if (_isPreloading)
                    {
                        if (_preloadBuffer.Count > 0)
                        {
                            _logger.LogInformation($"Preloading complete. Processing {_preloadBuffer.Count} buffered entries...");
                            
                            // Process all preloaded entries in batches WITHOUT real-time updates
                            var preloadBatches = _preloadBuffer.Chunk(100);
                            foreach (var batch in preloadBatches)
                            {
                                await ProcessBatch(batch.ToList(), false); // Don't send SignalR updates during preload
                            }
                            
                            _preloadBuffer.Clear();
                            
                            var preloadTime = DateTime.UtcNow - preloadStartTime;
                            _logger.LogInformation($"Preloading finished in {preloadTime.TotalSeconds:F1} seconds");
                            
                            // Notify clients that initial data is ready
                            await _hubContext.Clients.All.SendAsync("PreloadComplete");
                        }
                        
                        _isPreloading = false;
                        _logger.LogInformation("Now monitoring for real-time updates...");
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
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading position");
        }
    }
}