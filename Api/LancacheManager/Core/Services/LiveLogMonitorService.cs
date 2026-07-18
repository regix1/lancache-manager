using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;

namespace LancacheManager.Core.Services;

/// <summary>
/// Background service that continuously monitors log files for new entries
/// and processes them using the Rust processor. This enables live/automatic updates
/// without requiring manual "Process All Logs" button clicks.
/// Supports multiple datasources - monitors each datasource's log directory.
/// </summary>
public class LiveLogMonitorService : ScheduledBackgroundService
{
    private readonly RustLogProcessorService _rustLogProcessorService;
    private readonly RustLogRemovalService _rustLogRemovalService;
    private readonly StateService _stateService;
    private readonly DatasourceService _datasourceService;
    private readonly IOperationConflictChecker _conflictChecker;
    private readonly RustProcessHelper _rustProcessHelper;
    // Per-source watermarks keyed "<datasource>::<stem>": on-disk size of each stem's
    // CURRENT file at the last successful processing pass (rotations never grow).
    private readonly Dictionary<string, long> _lastFileSizes = new();
    private bool _isProcessing = false;

    // Per-datasource permission error tracking for exponential backoff
    private readonly Dictionary<string, int> _consecutivePermissionErrors = new();
    private readonly Dictionary<string, DateTime> _lastPermissionErrorLogTime = new();

    // Per-datasource missing-source warning state (backoff-throttled, clears on reopen)
    private readonly Dictionary<string, DateTime> _lastMissingSourcesWarnTime = new();
    private readonly HashSet<string> _missingSourcesWarned = new();
    private static readonly TimeSpan _missingSourcesWarnInterval = TimeSpan.FromMinutes(5);

    // Static pause mechanism for log file operations (corruption removal, etc.)
    private static readonly SemaphoreSlim _pauseLock = new SemaphoreSlim(1, 1);
    private static bool _isPaused = false;

    // Configuration - optimized for real-time updates with minimal latency
    private readonly long _minFileSizeIncrease = 10_000; // 10 KB minimum increase to trigger processing (very responsive)
    internal const long MaxConcurrentCorruptionIngestionBytes = 4 * 1024 * 1024;
    private DateTime _lastProcessTime = DateTime.MinValue;
    private readonly int _minSecondsBetweenProcessing = 1; // Minimum 1 second between processing runs (near-instant updates)

