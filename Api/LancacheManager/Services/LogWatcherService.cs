using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using LancacheManager.Hubs;
using LancacheManager.Models;

namespace LancacheManager.Services;

/// <summary>
/// High-throughput log processing service capable of handling thousands of lines per second.
/// Uses Channel-based architecture for efficient producer-consumer pattern.
/// </summary>
public class LogWatcherService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly LogParserService _parser;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LogWatcherService> _logger;
    private readonly IHubContext<DownloadHub> _hubContext;
    
    // File tracking
    private readonly string _logPath;
    private readonly string _positionFile = "/data/logposition.txt";
    private readonly string _processingMarker = "/data/bulk_processing.marker";
    private long _lastPosition = -1;
    private FileSystemWatcher? _fileWatcher;
    
    // Channel for high-throughput processing
    private readonly Channel<ParsedLogEntry> _logChannel;
    private readonly Channel<LogBatch> _batchChannel;
    
    // Processing state
    private volatile bool _isBulkProcessing = false;
    private readonly CancellationTokenSource _internalCts = new();
    
    // Configuration
    private readonly int _channelCapacity;
    private readonly int _batchSize;
    private readonly int _batchTimeoutMs;
    private readonly int _consumerCount;
    
    // Statistics
    private readonly ConcurrentDictionary<string, long> _stats = new();
    private DateTime _processingStartTime;
    private long _totalLinesProcessed = 0;
    private long _totalEntriesProcessed = 0;

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
        
        // Performance configuration
        _channelCapacity = configuration.GetValue("LanCache:ChannelCapacity", 100_000);
        _batchSize = configuration.GetValue("LanCache:BatchSize", 5_000);
        _batchTimeoutMs = configuration.GetValue("LanCache:BatchTimeoutMs", 500);
        _consumerCount = configuration.GetValue("LanCache:ConsumerCount", Environment.ProcessorCount);
        
        // Create bounded channels for backpressure
        _logChannel = Channel.CreateBounded<ParsedLogEntry>(new BoundedChannelOptions(_channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false
        });
        
        _batchChannel = Channel.CreateBounded<LogBatch>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = false
        });
        
        _logger.LogInformation($"LogWatcherService initialized: capacity={_channelCapacity}, batch={_batchSize}, timeout={_batchTimeoutMs}ms, consumers={_consumerCount}");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for log file to exist
        await WaitForLogFile(stoppingToken);
        
        if (stoppingToken.IsCancellationRequested)
            return;

        // Initialize position
        await InitializePosition();
        
        // Set up file watcher for rotation/truncation detection
        SetupFileWatcher();
        
        // Create linked token source
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _internalCts.Token);
        var linkedToken = linkedCts.Token;
        
        // Start all processing tasks
        var tasks = new List<Task>
        {
            // File tailer (producer)
            TailFileAsync(linkedToken),
            
            // Batch aggregator
            AggregateBatchesAsync(linkedToken),
            
            // Progress reporter
            ReportProgressAsync(linkedToken)
        };
        
        // Add multiple parallel consumers
        for (int i = 0; i < _consumerCount; i++)
        {
            var consumerId = i;
            tasks.Add(ProcessBatchesAsync(consumerId, linkedToken));
        }
        
        // Add multiple batch processors
        var processorCount = Math.Max(2, _consumerCount / 2);
        for (int i = 0; i < processorCount; i++)
        {
            var processorId = i;
            tasks.Add(ProcessDatabaseBatchesAsync(processorId, linkedToken));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("LogWatcherService stopping gracefully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in LogWatcherService");
        }
    }

    /// <summary>
    /// Continuously tails the log file, reading new lines as they appear.
    /// This is the producer that feeds the channel.
    /// </summary>
    private async Task TailFileAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation($"Starting file tailer from position {_lastPosition}");
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 
                    bufferSize: 131072); // 128KB buffer for high throughput
                using var reader = new StreamReader(stream, Encoding.UTF8, true, 131072);
                
                // Check for bulk processing marker
                if (File.Exists(_processingMarker) && !_isBulkProcessing)
                {
                    _logger.LogInformation("Bulk processing marker detected - starting from beginning");
                    _isBulkProcessing = true;
                    _lastPosition = 0;
                    _processingStartTime = DateTime.UtcNow;
                    await CreateOperationState(stream.Length);
                }
                
                // Seek to position
                if (_lastPosition > 0 && _lastPosition < stream.Length)
                {
                    stream.Seek(_lastPosition, SeekOrigin.Begin);
                    // Skip partial line
                    if (_lastPosition > 0)
                        await reader.ReadLineAsync();
                }
                else if (_isBulkProcessing)
                {
                    stream.Seek(0, SeekOrigin.Begin);
                    _lastPosition = 0;
                }
                else
                {
                    stream.Seek(0, SeekOrigin.End);
                    _lastPosition = stream.Position;
                }
                
                _logger.LogInformation($"Tailing from position {stream.Position}/{stream.Length} (bulk={_isBulkProcessing})");
                
                var consecutiveEmptyReads = 0;
                var lineBuffer = new List<string>(1000);
                
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    
                    if (line != null)
                    {
                        consecutiveEmptyReads = 0;
                        lineBuffer.Add(line);
                        Interlocked.Increment(ref _totalLinesProcessed);
                        
                        // Process buffer when it gets large enough
                        if (lineBuffer.Count >= 100)
                        {
                            await ProcessLineBuffer(lineBuffer, cancellationToken);
                            lineBuffer.Clear();
                        }
                        
                        _lastPosition = stream.Position;
                    }
                    else
                    {
                        // Process any remaining lines
                        if (lineBuffer.Count > 0)
                        {
                            await ProcessLineBuffer(lineBuffer, cancellationToken);
                            lineBuffer.Clear();
                        }
                        
                        // Save position periodically
                        await SavePositionAtomic();
                        
                        // Check if bulk processing is complete
                        if (_isBulkProcessing)
                        {
                            var fileInfo = new FileInfo(_logPath);
                            if (stream.Position >= fileInfo.Length - 1000 || consecutiveEmptyReads > 10)
                            {
                                await CompleteBulkProcessing();
                                _isBulkProcessing = false;
                            }
                        }
                        
                        // Check for file growth
                        var currentLength = new FileInfo(_logPath).Length;
                        if (currentLength > stream.Length)
                        {
                            _logger.LogDebug($"File grew from {stream.Length} to {currentLength}");
                            continue; // Keep reading
                        }
                        
                        consecutiveEmptyReads++;
                        
                        // Backoff when no new data
                        var delay = _isBulkProcessing ? 10 : Math.Min(consecutiveEmptyReads * 100, 1000);
                        await Task.Delay(delay, cancellationToken);
                    }
                }
            }
            catch (FileNotFoundException)
            {
                _logger.LogWarning("Log file not found, waiting...");
                await Task.Delay(5000, cancellationToken);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "IO error reading log file, retrying...");
                await Task.Delay(1000, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in file tailer");
                await Task.Delay(5000, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Processes a buffer of lines in parallel for maximum throughput
    /// </summary>
    private async Task ProcessLineBuffer(List<string> lines, CancellationToken cancellationToken)
    {
        var tasks = new List<Task>();
        var semaphore = new SemaphoreSlim(Environment.ProcessorCount * 2);
        
        foreach (var line in lines)
        {
            await semaphore.WaitAsync(cancellationToken);
            
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var entry = _parser.ParseLine(line);
                    if (entry != null && entry.BytesServed > 0)
                    {
                        var parsed = new ParsedLogEntry
                        {
                            Entry = entry,
                            LineNumber = Interlocked.Read(ref _totalLinesProcessed)
                        };
                        
                        await _logChannel.Writer.WriteAsync(parsed, cancellationToken);
                        Interlocked.Increment(ref _totalEntriesProcessed);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken));
        }
        
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Aggregates individual log entries into batches for efficient DB writes
    /// </summary>
    private async Task AggregateBatchesAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting batch aggregator");
        
        var batch = new Dictionary<string, List<LogEntry>>();
        var stopwatch = Stopwatch.StartNew();
        var itemCount = 0;
        
        await foreach (var item in _logChannel.Reader.ReadAllAsync(cancellationToken))
        {
            var key = $"{item.Entry.ClientIp}_{item.Entry.Service}";
            
            if (!batch.ContainsKey(key))
                batch[key] = new List<LogEntry>();
            
            batch[key].Add(item.Entry);
            itemCount++;
            
            // Send batch when size or time threshold is reached
            if (itemCount >= _batchSize || stopwatch.ElapsedMilliseconds >= _batchTimeoutMs)
            {
                if (batch.Count > 0)
                {
                    var logBatch = new LogBatch
                    {
                        Entries = batch,
                        Timestamp = DateTime.UtcNow,
                        IsBulkProcessing = _isBulkProcessing
                    };
                    
                    await _batchChannel.Writer.WriteAsync(logBatch, cancellationToken);
                    
                    batch = new Dictionary<string, List<LogEntry>>();
                    itemCount = 0;
                    stopwatch.Restart();
                }
            }
        }
        
        // Process final batch
        if (batch.Count > 0)
        {
            await _batchChannel.Writer.WriteAsync(new LogBatch 
            { 
                Entries = batch, 
                Timestamp = DateTime.UtcNow,
                IsBulkProcessing = _isBulkProcessing
            }, cancellationToken);
        }
    }

    /// <summary>
    /// Processes batches and sends them to the database
    /// Multiple instances run in parallel for maximum throughput
    /// </summary>
    private async Task ProcessBatchesAsync(int consumerId, CancellationToken cancellationToken)
    {
        _logger.LogInformation($"Starting batch consumer {consumerId}");
        
        await foreach (var batch in _batchChannel.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
                
                // Process each client/service group
                var tasks = batch.Entries.Select(async kvp =>
                {
                    try
                    {
                        await dbService.ProcessLogEntryBatch(kvp.Value, !batch.IsBulkProcessing);
                        
                        // Update statistics
                        var key = kvp.Key;
                        _stats.AddOrUpdate(key, kvp.Value.Count, (k, v) => v + kvp.Value.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error processing batch for {kvp.Key}");
                    }
                });
                
                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Consumer {consumerId} error processing batch");
            }
        }
    }

    /// <summary>
    /// Alternative batch processor for database writes
    /// </summary>
    private async Task ProcessDatabaseBatchesAsync(int processorId, CancellationToken cancellationToken)
    {
        _logger.LogInformation($"Starting database processor {processorId}");
        
        // This is a placeholder for additional processing logic
        // You could use this for secondary processing, caching, etc.
        await Task.CompletedTask;
    }

    /// <summary>
    /// Reports progress periodically for monitoring
    /// </summary>
    private async Task ReportProgressAsync(CancellationToken cancellationToken)
    {
        var lastReport = DateTime.UtcNow;
        var lastLinesReported = 0L;
        var lastEntriesReported = 0L;
        
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(2000, cancellationToken);
            
            var now = DateTime.UtcNow;
            var currentLines = Interlocked.Read(ref _totalLinesProcessed);
            var currentEntries = Interlocked.Read(ref _totalEntriesProcessed);
            
            if (currentLines > lastLinesReported || _isBulkProcessing)
            {
                var elapsed = now - lastReport;
                var linesPerSecond = (currentLines - lastLinesReported) / elapsed.TotalSeconds;
                var entriesPerSecond = (currentEntries - lastEntriesReported) / elapsed.TotalSeconds;
                
                _logger.LogInformation($"Processing rate: {linesPerSecond:F0} lines/sec, {entriesPerSecond:F0} entries/sec");
                _logger.LogInformation($"Channel status: {_logChannel.Reader.Count}/{_channelCapacity} items queued");
                
                if (_isBulkProcessing)
                {
                    await UpdateOperationState(currentLines, currentEntries);
                    await SendProgressNotification(currentLines, currentEntries);
                }
                
                lastReport = now;
                lastLinesReported = currentLines;
                lastEntriesReported = currentEntries;
            }
        }
    }

    // Helper methods

    private async Task WaitForLogFile(CancellationToken cancellationToken)
    {
        var attempts = 0;
        while (!File.Exists(_logPath) && !cancellationToken.IsCancellationRequested)
        {
            attempts++;
            _logger.LogInformation($"Waiting for log file: {_logPath} (attempt {attempts})");
            await Task.Delay(Math.Min(10000 * attempts, 60000), cancellationToken);
        }
    }

    private async Task InitializePosition()
    {
        if (File.Exists(_processingMarker))
        {
            _logger.LogInformation("Found processing marker - will start bulk processing");
            _isBulkProcessing = true;
            _lastPosition = 0;
            _processingStartTime = DateTime.UtcNow;
        }
        else if (File.Exists(_positionFile))
        {
            try
            {
                var content = await File.ReadAllTextAsync(_positionFile);
                if (long.TryParse(content, out var position))
                {
                    _lastPosition = position;
                    _logger.LogInformation($"Loaded position: {_lastPosition}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading position");
            }
        }
        
        if (_lastPosition < 0)
        {
            var startFromEnd = _configuration.GetValue("LanCache:StartFromEndOfLog", true);
            _lastPosition = startFromEnd ? new FileInfo(_logPath).Length : 0;
            _logger.LogInformation($"Initial position: {_lastPosition}");
        }
    }

    private async Task SavePositionAtomic()
    {
        try
        {
            var tempFile = _positionFile + ".tmp";
            await File.WriteAllTextAsync(tempFile, _lastPosition.ToString());
            File.Move(tempFile, _positionFile, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving position");
        }
    }

    private void SetupFileWatcher()
    {
        try
        {
            var dir = Path.GetDirectoryName(_logPath) ?? "/logs";
            var file = Path.GetFileName(_logPath);
            
            _fileWatcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            
            _fileWatcher.Deleted += OnFileDeleted;
            _fileWatcher.Created += OnFileCreated;
            
            _logger.LogInformation("File watcher initialized");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to setup file watcher");
        }
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        _logger.LogWarning("Log file deleted - resetting position");
        _lastPosition = 0;
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        _logger.LogInformation("Log file recreated - restarting tailer");
        _lastPosition = 0;
    }

    private async Task CreateOperationState(long fileSize)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var stateService = scope.ServiceProvider.GetRequiredService<OperationStateService>();
            
            var state = new OperationState
            {
                Key = "activeLogProcessing",
                Type = "log_processing",
                Status = "processing",
                Message = "Processing log file",
                Data = new Dictionary<string, object>
                {
                    { "startTime", DateTime.UtcNow },
                    { "fileSize", fileSize },
                    { "status", "processing" }
                },
                ExpiresAt = DateTime.UtcNow.AddHours(24)
            };
            
            stateService.SaveState("activeLogProcessing", state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create operation state");
        }
    }

    private async Task UpdateOperationState(long lines, long entries)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var stateService = scope.ServiceProvider.GetRequiredService<OperationStateService>();
            
            var fileSize = new FileInfo(_logPath).Length;
            var percentComplete = _lastPosition > 0 && fileSize > 0 
                ? (_lastPosition * 100.0) / fileSize 
                : 0;
            
            stateService.UpdateState("activeLogProcessing", new Dictionary<string, object>
            {
                { "percentComplete", percentComplete },
                { "linesProcessed", lines },
                { "entriesProcessed", entries },
                { "currentPosition", _lastPosition },
                { "isProcessing", true }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update operation state");
        }
    }

    private async Task SendProgressNotification(long lines, long entries)
    {
        try
        {
            var fileSize = new FileInfo(_logPath).Length;
            var percentComplete = _lastPosition > 0 && fileSize > 0 
                ? (_lastPosition * 100.0) / fileSize 
                : 0;
            
            await _hubContext.Clients.All.SendAsync("ProcessingProgress", new
            {
                percentComplete,
                mbProcessed = _lastPosition / (1024.0 * 1024.0),
                mbTotal = fileSize / (1024.0 * 1024.0),
                entriesProcessed = entries,
                linesProcessed = lines,
                timestamp = DateTime.UtcNow,
                status = "processing"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send progress notification");
        }
    }

    private async Task CompleteBulkProcessing()
    {
        var elapsed = DateTime.UtcNow - _processingStartTime;
        _logger.LogInformation($"Bulk processing completed in {elapsed.TotalMinutes:F1} minutes");
        _logger.LogInformation($"Processed {_totalLinesProcessed} lines, {_totalEntriesProcessed} entries");
        
        // Clean up marker
        if (File.Exists(_processingMarker))
        {
            try
            {
                File.Delete(_processingMarker);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete processing marker");
            }
        }
        
        // Clear operation state
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var stateService = scope.ServiceProvider.GetRequiredService<OperationStateService>();
            stateService.RemoveState("activeLogProcessing");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear operation state");
        }
        
        // Send completion notification
        try
        {
            await _hubContext.Clients.All.SendAsync("BulkProcessingComplete", new
            {
                entriesProcessed = _totalEntriesProcessed,
                linesProcessed = _totalLinesProcessed,
                elapsed = elapsed.TotalMinutes,
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send completion notification");
        }
    }

    public override void Dispose()
    {
        _internalCts?.Cancel();
        _internalCts?.Dispose();
        _fileWatcher?.Dispose();
        base.Dispose();
    }
}

// Helper classes
internal class ParsedLogEntry
{
    public LogEntry Entry { get; set; } = null!;
    public long LineNumber { get; set; }
}

internal class LogBatch
{
    public Dictionary<string, List<LogEntry>> Entries { get; set; } = new();
    public DateTime Timestamp { get; set; }
    public bool IsBulkProcessing { get; set; }
}