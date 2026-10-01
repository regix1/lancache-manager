using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using ModelCacheClearOperation = LancacheManager.Models.CacheClearOperation;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;

namespace LancacheManager.Core.Services;

public class CacheClearingService : ScheduledBackgroundService
{
    private readonly ISignalRNotificationService _notifications;
    private readonly IPathResolver _pathResolver;
    private readonly StateService _stateService;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly DatasourceService _datasourceService;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly DatasourceCapabilityService _capabilityService;
    private readonly OperationStateService _operationStateService;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private string _cachePath = null!;
    private CacheDeleteMode _deleteMode;
    private Guid? _currentTrackerOperationId;

    protected override string ServiceName => "CacheClearingService";
    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);
    public override bool DefaultRunOnStartup => true;

    public override string ServiceKey => "cacheClearing";

    public CacheClearingService(
        ILogger<CacheClearingService> logger,
        ISignalRNotificationService notifications,
        IConfiguration configuration,
        IPathResolver pathResolver,
        StateService stateService,
        RustProcessHelper rustProcessHelper,
        DatasourceService datasourceService,
        IUnifiedOperationTracker operationTracker,
        DatasourceCapabilityService capabilityService,
        OperationStateService operationStateService)
        : base(logger, configuration)
    {
        _notifications = notifications;
        _pathResolver = pathResolver;
        _stateService = stateService;
        _rustProcessHelper = rustProcessHelper;
        _datasourceService = datasourceService;
        _operationTracker = operationTracker;
        _capabilityService = capabilityService;
        _operationStateService = operationStateService;

        _deleteMode = CacheDeleteMode.Preserve;

        LoadStateOverrides(stateService);
    }

    protected override Task OnStartupAsync(CancellationToken stoppingToken)
    {
        // Resolve cache path at startup instead of in the constructor to avoid
        // blocking DI when datasource resolution depends on external resources
        var primaryCachePath = _datasourceService.ResolvePrimaryCachePath();
        if (primaryCachePath != null)
        {
            _cachePath = primaryCachePath;
            _logger.LogInformation("Using cache path from default datasource: {CachePath}", _cachePath);
        }
        else
        {
            _cachePath = DetectLegacyCachePath(_configuration);
        }

        _logger.LogInformation("CacheClearingService initialized with {Count} datasource(s)", _datasourceService.DatasourceCount);

        LoadOperations();
        return Task.CompletedTask;
    }

    protected override Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        SaveAllOperations();

        // Cancel all active cache clearing operations
        var activeOperations = _operationTracker.GetActiveOperations(OperationType.CacheClearing);
        foreach (var operation in activeOperations)
        {
            _operationTracker.CancelOperation(operation.Id);
        }
    }

    public async Task<Guid?> StartCacheClearAsync(string? datasourceName = null)
    {
        await _startLock.WaitAsync();
        try
        {
            // Use "all" as the key when clearing all datasources
            var trackerKey = datasourceName ?? "all";

            // Check for any active cache clearing operations
            var activeOperations = _operationTracker.GetActiveOperations(OperationType.CacheClearing);
            if (activeOperations.Any())
            {
                var activeOperation = activeOperations.First();
                _logger.LogWarning("Cache clear is already running: {OperationId}", activeOperation.Id);
                return null; // Return null to indicate operation already running
            }

            var cts = new CancellationTokenSource();

            // Register with unified operation tracker for centralized cancellation
            var metadata = new CacheClearingMetrics
            {
                EntityKey = trackerKey,
                DatasourceName = datasourceName
            };
            // operationId is filled in right after RegisterOperation returns; the closure fires later
            // (at completion) so it reads the assigned value by reference-capture of this local.
            var capturedOperationId = Guid.Empty;
            CacheClearComplete? completion = null;
            _currentTrackerOperationId = _operationTracker.RegisterOperation(
                OperationType.CacheClearing,
                "Cache Clearing",
                cts,
                metadata,
                onTerminalCleanup: () =>
                {
                    if (_currentTrackerOperationId == capturedOperationId)
                        _currentTrackerOperationId = null;
                },
                onTerminalEmit: info => _notifications.NotifyAllAsync(
                    SignalREvents.CacheClearingComplete,
                    BuildClearCompleteEvent(capturedOperationId, info, completion)),
                ownerCompletes: true
            );
            var operationId = _currentTrackerOperationId.Value;
            capturedOperationId = operationId;

            // Update the initial message
            var initialMessage = datasourceName != null
                ? $"Initializing cache clear for {datasourceName}..."
                : "Initializing cache clear...";
            _operationTracker.UpdateProgress(operationId, 0, initialMessage);

            SaveOperationToState(operationId);

            _logger.LogInformation($"Starting cache clear operation {operationId}" +
                (datasourceName != null ? $" for datasource: {datasourceName}" : " for all datasources"));

            // Start the clear operation on a background thread
            _ = Task.Run(async () => await RunCacheClearAsync(operationId, datasourceName,
                value => completion = value), CancellationToken.None);

            return operationId;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task RunCacheClearAsync(Guid operationId, string? datasourceName,
        Action<CacheClearComplete> publish)
    {
        var clearedDatasourceNames = new List<string>();
        var repairPrepared = false;
        try
        {
            _logger.LogInformation($"Executing cache clear operation {operationId}");

            // Send started event
            await _notifications.NotifyAllAsync(SignalREvents.CacheClearingStarted, new
            {
                OperationId = operationId,
                StageKey = "signalr.cacheClear.initializing"
            });

            _operationTracker.UpdateProgress(operationId, 0, "Checking permissions...");
            await ReportProgressAsync(operationId);

            _datasourceService.RefreshPermissions();
            var allDatasources = _datasourceService.GetDatasources()
                .Where(ds => ds.Enabled && !string.IsNullOrEmpty(ds.CachePath))
                .ToList();

            List<ResolvedDatasource> datasources;
            if (!string.IsNullOrEmpty(datasourceName))
            {
                // Filter to specific datasource
                datasources = allDatasources
                    .Where(ds => ds.Name.Equals(datasourceName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (!datasources.Any())
                {
                    var errorMessage = $"Datasource '{datasourceName}' not found";

                    // Mark operation as complete (failed) in unified tracker
                    _operationTracker.CompleteOperation(operationId, success: false, error: errorMessage);
                    if (_currentTrackerOperationId == operationId) _currentTrackerOperationId = null;

                    await ReportProgressAsync(operationId);
                    SaveOperationToState(operationId);

                    return;
                }

                _logger.LogInformation($"Cache clear will process specific datasource: {datasourceName}");
            }
            else
            {
                datasources = allDatasources;
                _logger.LogInformation($"Cache clear will process {datasources.Count} datasource(s)");
            }

            if (datasources.Count == 0)
            {
                var errorMessage = "No enabled datasource is configured for cache clearing";
                _operationTracker.CompleteOperation(operationId, success: false, error: errorMessage);
                if (_currentTrackerOperationId == operationId) _currentTrackerOperationId = null;
                await ReportProgressAsync(operationId);
                SaveOperationToState(operationId);
                return;
            }

            var blockedDatasource = datasources.FirstOrDefault(datasource =>
                !Directory.Exists(datasource.CachePath) || !datasource.CacheWritable);
            if (blockedDatasource is not null)
            {
                var errorMessage =
                    $"Datasource '{blockedDatasource.Name}' cache root is missing or read-only";
                _operationTracker.CompleteOperation(operationId, success: false, error: errorMessage);
                if (_currentTrackerOperationId == operationId) _currentTrackerOperationId = null;
                await ReportProgressAsync(operationId);
                SaveOperationToState(operationId);
                return;
            }

            // Collect all valid cache paths with their directory counts
            var validCachePaths = new List<(string Name, string Path, int DirCount)>();
            foreach (var ds in datasources)
            {
                if (!Directory.Exists(ds.CachePath))
                {
                    _logger.LogWarning($"Cache path does not exist for datasource {ds.Name}: {ds.CachePath}");
                    continue;
                }

                var cacheSubdirs = Directory.GetDirectories(ds.CachePath)
                    .Where(d =>
                    {
                        var name = Path.GetFileName(d);
                        return name.Length == 2 && IsHex(name);
                    }).ToList();

                if (cacheSubdirs.Any())
                {
                    validCachePaths.Add((ds.Name, ds.CachePath, cacheSubdirs.Count));
                    _logger.LogInformation($"Datasource {ds.Name}: {cacheSubdirs.Count} cache directories at {ds.CachePath}");
                }
                else
                {
                    // An existing root with no hex subdirectories contributes a zero-directory
                    // clear result. No native mutation starts, so this path creates no populated-root
                    // receipt or eviction exemption.
                    validCachePaths.Add((ds.Name, ds.CachePath, 0));
                    _logger.LogInformation($"Datasource {ds.Name}: no cache directories found at {ds.CachePath}; native clear will be skipped");
                }
            }

            if (!validCachePaths.Any())
            {
                var error = "Cache path does not exist for any datasource";
                _logger.LogWarning("Cache clear operation {OperationId} failed: {Error}", operationId, error);

                // Mark operation as complete (failed) in unified tracker.
                // Terminal CacheClearingComplete (failed) is emitted by the onTerminalEmit closure.
                _operationTracker.CompleteOperation(operationId, success: false, error: error);
                if (_currentTrackerOperationId == operationId) _currentTrackerOperationId = null;

                await ReportProgressAsync(operationId);

                SaveOperationToState(operationId);

                return;
            }

            // Calculate total directories across all datasources
            var totalDirectoriesAllDatasources = validCachePaths.Sum(p => p.DirCount);

            // Update total directories in metrics
            _operationTracker.UpdateMetadata(operationId, (object meta) =>
            {
                var metrics = (CacheClearingMetrics)meta;
                metrics.TotalDirectories = totalDirectoriesAllDatasources;
            });

            _logger.LogInformation($"Total cache directories to clear across all datasources: {totalDirectoriesAllDatasources}");

            // Use Rust binary for fast cache clearing
            var operationsDir = _pathResolver.GetOperationsDirectory();
            var rustBinaryPath = _pathResolver.GetRustCacheCleanerPath();

            if (!File.Exists(rustBinaryPath))
            {
                var error = $"Rust cache_cleaner binary not found at {rustBinaryPath}";

                // Mark operation as complete (failed) in unified tracker.
                // Terminal CacheClearingComplete (failed) is emitted by the onTerminalEmit closure.
                _operationTracker.CompleteOperation(operationId, success: false, error: error);
                if (_currentTrackerOperationId == operationId) _currentTrackerOperationId = null;

                await ReportProgressAsync(operationId);

                SaveOperationToState(operationId);

                return;
            }

            _logger.LogInformation($"Using Rust cache cleaner: {rustBinaryPath}");

            var clearSources = validCachePaths
                .Select(path => datasources.Single(source =>
                    source.Name.Equals(path.Name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var trackedOperation = _operationTracker.GetOperation(operationId)
                ?? throw new InvalidOperationException($"Cache clear {operationId} is not tracked.");
            await _operationStateService.PrepareRepairAsync(
                new OperationRepair
                {
                    Id = operationId,
                    Type = OperationType.CacheClearing,
                    Name = trackedOperation.Name,
                    StartedAt = trackedOperation.StartedAt,
                    Notice = trackedOperation.Notice,
                    Sources = clearSources.Select(source => new OperationRepairSource
                    {
                        Datasource = source.Name,
                        LogRoot = source.LogPath,
                        CacheRoot = source.CachePath,
                        KeyScheme = _capabilityService.GetKeySchemeWireValue(source),
                        ReceiptPath = Path.Combine(
                            source.CachePath,
                            $".lancache-repair-{operationId:N}.json"),
                        RefreshDownloads = true,
                        ReconcileCache = true,
                        RefreshDetection = true,
                        InvalidateCorruption = true
                    }).ToList(),
                    CacheClearing = new CacheClearingRepair
                    {
                        EntityKey = datasourceName ?? "all",
                        DatasourceName = datasourceName
                    }
                },
                trackedOperation.CancellationTokenSource?.Token ?? CancellationToken.None);
            repairPrepared = true;

            _operationTracker.UpdateProgress(operationId, 0, "Starting cache clear...");
            await ReportProgressAsync(operationId);
            SaveOperationToState(operationId);

            // Track aggregate totals across all datasources
            var totalBytesDeleted = 0L;
            var totalFilesDeleted = 0L;
            var totalDirsProcessed = 0;
            var dirsProcessedBefore = 0;

            // Get operation for CancellationToken access
            var operation = _operationTracker.GetOperation(operationId);
            var cancellationToken = operation?.CancellationTokenSource?.Token ?? CancellationToken.None;

            // Process each datasource cache path sequentially
            for (var dsIndex = 0; dsIndex < validCachePaths.Count; dsIndex++)
            {
                var (dsName, cachePath, dirCount) = validCachePaths[dsIndex];
                _operationTracker.UpdateMetadata(operationId, (object meta) =>
                {
                    ((CacheClearingMetrics)meta).DatasourceName = dsName;
                });

                // A zero-directory result has no native mutation, populated-root receipt, or
                // eviction exemption.
                if (dirCount == 0)
                {
                    _logger.LogInformation($"Datasource {dsName}: no cache directories found; skipping native clear ({dsIndex + 1}/{validCachePaths.Count})");
                    clearedDatasourceNames.Add(dsName);
                    continue;
                }

                var progressFile = Path.Combine(operationsDir, $"cache_clear_progress_{operationId}_{dsIndex}.json");

                _logger.LogInformation($"Clearing cache for datasource {dsName} ({dsIndex + 1}/{validCachePaths.Count}): {cachePath}");
                var percentSoFar = (double)dsIndex / validCachePaths.Count * 100;
                _operationTracker.UpdateProgress(operationId, percentSoFar, $"Clearing {dsName} cache ({dsIndex + 1}/{validCachePaths.Count})...");
                await ReportProgressAsync(operationId);

                // Check for cancellation before starting each datasource
                operation = _operationTracker.GetOperation(operationId);
                if (operation?.CancellationTokenSource?.Token.IsCancellationRequested == true)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                await _operationStateService.StartWorkAsync(
                    operationId,
                    dsName,
                    cancellationToken);

                // Build arguments - Rust auto-detects optimal thread count. --progress enables
                // cache_clear.rs's live stdout progress events, which the hybrid callback below
                // waits on; without it ProgressReporter.is_enabled() is false and no events flow.
                var arguments = $"\"{cachePath}\" \"{progressFile}\" {_deleteMode.ToWireString()} --progress --operation-id \"{operationId}\"";

                var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                    rustBinaryPath,
                    arguments);

                var lastLoggedDirs = 0;
                var lastLogTime = DateTime.UtcNow;
                var lastProgressEmitTicks = long.MinValue;
                string? lastProgressEmitStageKey = null;

                var result = await _rustProcessHelper.ExecuteTrackedProcessWithProgressEventsAsync(
                    startInfo,
                    operationId,
                    cancellationToken,
                    async _ =>
                    {
                        // cache_clear.rs's live stdout event only carries processed/totalDirs/
                        // activeCount in its free-form context bag (confirmed by reading
                        // cache_clear.rs directly) — it does not carry BytesDeleted/FilesDeleted/
                        // the active-directory-name list this callback depends on, which stay
                        // file-only. Each live stdout tick now triggers exactly one authoritative
                        // read of the (Rust-side-unchanged) progress file instead of blindly
                        // polling it every DefaultProgressPollMs regardless of whether anything
                        // changed — trading the poll-interval latency/wasted reads for an
                        // event-driven read the instant Rust reports a genuine tick, with zero
                        // loss of the fields below.
                        var progressData = await _rustProcessHelper.ReadProgressFileAsync<RustCacheProgress>(progressFile);
                        if (progressData == null)
                        {
                            return;
                        }

                        var currentDirsProcessed = dirsProcessedBefore + progressData.DirectoriesProcessed;
                        var currentBytesDeleted = totalBytesDeleted + (long)progressData.BytesDeleted;
                        var currentFilesDeleted = totalFilesDeleted + (long)progressData.FilesDeleted;
                        var percentComplete = (double)currentDirsProcessed / totalDirectoriesAllDatasources * 100;

                        var stageKey = progressData.StageKey;
                        var context = progressData.Context == null ? null : new Dictionary<string, object?>(progressData.Context);
                        var accepted = false;
                        _operationTracker.UpdateProgress(operationId, percentComplete, stageKey ?? string.Empty, onProgress: current =>
                        {
                            var metricsToUpdate = (CacheClearingMetrics)current.Metadata!;
                            metricsToUpdate.DirectoriesProcessed = currentDirsProcessed;
                            metricsToUpdate.BytesDeleted = currentBytesDeleted;
                            metricsToUpdate.FilesDeleted = currentFilesDeleted;
                            metricsToUpdate.CurrentStageKey = stageKey;
                            metricsToUpdate.CurrentContext = context;
                            accepted = true;
                        });
                        if (!accepted) return;

                        // Gate the per-tick broadcast (tracker/metadata updates above stay
                        // per-tick for recovery accuracy): rust can tick many times per second
                        // and every emit re-renders every client. Emit on stage change or at
                        // most every 250ms; milestone and terminal notifications elsewhere in
                        // this method are never gated.
                        var nowTicks = Environment.TickCount64;
                        if (progressData.StageKey != lastProgressEmitStageKey ||
                            nowTicks - lastProgressEmitTicks >= RustProcessHelper.ProgressEmitMinIntervalMs)
                        {
                            lastProgressEmitStageKey = progressData.StageKey;
                            lastProgressEmitTicks = nowTicks;
                            await ReportProgressAsync(operationId);
                        }

                        var timeSinceLastLog = DateTime.UtcNow - lastLogTime;
                        var dirsChanged = progressData.DirectoriesProcessed != lastLoggedDirs;
                        var shouldLog =
                            (dirsChanged && progressData.DirectoriesProcessed % 5 == 0) ||
                            (dirsChanged && timeSinceLastLog.TotalSeconds >= 3) ||
                            (!dirsChanged && timeSinceLastLog.TotalSeconds >= 30);

                        if (shouldLog)
                        {
                            var activeInfo = progressData.ActiveCount > 0
                                ? $" | Active: {progressData.ActiveCount} [{string.Join(", ", progressData.ActiveDirectories)}]"
                                : "";
                            _logger.LogInformation($"[{GetDeleteModeDisplayName()}] Cache Clear Progress: {percentComplete:F1}% complete - {currentDirsProcessed}/{totalDirectoriesAllDatasources} directories cleared{activeInfo}");
                            lastLoggedDirs = progressData.DirectoriesProcessed;
                            lastLogTime = DateTime.UtcNow;
                        }

                        if (progressData.DirectoriesProcessed % 10 == 0)
                        {
                            SaveOperationToState(operationId);
                        }
                    },
                    "cache_cleaner");

                result.EnsureSuccess("cache_cleaner", dsName, cancellationToken);

                _logger.LogInformation($"[{GetDeleteModeDisplayName()}] Rust cache cleaner output: {result.Output}");

                var finalProgress = await _rustProcessHelper.ReadProgressFileAsync<RustCacheProgress>(progressFile);
                if (finalProgress != null)
                {
                    totalBytesDeleted += (long)finalProgress.BytesDeleted;
                    totalFilesDeleted += (long)finalProgress.FilesDeleted;
                    totalDirsProcessed += finalProgress.DirectoriesProcessed;
                    dirsProcessedBefore = totalDirsProcessed;

                    _operationTracker.UpdateMetadata(operationId, (object meta) =>
                    {
                        var metricsToUpdate = (CacheClearingMetrics)meta;
                        metricsToUpdate.DirectoriesProcessed = totalDirsProcessed;
                        metricsToUpdate.BytesDeleted = totalBytesDeleted;
                        metricsToUpdate.FilesDeleted = totalFilesDeleted;
                    });
                }

                await _operationStateService.SaveRepairAsync(
                    operationId,
                    repair =>
                    {
                        var source = repair.Sources.Single(item =>
                            item.Datasource.Equals(dsName, StringComparison.OrdinalIgnoreCase));
                        source.NativeCompletionAccepted = true;
                        repair.CacheClearing = new CacheClearingRepair
                        {
                            EntityKey = datasourceName ?? "all",
                            DatasourceName = datasourceName,
                            CurrentStageKey = finalProgress?.StageKey,
                            CurrentContext = finalProgress?.Context is null
                                ? null
                                : new Dictionary<string, object?>(finalProgress.Context),
                            DirectoriesProcessed = totalDirsProcessed,
                            TotalDirectories = totalDirectoriesAllDatasources,
                            BytesDeleted = totalBytesDeleted,
                            FilesDeleted = totalFilesDeleted,
                            DatasourcesCleared = clearedDatasourceNames.Count + 1
                        };
                    },
                    cancellationToken);

                await _rustProcessHelper.DeleteTempFileAsync(progressFile);
                _logger.LogInformation($"Completed clearing {dsName} cache: {finalProgress?.DirectoriesProcessed ?? 0} directories");
                clearedDatasourceNames.Add(dsName);
            }

            var datasourceNames = string.Join(", ", validCachePaths.Select(p => p.Name));
            var successMessage = validCachePaths.Count > 1
                ? $"Successfully cleared {totalDirsProcessed} cache directories across {validCachePaths.Count} datasources ({datasourceNames})"
                : $"Successfully cleared {totalDirsProcessed} cache directories";

            // Compute duration BEFORE CompleteOperation: the onTerminalEmit closure fires inside
            // CompleteOperation, so the completion metrics must be captured by value first.
            operation = _operationTracker.GetOperation(operationId);
            var duration = operation != null
                ? (DateTime.UtcNow - operation.StartedAt).TotalSeconds
                : 0;

            var completion = new CacheClearComplete(
                OperationId: operationId, Success: true, Status: OperationStatus.Completed,
                Message: successMessage, Cancelled: false,
                FilesDeleted: (int)totalFilesDeleted, DirectoriesProcessed: totalDirsProcessed,
                BytesDeleted: totalBytesDeleted, DatasourcesCleared: validCachePaths.Count,
                Duration: duration);

            // The next size scan estimates this mode from this run instead of from the scanner's
            // synthetic benchmark. An already-empty cache, or a run whose tracker entry is gone,
            // measured nothing.
            if (totalFilesDeleted > 0 && duration > 0)
            {
                _stateService.SetCacheClearRate(_deleteMode, new CacheClearRate
                {
                    FilesDeleted = totalFilesDeleted,
                    DurationSeconds = duration
                });
            }

            await _operationStateService.SaveRepairAsync(
                operationId,
                repair =>
                {
                    repair.CacheClearing = new CacheClearingRepair
                    {
                        EntityKey = datasourceName ?? "all",
                        DatasourceName = datasourceName,
                        DirectoriesProcessed = totalDirsProcessed,
                        TotalDirectories = totalDirectoriesAllDatasources,
                        BytesDeleted = totalBytesDeleted,
                        FilesDeleted = totalFilesDeleted,
                        DatasourcesCleared = clearedDatasourceNames.Count,
                        Duration = duration
                    };
                },
                CancellationToken.None);
            await FinishClearRepairAsync(
                operationId,
                success: true,
                cancelled: false,
                error: null);

            // Mark operation as complete in unified tracker (emits CacheClearingComplete via onTerminalEmit)
            _operationTracker.CompleteOperation(operationId, success: true,
                onCompleting: _ => publish(completion));

            _logger.LogInformation($"Cache clear completed in {duration:F1} seconds - Cleared {totalDirsProcessed} directories across {validCachePaths.Count} datasource(s)");

            await ReportProgressAsync(operationId);

            // Terminal CacheClearingComplete is emitted by the onTerminalEmit closure inside
            // CompleteOperation above (exactly-once, CompletedFlag-gated).

            SaveOperationToState(operationId);
        }
        catch (OperationCanceledException)
        {
            // Handle cancellation gracefully - this is expected when user cancels
            _logger.LogInformation("Cache clear operation {OperationId} was cancelled", operationId);

            if (repairPrepared)
            {
                await FinishClearRepairAsync(
                    operationId,
                    success: false,
                    cancelled: true,
                    error: null);
            }

            // If a universal force-kill already completed this op, the CompletedFlag-gated
            // CompleteOperation below is a no-op and the onTerminalEmit closure does not re-fire.
            if (_operationTracker.GetOperation(operationId)?.Status.IsTerminal() != true)
            {
                // Mark operation as complete (cancelled) in unified tracker.
                // Terminal CacheClearingComplete (cancelled) is emitted by the onTerminalEmit closure.
                _operationTracker.CompleteOperation(operationId, success: false, cancelled: true);

                await ReportProgressAsync(operationId);
            }

            SaveOperationToState(operationId);
        }
        catch (Exception ex)
        {
            // Distinguish between expected failures and unexpected errors
            var isExpectedFailure = ex.Message.Contains("No cache directories found") ||
                                   ex.Message.Contains("Cache path does not exist");

            if (isExpectedFailure)
            {
                _logger.LogWarning(ex, "Cache clear operation {OperationId} failed", operationId);
            }
            else
            {
                _logger.LogError(ex, "Error in cache clear operation {OperationId}", operationId);
            }

            var failureMessage = clearedDatasourceNames.Count > 0
                ? $"Cache clear failed after clearing {string.Join(", ", clearedDatasourceNames)}: {ex.Message}"
                : $"Cache clear failed: {ex.Message}";

            if (repairPrepared)
            {
                await FinishClearRepairAsync(
                    operationId,
                    success: false,
                    cancelled: false,
                    error: failureMessage);
            }

            // Mark operation as complete (failed) in unified tracker.
            // Terminal CacheClearingComplete (failed) is emitted by the onTerminalEmit closure,
            // which reads this error string from OperationTerminalInfo.Error.
            _operationTracker.CompleteOperation(operationId, success: false, error: failureMessage);
            if (_currentTrackerOperationId == operationId) _currentTrackerOperationId = null;

            await ReportProgressAsync(operationId);

            SaveOperationToState(operationId);
        }
    }

    /// <summary>
    /// Invalidates all cache-derived detection projections after a successful filesystem clear.
    /// Candidate rows are deleted before scan headers, and the caller sees either the complete
    /// invalidation or the original current/history snapshots because every delete shares one transaction.
    /// </summary>
    internal static async Task<(
        int Games,
        int Services,
        int CorruptionCandidates,
        int CorruptionScans,
        int PrefillDepots,
        int PrefillApps)> InvalidateCachedDetectionResultsAsync(
        AppDbContext context,
        CancellationToken cancellationToken)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted,
                cancellationToken);
            try
            {
                var invalidated = await InvalidateCachedDetectionResultsCoreAsync(
                    context,
                    cancellationToken);

                await transaction.CommitAsync(cancellationToken);
                return invalidated;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        });
    }

    private static async Task<(
        int Games,
        int Services,
        int CorruptionCandidates,
        int CorruptionScans,
        int PrefillDepots,
        int PrefillApps)> InvalidateCachedDetectionResultsCoreAsync(
        AppDbContext context,
        CancellationToken cancellationToken)
    {
        // Direct DbContext deletes are deliberate: cache clear wipes whole detection tables,
        // not the load/upsert flow GameCacheDetectionDataService owns.
        var games = await context.CachedGameDetections.ExecuteDeleteAsync(cancellationToken);
        var services = await context.CachedServiceDetections.ExecuteDeleteAsync(cancellationToken);
        var (corruptionCandidates, corruptionScans) =
            await DatabaseService.DeleteCachedCorruptionEvidenceAsync(
                context,
                cancellationToken);
        await context.CachedDetectionSummaries
            .Where(summary => summary.Id == CachedDetectionSummary.SingletonId)
            .ExecuteDeleteAsync(cancellationToken);

        // The prefill "Cached" badges are a record of what prefill put on disk, so the clear that
        // deleted those files falsifies every one of them. Left behind, the game picker reports
        // games as cached that are not, and the daemon skips them on the next run - the user sees
        // a prefill that appears to do nothing. Wiped wholesale like the detection tables above:
        // the row carries no datasource, so there is no per-datasource answer to give, and the
        // safe direction is a badge that under-claims (a re-prefill costs bandwidth) rather than
        // one that over-claims (a game silently never prefills).
        var prefillDepots = await context.PrefillCachedDepots.ExecuteDeleteAsync(cancellationToken);
        var prefillApps = await context.PrefillCachedApps.ExecuteDeleteAsync(cancellationToken);

        return (games, services, corruptionCandidates, corruptionScans, prefillDepots, prefillApps);
    }

    internal static async Task InvalidateStructuralCorruptionStateAsync(
        AppDbContext dbContext,
        IPathResolver pathResolver,
        IReadOnlyCollection<(string DatasourceName, string CachePath)> clearedDatasources,
        CancellationToken cancellationToken)
    {
        // The scanner creates its own tables the first time it runs, so on an install that has
        // never scanned there is nothing to delete and the table does not exist yet. Clearing the
        // cache has to keep working there.
        // Unqualified on purpose, so this resolves through the same search path as the delete
        // below and the two can never disagree about which schema they mean.
        var stateExists = await dbContext.Database
            .SqlQueryRaw<bool>("SELECT to_regclass('structural_namespaces') IS NOT NULL AS \"Value\"")
            .SingleAsync(cancellationToken);
        if (!stateExists)
        {
            return;
        }

        var scopes = clearedDatasources
            .Select(item => pathResolver.GetStructuralCorruptionStateScope(
                item.DatasourceName,
                item.CachePath))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // One statement for every cleared root is the fail-closed boundary: either all of the
        // pre-clear baselines are gone or none are, so a later Incremental can never reuse a
        // baseline describing files the clear has already deleted. The runs and per-file rows
        // beneath each namespace go with it through ON DELETE CASCADE.
        await dbContext.Database.ExecuteSqlRawAsync(
            "DELETE FROM structural_namespaces WHERE scope = ANY({0})",
            [scopes],
            cancellationToken);
    }

    public Task RestoreRepairAsync(OperationRepair repair, CancellationToken stoppingToken)
    {
        stoppingToken.ThrowIfCancellationRequested();
        if (repair.Type != OperationType.CacheClearing)
        {
            throw new InvalidOperationException(
                $"Operation type {repair.Type} is not owned by cache clearing.");
        }

        var metrics = repair.CacheClearing
            ?? throw new InvalidDataException($"Cache clear repair {repair.Id} has no metrics.");
        var completion = repair.Outcome == OperationStatus.Completed
            ? new CacheClearComplete(
                OperationId: repair.Id,
                Success: true,
                Status: OperationStatus.Completed,
                Message: "Cache clear completed",
                Cancelled: false,
                FilesDeleted: (int)metrics.FilesDeleted,
                DirectoriesProcessed: metrics.DirectoriesProcessed,
                BytesDeleted: metrics.BytesDeleted,
                DatasourcesCleared: metrics.DatasourcesCleared,
                Duration: metrics.Duration)
            : null;
        var cts = new CancellationTokenSource();
        var restored = _operationTracker.TryRestoreOperation(
            repair.Id,
            repair.Type,
            repair.Name,
            cts,
            metrics,
            onTerminalCleanup: () =>
            {
                if (_currentTrackerOperationId == repair.Id)
                {
                    _currentTrackerOperationId = null;
                }
            },
            onTerminalEmit: terminal => _notifications.NotifyAllAsync(
                SignalREvents.CacheClearingComplete,
                BuildClearCompleteEvent(repair.Id, terminal, completion)),
            startedAt: repair.StartedAt,
            notice: repair.Notice,
            ownerCompletes: true);
        if (!restored)
        {
            cts.Dispose();
            return Task.CompletedTask;
        }

        _currentTrackerOperationId = repair.Id;
        return Task.CompletedTask;
    }

    public Task ResumeRepairAsync(
        OperationRepair repair,
        CancellationToken stoppingToken)
    {
        if (repair.Type != OperationType.CacheClearing)
        {
            throw new InvalidOperationException(
                $"Operation type {repair.Type} is not owned by cache clearing.");
        }

        stoppingToken.ThrowIfCancellationRequested();
        _ = repair.CacheClearing
            ?? throw new InvalidDataException(
                $"Cache clearing repair {repair.Id} has no metrics.");
        return Task.CompletedTask;
    }

    private async Task FinishClearRepairAsync(
        Guid operationId,
        bool success,
        bool cancelled,
        string? error)
    {
        var operation = _operationTracker.GetOperation(operationId);
        if (operation?.Status.IsTerminal() == true)
        {
            success = operation.Status == OperationStatus.Completed;
            cancelled = operation.Status == OperationStatus.Cancelled;
            error = operation.Status == OperationStatus.Failed
                ? operation.Message
                : null;
        }

        await _operationStateService.FinishRepairAsync(
            operationId,
            success,
            cancelled,
            error);
    }

    /// <summary>
    /// Builds the strongly-typed terminal payload for the cache-clear operation. Fires EXACTLY ONCE
    /// from inside CompleteOperation (CompletedFlag-gated) for success, cancel, and error alike.
    /// The winning claim publishes the optional completion record for this run.
    /// </summary>
    private static CacheClearComplete BuildClearCompleteEvent(Guid operationId, OperationTerminalInfo info,
        CacheClearComplete? completion)
    {
        if (info.Cancelled)
        {
            return new CacheClearComplete(
                OperationId: operationId,
                Success: false,
                Status: OperationStatus.Cancelled,
                Message: "Cache clear cancelled",
                Cancelled: true);
        }

        if (info.Success)
        {
            return completion ?? new CacheClearComplete(
                OperationId: operationId,
                Success: true,
                Status: OperationStatus.Completed,
                Message: "Cache clear completed",
                Cancelled: false);
        }

        var error = info.Error ?? "Cache clear failed";
        return new CacheClearComplete(
            OperationId: operationId,
            Success: false,
            Status: OperationStatus.Failed,
            Message: error,
            Cancelled: false,
            Error: error);
    }

    // Helper class for deserializing Rust progress data
    private class RustCacheProgress
    {
        public bool IsProcessing { get; set; }
        public double PercentComplete { get; set; }
        public OperationStatus Status { get; set; } = OperationStatus.Pending;

        [System.Text.Json.Serialization.JsonPropertyName("stageKey")]
        public string? StageKey { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("context")]
        public Dictionary<string, object?>? Context { get; set; }

        public int DirectoriesProcessed { get; set; }
        public int TotalDirectories { get; set; }
        public ulong BytesDeleted { get; set; }
        public ulong FilesDeleted { get; set; }
        public List<string> ActiveDirectories { get; set; } = new();
        public int ActiveCount { get; set; }
    }

    /// <summary>
    /// Detects the cache path using legacy configuration when no datasource is available.
    /// Checks configured paths for directories containing two-character hex-named subdirectories.
    /// </summary>
    private string DetectLegacyCachePath(IConfiguration configuration)
    {
        var possiblePaths = new List<string> { _pathResolver.GetCacheDirectory() };

        var configPath = configuration["LanCache:CachePath"];
        if (!string.IsNullOrEmpty(configPath) && !possiblePaths.Contains(configPath))
        {
            possiblePaths.Insert(0, configPath);
        }

        foreach (var path in possiblePaths)
        {
            if (!Directory.Exists(path))
                continue;

            var dirs = Directory.GetDirectories(path);
            var hasHexDirs = dirs.Any(d =>
            {
                var name = Path.GetFileName(d);
                return name.Length == 2 && IsHex(name);
            });

            if (!hasHexDirs)
                continue;

            _logger.LogInformation("Detected cache path: {CachePath}", path);
            return path;
        }

        var fallback = _pathResolver.GetCacheDirectory();
        _logger.LogWarning("No cache detected, using configured path: {CachePath}", fallback);
        return fallback;
    }

    private bool IsHex(string value)
    {
        return value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
    }

    private async Task ReportProgressAsync(Guid operationId)
    {
        try
        {
            var operation = _operationTracker.GetOperation(operationId);
            if (operation == null) return;

            object? progress = null;
            _operationTracker.UpdateProgress(operationId, operation.PercentComplete, operation.Message,
                onProgress: current =>
                {
                    var metrics = current.Metadata as CacheClearingMetrics;
                    progress = new
                    {
                        OperationId = current.Id,
                        PercentComplete = current.PercentComplete,
                        Status = current.Status,
                        StageKey = metrics?.CurrentStageKey,
                        DatasourceName = metrics?.DatasourceName,
                        Context = metrics?.CurrentContext == null ? null : new Dictionary<string, object?>(metrics.CurrentContext),
                        DirectoriesProcessed = metrics?.DirectoriesProcessed ?? 0,
                        TotalDirectories = metrics?.TotalDirectories ?? 0,
                        BytesDeleted = metrics?.BytesDeleted ?? 0L,
                        FilesDeleted = metrics?.FilesDeleted ?? 0L
                    };
                });
            if (progress != null)
                await _notifications.NotifyAllAsync(SignalREvents.CacheClearingProgress, progress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending cache clear progress notification");
        }
    }

    private void LoadOperations()
    {
        try
        {
            var operations = _stateService.GetCacheClearOperations().ToList();
            var modifiedCount = 0;

            foreach (var op in operations)
            {
                // If operation was running when service stopped, mark it as failed
                // (We can't resume Rust processes after restart)
                if (op.Status == OperationStatus.Pending || op.Status == OperationStatus.Running)
                {
                    op.Status = OperationStatus.Failed;
                    op.Error = "Operation interrupted by service restart";
                    op.EndTime = DateTime.UtcNow;
                    op.Message = op.Error;
                    modifiedCount++;
                    _logger.LogWarning($"Cache clear operation {op.Id} was interrupted by restart, marking as failed");
                }
            }

            // Save any modifications
            if (modifiedCount > 0)
            {
                _stateService.UpdateCacheClearOperations(ops =>
                {
                    ops.Clear();
                    ops.AddRange(operations);
                });
            }

            _logger.LogInformation($"Loaded {operations.Count} persisted cache clear operations ({modifiedCount} marked as failed due to restart)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load persisted operations");
        }
    }

    private void SaveOperationToState(Guid operationId)
    {
        try
        {
            var operation = _operationTracker.GetOperation(operationId);
            if (operation == null) return;

            var stateOp = new ModelCacheClearOperation
            {
                Id = operationId,
                Status = operation.Status,
                Message = operation.Message,
                Progress = (int)operation.PercentComplete,
                StartTime = operation.StartedAt,
                EndTime = operation.CompletedAt,
                Error = operation.Status == OperationStatus.Failed ? operation.Message : null,
                DatasourceName = (operation.Metadata as CacheClearingMetrics)?.DatasourceName
            };

            _stateService.UpdateCacheClearOperations(operations =>
            {
                var existing = operations.FirstOrDefault(op => op.Id == operationId);
                if (existing != null)
                {
                    operations.Remove(existing);
                }
                operations.Add(stateOp);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save operation to state");
        }
    }

    private void SaveAllOperations()
    {
        try
        {
            var activeOperations = _operationTracker.GetActiveOperations(OperationType.CacheClearing);
            var newOperations = activeOperations.Select(op => new ModelCacheClearOperation
            {
                Id = op.Id,
                Status = op.Status,
                Message = op.Message,
                Progress = (int)op.PercentComplete,
                StartTime = op.StartedAt,
                EndTime = op.CompletedAt,
                Error = op.Status == OperationStatus.Failed ? op.Message : null,
                DatasourceName = (op.Metadata as CacheClearingMetrics)?.DatasourceName
            }).ToList();

            _stateService.UpdateCacheClearOperations(operations =>
            {
                // Merge active operations with existing completed ones
                var completedOps = operations.Where(o => o.Status.IsTerminal()).ToList();
                operations.Clear();
                operations.AddRange(completedOps);
                operations.AddRange(newOperations);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save all operations to state");
        }
    }

    public CacheClearProgress? GetOperationStatus(Guid operationId)
    {
        // Search through active operations for the one with matching ID
        var operation = _operationTracker.GetOperation(operationId);

        // Also check if it might be a completed operation by checking state
        if (operation == null)
        {
            var stateOps = _stateService.GetCacheClearOperations();
            var stateOp = stateOps.FirstOrDefault(op => op.Id == operationId);
            if (stateOp != null)
            {
                return new CacheClearProgress
                {
                    OperationId = stateOp.Id,
                    Status = stateOp.Status,
                    StatusMessage = stateOp.Message,
                    StartTime = stateOp.StartTime,
                    EndTime = stateOp.EndTime,
                    Error = stateOp.Error,
                    DatasourceName = stateOp.DatasourceName,
                    PercentComplete = stateOp.Progress
                };
            }
            return null;
        }

        // Get metrics from tracker metadata
        var metrics = operation.Metadata as CacheClearingMetrics;

        return new CacheClearProgress
        {
            OperationId = operation.Id,
            Status = operation.Status,
            StatusMessage = operation.Message,
            StartTime = operation.StartedAt,
            EndTime = operation.CompletedAt,
            DirectoriesProcessed = metrics?.DirectoriesProcessed ?? 0,
            TotalDirectories = metrics?.TotalDirectories ?? 0,
            BytesDeleted = metrics?.BytesDeleted ?? 0,
            FilesDeleted = metrics?.FilesDeleted ?? 0,
            Error = operation.Status == OperationStatus.Failed ? operation.Message : null,
            DatasourceName = metrics?.DatasourceName,
            PercentComplete = operation.PercentComplete
        };
    }

    /// <summary>
    /// Gets all active cache clear operations (wrapper for GetAllOperations)
    /// </summary>
    public List<CacheClearProgress> GetActiveOperations()
    {
        return _operationTracker.GetActiveOperations(OperationType.CacheClearing)
            .Select(op =>
            {
                var metrics = op.Metadata as CacheClearingMetrics;
                return new CacheClearProgress
                {
                    OperationId = op.Id,
                    Status = op.Status,
                    StatusMessage = op.Message,
                    StartTime = op.StartedAt,
                    EndTime = op.CompletedAt,
                    DirectoriesProcessed = metrics?.DirectoriesProcessed ?? 0,
                    TotalDirectories = metrics?.TotalDirectories ?? 0,
                    BytesDeleted = metrics?.BytesDeleted ?? 0,
                    FilesDeleted = metrics?.FilesDeleted ?? 0,
                    Error = op.Status == OperationStatus.Failed ? op.Message : null,
                    DatasourceName = metrics?.DatasourceName,
                    PercentComplete = op.PercentComplete
                };
            }).ToList();
    }

    /// <summary>
    /// Gets cache clear status for a specific operation (wrapper for GetOperationStatus)
    /// </summary>
    public CacheClearProgress? GetCacheClearStatus(Guid operationId)
    {
        return GetOperationStatus(operationId);
    }

    public List<CacheClearProgress> GetAllOperations()
    {
        // Combine active operations from tracker with completed operations from state
        var activeOps = _operationTracker.GetActiveOperations(OperationType.CacheClearing)
            .Select(op =>
            {
                var metrics = op.Metadata as CacheClearingMetrics;
                return new CacheClearProgress
                {
                    OperationId = op.Id,
                    Status = op.Status,
                    StatusMessage = op.Message,
                    StartTime = op.StartedAt,
                    EndTime = op.CompletedAt,
                    DirectoriesProcessed = metrics?.DirectoriesProcessed ?? 0,
                    TotalDirectories = metrics?.TotalDirectories ?? 0,
                    BytesDeleted = metrics?.BytesDeleted ?? 0,
                    FilesDeleted = metrics?.FilesDeleted ?? 0,
                    Error = op.Status == OperationStatus.Failed ? op.Message : null,
                    DatasourceName = metrics?.DatasourceName,
                    PercentComplete = op.PercentComplete
                };
            }).ToList();

        // Add completed operations from state that aren't currently active
        var stateOps = _stateService.GetCacheClearOperations()
            .Where(so => !activeOps.Any(ao => ao.OperationId == so.Id))
            .Select(op => new CacheClearProgress
            {
                OperationId = op.Id,
                Status = op.Status,
                StatusMessage = op.Message,
                StartTime = op.StartTime,
                EndTime = op.EndTime,
                Error = op.Error,
                DatasourceName = op.DatasourceName,
                PercentComplete = op.Progress
            }).ToList();

        return activeOps.Concat(stateOps).ToList();
    }

    public void SetDeleteMode(CacheDeleteMode deleteMode)
    {
        _deleteMode = deleteMode;
        _logger.LogInformation($"Cache clear delete mode updated to {deleteMode.ToWireString()}");
    }

    public CacheDeleteMode GetDeleteMode()
    {
        return _deleteMode;
    }

    private string GetDeleteModeDisplayName()
    {
        return _deleteMode.ToDisplayName();
    }

    public async Task<bool> IsRsyncAvailableAsync()
    {
        // Rsync only available on Linux
        if (!OperatingSystemDetector.IsLinux)
        {
            return false;
        }

        try
        {
            // Check if rsync command exists
            var startInfo = _rustProcessHelper.CreateProcessStartInfo("which", "rsync");
            var result = await _rustProcessHelper.ExecuteProcessAsync(startInfo, CancellationToken.None);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

}
