using LancacheManager.Core.Services;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Extensions;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Hubs;
using LancacheManager.Core.Interfaces;
using LancacheManager.Models;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Service that spawns the Rust log processor and monitors its progress
/// </summary>
public class RustLogProcessorService
{
    private readonly ILogger<RustLogProcessorService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly ISignalRNotificationService _notifications;
    private readonly StateService _stateService;
    private readonly IServiceProvider _serviceProvider;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly DatasourceService _datasourceService;
    private readonly IUnifiedOperationTracker _operationTracker;
    private CancellationTokenSource? _cancellationTokenSource;
    private Guid? _currentOperationId;
    private string? _currentDatasourceName;
    private string? _currentProgressPath;
    private Task? _progressMonitorTask;
    private readonly SemaphoreSlim _startLock = new(1, 1);

    // Signaled at the RegisterOperation call inside the processor so StartBackgroundProcessingAsync
    // can return the assigned operationId without polling. Class-field is safe because _startLock
    // gates IsProcessing = true, so there is at most one in-flight start at a time.
    private TaskCompletionSource<Guid>? _operationRegisteredTcs;

    // Completion metrics captured by value just before CompleteOperation is called, so the
    // onTerminalEmit closure (PR2: terminal SignalR fires exactly once from CompleteOperation)
    // can read the final EntriesProcessed/LinesProcessed/Elapsed without re-reading the progress
    // file. _startLock gates a single in-flight start, so a class field is safe here.
    private LogProcessingTerminalMetrics _terminalMetrics;

    private readonly record struct LogProcessingTerminalMetrics(
        long EntriesProcessed,
        long LinesProcessed,
        double? Elapsed,
        string? Message,
        string? StageKey);

    public bool IsProcessing { get; private set; }
    private bool IsSilentMode { get; set; }
    public Guid? CurrentOperationId => _currentOperationId;

    public Task<Guid?> StartAllInBackgroundAsync()
    {
        var datasources = _datasourceService.GetDatasources();
        if (datasources.Count == 0)
        {
            _logger.LogWarning("No datasources configured for log processing");
            return Task.FromResult<Guid?>(null);
        }

        return RunBackgroundAsync(
            () => RunAllDatasourcesAsync(silentMode: false),
            "processing all datasources");
    }

