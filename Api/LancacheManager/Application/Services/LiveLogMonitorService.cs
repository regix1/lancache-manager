using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Infrastructure.Services;
namespace LancacheManager.Application.Services;

/// <summary>
/// Background service that continuously monitors log files for new entries
/// and processes them using the Rust processor. This enables live/automatic updates
/// without requiring manual "Process All Logs" button clicks.
/// Supports multiple datasources - monitors each datasource's log directory.
/// </summary>
public class LiveLogMonitorService : BackgroundService
{
    private readonly ILogger<LiveLogMonitorService> _logger;
    private readonly RustLogProcessorService _rustLogProcessorService;
    private readonly RustLogRemovalService _rustLogRemovalService;
    private readonly StateRepository _stateService;
    private readonly DatasourceService _datasourceService;
    private readonly Dictionary<string, long> _lastFileSizes = new(); // Per-datasource file sizes
    private bool _isProcessing = false;

    // Static pause mechanism for log file operations (corruption removal, etc.)
    private static readonly SemaphoreSlim _pauseLock = new SemaphoreSlim(1, 1);
    private static bool _isPaused = false;

    // Configuration - optimized for real-time updates with minimal latency
    private readonly int _pollIntervalSeconds = 1; // Check every 1 second for near-instant detection
    private readonly long _minFileSizeIncrease = 10_000; // 10 KB minimum increase to trigger processing (very responsive)
    private DateTime _lastProcessTime = DateTime.MinValue;
    private readonly int _minSecondsBetweenProcessing = 1; // Minimum 1 second between processing runs (near-instant updates)

    /// <summary>
    /// Temporarily pause the log monitor to allow other operations (like corruption removal) to modify log files
    /// </summary>
    public static async Task PauseAsync()
    {
        await _pauseLock.WaitAsync();
        try
        {
            _isPaused = true;
        }
        finally
        {
            _pauseLock.Release();
        }
    }

    /// <summary>
    /// Resume the log monitor after log file modifications are complete
    /// </summary>
    public static async Task ResumeAsync()
    {
        await _pauseLock.WaitAsync();
        try
        {
            _isPaused = false;
        }
        finally
        {
            _pauseLock.Release();
        }
    }

    public LiveLogMonitorService(
        ILogger<LiveLogMonitorService> logger,
        RustLogProcessorService rustLogProcessorService,
        RustLogRemovalService rustLogRemovalService,
        StateRepository stateService,
        DatasourceService datasourceService)
    {
        _logger = logger;
        _rustLogProcessorService = rustLogProcessorService;
        _rustLogRemovalService = rustLogRemovalService;
        _stateService = stateService;
        _datasourceService = datasourceService;
    }

    /// <summary>
    /// Counts lines in a file with proper file sharing to allow other processes to delete/modify the file.
    /// Includes retry logic to handle transient file locks from other processes (e.g., Rust log processor).
    /// </summary>
    private long CountLinesWithSharing(string filePath)
    {
        const int maxRetries = 5;
        const int baseDelayMs = 100;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
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
            catch (IOException) when (attempt < maxRetries - 1)
            {
                // File is locked by another process, wait and retry with exponential backoff
                var delayMs = baseDelayMs * (int)Math.Pow(2, attempt);
                Thread.Sleep(delayMs);
            }
        }

        // If all retries failed, throw the exception
        using var finalStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var finalReader = new StreamReader(finalStream);

        long finalCount = 0;
        while (finalReader.ReadLine() != null)
        {
            finalCount++;
        }
        return finalCount;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for app to start up and for initial setup to complete
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

        var datasources = _datasourceService.GetDatasources();

        if (datasources.Count == 0)
        {
            _logger.LogWarning("No datasources configured, LiveLogMonitorService will not monitor any log files");
            return;
        }

        // Check if logs have been processed before (to distinguish fresh install from manual reset)
        var hasProcessedLogs = _stateService.GetHasProcessedLogs();

