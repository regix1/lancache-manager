using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Generic progress monitor that polls a JSON progress file written by a Rust process
/// and sends updates via a callback. Consolidates the identical polling loops found
/// across RustLogProcessorService, RustLogRemovalService, and RustDatabaseResetService.
/// </summary>
public class RustProgressMonitor<T> where T : class
{
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly ILogger _logger;

    public RustProgressMonitor(RustProcessHelper rustProcessHelper, ILogger logger)
    {
        _rustProcessHelper = rustProcessHelper;
        _logger = logger;
    }

    /// <summary>
    /// Polls a JSON progress file at the given interval and invokes sendProgress for each update.
    /// Runs until cancellation is requested. Catches OperationCanceledException as expected behavior.
    /// </summary>
    /// <param name="progressFilePath">Path to the JSON progress file written by Rust</param>
    /// <param name="sendProgress">Async callback invoked with each deserialized progress update</param>
    /// <param name="ct">Cancellation token to stop monitoring</param>
    /// <param name="pollIntervalMs">Polling interval in milliseconds (default 500ms)</param>
    public async Task MonitorAsync(
        string progressFilePath,
        Func<T, Task> sendProgress,
        CancellationToken ct,
        int pollIntervalMs = 500)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(pollIntervalMs, ct);

                var progress = await _rustProcessHelper.ReadProgressFileAsync<T>(progressFilePath);
                if (progress != null)
                {
                    await sendProgress(progress);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error monitoring Rust progress from {ProgressFile}", progressFilePath);
        }
    }
}
