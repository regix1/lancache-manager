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
    private Channel<string> _logChannel;
    private readonly List<LogEntry> _batchBuffer = new();
    private readonly object _batchLock = new();
    private Timer? _batchTimer;
    private readonly SemaphoreSlim _batchSemaphore = new(4, 4); // Increased to 4 concurrent batch operations for better performance
    private volatile bool _isRunning = false;
    private volatile bool _isBulkProcessing = false; // Track if we're in bulk processing mode

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

        // Create unbounded channel with better error handling
        var options = new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };
        _logChannel = Channel.CreateUnbounded<string>(options);

        _logger.LogInformation("LogProcessingService initialized");
    }

    /// <summary>
    /// Set bulk processing mode for optimized performance
    /// </summary>
    public void SetBulkProcessingMode(bool isBulkProcessing)
    {
        _isBulkProcessing = isBulkProcessing;
        _logger.LogInformation($"Bulk processing mode: {_isBulkProcessing}");
    }

    private void RecreateChannel()
    {
        // Recreate the channel in case it was previously completed
        var options = new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };
        _logChannel = Channel.CreateUnbounded<string>(options);
        _logger.LogDebug("Channel recreated for new processing session");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _isRunning = true;
        _logger.LogInformation("LogProcessingService starting...");

        try
        {
            // Recreate channel if it was previously completed
            RecreateChannel();

            // Start batch timer with different intervals based on processing mode
            var flushInterval = _isBulkProcessing ? 2000 : 5000; // 2 seconds for bulk, 5 seconds for real-time
            _batchTimer = new Timer(async _ => await SafeTimerFlush(), null, flushInterval, flushInterval);
            _logger.LogDebug($"Batch timer set to {flushInterval}ms intervals (bulk mode: {_isBulkProcessing})");

            // Process log lines
            await ProcessLogLines(stoppingToken);
        }
        finally
        {
            _isRunning = false;
            _logger.LogInformation("LogProcessingService stopped");
        }
    }

    /// <summary>
    /// Add a raw log line to the processing queue
    /// </summary>
    public async Task<bool> EnqueueLogLine(string logLine)
    {
        try
        {
            if (!_isRunning)
            {
                _logger.LogWarning("Cannot enqueue log line - LogProcessingService is not running");
                return false;
            }

            var result = _logChannel.Writer.TryWrite(logLine);
            if (!result)
            {
                _logger.LogWarning("Failed to enqueue log line - channel writer may be completed or service stopping");
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enqueueing log line: {Message}", ex.Message);
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

                        // Use different batch sizes based on processing mode
                        var batchSize = _isBulkProcessing ? 200 : 50; // Much larger batches for bulk processing
                        if (_batchBuffer.Count >= batchSize)
                        {
                            var batch = _batchBuffer.ToList();
                            _batchBuffer.Clear();

                            // Process batch with throttling to prevent overwhelming the system
                            _ = Task.Run(async () => await ProcessBatchWithThrottling(batch, cancellationToken), cancellationToken);
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
    /// Safe timer flush that handles disposal states
    /// </summary>
    private async Task SafeTimerFlush()
    {
        if (!_isRunning)
        {
            return; // Service is stopping, skip flush
        }

        try
        {
            await FlushBatch();
        }
        catch (ObjectDisposedException)
        {
            // Timer fired after disposal, ignore
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during timer flush");
        }
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
            await ProcessBatchWithThrottling(batch, CancellationToken.None);
        }
    }

    /// <summary>
    /// Safely flush batch with proper disposal handling
    /// </summary>
    private async Task FlushBatchSafe()
    {
        List<LogEntry>? batch = null;

        lock (_batchLock)
        {
            if (_batchBuffer.Count > 0)
            {
                batch = _batchBuffer.ToList();
                _batchBuffer.Clear();
                _logger.LogDebug($"Safely flushing batch of {batch.Count} entries during shutdown");
            }
        }

        if (batch != null)
        {
            await ProcessBatchSafelyDuringShutdown(batch);
        }
    }

    /// <summary>
    /// Process a batch with throttling to prevent resource exhaustion
    /// </summary>
    private async Task ProcessBatchWithThrottling(List<LogEntry> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0) return;

        // Don't process if service is stopping
        if (!_isRunning) return;

        try
        {
            await _batchSemaphore.WaitAsync(cancellationToken);

            try
            {
                // Check again after acquiring semaphore
                if (_isRunning)
                {
                    await ProcessBatch(entries);

                    // Add delay based on processing mode - minimal for bulk processing
                    var delay = _isBulkProcessing ? 10 : 100; // Much shorter delay for bulk processing
                    await Task.Delay(delay, cancellationToken);
                }
            }
            finally
            {
                _batchSemaphore.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            _logger.LogDebug("Semaphore disposed during batch processing - service is shutting down");
            // Don't process during shutdown
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Batch processing cancelled");
        }
    }

    /// <summary>
    /// Process a batch safely during shutdown without using disposed resources
    /// </summary>
    private async Task ProcessBatchSafelyDuringShutdown(List<LogEntry> entries)
    {
        if (entries.Count == 0) return;

        try
        {
            await ProcessBatch(entries);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing final batch during shutdown");
        }
    }

    /// <summary>
    /// Process a batch of log entries
    /// </summary>
    private async Task ProcessBatch(List<LogEntry> entries)
    {
        if (entries.Count == 0) return;

        // Don't process if service is stopping
        if (!_isRunning) return;

        _logger.LogDebug($"Processing batch of {entries.Count} entries");

        try
        {
            // Check if service provider is still available
            if (_serviceProvider == null)
            {
                _logger.LogWarning("Service provider is null, skipping batch processing");
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();

            // Group entries by client and service for efficient processing
            var grouped = entries.GroupBy(e => new { e.ClientIp, e.Service });

            foreach (var group in grouped)
            {
                // Check if still running before each group
                if (!_isRunning) break;

                try
                {
                    // Pass false for sendRealtimeUpdates when in bulk processing mode
                    // This prevents PICS from starting during log processing
                    bool sendRealtimeUpdates = !_isBulkProcessing;
                    await dbService.ProcessLogEntryBatch(group.ToList(), sendRealtimeUpdates);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error processing batch for {group.Key.ClientIp}/{group.Key.Service}");
                }
            }

            // Only send real-time updates if not in bulk processing mode and still running
            if (!_isBulkProcessing && _isRunning)
            {
                await SendRealtimeUpdate(entries);
            }
        }
        catch (ObjectDisposedException)
        {
            _logger.LogDebug("Service provider disposed during batch processing - service is shutting down");
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

        _isRunning = false;

        // Stop accepting new items
        _logChannel.Writer.TryComplete();

        // Stop and dispose batch timer first, wait for any pending callbacks
        if (_batchTimer != null)
        {
            await _batchTimer.DisposeAsync();
            _batchTimer = null;
        }

        // Wait a bit for any in-flight timer callbacks to complete
        await Task.Delay(100, cancellationToken);

        // Flush remaining batch safely
        await FlushBatchSafe();

        // Wait for any remaining batch operations to complete before disposing semaphore
        try
        {
            await _batchSemaphore.WaitAsync(5000, cancellationToken);
            _batchSemaphore.Release();
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, that's fine
        }

        // Now safe to dispose semaphore
        _batchSemaphore?.Dispose();

        await base.StopAsync(cancellationToken);
    }
}