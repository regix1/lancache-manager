using System.Threading;
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
    private readonly SemaphoreSlim _batchSemaphore = new(1, 1); // Only 1 concurrent batch operation to prevent race conditions and ensure consistent results
    private volatile bool _isRunning = false;
    private volatile bool _isBulkProcessing = false; // Track if we're in bulk processing mode
    private long _pendingLogLines = 0;
    private long _pendingBatchEntries = 0;
    private int _activeBatchTasks = 0;
    private static readonly TimeSpan BulkDrainWaitInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan BulkDrainTimeout = TimeSpan.FromMinutes(5);

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
        _logger.LogInformation("Bulk processing mode: {IsBulkProcessing}", _isBulkProcessing);
    }

    /// <summary>
    /// Wait for any queued log entries and batches to drain before switching out of bulk mode.
    /// Keeps bulk mode active until processing queue is empty so depot processing isn't triggered prematurely.
    /// </summary>
    public async Task CompleteBulkProcessingAsync(CancellationToken cancellationToken = default)
    {
        if (!_isBulkProcessing)
        {
            _logger.LogDebug("CompleteBulkProcessingAsync called but bulk mode is already disabled");
            return;
        }

        _logger.LogInformation("Waiting for log processing queue to drain before leaving bulk mode");

        // Stop the batch timer first to prevent new flushes
        if (_batchTimer != null)
        {
            await _batchTimer.DisposeAsync();
            _batchTimer = null;
            _logger.LogDebug("Stopped batch timer for bulk completion");
        }

        // Flush any pending buffered entries synchronously so they enter the processing pipeline
        await FlushBatch().ConfigureAwait(false);

        var startTime = DateTime.UtcNow;
        var lastLogTime = DateTime.UtcNow;
        var lastSignalRUpdate = DateTime.UtcNow;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pendingLines = Volatile.Read(ref _pendingLogLines);
            var pendingEntries = Volatile.Read(ref _pendingBatchEntries);
            var activeTasks = Volatile.Read(ref _activeBatchTasks);
            int bufferedEntries;
            lock (_batchLock)
            {
                bufferedEntries = _batchBuffer.Count;
            }

            // Send SignalR update every 1 second to keep UI updated during queue draining
            if (DateTime.UtcNow - lastSignalRUpdate > TimeSpan.FromSeconds(1))
            {
                try
                {
                    var totalPending = pendingLines + pendingEntries + bufferedEntries;
                    var message = totalPending > 0
                        ? $"Finalizing: {totalPending} entries remaining in queue..."
                        : "Finalizing log processing...";

                    var progressData = new {
                        percentComplete = 99.95,
                        status = "finalizing",
                        message = message,
                        pendingLines = pendingLines,
                        pendingEntries = pendingEntries,
                        activeTasks = activeTasks,
                        timestamp = DateTime.UtcNow
                    };
                    await _hubContext.Clients.All.SendAsync("ProcessingProgress", progressData);
                    _logger.LogInformation("Sent SignalR ProcessingProgress update: {Progress}%", 99.95);
                    lastSignalRUpdate = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error sending SignalR update during queue drain");
                }
            }

            // Log status every 5 seconds if still waiting
            if (DateTime.UtcNow - lastLogTime > TimeSpan.FromSeconds(5))
            {
                _logger.LogInformation("Still waiting for queue to drain - pendingLines={PendingLines}, pendingEntries={PendingEntries}, activeBatches={ActiveTasks}, buffered={Buffered}",
                    pendingLines, pendingEntries, activeTasks, bufferedEntries);
                lastLogTime = DateTime.UtcNow;
            }

            if (pendingLines == 0 && pendingEntries <= 0 && activeTasks == 0 && bufferedEntries == 0)
            {
                // Double-check with a small delay to ensure everything is truly complete
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);

                // Re-check after delay
                pendingLines = Volatile.Read(ref _pendingLogLines);
                pendingEntries = Volatile.Read(ref _pendingBatchEntries);
                activeTasks = Volatile.Read(ref _activeBatchTasks);
                lock (_batchLock)
                {
                    bufferedEntries = _batchBuffer.Count;
                }

                if (pendingLines == 0 && pendingEntries <= 0 && activeTasks == 0 && bufferedEntries == 0)
                {
                    break;
                }
            }

            // If we've been idle for 10 seconds with no activity, assume completion
            if (pendingLines == 0 && pendingEntries <= 0 && activeTasks == 0 && bufferedEntries == 0 && DateTime.UtcNow - startTime > TimeSpan.FromSeconds(10))
            {
                _logger.LogInformation("No activity detected for 10 seconds, assuming bulk processing complete");
                break;
            }

            // Much longer timeout to ensure all entries are processed
            // Don't force completion if there are still pending items
            if (DateTime.UtcNow - startTime > TimeSpan.FromMinutes(10) && pendingLines == 0 && pendingEntries == 0)
            {
                _logger.LogWarning("Forcing bulk completion after 10 minutes with no pending items (activeBatches={ActiveTasks}, buffered={Buffered})",
                    activeTasks, bufferedEntries);

                // Force flush one more time before giving up
                await FlushBatch().ConfigureAwait(false);
                break;
            }
            else if (DateTime.UtcNow - startTime > TimeSpan.FromMinutes(15))
            {
                // Absolute timeout after 15 minutes, but log as error since data might be lost
                _logger.LogError("CRITICAL: Forcing bulk completion after 15 minutes - DATA MAY BE LOST! (pendingLines={PendingLines}, pendingEntries={PendingEntries}, activeBatches={ActiveTasks}, buffered={Buffered})",
                    pendingLines, pendingEntries, activeTasks, bufferedEntries);

                // Force flush one more time before giving up
                await FlushBatch().ConfigureAwait(false);
                break;
            }

            await Task.Delay(BulkDrainWaitInterval, cancellationToken).ConfigureAwait(false);
        }

        _isBulkProcessing = false;
        _logger.LogInformation("Bulk log processing completed successfully");

        // Restart the batch timer for normal operation
        var flushInterval = 5000; // 5 seconds for real-time mode
        _batchTimer = new Timer(async _ => await SafeTimerFlush(), null, flushInterval, flushInterval);
        _logger.LogDebug("Restarted batch timer with {FlushInterval}ms intervals for normal mode", flushInterval);
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
            await ProcessLogLines(stoppingToken).ConfigureAwait(false);
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
            else
            {
                Interlocked.Increment(ref _pendingLogLines);
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

        var lineCount = 0;
        await foreach (var logLine in _logChannel.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                // Parse the log line
                var entry = _parser.ParseLine(logLine);
                if (entry != null) // Remove BytesServed filter - process ALL entries
                {
                    lock (_batchLock)
                    {
                        _batchBuffer.Add(entry);
                        Interlocked.Increment(ref _pendingBatchEntries);

                        // Use different batch sizes based on processing mode
                        var batchSize = _isBulkProcessing ? 200 : 50; // Much larger batches for bulk processing
                        if (_batchBuffer.Count >= batchSize)
                        {
                            var batch = _batchBuffer.ToList();
                            _batchBuffer.Clear();

                            // Queue batch for background processing without using Task.Run to avoid thread pool starvation
                            _ = ProcessBatchWithThrottling(batch, cancellationToken);
                        }
                    }
                }

                // Yield control more frequently during bulk processing to prevent blocking
                lineCount++;
                var yieldInterval = _isBulkProcessing ? 50 : 100; // More frequent yields during bulk processing
                if (lineCount % yieldInterval == 0)
                {
                    await Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();

                    // Add small delay every 1000 lines to prevent overwhelming the system
                    if (lineCount % 1000 == 0)
                    {
                        await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Log processing cancelled");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing log line");
            }
            finally
            {
                Interlocked.Decrement(ref _pendingLogLines);
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
            await FlushBatch().ConfigureAwait(false);
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
            await ProcessBatchWithThrottling(batch, CancellationToken.None).ConfigureAwait(false);
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
            await ProcessBatchSafelyDuringShutdown(batch).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Process a batch with throttling to prevent resource exhaustion
    /// </summary>
    private async Task ProcessBatchWithThrottling(List<LogEntry> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0) return;

        Interlocked.Increment(ref _activeBatchTasks);

        try
        {
            if (!_isRunning)
            {
                return;
            }

            await _batchSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                // Check again after acquiring semaphore
                if (_isRunning)
                {
                    await ProcessBatch(entries).ConfigureAwait(false);

                    // Add delay based on processing mode to prevent resource exhaustion
                    var delay = _isBulkProcessing ? 50 : 200; // Increased delays to give other operations a chance
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
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
        finally
        {
            Interlocked.Add(ref _pendingBatchEntries, -entries.Count);
            Interlocked.Decrement(ref _activeBatchTasks);
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
            await ProcessBatch(entries).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing final batch during shutdown");
        }
        finally
        {
            Interlocked.Add(ref _pendingBatchEntries, -entries.Count);
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

            // Group entries by client, service, and depot (for Steam)
            // This ensures each depot gets its own download session
            var grouped = entries.GroupBy(e => new {
                e.ClientIp,
                e.Service,
                DepotId = e.Service == "steam" ? e.DepotId : null
            });

            foreach (var group in grouped)
            {
                // Check if still running before each group
                if (!_isRunning) break;

                try
                {
                    // Pass false for sendRealtimeUpdates when in bulk processing mode
                    // This prevents PICS from starting during log processing
                    bool sendRealtimeUpdates = !_isBulkProcessing;
                    await dbService.ProcessLogEntryBatch(group.ToList(), sendRealtimeUpdates).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var depotInfo = group.Key.DepotId.HasValue ? $" depot:{group.Key.DepotId.Value}" : "";
                    _logger.LogError(ex, $"Error processing batch for {group.Key.ClientIp}/{group.Key.Service}{depotInfo}");
                }
            }

            // Only send real-time updates if not in bulk processing mode and still running
            if (!_isBulkProcessing && _isRunning)
            {
                await SendRealtimeUpdate(entries).ConfigureAwait(false);
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
            }).ConfigureAwait(false);
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

        // Wait a bit for any in-flight timer callbacks to complete, but respect cancellation
        try
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("StopAsync was cancelled - forcing immediate shutdown");
        }

        // Flush remaining batch safely
        try
        {
            await FlushBatchSafe().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error flushing final batch during shutdown");
        }

        // Wait for any remaining batch operations to complete before disposing semaphore
        // Use a timeout that respects the cancellation token
        try
        {
            var waitTimeout = cancellationToken.IsCancellationRequested ? 1000 : 5000;
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            combinedCts.CancelAfter(waitTimeout);

            await _batchSemaphore.WaitAsync(combinedCts.Token).ConfigureAwait(false);
            _batchSemaphore.Release();
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timeout waiting for batch operations to complete - forcing shutdown");
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, that's fine
        }

        // Now safe to dispose semaphore
        _batchSemaphore?.Dispose();

        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Base StopAsync was cancelled");
        }

        _logger.LogInformation("LogProcessingService stopped");
    }
}