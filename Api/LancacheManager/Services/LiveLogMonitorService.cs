using Microsoft.AspNetCore.SignalR;
using LancacheManager.Hubs;

namespace LancacheManager.Services;

/// <summary>
/// Background service that continuously monitors the log file for new entries
/// and processes them using the Rust processor. This enables live/automatic updates
/// without requiring manual "Process All Logs" button clicks.
/// </summary>
public class LiveLogMonitorService : BackgroundService
{
    private readonly ILogger<LiveLogMonitorService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly RustLogProcessorService _rustLogProcessorService;
    private readonly StateService _stateService;
    private readonly string _logFilePath;
    private long _lastFileSize = 0;
    private bool _isProcessing = false;

    // Configuration - optimized for low CPU usage with good responsiveness
    private readonly int _pollIntervalSeconds = 3; // Check every 3 seconds
    private readonly long _minFileSizeIncrease = 50_000; // 50 KB minimum increase to trigger processing

    public LiveLogMonitorService(
        ILogger<LiveLogMonitorService> logger,
        IPathResolver pathResolver,
        RustLogProcessorService rustLogProcessorService,
        StateService stateService)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _rustLogProcessorService = rustLogProcessorService;
        _stateService = stateService;
        _logFilePath = Path.Combine(_pathResolver.GetLogsDirectory(), "access.log");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for app to start up and for initial setup to complete
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        _logger.LogInformation("LiveLogMonitorService started - monitoring {LogFile} for new entries", _logFilePath);

        // Get initial file size if file exists
        if (File.Exists(_logFilePath))
        {
            var fileInfo = new FileInfo(_logFilePath);
            _lastFileSize = fileInfo.Length;
            _logger.LogInformation("Initial log file size: {Size:N0} bytes", _lastFileSize);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MonitorAndProcessLogFile(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Expected during shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in LiveLogMonitorService");
            }

            // Wait before next check
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("LiveLogMonitorService stopped");
    }

    private async Task MonitorAndProcessLogFile(CancellationToken stoppingToken)
    {
        // Skip if already processing
        if (_isProcessing)
        {
            return;
        }

        // Check if log file exists
        if (!File.Exists(_logFilePath))
        {
            return;
        }

        try
        {
            // Check file size (very fast operation)
            var fileInfo = new FileInfo(_logFilePath);
            var currentFileSize = fileInfo.Length;

            // Calculate size increase
            var sizeIncrease = currentFileSize - _lastFileSize;

            // Only process if file has grown by at least the threshold
            if (sizeIncrease >= _minFileSizeIncrease)
            {
                _logger.LogInformation(
                    "Detected {SizeIncrease:N0} bytes of new log data ({CurrentSize:N0} bytes total), triggering Rust processor",
                    sizeIncrease,
                    currentFileSize
                );

                // Check if manual processing is already running
                if (_rustLogProcessorService.IsProcessing)
                {
                    _logger.LogInformation("Manual processing is already running, skipping live update");
                    return;
                }

                // Start processing
                _isProcessing = true;

                try
                {
                    // Get the last processed line position
                    var lastPosition = _stateService.GetLogPosition();

                    // Start Rust processor from last processed position
                    // The Rust processor will:
                    // 1. Skip to the last position
                    // 2. Process all new lines to the end of file
                    // 3. Use duplicate detection to avoid re-processing entries
                    var success = await _rustLogProcessorService.StartProcessingAsync(_logFilePath, lastPosition);

                    if (success)
                    {
                        // Update file size tracker after successful processing
                        _lastFileSize = currentFileSize;
                        _logger.LogInformation("Successfully processed new entries in live mode");
                    }
                    else
                    {
                        _logger.LogWarning("Live processing did not complete successfully, will retry on next interval");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during live log processing");
                }
                finally
                {
                    _isProcessing = false;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking log file size");
            _isProcessing = false;
        }
    }
}
