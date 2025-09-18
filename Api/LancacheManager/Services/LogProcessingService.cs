using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using LancacheManager.Hubs;
using LancacheManager.Models;

namespace LancacheManager.Services;

/// <summary>
/// Simple log processing service
/// </summary>
public class LogProcessingService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LogProcessingService> _logger;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly LogParserService _parser;

    // Simple processing queue
    private readonly Channel<string> _logChannel;
    private readonly List<LogEntry> _batchBuffer = new();
    private readonly object _batchLock = new();
    private Timer? _batchTimer;

    public LogProcessingService(
        IServiceProvider serviceProvider,
        ILogger<LogProcessingService> logger,
        IHubContext<DownloadHub> hubContext,
        LogParserService parser)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _hubContext = hubContext;
        _parser = parser;

        // Simple unbounded channel
        _logChannel = Channel.CreateUnbounded<string>();

        _logger.LogInformation("LogProcessingService initialized");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Start batch timer (flush every 5 seconds)
        _batchTimer = new Timer(async _ => await FlushBatch(), null, 5000, 5000);

        // Process log lines
        await ProcessLogLines(stoppingToken);
    }

    /// <summary>
    /// Add a raw log line to the processing queue
    /// </summary>
    public async Task<bool> EnqueueLogLine(string logLine)
    {
        try
        {
            return _logChannel.Writer.TryWrite(logLine);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enqueueing log line");
            return false;
        }
    }

    /// <summary>
    /// Process log lines sequentially
    /// </summary>
    private async Task ProcessLogLines(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Log processor started");

        await foreach (var logLine in _logChannel.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                // Parse the log line
                var entry = _parser.ParseLine(logLine);
                if (entry != null && entry.BytesServed > 0)
                {
                    lock (_batchLock)
                    {
                        _batchBuffer.Add(entry);

                        // Process batch when it reaches 100 entries
                        if (_batchBuffer.Count >= 100)
                        {
                            var batch = _batchBuffer.ToList();
                            _batchBuffer.Clear();

                            // Process batch asynchronously
                            Task.Run(async () => await ProcessBatch(batch), cancellationToken);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing log line");
            }
        }

        _logger.LogInformation("Log processor stopped");
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
            await ProcessBatch(batch);
        }
    }

    /// <summary>
    /// Process a batch of log entries
    /// </summary>
    private async Task ProcessBatch(List<LogEntry> entries)
    {
        if (entries.Count == 0) return;

        _logger.LogDebug($"Processing batch of {entries.Count} entries");

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
                    await dbService.ProcessLogEntryBatch(group.ToList(), true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error processing batch for {group.Key.ClientIp}/{group.Key.Service}");
                }
            }

            // Send real-time updates
            await SendRealtimeUpdate(entries);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing batch");
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping LogProcessingService");

        // Stop accepting new items
        _logChannel.Writer.TryComplete();

        // Stop batch timer
        _batchTimer?.Dispose();

        // Flush remaining batch
        await FlushBatch();

        await base.StopAsync(cancellationToken);
    }
}