        // Initialize file sizes for each datasource
        foreach (var ds in datasources)
        {
            var logFile = Path.Combine(ds.LogPath, "access.log");
            if (File.Exists(logFile))
            {
                var fileInfo = new FileInfo(logFile);
                _lastFileSizes[ds.Name] = fileInfo.Length;
                _logger.LogInformation("Datasource '{Name}': Initial log file size: {Size:N0} bytes", ds.Name, fileInfo.Length);

                // Only auto-initialize to end of file on fresh install (never processed logs before)
                // If position is 0 but logs have been processed, user intentionally reset to beginning
                var currentPosition = _stateService.GetLogPosition(ds.Name);
                if (currentPosition == 0 && !hasProcessedLogs)
                {
                    var lineCount = CountLinesWithSharing(logFile);
                    _stateService.SetLogPosition(ds.Name, lineCount);
                    _logger.LogInformation("Datasource '{Name}': Fresh install - initialized log position to end of file (line {LineCount})", ds.Name, lineCount);
                }
            }
            else
            {
                _lastFileSizes[ds.Name] = 0;
            }
        }

        _logger.LogInformation("LiveLogMonitorService started - monitoring {Count} datasource(s) for new entries (silent mode enabled)", datasources.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Skip monitoring if paused (e.g., during corruption removal)
                bool shouldSkip = false;
                await _pauseLock.WaitAsync(stoppingToken);
                try
                {
                    shouldSkip = _isPaused;
                }
                finally
                {
                    _pauseLock.Release();
                }

                if (!shouldSkip)
                {
                    // Monitor each datasource
                    foreach (var ds in datasources)
                    {
                        if (!ds.Enabled) continue;
                        await MonitorAndProcessDatasource(ds, stoppingToken);
                    }
                }
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

    private async Task MonitorAndProcessDatasource(ResolvedDatasource datasource, CancellationToken stoppingToken)
    {
        // Skip if already processing
        if (_isProcessing)
        {
            return;
        }

        var logFilePath = Path.Combine(datasource.LogPath, "access.log");

        // Check if log file exists - if not, skip monitoring (only rotated files exist)
        if (!File.Exists(logFilePath))
        {
            // Only log once per service start to avoid spam
            if (!_lastFileSizes.ContainsKey(datasource.Name) || _lastFileSizes[datasource.Name] == 0)
            {
                _lastFileSizes[datasource.Name] = -1; // Mark as logged
            }
            return;
        }

        // Reset size tracker if file reappeared after being missing
        if (_lastFileSizes.TryGetValue(datasource.Name, out var lastSize) && lastSize == -1)
        {
            _lastFileSizes[datasource.Name] = 0;
            _logger.LogInformation("Datasource '{Name}': Log file now exists. Resuming live monitoring.", datasource.Name);
        }

        try
        {
            // Check file size (very fast operation)
            var fileInfo = new FileInfo(logFilePath);
            var currentFileSize = fileInfo.Length;

            // Get last known file size for this datasource
            _lastFileSizes.TryGetValue(datasource.Name, out var lastFileSize);

            // Calculate size increase
            var sizeIncrease = currentFileSize - lastFileSize;

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
                    _logger.LogDebug("Manual processing is already running, skipping live update for '{Name}'", datasource.Name);
                    return;
                }

                // Check if log removal is in progress
                if (_rustLogRemovalService.IsProcessing)
                {
                    _logger.LogDebug("Log removal is in progress for {Service}, skipping live update for '{Name}'", _rustLogRemovalService.CurrentService, datasource.Name);
                    return;
                }

                // Start processing
                _isProcessing = true;
                _lastProcessTime = DateTime.UtcNow;

                try
                {
                    // Get the stored position for this datasource
                    var lastPosition = _stateService.GetLogPosition(datasource.Name);

                    // Count total lines in the file to get the current end position
                    var currentLineCount = CountLinesWithSharing(logFilePath);

                    // If stored position is less than current file size, use stored position
                    // Otherwise use current line count (file might have been truncated/rotated)
                    var startPosition = Math.Min(lastPosition, currentLineCount);

                    // Start Rust processor from last processed position in SILENT MODE
                    // Pass the datasource name so the position is saved correctly
                    var success = await _rustLogProcessorService.StartProcessingAsync(
                        datasource.LogPath,
                        startPosition,
                        silentMode: true,
                        datasourceName: datasource.Name);

                    if (success)
                    {
                        // Update file size tracker after successful processing
                        _lastFileSizes[datasource.Name] = currentFileSize;
                    }
                    else
                    {
                        _logger.LogWarning("Live processing for datasource '{Name}' did not complete successfully, will retry on next interval", datasource.Name);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during live log processing for datasource '{Name}'", datasource.Name);
                }
                finally
                {
                    _isProcessing = false;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking log file size for datasource '{Name}'", datasource.Name);
            _isProcessing = false;
        }
    }
}