    private Guid BeginOperation()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        var operationId = _operationTracker.RegisterOperation(
            OperationType.LogProcessing,
            "Log Processing",
            _cancellationTokenSource,
            onTerminalCleanup: () =>
            {
                _currentOperationId = null;
                _currentDatasourceName = null;
                _currentProgressPath = null;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                // Reset the busy/state gates too: the universal force-kill path bypasses
                // EndLogProcessingOperation/ResetState, so without this IsProcessing stays true and
                // blocks the next StartProcessing (guard at the IsProcessing check).
                IsProcessing = false;
                IsSilentMode = false;
            },
            // Batch path is always interactive (RunAllDatasourcesAsync is only ever invoked with
            // silentMode:false), so the terminal SignalR emitter is always wired here.
            onTerminalEmit: BuildTerminalEmit());
        _currentOperationId = operationId;
        _operationRegisteredTcs?.TrySetResult(operationId);
        IsProcessing = true;
        IsSilentMode = false;
        return operationId;
    }

    /// <summary>
    /// Builds the single terminal SignalR emitter for a LogProcessing operation. Invoked EXACTLY
    /// ONCE from inside <see cref="IUnifiedOperationTracker.CompleteOperation"/> (CompletedFlag-gated)
    /// across the success, cancel (OCE/force-kill), and error paths. Reads the completion metrics that
    /// <see cref="StartProcessingAsync(string, long, bool, string, System.Guid?, bool)"/> snapshots into <see cref="_terminalMetrics"/> just before
    /// calling CompleteOperation. Silent operations never register an onTerminalEmit, so this only
    /// runs for interactive ops. The closure must not throw (tracker fire-and-forgets it).
    /// </summary>
    private Func<OperationTerminalInfo, Task> BuildTerminalEmit()
    {
        // operationId is captured lazily via _currentOperationId because RegisterOperation has not
        // returned the id yet at the moment this closure is constructed. By the time the closure
        // fires (at CompleteOperation), _currentOperationId is set; we snapshot it then.
        return info =>
        {
            var metrics = _terminalMetrics;
            var operationId = _currentOperationId;

            if (info.Cancelled)
            {
                return _notifications.NotifyAllAsync(
                    SignalREvents.LogProcessingComplete,
                    new LogProcessingComplete(
                        OperationId: operationId,
                        Success: false,
                        Status: OperationStatus.Cancelled,
                        Message: metrics.Message ?? "Log processing was cancelled",
                        Cancelled: true,
                        EntriesProcessed: metrics.EntriesProcessed,
                        LinesProcessed: metrics.LinesProcessed,
                        Elapsed: metrics.Elapsed,
                        StageKey: metrics.StageKey));
            }

            if (info.Success)
            {
                return _notifications.NotifyAllAsync(
                    SignalREvents.LogProcessingComplete,
                    new LogProcessingComplete(
                        OperationId: operationId,
                        Success: true,
                        Status: OperationStatus.Completed,
                        Message: metrics.Message ?? "Log processing completed successfully",
                        Cancelled: false,
                        EntriesProcessed: metrics.EntriesProcessed,
                        LinesProcessed: metrics.LinesProcessed,
                        Elapsed: metrics.Elapsed,
                        StageKey: metrics.StageKey));
            }

            return _notifications.NotifyAllAsync(
                SignalREvents.LogProcessingComplete,
                new LogProcessingComplete(
                    OperationId: operationId,
                    Success: false,
                    Status: OperationStatus.Failed,
                    Message: info.Error ?? metrics.Message ?? "Log processing failed",
                    Cancelled: false,
                    EntriesProcessed: metrics.EntriesProcessed,
                    LinesProcessed: metrics.LinesProcessed,
                    Elapsed: metrics.Elapsed,
                    StageKey: metrics.StageKey));
        };
    }

    private void EndOperation()
    {
        IsProcessing = false;
        IsSilentMode = false;
        _currentOperationId = null;
        _currentDatasourceName = null;
        _currentProgressPath = null;
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
    }

    private async Task<bool> RunAllDatasourcesAsync(bool silentMode)
    {
        var datasources = _datasourceService.GetDatasources();
        if (datasources.Count == 0)
        {
            _logger.LogWarning("No datasources configured for log processing");
            return false;
        }

        // Same atomic gate the single-datasource path takes: without it the batch could
        // race a live-monitor start and overwrite the singleton's CTS/operation fields.
        await _startLock.WaitAsync();
        try
        {
            if (IsProcessing)
            {
                _logger.LogWarning("Rust log processor is already running; batch start rejected");
                return false;
            }
            IsProcessing = true;
        }
        finally
        {
            _startLock.Release();
        }

        var batchOperationId = BeginOperation();
        try
        {
            var allSuccess = true;
            for (var i = 0; i < datasources.Count; i++)
            {
                // Stop spawning Rust children for the remaining datasources once the shared
                // operation has been cancelled (csharp-services-1 / P2-E). The in-flight
                // datasource handles its own cancellation via the shared CTS token.
                if (_cancellationTokenSource?.IsCancellationRequested == true)
                {
                    _logger.LogInformation("Log processing batch cancelled; skipping remaining datasources");
                    allSuccess = false;
                    break;
                }

                var datasource = datasources[i];
                var logPosition = _stateService.GetLogPosition(datasource.Name);
                _logger.LogInformation("Processing datasource '{DatasourceName}' from position {Position}",
                    datasource.Name, logPosition);

                var success = await StartProcessingAsync(
                    datasource.LogPath,
                    logPosition,
                    silentMode,
                    datasource.Name,
                    sharedOperationId: batchOperationId,
                    finalizeOperation: i == datasources.Count - 1);
                if (!success)
                {
                    allSuccess = false;
                    _logger.LogWarning("Processing failed for datasource '{DatasourceName}'", datasource.Name);
                }
            }

            return allSuccess;
        }
        finally
        {
            if (IsProcessing)
            {
                EndOperation();
            }
        }
    }

    public Task<Guid?> StartInBackgroundAsync(string logFilePath, long startPosition = 0, bool silentMode = false, string? datasourceName = null)
    {
        return RunBackgroundAsync(
            () => StartProcessingAsync(logFilePath, startPosition, silentMode, datasourceName),
            $"processing datasource '{datasourceName ?? "default"}'");
    }

    private async Task<Guid?> RunBackgroundAsync(Func<Task<bool>> processor, string description)
    {
        var registered = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        _operationRegisteredTcs = registered;

        var backgroundTask = Task.Run(async () =>
        {
            try
            {
                return await processor();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error while {Description}", description);
                return false;
            }
            finally
            {
                // Unblocks the outer wait if the processor exited without reaching RegisterOperation
                // (e.g. IsProcessing guard returned false, or an early exception). Harmless if already set.
                registered.TrySetCanceled();
            }
        });

        await Task.WhenAny(registered.Task, backgroundTask);
        return registered.Task.IsCompletedSuccessfully ? registered.Task.Result : (Guid?)null;
    }

    /// <summary>
    /// Force kills the log processing operation
    /// </summary>
    public async Task<bool> ForceKillProcessingAsync()
    {
        if (!IsProcessing)
        {
            _logger.LogWarning("No log processing operation to kill");
            return false;
        }

        _logger.LogWarning("Force killing log processing operation");

        try
        {
            // Snapshot a cancelled message for the onTerminalEmit closure. ForceKillOperation drives
            // CompleteOperation, which fires the single terminal LogProcessingComplete event.
            _terminalMetrics = _terminalMetrics with { Message = "Log processing was cancelled" };

            if (_currentOperationId.HasValue)
            {
                _operationTracker.ForceKillOperation(_currentOperationId.Value);
            }
            else
            {
                _cancellationTokenSource?.Cancel();
            }

            await Task.Delay(500);

            IsProcessing = false;

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during force kill of log processing");
            return false;
        }
    }

    /// <summary>
    /// Resets the log position to 0 to reprocess all logs (all datasources)
    /// </summary>
    public void ResetLogPosition()
    {
        // Reset all datasource positions, including every per-source-stem checkpoint —
        // leaving stale stem offsets behind would resurrect them via the positions file.
        foreach (var ds in _datasourceService.GetDatasources())
        {
            _stateService.SetLogSourcePositions(ds.Name, new Dictionary<string, long>());
            _stateService.SetLogPosition(ds.Name, 0);
        }
        // Also reset legacy position for backward compatibility
        _stateService.SetLogPosition(0);
        _logger.LogInformation("Log position reset to 0 for all datasources");
    }

    /// <summary>
    /// Resets the log position for a specific datasource
    /// </summary>
    public void ResetLogPosition(string datasourceName)
    {
        _stateService.SetLogSourcePositions(datasourceName, new Dictionary<string, long>());
        _stateService.SetLogPosition(datasourceName, 0);
        _logger.LogInformation("Log position reset to 0 for datasource '{DatasourceName}'", datasourceName);
    }

    /// <summary>
    /// Starts log processing for all configured datasources
    /// </summary>
    public async Task<bool> StartProcessingAsync()
    {
        return await RunAllDatasourcesAsync(silentMode: false);
    }

    /// <summary>
    /// Gets the current processing status including progress data from Rust
    /// </summary>
    public object GetStatus()
    {
        // Snapshot the silent flag together with the processing guard, BEFORE the progress-file
        // read below. The completion path clears IsProcessing first and IsSilentMode a few
        // milliseconds later (cleanup runs after an awaited broadcast); reading IsSilentMode
        // after the file I/O let a status fetch at a silent run's end pair isProcessing=true
        // with silentMode=false, so recovery resurrected a visible card frozen at the terminal
        // snapshot. Mirrors the up-front snapshot in StatsController.EvictionScanStatus.
        var silentMode = IsSilentMode;
        if (!IsProcessing)
        {
            return new
            {
                isProcessing = false,
                silentMode = false,
                status = "idle",
                operationId = _currentOperationId
            };
        }

        // Read progress from the active datasource's progress file.
        var operationsDir = _pathResolver.GetOperationsDirectory();
        var progressPath = _currentProgressPath
            ?? Path.Combine(operationsDir, $"rust_progress_{_currentDatasourceName ?? (_datasourceService.GetDefaultDatasource()?.Name ?? "default")}.json");
        var legacyProgressPath = Path.Combine(operationsDir, "rust_progress.json");

        ProgressData? progress = null;
        try
        {
            if (!File.Exists(progressPath) && File.Exists(legacyProgressPath))
            {
                progressPath = legacyProgressPath;
            }

            if (File.Exists(progressPath))
            {
                var json = File.ReadAllText(progressPath);
                progress = System.Text.Json.JsonSerializer.Deserialize<ProgressData>(json);
            }
        }
        catch
        {
            // Ignore read errors - file may be being written
        }

        if (progress == null)
        {
            return new
            {
                isProcessing = true,
                silentMode,
                status = "starting",
                operationId = _currentOperationId
            };
        }

        // The Rust processor reports real byte counts across ALL discovered log files
        // (access.log + rotated + compressed), replacing the old single-file estimate.
        var mbTotal = progress.TotalBytes / (1024.0 * 1024.0);
        var mbProcessed = progress.BytesProcessed / (1024.0 * 1024.0);

        return new
        {
            isProcessing = true,
            silentMode,
            operationId = _currentOperationId,
            status = progress.Status,
            percentComplete = progress.PercentComplete,
            mbProcessed = Math.Round(mbProcessed, 1),
            mbTotal = Math.Round(mbTotal, 1),
            entriesProcessed = progress.EntriesSaved,
            totalLines = progress.TotalLines,
            stageKey = progress.StageKey
        };
    }

    public RustLogProcessorService(
        ILogger<RustLogProcessorService> logger,
        IPathResolver pathResolver,
        ISignalRNotificationService notifications,
        StateService stateService,
        IServiceProvider serviceProvider,
        RustProcessHelper rustProcessHelper,
        DatasourceService datasourceService,
        IUnifiedOperationTracker operationTracker)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _notifications = notifications;
        _stateService = stateService;
        _serviceProvider = serviceProvider;
        _rustProcessHelper = rustProcessHelper;
        _datasourceService = datasourceService;
        _operationTracker = operationTracker;
    }

    public class ProgressData
    {
        /// <summary>
        /// Only meaningful on the final "completed" write: the Rust processor no longer
        /// runs a line-counting pre-pass, so the total is unknown (0) while running.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("total_lines")]
        public long TotalLines { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("lines_parsed")]
        public long LinesParsed { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("entries_saved")]
        public long EntriesSaved { get; set; }

        /// <summary>Raw (compressed) log bytes consumed so far across all files.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("bytes_processed")]
        public long BytesProcessed { get; set; }

        /// <summary>Sum of on-disk sizes of every discovered log file.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("total_bytes")]
        public long TotalBytes { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("percent_complete")]
        public double PercentComplete { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("stage_key")]
        public string StageKey { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("context")]
        public Dictionary<string, object?> Context { get; set; } = new();

        [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("warnings")]
        public List<string> Warnings { get; set; } = new();

        [System.Text.Json.Serialization.JsonPropertyName("errors")]
        public List<string> Errors { get; set; } = new();

        /// <summary>Contract version of the polled progress file (0 = pre-contract writer).</summary>
        [System.Text.Json.Serialization.JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("run_id")]
        public string RunId { get; set; } = string.Empty;

        /// <summary>
        /// Empty while running; on the final write one of completed | completed_with_warnings |
        /// partial | failed | cancelled. This polled file is the single authority: exit code 0
        /// without a valid terminal checkpoint is treated as a failure.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("terminal_status")]
        public string TerminalStatus { get; set; } = string.Empty;

        /// <summary>Presentation-only source layout: monolithic | bare_metal | mixed.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("layout")]
        public string Layout { get; set; } = string.Empty;

        /// <summary>Per-source-stem series line counts as consumed by this run.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("source_positions")]
        public Dictionary<string, long> SourcePositions { get; set; } = new();

        [System.Text.Json.Serialization.JsonPropertyName("unparsed_lines")]
        public long UnparsedLines { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("hintless_http_detailed_lines")]
        public long HintlessHttpDetailedLines { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("skipped_fallback_lines")]
        public long SkippedFallbackLines { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("invalid_encoding_lines")]
        public long InvalidEncodingLines { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("recognized_ignored_lines")]
        public long RecognizedIgnoredLines { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("incomplete_final_records")]
        public long IncompleteFinalRecords { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("files_with_errors")]
        public List<string> FilesWithErrors { get; set; } = new();
    }

    private static readonly string[] _validTerminalStatuses =
    {
        "completed", "completed_with_warnings", "partial", "failed", "cancelled"
    };

    /// <summary>
    /// True when the final progress file is a valid terminal checkpoint from the new
    /// contract writer: known schema version and a recognized terminal status.
    /// </summary>
    private static bool IsValidTerminalCheckpoint(ProgressData? progress) =>
        progress is { SchemaVersion: 1 } &&
        _validTerminalStatuses.Contains(progress.TerminalStatus);

    /// <summary>
    /// True when a run's terminal checkpoint proves committed download rows exist: a valid
    /// terminal checkpoint with at least one saved entry. Rust commits every batch transaction
    /// before writing the checkpoint, so an event emitted on this predicate can never precede
    /// the rows it announces. Partial and cancelled runs with committed batches qualify; a
    /// missing or invalid checkpoint never does (its entry count cannot be trusted).
    /// </summary>
    public static bool HasCommittedDownloads(ProgressData? progress) =>
        IsValidTerminalCheckpoint(progress) && progress!.EntriesSaved > 0;

    /// <summary>
    /// Emits the committed-boundary <see cref="SignalREvents.DownloadsRefresh"/>: fired once
    /// per run, immediately after terminal-checkpoint validation and BEFORE the auto-tag and
    /// mapping post-passes, so the frontend refetches inserted rows at commit latency instead
    /// of post-pass latency. Must stay on NotifyAllAsync: it bumps the dashboard batch cache
    /// generation before the hub send, which is what guarantees the event-driven refetch can
    /// never be served a stale cached batch. The payload is diagnostic only; the frontend must
    /// not depend on any of its fields.
    /// </summary>
    private async Task NotifyCommittedDownloadsAsync(ProgressData? finalProgress)
    {
        if (!HasCommittedDownloads(finalProgress))
        {
            return;
        }

        await _notifications.NotifyAllAsync(SignalREvents.DownloadsRefresh, new
        {
            source = "rust-insert-early",
            terminalStatus = finalProgress!.TerminalStatus,
            logEntriesSaved = finalProgress.EntriesSaved,
            runId = finalProgress.RunId,
            timestamp = DateTime.UtcNow
        });
    }

    public async Task<bool> StartProcessingAsync(
        string logFilePath,
        long startPosition = 0,
        bool silentMode = false,
        string? datasourceName = null,
        Guid? sharedOperationId = null,
        bool finalizeOperation = true)
    {
        await _startLock.WaitAsync();
        try
        {
            if (IsProcessing && sharedOperationId == null)
            {
                _logger.LogWarning("Rust log processor is already running");
                return false;
            }

            if (sharedOperationId == null)
            {
                // Silent flag first: GetStatus snapshots IsSilentMode before its IsProcessing
                // guard, so the flag must already be correct when IsProcessing becomes visible.
                IsSilentMode = silentMode;
                IsProcessing = true;
            }
        }
        finally
        {
            _startLock.Release();
        }

        datasourceName ??= _datasourceService.GetDefaultDatasource()?.Name ?? "default";

        var shouldFinalizeOperation = finalizeOperation;

        try
        {
            if (sharedOperationId != null)
            {
                _currentOperationId = sharedOperationId;
                IsSilentMode = silentMode;
            }
            else
            {
                _cancellationTokenSource = new CancellationTokenSource();
                _currentOperationId = _operationTracker.RegisterOperation(
                    OperationType.LogProcessing,
                    "Log Processing",
                    _cancellationTokenSource,
                    onTerminalCleanup: () =>
                    {
                        _currentOperationId = null;
                        _currentDatasourceName = null;
                        _currentProgressPath = null;
                        _cancellationTokenSource?.Dispose();
                        _cancellationTokenSource = null;
                        // Reset the busy/state gates too: the universal force-kill path bypasses
                        // EndLogProcessingOperation/ResetState, so without this IsProcessing stays true
                        // and blocks the next StartProcessing (guard at the IsProcessing check).
                        IsProcessing = false;
                        IsSilentMode = false;
                    },
                    // Silent ops emit no terminal SignalR (preserves the old !silentMode guard), so
                    // only wire the emitter for interactive ops. The single terminal event then fires
                    // exactly once from CompleteOperation (success / OCE / force-kill).
                    onTerminalEmit: silentMode ? null : BuildTerminalEmit());
                _operationRegisteredTcs?.TrySetResult(_currentOperationId.Value);
            }

            var processingToken = _cancellationTokenSource?.Token
                ?? _operationTracker.GetOperation(_currentOperationId!.Value)?.CancellationTokenSource?.Token
                ?? CancellationToken.None;

            var operationsDir = _pathResolver.GetOperationsDirectory();
            var progressPath = Path.Combine(operationsDir, $"rust_progress_{datasourceName}.json");
            var rustExecutablePath = _pathResolver.GetRustLogProcessorPath();

            // Per-source positions file: the single source of stem offsets for this run
            // (the CLI start position is ignored by the multi-source processor). When no
            // per-source checkpoint exists yet (pre-upgrade state), migrate the legacy
            // aggregate position onto the access.log stem so monolithic behavior is
            // unchanged; other stems default to 0 inside the processor.
            var sourcePositions = _stateService.GetLogSourcePositions(datasourceName);
            if (sourcePositions.Count == 0)
            {
                var legacyPosition = _stateService.GetLogPosition(datasourceName);
                if (legacyPosition > 0)
                {
                    sourcePositions[LogSourceLayout.MonolithicStem] = legacyPosition;
                }
            }
            var startPositions = new Dictionary<string, long>(sourcePositions);
            var positionsPath = Path.Combine(operationsDir, $"rust_positions_{datasourceName}.json");
            var positionsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                schema_version = 1,
                sources = sourcePositions
            });
            await File.WriteAllTextAsync(positionsPath, positionsJson);

            // Determine if logFilePath is a directory or file path
            // If it's already a directory, use it directly; otherwise extract directory from file path
            var logDirectory = Directory.Exists(logFilePath)
                ? logFilePath  // It's already a directory
                : (Path.GetDirectoryName(logFilePath) ?? _pathResolver.GetLogsDirectory());  // Extract from file path

            _currentDatasourceName = datasourceName;
            _currentProgressPath = progressPath;

            // Delete old progress file
            if (File.Exists(progressPath))
            {
                File.Delete(progressPath);
            }

            _logger.LogInformation("Starting Rust log processor");
            _logger.LogInformation("Log directory: {LogDirectory}", logDirectory);
            _logger.LogInformation("Progress file: {ProgressPath}", progressPath);
            _logger.LogInformation("Start position: {StartPosition}", startPosition);

            // Send started event
            if (!silentMode)
            {
                await _notifications.NotifyAllAsync(SignalREvents.LogProcessingStarted, new
                {
                    OperationId = _currentOperationId,
                    StageKey = "signalr.logProcessing.starting",
                    Context = new Dictionary<string, object?>()
                });
            }

            // Auto-import PICS data if database is sparse but JSON file exists
            // Depot mappings should be set up via initialization flow before log processing
            // Check depot count asynchronously without blocking startup
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scopedDb = _serviceProvider.CreateScopedDbContext();
                    var depotCount = await scopedDb.DbContext.SteamDepotMappings.CountAsync();
                    _logger.LogInformation("Starting log processing with {DepotCount} depot mappings available", depotCount);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to check depot count before log processing");
                }
            });

            // Start Rust process
            // Now passing log directory instead of single file path
            // Rust processor will discover all access.log* files (including .1, .2, .gz, .zst)
            // Pass auto_map_depots flag: Always 1 to map depots during processing (avoids showing "Unknown Game" in Active tab)
            // This ensures downloads are properly mapped before appearing in the UI
            // Pass datasource name for multi-datasource support (records will be tagged with this name)
            var autoMapDepots = 1;
            var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                rustExecutablePath,
                $"\"{logDirectory}\" \"{progressPath}\" {startPosition} {autoMapDepots} \"{datasourceName}\" \"{positionsPath}\"",
                Path.GetDirectoryName(rustExecutablePath));

            // Pass TZ environment variable to Rust processor so it uses the correct timezone
            var tz = Environment.GetEnvironmentVariable("TZ");
            if (!string.IsNullOrEmpty(tz))
            {
                startInfo.EnvironmentVariables["TZ"] = tz;
                _logger.LogInformation("Passing TZ={TimeZone} to Rust processor", tz);
            }

            var startTime = DateTime.UtcNow;

            // Snapshotted inside the run callback BEFORE the monitor-stopping Cancel() call
            // below poisons the token: "cancelled" is an intent this host expressed, and the
            // terminal checkpoint alone must not be able to claim it (C# owns intent).
            var cancelRequestedDuringRun = false;

            var exitCode = await _rustProcessHelper.RunTrackedProcessAsync(
                startInfo,
                _currentOperationId,
                processingToken,
                async process =>
                {
                    // log_processor.rs's stdout NDJSON protocol only emits started/complete/failed
                    // lifecycle events (confirmed by reading log_processor.rs directly) — unlike
                    // cache_clear/cache_game_detect it does NOT emit intermediate percent ticks
                    // over stdout, so MonitorProgressAsync's file poll below remains the only
                    // source of granular line/byte progress and stays unchanged. Consuming stdout
                    // via the new structured event parser (instead of the old raw-line stdout logger's
                    // "log every raw JSON line at Info") turns what used to be raw-JSON log spam
                    // into clean, low-noise lifecycle log lines; stderr keeps its own line logger.
                    var stdoutTask = _rustProcessHelper.ConsumeStdoutProgressEventsAsync(
                        process,
                        onProgressEvent: evt =>
                        {
                            _logger.LogInformation("[Rust log processor] {Event} ({StageKey})", evt.Event, evt.StageKey);
                            return Task.CompletedTask;
                        },
                        processLabel: "log_processor",
                        processingToken);

                    var stderrTask = Task.Run(async () =>
                    {
                        string? line;
                        while ((line = await process.StandardError.ReadLineAsync(processingToken)) != null)
                        {
                            if (!string.IsNullOrEmpty(line))
                            {
                                _logger.LogInformation("[Rust log processor stderr] {Line}", line);
                            }
                        }
                    });

                    // The progress monitor gets its OWN linked token: cancelling the shared
                    // operation CTS after a child exits used to make a multi-datasource batch
                    // abort before its remaining datasources ever ran.
                    using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(processingToken);

                    if (!silentMode)
                    {
                        await _notifications.NotifyAllAsync(SignalREvents.LogProcessingProgress, new
                        {
                            OperationId = _currentOperationId,
                            PercentComplete = 0.0,
                            Status = OperationStatus.Running,
                            StageKey = "signalr.logProcessing.starting",
                            Context = new Dictionary<string, object?>(),
                            TotalLines = 0,
                            LinesParsed = 0,
                            EntriesSaved = 0,
                            MbProcessed = 0.0,
                            MbTotal = 0.0
                        });

                        _progressMonitorTask = Task.Run(async () => await MonitorProgressAsync(progressPath, monitorCts.Token));
                    }

                    await process.WaitForExitAsync(processingToken);

                    cancelRequestedDuringRun = processingToken.IsCancellationRequested;

                    _logger.LogInformation("Rust processor exited with code {ExitCode}", process.ExitCode);

                    await _rustProcessHelper.AwaitOutputTasksAsync(stdoutTask, stderrTask, TimeSpan.FromSeconds(5));

                    monitorCts.Cancel();
                    if (_progressMonitorTask != null)
                    {
                        try
                        {
                            await _progressMonitorTask;
                        }
                        catch (OperationCanceledException)
                        {
                            // Expected
                        }
                    }

                    return process.ExitCode;
                },
                processLabel: "log_processor");

            // The polled progress file is the single authority on the run outcome. The
            // typed terminal_status decides success/warning/partial/failure; "cancelled"
            // additionally requires that this host actually requested cancellation.
            var finalProgress = await ReadProgressFileAsync(progressPath);
            var hasTerminalCheckpoint = IsValidTerminalCheckpoint(finalProgress);
            var wasCancelled = cancelRequestedDuringRun &&
                ((hasTerminalCheckpoint && finalProgress!.TerminalStatus == "cancelled") ||
                 finalProgress?.Status == OperationStatus.Cancelled.ToWireString() ||
                 exitCode != 0);

            // Committed rows become visible NOW, before any cancellation/partial/success
            // branching and before the post-passes below (auto-tag and the Epic/Blizzard/Xbox
            // resolves each emit their own conditional refresh when they change rows). A
            // partial or cancelled run whose earlier batches committed still exposes that
            // data here without ever being reported as a completed operation.
            await NotifyCommittedDownloadsAsync(finalProgress);

            if (wasCancelled)
            {
                _logger.LogInformation("Processing was cancelled (exit code: {ExitCode}, progress: {Progress}%)",
                    exitCode, finalProgress?.PercentComplete ?? 0);

                // Complete the operation with cancellation status. The single terminal
                // LogProcessingComplete event is emitted by the onTerminalEmit closure (interactive
                // ops only); snapshot the final metrics by value first so the closure can read them.
                if (_currentOperationId.HasValue && shouldFinalizeOperation)
                {
                    _terminalMetrics = new LogProcessingTerminalMetrics(
                        EntriesProcessed: finalProgress?.EntriesSaved ?? 0,
                        LinesProcessed: finalProgress?.LinesParsed ?? 0,
                        Elapsed: null,
                        Message: "Log processing was cancelled",
                        StageKey: null);
                    _operationTracker.CompleteOperation(_currentOperationId.Value, false, "Operation was cancelled");
                }

                return false;
            }

            if (exitCode == 0)
            {
                // Check if Rust reported a failure status despite exit code 0
                // This catches edge cases where errors were logged but the process still exited cleanly
                if (finalProgress?.Status == OperationStatus.Failed.ToWireString() ||
                    (hasTerminalCheckpoint && finalProgress!.TerminalStatus == "failed"))
                {
                    _logger.LogError("Rust processor exited with code 0 but reported failure: {StageKey}", finalProgress!.StageKey);

                    // Snapshot failure metrics for the onTerminalEmit closure, then complete the op
                    // (CompleteOperation fires the single terminal LogProcessingComplete event).
                    if (_currentOperationId.HasValue && shouldFinalizeOperation)
                    {
                        _terminalMetrics = new LogProcessingTerminalMetrics(
                            EntriesProcessed: 0,
                            LinesProcessed: finalProgress.LinesParsed,
                            Elapsed: null,
                            Message: finalProgress.StageKey ?? "Log processing failed",
                            StageKey: finalProgress.StageKey);
                        _operationTracker.CompleteOperation(_currentOperationId.Value, false, finalProgress.StageKey);
                    }

                    return false;
                }

                // Exit code 0 without a valid terminal checkpoint means the processor died
                // between its last progress tick and its terminal write (or an incompatible
                // binary is installed). Positions must NOT be persisted from a non-terminal
                // snapshot, so this is a failure, not a quiet success.
                if (!hasTerminalCheckpoint)
                {
                    _logger.LogError(
                        "Rust processor exited with code 0 but left no valid terminal checkpoint (schema {Schema}, terminal '{Terminal}')",
                        finalProgress?.SchemaVersion ?? 0, finalProgress?.TerminalStatus ?? "<none>");
                    if (_currentOperationId.HasValue && shouldFinalizeOperation)
                    {
                        _terminalMetrics = new LogProcessingTerminalMetrics(
                            EntriesProcessed: finalProgress?.EntriesSaved ?? 0,
                            LinesProcessed: finalProgress?.LinesParsed ?? 0,
                            Elapsed: null,
                            Message: "Log processing ended without a valid completion checkpoint",
                            StageKey: null);
                        _operationTracker.CompleteOperation(_currentOperationId.Value, false,
                            "Log processing ended without a valid completion checkpoint");
                    }
                    return false;
                }

                PersistIngestDiagnostics(datasourceName!, finalProgress!, startPositions);

                // A partial run saved what it could but hit per-file errors: positions are
                // NOT persisted (the next run re-reads; dedup absorbs the overlap) and the
                // operation surfaces as failed-with-detail, never plain success.
                if (finalProgress!.TerminalStatus == "partial")
                {
                    var partialMessage =
                        $"Log processing finished with {finalProgress.FilesWithErrors.Count} file error(s); " +
                        $"{finalProgress.EntriesSaved} entries were saved";
                    _logger.LogError("{Message}: {Files}", partialMessage,
                        string.Join("; ", finalProgress.FilesWithErrors));
                    if (_currentOperationId.HasValue && shouldFinalizeOperation)
                    {
                        _terminalMetrics = new LogProcessingTerminalMetrics(
                            EntriesProcessed: finalProgress.EntriesSaved,
                            LinesProcessed: finalProgress.LinesParsed,
                            Elapsed: null,
                            Message: partialMessage,
                            StageKey: null);
                        _operationTracker.CompleteOperation(_currentOperationId.Value, false, partialMessage);
                    }
                    return false;
                }

                // Success is an ALLOWLIST, not "anything that wasn't rejected": a checkpoint
                // that says cancelled without this host having requested cancellation (or any
                // future terminal value) must never persist positions or report success.
                if (finalProgress.TerminalStatus is not ("completed" or "completed_with_warnings"))
                {
                    var unexpectedMessage =
                        $"Log processing ended with unexpected status '{finalProgress.TerminalStatus}'";
                    _logger.LogError("{Message}", unexpectedMessage);
                    if (_currentOperationId.HasValue && shouldFinalizeOperation)
                    {
                        _terminalMetrics = new LogProcessingTerminalMetrics(
                            EntriesProcessed: finalProgress.EntriesSaved,
                            LinesProcessed: finalProgress.LinesParsed,
                            Elapsed: null,
                            Message: unexpectedMessage,
                            StageKey: null);
                        _operationTracker.CompleteOperation(_currentOperationId.Value, false, unexpectedMessage);
                    }
                    return false;
                }

                // Normal completion - send completion with actual data
                if (finalProgress != null)
                {
                    // Persist the per-stem checkpoints atomically from the validated terminal
                    // checkpoint (never from a mid-run snapshot). The terminal map is MERGED
                    // over existing state: a stem whose files were briefly absent this run must
                    // keep its old checkpoint, not replay from zero when it reopens. Explicit
                    // lifecycle paths (service removal, delete-file, resets) are the only
                    // places checkpoints are removed. The legacy aggregate position is kept in
                    // sync inside SetLogSourcePositions; total lines comes from Rust to avoid
                    // C# recounting all log files.
                    if (finalProgress.SourcePositions.Count > 0)
                    {
                        var mergedPositions = _stateService.GetLogSourcePositions(datasourceName!);
                        foreach (var (stem, offset) in finalProgress.SourcePositions)
                        {
                            mergedPositions[stem] = offset;
                        }
                        _stateService.SetLogSourcePositions(datasourceName!, mergedPositions);
                        _stateService.SetLogTotalLines(datasourceName!, finalProgress.TotalLines);
                    }

                    // Mark that logs have been processed at least once to enable guest mode
                    _stateService.SetHasProcessedLogs(true);

                    // Only send SignalR notifications if not in silent mode
                    if (!silentMode)
                    {
                        // Real total byte count from the Rust processor (all discovered log files)
                        var mbTotal = finalProgress.TotalBytes / (1024.0 * 1024.0);

                        // Send final progress update with 100% and complete status
                        await _notifications.NotifyAllAsync(SignalREvents.LogProcessingProgress, new
                        {
                            OperationId = _currentOperationId,
                            PercentComplete = 100.0,
                            Status = OperationStatus.Completed,
                            StageKey = "signalr.logProcessing.complete",
                            Context = new Dictionary<string, object?>(),
                            finalProgress.TotalLines,
                            finalProgress.LinesParsed,
                            finalProgress.EntriesSaved,
                            MbProcessed = Math.Round(mbTotal, 1),
                            MbTotal = Math.Round(mbTotal, 1)
                        });
                    }
                }

                // Rust processor automatically maps depots during processing (auto_map_depots = 1);
                // game images are fetched from the Steam API in the background task below.
                if (finalProgress?.EntriesSaved > 0)
                {
                    // Auto-tag new downloads to active events for BOTH silent and interactive
                    // mode (prevents duplicate grouping issues). The committed-boundary refresh
                    // above already made the inserted rows visible; the tag pass emits its own
                    // conditional DownloadsRefresh when it changes associations.
                    await AutoTagNewDownloadsAsync();
                }

                // Resolve Epic downloads BEFORE the UI refresh so downloads show with game names.
                // This runs unconditionally (not gated on EntriesSaved > 0) because new CDN patterns
                // may have been added since the last run (e.g., a new user contributed patterns),
                // allowing previously unresolved downloads to be matched. The method itself is
                // efficient and no-ops when there are no unresolved Epic downloads.
                try
                {
                    using var epicScope = _serviceProvider.CreateScope();
                    var epicMappingService = epicScope.ServiceProvider.GetRequiredService<EpicMappingService>();
                    var resolved = await epicMappingService.ResolveDownloadsAsync();
                    if (resolved > 0)
                    {
                        _logger.LogInformation("Resolved {Count} Epic downloads to game names after log processing", resolved);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resolve Epic downloads (non-fatal)");
                }

                // Resolve Blizzard / Battle.net downloads the same way. Blizzard games are named
                // from the static, compiled-in TACT catalog at ingest, but downloads ingested
                // before a catalog entry existed stay unnamed; re-running the re-map after each
                // log process names them automatically (mirroring Epic above), so no manual
                // "Apply Now" card is needed. The service singleton no-ops when nothing is
                // unresolved and emits its own DownloadsRefresh when it renames rows.
                try
                {
                    var battleNetMappingService = _serviceProvider.GetRequiredService<LancacheManager.Core.Services.BattleNet.BattleNetMappingService>();
                    var resolvedBlizzard = await battleNetMappingService.ResolveDownloadsAsync();
                    if (resolvedBlizzard > 0)
                    {
                        _logger.LogInformation("Resolved {Count} Blizzard downloads to game names after log processing", resolvedBlizzard);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resolve Blizzard downloads (non-fatal)");
                }

                // Resolve Xbox / Microsoft Store downloads the same way. The Rust ingest path is the
                // primary, active-session-safe canonicalizer (wsus -> Service='xbox'); this post-pass
                // is BACKFILL ONLY for already-ingested, still-wsus INACTIVE rows the daemon later
                // contributed a CDN pattern for. The service re-tags inactive rows only (re-tagging an
                // active row would split the in-flight download) and no-ops when nothing matches.
                try
                {
                    var xboxMappingService = _serviceProvider.GetRequiredService<LancacheManager.Services.Xbox.XboxMappingService>();
                    var resolvedXbox = await xboxMappingService.ResolveDownloadsAsync();
                    if (resolvedXbox > 0)
                    {
                        _logger.LogInformation("Re-tagged {Count} wsus downloads to Xbox titles after log processing", resolvedXbox);
                    }

                    // Backfill DisplayCatalog banner URLs for any Xbox mapping still missing art so
                    // log-driven banners self-heal a transient first-fetch miss. This is a URL-only
                    // DisplayCatalog lookup (no image binaries), so it is safe inside the ingest
                    // pipeline; best-effort so a backfill failure can never break log processing.
                    try
                    {
                        await xboxMappingService.BackfillMissingBannerArtAsync();
                    }
                    catch (OperationCanceledException)
                    {
                        // Shutdown/cancellation - let it propagate, do not treat as a non-fatal backfill error.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to backfill Xbox banner art (non-fatal)");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resolve Xbox downloads (non-fatal)");
                }

                // Image fetching can run in background as it's not critical for the UI refresh
                _ = Task.Run(async () =>
                {
                    // Brief settle delay so any final DB writes from the Rust process are durable
                    // before we fetch images. (The live dashboard-batch cache is invalidated
                    // synchronously in the finalize step below, before the UI refresh signal —
                    // see InvalidateLiveCache.)
                    await Task.Delay(500);

                    // Rust mapped the depot IDs to game names during processing, but we still need to fetch images
                    // Only fetch Steam images when new entries were saved (requires Steam API calls)
                    if (finalProgress?.EntriesSaved > 0)
                    {
                        await FetchMissingGameNamesAsync();
                    }

                    // Fetch images for resolved Epic downloads unconditionally - new resolutions
                    // may have occurred above even without new log entries
                    await FetchMissingEpicImagesAsync();

                    // NOTE: GameImageFetchService runs on its own 30-minute schedule and will
                    // fetch image binaries after all game detection, mapping, and DB saves complete.
                    // We do NOT trigger it here to avoid fetching images mid-pipeline before
                    // game detection (GameDetectionService) has finished.
                });

                if (!silentMode && shouldFinalizeOperation)
                {
                    // Set IsProcessing to false BEFORE the delay so polling can detect completion
                    // This is critical for the initialization wizard step 5 to detect completion
                    IsProcessing = false;

                    // Ensure minimum display duration of 2 seconds for UI visibility BEFORE sending completion
                    // This prevents the progress UI from disappearing before users can see it
                    var elapsed = DateTime.UtcNow - startTime;
                    var minDisplayDuration = TimeSpan.FromSeconds(2);
                    _logger.LogInformation("Processing completed in {Elapsed}ms (minimum display duration: {MinDuration}ms)",
                        elapsed.TotalMilliseconds, minDisplayDuration.TotalMilliseconds);

                    if (elapsed < minDisplayDuration)
                    {
                        var remainingDelay = minDisplayDuration - elapsed;
                        _logger.LogInformation("Delaying completion signal by {Delay}ms for UI visibility",
                            remainingDelay.TotalMilliseconds);
                        await Task.Delay(remainingDelay);
                        _logger.LogInformation("Delay complete, sending completion signal now");
                    }
                    else
                    {
                        _logger.LogInformation("No delay needed, processing took longer than minimum duration");
                    }

                    // Calculate final elapsed time after delay and snapshot success metrics for the
                    // onTerminalEmit closure (the single terminal event fires from CompleteOperation
                    // below, after the min-display-duration delay so UI visibility is preserved).
                    var finalElapsed = DateTime.UtcNow - startTime;
                    var completionMessage = finalProgress?.TerminalStatus == "completed_with_warnings"
                        ? $"Log processing completed with warnings: {finalProgress.UnparsedLines} unrecognized line(s), " +
                          $"{finalProgress.HintlessHttpDetailedLines} line(s) without a service, " +
                          $"{finalProgress.InvalidEncodingLines} line(s) with invalid encoding"
                        : "Log processing completed successfully";
                    _terminalMetrics = new LogProcessingTerminalMetrics(
                        EntriesProcessed: finalProgress?.EntriesSaved ?? 0,
                        LinesProcessed: finalProgress?.LinesParsed ?? 0,
                        Elapsed: Math.Round(finalElapsed.TotalMinutes, 1),
                        Message: completionMessage,
                        StageKey: "signalr.logProcessing.complete");
                }
                else if (shouldFinalizeOperation)
                {
                    // In silent mode, we can set IsProcessing to false immediately. No trailing
                    // refresh here: the committed-boundary DownloadsRefresh already fired, and
                    // the auto-tag/mapping passes emit their own conditional refreshes when
                    // they change rows.
                    IsProcessing = false;
                }

                // Complete the operation successfully
                if (_currentOperationId.HasValue && shouldFinalizeOperation)
                {
                    _operationTracker.CompleteOperation(_currentOperationId.Value, true);
                }

                return true;
            }
            else
            {
                // Non-zero exit code but not cancelled - this is an actual error
                _logger.LogError("Rust processor failed with exit code {ExitCode}", exitCode);

                // Snapshot error metrics for the onTerminalEmit closure, then complete the op
                // (CompleteOperation fires the single terminal LogProcessingComplete event).
                if (_currentOperationId.HasValue && shouldFinalizeOperation)
                {
                    _terminalMetrics = new LogProcessingTerminalMetrics(
                        EntriesProcessed: 0,
                        LinesProcessed: 0,
                        Elapsed: null,
                        Message: $"Log processing failed with exit code {exitCode}",
                        StageKey: null);
                    _operationTracker.CompleteOperation(_currentOperationId.Value, false, $"Log processing failed with exit code {exitCode}");
                }

                return false;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Log processing was cancelled for datasource '{DatasourceName}'", datasourceName);

            // Snapshot the id once: a universal force-kill may have already run the cleanup
            // callback (which nulls _currentOperationId) on another thread.
            var cancelOpId = _currentOperationId;

            // If a universal force-kill already completed this op (or already cleared the id),
            // suppress the duplicate SignalR completion + CompleteOperation so only ONE
            // terminal event is emitted.
            var alreadyTerminal = !cancelOpId.HasValue
                || _operationTracker.GetOperation(cancelOpId.Value)?.Status
                    is (OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled);

            if (!alreadyTerminal && cancelOpId.HasValue)
            {
                if (shouldFinalizeOperation)
                {
                    // Snapshot the cancelled message for the onTerminalEmit closure, then complete
                    // (CompleteOperation fires the single terminal LogProcessingComplete event).
                    _terminalMetrics = new LogProcessingTerminalMetrics(
                        EntriesProcessed: 0,
                        LinesProcessed: 0,
                        Elapsed: null,
                        Message: "Log processing was cancelled",
                        StageKey: null);
                    _operationTracker.CompleteOperation(cancelOpId.Value, false, "Operation was cancelled");
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting Rust log processor");

            // Snapshot error metrics for the onTerminalEmit closure, then complete the op
            // (CompleteOperation fires the single terminal LogProcessingComplete event).
            if (_currentOperationId.HasValue && shouldFinalizeOperation)
            {
                _terminalMetrics = new LogProcessingTerminalMetrics(
                    EntriesProcessed: 0,
                    LinesProcessed: 0,
                    Elapsed: null,
                    Message: $"Log processing error: {ex.Message}",
                    StageKey: null);
                _operationTracker.CompleteOperation(_currentOperationId.Value, false, ex.Message);
            }

            return false;
        }
        finally
        {
            if (shouldFinalizeOperation)
            {
                EndOperation();
            }
            else
            {
                _currentDatasourceName = null;
                _currentProgressPath = null;
            }
        }
    }

    private Task MonitorProgressAsync(string progressPath, CancellationToken cancellationToken)
    {
        var loggedWarnings = new HashSet<string>();
        var loggedErrors = new HashSet<string>();

        var monitor = new RustProgressMonitor<ProgressData>(_rustProcessHelper, _logger);
        return monitor.MonitorAsync(progressPath, async (ProgressData progress) =>
        {
            // Log any new warnings
            foreach (var warning in progress.Warnings)
            {
                if (loggedWarnings.Add(warning))
                {
                    _logger.LogWarning("[Rust] {Warning}", warning);
                }
            }

            // Log any new errors
            foreach (var error in progress.Errors)
            {
                if (loggedErrors.Add(error))
                {
                    _logger.LogError("[Rust] {Error}", error);
                }
            }

            // Real byte counts from the Rust processor (all files, compressed sizes)
            var mbTotal = progress.TotalBytes / (1024.0 * 1024.0);
            var mbProcessed = progress.BytesProcessed / (1024.0 * 1024.0);

            // Update the unified operation tracker with progress
            if (_currentOperationId.HasValue)
            {
                _operationTracker.UpdateProgress(_currentOperationId.Value, progress.PercentComplete, progress.StageKey ?? "");
            }

            // Send progress update via SignalR with standardized format
            await _notifications.NotifyAllAsync(SignalREvents.LogProcessingProgress, new
            {
                OperationId = _currentOperationId,
                progress.PercentComplete,
                Status = OperationStatus.Running,
                StageKey = progress.StageKey,
                Context = progress.Context,
                progress.TotalLines,
                progress.LinesParsed,
                progress.EntriesSaved,
                MbProcessed = Math.Round(mbProcessed, 1),
                MbTotal = Math.Round(mbTotal, 1)
            });
        }, cancellationToken);
    }

    private async Task<ProgressData?> ReadProgressFileAsync(string progressPath)
    {
        return await _rustProcessHelper.ReadProgressFileAsync<ProgressData>(progressPath);
    }

    /// <summary>
    /// Persists the run's diagnostic counters. Warning-clear rule: an anomaly-free run
    /// only overwrites (and thereby clears) previous warnings when it examined meaningful
    /// new input — a no-op run must never launder away a standing warning. "New input" is
    /// judged per stem (one stem growing while another shrinks must still count), and an
    /// incomplete final record is a real observation worth recording too.
    /// </summary>
    private void PersistIngestDiagnostics(
        string datasourceName, ProgressData progress, Dictionary<string, long> startPositions)
    {
        var examinedNewInput = progress.SourcePositions.Count > 0
            ? progress.SourcePositions.Any(kvp =>
                kvp.Value > startPositions.GetValueOrDefault(kvp.Key, 0))
            : progress.LinesParsed > startPositions.Values.Sum();
        // IncompleteFinalRecords is deliberately NOT an anomaly trigger: a live file is
        // mid-line almost every tick, and letting that overwrite counters would clear a
        // standing warning without meaningful new input. It still persists (and displays)
        // whenever a run qualifies through the conditions below.
        var hasAnomalies = progress.UnparsedLines > 0
            || progress.HintlessHttpDetailedLines > 0
            || progress.InvalidEncodingLines > 0
            || progress.FilesWithErrors.Count > 0;

        if (!hasAnomalies && !examinedNewInput)
        {
            return;
        }

        _stateService.SetLogIngestDiagnostics(datasourceName, new LogIngestDiagnostics
        {
            Layout = progress.Layout,
            TerminalStatus = progress.TerminalStatus,
            UnparsedLines = progress.UnparsedLines,
            HintlessHttpDetailedLines = progress.HintlessHttpDetailedLines,
            SkippedFallbackLines = progress.SkippedFallbackLines,
            InvalidEncodingLines = progress.InvalidEncodingLines,
            RecognizedIgnoredLines = progress.RecognizedIgnoredLines,
            IncompleteFinalRecords = progress.IncompleteFinalRecords,
            FilesWithErrors = new List<string>(progress.FilesWithErrors),
            LastRunUtc = DateTime.UtcNow
        });
    }

        /// <summary>
    /// Fetches game names for downloads that have a GameAppId but no GameName.
    /// Image bytes are fetched exclusively by <see cref="GameImageFetchService"/> (3-tier pipeline).
    /// This method only updates GameName, which GameImageFetchService does not enrich.
    /// </summary>
    private async Task FetchMissingGameNamesAsync()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var steamService = scope.ServiceProvider.GetRequiredService<SteamService>();

            // Find downloads that have GameAppId but missing game name - image bytes are now
            // fetched exclusively by GameImageFetchService (3-tier pipeline). We only update
            // GameName here, since GameImageFetchService does not enrich Download.GameName.
            var downloadsNeedingName = await context.Downloads
                .Where(d => d.GameAppId.HasValue && string.IsNullOrEmpty(d.GameName))
                .Take(50)
                .ToListAsync();

            if (downloadsNeedingName.Count == 0)
            {
                return;
            }

            _logger.LogInformation("Fetching game names for {Count} downloads", downloadsNeedingName.Count);

            int updated = 0;
            foreach (var download in downloadsNeedingName)
            {
                try
                {
                    var gameInfo = await steamService.GetGameInfoAsync(download.GameAppId!.Value);
                    if (gameInfo != null && !string.IsNullOrEmpty(gameInfo.Name))
                    {
                        download.GameName = gameInfo.Name;
                        updated++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch game info for app {AppId}", download.GameAppId);
                }
            }

            if (updated > 0)
            {
                await context.SaveChangesAsync();
                _logger.LogInformation("Updated {Count} downloads with game names", updated);

                // NOTE: We do not trigger GameImageFetchService here - it runs on its own schedule
                // and will fetch image bytes after game detection has completed.
                // NOTE: We do not send DownloadsRefresh here - the main completion handler
                // already sends DownloadsRefresh (silent mode) or LogProcessingComplete (non-silent).
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching missing game names - this is non-critical");
        }
    }

    /// <summary>
    /// Sets GameImageUrl on Epic downloads that have been resolved to games but are missing images.
    /// Looks up image URLs from the EpicGameMappings table.
    /// </summary>
    private async Task FetchMissingEpicImagesAsync()
    {
        try
        {
            using var scopedDb = _serviceProvider.CreateScopedDbContext();

            // Find Epic downloads that have EpicAppId but missing GameImageUrl
            var downloadsNeedingImages = await scopedDb.DbContext.Downloads
                .Where(d => d.EpicAppId != null && string.IsNullOrEmpty(d.GameImageUrl))
                .Take(100)
                .ToListAsync();

            if (downloadsNeedingImages.Count == 0)
                return;

            // Load all Epic game mappings with image URLs for lookup
            var imageLookup = await scopedDb.DbContext.EpicGameMappings
                .Where(m => m.ImageUrl != null)
                .ToDictionaryAsync(m => m.AppId, m => m.ImageUrl);

            var updated = 0;
            foreach (var download in downloadsNeedingImages)
            {
                if (download.EpicAppId != null && imageLookup.TryGetValue(download.EpicAppId, out var imageUrl) && imageUrl != null)
                {
                    download.GameImageUrl = EpicApiDirectClient.EnsureResizeParams(imageUrl);
                    updated++;
                }
            }

            if (updated > 0)
            {
                await scopedDb.DbContext.SaveChangesAsync();
                _logger.LogInformation("Updated {Count} Epic downloads with game images", updated);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching missing Epic game images - this is non-critical");
        }
    }

    /// <summary>
    /// Auto-tags newly processed downloads to any currently active events and returns the
    /// tagged count. Best-effort: a failure is logged and reported as zero tags, never as an
    /// operation failure. When rows were tagged, emits its own conditional DownloadsRefresh
    /// AFTER the service has committed the associations — DownloadAssociationsContext listens
    /// to DownloadsRefresh (not LogProcessingComplete), and the committed-boundary refresh
    /// fires before this pass runs, so without this emission a client could cache the newly
    /// inserted rows and never invalidate their association state.
    /// </summary>
    private async Task<int> AutoTagNewDownloadsAsync()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var eventsService = scope.ServiceProvider.GetRequiredService<IEventsService>();

            // AutoTagActiveEventsAsync saves its changes before returning the count, so the
            // refresh below is post-commit. NotifyAllAsync only: the cache generation bump
            // must precede the hub send (never fire-and-forget for DownloadsRefresh).
            var taggedCount = await eventsService.AutoTagActiveEventsAsync();
            if (taggedCount > 0)
            {
                _logger.LogInformation("Auto-tagged {Count} downloads to active events", taggedCount);

                await _notifications.NotifyAllAsync(SignalREvents.DownloadsRefresh, new
                {
                    source = "auto-tag",
                    taggedCount,
                    timestamp = DateTime.UtcNow
                });
            }

            return taggedCount;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error auto-tagging downloads to events - this is non-critical");
            return 0;
        }
    }

}
