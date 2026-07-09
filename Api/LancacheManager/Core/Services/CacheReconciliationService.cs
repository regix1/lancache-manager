using System.Collections.Concurrent;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

/// <summary>
/// Background service that periodically reconciles Download records with actual cache files on disk.
/// Downloads whose cache files have been evicted by nginx are flagged as IsEvicted = true.
/// Downloads whose cache files reappear (re-cached) are un-flagged back to IsEvicted = false.
/// In "remove" mode, evicted records are deleted from the database entirely.
/// The actual cache scanning is performed by the Rust cache_eviction_scan binary.
/// </summary>
public class CacheReconciliationService : ScopedScheduledBackgroundService
{
    private readonly DatasourceService _datasourceService;
    private readonly StateService _stateService;
    private readonly ISignalRNotificationService _notifications;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly IPathResolver _pathResolver;
    private readonly GameCacheDetectionDataService _gameCacheDetectionDataService;
    private readonly GameCacheDetectionService _gameCacheDetectionService;
    private readonly EvictedDetectionPreservationService _evictedDetectionPreservationService;
    private readonly IOperationConflictChecker _conflictChecker;
    private int _isRunning;
    private bool _currentScanIsSilent = true;
    // Broadcast gate shared by the rust stdout-tick callback and NotifyScanProgressAsync.
    // Safe as instance fields: TryBeginRun guarantees at most one scan emits at a time.
    private long _scanProgressLastEmitTicks = long.MinValue;
    private string? _scanProgressLastEmitStageKey;
    /// <summary>
    /// Context dictionary of the most recent eviction-scan progress tick (same shape as the
    /// EvictionScanProgress SignalR payload's Context). The unified tracker only stores the stage
    /// KEY in OperationInfo.Message, so the /api/stats/eviction/scan/status recovery endpoint reads
    /// this to interpolate placeholder-bearing keys like signalr.evictionScan.progress
    /// ({{totalProcessed}}/{{totalEstimate}}). Null when no scan is running or before the first tick.
    /// </summary>
    private volatile Dictionary<string, object?>? _currentScanProgressContext;
    /// <summary>
    /// Set of EvictionRemoval operationIds that should NOT emit SignalR notifications
    /// (Remove-mode auto-cleanup). Helpers consult this before calling NotifyAllAsync so
    /// the user never sees a removal notification for an automatic Remove-mode purge.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, byte> _silentRemovalOperationIds = new();
    /// <summary>
    /// Per-operation terminal metrics for EvictionScan, captured BY VALUE just before
    /// CompleteOperation so the registered onTerminalEmit closure (the SOLE terminal emitter)
    /// can build the typed EvictionScanComplete record. Force-kill bypasses ReconcileCacheFilesAsync,
    /// so a holder may be absent at emit time - the closure falls back to zeroed metrics.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, EvictionScanTerminalState> _evictionScanTerminalStates = new();
    /// <summary>
    /// Per-operation terminal metrics for EvictionRemoval, captured BY VALUE just before
    /// CompleteOperation (in CompleteEvictionRemovalAsync) so the registered onTerminalEmit closure
    /// can build the typed EvictionRemovalComplete record. Force-kill bypasses
    /// CompleteEvictionRemovalAsync, so a holder may be absent - the closure falls back to defaults.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, EvictionRemovalTerminalState> _evictionRemovalTerminalStates = new();
    private readonly TaskCompletionSource<bool> _firstStartupScanComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override string ServiceName => "CacheReconciliationService";
    protected override TimeSpan Interval => TimeSpan.FromHours(6);
    public override bool DefaultRunOnStartup => false;

    public override string ServiceKey => "cacheReconciliation";

    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;
    public bool CurrentScanIsSilent => _currentScanIsSilent;
    public IReadOnlyDictionary<string, object?>? CurrentScanProgressContext => _currentScanProgressContext;

    private bool TryBeginRun() => Interlocked.CompareExchange(ref _isRunning, 1, 0) == 0;

    private void EndRun() => Volatile.Write(ref _isRunning, 0);

    /// <summary>
    /// Completes when the first startup eviction scan (and any RemoveEvictedRecordsAsync cleanup) has finished.
    /// GameDetectionService awaits this before calling GetCachedDetectionAsync to ensure evicted
    /// Downloads have already been upserted into CachedGameDetections before detection reads the DB.
    /// </summary>
    public Task FirstStartupScanComplete => _firstStartupScanComplete.Task;

