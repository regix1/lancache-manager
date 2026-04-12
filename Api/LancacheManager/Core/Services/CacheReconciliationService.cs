using System.Diagnostics;
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
    private readonly GameCacheDetectionService _gameCacheDetectionService;
    private bool _isRunning;
    private bool _currentScanIsSilent = true;
    private readonly TaskCompletionSource<bool> _firstStartupScanComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override string ServiceName => "CacheReconciliationService";
    protected override TimeSpan Interval => TimeSpan.FromHours(6);
    public override bool DefaultRunOnStartup => true;

    public override string ServiceKey => "cacheReconciliation";

    public bool IsRunning => _isRunning;
    public bool CurrentScanIsSilent => _currentScanIsSilent;

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
    public string? RunManualAsync()
    {
        if (_isRunning) return null;

        var cts = new CancellationTokenSource();
        var operationId = _operationTracker.RegisterOperation(
            OperationType.EvictionScan,
            "Eviction Scan",
            cts);

        _ = Task.Run(async () =>
        {
            _isRunning = true;
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await ReconcileCacheFilesAsync(context, operationId, cts.Token, silent: false);
            }
            finally
            {
                _isRunning = false;
            }
        }, cts.Token);

        return operationId;
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
        GameCacheDetectionService gameCacheDetectionService)
        : base(serviceProvider, logger, configuration)
    {
        _datasourceService = datasourceService;
        _stateService = stateService;
        _notifications = notifications;
        _operationTracker = operationTracker;
        _rustProcessHelper = rustProcessHelper;
        _pathResolver = pathResolver;
        _gameCacheDetectionService = gameCacheDetectionService;

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

        var silent = !_stateService.GetEvictionScanNotifications();

        _isRunning = true;
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
            var operationId = _operationTracker.RegisterOperation(
                OperationType.EvictionScan,
                "Eviction Scan (Startup)",
                cts);

            await ReconcileCacheFilesAsync(context, operationId, linked.Token, silent);
        }
        finally
        {
            _isRunning = false;
            // Signal GameDetectionService that the first startup scan (and any removal cleanup) is done.
            // TrySetResult is safe to call multiple times — only the first call has effect.
            _firstStartupScanComplete.TrySetResult(true);
        }
    }

    protected override async Task ExecuteScopedWorkAsync(
        IServiceProvider scopedServices,
        CancellationToken stoppingToken)
    {
        var silent = !_stateService.GetEvictionScanNotifications();
        var context = scopedServices.GetRequiredService<AppDbContext>();

        // Skip scan if there are no downloads in the database
        if (!await context.Downloads.AnyAsync(stoppingToken))
        {
            _logger.LogDebug("[EvictionScan] No downloads in database, skipping scheduled scan");
            return;
        }

        var cts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, cts.Token);
        var operationId = _operationTracker.RegisterOperation(
            OperationType.EvictionScan,
            "Eviction Scan",
            cts);

        await ReconcileCacheFilesAsync(context, operationId, linked.Token, silent);
    }

    private async Task ReconcileCacheFilesAsync(AppDbContext context, string operationId, CancellationToken stoppingToken, bool silent = false)
    {
        _currentScanIsSilent = silent;
        string? datasourceConfigPath = null;
        string? progressFilePath = null;

        try
        {
            _logger.LogInformation("[EvictionScan] Starting eviction scan via Rust binary (silent: {Silent})", silent);

            if (!silent)
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

            // Start progress monitoring task (only if not silent)
            CancellationTokenSource? progressCts = null;
            Task? progressTask = null;
            if (!silent)
            {
                progressCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                progressTask = _rustProcessHelper.MonitorProgressFileAsync<EvictionScanProgressData>(
                    progressFilePath,
                    async (progress) =>
                    {
                        _operationTracker.UpdateProgress(operationId, progress.PercentComplete, progress.Message);
                        await _notifications.NotifyAllAsync(SignalREvents.EvictionScanProgress, new EvictionScanProgress(
                            OperationId: operationId,
                            Status: progress.Status,
                            StageKey: "signalr.evictionScan.progress",
                            PercentComplete: progress.PercentComplete,
                            Processed: progress.Processed,
                            TotalEstimate: progress.TotalEstimate,
                            Evicted: progress.Evicted,
                            UnEvicted: progress.UnEvicted,
                            Context: new Dictionary<string, object?> { ["totalProcessed"] = progress.Processed, ["totalEstimate"] = progress.TotalEstimate }));
                    },
                    progressCts.Token);
            }

            // Execute the Rust binary
            var result = await _rustProcessHelper.RunEvictionScanAsync(
                datasourceConfigPath, progressFilePath, stoppingToken);

            // Stop progress monitoring
            if (progressCts != null)
            {
                await progressCts.CancelAsync();
                if (progressTask != null)
                {
                    try { await progressTask; } catch (OperationCanceledException) { }
                }
                progressCts.Dispose();
            }

            // Parse result
            var scanResult = ParseScanResult(result);

            if (scanResult.Success)
            {
                _logger.LogInformation(
                    "[EvictionScan] Scan complete: processed {Total} downloads, {Evicted} newly evicted, {UnEvicted} un-evicted (re-cached)",
                    scanResult.Processed, scanResult.Evicted, scanResult.UnEvicted);

                // Fix 3 Trigger #1: whenever the eviction scan flipped any Downloads rows from
                // IsEvicted=true → IsEvicted=false (cache files reappeared), reverse-reconcile
                // the dependent CachedGameDetections.IsEvicted flag so detection UI reflects
                // the re-cached state without waiting for a new full detection scan.
                if (scanResult.UnEvicted > 0)
                {
                    try
                    {
                        var unevictedCount = await UnevictCachedGameDetectionsAsync(context, _logger, stoppingToken);
                        if (unevictedCount > 0)
                        {
                            _logger.LogInformation(
                                "[GameDetection] Self-healed {Count} CachedGameDetection rows after eviction scan reported {UnEvicted} un-evicted downloads",
                                unevictedCount, scanResult.UnEvicted);
                        }
                    }
                    catch (Exception selfHealEx) when (selfHealEx is not OperationCanceledException)
                    {
                        _logger.LogWarning(selfHealEx,
                            "[GameDetection] Reverse-reconcile of CachedGameDetections failed — will retry next scan");
                    }
                }

                // Fix A + B: Propagate eviction to CachedGameDetection and bust the in-memory cache
                if (scanResult.Evicted > 0)
                {
                    try
                    {
                        var evictedCount = await EvictCachedGameDetectionsAsync(context, _logger, stoppingToken);
                        _logger.LogInformation("Marked {Count} CachedGameDetection rows as evicted", evictedCount);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to propagate eviction to CachedGameDetection rows");
                    }
                    _gameCacheDetectionService.InvalidateDetectionCache();
                }

                // Handle evicted data "remove" mode — only run if there are evicted records
                var evictedDataMode = _stateService.GetEvictedDataMode();
                if (evictedDataMode == EvictedDataModes.Remove
                    && await context.Downloads.AnyAsync(d => d.IsEvicted, stoppingToken))
                {
                    await RemoveEvictedRecordsAsync(context, stoppingToken);
                }

                _operationTracker.CompleteOperation(operationId, success: true);
                if (!silent)
                {
                    await _notifications.NotifyAllAsync(SignalREvents.EvictionScanComplete, new EvictionScanComplete(
                        Success: true,
                        OperationId: operationId,
                        StageKey: "signalr.evictionScan.complete",
                        Processed: scanResult.Processed,
                        Evicted: scanResult.Evicted,
                        UnEvicted: scanResult.UnEvicted,
                        Context: new Dictionary<string, object?> { ["totalProcessed"] = scanResult.Processed, ["totalEvicted"] = scanResult.Evicted, ["totalUnEvicted"] = scanResult.UnEvicted }));
                }

                // Notify clients to refresh if eviction flags changed
                if (scanResult.Evicted > 0 || scanResult.UnEvicted > 0)
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
                _operationTracker.CompleteOperation(operationId, success: false, error: errorMsg);
                if (!silent)
                {
                    await _notifications.NotifyAllAsync(SignalREvents.EvictionScanComplete, new EvictionScanComplete(
                        Success: false,
                        OperationId: operationId,
                        StageKey: "signalr.evictionScan.complete",
                        Processed: 0,
                        Evicted: 0,
                        UnEvicted: 0,
                        Error: errorMsg));
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[EvictionScan] Operation {OperationId} was cancelled", operationId);
            _operationTracker.CompleteOperation(operationId, success: false, error: "Cancelled by user");
            if (!silent)
            {
                await _notifications.NotifyAllAsync(SignalREvents.EvictionScanComplete, new EvictionScanComplete(
                    Success: false,
                    OperationId: operationId,
                    StageKey: "signalr.evictionScan.complete",
                    Processed: 0,
                    Evicted: 0,
                    UnEvicted: 0,
                    Error: "Cancelled by user"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EvictionScan] Error during eviction scan");
            _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);
            if (!silent)
            {
                await _notifications.NotifyAllAsync(SignalREvents.EvictionScanComplete, new EvictionScanComplete(
                    Success: false,
                    OperationId: operationId,
                    StageKey: "signalr.evictionScan.complete",
                    Processed: 0,
                    Evicted: 0,
                    UnEvicted: 0,
                    Error: ex.Message));
            }
        }
        finally
        {
            // Clean up temp files
            if (datasourceConfigPath != null)
                await _rustProcessHelper.DeleteTemporaryFileAsync(datasourceConfigPath);
            if (progressFilePath != null)
                await _rustProcessHelper.DeleteTemporaryFileAsync(progressFilePath);
        }
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
    /// Deletes all evicted Download records and their associated LogEntries from the database.
    /// Called when evicted data mode is set to "remove", either from the scan flow (no operationId)
    /// or from the controller with a pre-registered operationId.
    /// When operationId is null, a new operation is registered and Started is emitted internally.
    /// In both cases Progress and Complete events are always emitted.
    /// </summary>
    public async Task RemoveEvictedRecordsAsync(AppDbContext context, CancellationToken stoppingToken, string? operationId = null)
    {
        CancellationTokenSource? cts = null;

        if (operationId == null)
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            operationId = _operationTracker.RegisterOperation(
                OperationType.EvictionRemoval,
                "Eviction Removal",
                cts);

            await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalStarted,
                new EvictionRemovalStarted("signalr.evictionRemove.starting.bulk", operationId));
        }

        try
        {
            // Step -1 (Fix 2): Rewrite nginx access.log files to drop entries for every evicted game
            // BEFORE deleting LogEntries/Downloads from the database. If we skipped this, a future
            // `ResetLogPosition` + full log re-parse would resurrect the evicted games because their
            // URLs still exist in the on-disk access.log. The rewrite is a best-effort optimization:
            // if the Rust binary fails we log a WARNING and continue — correctness of the DB delete
            // is preserved either way.
            await PurgeEvictedLogEntriesAsync(context, operationId, stoppingToken);

            // Step 0: Upsert CachedGameDetection rows for every evicted game BEFORE deleting Downloads.
            // This ensures evicted games remain visible after Downloads rows are deleted.
            // Progress range: 30-60% (log purge was 0-30%)
            _operationTracker.UpdateProgress(operationId, 30, "signalr.evictionRemove.preserving");
            await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalProgress,
                new EvictionRemovalProgress(operationId, "preserving_evicted_games", "signalr.evictionRemove.preserving", 30, 0, 0));

            // Load all evicted Downloads grouped by (GameAppId, EpicAppId) — one representative row per game
            var evictedDownloads = await context.Downloads
                .Where(d => d.IsEvicted && (d.GameAppId != null || d.EpicAppId != null))
                .ToListAsync(stoppingToken);

            var evictedGroups = evictedDownloads
                .GroupBy(d => new { d.GameAppId, d.EpicAppId })
                .Select(g => g.First())
                .ToList();

            int upsertedCount = 0;
            int logEntriesDeleted = 0;
            int downloadsDeleted = 0;
            int totalGroups = evictedGroups.Count;
            int processedGroups = 0;

            // EF Core's NpgsqlRetryingExecutionStrategy forbids user-initiated transactions unless
            // they are wrapped in a strategy-controlled retry block. Without this wrapper any call
            // to BeginTransactionAsync throws InvalidOperationException. Match the pattern used in
            // DownloadCleanupService / DatabaseService / PicsDataService.
            var strategy = context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                // Reset counters on retry so they reflect only the successful attempt.
                upsertedCount = 0;
                logEntriesDeleted = 0;
                downloadsDeleted = 0;
                processedGroups = 0;

                await using var transaction = await context.Database.BeginTransactionAsync(stoppingToken);
                try
                {
                    // Upsert one CachedGameDetection row per evicted game
                    // Throttle progress reports to ~20 updates (every 5%) to avoid flooding SignalR
                    int reportEvery = Math.Max(1, totalGroups / 20);
                    foreach (var representative in evictedGroups)
                    {
                        // Find existing row by GameAppId (Steam) or EpicAppId (Epic)
                        CachedGameDetection? existing = null;
                        if (representative.EpicAppId != null)
                        {
                            existing = await context.CachedGameDetections
                                .FirstOrDefaultAsync(c => c.EpicAppId == representative.EpicAppId, stoppingToken);
                        }
                        else if (representative.GameAppId != null)
                        {
                            existing = await context.CachedGameDetections
                                .FirstOrDefaultAsync(c => c.GameAppId == representative.GameAppId.Value, stoppingToken);
                        }

                        if (existing != null)
                        {
                            // Update existing row — mark as evicted with zero files
                            existing.IsEvicted = true;
                            existing.CacheFilesFound = 0;
                            existing.TotalSizeBytes = 0;
                            if (!string.IsNullOrEmpty(representative.GameName))
                                existing.GameName = representative.GameName;
                            if (representative.Service != null)
                                existing.Service = representative.Service;
                            existing.LastDetectedUtc = DateTime.UtcNow;
                        }
                        else
                        {
                            // Insert new row so the evicted game survives after Downloads are deleted
                            var newDetection = new CachedGameDetection
                            {
                                GameAppId = representative.GameAppId ?? 0,
                                GameName = representative.GameName ?? string.Empty,
                                EpicAppId = representative.EpicAppId,
                                Service = representative.Service,
                                CacheFilesFound = 0,
                                TotalSizeBytes = 0,
                                IsEvicted = true,
                                DatasourcesJson = $"[\"{representative.Datasource}\"]",
                                LastDetectedUtc = DateTime.UtcNow,
                                CreatedAtUtc = DateTime.UtcNow,
                            };
                            context.CachedGameDetections.Add(newDetection);
                        }

                        upsertedCount++;
                        processedGroups++;

                        // Report every ~5% of groups, and always on the final item
                        if (totalGroups > 0 && (processedGroups % reportEvery == 0 || processedGroups == totalGroups))
                        {
                            // Map upsert progress into 30-60% range
                            var progressPercent = (int)(30.0 + 30.0 * processedGroups / totalGroups);
                            _operationTracker.UpdateProgress(operationId, progressPercent, "signalr.evictionRemove.updatingEvictionStatus");
                            await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalProgress,
                                new EvictionRemovalProgress(operationId, "updating_eviction_status", "signalr.evictionRemove.updatingEvictionStatus", progressPercent, 0, 0));
                        }
                    }

                    await context.SaveChangesAsync(stoppingToken);

                    if (upsertedCount > 0)
                    {
                        _logger.LogInformation(
                            "[EvictionScan] Upserted {Count} CachedGameDetection rows for evicted games before removal",
                            upsertedCount);
                    }

                    // Step 1: Delete LogEntries for evicted downloads first (foreign key constraint)
                    // Progress: 65% (log purge 0-30%, upserts 30-60%, DB deletes 65-90%)
                    _operationTracker.UpdateProgress(operationId, 65, "signalr.evictionRemove.removingLogs");
                    await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalProgress,
                        new EvictionRemovalProgress(operationId, "removing_log_entries", "signalr.evictionRemove.removingLogs", 65, 0, 0));

                    logEntriesDeleted = await context.LogEntries
                        .Where(le => le.DownloadId != null && le.Download != null && le.Download.IsEvicted)
                        .ExecuteDeleteAsync(stoppingToken);

                    // Step 2: Delete evicted Downloads
                    // Progress: 80%
                    _operationTracker.UpdateProgress(operationId, 80, "signalr.evictionRemove.removingDownloads");
                    await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalProgress,
                        new EvictionRemovalProgress(operationId, "removing_downloads", "signalr.evictionRemove.removingDownloads", 80, 0, logEntriesDeleted));

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

            if (downloadsDeleted > 0 || logEntriesDeleted > 0)
            {
                _logger.LogInformation(
                    "[EvictionScan] Remove mode: deleted {Downloads} evicted downloads and {LogEntries} associated log entries",
                    downloadsDeleted, logEntriesDeleted);
            }

            // Invalidate the detection cache so the frontend refetch gets fresh data
            _gameCacheDetectionService.InvalidateDetectionCache();
            _logger.LogDebug("[EvictedRemoval] Detection cache invalidated");

            _operationTracker.UpdateProgress(operationId, 100, "signalr.evictionRemove.complete");
            _operationTracker.CompleteOperation(operationId, success: true);
            await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalComplete,
                new EvictionRemovalComplete(true, operationId, "signalr.evictionRemove.complete", downloadsDeleted, logEntriesDeleted));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EvictionScan] Error removing evicted records from database");
            _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);
            await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalComplete,
                new EvictionRemovalComplete(false, operationId, "signalr.evictionRemove.failed", 0, 0, ex.Message));
        }
        finally
        {
            cts?.Dispose();
        }
    }


    /// <summary>
    /// Fix 2: Rewrites nginx access.log files to drop all entries belonging to evicted games,
    /// using the `cache_purge_log_entries` Rust binary. Runs once per configured datasource.
    ///
    /// Called by <see cref="RemoveEvictedRecordsAsync"/> BEFORE the DB LogEntries/Downloads deletes
    /// so a future `ResetLogPosition` + full log re-parse cannot resurrect the evicted games.
    ///
    /// Best-effort: if the Rust binary fails we log a warning but still allow the DB deletes to
    /// proceed — the only loss is that a later full re-parse could re-create the rows.
    /// </summary>
    private async Task PurgeEvictedLogEntriesAsync(AppDbContext context, string operationId, CancellationToken stoppingToken)
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
                _logger.LogDebug("[EvictedLogPurge] No evicted downloads — skipping log rewrite");
                return;
            }

            // Collect distinct URLs from LogEntries that belong to evicted downloads.
            var urls = await context.LogEntries
                .Where(le => le.DownloadId != null && evictedDownloadIds.Contains(le.DownloadId.Value))
                .Select(le => le.Url)
                .Where(u => u != null && u != string.Empty)
                .Distinct()
                .ToListAsync(stoppingToken);

            // Collect distinct depot IDs directly from the Downloads rows.
            var depotIds = await context.Downloads
                .Where(d => d.IsEvicted && d.DepotId != null)
                .Select(d => d.DepotId!.Value)
                .Distinct()
                .ToListAsync(stoppingToken);

            if (urls.Count == 0 && depotIds.Count == 0)
            {
                _logger.LogInformation(
                    "[EvictedLogPurge] {Count} evicted downloads have no URL/depot history — nothing to purge from logs",
                    evictedDownloadIds.Count);
                return;
            }

            _operationTracker.UpdateProgress(operationId, 0, "signalr.evictionRemove.purgingLogs");
            await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalProgress,
                new EvictionRemovalProgress(operationId, "purging_log_entries",
                    "signalr.evictionRemove.purgingLogs", 0, 0, 0,
                    new Dictionary<string, object?> { ["count"] = urls.Count + depotIds.Count }));

            var rustBinaryPath = _pathResolver.GetRustCachePurgeLogEntriesPath();
            if (!File.Exists(rustBinaryPath))
            {
                _logger.LogWarning(
                    "[EvictedLogPurge] cache_purge_log_entries binary not found at {Path} — skipping log rewrite. DB deletes will still proceed.",
                    rustBinaryPath);
                return;
            }

            var operationsDir = _pathResolver.GetOperationsDirectory();
            Directory.CreateDirectory(operationsDir);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var inputJsonPath = Path.Combine(operationsDir, $"evicted_log_purge_input_{timestamp}.json");

            var jsonPayload = JsonSerializer.Serialize(
                new { urls, depot_ids = depotIds },
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
                if (stoppingToken.IsCancellationRequested) break;

                var dsLogPath = datasource.LogPath;
                if (string.IsNullOrWhiteSpace(dsLogPath) || !Directory.Exists(dsLogPath))
                {
                    _logger.LogDebug(
                        "[EvictedLogPurge] Skipping datasource '{Datasource}': log dir '{LogPath}' does not exist",
                        datasource.Name, dsLogPath);
                    dsIndex++;
                    continue;
                }

                var outputJsonPath = Path.Combine(operationsDir,
                    $"evicted_log_purge_output_{datasource.Name}_{timestamp}.json");

                var args = $"\"{dsLogPath}\" \"{inputJsonPath}\" \"{outputJsonPath}\" --progress";
                var startInfo = _rustProcessHelper.CreateProcessStartInfo(rustBinaryPath, args);

                _logger.LogInformation(
                    "[EvictedLogPurge] Running bulk log purge for datasource '{Datasource}': {Binary} {Args}",
                    datasource.Name, rustBinaryPath, args);

                try
                {
                    // Launch process and read stdout line-by-line for real-time progress
                    using var process = Process.Start(startInfo);
                    if (process == null)
                        throw new InvalidOperationException($"Failed to start cache_purge_log_entries for datasource '{datasource.Name}'");

                    // Read stdout line-by-line to capture progress events in real-time
                    var stderrTask = process.StandardError.ReadToEndAsync(stoppingToken);
                    var progressOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                    string? line;
                    while ((line = await process.StandardOutput.ReadLineAsync(stoppingToken)) != null)
                    {
                        stoppingToken.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        try
                        {
                            var progressEvent = JsonSerializer.Deserialize<PurgeLogProgressEvent>(line, progressOptions);
                            if (progressEvent?.Event == "progress" && progressEvent.PercentComplete.HasValue)
                            {
                                // Map Rust binary's 0-100% into the per-datasource sub-range of the overall purge step.
                                // The purge step covers 0-30% of the overall operation. Within that, each datasource
                                // gets an equal slice. E.g. with 2 datasources: ds0 = 0-15%, ds1 = 15-30%.
                                var dsSliceStart = 30.0 * dsIndex / totalDatasources;
                                var dsSliceSize = 30.0 / totalDatasources;
                                var mappedPercent = dsSliceStart + (progressEvent.PercentComplete.Value / 100.0) * dsSliceSize;

                                _operationTracker.UpdateProgress(operationId, mappedPercent, "signalr.evictionRemove.purgingLogs");
                                await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalProgress,
                                    new EvictionRemovalProgress(operationId, "purging_log_entries",
                                        "signalr.evictionRemove.purgingLogs", mappedPercent, 0, 0,
                                        new Dictionary<string, object?> { ["count"] = urls.Count + depotIds.Count, ["datasource"] = datasource.Name }));
                            }
                        }
                        catch (JsonException)
                        {
                            // Non-JSON stdout line — ignore (e.g. plain text log output)
                        }
                    }

                    await process.WaitForExitAsync(stoppingToken);
                    var stderr = await stderrTask;

                    if (process.ExitCode != 0)
                    {
                        datasourcesFailed++;
                        _logger.LogWarning(
                            "[EvictedLogPurge] cache_purge_log_entries exited {Code} for datasource '{Datasource}'. stderr: {Err}",
                            process.ExitCode, datasource.Name, stderr);
                        continue;
                    }

                    try
                    {
                        var report = await _rustProcessHelper.ReadAndCleanupOutputJsonAsync<PurgeLogEntriesReport>(
                            outputJsonPath, $"cache_purge_log_entries/{datasource.Name}");
                        totalLinesRemoved += report.LinesRemoved;
                        datasourcesProcessed++;
                        _logger.LogInformation(
                            "[EvictedRemoval] Log purge removed {Lines} lines from access.log* in datasource '{Datasource}' ({Perms} permission errors)",
                            report.LinesRemoved, datasource.Name, report.PermissionErrors);
                    }
                    catch (Exception reportEx)
                    {
                        datasourcesProcessed++;
                        _logger.LogWarning(reportEx,
                            "[EvictedRemoval] Log purge succeeded (exit 0) for datasource '{Datasource}' but output JSON was unreadable",
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
                    _logger.LogWarning(innerEx,
                        "[EvictedLogPurge] Failed to run cache_purge_log_entries for datasource '{Datasource}' — DB deletes will still proceed",
                        datasource.Name);
                }

                dsIndex++;
            }

            _logger.LogInformation(
                "[EvictedRemoval] Log purge summary: {Total} lines removed across {Ok} datasources ({Failed} failed)",
                totalLinesRemoved, datasourcesProcessed, datasourcesFailed);

            // Cleanup input JSON (output JSONs already cleaned up via ReadAndCleanupOutputJsonAsync)
            try { File.Delete(inputJsonPath); } catch { /* best effort */ }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort: log and continue so DB deletes still run.
            _logger.LogWarning(ex,
                "[EvictedLogPurge] Unexpected error during bulk log purge — DB deletes will still proceed");
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
        CancellationToken ct)
    {
        int totalUnevicted = 0;

        // Steam: rows matched by GameAppId (EpicAppId is null)
        var evictedSteamGameIds = await context.CachedGameDetections
            .Where(g => g.IsEvicted && g.EpicAppId == null)
            .Select(g => g.GameAppId)
            .ToListAsync(ct);

        if (evictedSteamGameIds.Count > 0)
        {
            var steamGamesToUnevict = await context.Downloads
                .Where(d => d.GameAppId != null
                         && evictedSteamGameIds.Contains(d.GameAppId.Value)
                         && !d.IsEvicted)
                .Select(d => d.GameAppId!.Value)
                .Distinct()
                .ToListAsync(ct);

            if (steamGamesToUnevict.Count > 0)
            {
                var steamUpdated = await context.CachedGameDetections
                    .Where(g => g.EpicAppId == null && steamGamesToUnevict.Contains(g.GameAppId))
                    .ExecuteUpdateAsync(s => s.SetProperty(g => g.IsEvicted, false), ct);

                totalUnevicted += steamUpdated;
                logger.LogInformation(
                    "[GameDetection] Self-healed {Count} Steam games — Downloads no longer all evicted",
                    steamUpdated);
            }
        }

        // Epic: rows matched by EpicAppId
        var evictedEpicAppIds = await context.CachedGameDetections
            .Where(g => g.IsEvicted && g.EpicAppId != null)
            .Select(g => g.EpicAppId!)
            .ToListAsync(ct);

        if (evictedEpicAppIds.Count > 0)
        {
            var epicGamesToUnevict = await context.Downloads
                .Where(d => d.EpicAppId != null
                         && evictedEpicAppIds.Contains(d.EpicAppId)
                         && !d.IsEvicted)
                .Select(d => d.EpicAppId!)
                .Distinct()
                .ToListAsync(ct);

            if (epicGamesToUnevict.Count > 0)
            {
                var epicUpdated = await context.CachedGameDetections
                    .Where(g => g.EpicAppId != null && epicGamesToUnevict.Contains(g.EpicAppId!))
                    .ExecuteUpdateAsync(s => s.SetProperty(g => g.IsEvicted, false), ct);

                totalUnevicted += epicUpdated;
                logger.LogInformation(
                    "[GameDetection] Self-healed {Count} Epic games — Downloads no longer all evicted",
                    epicUpdated);
            }
        }

        return totalUnevicted;
    }

    public static async Task<int> EvictCachedGameDetectionsAsync(
        AppDbContext context,
        ILogger logger,
        CancellationToken ct)
    {
        int totalEvicted = 0;

        // Steam: rows matched by GameAppId (EpicAppId is null)
        var unevictedSteamGameIds = await context.CachedGameDetections
            .Where(g => !g.IsEvicted && g.EpicAppId == null)
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
                    "[GameDetection] Marked {Count} Steam games as evicted — all Downloads now evicted",
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
                    "[GameDetection] Marked {Count} Epic games as evicted — all Downloads now evicted",
                    epicUpdated);
            }
        }

        return totalEvicted;
    }

    /// <summary>
    /// Self-heal: clears IsEvicted on CachedServiceDetection rows that have reappeared on disk
    /// (CacheFilesFound > 0). Services do not have a Downloads FK relationship so the check is
    /// simpler — if the Rust scan found cache files again, the service is no longer evicted.
    /// </summary>
    public static async Task<int> UnevictCachedServiceDetectionsAsync(
        AppDbContext context,
        ILogger logger,
        CancellationToken ct)
    {
        var updated = await context.CachedServiceDetections
            .Where(s => s.IsEvicted && s.CacheFilesFound > 0)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEvicted, false), ct);

        if (updated > 0)
        {
            logger.LogInformation(
                "[ServiceDetection] Self-healed {Count} evicted services — cache files found on disk again",
                updated);
        }

        return updated;
    }

    /// <summary>
    /// Removes evicted Downloads and their LogEntries for a single entity (Steam game, Epic game,
    /// or non-game service). Unlike <see cref="RemoveEvictedRecordsAsync"/>, this method:
    /// - Scopes ALL database operations to the specified entity.
    /// - Does NOT upsert a CachedGameDetection row — the aggregate detection row is left untouched
    ///   so that Dashboard "Games on Disk" stats remain correct for partially-evicted entities.
    /// - Calls <see cref="UnevictCachedGameDetectionsAsync"/> / <see cref="UnevictCachedServiceDetectionsAsync"/>
    ///   at the end for defensive self-healing.
    /// </summary>
    public async Task RemoveEvictedRecordsForEntityAsync(
        AppDbContext context,
        EvictionScope scope,
        string key,
        CancellationToken stoppingToken,
        string? operationId = null)
    {
        CancellationTokenSource? cts = null;

        if (operationId == null)
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            operationId = _operationTracker.RegisterOperation(
                OperationType.EvictionRemoval,
                $"Eviction Removal ({scope}: {key})",
                cts);

            await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalStarted,
                new EvictionRemovalStarted("signalr.evictionRemove.starting.entity", operationId,
                    new Dictionary<string, object?> { ["scope"] = scope.ToString(), ["key"] = key }));
        }

        try
        {
            // Step -1: Rewrite nginx access.log files to drop entries for this entity's evicted
            // downloads BEFORE deleting LogEntries/Downloads from the database. Best-effort —
            // failures are logged as warnings and do not block the DB delete.
            _operationTracker.UpdateProgress(operationId, 10, "signalr.evictionRemove.purgingLogs");
            await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalProgress,
                new EvictionRemovalProgress(operationId, "purging_logs", "signalr.evictionRemove.purgingLogs", 10, 0, 0));

            await PurgeEvictedLogEntriesForEntityAsync(context, scope, key, operationId, stoppingToken);

            int logEntriesDeleted = 0;
            int downloadsDeleted = 0;

            _operationTracker.UpdateProgress(operationId, 25, "signalr.evictionRemove.removingLogs");
            await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalProgress,
                new EvictionRemovalProgress(operationId, "removing_log_entries", "signalr.evictionRemove.removingLogs", 25, 0, 0));

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

                        EvictionScope.Service => await context.LogEntries
                            .Where(le => le.DownloadId != null
                                      && le.Download != null
                                      && le.Download.IsEvicted
                                      && le.Download.GameAppId == null
                                      && le.Download.EpicAppId == null
                                      && le.Download.Service.ToLower() == key.ToLower())
                            .ExecuteDeleteAsync(stoppingToken),

                        _ => throw new ArgumentOutOfRangeException(nameof(scope))
                    };

                    // Step 2: Delete this entity's evicted Downloads.
                    _operationTracker.UpdateProgress(operationId, 50, "signalr.evictionRemove.removingDownloads");
                    await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalProgress,
                        new EvictionRemovalProgress(operationId, "removing_downloads", "signalr.evictionRemove.removingDownloads", 50, 0, logEntriesDeleted));

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

                        EvictionScope.Service => await context.Downloads
                            .Where(d => d.IsEvicted
                                     && d.GameAppId == null
                                     && d.EpicAppId == null
                                     && d.Service.ToLower() == key.ToLower())
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

            // Step 3: Defensive self-heal — if all evicted rows for this entity are now gone, clear
            // the aggregate IsEvicted flag so Dashboard stats update on the next GetCachedDetectionAsync.
            _operationTracker.UpdateProgress(operationId, 75, "signalr.evictionRemove.updatingStatus");
            await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalProgress,
                new EvictionRemovalProgress(operationId, "updating_status", "signalr.evictionRemove.updatingStatus", 75, downloadsDeleted, logEntriesDeleted));

            // Step 3a: Targeted un-evict — clear IsEvicted on the specific entity we just removed
            // downloads for. This is the equivalent of Game Cache Removal's row-delete for the
            // partial-eviction case: after the user removes the evicted portion, the entity is no
            // longer considered evicted regardless of CacheFilesFound (per CachedGameDetection.IsEvicted
            // docstring: "Games with no matching downloads are NOT considered evicted"). Any remaining
            // non-evicted downloads keep their row with IsEvicted=false. The next detection scan will
            // refresh CacheFilesFound / TotalSizeBytes if they drifted.
            int targetedUnevicted = scope switch
            {
                EvictionScope.Steam => await context.CachedGameDetections
                    .Where(g => g.IsEvicted
                             && g.GameAppId == long.Parse(key)
                             && g.EpicAppId == null)
                    .ExecuteUpdateAsync(g => g.SetProperty(x => x.IsEvicted, false), stoppingToken),

                EvictionScope.Epic => await context.CachedGameDetections
                    .Where(g => g.IsEvicted && g.EpicAppId == key)
                    .ExecuteUpdateAsync(g => g.SetProperty(x => x.IsEvicted, false), stoppingToken),

                EvictionScope.Service => await context.CachedServiceDetections
                    .Where(s => s.IsEvicted && s.ServiceName.ToLower() == key.ToLower())
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEvicted, false), stoppingToken),

                _ => 0
            };

            if (targetedUnevicted > 0)
            {
                _logger.LogInformation(
                    "[EvictionRemoval] Targeted un-evict for {Scope} '{Key}': cleared IsEvicted on {Count} detection row(s)",
                    scope, key, targetedUnevicted);
            }

            // Step 3b: Bulk self-heal helper stays for the separate "files reappeared on disk"
            // use case (background reconciliation). Its `CacheFilesFound > 0` filter is correct
            // for that scenario; the targeted un-evict above handles the user-triggered removal.
            if (scope == EvictionScope.Service)
            {
                await UnevictCachedServiceDetectionsAsync(context, _logger, stoppingToken);
            }
            else
            {
                await UnevictCachedGameDetectionsAsync(context, _logger, stoppingToken);
            }

            // Invalidate the detection cache so the frontend refetch gets fresh data
            _operationTracker.UpdateProgress(operationId, 90, "signalr.evictionRemove.finalizingRemoval");
            await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalProgress,
                new EvictionRemovalProgress(operationId, "finalizing_removal", "signalr.evictionRemove.finalizingRemoval", 90, downloadsDeleted, logEntriesDeleted));

            var detectionService = _serviceProvider.GetService<GameCacheDetectionService>();
            detectionService?.InvalidateDetectionCache();

            _operationTracker.UpdateProgress(operationId, 100, "signalr.evictionRemove.complete");
            _operationTracker.CompleteOperation(operationId, success: true);
            await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalComplete,
                new EvictionRemovalComplete(true, operationId, "signalr.evictionRemove.complete", downloadsDeleted, logEntriesDeleted));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EvictionScan] Error removing evicted records for {Scope} '{Key}'", scope, key);
            _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);
            await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalComplete,
                new EvictionRemovalComplete(false, operationId, "signalr.evictionRemove.failed", 0, 0, ex.Message));
        }
        finally
        {
            cts?.Dispose();
        }
    }

    /// <summary>
    /// Entity-scoped variant of <see cref="PurgeEvictedLogEntriesAsync"/>. Rewrites nginx access.log
    /// files to drop entries belonging only to the specified entity's evicted downloads.
    /// Best-effort: failures are logged as warnings and do not block the DB delete.
    /// </summary>
    private async Task PurgeEvictedLogEntriesForEntityAsync(
        AppDbContext context,
        EvictionScope scope,
        string key,
        string operationId,
        CancellationToken stoppingToken)
    {
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

                EvictionScope.Service => await context.Downloads
                    .Where(d => d.IsEvicted
                             && d.GameAppId == null
                             && d.EpicAppId == null
                             && d.Service.ToLower() == key.ToLower())
                    .Select(d => d.Id)
                    .ToListAsync(stoppingToken),

                _ => throw new ArgumentOutOfRangeException(nameof(scope))
            };

            if (evictedDownloadIds.Count == 0)
            {
                _logger.LogDebug("[EvictedLogPurge] No evicted downloads for {Scope} '{Key}' — skipping log rewrite", scope, key);
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

                EvictionScope.Service => await context.Downloads
                    .Where(d => d.IsEvicted
                             && d.GameAppId == null
                             && d.EpicAppId == null
                             && d.Service.ToLower() == key.ToLower()
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
            // lines belonging to the still-cached copy — data loss.
            //
            // Filter depot_ids down to those that ONLY appear in evicted Downloads for this
            // entity. This preserves the benefit of depot matching (catching orphan log
            // lines that have no LogEntry row) while preventing cross-state contamination.
            List<long> safeDepotIds;
            if (evictedDepotIds.Count == 0)
            {
                safeDepotIds = evictedDepotIds;
            }
            else
            {
                var cachedDepotIds = scope switch
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

                    EvictionScope.Service => await context.Downloads
                        .Where(d => !d.IsEvicted
                                 && d.GameAppId == null
                                 && d.EpicAppId == null
                                 && d.Service.ToLower() == key.ToLower()
                                 && d.DepotId != null)
                        .Select(d => d.DepotId!.Value)
                        .Distinct()
                        .ToListAsync(stoppingToken),

                    _ => throw new ArgumentOutOfRangeException(nameof(scope))
                };

                var cachedSet = new HashSet<long>(cachedDepotIds);
                safeDepotIds = evictedDepotIds.Where(d => !cachedSet.Contains(d)).ToList();

                var skippedCount = evictedDepotIds.Count - safeDepotIds.Count;
                if (skippedCount > 0)
                {
                    _logger.LogInformation(
                        "[EvictedLogPurge] Excluded {Skipped} depot ID(s) from log purge for {Scope} '{Key}' because they also appear in still-cached downloads (partial-eviction safety)",
                        skippedCount, scope, key);
                }
            }

            var depotIds = safeDepotIds;

            if (urls.Count == 0 && depotIds.Count == 0)
            {
                _logger.LogInformation(
                    "[EvictedLogPurge] {Count} evicted downloads for {Scope} '{Key}' have no URL/depot history — nothing to purge from logs",
                    evictedDownloadIds.Count, scope, key);
                return;
            }

            _operationTracker.UpdateProgress(operationId, 0, "signalr.evictionRemove.purgingLogs");
            await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalProgress,
                new EvictionRemovalProgress(operationId, "purging_log_entries",
                    "signalr.evictionRemove.purgingLogs", 0, 0, 0,
                    new Dictionary<string, object?> { ["count"] = urls.Count + depotIds.Count }));

            var rustBinaryPath = _pathResolver.GetRustCachePurgeLogEntriesPath();
            if (!File.Exists(rustBinaryPath))
            {
                _logger.LogWarning(
                    "[EvictedLogPurge] cache_purge_log_entries binary not found at {Path} — skipping log rewrite. DB deletes will still proceed.",
                    rustBinaryPath);
                return;
            }

            var operationsDir = _pathResolver.GetOperationsDirectory();
            Directory.CreateDirectory(operationsDir);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var inputJsonPath = Path.Combine(operationsDir, $"evicted_entity_log_purge_input_{timestamp}.json");

            var jsonPayload = JsonSerializer.Serialize(
                new { urls, depot_ids = depotIds },
                new JsonSerializerOptions { WriteIndented = false });
            await File.WriteAllTextAsync(inputJsonPath, jsonPayload, stoppingToken);

            long totalLinesRemoved = 0;
            int datasourcesProcessed = 0;
            int datasourcesFailed = 0;

            foreach (var datasource in _datasourceService.GetDatasources())
            {
                if (stoppingToken.IsCancellationRequested) break;

                var dsLogPath = datasource.LogPath;
                if (string.IsNullOrWhiteSpace(dsLogPath) || !Directory.Exists(dsLogPath))
                {
                    _logger.LogDebug(
                        "[EvictedLogPurge] Skipping datasource '{Datasource}': log dir '{LogPath}' does not exist",
                        datasource.Name, dsLogPath);
                    continue;
                }

                var outputJsonPath = Path.Combine(operationsDir,
                    $"evicted_entity_log_purge_output_{datasource.Name}_{timestamp}.json");

                var args = $"\"{dsLogPath}\" \"{inputJsonPath}\" \"{outputJsonPath}\"";
                var startInfo = _rustProcessHelper.CreateProcessStartInfo(rustBinaryPath, args);

                _logger.LogInformation(
                    "[EvictedLogPurge] Running entity log purge for {Scope} '{Key}', datasource '{Datasource}': {Binary} {Args}",
                    scope, key, datasource.Name, rustBinaryPath, args);

                try
                {
                    var result = await _rustProcessHelper.ExecuteProcessAsync(startInfo, stoppingToken);

                    if (result.ExitCode != 0)
                    {
                        datasourcesFailed++;
                        _logger.LogWarning(
                            "[EvictedLogPurge] cache_purge_log_entries exited {Code} for datasource '{Datasource}'. stderr: {Err}",
                            result.ExitCode, datasource.Name, result.Error);
                        continue;
                    }

                    try
                    {
                        var report = await _rustProcessHelper.ReadAndCleanupOutputJsonAsync<PurgeLogEntriesReport>(
                            outputJsonPath, $"cache_purge_log_entries/{datasource.Name}");
                        totalLinesRemoved += report.LinesRemoved;
                        datasourcesProcessed++;
                        _logger.LogInformation(
                            "[EvictedRemoval] Entity log purge removed {Lines} lines from access.log* in datasource '{Datasource}' ({Perms} permission errors)",
                            report.LinesRemoved, datasource.Name, report.PermissionErrors);
                    }
                    catch (Exception reportEx)
                    {
                        datasourcesProcessed++;
                        _logger.LogWarning(reportEx,
                            "[EvictedRemoval] Entity log purge succeeded (exit 0) for datasource '{Datasource}' but output JSON was unreadable",
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
                    _logger.LogWarning(innerEx,
                        "[EvictedLogPurge] Failed to run cache_purge_log_entries for datasource '{Datasource}' — DB deletes will still proceed",
                        datasource.Name);
                }
            }

            _logger.LogInformation(
                "[EvictedRemoval] Entity log purge summary ({Scope} '{Key}'): {Total} lines removed across {Ok} datasources ({Failed} failed)",
                scope, key, totalLinesRemoved, datasourcesProcessed, datasourcesFailed);

            // Cleanup input JSON (output JSONs already cleaned up via ReadAndCleanupOutputJsonAsync)
            try { File.Delete(inputJsonPath); } catch { /* best effort */ }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort: log and continue so DB deletes still run.
            _logger.LogWarning(ex,
                "[EvictedLogPurge] Unexpected error during entity log purge ({Scope} '{Key}') — DB deletes will still proceed",
                scope, key);
        }
    }

    /// <summary>
    /// Deserialized progress event from the `cache_purge_log_entries` Rust binary's stdout JSON lines.
    /// Matches the ProgressEvent struct emitted by the Rust ProgressReporter when --progress is passed.
    /// </summary>
    private sealed class PurgeLogProgressEvent
    {
        [System.Text.Json.Serialization.JsonPropertyName("event")]
        public string? Event { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("percentComplete")]
        public double? PercentComplete { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("stageKey")]
        public string? StageKey { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public string? Status { get; set; }
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
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public double PercentComplete { get; set; }
    public int Processed { get; set; }
    public int TotalEstimate { get; set; }
    public int Evicted { get; set; }
    public int UnEvicted { get; set; }
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
