using System.Collections.Concurrent;
using System.Text.Json;
using LancacheManager.Core.Cache;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
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
    private readonly DatasourceCapabilityService _capabilityService;
    private readonly CacheScanGate _cacheScanGate;
    private readonly IStateService _stateService;
    private readonly ISignalRNotificationService _notifications;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly NginxLogRotationService _nginxLogRotationService;
    private readonly IPathResolver _pathResolver;
    private readonly GameCacheDetectionDataService _cacheDetections;
    private readonly GameCacheDetectionService _gameCacheDetectionService;
    private readonly EvictedDetectionPreservationService _evictedDetectionPreservationService;
    private readonly IOperationQueue _operationQueue;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private int _isRunning;
    private Guid? _currentScanOperationId;
    // Broadcast gate shared by the rust stdout-tick callback and ReportScanProgressAsync.
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
    protected override bool SupportsNotifications => true;
    // The 6-hour tick is maintenance: an unconfigured install stays off the notification bar
    // (Silent) and uses the condensed status line. The Schedules card is what turns those back on.
    protected override NotificationMode DefaultNotificationMode => NotificationMode.Silent;

    public override string ServiceKey => "cacheReconciliation";

    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;
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
    /// Returns the operationId immediately, or null if already running. The caller's notice carries
    /// this service's notification mode and the manual trigger (this call site bypasses
    /// TriggerImmediateRun entirely, so CurrentRunTrigger cannot be relied on here).
    /// </summary>
    public Guid? RunManualAsync(RunNotice notice)
    {
        return StartScanInBackground(
            "Eviction Scan",
            deferIfDownloading: false,
            notice: notice);
    }

    /// <summary>
    /// Starts a scan whose lifetime belongs to this singleton rather than to the scheduler
    /// invocation that requested it. This is required for wait-queue promotion, which may happen
    /// long after the original scheduled tick and its scoped DbContext have ended.
    /// </summary>
    /// <param name="deferIfDownloading">
    /// True for the runs nobody is watching, so a scan promoted into a download that started while it
    /// waited is owed rather than lost. False for a person's own scan: they are told why it stopped,
    /// and one arriving by itself an hour later would be a surprise.
    /// </param>
    private Guid? StartScanInBackground(
        string name,
        bool deferIfDownloading,
        RunNotice notice,
        Action? onCompleted = null)
    {
        if (!TryBeginRun())
        {
            return null;
        }

        CancellationTokenSource? cts = null;
        Guid operationId = default;
        var operationRegistered = false;
        var ownsToken = false;
        // Disposed by whichever path ends this attempt. The host token outlives every scan, so a
        // registration left on it would hold this scan's token source for the life of the process.
        var stopScanOnShutdown = default(CancellationTokenRegistration);
        try
        {
            // A scan that waited keeps the parked operation. A second registration is a second
            // notification. The parked token is the one the waiting card already cancels.
            var parked = notice.OperationId is Guid parkedId
                ? _operationTracker.GetOperation(parkedId)
                : null;
            var continueQueued = parked?.Status == OperationStatus.Waiting
                && parked.Type == OperationType.EvictionScan
                && parked.CancellationTokenSource != null;
            if (continueQueued)
            {
                cts = parked!.CancellationTokenSource!;
                stopScanOnShutdown = _applicationLifetime.ApplicationStopping.Register(static source =>
                {
                    var tokenSource = (CancellationTokenSource)source!;
                    try
                    {
                        if (!tokenSource.IsCancellationRequested) tokenSource.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // The scan already finished and the tracker disposed its token.
                    }
                }, cts);
            }
            else
            {
                // The operation is detached from the scheduler/HTTP request that launched it, but it
                // must still stop with the host so its Rust child cannot outlive application shutdown.
                cts = CancellationTokenSource.CreateLinkedTokenSource(
                    _applicationLifetime.ApplicationStopping);
                ownsToken = true;
            }

            operationId = RegisterEvictionScanOperation(name, cts, notice);
            if (operationId == Guid.Empty)
            {
                stopScanOnShutdown.Dispose();
                EndRun();
                return null;
            }

            operationRegistered = true;

            _ = Task.Run(async () =>
            {
                var outcome = new EvictionScanRunOutcome(
                    Success: false,
                    Error: "Eviction scan worker failed before it started");
                var stoppedByShutdown = false;
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    outcome = await ReconcileCacheFilesAsync(
                        context,
                        operationId,
                        cts.Token,
                        notice,
                        deferIfDownloading);
                }
                catch (OperationCanceledException) when (_applicationLifetime.ApplicationStopping.IsCancellationRequested)
                {
                    // The scan's outcome was never recorded, so the next start owns its repair record
                    // and the card with it.
                    stoppedByShutdown = true;
                }
                catch (Exception ex)
                {
                    outcome = new EvictionScanRunOutcome(Success: false, Error: ex.Message);
                    _logger.LogError(ex, "[EvictionScan] Background scan worker failed unexpectedly");
                }
                finally
                {
                    stopScanOnShutdown.Dispose();
                    // Single owner and strict ordering: release the service-local gate exactly once,
                    // then complete the tracker operation so queue promotion can safely acquire it.
                    EndRun();
                    // A skipped run did not fail, so it is completed with success true and the
                    // reason on the message, which is the pairing CompleteOperation documents. The
                    // card ends even while the scan's repair still runs or its outcome save retries.
                    if (!stoppedByShutdown)
                    {
                        _operationTracker.CompleteOperation(
                            operationId,
                            outcome.Success || outcome.Skipped,
                            outcome.Error,
                            skipped: outcome.Skipped,
                            onCompleting: operation =>
                            {
                                if (_evictionScanTerminalStates.TryGetValue(operationId, out var terminalState))
                                {
                                    terminalState.Processed = outcome.Processed;
                                    terminalState.Evicted = outcome.Evicted;
                                    terminalState.UnEvicted = outcome.UnEvicted;
                                }
                                if (outcome.Success) operation.PercentComplete = 100;
                            });
                    }
                    onCompleted?.Invoke();
                }
            }, CancellationToken.None);

            return operationId;
        }
        catch (Exception ex)
        {
            stopScanOnShutdown.Dispose();
            EndRun();
            if (operationRegistered)
            {
                _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);
            }
            else if (ownsToken)
            {
                cts?.Dispose();
            }
            onCompleted?.Invoke();
            throw;
        }
    }

    public CacheReconciliationService(
        IServiceProvider serviceProvider,
        ILogger<CacheReconciliationService> logger,
        IConfiguration configuration,
        DatasourceService datasourceService,
        IStateService stateService,
        ISignalRNotificationService notifications,
        IUnifiedOperationTracker operationTracker,
        RustProcessHelper rustProcessHelper,
        NginxLogRotationService nginxLogRotationService,
        IPathResolver pathResolver,
        GameCacheDetectionDataService gameCacheDetectionDataService,
        GameCacheDetectionService gameCacheDetectionService,
        EvictedDetectionPreservationService evictedDetectionPreservationService,
        IOperationQueue operationQueue,
        IHostApplicationLifetime applicationLifetime,
        DatasourceCapabilityService capabilityService,
        CacheScanGate cacheScanGate)
        : base(serviceProvider, logger, configuration)
    {
        _capabilityService = capabilityService;
        _cacheScanGate = cacheScanGate;
        _datasourceService = datasourceService;
        _stateService = stateService;
        _notifications = notifications;
        _operationTracker = operationTracker;
        _rustProcessHelper = rustProcessHelper;
        _nginxLogRotationService = nginxLogRotationService;
        _pathResolver = pathResolver;
        _cacheDetections = gameCacheDetectionDataService;
        _gameCacheDetectionService = gameCacheDetectionService;
        _evictedDetectionPreservationService = evictedDetectionPreservationService;
        _operationQueue = operationQueue;
        _applicationLifetime = applicationLifetime;

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

    protected override async Task OnStartupAsync(CancellationToken stoppingToken)
    {
        // Wait for setup to complete so datasources and database are configured
        await _stateService.WaitForSetupCompletedAsync(stoppingToken);

        var notice = CurrentRunNotice;

        try
        {
            bool hasDownloads;
            using (var scope = _serviceProvider.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                hasDownloads = await context.Downloads.AnyAsync(stoppingToken);
            }

            // Skip scan entirely if there are no downloads in the database
            if (!hasDownloads)
            {
                _logger.LogInformation("[EvictionScan] No downloads in database, skipping startup scan");
                return;
            }

            var scanCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<Guid?> StartStartupScanAsync() => Task.FromResult(StartScanInBackground(
                "Eviction Scan",
                deferIfDownloading: true,
                notice,
                () => scanCompleted.TrySetResult()));

            // No reportRefusal here: that only fires for a refusal the start delegate throws, and
            // this one cannot. StartScanInBackground returns an id before any download is checked,
            // and the check that does happen returns a skipped outcome from the background worker,
            // which this service announces itself on its own terminal event.
            var outcome = await _operationQueue.EnqueueAsync(
                OperationType.EvictionScan,
                ConflictScope.Bulk(),
                "Eviction Scan",
                StartStartupScanAsync,
                stoppingToken,
                notice: notice);

            if (outcome.Queued || outcome.AlreadyRunning)
            {
                // Preserve the old startup dependency behavior when another heavy operation is
                // already active: let GameDetectionService continue while this scan remains queued
                // or an identical scan is already represented by the queue/tracker.
                _logger.LogInformation(
                    "[EvictionScan] Startup scan {Disposition} (operation: {OperationId})",
                    outcome.Queued ? "queued" : "already requested",
                    outcome.OperationId);
                return;
            }

            await scanCompleted.Task.WaitAsync(stoppingToken);
        }
        finally
        {
            // Signal GameDetectionService that the first startup scan (and any removal cleanup) is done.
            // TrySetResult is safe to call multiple times - only the first call has effect.
            _firstStartupScanComplete.TrySetResult(true);
        }
    }

    protected override async Task ExecuteWorkAsync(
        IServiceProvider scopedServices,
        CancellationToken stoppingToken)
    {
        var notice = CurrentRunNotice;
        var context = scopedServices.GetRequiredService<AppDbContext>();

        // Skip scan if there are no downloads in the database. A Run Now reports itself skipped so the
        // click is answered; an automatic run stays quiet.
        if (!await context.Downloads.AnyAsync(stoppingToken))
        {
            _logger.LogDebug("[EvictionScan] No downloads in database, skipping scheduled scan");
            if (notice.Trigger == RunTrigger.Manual)
            {
                var skippedId = _operationTracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan",
                    new CancellationTokenSource(), notice: notice);
                notice.Attach(_operationTracker, skippedId);
                _operationTracker.CompleteOperation(skippedId, success: true,
                    error: ScheduledRunReporter.NothingToDoStageKey, skipped: true);
            }
            return;
        }

        Task<Guid?> StartScheduledScanAsync() => Task.FromResult(
            StartScanInBackground("Eviction Scan", deferIfDownloading: true, notice: notice));

        // Same as the startup path: the start delegate cannot throw a refusal, so asking the queue
        // to announce one would announce nothing. The refusal this run can hit is reported by the
        // background worker's own terminal event.
        var outcome = await _operationQueue.EnqueueAsync(
            OperationType.EvictionScan,
            ConflictScope.Bulk(),
            "Eviction Scan",
            StartScheduledScanAsync,
            stoppingToken,
            notice: notice);

        if (outcome.Queued)
        {
            _logger.LogInformation(
                "[EvictionScan] Scheduled scan queued (waiting operation: {OperationId})",
                outcome.OperationId);
        }
        else if (outcome.AlreadyRunning)
        {
            _logger.LogInformation(
                "[EvictionScan] Scheduled scan already requested (operation: {OperationId})",
                outcome.OperationId);
        }
        else
        {
            _logger.LogInformation(
                "[EvictionScan] Scheduled scan started (operation: {OperationId})",
                outcome.OperationId);
        }
    }

    private async Task<EvictionScanRunOutcome> ReconcileCacheFilesAsync(
        AppDbContext context,
        Guid operationId,
        CancellationToken stoppingToken,
        RunNotice notice,
        bool deferIfDownloading)
    {
        var isRemoveMode = _stateService.GetEvictedDataMode() == EvictedDataMode.Remove.ToWireString();

        // A manual request parked behind another operation can be promoted much later, so the
        // download state is read again here rather than only at request time. Asked before the
        // capability revalidation below, which enumerates log directories for a run that is
        // already refused.
        var downloadDenial = _cacheScanGate.CheckDownloadInProgress();
        if (downloadDenial != null)
        {
            _logger.LogWarning("[EvictionScan] Skipping eviction scan: {Reason}", downloadDenial);
            if (deferIfDownloading)
            {
                // This refusal is read here, inside the promoted run, and never reaches the schedule's
                // own run gate, so nothing else has recorded that a run is owed. Waking the loop puts
                // the run back in front of that gate, which refuses it for the same download and holds
                // it there until downloads stop. Only the download branch arms this: the capability
                // refusal below can stay true indefinitely, and waking for that would be a loop.
                // The terminal listener retains this admitted run until downloads stop.

                // No error text: the card prints this field verbatim in preference to any
                // translation key, so the gate's English would tell the reader to try again for a
                // run that is already coming back on its own, and a key would show as the key.
                return new EvictionScanRunOutcome(Success: false, Error: null, Skipped: true);
            }

            return new EvictionScanRunOutcome(Success: false, Error: downloadDenial, Skipped: true);
        }

        // Execution-time capability revalidation prevents a queued scan from running after
        // the fleet changes to mixed or unknown key evidence. Fail closed across the whole
        // fleet because a partial scan would mark false evictions.
        var capabilityDenial = _capabilityService.CheckAllCanMapLogicalObjects();
        if (capabilityDenial != null)
        {
            _logger.LogWarning("[EvictionScan] Skipping eviction scan: {Reason}", capabilityDenial);
            return new EvictionScanRunOutcome(Success: false, Error: capabilityDenial);
        }

        var repairOwner = _serviceProvider.GetRequiredService<OperationStateService>();
        var trackedOperation = _operationTracker.GetOperation(operationId)
            ?? throw new InvalidOperationException($"Eviction scan {operationId} is not tracked.");
        var repairSources = _datasourceService.GetDatasources()
            .Where(source => source.Enabled && !string.IsNullOrWhiteSpace(source.CachePath))
            .Select(source => new OperationRepairSource
            {
                Datasource = source.Name,
                LogRoot = source.LogPath,
                CacheRoot = source.CachePath,
                KeyScheme = _capabilityService.GetKeySchemeWireValue(source),
                ReconcileCache = true,
                RefreshDetection = true
                // No corruption invalidation: a scan deletes no cache file, so the results stay removable.
            })
            .ToList();
        await repairOwner.PrepareRepairAsync(
            new OperationRepair
            {
                Id = operationId,
                Type = OperationType.EvictionScan,
                Name = trackedOperation.Name,
                StartedAt = trackedOperation.StartedAt,
                Notice = notice,
                Sources = repairSources,
                EvictionScan = new EvictionScanRepair()
            },
            stoppingToken);

        string? datasourceConfigPath = null;
        string? progressFilePath = null;
        var operationSucceeded = false;
        var operationCancelled = false;
        var scanPrepared = false;
        string? operationError = null;
        var completedScan = new EvictionScanResult();

        try
        {
            await _notifications.NotifyAllAsync(SignalREvents.EvictionScanStarted, new EvictionScanStarted(
                StageKey: "signalr.evictionScan.detectingGames",
                OperationId: operationId));

            await RunFullDetectionPhaseAsync(
                operationId,
                stoppingToken: stoppingToken);
            stoppingToken.ThrowIfCancellationRequested();
            await ReportScanProgressAsync(
                operationId,
                0,
                "signalr.evictionScan.scanning",
                new EvictionScanResult());

            // Checked again after the detection phase, which can run for minutes: evidence that turned mixed
            // or unknown meanwhile refuses the scan here, before it starts any work, so it owes no repair.
            var detectionPhaseDenial = _capabilityService.CheckAllCanMapLogicalObjects();
            if (detectionPhaseDenial != null)
            {
                throw new ConflictException(detectionPhaseDenial);
            }

            _logger.LogInformation("[EvictionScan] Starting eviction scan via Rust binary");

            // Create progress file for monitoring
            progressFilePath = Path.GetTempFileName();

            context.EvictionScanCheckpoints.Add(new EvictionScanCheckpoint
            {
                OperationId = operationId,
                StartedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync(stoppingToken);
            await repairOwner.SaveRepairAsync(
                operationId,
                repair => repair.EvictionScanId = operationId,
                stoppingToken);
            scanPrepared = true;

            await repairOwner.StartWorkAsync(
                operationId,
                datasource: null,
                cancellationToken: stoppingToken);

            // Read after the repair waits above: key evidence can change while a repair runs, and the scan
            // must use the scheme the log files show at launch. Evidence that turned mixed or unknown
            // refuses the scan here, before its launch, the same way the check after the detection phase does.
            // Write datasource configuration to temp file for the Rust binary
            datasourceConfigPath = Path.GetTempFileName();
            var datasourceConfig = _datasourceService.GetDatasources().Select(ds =>
            {
                string keyScheme;
                try
                {
                    keyScheme = _capabilityService.GetKeySchemeWireValue(ds);
                }
                catch (InvalidOperationException denial)
                {
                    throw new ConflictException(denial.Message);
                }
                return new
                {
                    name = ds.Name,
                    cachePath = ds.CachePath,
                    isDefault = ds == _datasourceService.GetDefaultDatasource(),
                    keyScheme
                };
            }).ToArray();
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            await File.WriteAllTextAsync(datasourceConfigPath, JsonSerializer.Serialize(datasourceConfig, jsonOptions), stoppingToken);

            // Each source is marked launched only after its scheme is read above: a scan refused or cancelled
            // before this point launched nothing, so its repair checks no source's scheme and only finalizes
            // the scan's empty checkpoint.
            foreach (var source in repairSources)
            {
                await repairOwner.StartWorkAsync(operationId, source.Datasource, stoppingToken);
            }

            // Hybrid transport (mirrors CacheClearingService): the stdout progress event from
            // cache_eviction_scan.rs is a zero-latency wake-up that triggers exactly one read of
            // the (Rust-side-unchanged) progress file, replacing the previous standalone
            // MonitorProgressFileAsync poll-every-500ms task. Lifecycle events remain wired for
            // state tracking regardless of presentation mode.
            Func<RustProgressEvent, Task>? onProgressEvent = async _ =>
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
                    if (_evictionScanTerminalStates.TryGetValue(operationId, out var terminalState) && terminalState.DetectionError != null)
                        context["detectionError"] = terminalState.DetectionError;

                    // The Rust disk scan owns 0-85% of the bar. The C# post-processing that
                    // follows (detection-row updates, post-scan recovery, and the disk-summary
                    // refresh - minutes on large databases) owns 85-100%. Forwarding the raw
                    // Rust percent left the bar at 99.5% while most of the wall-clock tail was
                    // still ahead.
                    var scaledPercent = progress.PercentComplete * 0.85;

                    var accepted = false;
                    _operationTracker.UpdateProgress(operationId, scaledPercent, stageKey, onProgress: operation =>
                    {
                        lock (_evictionScanTerminalStates)
                        {
                            if (_currentScanOperationId == operationId) _currentScanProgressContext = context;
                        }
                        if (operation.Metadata is Dictionary<string, object?> values) values["context"] = context;
                        accepted = true;
                    });

                    // Gate the broadcast (tracker + recovery context above stay per-tick): rust
                    // ticks can arrive many times per second and every emit re-renders every
                    // client. Emit on stage change or at most every 250ms; the terminal state
                    // travels on EvictionScanComplete, never a gated tick.
                    if (!accepted || !ShouldEmitScanProgress(stageKey))
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
                datasourceConfigPath,
                progressFilePath,
                stoppingToken,
                operationId,
                onProgressEvent,
                scanId: operationId);

            stoppingToken.ThrowIfCancellationRequested();

            // Parse result
            var scanResult = ParseScanResult(result);

            if (scanResult.Success)
            {
                _logger.LogInformation(
                    "[EvictionScan] Scan complete: processed {Total} downloads, {Evicted} newly evicted, {UnEvicted} un-evicted (re-cached)",
                    scanResult.Processed, scanResult.Evicted, scanResult.UnEvicted);

                // A cache folder that was missing, empty or partly unreadable was not checked, so a
                // wrong mount no longer passes as a clean scan.
                if (scanResult.UncheckedFolders.Count > 0)
                {
                    var folders = string.Join(", ", scanResult.UncheckedFolders);
                    _logger.LogWarning("[EvictionScan] Cache folders not checked: {Folders}", folders);
                    _operationTracker.SetWarning(operationId, new RunWarning(
                        "common.notifications.warnings.cacheFoldersUnchecked",
                        new Dictionary<string, object?> { ["folders"] = folders }));
                    // Kept on the repair record at once, so a restart before the run ends restores the warning.
                    await repairOwner.SaveRepairAsync(
                        operationId,
                        repair => repair.EvictionScan!.UncheckedFolders = scanResult.UncheckedFolders,
                        stoppingToken);
                }

                await ReportScanProgressAsync(
                    operationId,
                    86.0,
                    "signalr.evictionScan.postProcessing",
                    scanResult);

                stoppingToken.ThrowIfCancellationRequested();

                await ReportScanProgressAsync(
                    operationId,
                    92.0,
                    "signalr.evictionScan.refreshingSummary",
                    scanResult);

                // Capture the success metrics BY VALUE before the optional removal phase. The scan
                // operation deliberately remains active through that tail so queue promotion cannot
                // start another full-disk scan while remove-mode cleanup is still mutating cache/log
                // state. Internal removal registration does not run a controller conflict check.
                var scanRepair = repairOwner.GetPendingRepairs()
                    .Single(repair => repair.Id == operationId);
                await FinalizeEvictionScanAttemptAsync(
                    scanRepair,
                    operationId,
                    saveScanMetrics: true,
                    stoppingToken: stoppingToken);
                var committedCheckpoint = await context.EvictionScanCheckpoints
                    .AsNoTracking()
                    .SingleAsync(entry => entry.OperationId == operationId, stoppingToken);
                completedScan = new EvictionScanResult
                {
                    Success = true,
                    Processed = committedCheckpoint.Processed,
                    Evicted = committedCheckpoint.Evicted,
                    UnEvicted = committedCheckpoint.UnEvicted,
                    FilesOnDisk = scanResult.FilesOnDisk
                };
                await repairOwner.FinishRepairAsync(
                    operationId,
                    success: true,
                    cancelled: false,
                    error: null);

                // Handle evicted data "remove" mode. The removal self-registers its OWN
                // OperationType.EvictionRemoval operation (operationId: null) so it is cancellable,
                // visible to GET /api/cache/removals/active, and emits its own
                // EvictionRemovalStarted/Progress/Complete events with its own operationId. The
                // removal gets a fresh notice with the scan's mode and trigger, so it is drawn the
                // way the scan is; the scan's own notice stays attached to the scan.
                if (isRemoveMode
                    && await context.Downloads.AnyAsync(d => d.IsEvicted, stoppingToken))
                {
                    await RemoveEvictedRecordsAsync(context, stoppingToken, operationId: null,
                        notice: new RunNotice(notice.Mode, notice.Trigger));
                }

                stoppingToken.ThrowIfCancellationRequested();
                operationSucceeded = true;
            }
            else
            {
                var errorMsg = scanResult.Error ?? "Rust eviction scan binary returned failure";
                _logger.LogError("[EvictionScan] Rust binary failed: {Error}", errorMsg);
                operationError = errorMsg;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[EvictionScan] Operation {OperationId} was cancelled", operationId);
            operationCancelled = true;
            // No error text: the run stopped on request, and Success:false already carries that.
        }
        catch (ConflictException refusal)
        {
            // Refused after the detection phase or the repair wait, before the scan launched: logged as the
            // same refusal before the detection phase is.
            _logger.LogWarning("[EvictionScan] Skipping eviction scan: {Reason}", refusal.Message);
            operationError = refusal.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EvictionScan] Error during eviction scan");
            operationError = ex.Message;
        }
        finally
        {
            // Clean up temp files before returning the outcome to the single owner in
            // StartScanInBackground. That owner releases the local gate and completes the tracker.
            if (datasourceConfigPath != null)
                await _rustProcessHelper.DeleteTempFileAsync(datasourceConfigPath);
            if (progressFilePath != null)
                await _rustProcessHelper.DeleteTempFileAsync(progressFilePath);
        }

        if (scanPrepared)
        {
            try
            {
                var checkpoint = await context.EvictionScanCheckpoints
                    .AsNoTracking()
                    .SingleAsync(
                        entry => entry.OperationId == operationId,
                        _applicationLifetime.ApplicationStopping);
                completedScan.Processed = checkpoint.Processed;
                completedScan.Evicted = checkpoint.Evicted;
                completedScan.UnEvicted = checkpoint.UnEvicted;
                await repairOwner.SaveRepairAsync(
                    operationId,
                    repair =>
                    {
                        repair.EvictionScan = new EvictionScanRepair
                        {
                            Processed = checkpoint.Processed,
                            Evicted = checkpoint.Evicted,
                            UnEvicted = checkpoint.UnEvicted,
                            DetectionError = _evictionScanTerminalStates.TryGetValue(operationId, out var terminal)
                                ? terminal.DetectionError
                                : null,
                            UncheckedFolders = repair.EvictionScan?.UncheckedFolders
                        };
                    },
                    _applicationLifetime.ApplicationStopping);
            }
            catch (OperationCanceledException) when (_applicationLifetime.ApplicationStopping.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                operationSucceeded = false;
                operationCancelled = false;
                operationError = ex.Message;
                _logger.LogError(
                    ex,
                    "[EvictionScan] Failed to read or save checkpoint {OperationId}",
                    operationId);
            }
        }
        await repairOwner.FinishRepairAsync(
            operationId,
            operationSucceeded,
            operationCancelled,
            operationError);

        return new EvictionScanRunOutcome(operationSucceeded, operationError,
            RepairStarted: true,
            Processed: completedScan.Processed, Evicted: completedScan.Evicted, UnEvicted: completedScan.UnEvicted);
    }

    public Task RestoreRepairAsync(OperationRepair repair, CancellationToken stoppingToken)
    {
        stoppingToken.ThrowIfCancellationRequested();
        return repair.Type switch
        {
            OperationType.EvictionScan => RestoreEvictionScanRepairAsync(repair),
            OperationType.EvictionRemoval => RestoreEvictionRemovalRepairAsync(repair),
            _ => throw new InvalidOperationException(
                $"Operation type {repair.Type} is not owned by cache reconciliation.")
        };
    }

    public virtual Task ResumeRepairAsync(OperationRepair repair, CancellationToken stoppingToken)
    {
        return repair.Type switch
        {
            OperationType.EvictionScan => FinalizeEvictionScanAsync(repair, stoppingToken),
            OperationType.EvictionRemoval => FinalizeEvictionRemovalAsync(repair, stoppingToken),
            _ => throw new InvalidOperationException(
                $"Operation type {repair.Type} is not owned by cache reconciliation.")
        };
    }

    private Task RestoreEvictionScanRepairAsync(OperationRepair repair)
    {
        var metrics = repair.EvictionScan
            ?? throw new InvalidDataException($"Eviction scan repair {repair.Id} has no metrics.");
        var state = new EvictionScanTerminalState
        {
            Processed = metrics.Processed,
            Evicted = metrics.Evicted,
            UnEvicted = metrics.UnEvicted,
            DetectionError = metrics.DetectionError
        };
        var cts = new CancellationTokenSource();
        Func<OperationTerminalInfo, Task> emit = terminal =>
        {
            var context = new Dictionary<string, object?>
            {
                ["totalProcessed"] = state.Processed,
                ["totalEvicted"] = state.Evicted,
                ["totalUnEvicted"] = state.UnEvicted
            };
            if (state.DetectionError is not null)
            {
                context["detectionError"] = state.DetectionError;
            }

            return _notifications.NotifyAllAsync(
                SignalREvents.EvictionScanComplete,
                new EvictionScanComplete(
                    Success: terminal.Success,
                    OperationId: repair.Id,
                    StageKey: terminal.Cancelled
                        ? "signalr.evictionScan.cancelled"
                        : "signalr.evictionScan.complete",
                    Processed: state.Processed,
                    Evicted: state.Evicted,
                    UnEvicted: state.UnEvicted,
                    Error: terminal.Error,
                    Context: context,
                    Cancelled: terminal.Cancelled));
        };

        var restored = _operationTracker.TryRestoreOperation(
            repair.Id,
            repair.Type,
            repair.Name,
            cts,
            new Dictionary<string, object?>(),
            onTerminalCleanup: () => _evictionScanTerminalStates.TryRemove(repair.Id, out _),
            onTerminalEmit: emit,
            startedAt: repair.StartedAt,
            notice: repair.Notice,
            ownerCompletes: true);
        if (!restored)
        {
            cts.Dispose();
            return Task.CompletedTask;
        }

        // A scan that left cache folders unchecked ends amber after a restart too.
        if (metrics.UncheckedFolders is { Count: > 0 } folders)
        {
            _operationTracker.SetWarning(repair.Id, new RunWarning(
                "common.notifications.warnings.cacheFoldersUnchecked",
                new Dictionary<string, object?> { ["folders"] = string.Join(", ", folders) }));
        }

        // A scan whose game detection failed ends amber after a restart too.
        if (metrics.DetectionError is { } detectionError)
        {
            _operationTracker.SetWarning(repair.Id, new RunWarning(
                "signalr.gameDetect.error.fatal",
                new Dictionary<string, object?> { ["errorDetail"] = detectionError }));
        }

        _evictionScanTerminalStates[repair.Id] = state;
        lock (_evictionScanTerminalStates)
        {
            _currentScanOperationId = repair.Id;
            _currentScanProgressContext = null;
        }
        return Task.CompletedTask;
    }

    private Task RestoreEvictionRemovalRepairAsync(OperationRepair repair)
    {
        var metrics = repair.EvictionRemoval
            ?? throw new InvalidDataException($"Eviction removal repair {repair.Id} has no metrics.");
        var state = new EvictionRemovalTerminalState
        {
            StageKey = metrics.StageKey,
            DownloadsRemoved = metrics.DownloadsRemoved,
            LogEntriesRemoved = metrics.LogEntriesRemoved
        };
        var cts = new CancellationTokenSource();
        var restored = _operationTracker.TryRestoreOperation(
            repair.Id,
            repair.Type,
            repair.Name,
            cts,
            metrics.Selection,
            onTerminalCleanup: () => _evictionRemovalTerminalStates.TryRemove(repair.Id, out _),
            onTerminalEmit: BuildTerminalEmit(() => repair.Id, state),
            startedAt: repair.StartedAt,
            notice: repair.Notice,
            ownerCompletes: true);
        if (!restored)
        {
            cts.Dispose();
            return Task.CompletedTask;
        }

        _evictionRemovalTerminalStates[repair.Id] = state;
        return Task.CompletedTask;
    }

    private async Task FinalizeEvictionScanAsync(
        OperationRepair repair,
        CancellationToken stoppingToken)
    {
        if (!repair.EvictionScanId.HasValue)
        {
            return;
        }

        await FinalizeEvictionScanAttemptAsync(
            repair,
            repair.EvictionScanId.Value,
            saveScanMetrics: true,
            stoppingToken: stoppingToken);
    }

    public virtual async Task ReconcileRepairAsync(
        OperationRepair repair,
        CancellationToken stoppingToken)
    {
        var sources = repair.Sources
            .Where(source => source.NativeLaunchAuthorized
                && source.ReconcileCache
                && !string.IsNullOrWhiteSpace(source.CacheRoot)
                && !string.IsNullOrWhiteSpace(source.KeyScheme))
            .ToList();
        if (sources.Count == 0)
        {
            return;
        }

        if (repair.EvictionScanId is Guid previousScanId)
        {
            using var previousScope = _serviceProvider.CreateScope();
            var previousContext = previousScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var previous = await previousContext.EvictionScanCheckpoints
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    entry => entry.OperationId == previousScanId,
                    stoppingToken)
                ?? throw new InvalidDataException(
                    $"Eviction scan checkpoint {previousScanId} was not found.");
            var finalizedBeforeResume = previous.FinalizedAtUtc.HasValue;
            await FinalizeEvictionScanAttemptAsync(
                repair,
                previousScanId,
                saveScanMetrics: false,
                stoppingToken: stoppingToken);
            if (finalizedBeforeResume)
            {
                return;
            }
        }

        var scanId = Guid.NewGuid();
        while (scanId == repair.Id)
        {
            scanId = Guid.NewGuid();
        }

        using (var checkpointScope = _serviceProvider.CreateScope())
        {
            var checkpointContext = checkpointScope.ServiceProvider.GetRequiredService<AppDbContext>();
            checkpointContext.EvictionScanCheckpoints.Add(new EvictionScanCheckpoint
            {
                OperationId = scanId,
                StartedAtUtc = DateTime.UtcNow
            });
            await checkpointContext.SaveChangesAsync(stoppingToken);
        }

        var repairOwner = _serviceProvider.GetRequiredService<OperationStateService>();
        await repairOwner.SaveRepairAsync(
            repair.Id,
            current => current.EvictionScanId = scanId,
            stoppingToken);

        var datasourcePath = Path.GetTempFileName();
        var progressPath = Path.GetTempFileName();
        var repairPath = Path.GetTempFileName();
        try
        {
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            var datasourceConfig = _datasourceService.GetDatasources()
                .Where(source => source.Enabled && !string.IsNullOrWhiteSpace(source.CachePath))
                .Select(source => new
                {
                    name = source.Name,
                    cachePath = source.CachePath,
                    isDefault = source == _datasourceService.GetDefaultDatasource(),
                    // A datasource outside the repair may have no key recipe; it is listed, not scanned.
                    keyScheme = DatasourceCapabilityService.GetSchemeWireValue(
                        _capabilityService.GetCapabilities(source))
                })
                .ToArray();
            var document = new CacheRepairDocument
            {
                OperationId = repair.Id,
                Sources = sources.Select(source => new CacheRepairSource
                {
                    Name = source.Datasource,
                    CachePath = source.CacheRoot!,
                    KeyScheme = source.KeyScheme!
                }).ToList(),
                Target = repair.Target
            };
            await File.WriteAllTextAsync(
                datasourcePath,
                JsonSerializer.Serialize(datasourceConfig, jsonOptions),
                stoppingToken);
            await File.WriteAllTextAsync(
                repairPath,
                JsonSerializer.Serialize(document, jsonOptions),
                stoppingToken);

            // Not tied to the card's operation: a force stop of the card must not kill a repair,
            // which always runs to completion.
            var result = await _rustProcessHelper.RunEvictionScanAsync(
                datasourcePath,
                progressPath,
                stoppingToken,
                operationId: null,
                onProgressEvent: null,
                scanId: scanId,
                repairPath: repairPath);
            var scanResult = ParseScanResult(result);

            await FinalizeEvictionScanAttemptAsync(
                repair,
                scanId,
                saveScanMetrics: false,
                stoppingToken: stoppingToken,
                markFinalized: scanResult.Success);

            if (!scanResult.Success)
            {
                if (string.IsNullOrWhiteSpace(scanResult.Error))
                {
                    throw new InvalidDataException(
                        $"Eviction repair scan {scanId} failed without an error.");
                }
                throw new InvalidOperationException(scanResult.Error);
            }
        }
        finally
        {
            await _rustProcessHelper.DeleteTempFileAsync(datasourcePath);
            await _rustProcessHelper.DeleteTempFileAsync(progressPath);
            await _rustProcessHelper.DeleteTempFileAsync(repairPath);
        }
    }

    private async Task FinalizeEvictionScanAttemptAsync(
        OperationRepair repair,
        Guid scanId,
        bool saveScanMetrics,
        CancellationToken stoppingToken,
        bool markFinalized = true)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var checkpoint = await context.EvictionScanCheckpoints
            .SingleOrDefaultAsync(entry => entry.OperationId == scanId, stoppingToken)
            ?? throw new InvalidDataException($"Eviction scan checkpoint {scanId} was not found.");
        if (checkpoint.Processed < 0
            || checkpoint.Evicted < 0
            || checkpoint.UnEvicted < 0
            || checkpoint.Evicted > checkpoint.Processed - checkpoint.UnEvicted)
        {
            throw new InvalidDataException(
                $"Eviction scan checkpoint {scanId} has invalid counters.");
        }

        if (!checkpoint.FinalizedAtUtc.HasValue)
        {
            if (checkpoint.UnEvicted > 0)
            {
                await UnevictCachedGameDetectionsAsync(
                    context,
                    _logger,
                    _cacheDetections,
                    _evictedDetectionPreservationService,
                    stoppingToken);
                await UnevictCachedServiceDetectionsAsync(
                    context,
                    _logger,
                    _cacheDetections,
                    stoppingToken);
            }

            if (checkpoint.Evicted > 0)
            {
                await EvictCachedGameDetectionsAsync(context, _logger, stoppingToken);
                await EvictCachedServiceDetectionsAsync(context, _logger, stoppingToken);
            }

            await _gameCacheDetectionService.RecoverEvictedGamesAsync(stoppingToken);
            await _gameCacheDetectionService.RecoverEvictedServicesAsync(stoppingToken);
            var prefillRowsRemoved = await RemoveProvenPrefillAsync(
                context,
                repair.Target,
                stoppingToken);
            // The prefill picker also hides the badge of an app whose downloads are evicted, so a
            // scan that moved any eviction flag refreshes it.
            if (prefillRowsRemoved > 0 || checkpoint.Evicted > 0 || checkpoint.UnEvicted > 0)
            {
                await _notifications.NotifyAllAsync(SignalREvents.PrefillCacheChanged);
            }

            await scope.ServiceProvider
                .GetRequiredService<CorruptionDetectionService>()
                .InvalidateRepairAsync(repair, stoppingToken);
            await _gameCacheDetectionService.RefreshDiskSummaryAndInvalidateAsync(stoppingToken);
            // A normal eviction scan deletes no cache file, so the Cache Files card keeps its scan.
            if (repair.Type != OperationType.EvictionScan)
            {
                scope.ServiceProvider
                    .GetRequiredService<CacheManagementService>()
                    .InvalidateCachedScan();
            }

            if (repair.Type == OperationType.EvictionScan
                && (checkpoint.Evicted > 0 || checkpoint.UnEvicted > 0))
            {
                await _notifications.NotifyAllAsync(
                    SignalREvents.DownloadsRefresh,
                    new { reason = "eviction-scan-complete" });
            }

            if (markFinalized)
            {
                checkpoint.FinalizedAtUtc = DateTime.UtcNow;
                await context.SaveChangesAsync(stoppingToken);
            }
        }

        if (!saveScanMetrics)
        {
            return;
        }

        if (_evictionScanTerminalStates.TryGetValue(repair.Id, out var terminal))
        {
            terminal.Processed = checkpoint.Processed;
            terminal.Evicted = checkpoint.Evicted;
            terminal.UnEvicted = checkpoint.UnEvicted;
        }

        var repairOwner = _serviceProvider.GetRequiredService<OperationStateService>();
        await repairOwner.SaveRepairAsync(
            repair.Id,
            current =>
            {
                current.EvictionScan = new EvictionScanRepair
                {
                    Processed = checkpoint.Processed,
                    Evicted = checkpoint.Evicted,
                    UnEvicted = checkpoint.UnEvicted,
                    DetectionError = current.EvictionScan?.DetectionError,
                    UncheckedFolders = current.EvictionScan?.UncheckedFolders
                };
            },
            stoppingToken);
    }

    /// <summary>
    /// The database half of a completed cache clear's repair. In one transaction it marks evicted
    /// the finished byte-backed downloads, ended before the clear started, of every cleared source
    /// the full scan will not reach (no scan follows a clean clear without download traffic, and a
    /// source without a launch, a key scheme or a receipt cannot be scanned), and it wipes the
    /// prefill "Cached" badges; then it refreshes the projections built on those rows. A clear that
    /// did not complete leaves the rest to its full scan, but no scan can check a source without a
    /// key scheme, so such a source that the clear finished is evicted here too. A source with no key
    /// scheme is evicted only when its clear kept nothing, whatever the outcome.
    /// </summary>
    public async Task EvictClearedSourcesAsync(
        OperationRepair repair,
        bool skipsCacheScan,
        CancellationToken stoppingToken)
    {
        var completed = repair.Outcome == OperationStatus.Completed;
        // Matched case-insensitively: Downloads.Datasource drifts in case from the configured
        // name (rows stored as 'Default' against a 'default' config were observed live).
        var datasourceNames = repair.Sources
            .Where(source => source.KeyScheme is null
                ? source.NativeCompletionAccepted
                : completed
                    && (skipsCacheScan
                        || !source.NativeLaunchAuthorized
                        || !File.Exists(source.ReceiptPath)))
            .Select(source => source.Datasource.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (!completed && datasourceNames.Count == 0)
        {
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retryPolicy = context.Database.CreateExecutionStrategy();
        var (downloadsEvicted, prefillRowsRemoved) = await retryPolicy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted,
                stoppingToken);
            var evicted = await context.Downloads
                .Where(download =>
                    !download.IsActive
                    && !download.IsEvicted
                    && (download.CacheHitBytes > 0 || download.CacheMissBytes > 0)
                    && download.EndTimeUtc < repair.StartedAt
                    && datasourceNames.Contains(download.Datasource.ToLower()))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(download => download.IsEvicted, true),
                    stoppingToken);

            // The badges record what prefill put on disk and carry no datasource, so a clear
            // falsifies all of them.
            var prefillRemoved = await context.PrefillCachedDepots.ExecuteDeleteAsync(stoppingToken)
                + await context.PrefillCachedApps.ExecuteDeleteAsync(stoppingToken);

            await transaction.CommitAsync(stoppingToken);
            return (evicted, prefillRemoved);
        });

        if (prefillRowsRemoved > 0)
        {
            await _notifications.NotifyAllAsync(SignalREvents.PrefillCacheChanged);
        }

        if (downloadsEvicted == 0)
        {
            return;
        }

        await EvictCachedGameDetectionsAsync(context, _logger, stoppingToken);
        await EvictCachedServiceDetectionsAsync(context, _logger, stoppingToken);
        await _gameCacheDetectionService.RecoverEvictedGamesAsync(stoppingToken);
        await _gameCacheDetectionService.RecoverEvictedServicesAsync(stoppingToken);
        await scope.ServiceProvider
            .GetRequiredService<CorruptionDetectionService>()
            .InvalidateRepairAsync(repair, stoppingToken);
        await _gameCacheDetectionService.RefreshDiskSummaryAndInvalidateAsync(stoppingToken);
    }

    private async Task FinalizeEvictionRemovalAsync(
        OperationRepair repair,
        CancellationToken stoppingToken)
    {
        var metrics = repair.EvictionRemoval
            ?? throw new InvalidDataException($"Eviction removal repair {repair.Id} has no metrics.");
        using var scope = _serviceProvider.CreateScope();

        if (_evictionRemovalTerminalStates.TryGetValue(repair.Id, out var terminal))
        {
            terminal.StageKey = metrics.StageKey;
            terminal.DownloadsRemoved = metrics.DownloadsRemoved;
            terminal.LogEntriesRemoved = metrics.LogEntriesRemoved;
        }

        // A log step that started always finishes, after a crash, a force stop or a failure too.
        if (repair.Sources.Any(OperationStateService.LogStepUnfinished))
        {
            await RunEvictionLogStepAsync(
                scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                repair.Id,
                metrics.Selection,
                repair.Sources,
                stoppingToken);
        }

        if (!repair.DatabaseWriteStarted)
        {
            return;
        }

        await scope.ServiceProvider
            .GetRequiredService<CorruptionDetectionService>()
            .InvalidateRepairAsync(repair, stoppingToken);
        await _gameCacheDetectionService.RefreshDiskSummaryAndInvalidateAsync(stoppingToken);
        scope.ServiceProvider
            .GetRequiredService<CacheManagementService>()
            .InvalidateCachedScan();
    }

    private static async Task<int> RemoveProvenPrefillAsync(
        AppDbContext context,
        CacheRepairTarget? target,
        CancellationToken stoppingToken)
    {
        IQueryable<Download> downloads = context.Downloads;
        if (target != null)
        {
            downloads = target switch
            {
                { SteamAppId: { } appId } => downloads.Where(download =>
                    download.GameAppId == appId && download.EpicAppId == null),
                { EpicGame: { } game } => downloads.Where(download =>
                    download.Service == "epicgames" && download.GameName == game),
                { GameName: { } game, Service: { } service } => downloads.Where(download =>
                    download.GameAppId == null
                    && download.EpicAppId == null
                    && download.Service == service
                    && download.GameName == game),
                { Service: { } service } => downloads.Where(download =>
                    download.GameAppId == null
                    && download.EpicAppId == null
                    && download.Service == service),
                _ => throw new InvalidDataException("Cache repair target has no selector.")
            };

            if (!await downloads.AnyAsync(stoppingToken)
                || await downloads.AnyAsync(download => !download.IsEvicted, stoppingToken))
            {
                return 0;
            }

            var appRows = await PrefillCacheService.MatchingCachedApps(context, downloads)
                .Select(app => app.Id)
                .ToListAsync(stoppingToken);
            var appsRemoved = appRows.Count == 0
                ? 0
                : await context.PrefillCachedApps
                    .Where(app => appRows.Contains(app.Id))
                    .ExecuteDeleteAsync(stoppingToken);
            var depotsRemoved = target.SteamAppId is { } steamAppId
                ? await context.PrefillCachedDepots
                    .Where(depot => depot.AppId == steamAppId)
                    .ExecuteDeleteAsync(stoppingToken)
                : 0;
            return appsRemoved + depotsRemoved;
        }

        var evictedAppRows = await PrefillCacheService.MatchingCachedApps(
                context,
                context.Downloads.Where(download => download.IsEvicted))
            .Select(app => app.Id)
            .ToListAsync(stoppingToken);
        var activeAppRows = await PrefillCacheService.MatchingCachedApps(
                context,
                context.Downloads.Where(download => !download.IsEvicted))
            .Select(app => app.Id)
            .ToListAsync(stoppingToken);
        var provenAppRows = evictedAppRows
            .Except(activeAppRows)
            .ToList();
        var removedApps = provenAppRows.Count == 0
            ? 0
            : await context.PrefillCachedApps
                .Where(app => provenAppRows.Contains(app.Id))
                .ExecuteDeleteAsync(stoppingToken);

        var provenSteamApps = await context.Downloads
            .Where(download => download.Service == "steam"
                && download.GameAppId != null
                && download.GameAppId > 0)
            .GroupBy(download => download.GameAppId!.Value)
            .Where(group => group.Any(download => download.IsEvicted)
                && group.All(download => download.IsEvicted))
            .Select(group => group.Key)
            .ToListAsync(stoppingToken);
        var removedDepots = provenSteamApps.Count == 0
            ? 0
            : await context.PrefillCachedDepots
                .Where(depot => provenSteamApps.Contains(depot.AppId))
                .ExecuteDeleteAsync(stoppingToken);
        return removedApps + removedDepots;
    }

    private static Dictionary<string, object?> BuildScanProgressContext(EvictionScanProgressData progress)
    {
        if (progress.Context != null && progress.Context.Count > 0)
        {
            return new Dictionary<string, object?>(progress.Context);
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
    /// The run's notice decides how the tracker draws it, so the terminal carries no display flags.
    /// </summary>
    private Guid RegisterEvictionScanOperation(string name, CancellationTokenSource cts, RunNotice notice)
    {
        var parked = notice.OperationId is Guid parkedId ? _operationTracker.GetOperation(parkedId) : null;
        var continueQueued = parked?.Status == OperationStatus.Waiting
            && parked.Type == OperationType.EvictionScan
            && parked.CancellationTokenSource == cts;
        var terminalState = new EvictionScanTerminalState();
        Guid operationId = default;
        var scanState = new Dictionary<string, object?>();
        Action finish = () =>
        {
                _evictionScanTerminalStates.TryRemove(operationId, out _);
                lock (_evictionScanTerminalStates)
                {
                    if (_currentScanOperationId == operationId)
                    {
                        _currentScanProgressContext = null;
                        _currentScanOperationId = null;
                    }
                }
            };
        Func<OperationTerminalInfo, Task> emit = terminal =>
            {
                var context = new Dictionary<string, object?>
                {
                    ["totalProcessed"] = terminalState.Processed,
                    ["totalEvicted"] = terminalState.Evicted,
                    ["totalUnEvicted"] = terminalState.UnEvicted
                };
                if (terminalState.DetectionError != null) context["detectionError"] = terminalState.DetectionError;

                if (terminal.Cancelled)
                {
                    return _notifications.NotifyAllAsync(SignalREvents.EvictionScanComplete, new EvictionScanComplete(
                        Success: false,
                        OperationId: operationId,
                        StageKey: "signalr.evictionScan.cancelled",
                        Processed: 0,
                        Evicted: 0,
                        UnEvicted: 0,
                        Context: context,
                        Cancelled: true));
                }

                // Above the success branch: a refused run completes successfully because it did
                // not fail, so without this it would announce itself as a finished scan that
                // found nothing to evict.
                if (terminal.Skipped)
                {
                    return _notifications.NotifyAllAsync(SignalREvents.EvictionScanComplete, new EvictionScanComplete(
                        Success: true,
                        OperationId: operationId,
                        // No stage key: this branch always carries a reason, because the only run
                        // outcome that sets Skipped is the download refusal and it is built with
                        // the gate's own sentence. The reader prefers that sentence to a key, so a
                        // key here could never render and would only oblige every locale to
                        // translate a line nobody sees.
                        StageKey: null,
                        Processed: 0,
                        Evicted: 0,
                        UnEvicted: 0,
                        Error: terminal.Error,
                        Context: context,
                        Skipped: true));
                }

                if (terminal.Success)
                {
                    return _notifications.NotifyAllAsync(SignalREvents.EvictionScanComplete, new EvictionScanComplete(
                        Success: true,
                        OperationId: operationId,
                        StageKey: "signalr.evictionScan.complete",
                        Processed: terminalState.Processed,
                        Evicted: terminalState.Evicted,
                        UnEvicted: terminalState.UnEvicted,
                        Context: context));
                }

                return _notifications.NotifyOperationFailedAsync(SignalREvents.EvictionScanComplete, new EvictionScanComplete(
                    Success: false,
                    OperationId: operationId,
                    StageKey: "signalr.evictionScan.complete",
                    Processed: 0,
                    Evicted: 0,
                    UnEvicted: 0,
                    Error: terminal.Error ?? "Rust eviction scan binary returned failure",
                    Context: context));
            };

        if (continueQueued)
        {
            if (parked == null || !_operationTracker.BeginQueuedOperation(
                    parked.Id,
                    scanState,
                    finish,
                    emit,
                    ownerCompletes: true))
            {
                return Guid.Empty;
            }

            operationId = parked.Id;
        }
        else
        {
            operationId = _operationTracker.RegisterOperation(
                OperationType.EvictionScan,
                name,
                cts,
                scanState,
                finish,
                emit,
                notice: notice,
                ownerCompletes: true);
        }

        _evictionScanTerminalStates[operationId] = terminalState;
        lock (_evictionScanTerminalStates)
        {
            _currentScanOperationId = operationId;
            _currentScanProgressContext = null;
        }
        cts.Token.Register(() => notice.Cancel(_operationTracker, operationId));
        notice.Attach(_operationTracker, operationId);
        return operationId;
    }

    /// <summary>
    /// Phase one of every scan: a full game detection under this scan's operation, so a game whose
    /// files came back since the last detection is re-sized before the scan zeroes what is gone.
    /// The detection runs with its own card hidden (its data events still fire), its percent is
    /// forwarded onto this scan's card, and cancelling the scan cancels it. A refused or failed
    /// detection is logged and the scan goes on, because the scan reports its own gates itself.
    /// A scan promoted from behind a successful full detection reuses that detection when nothing
    /// has invalidated it and no download is writing the cache.
    /// </summary>
    private async Task RunFullDetectionPhaseAsync(
        Guid operationId,
        CancellationToken stoppingToken)
    {
        var blockedBy = _operationTracker.GetOperation(operationId)?.Notice?.BlockedByOperationId;
        if (blockedBy is Guid reusableDetectionId &&
            _gameCacheDetectionService.HasReusableFullDetection(reusableDetectionId))
        {
            _logger.LogInformation(
                "[EvictionScan] Reusing completed full detection {DetectionId}",
                reusableDetectionId);
            return;
        }

        Guid? detectionId;
        try
        {
            var childNotice = new RunNotice(NotificationMode.Hidden, RunTrigger.Manual);
            detectionId = await _gameCacheDetectionService.StartDetectionAsync(
                incremental: false,
                notice: childNotice,
                parentOperationId: operationId);
        }
        catch (ValidationException ex)
        {
            _logger.LogWarning("[EvictionScan] Full detection ahead of the scan declined: {Reason}", ex.Message);
            return;
        }

        if (detectionId == null)
        {
            _logger.LogWarning("[EvictionScan] A game detection is already running, so the scan continues without its own");
            return;
        }

        var finished = new TaskCompletionSource<OperationInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnTerminal(OperationInfo operation)
        {
            if (operation.Id == detectionId.Value)
            {
                finished.TrySetResult(operation);
            }
        }

        _operationTracker.OperationTerminal += OnTerminal;
        // Cancelling the scan cancels the detection it is waiting on. The wait still ends on the
        // detection's own terminal, which keeps the single-owner ordering the tracker expects.
        using var cancelDetection = stoppingToken.Register(() => { _operationTracker.CancelOperation(detectionId.Value); });
        try
        {
            OperationInfo? terminal = null;
            while (!finished.Task.IsCompleted)
            {
                var detection = _operationTracker.GetOperation(detectionId.Value);
                if (detection == null || detection.Status.IsTerminal())
                {
                    terminal = detection;
                    // Finished before the subscription landed, or already gone from the tracker.
                    break;
                }

                await ReportScanProgressAsync(
                    operationId,
                    detection.PercentComplete,
                    "signalr.evictionScan.detectingGames",
                    new EvictionScanResult());

                await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None));
            }

            terminal ??= finished.Task.IsCompletedSuccessfully ? finished.Task.Result : _operationTracker.GetOperation(detectionId.Value);
            if (terminal?.Status == OperationStatus.Failed)
            {
                var metrics = terminal.Metadata as GameDetectionMetrics;
                var detectionError = metrics?.CompletionContext?.GetValueOrDefault("errorDetail")?.ToString()
                    ?? metrics?.Error ?? terminal.Message ?? "signalr.generic.failed";
                _operationTracker.UpdateMetadata(operationId, values =>
                {
                    if (_evictionScanTerminalStates.TryGetValue(operationId, out var terminalState))
                        terminalState.DetectionError = detectionError;
                });
                await ReportScanProgressAsync(operationId, terminal.PercentComplete,
                    "signalr.evictionScan.detectingGames", new EvictionScanResult());
            }

            _logger.LogInformation("[EvictionScan] Full detection ahead of the scan finished (operation: {DetectionId})", detectionId);
        }
        finally
        {
            _operationTracker.OperationTerminal -= OnTerminal;
        }
    }

    private async Task ReportScanProgressAsync(
        Guid operationId,
        double percentComplete,
        string stageKey,
        EvictionScanResult scanResult)
    {
        var context = new Dictionary<string, object?>
        {
            ["totalProcessed"] = scanResult.Processed,
            ["totalEvicted"] = scanResult.Evicted,
            ["totalUnEvicted"] = scanResult.UnEvicted
        };
        if (_evictionScanTerminalStates.TryGetValue(operationId, out var terminalState) && terminalState.DetectionError != null)
            context["detectionError"] = terminalState.DetectionError;

        var accepted = false;
        _operationTracker.UpdateProgress(operationId, percentComplete, stageKey, onProgress: operation =>
        {
            lock (_evictionScanTerminalStates)
            {
                if (_currentScanOperationId == operationId) _currentScanProgressContext = context;
            }
            if (operation.Metadata is Dictionary<string, object?> values) values["context"] = context;
            accepted = true;
        });

        // Same broadcast gate as the rust-tick callback: the post-scan phases (detection updates,
        // disk-summary refresh) can call this per batch. Stage transitions always emit.
        if (!accepted || !ShouldEmitScanProgress(stageKey))
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
    /// closure registered at RegisterOperation time.
    /// </summary>
    private sealed class EvictionScanTerminalState
    {
        public string? DetectionError;
        public int Processed;
        public int Evicted;
        public int UnEvicted;
    }

    /// <summary>
    /// The result of one eviction scan run. <see cref="Skipped"/> separates a run that declined to
    /// start from one that started and failed, so the card carries the reason without turning red.
    /// </summary>
    private sealed record EvictionScanRunOutcome(bool Success, string? Error, bool Skipped = false,
        bool RepairStarted = false,
        int Processed = 0, int Evicted = 0, int UnEvicted = 0);

    /// <summary>
    /// Mutable terminal-metrics holder for an in-flight EvictionRemoval. Populated BY VALUE in
    /// CompleteEvictionRemovalAsync immediately before CompleteOperation; read by the onTerminalEmit
    /// closure registered at RegisterOperation time.
    /// </summary>
    private sealed class EvictionRemovalTerminalState
    {
        public string StageKey = "signalr.evictionRemove.complete";
        public int DownloadsRemoved;
        public int LogEntriesRemoved;
    }

    private sealed record EvictedLogPurgeRunOptions(
        double ProgressStartPercent,
        double ProgressSpanPercent,
        string SuccessDescription,
        string SummaryDescription);

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
            // Terminal cleanup is the sole remover of the terminal-state entry so a universal
            // force-kill (which bypasses CompleteEvictionRemovalAsync) cannot leak it.
            onTerminalCleanup: () => _evictionRemovalTerminalStates.TryRemove(operationId, out _),
            // Terminal EvictionRemovalComplete fires EXACTLY ONCE from inside CompleteOperation.
            onTerminalEmit: BuildTerminalEmit(() => operationId, terminalState),
            ownerCompletes: true);
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
        }, CancellationToken.None);

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
            // Terminal cleanup is the sole remover of the terminal-state entry so a universal
            // force-kill (which bypasses CompleteEvictionRemovalAsync) cannot leak it.
            onTerminalCleanup: () => _evictionRemovalTerminalStates.TryRemove(operationId, out _),
            // Terminal EvictionRemovalComplete fires EXACTLY ONCE from inside CompleteOperation.
            onTerminalEmit: BuildTerminalEmit(() => operationId, terminalState),
            ownerCompletes: true);
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
        }, CancellationToken.None);

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
        var accepted = false;
        context = context == null ? null : new Dictionary<string, object?>(context);
        _operationTracker.UpdateProgress(operationId, percentComplete, stageKey, onProgress: operation =>
        {
            accepted = true;
        });
        if (!accepted) return;

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
        _operationTracker.CompleteOperation(operationId, success, success ? null : error,
            cancelled: cancelled, onCompleting: operation =>
            {
                if (success) operation.PercentComplete = 100;
                if (_evictionRemovalTerminalStates.TryGetValue(operationId, out var removalTerminalState))
                {
                    removalTerminalState.StageKey = stageKey;
                    removalTerminalState.DownloadsRemoved = downloadsRemoved;
                    removalTerminalState.LogEntriesRemoved = logEntriesRemoved;
                }
            });
        return Task.CompletedTask;
    }

    private async Task FinishEvictionRemovalRepairAsync(
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

        await _serviceProvider
            .GetRequiredService<OperationStateService>()
            .FinishRepairAsync(operationId, success, cancelled, error);
    }

    /// <summary>
    /// Registers (or returns the closure for) the EvictionRemoval terminal emit. A mutable
    /// terminal-state holder is created up front, stored in <see cref="_evictionRemovalTerminalStates"/>,
    /// and captured by the returned closure. CompleteEvictionRemovalAsync fills the holder just
    /// before CompleteOperation; force-kill (which bypasses CompleteEvictionRemovalAsync) leaves the
    /// holder at its defaults so the closure still emits a coherent record. The terminal SignalR
    /// event fires EXACTLY ONCE from inside CompleteOperation.
    /// </summary>
    private Func<OperationTerminalInfo, Task> BuildTerminalEmit(
        Func<Guid> operationIdAccessor,
        EvictionRemovalTerminalState terminalState)
    {
        return info =>
        {
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
                        Error: info.Error,
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

    /// <summary>
    /// The eviction removal's log step. Its purge targets, every datasource's purge and its row
    /// deletes run under one Rewrite lock, so no import adds a line or a row for a target between
    /// the target queries and the deletes. The row deletes cover every datasource, so a step that
    /// stopped after any purge started is redone whole by the repair; redoing only the started
    /// datasources would leave log lines for rows the step deletes.
    /// </summary>
    /// <param name="redoSources">
    /// Null when the removal runs the step. The repair passes the sources it may still touch: its
    /// record is Repairing, so it starts no work, and its card has ended, so it reports no progress.
    /// </param>
    private async Task<(int DetectionGames, int DetectionServices, int LogEntries, int Downloads,
        int PrefillDepots, int PrefillApps)> RunEvictionLogStepAsync(
        AppDbContext context,
        Guid operationId,
        EvictionRemovalMetadata selection,
        IReadOnlyCollection<OperationRepairSource>? redoSources,
        CancellationToken stoppingToken)
    {
        var repairOwner = _serviceProvider.GetRequiredService<OperationStateService>();
        var reportProgress = redoSources is null;
        var datasources = _datasourceService.GetDatasources()
            .Where(datasource => !string.IsNullOrWhiteSpace(datasource.LogPath)
                && Directory.Exists(datasource.LogPath)
                && (redoSources is null || redoSources.Any(source => string.Equals(
                    source.Datasource,
                    datasource.Name,
                    StringComparison.OrdinalIgnoreCase))))
            .ToList();
        // A datasource whose log folder is missing keeps the lines of the rows this step deletes, and
        // reading its logs again from the start can bring them back, so the removal's card names it
        // once those rows are gone.
        List<string> logFoldersMissing = _datasourceService.GetDatasources()
            .Where(datasource => !string.IsNullOrWhiteSpace(datasource.LogPath)
                && !Directory.Exists(datasource.LogPath))
            .Select(datasource => datasource.Name)
            .ToList();
        if (redoSources is null)
        {
            // Every start comes before the lock: a start waits for another operation's repair,
            // and that repair may need the lock for its own position reset.
            foreach (var datasource in datasources)
            {
                await repairOwner.StartWorkAsync(operationId, datasource.Name, stoppingToken);
            }
            await repairOwner.StartWorkAsync(
                operationId,
                datasource: null,
                cancellationToken: stoppingToken);
        }

        Task ReportAsync(double percentComplete, string status, string stageKey, int logEntriesRemoved = 0) =>
            reportProgress
                ? ReportRemovalProgressAsync(operationId, percentComplete, status, stageKey, logEntriesRemoved: logEntriesRemoved)
                : Task.CompletedTask;

        int detectionGamesDeleted = 0;
        int detectionServicesDeleted = 0;
        int logEntriesDeleted = 0;
        int downloadsDeleted = 0;
        int prefillDepotsDeleted = 0;
        int prefillAppsDeleted = 0;
        List<string> deletedFrom = [];
        await using (await repairOwner.LockLogFilesAsync(
            operationId,
            OperationType.EvictionRemoval,
            LogFileLockKind.Rewrite,
            stoppingToken))
        {
            List<string> purged;
            var retryPolicy = context.Database.CreateExecutionStrategy();
            if (selection.Scope is null)
            {
                // Rewrite nginx access.log files before deleting LogEntries or Downloads. A later
                // full re-parse would otherwise restore evicted games from URLs that remain on disk.
                // A failed rewrite blocks the database deletion so the two stores stay consistent.
                purged = await PurgeLogEntriesAsync(context, operationId, datasources, reportProgress, stoppingToken);

                // Removal-driven cleanup: the bulk path (Remove mode auto-scan and the controller-
                // driven "Remove All Evicted" button) is an explicit user request to delete evicted
                // entities. Like the per-item path (RemoveEvictedRecordsForEntityAsync), we DELETE
                // the matching CachedGameDetections / CachedServiceDetections rows so the Evicted
                // Items list clears on the frontend's next refetch - no ghost rows with 0 files /
                // 0 B left behind. Order: detection rows → log entries → downloads, all in one
                // transaction.
                await retryPolicy.ExecuteAsync(async () =>
                {
                    detectionGamesDeleted = 0;
                    detectionServicesDeleted = 0;
                    logEntriesDeleted = 0;
                    downloadsDeleted = 0;
                    prefillDepotsDeleted = 0;
                    prefillAppsDeleted = 0;

                    await using var transaction = await context.Database.BeginTransactionAsync(stoppingToken);
                    try
                    {
                        if (context.Database.IsNpgsql())
                        {
                            await context.Database.ExecuteSqlRawAsync(
                                "LOCK TABLE \"Downloads\" IN SHARE ROW EXCLUSIVE MODE",
                                stoppingToken);
                            await context.Database.ExecuteSqlRawAsync(
                                "LOCK TABLE \"LogEntries\" IN SHARE ROW EXCLUSIVE MODE",
                                stoppingToken);
                        }

                        deletedFrom = await context.Downloads
                            .Where(d => d.IsEvicted)
                            .Select(d => d.Datasource)
                            .Distinct()
                            .ToListAsync(stoppingToken);

                        // Step 1: delete evicted detection rows so the frontend list clears.
                        await ReportAsync(
                            40,
                            "removing_detection_rows",
                            "signalr.evictionRemove.removingDetectionRows");

                        detectionGamesDeleted = await context.CachedGameDetections
                            .Where(g => g.IsEvicted)
                            .ExecuteDeleteAsync(stoppingToken);

                        detectionServicesDeleted = await context.CachedServiceDetections
                            .Where(s => s.IsEvicted)
                            .ExecuteDeleteAsync(stoppingToken);

                        // The prefill "Cached" badges are a record of what prefill put on disk for a
                        // Steam app, and these downloads are being deleted precisely because their cache
                        // files are gone. Left behind, the prefill game picker keeps calling those games
                        // cached and the daemon skips them on the next run. Matched through Downloads
                        // because a prefill row carries only a Steam app id.
                        var evictedGameAppIds = await context.Downloads
                            .Where(d => d.IsEvicted && d.Service == "steam" && d.GameAppId != null && d.GameAppId > 0)
                            .Select(d => d.GameAppId!.Value)
                            .Distinct()
                            .ToListAsync(stoppingToken);

                        prefillDepotsDeleted = await context.PrefillCachedDepots
                            .Where(depot => evictedGameAppIds.Contains(depot.AppId))
                            .ExecuteDeleteAsync(stoppingToken);
                        prefillAppsDeleted = await PrefillCacheService.MatchingCachedApps(context,
                            context.Downloads.Where(d => d.IsEvicted)).ExecuteDeleteAsync(stoppingToken);

                        // Step 2: delete LogEntries for evicted downloads (FK constraint).
                        await ReportAsync(
                            60,
                            "removing_log_entries",
                            "signalr.evictionRemove.removingLogs");

                        // One statement over every evicted download's log entries ran past the
                        // command timeout on a production removal, and the retry strategy re-ran the
                        // same statement three times before giving up. A statement per batch of
                        // downloads stays short whatever the table holds.
                        var evictedDownloadIds = await context.Downloads
                            .Where(d => d.IsEvicted)
                            .Select(d => d.Id)
                            .ToListAsync(stoppingToken);
                        foreach (var batch in evictedDownloadIds.Chunk(200))
                        {
                            logEntriesDeleted += await context.LogEntries
                                .Where(le => le.DownloadId != null && batch.Contains(le.DownloadId.Value))
                                .ExecuteDeleteAsync(stoppingToken);
                        }

                        // Step 3: delete evicted Downloads.
                        await ReportAsync(
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
            }
            else
            {
                var scope = Enum.Parse<EvictionScope>(selection.Scope, ignoreCase: true);
                var key = selection.Key
                    ?? throw new InvalidDataException($"Eviction removal {operationId} has no key for its {selection.Scope} selection.");
                // Service names are stored lowercase, so the Service and Named queries compare with
                // plain == (Npgsql cannot translate an ignore-case string.Equals).
                var keyLower = key.ToLowerInvariant();
                var namedGameName = scope == EvictionScope.Named ? selection.GameName : null;

                // Rewrite nginx access.log files before deleting this entity's LogEntries or Downloads.
                // A failed rewrite blocks the database deletion so the two stores stay consistent.
                purged = await PurgeLogEntriesForEntityAsync(
                    context, scope, key, operationId, datasources, reportProgress, stoppingToken, namedGameName);

                await ReportAsync(
                    25,
                    "removing_log_entries",
                    "signalr.evictionRemove.removingLogs");

                // EF Core's NpgsqlRetryingExecutionStrategy forbids user-initiated transactions unless
                // they are wrapped in a strategy-controlled retry block. Without this wrapper any call
                // to BeginTransactionAsync throws InvalidOperationException. Match the pattern used in
                // DownloadCleanupService / DatabaseService / PicsDataService.
                await retryPolicy.ExecuteAsync(async () =>
                {
                    await using var transaction = await context.Database.BeginTransactionAsync(stoppingToken);
                    try
                    {
                        if (context.Database.IsNpgsql())
                        {
                            await context.Database.ExecuteSqlRawAsync(
                                "LOCK TABLE \"Downloads\" IN SHARE ROW EXCLUSIVE MODE",
                                stoppingToken);
                            await context.Database.ExecuteSqlRawAsync(
                                "LOCK TABLE \"LogEntries\" IN SHARE ROW EXCLUSIVE MODE",
                                stoppingToken);
                        }

                        prefillAppsDeleted = 0;
                        prefillDepotsDeleted = 0;
                        logEntriesDeleted = 0;
                        downloadsDeleted = 0;
                        var downloads = context.Downloads.Where(d => d.IsEvicted);
                        downloads = scope switch
                        {
                            EvictionScope.Steam => downloads.Where(d => d.Service == "steam" && d.GameAppId == long.Parse(key)),
                            EvictionScope.Epic => downloads.Where(d => d.Service == "epicgames" && d.EpicAppId == key),
                            EvictionScope.Named => downloads.Where(d => d.GameAppId == null && d.EpicAppId == null
                                && d.Service == keyLower && d.GameName == namedGameName),
                            EvictionScope.Service => downloads.Where(d => d.GameAppId == null && d.EpicAppId == null && d.Service == keyLower),
                            _ => throw new ArgumentOutOfRangeException(nameof(selection))
                        };
                        deletedFrom = await downloads.Select(d => d.Datasource).Distinct().ToListAsync(stoppingToken);
                        prefillAppsDeleted = await PrefillCacheService.MatchingCachedApps(context, downloads)
                            .ExecuteDeleteAsync(stoppingToken);
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

                            _ => throw new ArgumentOutOfRangeException(nameof(selection))
                        };

                        // Step 2: Delete this entity's evicted Downloads.
                        await ReportAsync(
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

                            _ => throw new ArgumentOutOfRangeException(nameof(selection))
                        };

                        // Drop the prefill "Cached" badge rows for this entity. Only the Steam scope can
                        // match one: a prefill row carries a Steam app id and nothing else. Left behind,
                        // the prefill game picker keeps calling the game cached and the daemon skips it
                        // on the next run, though the files that row recorded are gone.
                        prefillDepotsDeleted = scope == EvictionScope.Steam
                            ? await context.PrefillCachedDepots
                                .Where(depot => depot.AppId == long.Parse(key))
                                .ExecuteDeleteAsync(stoppingToken)
                            : 0;

                        await transaction.CommitAsync(stoppingToken);
                    }
                    catch
                    {
                        await transaction.RollbackAsync(stoppingToken);
                        throw;
                    }
                });
            }

            // Marked only after the rows are gone, so a stop before this point redoes the step.
            foreach (var datasource in purged)
            {
                await repairOwner.MarkLogPositionsKeptAsync(operationId, datasource);
            }

            var missingWithRows = logFoldersMissing
                .Where(name => deletedFrom.Contains(name, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (missingWithRows.Count > 0)
            {
                await repairOwner.SetRunWarningAsync(operationId, new RunWarning(
                    "common.notifications.warnings.logFoldersMissing",
                    new Dictionary<string, object?> { ["datasources"] = string.Join(", ", missingWithRows) }));
            }
        }

        return (detectionGamesDeleted, detectionServicesDeleted, logEntriesDeleted, downloadsDeleted,
            prefillDepotsDeleted, prefillAppsDeleted);
    }

    /// <summary>Purges each datasource's logs in turn and returns the datasources it purged.</summary>
    private async Task<List<string>> RunEvictedLogPurgeAsync(
        Guid operationId,
        LogPurgeTargets targets,
        IReadOnlyList<ResolvedDatasource> datasources,
        bool reportProgress,
        CancellationToken stoppingToken,
        EvictedLogPurgeRunOptions options)
    {
        var purged = new List<string>();
        var logLinesKept = new List<string>();
        if (reportProgress)
        {
            await ReportRemovalProgressAsync(
                operationId,
                options.ProgressStartPercent,
                "purging_log_entries",
                "signalr.evictionRemove.purgingLogs",
                context: new Dictionary<string, object?> { ["count"] = targets.Urls.Count + targets.DepotIds.Count });
        }

        var rustBinaryPath = _pathResolver.GetRustLogPurgePath();
        if (!File.Exists(rustBinaryPath))
        {
            _logger.LogWarning(
                "[EvictedLogPurge] cache_purge_log_entries binary not found at {Path} - skipping log rewrite. DB deletes will still proceed.",
                rustBinaryPath);
            return purged;
        }

        long totalLinesRemoved = 0;
        var totalDatasources = Math.Max(1, datasources.Count);
        var dsIndex = 0;
        var repairOwner = _serviceProvider.GetRequiredService<OperationStateService>();
        var runner = new LogPurgeRunner(
            _pathResolver,
            _rustProcessHelper,
            _nginxLogRotationService,
            _stateService,
            repairOwner,
            _logger);

        foreach (var datasource in datasources)
        {
            var dsSliceStart = options.ProgressStartPercent +
                (options.ProgressSpanPercent * dsIndex / totalDatasources);
            var dsSliceSize = options.ProgressSpanPercent / totalDatasources;

            // A cancel seen here or in the purge's checks stores no started flag, so the repair neither
            // resets the positions nor redoes the step.
            stoppingToken.ThrowIfCancellationRequested();
            var report = await runner.RunAsync(
                operationId,
                datasource,
                targets,
                reportProgress
                    ? progress => ReportRemovalProgressAsync(
                        operationId,
                        dsSliceStart + (progress.PercentComplete / 100.0) * dsSliceSize,
                        "purging_log_entries",
                        "signalr.evictionRemove.purgingLogs",
                        context: new Dictionary<string, object?>
                        {
                            ["count"] = targets.Urls.Count + targets.DepotIds.Count,
                            ["datasource"] = datasource.Name
                        })
                    : null,
                stoppingToken);
            totalLinesRemoved += report.LinesRemoved;
            purged.Add(datasource.Name);
            if (report.PermissionErrors > 0)
            {
                logLinesKept.Add(datasource.Name);
            }
            _logger.LogInformation(
                "[EvictedRemoval] {SuccessDescription} removed {Lines} lines from access.log* in datasource '{Datasource}' ({Perms} permission errors)",
                options.SuccessDescription,
                report.LinesRemoved,
                datasource.Name,
                report.PermissionErrors);

            dsIndex++;
        }

        // Lines a permission error kept stay in the log, and reading it again from the start can bring
        // their rows back; the removal's card names the datasources, a repair's redo included (it runs
        // under the same operation).
        if (logLinesKept.Count > 0)
        {
            await repairOwner.SetRunWarningAsync(operationId, new RunWarning(
                "common.notifications.warnings.logLinesKept",
                new Dictionary<string, object?> { ["datasources"] = string.Join(", ", logLinesKept) }));
        }

        _logger.LogInformation(
            "[EvictedRemoval] {SummaryDescription}: {Total} lines removed across {Ok} datasources",
            options.SummaryDescription,
            totalLinesRemoved,
            purged.Count);

        if (totalLinesRemoved > 0)
        {
            // The purge rewrote the log files, so the per-service count cache is stale.
            // InvalidateServiceCountsAsync also broadcasts ServiceCountsChanged so the
            // Log Removal panel refetches live (covers both bulk and per-entity purges).
            var cacheManagementService = _serviceProvider.GetRequiredService<CacheManagementService>();
            await cacheManagementService.InvalidateServiceCountsAsync();

        }

        return purged;
    }

    /// <summary>
    /// Deletes all evicted Download records, their associated LogEntries, and their matching
    /// CachedGameDetections / CachedServiceDetections rows from the database. Called either
    /// from the scan flow (no operationId, mode == Remove) or from the controller's
    /// "Remove All Evicted" button (pre-registered operationId).
    /// <paramref name="notice"/> is the scan's mode and trigger for the scan-driven cleanup, which
    /// registers its own operation with it; the pre-registered path already has its operation.
    /// </summary>
    public async Task RemoveEvictedRecordsAsync(AppDbContext context, CancellationToken stoppingToken, Guid? operationId = null, RunNotice? notice = null)
    {
        // Revalidate immediately before mutation (never trust an earlier check): evicted-record
        // removal purges logs and deletes rows based on recipe-derived eviction evidence.
        var capabilityDenial = _capabilityService.CheckAllCanMapLogicalObjects();
        if (capabilityDenial != null)
        {
            throw new InvalidOperationException(capabilityDenial);
        }

        CancellationTokenSource? cts = null;

        // Deliberately not using TrackedRemovalOperationRunner: this service can start from the background scan path without a controller HTTP lifecycle.
        if (operationId == null)
        {
            // A detection can un-evict a download after this removal reads its targets, which
            // would keep the row and purge its log lines.
            if (_operationTracker.GetActiveOperations(OperationType.GameDetection).Any())
            {
                _logger.LogInformation("[EvictionScan] A game detection is running, so the evicted records are removed by the next scan");
                return;
            }

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
                // Bulk removal - no specific scope/key.
                new EvictionRemovalMetadata(),
                // Terminal cleanup is the sole remover of the terminal-state entry so a universal
                // force-kill cannot leak it.
                onTerminalCleanup: () => _evictionRemovalTerminalStates.TryRemove(selfRegisteredId, out _),
                // Terminal EvictionRemovalComplete fires EXACTLY ONCE from inside CompleteOperation.
                onTerminalEmit: BuildTerminalEmit(() => selfRegisteredId, terminalState),
                notice: notice,
                ownerCompletes: true);
            _evictionRemovalTerminalStates[selfRegisteredId] = terminalState;
            operationId = selfRegisteredId;

            await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalStarted,
                new EvictionRemovalStarted(
                    "signalr.evictionRemove.starting.bulk",
                    operationId.Value));
        }

        // At this point operationId is guaranteed non-null; capture as non-nullable for the rest of the method.
        var opId = operationId.Value;
        var trackedOperation = _operationTracker.GetOperation(opId)
            ?? throw new InvalidOperationException($"Eviction removal {opId} is not tracked.");
        var selection = trackedOperation.Metadata as EvictionRemovalMetadata
            ?? throw new InvalidDataException($"Eviction removal {opId} has no typed selection.");
        var repairOwner = _serviceProvider.GetRequiredService<OperationStateService>();
        var repairPrepared = false;
        try
        {
            await repairOwner.PrepareRepairAsync(
                new OperationRepair
                {
                    Id = opId,
                    Type = OperationType.EvictionRemoval,
                    Name = trackedOperation.Name,
                    StartedAt = trackedOperation.StartedAt,
                    Notice = trackedOperation.Notice,
                    Sources = _datasourceService.GetDatasources()
                        .Select(source => new OperationRepairSource
                        {
                            Datasource = source.Name,
                            LogRoot = source.LogPath,
                            CacheRoot = source.CachePath,
                            KeyScheme = _capabilityService.GetKeySchemeWireValue(source),
                            ResetLogPositions = true,
                            RefreshDownloads = true,
                            RefreshDetection = true,
                            InvalidateCorruption = true
                        })
                        .ToList(),
                    EvictionRemoval = new EvictionRemovalRepair
                    {
                        Selection = selection,
                        StageKey = "signalr.evictionRemove.starting.bulk"
                    }
                },
                stoppingToken);
            repairPrepared = true;

            var (detectionGamesDeleted, detectionServicesDeleted, logEntriesDeleted, downloadsDeleted,
                prefillDepotsDeleted, prefillAppsDeleted) = await RunEvictionLogStepAsync(
                context,
                opId,
                selection,
                redoSources: null,
                stoppingToken);

            await repairOwner.SaveRepairAsync(
                opId,
                repair =>
                {
                    repair.EvictionRemoval = new EvictionRemovalRepair
                    {
                        Selection = selection,
                        StageKey = "signalr.evictionRemove.finalizingRemoval",
                        DownloadsRemoved = downloadsDeleted,
                        LogEntriesRemoved = logEntriesDeleted
                    };
                },
                CancellationToken.None);

            if (downloadsDeleted > 0 || logEntriesDeleted > 0 || detectionGamesDeleted > 0 || detectionServicesDeleted > 0)
            {
                _logger.LogInformation(
                    "[EvictionScan] Remove mode: deleted {Games} game detection rows + {Services} service detection rows, {Downloads} downloads, {LogEntries} log entries, {PrefillDepots} prefill cached-depot rows",
                    detectionGamesDeleted, detectionServicesDeleted, downloadsDeleted, logEntriesDeleted, prefillDepotsDeleted);
            }

            // The prefill game picker holds its "Cached" badges in memory, so an open browser would
            // keep showing the pre-removal ones until it is reloaded.
            if (prefillDepotsDeleted + prefillAppsDeleted > 0)
            {
                await _notifications.NotifyAllAsync(SignalREvents.PrefillCacheChanged);
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

            // Refresh persisted disk-summary totals so dashboard reads reflect post-removal state.
            await _gameCacheDetectionService.RefreshDiskSummaryAndInvalidateAsync(stoppingToken);
            _logger.LogDebug("[EvictedRemoval] Detection cache refreshed after bulk removal");

            // The current corruption scans are snapshots of cache files and access-log evidence
            // this operation just changed, so they may no longer authorize removals. Demote them
            // to view-only history (retained snapshots stay for reference) only after every
            // removal and derived-summary step has succeeded, immediately before publishing the
            // successful terminal state. The single atomic update means failure or cancellation
            // retains the previously authoritative scan.
            await DatabaseService.DemoteCachedCorruptionEvidenceAsync(context, stoppingToken);

            await FinishEvictionRemovalRepairAsync(
                opId,
                success: true,
                cancelled: false,
                error: null);

            await CompleteRemovalAsync(
                opId,
                success: true,
                stageKey: "signalr.evictionRemove.complete",
                downloadsRemoved: downloadsDeleted,
                logEntriesRemoved: logEntriesDeleted);
        }
        catch (OperationCanceledException)
        {
            // A cancel is an expected outcome, not an error.
            _logger.LogInformation("[EvictionScan] Bulk eviction removal cancelled (operation {OpId})", opId);
            if (repairPrepared)
            {
                await FinishEvictionRemovalRepairAsync(
                    opId,
                    success: false,
                    cancelled: true,
                    error: null);
            }
            await CompleteRemovalAsync(
                opId,
                success: false,
                stageKey: "signalr.evictionRemove.cancelled",
                error: null,
                cancelled: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EvictionScan] Error removing evicted records from database");
            if (repairPrepared)
            {
                await FinishEvictionRemovalRepairAsync(
                    opId,
                    success: false,
                    cancelled: false,
                    error: ex.Message);
            }
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
    /// Removes the chosen orphaned download rows. Deleting Downloads rows must not run beside a log
    /// import or another job's log step, so it waits for the log lock; it has no operation of its own.
    /// </summary>
    public async Task<int> RemoveOrphanedDownloadsAsync(
        AppDbContext context,
        IReadOnlyCollection<long> downloadIds,
        CancellationToken cancellationToken)
    {
        await using (await _serviceProvider.GetRequiredService<OperationStateService>().LockLogFilesAsync(
            operationId: null,
            OperationType.DatabaseReset,
            LogFileLockKind.Rows,
            cancellationToken))
        {
            return await OrphanedDownloadRecords.RemoveAsync(context, downloadIds, cancellationToken);
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
    /// Called by <see cref="RunEvictionLogStepAsync"/> BEFORE the DB LogEntries/Downloads deletes
    /// so a future `ResetLogPosition` + full log re-parse cannot resurrect the evicted games.
    ///
    /// A failed Rust rewrite blocks the database deletion because a later full re-parse could
    /// recreate the removed rows.
    /// </summary>
    private async Task<List<string>> PurgeLogEntriesAsync(
        AppDbContext context,
        Guid operationId,
        IReadOnlyList<ResolvedDatasource> datasources,
        bool reportProgress,
        CancellationToken stoppingToken)
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
                return [];
            }

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

            // URLs cover the lines the depot list cannot: downloads with no depot (every non-Steam
            // service) and the partially evicted depots narrowed out above. A Steam line's depot is
            // parsed from its own URL, so the lines of a download whose depot is in the list already
            // match on the depot, and sending their URLs as well only multiplies the payload: one
            // bulk removal shipped 3.7 M URLs beside 337 depots and the purge was killed for memory.
            var urls = await context.LogEntries
                .Where(le => le.DownloadId != null && evictedDownloadIds.Contains(le.DownloadId.Value))
                .Where(le => le.Download!.DepotId == null || !depotIds.Contains(le.Download.DepotId.Value))
                .Select(le => le.Url)
                .Where(u => u != null && u != string.Empty)
                .Distinct()
                .ToListAsync(stoppingToken);

            if (urls.Count == 0 && depotIds.Count == 0)
            {
                _logger.LogInformation(
                    "[EvictedLogPurge] {Count} evicted downloads have no URL/depot history - nothing to purge from logs",
                    evictedDownloadIds.Count);
                return [];
            }
            return await RunEvictedLogPurgeAsync(
                operationId,
                new LogPurgeTargets(urls, depotIds, Service: null),
                datasources,
                reportProgress,
                stoppingToken,
                new EvictedLogPurgeRunOptions(
                    0,
                    30,
                    "Log purge",
                    "Log purge summary"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Bulk evicted-log purge failed", ex);
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
            // All three arms treat zero-byte Downloads as neutral: they never proved cache
            // content existed and are never flagged evicted, so they must not veto the
            // all-evicted test. At least one genuinely evicted row is still required so an
            // entity with ONLY zero-byte rows can never flip evicted.
            //
            // App 0 Downloads are skipped: 0 is the named-game sentinel on CachedGameDetection, and
            // the update below matches every row holding an id from this list with EpicAppId null.
            // Letting 0 through would flag every named (Blizzard/Riot/Xbox) row evicted whenever the
            // unrelated App 0 Downloads happened to be all evicted.
            var steamGamesToEvict = await context.Downloads
                .Where(d => d.GameAppId != null
                         && d.GameAppId > 0
                         && unevictedSteamGameIds.Contains(d.GameAppId.Value))
                .GroupBy(d => d.GameAppId!.Value)
                .Where(g => g.Any(d => d.IsEvicted)
                         && g.All(d => d.IsEvicted || (d.CacheHitBytes == 0 && d.CacheMissBytes == 0)))
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
                .Where(g => g.Any(d => d.IsEvicted)
                         && g.All(d => d.IsEvicted || (d.CacheHitBytes == 0 && d.CacheMissBytes == 0)))
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
                .Where(g => g.Any(d => d.IsEvicted)
                         && g.All(d => d.IsEvicted || (d.CacheHitBytes == 0 && d.CacheMissBytes == 0)))
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
    /// Inverse of <see cref="UnevictCachedServiceDetectionsAsync"/>: marks a service evicted when
    /// its byte-backed service Downloads are all evicted, zeroing the snapshot columns the way the
    /// full detection does. It reads exactly the Download set
    /// <see cref="GameCacheDetectionDataService.GetServicesToUnevictAsync"/> reads (named-game rows
    /// included, zero-byte rows ignored); with different sets a service would flip on every scan.
    /// </summary>
    public static async Task<int> EvictCachedServiceDetectionsAsync(
        AppDbContext context,
        ILogger logger,
        CancellationToken ct)
    {
        var unevictedServiceNames = await context.CachedServiceDetections
            .Where(s => !s.IsEvicted)
            .Select(s => s.ServiceName.ToLower())
            .Distinct()
            .ToListAsync(ct);

        if (unevictedServiceNames.Count == 0)
        {
            return 0;
        }

        var servicesToEvict = await context.Downloads
            .Where(d => d.GameAppId == null
                     && d.EpicAppId == null
                     && d.Service != null
                     && (d.CacheHitBytes > 0 || d.CacheMissBytes > 0)
                     && unevictedServiceNames.Contains(d.Service!.ToLower()))
            .GroupBy(d => d.Service!.ToLower())
            .Where(g => g.All(d => d.IsEvicted))
            .Select(g => g.Key)
            .ToListAsync(ct);

        if (servicesToEvict.Count == 0)
        {
            return 0;
        }

        var updated = await context.CachedServiceDetections
            .Where(s => !s.IsEvicted && servicesToEvict.Contains(s.ServiceName.ToLower()))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsEvicted, true)
                .SetProperty(x => x.CacheFilesFound, 0)
                .SetProperty(x => x.TotalSizeBytes, 0UL), ct);

        logger.LogInformation(
            "[ServiceDetection] Marked {Count} services as evicted - all Downloads now evicted",
            updated);

        return updated;
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
        // Revalidate immediately before mutation, same as the bulk path.
        var capabilityDenial = _capabilityService.CheckAllCanMapLogicalObjects();
        if (capabilityDenial != null)
        {
            throw new InvalidOperationException(capabilityDenial);
        }

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
            // Self-start fallback: the sole caller (StartScopedEvictionRemovalAsync) always passes a
            // pre-registered operationId, so this registers with no notice and draws a full card. It
            // registers an onTerminalEmit (so the terminal EvictionRemovalComplete fires exactly once
            // from inside CompleteOperation) and a cleanup lambda that removes the terminal-state holder.
            cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var terminalState = new EvictionRemovalTerminalState();
            Guid selfRegisteredId = default;
            selfRegisteredId = _operationTracker.RegisterOperation(
                OperationType.EvictionRemoval,
                $"Eviction Removal ({scope}: {key})",
                cts,
                new EvictionRemovalMetadata
                {
                    Scope = scope.ToString().ToLowerInvariant(),
                    Key = key,
                    GameName = namedGameName
                },
                onTerminalCleanup: () => _evictionRemovalTerminalStates.TryRemove(selfRegisteredId, out _),
                // Terminal EvictionRemovalComplete fires EXACTLY ONCE from inside CompleteOperation.
                onTerminalEmit: BuildTerminalEmit(() => selfRegisteredId, terminalState),
                ownerCompletes: true);
            _evictionRemovalTerminalStates[selfRegisteredId] = terminalState;
            operationId = selfRegisteredId;

            await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalStarted,
                new EvictionRemovalStarted("signalr.evictionRemove.starting.entity", operationId.Value,
                    new Dictionary<string, object?> { ["scope"] = scope.ToString(), ["key"] = key },
                    GameName: scope == EvictionScope.Named ? namedGameName : null,
                    EpicAppId: scope == EvictionScope.Epic ? key : null));
        }

        var opId = operationId.Value;
        var trackedOperation = _operationTracker.GetOperation(opId)
            ?? throw new InvalidOperationException($"Eviction removal {opId} is not tracked.");
        var selection = trackedOperation.Metadata as EvictionRemovalMetadata
            ?? throw new InvalidDataException($"Eviction removal {opId} has no typed selection.");
        var repairOwner = _serviceProvider.GetRequiredService<OperationStateService>();
        var repairPrepared = false;
        try
        {
            await repairOwner.PrepareRepairAsync(
                new OperationRepair
                {
                    Id = opId,
                    Type = OperationType.EvictionRemoval,
                    Name = trackedOperation.Name,
                    StartedAt = trackedOperation.StartedAt,
                    Notice = trackedOperation.Notice,
                    Sources = _datasourceService.GetDatasources()
                        .Select(source => new OperationRepairSource
                        {
                            Datasource = source.Name,
                            LogRoot = source.LogPath,
                            CacheRoot = source.CachePath,
                            KeyScheme = _capabilityService.GetKeySchemeWireValue(source),
                            ResetLogPositions = true,
                            RefreshDownloads = true,
                            RefreshDetection = true,
                            InvalidateCorruption = true
                        })
                        .ToList(),
                    EvictionRemoval = new EvictionRemovalRepair
                    {
                        Selection = selection,
                        StageKey = "signalr.evictionRemove.starting.entity"
                    }
                },
                stoppingToken);
            repairPrepared = true;

            var (_, _, logEntriesDeleted, downloadsDeleted, prefillDepotsDeleted, prefillAppsDeleted) =
                await RunEvictionLogStepAsync(
                    context,
                    opId,
                    selection,
                    redoSources: null,
                    stoppingToken);

            await repairOwner.SaveRepairAsync(
                opId,
                repair =>
                {
                    repair.EvictionRemoval = new EvictionRemovalRepair
                    {
                        Selection = selection,
                        StageKey = "signalr.evictionRemove.finalizingRemoval",
                        DownloadsRemoved = downloadsDeleted,
                        LogEntriesRemoved = logEntriesDeleted
                    };
                },
                CancellationToken.None);

            if (downloadsDeleted > 0 || logEntriesDeleted > 0)
            {
                _logger.LogInformation(
                    "[EvictionScan] Entity removal ({Scope} '{Key}'): deleted {Downloads} evicted downloads, {LogEntries} associated log entries and {PrefillDepots} prefill cached-depot rows",
                    scope, key, downloadsDeleted, logEntriesDeleted, prefillDepotsDeleted);
            }

            // The prefill game picker holds its "Cached" badges in memory, so an open browser would
            // keep showing the pre-removal one until it is reloaded.
            if (prefillDepotsDeleted + prefillAppsDeleted > 0)
            {
                await _notifications.NotifyAllAsync(SignalREvents.PrefillCacheChanged);
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
            unevictedRows = await UnevictCachedServiceDetectionsAsync(context, _logger, _cacheDetections, stoppingToken);
            }
            else
            {
                unevictedRows = await UnevictCachedGameDetectionsAsync(
                    context,
                    _logger,
                _cacheDetections,
                    _evictedDetectionPreservationService,
                    stoppingToken);
            }

            var detectionService = _serviceProvider.GetService<GameCacheDetectionService>();
            if (detectionService != null)
            {
                // The disk-summary re-aggregation reads the sizes of the ACTIVE detection rows and
                // nothing from evicted ones, so deleting an evicted entity's rows cannot change
                // its result. It is needed only when this removal flipped rows back to active
                // (partial eviction clears IsEvicted, or the self-heal un-evicted entities whose
                // files reappeared). Otherwise invalidating the in-memory detection cache is
                // enough for the frontend refetch to see the deleted rows.
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

                    await detectionService.RefreshDiskSummaryAndInvalidateAsync(stoppingToken);
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

            // The targeted cache/log mutation stales the current corruption scans, so demote them
            // to view-only history; retained snapshots survive as reference. This is deliberately
            // the last fallible step before success is published; the atomic update retains the
            // prior current scans if it fails or is cancelled.
            await DatabaseService.DemoteCachedCorruptionEvidenceAsync(context, stoppingToken);

            await FinishEvictionRemovalRepairAsync(
                opId,
                success: true,
                cancelled: false,
                error: null);

            await CompleteRemovalAsync(
                opId,
                success: true,
                stageKey: "signalr.evictionRemove.complete",
                downloadsRemoved: downloadsDeleted,
                logEntriesRemoved: logEntriesDeleted);
        }
        catch (OperationCanceledException)
        {
            // A cancel is an expected outcome, not an error.
            _logger.LogInformation("[EvictionScan] Eviction removal for {Scope} '{Key}' cancelled (operation {OpId})",
                scope, key, opId);
            if (repairPrepared)
            {
                await FinishEvictionRemovalRepairAsync(
                    opId,
                    success: false,
                    cancelled: true,
                    error: null);
            }
            await CompleteRemovalAsync(
                opId,
                success: false,
                stageKey: "signalr.evictionRemove.cancelled",
                error: null,
                cancelled: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EvictionScan] Error removing evicted records for {Scope} '{Key}'", scope, key);
            if (repairPrepared)
            {
                await FinishEvictionRemovalRepairAsync(
                    opId,
                    success: false,
                    cancelled: false,
                    error: ex.Message);
            }
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
    /// A failed rewrite blocks the database deletion so a later full re-parse cannot restore rows.
    /// </summary>
    private async Task<List<string>> PurgeLogEntriesForEntityAsync(
        AppDbContext context,
        EvictionScope scope,
        string key,
        Guid operationId,
        IReadOnlyList<ResolvedDatasource> datasources,
        bool reportProgress,
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
                return [];
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
                return [];
            }
            return await RunEvictedLogPurgeAsync(
                operationId,
                new LogPurgeTargets(urls, depotIds, Service: null),
                datasources,
                reportProgress,
                stoppingToken,
                new EvictedLogPurgeRunOptions(
                    10,
                    15,
                    "Entity log purge",
                    $"Entity log purge summary ({scope} '{key}')"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Evicted-log purge failed for {scope} '{key}'",
                ex);
        }
    }
}
