using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using LancacheManager.Hubs;
using LancacheManager.Models;

namespace LancacheManager.Services;

/// <summary>
/// High-performance log processing service with configurable parallelism
/// </summary>
public class LogProcessingService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LogProcessingService> _logger;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly LogParserService _parser;

    // Performance configuration
    private readonly int _channelCapacity;
    private readonly int _batchSize;
    private readonly int _batchTimeoutMs;
    private readonly int _consumerCount;
    private readonly int _parserParallelism;
    private readonly bool _useHighThroughputMode;

    // Processing channels
    private Channel<string> _rawLogChannel;
    private Channel<LogEntry> _parsedLogChannel;
    private readonly List<Task> _consumers = new();
    private readonly SemaphoreSlim _parserSemaphore;

    // Batch processing
    private readonly List<LogEntry> _batchBuffer = new();
    private readonly object _batchLock = new();
    private Timer? _batchTimer;

    public LogProcessingService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<LogProcessingService> logger,
        IHubContext<DownloadHub> hubContext,
        LogParserService parser)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
        _hubContext = hubContext;
        _parser = parser;

        // Load performance configuration
        _channelCapacity = configuration.GetValue<int>("LanCache:ChannelCapacity", 100000);
        _batchSize = configuration.GetValue<int>("LanCache:BatchSize", 5000);
        _batchTimeoutMs = configuration.GetValue<int>("LanCache:BatchTimeoutMs", 500);
        _consumerCount = configuration.GetValue<int>("LanCache:ConsumerCount", 4);
        _parserParallelism = configuration.GetValue<int>("LanCache:ParserParallelism", 8);
        _useHighThroughputMode = configuration.GetValue<bool>("LanCache:UseHighThroughputMode", false);

        // Initialize channels with configured capacity
        var channelOptions = new BoundedChannelOptions(_channelCapacity)
        {
            FullMode = _useHighThroughputMode ? BoundedChannelFullMode.Wait : BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        };

        _rawLogChannel = Channel.CreateBounded<string>(channelOptions);
        _parsedLogChannel = Channel.CreateBounded<LogEntry>(channelOptions);

        // Initialize parser semaphore for parallel parsing
        _parserSemaphore = new SemaphoreSlim(_parserParallelism, _parserParallelism);

        _logger.LogInformation($"LogProcessingService initialized with: ChannelCapacity={_channelCapacity}, BatchSize={_batchSize}, " +
            $"BatchTimeout={_batchTimeoutMs}ms, Consumers={_consumerCount}, ParserParallelism={_parserParallelism}, " +
            $"HighThroughputMode={_useHighThroughputMode}");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Start batch timer
        _batchTimer = new Timer(async _ => await FlushBatch(), null, _batchTimeoutMs, _batchTimeoutMs);

        // Start parser tasks
        var parserTasks = new List<Task>();
        for (int i = 0; i < _parserParallelism; i++)
        {
            int parserId = i;
            parserTasks.Add(Task.Run(async () => await ParseLogLines(parserId, stoppingToken), stoppingToken));
        }

        // Start consumer tasks
        for (int i = 0; i < _consumerCount; i++)
        {
            int consumerId = i;
            _consumers.Add(Task.Run(async () => await ConsumeLogEntries(consumerId, stoppingToken), stoppingToken));
        }

        // Wait for all tasks
        await Task.WhenAll(parserTasks.Concat(_consumers));
    }

    /// <summary>
    /// Add a raw log line to the processing queue
    /// </summary>
    public async Task<bool> EnqueueLogLine(string logLine)
    {
        try
        {
            if (_useHighThroughputMode)
            {
                // In high throughput mode, wait if channel is full
                await _rawLogChannel.Writer.WriteAsync(logLine);
            }
            else
            {
                // In normal mode, try to write without waiting
                return _rawLogChannel.Writer.TryWrite(logLine);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enqueueing log line");
            return false;
        }
    }

    /// <summary>
    /// Parse log lines in parallel
    /// </summary>
    private async Task ParseLogLines(int parserId, CancellationToken cancellationToken)
    {
        _logger.LogInformation($"Parser {parserId} started");

        await foreach (var logLine in _rawLogChannel.Reader.ReadAllAsync(cancellationToken))
        {
            await _parserSemaphore.WaitAsync(cancellationToken);
            try
            {
                // Parse the log line
                var entry = _parser.ParseLine(logLine);
                if (entry != null && entry.BytesServed > 0)
                {
                    await _parsedLogChannel.Writer.WriteAsync(entry, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Parser {parserId} error processing line");
            }
            finally
            {
                _parserSemaphore.Release();
            }
        }

        _logger.LogInformation($"Parser {parserId} stopped");
    }

    /// <summary>
    /// Consume parsed log entries and batch them for database processing
    /// </summary>
    private async Task ConsumeLogEntries(int consumerId, CancellationToken cancellationToken)
    {
        _logger.LogInformation($"Consumer {consumerId} started");

        await foreach (var entry in _parsedLogChannel.Reader.ReadAllAsync(cancellationToken))
        {
            lock (_batchLock)
            {
                _batchBuffer.Add(entry);

                // Check if batch is full
                if (_batchBuffer.Count >= _batchSize)
                {
                    var batch = _batchBuffer.ToList();
                    _batchBuffer.Clear();

                    // Process batch asynchronously
                    Task.Run(async () => await ProcessBatch(batch, consumerId), cancellationToken);
                }
            }
        }

        _logger.LogInformation($"Consumer {consumerId} stopped");
    }

    /// <summary>
    /// Flush batch based on timeout
    /// </summary>
    private async Task FlushBatch()
    {
        List<LogEntry>? batch = null;

        lock (_batchLock)
        {
            if (_batchBuffer.Count > 0)
            {
                batch = _batchBuffer.ToList();
                _batchBuffer.Clear();
                _logger.LogDebug($"Flushing batch of {batch.Count} entries due to timeout");
            }
        }

        if (batch != null)
        {
            await ProcessBatch(batch, -1); // -1 indicates timer-based flush
        }
    }

    /// <summary>
    /// Process a batch of log entries
    /// </summary>
    private async Task ProcessBatch(List<LogEntry> entries, int consumerId)
    {
        if (entries.Count == 0) return;

        var processorId = consumerId >= 0 ? $"Consumer-{consumerId}" : "Timer";
        _logger.LogDebug($"{processorId}: Processing batch of {entries.Count} entries");

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();

            // Group entries by client and service for efficient processing
            var grouped = entries.GroupBy(e => new { e.ClientIp, e.Service });

            foreach (var group in grouped)
            {
                try
                {
                    await dbService.ProcessLogEntryBatch(group.ToList(), !_useHighThroughputMode);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{processorId}: Error processing batch for {group.Key.ClientIp}/{group.Key.Service}");
                }
            }

            // Send real-time updates if not in high throughput mode
            if (!_useHighThroughputMode)
            {
                await SendRealtimeUpdate(entries);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"{processorId}: Error processing batch");
        }
    }

    /// <summary>
    /// Send real-time updates via SignalR
    /// </summary>
    private async Task SendRealtimeUpdate(List<LogEntry> entries)
    {
        try
        {
            var summary = entries.GroupBy(e => e.Service)
                .Select(g => new
                {
                    Service = g.Key,
                    Count = g.Count(),
                    TotalBytes = g.Sum(e => e.BytesServed)
                })
                .ToList();

            await _hubContext.Clients.All.SendAsync("BatchProcessed", new
            {
                Timestamp = DateTime.UtcNow,
                EntriesProcessed = entries.Count,
                Services = summary
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending real-time update");
        }
    }

    /// <summary>
    /// Get current queue statistics
    /// </summary>
    public object GetQueueStats()
    {
        return new
        {
            RawQueueCount = _rawLogChannel.Reader.Count,
            ParsedQueueCount = _parsedLogChannel.Reader.Count,
            BatchBufferCount = _batchBuffer.Count,
            ChannelCapacity = _channelCapacity,
            ActiveConsumers = _consumerCount,
            ActiveParsers = _parserParallelism,
            HighThroughputMode = _useHighThroughputMode
        };
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping LogProcessingService");

        // Stop accepting new items
        _rawLogChannel.Writer.TryComplete();
        _parsedLogChannel.Writer.TryComplete();

        // Stop batch timer
        _batchTimer?.Dispose();

        // Flush remaining batch
        await FlushBatch();

        // Wait for consumers to finish
        await Task.WhenAll(_consumers);

        await base.StopAsync(cancellationToken);
    }
}