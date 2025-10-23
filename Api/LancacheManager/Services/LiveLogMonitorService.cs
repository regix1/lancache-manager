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
    private readonly int _pollIntervalSeconds = 1; // Check every 1 second for near-instant detection
    private readonly long _minFileSizeIncrease = 10_000; // 10 KB minimum increase to trigger processing (very responsive)
    private DateTime _lastProcessTime = DateTime.MinValue;
    private readonly int _minSecondsBetweenProcessing = 1; // Minimum 1 second between processing runs (near-instant updates)

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

    /// <summary>
    /// Counts lines in a file with proper file sharing to allow other processes to delete/modify the file
    /// </summary>
    private long CountLinesWithSharing(string filePath)
    {
        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fileStream);

        long lineCount = 0;
        while (reader.ReadLine() != null)
        {
            lineCount++;
        }
        return lineCount;
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

            // If log position is 0 (default), initialize it to the end of the file
            // This ensures we only process NEW entries going forward, not the entire history
            var currentPosition = _stateService.GetLogPosition();
            if (currentPosition == 0)
            {
                var lineCount = CountLinesWithSharing(_logFilePath);
                _stateService.SetLogPosition(lineCount);
                _logger.LogInformation("Initialized log position to end of file (line {LineCount}) - will only process new entries", lineCount);
            }
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

        // Check if log file exists - if not, skip monitoring (only rotated files exist)
        // This is normal when logs are rotated and current access.log doesn't exist yet
        if (!File.Exists(_logFilePath))
        {
            // Only log once per service start to avoid spam
            if (_lastFileSize == 0)
            {
                _lastFileSize = -1; // Mark as logged
            }
            return;
        }

        // Reset size tracker if file reappeared after being missing
        if (_lastFileSize == -1)
        {
            _lastFileSize = 0;
            _logger.LogInformation("Current log file now exists: {LogFile}. Resuming live monitoring.", _logFilePath);
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
                    return;
                }

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
                    // ALWAYS use the current end of file position for live monitoring
                    // This ensures we only process NEW entries, not the entire log history
                    // The stored position is only used by the "Process All Logs" button
                    var lastPosition = _stateService.GetLogPosition();

                    // Count total lines in the file to get the current end position
                    var currentLineCount = CountLinesWithSharing(_logFilePath);

                    // If stored position is less than current file size, use stored position
                    // Otherwise use current line count (file might have been truncated/rotated)
                    var startPosition = Math.Min(lastPosition, currentLineCount);

                    // Start Rust processor from last processed position in SILENT MODE
                    // The Rust processor will:
                    // 1. Skip to the start position
                    // 2. Process all new lines to the end of file
                    // 3. Use duplicate detection to avoid re-processing entries
                    // 4. NOT send any SignalR notifications (silentMode = true)
                    var success = await _rustLogProcessorService.StartProcessingAsync(_logFilePath, startPosition, silentMode: true);

                    if (success)
                    {
                        // Update file size tracker after successful processing
                        _lastFileSize = currentFileSize;
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