    protected override string ServiceName => "LiveLogMonitor";
    protected override TimeSpan Interval => TimeSpan.FromSeconds(1);
    protected override TimeSpan StartupDelay => TimeSpan.Zero;
    public override bool DefaultRunOnStartup => true;

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
        IConfiguration configuration,
        RustLogProcessorService rustLogProcessorService,
        RustLogRemovalService rustLogRemovalService,
        StateService stateService,
        DatasourceService datasourceService,
        IOperationConflictChecker conflictChecker,
        RustProcessHelper rustProcessHelper)
        : base(logger, configuration)
    {
        _rustLogProcessorService = rustLogProcessorService;
        _rustLogRemovalService = rustLogRemovalService;
        _stateService = stateService;
        _datasourceService = datasourceService;
        _conflictChecker = conflictChecker;
        _rustProcessHelper = rustProcessHelper;
    }

    protected override async Task OnStartupAsync(CancellationToken stoppingToken)
    {
        // Wait for setup to complete so datasources are configured
        await _stateService.WaitForSetupCompletedAsync(stoppingToken);

        var datasources = _datasourceService.GetDatasources();

        if (datasources.Count == 0)
        {
            _logger.LogWarning("No datasources configured, LiveLogMonitorService will not monitor any log files");
            return;
        }

        // Check if logs have been processed before (to distinguish fresh install from manual reset)
        var hasProcessedLogs = _stateService.HasProcessedLogs();

        // Initialize per-source watermarks for each datasource
        foreach (var ds in datasources)
        {
            // Validate log directory exists before monitoring
            if (!Directory.Exists(ds.LogPath))
            {
                _logger.LogWarning("Datasource '{Name}': Log directory does not exist at '{LogPath}', skipping monitoring for this datasource", ds.Name, ds.LogPath);
                continue;
            }

            ds.RefreshLogSources();
            foreach (var filePath in ds.LogFilePaths)
            {
                var fileInfo = new FileInfo(filePath);
                _lastFileSizes[WatermarkKey(ds.Name, Path.GetFileName(filePath))] = fileInfo.Length;
                _logger.LogInformation("Datasource '{Name}': Initial {Stem} size: {Size:N0} bytes",
                    ds.Name, Path.GetFileName(filePath), fileInfo.Length);
            }

            // Only auto-initialize to end of file on fresh install (never processed logs before).
            // If positions are 0 but logs have been processed, the user intentionally reset to
            // beginning. EOF-seed covers EVERY source stem (access.log AND per-service files),
            // series-wide, at the last complete record of each stem.
            var sourcePositions = _stateService.GetLogSourcePositions(ds.Name);
            var legacyPosition = _stateService.GetLogPosition(ds.Name);
            // Stem set, not current-file list: a source caught between rename and reopen
            // (rotations only on disk) still needs its series seeded to EOF.
            if (!hasProcessedLogs && sourcePositions.Count == 0 && legacyPosition == 0 &&
                ds.LogSourceStems.Count > 0)
            {
                try
                {
                    var count = await _rustProcessHelper.CountLogLinesAsync(ds.LogPath, stoppingToken);
                    _stateService.SetLogSourcePositions(ds.Name, count.SourceLineCounts);
                    _stateService.SetLogTotalLines(ds.Name, count.LinesProcessed);
                    _logger.LogInformation(
                        "Datasource '{Name}': Fresh install - initialized {SourceCount} source position(s) to end of file ({LineCount} lines total)",
                        ds.Name, count.SourceLineCounts.Count, count.LinesProcessed);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Datasource '{Name}': Failed to seed fresh-install positions; sources start at 0 (dedup absorbs any overlap)",
                        ds.Name);
                }
            }
        }

        _logger.LogInformation("LiveLogMonitorService started - monitoring {Count} datasource(s) for new entries (silent mode enabled)", datasources.Count);

        await Task.CompletedTask;
    }

    protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
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

        if (shouldSkip)
        {
            return;
        }

        // Monitor each datasource for access.log changes
        var datasources = _datasourceService.GetDatasources();
        foreach (var ds in datasources)
        {
            if (!ds.Enabled) continue;
            await ProcessDatasourceAsync(ds, stoppingToken);
        }
    }

    private static string WatermarkKey(string datasourceName, string stem) => $"{datasourceName}::{stem}";

    /// <summary>
    /// Missing-source handling: when a datasource directory holds NO access-log sources at
    /// all, warn once (then backoff-throttled), naming the path and the *.log files that ARE
    /// present; persist the warning for the UI. Clears (with an info line) when a source
    /// reopens. Returns true when sources are present.
    /// </summary>
    internal bool CheckDatasourceSources(ResolvedDatasource datasource)
    {
        datasource.RefreshLogSources();

        if (datasource.LogFilePaths.Count > 0)
        {
            // Clear the persisted warning too when it survived a restart (the source may
            // have reopened while the manager was down; the in-memory set alone cannot
            // know that).
            var hadPersistedWarning =
                _stateService.GetLogIngestDiagnostics(datasource.Name)?.MissingSourcesMessage != null;
            if (_missingSourcesWarned.Remove(datasource.Name) || hadPersistedWarning)
            {
                _lastMissingSourcesWarnTime.Remove(datasource.Name);
                _stateService.SetLogMissingSourcesWarning(datasource.Name, null);
                _logger.LogInformation("Datasource '{Name}': Log source(s) now exist. Resuming live monitoring.", datasource.Name);
            }
            return true;
        }

        var now = DateTime.UtcNow;
        var lastWarn = _lastMissingSourcesWarnTime.GetValueOrDefault(datasource.Name, DateTime.MinValue);
        if (now - lastWarn >= _missingSourcesWarnInterval)
        {
            string[] otherLogFiles;
            try
            {
                otherLogFiles = Directory.GetFiles(datasource.LogPath, "*.log", SearchOption.TopDirectoryOnly)
                    .Select(f => Path.GetFileName(f)!)
                    .ToArray();
            }
            catch
            {
                otherLogFiles = Array.Empty<string>();
            }

            var found = otherLogFiles.Length > 0
                ? $"found only: {string.Join(", ", otherLogFiles)}"
                : "no .log files present";
            var message =
                $"No access-log source found in '{datasource.LogPath}' ({found}). " +
                "Expected access.log or per-service files like steam-access.log.";

            _logger.LogWarning("Datasource '{Name}': {Message}", datasource.Name, message);
            _lastMissingSourcesWarnTime[datasource.Name] = now;
            _missingSourcesWarned.Add(datasource.Name);
            _stateService.SetLogMissingSourcesWarning(datasource.Name, message);
        }

        return false;
    }

    private async Task ProcessDatasourceAsync(ResolvedDatasource datasource, CancellationToken stoppingToken)
    {
        // Skip if already processing
        if (_isProcessing)
        {
            return;
        }

        if (!CheckDatasourceSources(datasource))
        {
            return;
        }

        // Check if we should skip this datasource due to permission error backoff
        if (_consecutivePermissionErrors.TryGetValue(datasource.Name, out var errorCount) && errorCount > 0)
        {
            // Exponential backoff: skip for 2^(errorCount-1) seconds, capped at 60 seconds
            var backoffSeconds = Math.Min(Math.Pow(2, errorCount - 1), 60);
            var lastErrorTime = _lastPermissionErrorLogTime.GetValueOrDefault(datasource.Name, DateTime.MinValue);
            if ((DateTime.UtcNow - lastErrorTime).TotalSeconds < backoffSeconds)
            {
                return; // Still in backoff period
            }
        }

        try
        {
            // Per-source watermark check (fast: one FileInfo per current source file).
            // A shrunken current file means a rename rotation happened: its whole new
            // content is pending. Growth on ANY source triggers one processing pass.
            long sizeIncrease = 0;
            var currentSizes = new Dictionary<string, long>();
            foreach (var filePath in datasource.LogFilePaths)
            {
                var stem = Path.GetFileName(filePath);
                var key = WatermarkKey(datasource.Name, stem);
                var currentFileSize = new FileInfo(filePath).Length;
                currentSizes[key] = currentFileSize;

                var lastFileSize = _lastFileSizes.GetValueOrDefault(key, 0);
                sizeIncrease += currentFileSize >= lastFileSize
                    ? currentFileSize - lastFileSize
                    : currentFileSize;
            }

            // Only process if the source set has grown by at least the threshold
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

                // Heavy data ops normally run one at a time (OperationConflictChecker section 1a).
                // Corruption detection is read-only with respect to access.log and the Downloads
                // projection, so a bounded automatic batch may run beside it to keep the Recent
                // downloads list live. The byte cap prevents an accumulated/full-log import from
                // bypassing the heavy-operation policy; manual processing never enters this path.
                var conflict = await _conflictChecker.CheckAsync(
                    OperationType.LogProcessing, ConflictScope.Bulk(), CancellationToken.None);
                if (conflict != null && !CanBypassConflictForIncrementalIngestion(conflict, sizeIncrease))
                {
                    _logger.LogDebug(
                        "Active {ActiveType} operation holds the heavy-op slot, skipping live update for '{Name}'",
                        conflict.ActiveOperationType, datasource.Name);
                    return;
                }

                if (conflict != null)
                {
                    _logger.LogDebug(
                        "Allowing {PendingBytes} byte incremental live ingestion for '{Name}' during corruption detection",
                        sizeIncrease, datasource.Name);
                }

                // Start processing
                _isProcessing = true;
                _lastProcessTime = DateTime.UtcNow;

                try
                {
                    // Start the Rust processor in SILENT MODE. Per-stem offsets come from the
                    // positions file the processor service writes from persisted checkpoints;
                    // the legacy start position argument is ignored in multi-source mode.
                    var success = await _rustLogProcessorService.StartProcessingAsync(
                        datasource.LogPath,
                        startPosition: 0,
                        silentMode: true,
                        datasourceName: datasource.Name);

                    if (success)
                    {
                        // Update per-source watermarks after successful processing
                        foreach (var (key, size) in currentSizes)
                        {
                            _lastFileSizes[key] = size;
                        }
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

            // If we got here without exception, the datasource is accessible - reset error tracking
            if (_consecutivePermissionErrors.TryGetValue(datasource.Name, out var prevErrors) && prevErrors > 0)
            {
                _logger.LogInformation("Datasource '{Name}': Log file permissions restored, resuming normal monitoring", datasource.Name);
                _consecutivePermissionErrors[datasource.Name] = 0;
            }
        }
        catch (UnauthorizedAccessException)
        {
            _isProcessing = false;

            // Track consecutive permission errors for this datasource
            _consecutivePermissionErrors.TryGetValue(datasource.Name, out var currentErrors);
            _consecutivePermissionErrors[datasource.Name] = currentErrors + 1;

            // Throttle logging: warn on first error, then once per minute
            var lastLogTime = _lastPermissionErrorLogTime.GetValueOrDefault(datasource.Name, DateTime.MinValue);
            var timeSinceLastLog = (DateTime.UtcNow - lastLogTime).TotalSeconds;

            if (currentErrors == 0 || timeSinceLastLog >= 60)
            {
                _logger.LogWarning(
                    "Datasource '{Name}': Permission denied reading log file. Check PUID/PGID settings. " +
                    "Backing off (consecutive errors: {ErrorCount})",
                    datasource.Name, currentErrors + 1);
                _lastPermissionErrorLogTime[datasource.Name] = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking log file size for datasource '{Name}'", datasource.Name);
            _isProcessing = false;
        }
    }

    internal static bool CanBypassConflictForIncrementalIngestion(
        OperationConflictResponse conflict,
        long pendingBytes) =>
        pendingBytes > 0 &&
        pendingBytes <= MaxConcurrentCorruptionIngestionBytes &&
        string.Equals(
            conflict.ActiveOperationType,
            nameof(OperationType.CorruptionDetection),
            StringComparison.Ordinal);
}