    /// <summary>
    /// Start reconciliation as a fire-and-forget background task.
    /// Returns the operationId immediately, or null if already running.
    /// Manual scans always show notifications.
    /// </summary>
    public Guid? RunManualAsync()
    {
        if (!TryBeginRun()) return null;

        try
        {
            var cts = new CancellationTokenSource();
            var operationId = RegisterEvictionScanOperation("Eviction Scan", cts);

            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    await ReconcileCacheFilesAsync(context, operationId, cts.Token, silent: false);
                }
                finally
                {
                    EndRun();
                }
            }, cts.Token);

            return operationId;
        }
        catch
        {
            EndRun();
            throw;
        }
    }

    public CacheReconciliationService(
        IServiceProvider serviceProvider,
        ILogger<CacheReconciliationService> logger,
        IConfiguration configuration,
        DatasourceService datasourceService,
        StateService stateService,
        ISignalRNotificationService notifications,
        IUnifiedOperationTracker operationTracker,
        RustProcessHelper rustProcessHelper,
        IPathResolver pathResolver,
        GameCacheDetectionDataService gameCacheDetectionDataService,
        GameCacheDetectionService gameCacheDetectionService,
        EvictedDetectionPreservationService evictedDetectionPreservationService,
        IOperationConflictChecker conflictChecker)
        : base(serviceProvider, logger, configuration)
    {
        _datasourceService = datasourceService;
        _stateService = stateService;
        _notifications = notifications;
        _operationTracker = operationTracker;
        _rustProcessHelper = rustProcessHelper;
        _pathResolver = pathResolver;
        _gameCacheDetectionDataService = gameCacheDetectionDataService;
        _gameCacheDetectionService = gameCacheDetectionService;
        _evictedDetectionPreservationService = evictedDetectionPreservationService;
        _conflictChecker = conflictChecker;

        LoadStateOverrides(stateService);
    }

    protected override bool IsEnabled()
    {
        var rustBinaryPath = _pathResolver.GetRustEvictionScanPath();
        if (!File.Exists(rustBinaryPath))
        {
            _logger.LogWarning("cache_eviction_scan binary not found at {Path}, eviction scanning disabled", rustBinaryPath);
            return false;
        }

        return base.IsEnabled();
    }

    /// <summary>
    /// Automatic scans (startup + scheduled) are silent when:
    ///   - The evicted-data mode is "Remove" (the user has opted into invisible automatic cleanup), OR
    ///   - The "Show scheduled scan notifications" toggle is off.
    /// Manual scans always notify and ignore this helper (per the UI's documented behavior).
    /// </summary>
    private bool IsSilentAutomaticScan()
    {
        var mode = _stateService.GetEvictedDataMode();
        if (mode == EvictedDataMode.Remove.ToWireString())
        {
            return true;
        }

        return !_stateService.GetEvictionScanNotifications();
    }

    /// <summary>
    /// Automatic (startup/scheduled) scans defer to any active heavy operation: heavy data ops
    /// run one at a time (OperationConflictChecker section 1a), and an automatic scan must not
    /// jump ahead of a user-started operation the way a queued controller request never could.
    /// Skip-if-busy (the next scheduled tick retries) mirrors CacheSizeScanScheduledService;
    /// manual scans keep going through the controller's conflict-check + queue path instead.
    /// </summary>
    private async Task<bool> IsBlockedByActiveOperationAsync(string runKind, CancellationToken ct)
    {
        var conflict = await _conflictChecker.CheckAsync(OperationType.EvictionScan, ConflictScope.Bulk(), ct);
        if (conflict == null)
        {
            return false;
        }

        _logger.LogInformation(
            "[EvictionScan] {RunKind} scan skipped: active {ActiveType} operation ({ActiveId}) holds the heavy-op slot",
            runKind, conflict.ActiveOperationType, conflict.ActiveOperationId);
        return true;
    }

    protected override async Task OnStartupAsync(CancellationToken stoppingToken)
    {
        // Wait for setup to complete so datasources and database are configured
        await _stateService.WaitForSetupCompletedAsync(stoppingToken);

        var silent = IsSilentAutomaticScan();

        if (await IsBlockedByActiveOperationAsync("Startup", stoppingToken))
        {
            _firstStartupScanComplete.TrySetResult(true);
            return;
        }

        if (!TryBeginRun())
        {
            _logger.LogWarning("[EvictionScan] Startup scan skipped because another scan is already running");
            _firstStartupScanComplete.TrySetResult(true);
            return;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Skip scan entirely if there are no downloads in the database
            if (!await context.Downloads.AnyAsync(stoppingToken))
            {
                _logger.LogInformation("[EvictionScan] No downloads in database, skipping startup scan");
                return;
            }

            var cts = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, cts.Token);
            var operationId = RegisterEvictionScanOperation("Eviction Scan (Startup)", cts);

            await ReconcileCacheFilesAsync(context, operationId, linked.Token, silent);
        }
        finally
        {
            EndRun();
            // Signal GameDetectionService that the first startup scan (and any removal cleanup) is done.
            // TrySetResult is safe to call multiple times - only the first call has effect.
            _firstStartupScanComplete.TrySetResult(true);
        }
    }

    protected override async Task ExecuteWorkAsync(
        IServiceProvider scopedServices,
        CancellationToken stoppingToken)
    {
        var silent = IsSilentAutomaticScan();
        var context = scopedServices.GetRequiredService<AppDbContext>();

        if (await IsBlockedByActiveOperationAsync("Scheduled", stoppingToken))
        {
            return;
        }

        if (!TryBeginRun())
        {
            _logger.LogDebug("[EvictionScan] Scheduled scan skipped because another scan is already running");
            return;
        }

        // Skip scan if there are no downloads in the database
        try
        {
            if (!await context.Downloads.AnyAsync(stoppingToken))
            {
                _logger.LogDebug("[EvictionScan] No downloads in database, skipping scheduled scan");
                return;
            }

            var cts = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, cts.Token);
            var operationId = RegisterEvictionScanOperation("Eviction Scan", cts);

            await ReconcileCacheFilesAsync(context, operationId, linked.Token, silent);
        }
        finally
        {
            EndRun();
        }
    }

    private async Task ReconcileCacheFilesAsync(AppDbContext context, Guid operationId, CancellationToken stoppingToken, bool silent = false)
    {
        // In Remove mode the scan phase is ALWAYS notification-silent (manual or automatic): the
        // user-visible feedback for a Remove-mode run is the removal bar, not the scan bar. A manual
        // Remove-mode run arrives here with silent:false (RunManualAsync), so we force the scan phase
        // silent independently of the run origin. Non-Remove modes keep the caller's silent flag, so a
        // manual non-Remove scan still shows its scan bar exactly as before.
        var isRemoveMode = _stateService.GetEvictedDataMode() == EvictedDataMode.Remove.ToWireString();
        var scanSilent = silent || isRemoveMode;

        _currentScanIsSilent = scanSilent;
        _currentScanProgressContext = null;
        // Mirror the silent flag onto the terminal-state holder so the registered onTerminalEmit
        // closure suppresses the EvictionScanComplete emit exactly as the old inline guards did.
        if (_evictionScanTerminalStates.TryGetValue(operationId, out var scanTerminalState))
        {
            scanTerminalState.Silent = scanSilent;
        }
        string? datasourceConfigPath = null;
        string? progressFilePath = null;

        try
        {
            _logger.LogInformation("[EvictionScan] Starting eviction scan via Rust binary (silent: {Silent})", scanSilent);

            if (!scanSilent)
            {
                await _notifications.NotifyAllAsync(SignalREvents.EvictionScanStarted, new EvictionScanStarted(
                    StageKey: "signalr.evictionScan.scanning",
                    OperationId: operationId));
            }

            // Write datasource configuration to temp file for the Rust binary
            datasourceConfigPath = Path.GetTempFileName();
            var datasourceConfig = _datasourceService.GetDatasources().Select(ds => new
            {
                name = ds.Name,
                cachePath = ds.CachePath,
                isDefault = ds == _datasourceService.GetDefaultDatasource()
            }).ToArray();
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            await File.WriteAllTextAsync(datasourceConfigPath, JsonSerializer.Serialize(datasourceConfig, jsonOptions), stoppingToken);

            // Create progress file for monitoring
            progressFilePath = Path.GetTempFileName();

            // Hybrid transport (mirrors CacheClearingService): the stdout progress event from
            // cache_eviction_scan.rs is a zero-latency wake-up that triggers exactly one read of
            // the (Rust-side-unchanged) progress file, replacing the previous standalone
            // MonitorProgressFileAsync poll-every-500ms task. Only wired when not silent, same as
            // the old task's `if (!scanSilent)` guard.
            Func<RustProgressEvent, Task>? onProgressEvent = scanSilent
                ? null
                : async _ =>
                {
                    var progress = await _rustProcessHelper.ReadProgressFileAsync<EvictionScanProgressData>(progressFilePath!);
                    if (progress == null)
                    {
                        return;
                    }

                    var stageKey = string.IsNullOrEmpty(progress.StageKey)
                        ? "signalr.evictionScan.progress"
                        : progress.StageKey;
                    var context = BuildScanProgressContext(progress);
                    _currentScanProgressContext = context;

                    // The Rust disk scan owns 0-85% of the bar. The C# post-processing that
                    // follows (detection-row updates, post-scan recovery, and the disk-summary
                    // refresh - minutes on large databases) owns 85-100%. Forwarding the raw
                    // Rust percent left the bar at 99.5% while most of the wall-clock tail was
                    // still ahead.
                    var scaledPercent = progress.PercentComplete * 0.85;

                    _operationTracker.UpdateProgress(operationId, scaledPercent, stageKey);

                    // Gate the broadcast (tracker + recovery context above stay per-tick): rust
                    // ticks can arrive many times per second and every emit re-renders every
                    // client. Emit on stage change or at most every 250ms; the terminal state
                    // travels on EvictionScanComplete, never a gated tick.
                    if (!ShouldEmitScanProgress(stageKey))
                    {
                        return;
                    }

                    await _notifications.NotifyAllAsync(SignalREvents.EvictionScanProgress, new EvictionScanProgress(
                        OperationId: operationId,
                        Status: progress.Status.ToWireString(),
                        StageKey: stageKey,
                        PercentComplete: scaledPercent,
                        Processed: progress.Processed,
                        TotalEstimate: progress.TotalEstimate,
                        Evicted: progress.Evicted,
                        UnEvicted: progress.UnEvicted,
                        Context: context));
                };

            // Execute the Rust binary
            var result = await _rustProcessHelper.RunEvictionScanAsync(
                datasourceConfigPath, progressFilePath, stoppingToken, operationId, onProgressEvent);

            stoppingToken.ThrowIfCancellationRequested();

            // Parse result
            var scanResult = ParseScanResult(result);

            if (scanResult.Success)
            {
                _logger.LogInformation(
                    "[EvictionScan] Scan complete: processed {Total} downloads, {Evicted} newly evicted, {UnEvicted} un-evicted (re-cached)",
                    scanResult.Processed, scanResult.Evicted, scanResult.UnEvicted);

                if (!scanSilent)
                {
                    await NotifyScanProgressAsync(
                        operationId,
                        86.0,
                        "signalr.evictionScan.postProcessing",
                        scanResult);
                }

                stoppingToken.ThrowIfCancellationRequested();

                // Fix 3 Trigger #1: whenever the eviction scan flipped any Downloads rows from
                // IsEvicted=true → IsEvicted=false (cache files reappeared), reverse-reconcile
                // the dependent CachedGameDetections.IsEvicted flag so detection UI reflects
                // the re-cached state without waiting for a new full detection scan.
                if (scanResult.UnEvicted > 0)
                {
                    try
                    {
                        var unevictedCount = await UnevictCachedGameDetectionsAsync(
                            context,
                            _logger,
                            _gameCacheDetectionDataService,
                            _evictedDetectionPreservationService,
                            stoppingToken);
                        if (unevictedCount > 0)
                        {
                            _logger.LogInformation(
                                "[GameDetection] Self-healed {Count} CachedGameDetection rows after eviction scan reported {UnEvicted} un-evicted downloads",
                                unevictedCount, scanResult.UnEvicted);
                        }

                        // Service badges self-heal the same way: when the scan re-cached any
                        // Downloads, clear CachedServiceDetections.IsEvicted for services whose
                        // Downloads are no longer all evicted (keys off Downloads.IsEvicted via
                        // GetServicesToUnevictAsync, not the stale CacheFilesFound snapshot). Runs
                        // in this same try/catch so a service failure does not abort the games heal.
                        var serviceUnevictedCount = await UnevictCachedServiceDetectionsAsync(
                            context,
                            _logger,
                            _gameCacheDetectionDataService,
                            stoppingToken);
                        if (serviceUnevictedCount > 0)
                        {
                            _logger.LogInformation(
                                "[ServiceDetection] Self-healed {Count} CachedServiceDetection rows after eviction scan reported {UnEvicted} un-evicted downloads",
                                serviceUnevictedCount, scanResult.UnEvicted);
                        }
                    }
                    catch (Exception selfHealEx) when (selfHealEx is not OperationCanceledException)
                    {
                        _logger.LogWarning(selfHealEx,
                            "[GameDetection] Reverse-reconcile of CachedGameDetections/CachedServiceDetections failed - will retry next scan");
                    }
                }

                // Opt-in orphan prune: a download with no LogEntries rows produces no probe
                // keys, so the scan classifies it as unverifiable and can never flag it
                // evicted - it lingers in Service Analytics forever (typical after a
                // purge/clear/reprocess cycle). When the setting is on, delete those rows
                // here so the disk-summary refresh below reflects the deletions. The age
                // guard keeps any download the log processor is still filling out safe.
                var prunedOrphans = 0;
                if (_stateService.GetPruneOrphanedDownloads())
                {
                    try
                    {
                        stoppingToken.ThrowIfCancellationRequested();
                        var pruneCutoffUtc = DateTime.UtcNow.AddMinutes(-5);
                        // !IsEvicted: flagged downloads already have a consistent removal path
                        // (Remove all evicted deletes their detection rows too); deleting them
                        // here would strand CachedGame/ServiceDetection rows in the Evicted UI.
                        prunedOrphans = await context.Downloads
                            .Where(d => !d.IsActive
                                     && !d.IsEvicted
                                     && d.EndTimeUtc < pruneCutoffUtc
                                     && !context.LogEntries.Any(le => le.DownloadId == d.Id))
                            .ExecuteDeleteAsync(stoppingToken);
                        if (prunedOrphans > 0)
                        {
                            _logger.LogInformation(
                                "[EvictionScan] Orphan prune: deleted {Count} download(s) with no log entries backing them (unverifiable by the scan)",
                                prunedOrphans);
                        }
                    }
                    catch (Exception pruneEx) when (pruneEx is not OperationCanceledException)
                    {
                        _logger.LogWarning(pruneEx, "[EvictionScan] Orphan prune failed - will retry next scan");
                    }
                }

                // Fix A + B: Propagate eviction to CachedGameDetection and bust the in-memory cache
                if (scanResult.Evicted > 0)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    try
                    {
                        var evictedCount = await EvictCachedGameDetectionsAsync(context, _logger, stoppingToken);
                        _logger.LogInformation("Marked {Count} CachedGameDetection rows as evicted", evictedCount);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to propagate eviction to CachedGameDetection rows");
                    }

                    // Fix for "newly evicted items don't show until restart": after the scan
                    // flags Downloads as evicted, run recovery so any game/service WITHOUT a
                    // matching CachedGameDetection / CachedServiceDetection row gets one
                    // inserted with IsEvicted=true. LoadDetectionFromDatabaseAsync only
                    // returns entities that have detection rows - missing rows = invisible
                    // in the Evicted Items UI. Recovery on startup wasn't enough; the user
                    // needs it on every eviction scan because cache files can be evicted
                    // (via manual clear, nginx cache miss, etc.) at any time.
                    try
                    {
                        stoppingToken.ThrowIfCancellationRequested();
                        var gamesRecovered = await _gameCacheDetectionService.RecoverEvictedGamesAsync(stoppingToken);
                        var servicesRecovered = await _gameCacheDetectionService.RecoverEvictedServicesAsync(stoppingToken);
                        _logger.LogInformation(
                            "[EvictionScan] Post-scan recovery: inserted {Games} game + {Services} service detection rows from Downloads history (zero counts mean every evicted entity already had a row - their evicted_downloads_count will update in-place)",
                            gamesRecovered, servicesRecovered);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[EvictionScan] Post-scan recovery failed - newly-evicted entities may remain hidden until next full scan");
                    }

                    // The disk-summary recompute is the long pole of the whole scan on large
                    // databases. Give it its own labeled stage at 92% and stream the parallel
                    // path-stat counts into 92-99% so the bar visibly moves instead of sitting
                    // frozen while millions of files are checked.
                    if (!scanSilent)
                    {
                        await NotifyScanProgressAsync(
                            operationId,
                            92.0,
                            "signalr.evictionScan.refreshingSummary",
                            scanResult);
                    }

                    // The callback fires from Parallel.ForEach worker threads (already
                    // throttled inside the calculator), so notify fire-and-forget.
                    Action<int, int>? onPathProgress = scanSilent
                        ? null
                        : (statted, totalPaths) =>
                        {
                            var percent = totalPaths > 0
                                ? 92.0 + (statted / (double)totalPaths) * 7.0
                                : 92.0;
                            _ = NotifyScanProgressAsync(
                                operationId,
                                percent,
                                "signalr.evictionScan.refreshingSummaryCounted",
                                scanResult,
                                filesChecked: statted,
                                filesTotal: totalPaths);
                        };

                    await _gameCacheDetectionService.RefreshDiskSummaryAndInvalidateAsync(stoppingToken, onPathProgress);
                }
                else if (prunedOrphans > 0)
                {
                    // The Evicted>0 branch above already covers pruned rows (the prune runs
                    // first); when nothing was newly evicted the prune needs its own refresh
                    // so Service Analytics stops showing the deleted downloads.
                    await _gameCacheDetectionService.RefreshDiskSummaryAndInvalidateAsync(stoppingToken);
                }

                stoppingToken.ThrowIfCancellationRequested();

                // Capture the success metrics BY VALUE and COMPLETE the scan op BEFORE the removal
                // phase runs. Two reasons:
                //   1. Metrics honesty - the scan's terminal EvictionScanComplete carries the real
                //      Processed/Evicted/UnEvicted counts (previously these were set after the removal
                //      had already completed the op, leaving the terminal event with zeroed metrics).
                //   2. Single active bulk op - the scan op (ConflictScope.Bulk) must be completed
                //      before the removal self-registers its own ConflictScope.Bulk op, otherwise the
                //      registration would collide with the still-active scan op.
                if (scanTerminalState != null)
                {
                    scanTerminalState.Processed = scanResult.Processed;
                    scanTerminalState.Evicted = scanResult.Evicted;
                    scanTerminalState.UnEvicted = scanResult.UnEvicted;
                    scanTerminalState.PrunedOrphans = prunedOrphans;
                }
                _operationTracker.CompleteOperation(operationId, success: true);

                // Handle evicted data "remove" mode. The removal self-registers its OWN
                // OperationType.EvictionRemoval operation (operationId: null) so it is cancellable,
                // visible to GET /api/cache/removals/active, and emits its own
                // EvictionRemovalStarted/Progress/Complete events with its own operationId. The
                // removal's silent flag mirrors the run ORIGIN, not the scan phase: automatic runs
                // (silent:true) stay fully silent (no removal bar - May 2026 behavior); manual runs
                // (silent:false) show the removal bar as the sole notification for the run.
                if (isRemoveMode
                    && await context.Downloads.AnyAsync(d => d.IsEvicted, stoppingToken))
                {
                    await RemoveEvictedRecordsAsync(context, stoppingToken, operationId: null, silent: silent);
                }

                // Notify clients to refresh if eviction flags changed or orphans were pruned
                if (scanResult.Evicted > 0 || scanResult.UnEvicted > 0 || prunedOrphans > 0)
                {
                    await _notifications.NotifyAllAsync(SignalREvents.DownloadsRefresh, new
                    {
                        reason = "eviction-scan-complete"
                    });
                }
            }
            else
            {
                var errorMsg = scanResult.Error ?? "Rust eviction scan binary returned failure";
                _logger.LogError("[EvictionScan] Rust binary failed: {Error}", errorMsg);
                // Terminal EvictionScanComplete(error) is emitted by the registered onTerminalEmit closure.
                _operationTracker.CompleteOperation(operationId, success: false, error: errorMsg);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[EvictionScan] Operation {OperationId} was cancelled", operationId);
            // Terminal EvictionScanComplete(cancelled) is emitted by the registered onTerminalEmit closure.
            _operationTracker.CompleteOperation(operationId, success: false, error: "Cancelled by user");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EvictionScan] Error during eviction scan");
            // Terminal EvictionScanComplete(error) is emitted by the registered onTerminalEmit closure.
            _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);
        }
        finally
        {
            _currentScanProgressContext = null;

            // Clean up temp files
            if (datasourceConfigPath != null)
                await _rustProcessHelper.DeleteTempFileAsync(datasourceConfigPath);
            if (progressFilePath != null)
                await _rustProcessHelper.DeleteTempFileAsync(progressFilePath);
        }
    }

    private static Dictionary<string, object?> BuildScanProgressContext(EvictionScanProgressData progress)
    {
        if (progress.Context != null && progress.Context.Count > 0)
        {
            return progress.Context;
        }

        return new Dictionary<string, object?>
        {
            ["totalProcessed"] = progress.Processed,
            ["totalEstimate"] = progress.TotalEstimate
        };
    }

    /// <summary>
    /// Registers an EvictionScan operation whose terminal SignalR event fires EXACTLY ONCE from
    /// inside CompleteOperation (via onTerminalEmit). A mutable terminal-state holder is created up
    /// front and captured by value; ReconcileCacheFilesAsync fills it just before CompleteOperation.
    /// Silent scans suppress the terminal emit (parity with the old inline `if (!silent)` guards).
    /// </summary>
    private Guid RegisterEvictionScanOperation(string name, CancellationTokenSource cts)
    {
        var terminalState = new EvictionScanTerminalState();
        Guid operationId = default;
        operationId = _operationTracker.RegisterOperation(
            OperationType.EvictionScan,
            name,
            cts,
            onTerminalCleanup: () => _evictionScanTerminalStates.TryRemove(operationId, out _),
            onTerminalEmit: info =>
            {
                // Silent scans never surfaced a terminal notification - preserve that exactly.
                if (terminalState.Silent)
                {
                    return Task.CompletedTask;
                }

                if (info.Cancelled)
                {
                    return _notifications.NotifyAllAsync(SignalREvents.EvictionScanComplete, new EvictionScanComplete(
                        Success: false,
                        OperationId: operationId,
                        StageKey: "signalr.evictionScan.complete",
                        Processed: 0,
                        Evicted: 0,
                        UnEvicted: 0,
                        Error: "Cancelled by user"));
                }

                if (info.Success)
                {
                    return _notifications.NotifyAllAsync(SignalREvents.EvictionScanComplete, new EvictionScanComplete(
                        Success: true,
                        OperationId: operationId,
                        StageKey: "signalr.evictionScan.complete",
                        Processed: terminalState.Processed,
                        Evicted: terminalState.Evicted,
                        UnEvicted: terminalState.UnEvicted,
                        PrunedOrphans: terminalState.PrunedOrphans,
                        Context: new Dictionary<string, object?>
                        {
                            ["totalProcessed"] = terminalState.Processed,
                            ["totalEvicted"] = terminalState.Evicted,
                            ["totalUnEvicted"] = terminalState.UnEvicted,
                            ["prunedOrphans"] = terminalState.PrunedOrphans
                        }));
                }

                return _notifications.NotifyAllAsync(SignalREvents.EvictionScanComplete, new EvictionScanComplete(
                    Success: false,
                    OperationId: operationId,
                    StageKey: "signalr.evictionScan.complete",
                    Processed: 0,
                    Evicted: 0,
                    UnEvicted: 0,
                    Error: info.Error ?? "Rust eviction scan binary returned failure"));
            });

        _evictionScanTerminalStates[operationId] = terminalState;
        return operationId;
    }

    private async Task NotifyScanProgressAsync(
        Guid operationId,
        double percentComplete,
        string stageKey,
        EvictionScanResult scanResult,
        int? filesChecked = null,
        int? filesTotal = null)
    {
        var context = new Dictionary<string, object?>
        {
            ["totalProcessed"] = scanResult.Processed,
            ["totalEvicted"] = scanResult.Evicted,
            ["totalUnEvicted"] = scanResult.UnEvicted
        };

        // Live counts for the disk-summary refresh stage (refreshingSummaryCounted).
        if (filesChecked.HasValue)
        {
            context["filesChecked"] = filesChecked.Value.ToString("N0");
        }
        if (filesTotal.HasValue)
        {
            context["filesTotal"] = filesTotal.Value.ToString("N0");
        }

        _operationTracker.UpdateProgress(operationId, percentComplete, stageKey);

        // Same broadcast gate as the rust-tick callback: the post-scan phases (detection updates,
        // disk-summary refresh) can call this per batch. Stage transitions always emit.
        if (!ShouldEmitScanProgress(stageKey))
        {
            return;
        }

        await _notifications.NotifyAllAsync(SignalREvents.EvictionScanProgress, new EvictionScanProgress(
            OperationId: operationId,
            Status: OperationStatus.Running.ToWireString(),
            StageKey: stageKey,
            PercentComplete: percentComplete,
            Processed: scanResult.Processed,
            TotalEstimate: scanResult.Processed,
            Evicted: scanResult.Evicted,
            UnEvicted: scanResult.UnEvicted,
            Context: context));
    }

    /// <summary>
    /// Broadcast gate for EvictionScanProgress: pass on stage-key change or when at least
    /// <see cref="RustProcessHelper.ProgressEmitMinIntervalMs"/> has elapsed since the last emit.
    /// </summary>
    private bool ShouldEmitScanProgress(string stageKey)
    {
        var nowTicks = Environment.TickCount64;
        if (stageKey == _scanProgressLastEmitStageKey &&
            nowTicks - _scanProgressLastEmitTicks < RustProcessHelper.ProgressEmitMinIntervalMs)
        {
            return false;
        }

        _scanProgressLastEmitStageKey = stageKey;
        _scanProgressLastEmitTicks = nowTicks;
        return true;
    }

    private static EvictionScanResult ParseScanResult(RustExecutionResult result)
    {
        if (result.Data != null)
        {
            try
            {
                var json = result.Data.ToString();
                if (!string.IsNullOrEmpty(json))
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var parsed = JsonSerializer.Deserialize<EvictionScanResult>(json, options);
                    if (parsed != null) return parsed;
                }
            }
            catch
            {
                // Fall through to error result
            }
        }

        return new EvictionScanResult
        {
            Success = result.Success,
            Error = result.Error
        };
    }

    /// <summary>
    /// Mutable terminal-metrics holder for an in-flight EvictionScan. Populated BY VALUE in
    /// ReconcileCacheFilesAsync immediately before CompleteOperation; read by the onTerminalEmit
    /// closure registered at RegisterOperation time. <see cref="Silent"/> mirrors the scan's silent
    /// flag so the closure suppresses the terminal emit exactly as the old inline code did.
    /// </summary>
    private sealed class EvictionScanTerminalState
    {
        public bool Silent;
        public int Processed;
        public int Evicted;
        public int UnEvicted;
        public int PrunedOrphans;
    }

    /// <summary>
    /// Mutable terminal-metrics holder for an in-flight EvictionRemoval. Populated BY VALUE in
    /// CompleteEvictionRemovalAsync immediately before CompleteOperation; read by the onTerminalEmit
    /// closure registered at RegisterOperation time. <see cref="Silent"/> mirrors the per-op silent
    /// snapshot so the closure suppresses the terminal emit for Remove-mode auto-cleanup.
    /// </summary>
    private sealed class EvictionRemovalTerminalState
    {
        public bool Silent;
        public string StageKey = "signalr.evictionRemove.complete";
        public int DownloadsRemoved;
        public int LogEntriesRemoved;
    }

    private sealed record EvictedLogPurgeTargets(
        IReadOnlyList<string> Urls,
        IReadOnlyList<long> DepotIds,
        int MatchingDownloadCount);

    private sealed record EvictedLogPurgeRunOptions(
        string InputFilePrefix,
        string OutputFilePrefix,
        double ProgressStartPercent,
        double ProgressSpanPercent,
        string RunDescription,
        string SuccessDescription,
        string SummaryDescription);

    private sealed record EvictedLogPurgeSummary(
        long TotalLinesRemoved,
        int DatasourcesProcessed,
        int DatasourcesFailed);

    /// <summary>
    /// Starts bulk eviction removal for all evicted records.
    /// </summary>
    /// <param name="cancellationToken">
    /// Unused for cancellation control — eviction removals are cancelled via the tracker
    /// (universal cancel/force-kill drives the registered CTS). Kept so the controller can pass
    /// <c>HttpContext.RequestAborted</c> without a signature change.
    /// </param>
    public async Task<Guid> StartBulkEvictionRemovalAsync(CancellationToken cancellationToken)
    {
        var cts = new CancellationTokenSource();
        var terminalState = new EvictionRemovalTerminalState();
        Guid operationId = default;
        operationId = _operationTracker.RegisterOperation(
            OperationType.EvictionRemoval,
            "Eviction Removal",
            cts,
            new EvictionRemovalMetadata(),
            // critic-2: terminal cleanup is the sole remover of the silent-id entry so a universal
            // force-kill (which bypasses CompleteEvictionRemovalAsync) cannot leak it.
            onTerminalCleanup: () =>
            {
                _silentRemovalOperationIds.TryRemove(operationId, out _);
                _evictionRemovalTerminalStates.TryRemove(operationId, out _);
            },
            // Terminal EvictionRemovalComplete fires EXACTLY ONCE from inside CompleteOperation.
            onTerminalEmit: CreateRemovalTerminalEmit(() => operationId, terminalState));
        _evictionRemovalTerminalStates[operationId] = terminalState;

        await _notifications.NotifyAllAsync(
            SignalREvents.EvictionRemovalStarted,
            new EvictionRemovalStarted("signalr.evictionRemove.starting.bulk", operationId));

        _ = Task.Run(async () =>
        {
            // core-3: do NOT dispose cts here — the tracker owns its lifetime and disposes it in
            // CompleteOperation. Disposing it from the worker races the tracker's cancel path.
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await RemoveEvictedRecordsAsync(context, cts.Token, operationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EvictionRemoval] Unhandled error before removal started");
                await CompleteRemovalAsync(
                    operationId,
                    success: false,
                    stageKey: "signalr.evictionRemove.failedToStart",
                    error: ex.Message);
            }
        }, cts.Token);

        return operationId;
    }

    /// <summary>
    /// Starts scoped eviction removal for a single game or service.
    /// </summary>
    /// <param name="cancellationToken">
    /// Unused for cancellation control — eviction removals are cancelled via the tracker
    /// (universal cancel/force-kill drives the registered CTS). Kept so the controller can pass
    /// <c>HttpContext.RequestAborted</c> without a signature change.
    /// </param>
    public async Task<Guid> StartScopedEvictionRemovalAsync(
        EvictionScope scope,
        string key,
        string? resolvedGameName,
        string? resolvedGameAppId,
        CancellationToken cancellationToken,
        string? resolvedEpicAppId = null,
        string? namedGameName = null)
    {
        var cts = new CancellationTokenSource();
        var metadata = new EvictionRemovalMetadata
        {
            Scope = scope.ToString().ToLowerInvariant(),
            Key = key,
            GameName = resolvedGameName
        };
        var terminalState = new EvictionRemovalTerminalState();
        Guid operationId = default;
        operationId = _operationTracker.RegisterOperation(
            OperationType.EvictionRemoval,
            $"Eviction Removal ({scope}: {key})",
            cts,
            metadata,
            // critic-2: terminal cleanup is the sole remover of the silent-id entry so a universal
            // force-kill (which bypasses CompleteEvictionRemovalAsync) cannot leak it.
            onTerminalCleanup: () =>
            {
                _silentRemovalOperationIds.TryRemove(operationId, out _);
                _evictionRemovalTerminalStates.TryRemove(operationId, out _);
            },
            // Terminal EvictionRemovalComplete fires EXACTLY ONCE from inside CompleteOperation.
            onTerminalEmit: CreateRemovalTerminalEmit(() => operationId, terminalState));
        _evictionRemovalTerminalStates[operationId] = terminalState;

        await _notifications.NotifyAllAsync(
            SignalREvents.EvictionRemovalStarted,
            new EvictionRemovalStarted(
                "signalr.evictionRemove.starting.entity",
                operationId,
                new Dictionary<string, object?> { ["scope"] = scope.ToString(), ["key"] = key },
                resolvedGameName,
                resolvedGameAppId,
                resolvedEpicAppId));

        _ = Task.Run(async () =>
        {
            // core-3: do NOT dispose cts here — the tracker owns its lifetime and disposes it in
            // CompleteOperation. Disposing it from the worker races the tracker's cancel path.
            try
            {
                using var scopeLifetime = _serviceProvider.CreateScope();
                var context = scopeLifetime.ServiceProvider.GetRequiredService<AppDbContext>();
                await RemoveEvictedRecordsForEntityAsync(context, scope, key, cts.Token, operationId, namedGameName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EvictedRemoval] Unhandled error before entity removal started ({Scope} '{Key}')", scope, key);
                await CompleteRemovalAsync(
                    operationId,
                    success: false,
                    stageKey: "signalr.evictionRemove.failedToStart",
                    error: ex.Message);
            }
        }, cts.Token);

        return operationId;
    }

    private async Task ReportRemovalProgressAsync(
        Guid operationId,
        double percentComplete,
        string status,
        string stageKey,
        int downloadsRemoved = 0,
        int logEntriesRemoved = 0,
        Dictionary<string, object?>? context = null)
    {
        _operationTracker.UpdateProgress(operationId, percentComplete, stageKey);

        // Silent ops (Remove-mode auto-cleanup) skip the SignalR notification.
        if (_silentRemovalOperationIds.ContainsKey(operationId))
        {
            return;
        }

        await _notifications.NotifyAllAsync(
            SignalREvents.EvictionRemovalProgress,
            new EvictionRemovalProgress(
                operationId,
                status,
                stageKey,
                percentComplete,
                downloadsRemoved,
                logEntriesRemoved,
                context));
    }

    private Task CompleteRemovalAsync(
        Guid operationId,
        bool success,
        string stageKey,
        int downloadsRemoved = 0,
        int logEntriesRemoved = 0,
        string? error = null,
        bool cancelled = false)
    {
        if (success)
        {
            _operationTracker.UpdateProgress(operationId, 100, stageKey);
        }

        // critic-2: snapshot the silent flag BEFORE CompleteOperation. The registered
        // onTerminalCleanup is now the sole remover of the silent-id entry, and it fires inside
        // CompleteOperation — so reading the set afterward would always miss.
        var wasSilent = _silentRemovalOperationIds.ContainsKey(operationId);

        // Capture the terminal metrics BY VALUE just before CompleteOperation so the registered
        // onTerminalEmit closure (the sole terminal emitter) builds the EvictionRemovalComplete
        // record. wasSilent is snapshotted here for the same race-avoidance reason as above.
        // The cancelled flag is intentionally NOT read here: the closure derives it from the
        // tracker's authoritative OperationTerminalInfo.Cancelled at emit time.
        if (_evictionRemovalTerminalStates.TryGetValue(operationId, out var removalTerminalState))
        {
            removalTerminalState.Silent = wasSilent;
            removalTerminalState.StageKey = stageKey;
            removalTerminalState.DownloadsRemoved = downloadsRemoved;
            removalTerminalState.LogEntriesRemoved = logEntriesRemoved;
        }

        // Terminal EvictionRemovalComplete (success/cancel/error, with silent suppression) is
        // emitted by the registered onTerminalEmit closure inside CompleteOperation.
        _operationTracker.CompleteOperation(operationId, success, success ? null : error);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Registers (or returns the closure for) the EvictionRemoval terminal emit. A mutable
    /// terminal-state holder is created up front, stored in <see cref="_evictionRemovalTerminalStates"/>,
    /// and captured by the returned closure. CompleteEvictionRemovalAsync fills the holder just
    /// before CompleteOperation; force-kill (which bypasses CompleteEvictionRemovalAsync) leaves the
    /// holder at its defaults so the closure still emits a coherent record. The terminal SignalR
    /// event fires EXACTLY ONCE from inside CompleteOperation.
    /// </summary>
    private Func<OperationTerminalInfo, Task> CreateRemovalTerminalEmit(
        Func<Guid> operationIdAccessor,
        EvictionRemovalTerminalState terminalState)
    {
        return info =>
        {
            // Silent ops (Remove-mode auto-cleanup) never surfaced a terminal notification.
            if (terminalState.Silent)
            {
                return Task.CompletedTask;
            }

            var operationId = operationIdAccessor();

            if (info.Cancelled)
            {
                return _notifications.NotifyAllAsync(
                    SignalREvents.EvictionRemovalComplete,
                    new EvictionRemovalComplete(
                        Success: false,
                        OperationId: operationId,
                        StageKey: terminalState.StageKey,
                        DownloadsRemoved: terminalState.DownloadsRemoved,
                        LogEntriesRemoved: terminalState.LogEntriesRemoved,
                        Error: info.Error ?? "Cancelled by user",
                        Cancelled: true));
            }

            return _notifications.NotifyAllAsync(
                SignalREvents.EvictionRemovalComplete,
                new EvictionRemovalComplete(
                    Success: info.Success,
                    OperationId: operationId,
                    StageKey: terminalState.StageKey,
                    DownloadsRemoved: terminalState.DownloadsRemoved,
                    LogEntriesRemoved: terminalState.LogEntriesRemoved,
                    Error: info.Success ? null : info.Error,
                    Cancelled: false));
        };
    }

    private async Task<EvictedLogPurgeSummary> RunEvictedLogPurgeAsync(
        Guid operationId,
        EvictedLogPurgeTargets targets,
        CancellationToken stoppingToken,
        EvictedLogPurgeRunOptions options)
    {
        await ReportRemovalProgressAsync(
            operationId,
            options.ProgressStartPercent,
            "purging_log_entries",
            "signalr.evictionRemove.purgingLogs",
            context: new Dictionary<string, object?> { ["count"] = targets.Urls.Count + targets.DepotIds.Count });

        var rustBinaryPath = _pathResolver.GetRustLogPurgePath();
        if (!File.Exists(rustBinaryPath))
        {
            _logger.LogWarning(
                "[EvictedLogPurge] cache_purge_log_entries binary not found at {Path} - skipping log rewrite. DB deletes will still proceed.",
                rustBinaryPath);
            return new EvictedLogPurgeSummary(0, 0, 0);
        }

        var operationsDir = _pathResolver.GetOperationsDirectory();
        Directory.CreateDirectory(operationsDir);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var inputJsonPath = Path.Combine(operationsDir, $"{options.InputFilePrefix}_{timestamp}.json");

        try
        {
            var jsonPayload = JsonSerializer.Serialize(
                new { urls = targets.Urls, depot_ids = targets.DepotIds },
                new JsonSerializerOptions { WriteIndented = false });
            await File.WriteAllTextAsync(inputJsonPath, jsonPayload, stoppingToken);

            long totalLinesRemoved = 0;
            int datasourcesProcessed = 0;
            int datasourcesFailed = 0;

            var allDatasources = _datasourceService.GetDatasources().ToList();
            var totalDatasources = Math.Max(1, allDatasources.Count);
            var dsIndex = 0;

            foreach (var datasource in allDatasources)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                var dsLogPath = datasource.LogPath;
                if (string.IsNullOrWhiteSpace(dsLogPath) || !Directory.Exists(dsLogPath))
                {
                    _logger.LogDebug(
                        "[EvictedLogPurge] Skipping datasource '{Datasource}': log dir '{LogPath}' does not exist",
                        datasource.Name,
                        dsLogPath);
                    dsIndex++;
                    continue;
                }

                var outputJsonPath = Path.Combine(operationsDir, $"{options.OutputFilePrefix}_{datasource.Name}_{timestamp}.json");
                var progressJsonPath = Path.Combine(operationsDir, $"{options.OutputFilePrefix}_progress_{datasource.Name}_{timestamp}.json");
                var args = $"\"{dsLogPath}\" \"{inputJsonPath}\" \"{outputJsonPath}\" --progress-json \"{progressJsonPath}\" --progress";
                var startInfo = _rustProcessHelper.CreateProcessStartInfo(rustBinaryPath, args);

                _logger.LogInformation(
                    "[EvictedLogPurge] Running {RunDescription} for datasource '{Datasource}': {Binary} {Args}",
                    options.RunDescription,
                    datasource.Name,
                    rustBinaryPath,
                    args);

                try
                {
                    // Same per-datasource slice mapping as before. Hybrid transport (mirrors
                    // CacheClearingService): the stdout progress event is a zero-latency wake-up;
                    // cache_purge_log_entries.rs's progress-file DTO is unchanged, so the callback
                    // still re-reads it for the real data on every tick.
                    var dsSliceStart = options.ProgressStartPercent +
                        (options.ProgressSpanPercent * dsIndex / totalDatasources);
                    var dsSliceSize = options.ProgressSpanPercent / totalDatasources;

                    var purgeResult = await _rustProcessHelper.ExecuteTrackedProcessWithProgressEventsAsync(
                        startInfo,
                        operationId,
                        stoppingToken,
                        async _ =>
                        {
                            var progress = await _rustProcessHelper.ReadProgressFileAsync<PurgeLogProgressData>(progressJsonPath);
                            if (progress == null)
                            {
                                return;
                            }

                            // Failure is surfaced via the non-zero exit code below.
                            if (string.Equals(progress.Status, "failed", StringComparison.OrdinalIgnoreCase))
                            {
                                return;
                            }

                            var mappedPercent = dsSliceStart +
                                (progress.PercentComplete / 100.0) * dsSliceSize;

                            await ReportRemovalProgressAsync(
                                operationId,
                                mappedPercent,
                                "purging_log_entries",
                                "signalr.evictionRemove.purgingLogs",
                                context: new Dictionary<string, object?>
                                {
                                    ["count"] = targets.Urls.Count + targets.DepotIds.Count,
                                    ["datasource"] = datasource.Name
                                });
                        },
                        "cache_purge_log_entries");

                    await _rustProcessHelper.DeleteTempFileAsync(progressJsonPath);

                    if (purgeResult.ExitCode != 0)
                    {
                        datasourcesFailed++;
                        _logger.LogWarning(
                            "[EvictedLogPurge] cache_purge_log_entries exited {Code} for datasource '{Datasource}'. stderr: {Err}",
                            purgeResult.ExitCode,
                            datasource.Name,
                            purgeResult.Error);
                        dsIndex++;
                        continue;
                    }

                    try
                    {
                        var report = await _rustProcessHelper.ReadAndCleanupOutputJsonAsync<PurgeLogEntriesReport>(
                            outputJsonPath,
                            $"cache_purge_log_entries/{datasource.Name}");
                        totalLinesRemoved += report.LinesRemoved;
                        datasourcesProcessed++;
                        _logger.LogInformation(
                            "[EvictedRemoval] {SuccessDescription} removed {Lines} lines from access.log* in datasource '{Datasource}' ({Perms} permission errors)",
                            options.SuccessDescription,
                            report.LinesRemoved,
                            datasource.Name,
                            report.PermissionErrors);
                    }
                    catch (Exception reportEx)
                    {
                        datasourcesProcessed++;
                        _logger.LogWarning(
                            reportEx,
                            "[EvictedRemoval] {SuccessDescription} succeeded (exit 0) for datasource '{Datasource}' but output JSON was unreadable",
                            options.SuccessDescription,
                            datasource.Name);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception innerEx)
                {
                    datasourcesFailed++;
                    _logger.LogWarning(
                        innerEx,
                        "[EvictedLogPurge] Failed to run cache_purge_log_entries for datasource '{Datasource}' - DB deletes will still proceed",
                        datasource.Name);
                }

                dsIndex++;
            }

            _logger.LogInformation(
                "[EvictedRemoval] {SummaryDescription}: {Total} lines removed across {Ok} datasources ({Failed} failed)",
                options.SummaryDescription,
                totalLinesRemoved,
                datasourcesProcessed,
                datasourcesFailed);

            if (totalLinesRemoved > 0)
            {
                // The purge rewrote the log files, so the per-service count cache is stale.
                // InvalidateServiceCountsAsync also broadcasts ServiceCountsChanged so the
                // Log Removal panel refetches live (covers both bulk and per-entity purges).
                var cacheManagementService = _serviceProvider.GetRequiredService<CacheManagementService>();
                await cacheManagementService.InvalidateServiceCountsAsync();
            }

            return new EvictedLogPurgeSummary(totalLinesRemoved, datasourcesProcessed, datasourcesFailed);
        }
        finally
        {
            try { File.Delete(inputJsonPath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// True when the given EvictionRemoval operation runs silently (automatic Remove-mode
    /// auto-cleanup). Silent removals emit no SignalR events, so recovery endpoints must not
    /// report them either - otherwise the frontend would resurrect a notification card for an
    /// operation that is deliberately invisible.
    /// </summary>
    public bool IsSilentRemovalOperation(Guid operationId)
    {
        return _silentRemovalOperationIds.ContainsKey(operationId);
    }

    /// <summary>
    /// Deletes all evicted Download records, their associated LogEntries, and their matching
    /// CachedGameDetections / CachedServiceDetections rows from the database. Called either
    /// from the scan flow (no operationId, mode == Remove) or from the controller's
    /// "Remove All Evicted" button (pre-registered operationId).
    /// When <paramref name="silent"/> is true, no EvictionRemoval SignalR notifications are
    /// emitted - the operationId is added to _silentRemovalOperationIds so downstream
    /// Progress/Complete helpers also skip their sends. Silent is only used by the
    /// scan-driven Remove-mode auto-cleanup; the controller-driven bulk button always notifies.
    /// </summary>
    public async Task RemoveEvictedRecordsAsync(AppDbContext context, CancellationToken stoppingToken, Guid? operationId = null, bool silent = false)
    {
        CancellationTokenSource? cts = null;

        // Deliberately not using TrackedRemovalOperationRunner: this service can start from the background scan path without a controller HTTP lifecycle.
        if (operationId == null)
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            // Run the removal work on the LINKED token: the universal cancel path
            // (/api/operations/{id}/cancel) drives the CTS registered with the tracker, so the
            // log purge and EF deletes below must observe cts.Token - not the caller's
            // stoppingToken - for a user cancel to actually stop the work. This mirrors the
            // pre-registered path, where the caller passes its tracker CTS token as
            // stoppingToken. Host/scan shutdown still propagates through the link.
            stoppingToken = cts.Token;
            var terminalState = new EvictionRemovalTerminalState();
            Guid selfRegisteredId = default;
            selfRegisteredId = _operationTracker.RegisterOperation(
                OperationType.EvictionRemoval,
                "Eviction Removal",
                cts,
                new EvictionRemovalMetadata(), // bulk removal - no specific scope/key
                // critic-2: this scan-driven path can run silently; the terminal cleanup is the sole
                // remover of the silent-id entry so a universal force-kill cannot leak it.
                onTerminalCleanup: () =>
                {
                    _silentRemovalOperationIds.TryRemove(selfRegisteredId, out _);
                    _evictionRemovalTerminalStates.TryRemove(selfRegisteredId, out _);
                },
                // Terminal EvictionRemovalComplete fires EXACTLY ONCE from inside CompleteOperation.
                // Silent Remove-mode auto-cleanup is suppressed by the holder's Silent flag.
                onTerminalEmit: CreateRemovalTerminalEmit(() => selfRegisteredId, terminalState));
            _evictionRemovalTerminalStates[selfRegisteredId] = terminalState;
            operationId = selfRegisteredId;

            // Silent mode (Remove-mode auto-cleanup): skip the EvictionRemovalStarted SignalR
            // event so the frontend never creates a removal notification. The operationId is
            // recorded in _silentRemovalOperationIds so all downstream Progress/Complete events
            // also skip their SignalR sends.
            if (silent)
            {
                _silentRemovalOperationIds.TryAdd(operationId.Value, 0);
            }
            else
            {
                await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalStarted,
                    new EvictionRemovalStarted("signalr.evictionRemove.starting.bulk", operationId.Value));
            }
        }
        else if (silent)
        {
            // Caller already registered the operation and emitted Started; honor silent for the
            // remaining Progress/Complete events.
            _silentRemovalOperationIds.TryAdd(operationId.Value, 0);
        }

        // At this point operationId is guaranteed non-null; capture as non-nullable for the rest of the method.
        var opId = operationId.Value;

        try
        {
            // Step -1 (Fix 2): Rewrite nginx access.log files to drop entries for every evicted game
            // BEFORE deleting LogEntries/Downloads from the database. If we skipped this, a future
            // `ResetLogPosition` + full log re-parse would resurrect the evicted games because their
            // URLs still exist in the on-disk access.log. The rewrite is a best-effort optimization:
            // if the Rust binary fails we log a WARNING and continue - correctness of the DB delete
            // is preserved either way.
            await PurgeLogEntriesAsync(context, opId, stoppingToken);

            // Removal-driven cleanup: the bulk path (Remove mode auto-scan and the controller-
            // driven "Remove All Evicted" button) is an explicit user request to delete evicted
            // entities. Like the per-item path (RemoveEvictedRecordsForEntityAsync), we DELETE
            // the matching CachedGameDetections / CachedServiceDetections rows so the Evicted
            // Items list clears on the frontend's next refetch - no ghost rows with 0 files /
            // 0 B left behind. Order: detection rows → log entries → downloads, all in one
            // transaction.
            int detectionGamesDeleted = 0;
            int detectionServicesDeleted = 0;
            int logEntriesDeleted = 0;
            int downloadsDeleted = 0;

            var strategy = context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                detectionGamesDeleted = 0;
                detectionServicesDeleted = 0;
                logEntriesDeleted = 0;
                downloadsDeleted = 0;

                await using var transaction = await context.Database.BeginTransactionAsync(stoppingToken);
                try
                {
                    // Step 1: delete evicted detection rows so the frontend list clears.
                    await ReportRemovalProgressAsync(
                        opId,
                        40,
                        "removing_detection_rows",
                        "signalr.evictionRemove.removingDetectionRows");

                    detectionGamesDeleted = await context.CachedGameDetections
                        .Where(g => g.IsEvicted)
                        .ExecuteDeleteAsync(stoppingToken);

                    detectionServicesDeleted = await context.CachedServiceDetections
                        .Where(s => s.IsEvicted)
                        .ExecuteDeleteAsync(stoppingToken);

                    // Step 2: delete LogEntries for evicted downloads (FK constraint).
                    await ReportRemovalProgressAsync(
                        opId,
                        60,
                        "removing_log_entries",
                        "signalr.evictionRemove.removingLogs");

                    logEntriesDeleted = await context.LogEntries
                        .Where(le => le.DownloadId != null && le.Download != null && le.Download.IsEvicted)
                        .ExecuteDeleteAsync(stoppingToken);

                    // Step 3: delete evicted Downloads.
                    await ReportRemovalProgressAsync(
                        opId,
                        80,
                        "removing_downloads",
                        "signalr.evictionRemove.removingDownloads",
                        logEntriesRemoved: logEntriesDeleted);

                    downloadsDeleted = await context.Downloads
                        .Where(d => d.IsEvicted)
                        .ExecuteDeleteAsync(stoppingToken);

                    await transaction.CommitAsync(stoppingToken);
                }
                catch
                {
                    await transaction.RollbackAsync(stoppingToken);
                    throw;
                }
            });

            if (downloadsDeleted > 0 || logEntriesDeleted > 0 || detectionGamesDeleted > 0 || detectionServicesDeleted > 0)
            {
                _logger.LogInformation(
                    "[EvictionScan] Remove mode: deleted {Games} game detection rows + {Services} service detection rows, {Downloads} downloads, {LogEntries} log entries",
                    detectionGamesDeleted, detectionServicesDeleted, downloadsDeleted, logEntriesDeleted);
            }

            // The disk-summary refresh can take a while on large caches; surface it as its own
            // progress stage so the notification doesn't sit frozen after the last delete step.
            await ReportRemovalProgressAsync(
                opId,
                90,
                "refreshing_detection",
                "signalr.evictionRemove.refreshingDetection",
                downloadsRemoved: downloadsDeleted,
                logEntriesRemoved: logEntriesDeleted);

            // Refresh persisted disk-summary totals so dashboard reads reflect post-removal
            // state, streaming the parallel path-stat counts into 90-99% (callback fires from
            // worker threads, already throttled inside the calculator - fire-and-forget).
            await _gameCacheDetectionService.RefreshDiskSummaryAndInvalidateAsync(
                stoppingToken,
                (statted, totalPaths) => _ = ReportRemovalProgressAsync(
                    opId,
                    totalPaths > 0 ? 90.0 + (statted / (double)totalPaths) * 9.0 : 90.0,
                    "refreshing_detection",
                    "signalr.evictionRemove.refreshingDetectionCounted",
                    downloadsRemoved: downloadsDeleted,
                    logEntriesRemoved: logEntriesDeleted,
                    context: new Dictionary<string, object?>
                    {
                        ["filesChecked"] = statted.ToString("N0"),
                        ["filesTotal"] = totalPaths.ToString("N0")
                    }));
            _logger.LogDebug("[EvictedRemoval] Detection cache refreshed after bulk removal");

            await CompleteRemovalAsync(
                opId,
                success: true,
                stageKey: "signalr.evictionRemove.complete",
                downloadsRemoved: downloadsDeleted,
                logEntriesRemoved: logEntriesDeleted);
        }
        catch (OperationCanceledException)
        {
            // User-initiated cancel is an expected outcome, not an error.
            _logger.LogInformation("[EvictionScan] Bulk eviction removal cancelled by user (operation {OpId})", opId);
            await CompleteRemovalAsync(
                opId,
                success: false,
                stageKey: "signalr.evictionRemove.cancelled",
                error: "Cancelled by user",
                cancelled: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EvictionScan] Error removing evicted records from database");
            await CompleteRemovalAsync(
                opId,
                success: false,
                stageKey: "signalr.evictionRemove.failed",
                error: ex.Message);
        }
        finally
        {
            cts?.Dispose();
        }
    }


    /// <summary>
    /// Partial-eviction safety (shared by the bulk <see cref="PurgeLogEntriesAsync"/> and the
    /// per-entity <see cref="PurgeLogEntriesForEntityAsync"/> paths). The Rust log purger matches
    /// access.log lines on URL OR depot_id; a depot present in BOTH an evicted and a still-cached
    /// Download would otherwise strip the still-cached copy's lines. Narrows <paramref name="evictedDepotIds"/>
    /// down to those that appear ONLY in evicted Downloads (i.e. not in <paramref name="cachedDepotIds"/>)
    /// and reports how many were skipped. Pure: each caller supplies its own scoped
    /// evicted/cached lists and owns the logging of the returned skip count.
    /// </summary>
    private static (List<long> Safe, int Skipped) NarrowDepotsToExclusivelyEvicted(
        List<long> evictedDepotIds,
        List<long> cachedDepotIds)
    {
        if (evictedDepotIds.Count == 0)
        {
            return (evictedDepotIds, 0);
        }

        var cachedSet = new HashSet<long>(cachedDepotIds);
        var safe = evictedDepotIds.Where(d => !cachedSet.Contains(d)).ToList();
        return (safe, evictedDepotIds.Count - safe.Count);
    }

    /// <summary>
    /// Fix 2: Rewrites nginx access.log files to drop all entries belonging to evicted games,
    /// using the `cache_purge_log_entries` Rust binary. Runs once per configured datasource.
    ///
    /// Called by <see cref="RemoveEvictedRecordsAsync"/> BEFORE the DB LogEntries/Downloads deletes
    /// so a future `ResetLogPosition` + full log re-parse cannot resurrect the evicted games.
    ///
    /// Best-effort: if the Rust binary fails we log a warning but still allow the DB deletes to
    /// proceed - the only loss is that a later full re-parse could re-create the rows.
    /// </summary>
    private async Task PurgeLogEntriesAsync(AppDbContext context, Guid operationId, CancellationToken stoppingToken)
    {
        try
        {
            // Collect the ids of all currently-evicted Download rows. We need ids to join LogEntries;
            // URLs come from LogEntries, depot_ids come from Downloads.DepotId.
            var evictedDownloadIds = await context.Downloads
                .Where(d => d.IsEvicted)
                .Select(d => d.Id)
                .ToListAsync(stoppingToken);

            if (evictedDownloadIds.Count == 0)
            {
                _logger.LogDebug("[EvictedLogPurge] No evicted downloads - skipping log rewrite");
                return;
            }

            // Collect distinct URLs from LogEntries that belong to evicted downloads.
            var urls = await context.LogEntries
                .Where(le => le.DownloadId != null && evictedDownloadIds.Contains(le.DownloadId.Value))
                .Select(le => le.Url)
                .Where(u => u != null && u != string.Empty)
                .Distinct()
                .ToListAsync(stoppingToken);

            // Collect candidate depot IDs from the evicted Downloads only.
            var evictedDepotIds = await context.Downloads
                .Where(d => d.IsEvicted && d.DepotId != null)
                .Select(d => d.DepotId!.Value)
                .Distinct()
                .ToListAsync(stoppingToken);

            // IMPORTANT (partial-eviction safety): the Rust log purger matches lines on URL OR
            // depot_id. If a depot appears in BOTH an evicted and a still-cached Download (e.g. a
            // game/service was downloaded twice and only the older copy was evicted), sending that
            // depot_id would also strip the still-cached copy's access.log lines - data loss. This
            // is the exact guard the per-entity sibling (PurgeLogEntriesForEntityAsync) already has;
            // the bulk path was missing it. Filter depot_ids down to those that appear ONLY in
            // evicted Downloads (across ALL entities, since this path purges every evicted row).
            // Shared narrowing lives in NarrowDepotsToExclusivelyEvicted; we only fetch cached depot
            // ids when there is something to narrow.
            var cachedDepotIds = evictedDepotIds.Count == 0
                ? new List<long>()
                : await context.Downloads
                    .Where(d => !d.IsEvicted && d.DepotId != null)
                    .Select(d => d.DepotId!.Value)
                    .Distinct()
                    .ToListAsync(stoppingToken);

            var (safeDepotIds, skippedCount) = NarrowDepotsToExclusivelyEvicted(evictedDepotIds, cachedDepotIds);
            if (skippedCount > 0)
            {
                _logger.LogInformation(
                    "[EvictedLogPurge] Excluded {Skipped} depot ID(s) from bulk log purge because they also appear in still-cached downloads (partial-eviction safety)",
                    skippedCount);
            }

            var depotIds = safeDepotIds;

            if (urls.Count == 0 && depotIds.Count == 0)
            {
                _logger.LogInformation(
                    "[EvictedLogPurge] {Count} evicted downloads have no URL/depot history - nothing to purge from logs",
                    evictedDownloadIds.Count);
                return;
            }
            await RunEvictedLogPurgeAsync(
                operationId,
                new EvictedLogPurgeTargets(urls, depotIds, evictedDownloadIds.Count),
                stoppingToken,
                new EvictedLogPurgeRunOptions(
                    "evicted_log_purge_input",
                    "evicted_log_purge_output",
                    0,
                    30,
                    "bulk log purge",
                    "Log purge",
                    "Log purge summary"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort: log and continue so DB deletes still run.
            _logger.LogWarning(ex,
                "[EvictedLogPurge] Unexpected error during bulk log purge - DB deletes will still proceed");
        }
    }

    /// <summary>
    /// Fix 3 reverse-reconcile helper: finds <see cref="CachedGameDetection"/> rows marked
    /// <c>IsEvicted = true</c> whose underlying <see cref="Download"/> rows have since been
    /// flipped back to <c>IsEvicted = false</c> (by `cache_eviction_scan.rs` when cache files
    /// reappear on disk) and clears the flag.
    ///
    /// Called from three trigger points:
    ///   1. <see cref="ReconcileCacheFilesAsync"/> after a successful eviction scan that reported UnEvicted &gt; 0.
    ///   2. <see cref="GameCacheDetectionService.GetCachedDetectionAsync"/> before the three-layer filter.
    ///   3. <see cref="GameCacheDetectionService.SaveGamesToDatabaseAsync"/> upsert path (inline on the tracked entity).
    ///
    /// Static so <see cref="GameCacheDetectionService"/> can call it without a DI dependency on this service.
    /// </summary>
    public static async Task<int> UnevictCachedGameDetectionsAsync(
        AppDbContext context,
        ILogger logger,
        GameCacheDetectionDataService detectionDataService,
        EvictedDetectionPreservationService evictedDetectionPreservationService,
        CancellationToken ct)
    {
        var gamesToUnevict = await detectionDataService.GetGamesToUnevictAsync(context, ct);
        if (gamesToUnevict.SteamGameAppIds.Count == 0
            && gamesToUnevict.EpicAppIds.Count == 0
            && gamesToUnevict.NamedGameKeys.Count == 0)
        {
            return 0;
        }

        var unpreserveResult = await evictedDetectionPreservationService.UnpreserveAsync(
            context,
            gamesToUnevict.SteamGameAppIds,
            gamesToUnevict.EpicAppIds,
            gamesToUnevict.NamedGameKeys,
            ct);

        if (unpreserveResult.SteamGamesUpdated > 0)
        {
            logger.LogInformation(
                "[GameDetection] Self-healed {Count} Steam games - Downloads no longer all evicted",
                unpreserveResult.SteamGamesUpdated);
        }

        if (unpreserveResult.EpicGamesUpdated > 0)
        {
            logger.LogInformation(
                "[GameDetection] Self-healed {Count} Epic games - Downloads no longer all evicted",
                unpreserveResult.EpicGamesUpdated);
        }

        if (unpreserveResult.NamedGamesUpdated > 0)
        {
            logger.LogInformation(
                "[GameDetection] Self-healed {Count} named (Blizzard/Riot) games - Downloads no longer all evicted",
                unpreserveResult.NamedGamesUpdated);
        }

        return unpreserveResult.TotalUpdated;
    }

    public static async Task<int> EvictCachedGameDetectionsAsync(
        AppDbContext context,
        ILogger logger,
        CancellationToken ct)
    {
        int totalEvicted = 0;

        // Steam: rows matched by GameAppId (EpicAppId is null). Exclude named (Blizzard/Riot) rows
        // (GameAppId==0 && Service set) - those are handled by the named arm below.
        var unevictedSteamGameIds = await context.CachedGameDetections
            .Where(g => !g.IsEvicted && g.EpicAppId == null
                && !(g.GameAppId == 0 && g.Service != null && g.GameName != ""))
            .Select(g => g.GameAppId)
            .ToListAsync(ct);

        if (unevictedSteamGameIds.Count > 0)
        {
            var steamGamesToEvict = await context.Downloads
                .Where(d => d.GameAppId != null
                         && unevictedSteamGameIds.Contains(d.GameAppId.Value))
                .GroupBy(d => d.GameAppId!.Value)
                .Where(g => g.All(d => d.IsEvicted))
                .Select(g => g.Key)
                .ToListAsync(ct);

            if (steamGamesToEvict.Count > 0)
            {
                var steamUpdated = await context.CachedGameDetections
                    .Where(g => g.EpicAppId == null && steamGamesToEvict.Contains(g.GameAppId))
                    .ExecuteUpdateAsync(s => s.SetProperty(g => g.IsEvicted, true), ct);

                totalEvicted += steamUpdated;
                logger.LogInformation(
                    "[GameDetection] Marked {Count} Steam games as evicted - all Downloads now evicted",
                    steamUpdated);
            }
        }

        // Epic: rows matched by EpicAppId
        var unevictedEpicAppIds = await context.CachedGameDetections
            .Where(g => !g.IsEvicted && g.EpicAppId != null)
            .Select(g => g.EpicAppId!)
            .ToListAsync(ct);

        if (unevictedEpicAppIds.Count > 0)
        {
            var epicGamesToEvict = await context.Downloads
                .Where(d => d.EpicAppId != null
                         && unevictedEpicAppIds.Contains(d.EpicAppId))
                .GroupBy(d => d.EpicAppId!)
                .Where(g => g.All(d => d.IsEvicted))
                .Select(g => g.Key)
                .ToListAsync(ct);

            if (epicGamesToEvict.Count > 0)
            {
                var epicUpdated = await context.CachedGameDetections
                    .Where(g => g.EpicAppId != null && epicGamesToEvict.Contains(g.EpicAppId!))
                    .ExecuteUpdateAsync(s => s.SetProperty(g => g.IsEvicted, true), ct);

                totalEvicted += epicUpdated;
                logger.LogInformation(
                    "[GameDetection] Marked {Count} Epic games as evicted - all Downloads now evicted",
                    epicUpdated);
            }
        }

        // Named (Blizzard/Riot): rows matched by (Service, GameName); GameAppId always 0.
        var unevictedNamedRows = await context.CachedGameDetections
            .Where(g => !g.IsEvicted && g.EpicAppId == null && g.GameAppId == 0 && g.Service != null && g.GameName != "")
            .Select(g => new { Service = g.Service!.ToLower(), g.GameName })
            .ToListAsync(ct);

        if (unevictedNamedRows.Count > 0)
        {
            var unevictedNamedServices = unevictedNamedRows.Select(g => g.Service).Distinct().ToList();

            // (Service, GameName) groups where every named Download is now evicted.
            var namedGroupsAllEvicted = await context.Downloads
                .Where(d => d.GameAppId == null
                         && d.EpicAppId == null
                         && d.Service != null
                         && d.GameName != null
                         && unevictedNamedServices.Contains(d.Service!.ToLower()))
                .GroupBy(d => new { Service = d.Service!.ToLower(), GameName = d.GameName! })
                .Where(g => g.All(d => d.IsEvicted))
                .Select(g => new { g.Key.Service, g.Key.GameName })
                .ToListAsync(ct);

            var evictTargets = namedGroupsAllEvicted
                .Select(x => (x.Service, x.GameName))
                .ToHashSet();

            if (evictTargets.Count > 0)
            {
                var namedRowsToEvict = await context.CachedGameDetections
                    .Where(g => !g.IsEvicted && g.EpicAppId == null && g.GameAppId == 0 && g.Service != null && g.GameName != ""
                        && unevictedNamedServices.Contains(g.Service!.ToLower()))
                    .ToListAsync(ct);

                var namedUpdated = 0;
                foreach (var row in namedRowsToEvict)
                {
                    if (evictTargets.Contains((row.Service!.ToLower(), row.GameName)))
                    {
                        row.IsEvicted = true;
                        namedUpdated++;
                    }
                }

                if (namedUpdated > 0)
                {
                    await context.SaveChangesAsync(ct);
                    totalEvicted += namedUpdated;
                    logger.LogInformation(
                        "[GameDetection] Marked {Count} named (Blizzard/Riot) games as evicted - all Downloads now evicted",
                        namedUpdated);
                }
            }
        }

        return totalEvicted;
    }

    /// <summary>
    /// Self-heal: clears IsEvicted on CachedServiceDetection rows that have reappeared on disk
    /// (CacheFilesFound > 0). Services do not have a Downloads FK relationship so the check is
    /// simpler - if the Rust scan found cache files again, the service is no longer evicted.
    /// </summary>
    /// <summary>
    /// Downloads-keyed service self-heal: clears <see cref="CachedServiceDetection.IsEvicted"/> for any
    /// service whose service-scoped Downloads are no longer all evicted (cache files reappeared on disk).
    /// Mirrors <see cref="UnevictCachedGameDetectionsAsync"/>: keys off <c>Downloads.IsEvicted</c> (the
    /// disk-probe signal) via <see cref="GameCacheDetectionDataService.GetServicesToUnevictAsync"/> rather
    /// than the stale <see cref="CachedServiceDetection.CacheFilesFound"/> snapshot column (which the
    /// eviction scan never updates and the absence→evict path zeroes), so a re-cached service self-heals
    /// within the eviction scan instead of waiting for the next full detection scan.
    /// Services DO join Downloads via the <c>Service</c> string (GameAppId/EpicAppId both null).
    /// </summary>
    public static async Task<int> UnevictCachedServiceDetectionsAsync(
        AppDbContext context,
        ILogger logger,
        GameCacheDetectionDataService detectionDataService,
        CancellationToken ct)
    {
        var serviceNamesToUnevict = await detectionDataService.GetServicesToUnevictAsync(context, ct);
        if (serviceNamesToUnevict.Count == 0)
        {
            return 0;
        }

        var updated = await context.CachedServiceDetections
            .Where(s => s.IsEvicted && serviceNamesToUnevict.Contains(s.ServiceName.ToLower()))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEvicted, false), ct);

        if (updated > 0)
        {
            logger.LogInformation(
                "[ServiceDetection] Self-healed {Count} evicted services - Downloads no longer all evicted",
                updated);
        }

        return updated;
    }

    /// <summary>
    /// Removes evicted Downloads and their LogEntries for a single entity (Steam game, Epic game,
    /// or non-game service). Unlike <see cref="RemoveEvictedRecordsAsync"/>, this method:
    /// - Scopes ALL database operations to the specified entity.
    /// - DELETES the CachedGameDetection / CachedServiceDetection row for the entity outright
    ///   so the removal is durable - the next scan cannot resurrect it. A fresh row is
    ///   re-inserted only if the entity ever caches again.
    /// - Calls <see cref="UnevictCachedGameDetectionsAsync"/> / <see cref="UnevictCachedServiceDetectionsAsync"/>
    ///   at the end for defensive self-healing of unrelated rows.
    /// </summary>
    public async Task RemoveEvictedRecordsForEntityAsync(
        AppDbContext context,
        EvictionScope scope,
        string key,
        CancellationToken stoppingToken,
        Guid? operationId = null,
        string? namedGameName = null)
    {
        // Npgsql cannot translate string.Equals(..., StringComparison.OrdinalIgnoreCase);
        // service names are already stored lowercase, so lowercasing `key` once here lets
        // the EvictionScope.Service / EvictionScope.Named branches use plain `==` in the LINQ
        // (SQL-translatable). For Named scope `key` is the lowercased service and the game
        // name travels in `namedGameName` (case-sensitive, as stored in CachedGameDetection).
        var keyLower = key.ToLowerInvariant();

        if (scope == EvictionScope.Named && string.IsNullOrEmpty(namedGameName))
        {
            throw new ArgumentException(
                "namedGameName is required for EvictionScope.Named", nameof(namedGameName));
        }

        CancellationTokenSource? cts = null;

        // Deliberately not using TrackedRemovalOperationRunner: this service can start from the background scan path without a controller HTTP lifecycle.
        if (operationId == null)
        {
            // G3 intentional exemption: unlike the three sibling EvictionRemoval registers, this
            // self-start register never needs the silent-id cleanup. That is correct because this path
            // can never be silent: it is reached only when operationId == null, and the sole caller
            // (StartScopedEvictionRemovalAsync) ALWAYS passes a non-null, pre-registered operationId — so
            // this branch is the never-silent self-start fallback. It also emits a NON-silent
            // EvictionRemovalStarted below and never adds opId to _silentRemovalOperationIds (the only
            // TryAdd sites are in the bulk RemoveEvictedRecordsAsync path). It DOES, however, register an
            // onTerminalEmit (so the terminal EvictionRemovalComplete fires exactly once from inside
            // CompleteOperation) and a cleanup lambda that removes the terminal-state holder.
            cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var terminalState = new EvictionRemovalTerminalState();
            Guid selfRegisteredId = default;
            selfRegisteredId = _operationTracker.RegisterOperation(
                OperationType.EvictionRemoval,
                $"Eviction Removal ({scope}: {key})",
                cts,
                onTerminalCleanup: () => _evictionRemovalTerminalStates.TryRemove(selfRegisteredId, out _),
                // Terminal EvictionRemovalComplete fires EXACTLY ONCE from inside CompleteOperation.
                onTerminalEmit: CreateRemovalTerminalEmit(() => selfRegisteredId, terminalState));
            _evictionRemovalTerminalStates[selfRegisteredId] = terminalState;
            operationId = selfRegisteredId;

            await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalStarted,
                new EvictionRemovalStarted("signalr.evictionRemove.starting.entity", operationId.Value,
                    new Dictionary<string, object?> { ["scope"] = scope.ToString(), ["key"] = key },
                    GameName: scope == EvictionScope.Named ? namedGameName : null,
                    EpicAppId: scope == EvictionScope.Epic ? key : null));
        }

        var opId = operationId.Value;

        try
        {
            // Step -1: Rewrite nginx access.log files to drop entries for this entity's evicted
            // downloads BEFORE deleting LogEntries/Downloads from the database. Best-effort -
            // failures are logged as warnings and do not block the DB delete.
            await PurgeLogEntriesForEntityAsync(context, scope, key, opId, stoppingToken, namedGameName);

            int logEntriesDeleted = 0;
            int downloadsDeleted = 0;

            await ReportRemovalProgressAsync(
                opId,
                25,
                "removing_log_entries",
                "signalr.evictionRemove.removingLogs");

            // EF Core's NpgsqlRetryingExecutionStrategy forbids user-initiated transactions unless
            // they are wrapped in a strategy-controlled retry block. Without this wrapper any call
            // to BeginTransactionAsync throws InvalidOperationException. Match the pattern used in
            // DownloadCleanupService / DatabaseService / PicsDataService.
            var strategy = context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(stoppingToken);
                try
                {
                    // Step 1: Delete LogEntries for this entity's evicted Downloads (FK constraint).
                    logEntriesDeleted = scope switch
                    {
                        EvictionScope.Steam => await context.LogEntries
                            .Where(le => le.DownloadId != null
                                      && le.Download != null
                                      && le.Download.IsEvicted
                                      && le.Download.GameAppId == long.Parse(key)
                                      && le.Download.EpicAppId == null)
                            .ExecuteDeleteAsync(stoppingToken),

                        EvictionScope.Epic => await context.LogEntries
                            .Where(le => le.DownloadId != null
                                      && le.Download != null
                                      && le.Download.IsEvicted
                                      && le.Download.EpicAppId == key)
                            .ExecuteDeleteAsync(stoppingToken),

                        EvictionScope.Named => await context.LogEntries
                            .Where(le => le.DownloadId != null
                                      && le.Download != null
                                      && le.Download.IsEvicted
                                      && le.Download.GameAppId == null
                                      && le.Download.EpicAppId == null
                                      && le.Download.Service == keyLower
                                      && le.Download.GameName == namedGameName)
                            .ExecuteDeleteAsync(stoppingToken),

                        EvictionScope.Service => await context.LogEntries
                            .Where(le => le.DownloadId != null
                                      && le.Download != null
                                      && le.Download.IsEvicted
                                      && le.Download.GameAppId == null
                                      && le.Download.EpicAppId == null
                                      && le.Download.Service == keyLower)
                            .ExecuteDeleteAsync(stoppingToken),

                        _ => throw new ArgumentOutOfRangeException(nameof(scope))
                    };

                    // Step 2: Delete this entity's evicted Downloads.
                    await ReportRemovalProgressAsync(
                        opId,
                        50,
                        "removing_downloads",
                        "signalr.evictionRemove.removingDownloads",
                        logEntriesRemoved: logEntriesDeleted);

                    downloadsDeleted = scope switch
                    {
                        EvictionScope.Steam => await context.Downloads
                            .Where(d => d.IsEvicted
                                     && d.GameAppId == long.Parse(key)
                                     && d.EpicAppId == null)
                            .ExecuteDeleteAsync(stoppingToken),

                        EvictionScope.Epic => await context.Downloads
                            .Where(d => d.IsEvicted && d.EpicAppId == key)
                            .ExecuteDeleteAsync(stoppingToken),

                        EvictionScope.Named => await context.Downloads
                            .Where(d => d.IsEvicted
                                     && d.GameAppId == null
                                     && d.EpicAppId == null
                                     && d.Service == keyLower
                                     && d.GameName == namedGameName)
                            .ExecuteDeleteAsync(stoppingToken),

                        EvictionScope.Service => await context.Downloads
                            .Where(d => d.IsEvicted
                                     && d.GameAppId == null
                                     && d.EpicAppId == null
                                     && d.Service == keyLower)
                            .ExecuteDeleteAsync(stoppingToken),

                        _ => throw new ArgumentOutOfRangeException(nameof(scope))
                    };

                    await transaction.CommitAsync(stoppingToken);
                }
                catch
                {
                    await transaction.RollbackAsync(stoppingToken);
                    throw;
                }
            });

            if (downloadsDeleted > 0 || logEntriesDeleted > 0)
            {
                _logger.LogInformation(
                    "[EvictionScan] Entity removal ({Scope} '{Key}'): deleted {Downloads} evicted downloads and {LogEntries} associated log entries",
                    scope, key, downloadsDeleted, logEntriesDeleted);
            }

            // Step 3: Defensive self-heal - if all evicted rows for this entity are now gone, clear
            // the aggregate IsEvicted flag so Dashboard stats update on the next GetCachedDetectionAsync.
            await ReportRemovalProgressAsync(
                opId,
                75,
                "updating_status",
                "signalr.evictionRemove.updatingStatus",
                downloadsRemoved: downloadsDeleted,
                logEntriesRemoved: logEntriesDeleted);

            // Step 3a: Targeted un-evict - clear IsEvicted on the specific entity we just removed
            // downloads for. This is the equivalent of Game Cache Removal's row-delete for the
            // partial-eviction case: after the user removes the evicted portion, the entity is no
            // longer considered evicted regardless of CacheFilesFound (per CachedGameDetection.IsEvicted
            // docstring: "Games with no matching downloads are NOT considered evicted"). Any remaining
            // non-evicted downloads keep their row with IsEvicted=false. The next detection scan will
            // refresh CacheFilesFound / TotalSizeBytes if they drifted.
            // Removal-driven cleanup. Two cases:
            //   • Full removal - no Downloads remain for this entity after the delete above.
            //     DELETE the detection row so the next scan's flip-to-evicted logic cannot
            //     resurrect it. This is the WSUS case that used to come back after restart.
            //   • Partial eviction - some Downloads for this entity still exist and are NOT
            //     evicted (the entity has real cache files). Leave the detection row in
            //     place and just clear IsEvicted = false so the UI shows the entity as
            //     cached again. Deleting here would wipe a legitimately-cached entity.
            bool anyRemaining = scope switch
            {
                EvictionScope.Steam => await context.Downloads
                    .AnyAsync(d => d.GameAppId == long.Parse(key) && d.EpicAppId == null, stoppingToken),
                EvictionScope.Epic => await context.Downloads
                    .AnyAsync(d => d.EpicAppId == key, stoppingToken),
                EvictionScope.Named => await context.Downloads
                    .AnyAsync(d => d.GameAppId == null
                                && d.EpicAppId == null
                                && d.Service == keyLower
                                && d.GameName == namedGameName, stoppingToken),
                EvictionScope.Service => await context.Downloads
                    .AnyAsync(d => d.GameAppId == null
                                && d.EpicAppId == null
                                && d.Service == keyLower, stoppingToken),
                _ => false
            };

            int detectionRowsChanged;
            bool deletedActiveDetectionRow = false;
            if (!anyRemaining)
            {
                // The delete below removes the detection row regardless of its IsEvicted flag,
                // and a row can still be ACTIVE (contributing bytes to the persisted disk
                // summary) while every one of its downloads is evicted. Deleting such a row
                // dirties the summary, so capture that before the delete - it decides whether
                // the expensive disk-summary refresh below can be skipped.
                deletedActiveDetectionRow = scope switch
                {
                    EvictionScope.Steam => await context.CachedGameDetections
                        .AnyAsync(g => !g.IsEvicted
                                    && g.GameAppId == long.Parse(key)
                                    && g.EpicAppId == null, stoppingToken),

                    EvictionScope.Epic => await context.CachedGameDetections
                        .AnyAsync(g => !g.IsEvicted && g.EpicAppId == key, stoppingToken),

                    EvictionScope.Named => await context.CachedGameDetections
                        .AnyAsync(g => !g.IsEvicted
                                    && g.GameAppId == 0
                                    && g.EpicAppId == null
                                    && g.Service != null
                                    && g.Service.ToLower() == keyLower
                                    && g.GameName == namedGameName, stoppingToken),

                    EvictionScope.Service => await context.CachedServiceDetections
                        .AnyAsync(s => !s.IsEvicted && s.ServiceName == keyLower, stoppingToken),

                    _ => false
                };

                detectionRowsChanged = scope switch
                {
                    EvictionScope.Steam => await context.CachedGameDetections
                        .Where(g => g.GameAppId == long.Parse(key) && g.EpicAppId == null)
                        .ExecuteDeleteAsync(stoppingToken),

                    EvictionScope.Epic => await context.CachedGameDetections
                        .Where(g => g.EpicAppId == key)
                        .ExecuteDeleteAsync(stoppingToken),

                    // Named detection rows always carry GameAppId == 0 (never null) and EpicAppId == null;
                    // identity is (Service, GameName). Service is stored lowercase, GameName case-sensitive.
                    EvictionScope.Named => await context.CachedGameDetections
                        .Where(g => g.GameAppId == 0
                                 && g.EpicAppId == null
                                 && g.Service != null
                                 && g.Service.ToLower() == keyLower
                                 && g.GameName == namedGameName)
                        .ExecuteDeleteAsync(stoppingToken),

                    EvictionScope.Service => await context.CachedServiceDetections
                        .Where(s => s.ServiceName == keyLower)
                        .ExecuteDeleteAsync(stoppingToken),

                    _ => 0
                };
            }
            else
            {
                detectionRowsChanged = scope switch
                {
                    EvictionScope.Steam => await context.CachedGameDetections
                        .Where(g => g.IsEvicted
                                 && g.GameAppId == long.Parse(key)
                                 && g.EpicAppId == null)
                        .ExecuteUpdateAsync(g => g.SetProperty(x => x.IsEvicted, false), stoppingToken),

                    EvictionScope.Epic => await context.CachedGameDetections
                        .Where(g => g.IsEvicted && g.EpicAppId == key)
                        .ExecuteUpdateAsync(g => g.SetProperty(x => x.IsEvicted, false), stoppingToken),

                    EvictionScope.Named => await context.CachedGameDetections
                        .Where(g => g.IsEvicted
                                 && g.GameAppId == 0
                                 && g.EpicAppId == null
                                 && g.Service != null
                                 && g.Service.ToLower() == keyLower
                                 && g.GameName == namedGameName)
                        .ExecuteUpdateAsync(g => g.SetProperty(x => x.IsEvicted, false), stoppingToken),

                    EvictionScope.Service => await context.CachedServiceDetections
                        .Where(s => s.IsEvicted && s.ServiceName == keyLower)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEvicted, false), stoppingToken),

                    _ => 0
                };
            }

            if (detectionRowsChanged > 0)
            {
                var verb = anyRemaining ? "cleared IsEvicted on" : "deleted";
                _logger.LogInformation(
                    "[EvictionRemoval] Targeted removal for {Scope} '{Key}': {Verb} {Count} detection row(s)",
                    scope, key, verb, detectionRowsChanged);
            }

            // Step 3b: Bulk self-heal helper stays for the separate "files reappeared on disk"
            // use case (background reconciliation). It now keys off Downloads.IsEvicted (via
            // GetServicesToUnevictAsync) so it stays correct for that scenario; the targeted
            // un-evict above handles the user-triggered removal.
            int unevictedRows;
            if (scope == EvictionScope.Service)
            {
                unevictedRows = await UnevictCachedServiceDetectionsAsync(context, _logger, _gameCacheDetectionDataService, stoppingToken);
            }
            else
            {
                unevictedRows = await UnevictCachedGameDetectionsAsync(
                    context,
                    _logger,
                    _gameCacheDetectionDataService,
                    _evictedDetectionPreservationService,
                    stoppingToken);
            }

            var detectionService = _serviceProvider.GetService<GameCacheDetectionService>();
            if (detectionService != null)
            {
                // The disk-summary recompute stats every persisted cache path of every ACTIVE
                // detection row (minutes on large caches) but reads nothing from evicted rows,
                // so deleting an evicted entity's rows cannot change its result. It is needed
                // only when this removal flipped rows back to active (partial eviction clears
                // IsEvicted, or the self-heal un-evicted entities whose files reappeared).
                // Otherwise invalidating the in-memory detection cache is enough for the
                // frontend refetch to see the deleted rows, and the op completes in seconds
                // instead of sitting at 90% through a full path-stat walk.
                var summaryDirty = (anyRemaining && detectionRowsChanged > 0)
                    || deletedActiveDetectionRow
                    || unevictedRows > 0;
                if (summaryDirty)
                {
                    await ReportRemovalProgressAsync(
                        opId,
                        90,
                        "refreshing_detection",
                        "signalr.evictionRemove.refreshingDetection",
                        downloadsRemoved: downloadsDeleted,
                        logEntriesRemoved: logEntriesDeleted);

                    await detectionService.RefreshDiskSummaryAndInvalidateAsync(
                        stoppingToken,
                        (statted, totalPaths) => _ = ReportRemovalProgressAsync(
                            opId,
                            totalPaths > 0 ? 90.0 + (statted / (double)totalPaths) * 9.0 : 90.0,
                            "refreshing_detection",
                            "signalr.evictionRemove.refreshingDetectionCounted",
                            downloadsRemoved: downloadsDeleted,
                            logEntriesRemoved: logEntriesDeleted,
                            context: new Dictionary<string, object?>
                            {
                                ["filesChecked"] = statted.ToString("N0"),
                                ["filesTotal"] = totalPaths.ToString("N0")
                            }));
                }
                else
                {
                    await ReportRemovalProgressAsync(
                        opId,
                        90,
                        "finalizing_removal",
                        "signalr.evictionRemove.finalizingRemoval",
                        downloadsRemoved: downloadsDeleted,
                        logEntriesRemoved: logEntriesDeleted);

                    detectionService.InvalidateDetectionCache();
                }
            }

            await CompleteRemovalAsync(
                opId,
                success: true,
                stageKey: "signalr.evictionRemove.complete",
                downloadsRemoved: downloadsDeleted,
                logEntriesRemoved: logEntriesDeleted);
        }
        catch (OperationCanceledException)
        {
            // User-initiated cancel is an expected outcome, not an error.
            _logger.LogInformation("[EvictionScan] Eviction removal for {Scope} '{Key}' cancelled by user (operation {OpId})",
                scope, key, opId);
            await CompleteRemovalAsync(
                opId,
                success: false,
                stageKey: "signalr.evictionRemove.cancelled",
                error: "Cancelled by user",
                cancelled: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EvictionScan] Error removing evicted records for {Scope} '{Key}'", scope, key);
            await CompleteRemovalAsync(
                opId,
                success: false,
                stageKey: "signalr.evictionRemove.failed",
                error: ex.Message);
        }
        finally
        {
            cts?.Dispose();
        }
    }

    /// <summary>
    /// Entity-scoped variant of <see cref="PurgeLogEntriesAsync"/>. Rewrites nginx access.log
    /// files to drop entries belonging only to the specified entity's evicted downloads.
    /// Best-effort: failures are logged as warnings and do not block the DB delete.
    /// </summary>
    private async Task PurgeLogEntriesForEntityAsync(
        AppDbContext context,
        EvictionScope scope,
        string key,
        Guid operationId,
        CancellationToken stoppingToken,
        string? namedGameName = null)
    {
        // Npgsql cannot translate string.Equals(..., StringComparison.OrdinalIgnoreCase);
        // service names are stored lowercase, so lowercasing `key` here lets the
        // EvictionScope.Service / EvictionScope.Named branches use plain `==` in the LINQ.
        // For Named scope `key` is the lowercased service; the game name is in `namedGameName`.
        var keyLower = key.ToLowerInvariant();

        try
        {
            // Collect IDs of evicted Downloads scoped to this entity.
            var evictedDownloadIds = scope switch
            {
                EvictionScope.Steam => await context.Downloads
                    .Where(d => d.IsEvicted
                             && d.GameAppId == long.Parse(key)
                             && d.EpicAppId == null)
                    .Select(d => d.Id)
                    .ToListAsync(stoppingToken),

                EvictionScope.Epic => await context.Downloads
                    .Where(d => d.IsEvicted && d.EpicAppId == key)
                    .Select(d => d.Id)
                    .ToListAsync(stoppingToken),

                EvictionScope.Named => await context.Downloads
                    .Where(d => d.IsEvicted
                             && d.GameAppId == null
                             && d.EpicAppId == null
                             && d.Service == keyLower
                             && d.GameName == namedGameName)
                    .Select(d => d.Id)
                    .ToListAsync(stoppingToken),

                EvictionScope.Service => await context.Downloads
                    .Where(d => d.IsEvicted
                             && d.GameAppId == null
                             && d.EpicAppId == null
                             && d.Service == keyLower)
                    .Select(d => d.Id)
                    .ToListAsync(stoppingToken),

                _ => throw new ArgumentOutOfRangeException(nameof(scope))
            };

            if (evictedDownloadIds.Count == 0)
            {
                _logger.LogDebug("[EvictedLogPurge] No evicted downloads for {Scope} '{Key}' - skipping log rewrite", scope, key);
                return;
            }

            // Collect distinct URLs from LogEntries belonging to these evicted downloads.
            // URLs are the authoritative scope signal: every LogEntry row is tied to exactly
            // one Download via FK, so URL-based matching cannot leak into still-cached
            // downloads for the same entity.
            var urls = await context.LogEntries
                .Where(le => le.DownloadId != null && evictedDownloadIds.Contains(le.DownloadId.Value))
                .Select(le => le.Url)
                .Where(u => u != null && u != string.Empty)
                .Distinct()
                .ToListAsync(stoppingToken);

            // Collect candidate depot IDs from the evicted Downloads only.
            var evictedDepotIds = scope switch
            {
                EvictionScope.Steam => await context.Downloads
                    .Where(d => d.IsEvicted
                             && d.GameAppId == long.Parse(key)
                             && d.EpicAppId == null
                             && d.DepotId != null)
                    .Select(d => d.DepotId!.Value)
                    .Distinct()
                    .ToListAsync(stoppingToken),

                EvictionScope.Epic => await context.Downloads
                    .Where(d => d.IsEvicted && d.EpicAppId == key && d.DepotId != null)
                    .Select(d => d.DepotId!.Value)
                    .Distinct()
                    .ToListAsync(stoppingToken),

                EvictionScope.Named => await context.Downloads
                    .Where(d => d.IsEvicted
                             && d.GameAppId == null
                             && d.EpicAppId == null
                             && d.Service == keyLower
                             && d.GameName == namedGameName
                             && d.DepotId != null)
                    .Select(d => d.DepotId!.Value)
                    .Distinct()
                    .ToListAsync(stoppingToken),

                EvictionScope.Service => await context.Downloads
                    .Where(d => d.IsEvicted
                             && d.GameAppId == null
                             && d.EpicAppId == null
                             && d.Service == keyLower
                             && d.DepotId != null)
                    .Select(d => d.DepotId!.Value)
                    .Distinct()
                    .ToListAsync(stoppingToken),

                _ => throw new ArgumentOutOfRangeException(nameof(scope))
            };

            // IMPORTANT (partial-eviction safety): the Rust log purger matches lines on URL
            // OR depot_id. If a depot appears in BOTH evicted and non-evicted Downloads for
            // the same entity (e.g. a game was downloaded twice, only the older copy was
            // evicted), sending that depot_id would cause the purger to also remove log
            // lines belonging to the still-cached copy - data loss.
            //
            // Filter depot_ids down to those that ONLY appear in evicted Downloads for this
            // entity. This preserves the benefit of depot matching (catching orphan log
            // lines that have no LogEntry row) while preventing cross-state contamination.
            // Shared narrowing lives in NarrowDepotsToExclusivelyEvicted; only fetch the scoped
            // cached depot ids when there is something to narrow.
            var cachedDepotIds = evictedDepotIds.Count == 0
                ? new List<long>()
                : scope switch
                {
                    EvictionScope.Steam => await context.Downloads
                        .Where(d => !d.IsEvicted
                                 && d.GameAppId == long.Parse(key)
                                 && d.EpicAppId == null
                                 && d.DepotId != null)
                        .Select(d => d.DepotId!.Value)
                        .Distinct()
                        .ToListAsync(stoppingToken),

                    EvictionScope.Epic => await context.Downloads
                        .Where(d => !d.IsEvicted && d.EpicAppId == key && d.DepotId != null)
                        .Select(d => d.DepotId!.Value)
                        .Distinct()
                        .ToListAsync(stoppingToken),

                    EvictionScope.Named => await context.Downloads
                        .Where(d => !d.IsEvicted
                                 && d.GameAppId == null
                                 && d.EpicAppId == null
                                 && d.Service == keyLower
                                 && d.GameName == namedGameName
                                 && d.DepotId != null)
                        .Select(d => d.DepotId!.Value)
                        .Distinct()
                        .ToListAsync(stoppingToken),

                    EvictionScope.Service => await context.Downloads
                        .Where(d => !d.IsEvicted
                                 && d.GameAppId == null
                                 && d.EpicAppId == null
                                 && d.Service == keyLower
                                 && d.DepotId != null)
                        .Select(d => d.DepotId!.Value)
                        .Distinct()
                        .ToListAsync(stoppingToken),

                    _ => throw new ArgumentOutOfRangeException(nameof(scope))
                };

            var (safeDepotIds, skippedCount) = NarrowDepotsToExclusivelyEvicted(evictedDepotIds, cachedDepotIds);
            if (skippedCount > 0)
            {
                _logger.LogInformation(
                    "[EvictedLogPurge] Excluded {Skipped} depot ID(s) from log purge for {Scope} '{Key}' because they also appear in still-cached downloads (partial-eviction safety)",
                    skippedCount, scope, key);
            }

            var depotIds = safeDepotIds;

            if (urls.Count == 0 && depotIds.Count == 0)
            {
                _logger.LogInformation(
                    "[EvictedLogPurge] {Count} evicted downloads for {Scope} '{Key}' have no URL/depot history - nothing to purge from logs",
                    evictedDownloadIds.Count, scope, key);
                return;
            }
            await RunEvictedLogPurgeAsync(
                operationId,
                new EvictedLogPurgeTargets(urls, depotIds, evictedDownloadIds.Count),
                stoppingToken,
                new EvictedLogPurgeRunOptions(
                    "evicted_entity_log_purge_input",
                    "evicted_entity_log_purge_output",
                    10,
                    15,
                    $"entity log purge for {scope} '{key}'",
                    "Entity log purge",
                    $"Entity log purge summary ({scope} '{key}')"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort: log and continue so DB deletes still run.
            _logger.LogWarning(ex,
                "[EvictedLogPurge] Unexpected error during entity log purge ({Scope} '{Key}') - DB deletes will still proceed",
                scope, key);
        }
    }

    /// <summary>
    /// Progress-file schema written by the `cache_purge_log_entries` Rust binary - the same
    /// camelCase shape every other cache_* binary writes for the progress-file poller.
    /// </summary>
    private sealed class PurgeLogProgressData
    {
        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("stageKey")]
        public string? StageKey { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("percentComplete")]
        public double PercentComplete { get; set; }
    }

    /// <summary>
    /// Deserialized report from the `cache_purge_log_entries` Rust binary's output JSON.
    /// </summary>
    private sealed class PurgeLogEntriesReport
    {
        [System.Text.Json.Serialization.JsonPropertyName("success")]
        public bool Success { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("lines_removed")]
        public long LinesRemoved { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("permission_errors")]
        public int PermissionErrors { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}

/// <summary>
/// Progress data from the Rust eviction scan binary (read from progress JSON file)
/// </summary>
internal class EvictionScanProgressData
{
    public OperationStatus Status { get; set; } = OperationStatus.Pending;

    [System.Text.Json.Serialization.JsonPropertyName("stageKey")]
    public string StageKey { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
    public double PercentComplete { get; set; }
    public int Processed { get; set; }
    public int TotalEstimate { get; set; }
    public int Evicted { get; set; }
    public int UnEvicted { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("context")]
    public Dictionary<string, object?>? Context { get; set; }
}

/// <summary>
/// Result from the Rust eviction scan binary (parsed from stdout JSON)
/// </summary>
internal class EvictionScanResult
{
    public bool Success { get; set; }
    public int Processed { get; set; }
    public int Evicted { get; set; }
    public int UnEvicted { get; set; }
    public int FilesOnDisk { get; set; }
    public string? Error { get; set; }
}
