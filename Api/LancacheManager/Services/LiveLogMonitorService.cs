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

    // Configuration - optimized for real-time updates with minimal latency
    private readonly int _pollIntervalSeconds = 2; // Check every 2 seconds for faster detection
    private readonly long _minFileSizeIncrease = 50_000; // 50 KB minimum increase to trigger processing (lowered for faster updates)
    private DateTime _lastProcessTime = DateTime.MinValue;
    private readonly int _minSecondsBetweenProcessing = 3; // Minimum 3 seconds between processing runs (faster for active downloads)

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
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

        _logger.LogInformation("LiveLogMonitorService started - monitoring {LogFile} for new entries (silent mode enabled)", _logFilePath);

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
                // Rate limiting: Don't process if we just processed recently
                var timeSinceLastProcess = (DateTime.UtcNow - _lastProcessTime).TotalSeconds;
                if (timeSinceLastProcess < _minSecondsBetweenProcessing)
                {
                    _logger.LogDebug(
                        "Skipping processing - only {TimeSince:F1}s since last run (minimum {MinTime}s)",
                        timeSinceLastProcess,
                        _minSecondsBetweenProcessing
                    );
                    return;
                }

                _logger.LogDebug(
                    "Detected {SizeIncrease:N0} bytes of new log data ({CurrentSize:N0} bytes total), triggering silent Rust processor",
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
                _lastProcessTime = DateTime.UtcNow;

                try
                {
                    // Get the last processed line position
                    var lastPosition = _stateService.GetLogPosition();

                    // Start Rust processor from last processed position in SILENT MODE
                    // The Rust processor will:
                    // 1. Skip to the last position
                    // 2. Process all new lines to the end of file
                    // 3. Use duplicate detection to avoid re-processing entries
                    // 4. NOT send any SignalR notifications (silentMode = true)
                    var success = await _rustLogProcessorService.StartProcessingAsync(_logFilePath, lastPosition, silentMode: true);

                    if (success)
                    {
                        // Update file size tracker after successful processing
                        _lastFileSize = currentFileSize;
                        _logger.LogDebug("Successfully processed new entries in live mode (silent)");
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
