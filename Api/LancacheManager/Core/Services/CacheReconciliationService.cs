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
    private readonly GameCacheDetectionDataService _gameCacheDetectionDataService;
    private readonly GameCacheDetectionService _gameCacheDetectionService;
    private readonly EvictedDetectionPreservationService _evictedDetectionPreservationService;
    private int _isRunning;
    private bool _currentScanIsSilent = true;
    private readonly TaskCompletionSource<bool> _firstStartupScanComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override string ServiceName => "CacheReconciliationService";
    protected override TimeSpan Interval => TimeSpan.FromHours(6);
    public override bool DefaultRunOnStartup => true;

    public override string ServiceKey => "cacheReconciliation";

    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;
    public bool CurrentScanIsSilent => _currentScanIsSilent;

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
            var operationId = _operationTracker.RegisterOperation(
                OperationType.EvictionScan,
                "Eviction Scan",
                cts);

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
        EvictedDetectionPreservationService evictedDetectionPreservationService)
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
            var operationId = _operationTracker.RegisterOperation(
                OperationType.EvictionScan,
                "Eviction Scan (Startup)",
                cts);

            await ReconcileCacheFilesAsync(context, operationId, linked.Token, silent);
        }
        finally
        {
            EndRun();
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
            var operationId = _operationTracker.RegisterOperation(
                OperationType.EvictionScan,
                "Eviction Scan",
                cts);

            await ReconcileCacheFilesAsync(context, operationId, linked.Token, silent);
        }
        finally
        {
            EndRun();
        }
    }

    private async Task ReconcileCacheFilesAsync(AppDbContext context, Guid operationId, CancellationToken stoppingToken, bool silent = false)
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
                            Status: progress.Status.ToWireString(),
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

                    // Fix for "newly evicted items don't show until restart": after the scan
                    // flags Downloads as evicted, run recovery so any game/service WITHOUT a
                    // matching CachedGameDetection / CachedServiceDetection row gets one
                    // inserted with IsEvicted=true. LoadDetectionFromDatabaseAsync only
                    // returns entities that have detection rows — missing rows = invisible
                    // in the Evicted Items UI. Recovery on startup wasn't enough; the user
                    // needs it on every eviction scan because cache files can be evicted
                    // (via manual clear, nginx cache miss, etc.) at any time.
                    try
                    {
                        var gamesRecovered = await _gameCacheDetectionService.RecoverEvictedGamesAsync();
                        var servicesRecovered = await _gameCacheDetectionService.RecoverEvictedServicesAsync();
                        _logger.LogInformation(
                            "[EvictionScan] Post-scan recovery: inserted {Games} game + {Services} service detection rows from Downloads history (zero counts mean every evicted entity already had a row — their evicted_downloads_count will update in-place)",
                            gamesRecovered, servicesRecovered);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[EvictionScan] Post-scan recovery failed — newly-evicted entities may remain hidden until next full scan");
                    }

                    _gameCacheDetectionService.InvalidateDetectionCache();
                }

                // Handle evicted data "remove" mode — only run if there are evicted records
                var evictedDataMode = _stateService.GetEvictedDataMode();
                if (evictedDataMode == EvictedDataMode.Remove.ToWireString()
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

    public async Task<Guid> StartBulkEvictionRemovalAsync(CancellationToken cancellationToken)
    {
        var cts = new CancellationTokenSource();
        var operationId = _operationTracker.RegisterOperation(
            OperationType.EvictionRemoval,
            "Eviction Removal",
            cts,
            new EvictionRemovalMetadata());

        await _notifications.NotifyAllAsync(
            SignalREvents.EvictionRemovalStarted,
            new EvictionRemovalStarted("signalr.evictionRemove.starting.bulk", operationId));

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await RemoveEvictedRecordsAsync(context, cts.Token, operationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EvictionRemoval] Unhandled error before removal started");
                await CompleteEvictionRemovalAsync(
                    operationId,
                    success: false,
                    stageKey: "signalr.evictionRemove.failedToStart",
                    error: ex.Message);
            }
            finally
            {
                cts.Dispose();
            }
        }, cts.Token);

        return operationId;
    }

    public async Task<Guid> StartScopedEvictionRemovalAsync(
        EvictionScope scope,
        string key,
        string? resolvedGameName,
        string? resolvedGameAppId,
        CancellationToken cancellationToken,
        string? resolvedEpicAppId = null)
    {
        var cts = new CancellationTokenSource();
        var metadata = new EvictionRemovalMetadata
        {
            Scope = scope.ToString().ToLowerInvariant(),
            Key = key,
            GameName = resolvedGameName
        };
        var operationId = _operationTracker.RegisterOperation(
            OperationType.EvictionRemoval,
            $"Eviction Removal ({scope}: {key})",
            cts,
            metadata);

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
            try
            {
                using var scopeLifetime = _serviceProvider.CreateScope();
                var context = scopeLifetime.ServiceProvider.GetRequiredService<AppDbContext>();
                await RemoveEvictedRecordsForEntityAsync(context, scope, key, cts.Token, operationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EvictedRemoval] Unhandled error before entity removal started ({Scope} '{Key}')", scope, key);
                await CompleteEvictionRemovalAsync(
                    operationId,
                    success: false,
                    stageKey: "signalr.evictionRemove.failedToStart",
                    error: ex.Message);
            }
            finally
            {
                cts.Dispose();
            }
        }, cts.Token);

        return operationId;
    }

    private async Task ReportEvictionRemovalProgressAsync(
        Guid operationId,
        double percentComplete,
        string status,
        string stageKey,
        int downloadsRemoved = 0,
        int logEntriesRemoved = 0,
        Dictionary<string, object?>? context = null)
    {
        _operationTracker.UpdateProgress(operationId, percentComplete, stageKey);
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

    private async Task CompleteEvictionRemovalAsync(
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

        _operationTracker.CompleteOperation(operationId, success, success ? null : error);
        await _notifications.NotifyAllAsync(
            SignalREvents.EvictionRemovalComplete,
            new EvictionRemovalComplete(
                success,
                operationId,
                stageKey,
                downloadsRemoved,
                logEntriesRemoved,
                error,
                cancelled));
    }

    private async Task<EvictedLogPurgeSummary> RunEvictedLogPurgeAsync(
        Guid operationId,
        EvictedLogPurgeTargets targets,
        CancellationToken stoppingToken,
        EvictedLogPurgeRunOptions options)
    {
        await ReportEvictionRemovalProgressAsync(
            operationId,
            options.ProgressStartPercent,
            "purging_log_entries",
            "signalr.evictionRemove.purgingLogs",
            context: new Dictionary<string, object?> { ["count"] = targets.Urls.Count + targets.DepotIds.Count });

        var rustBinaryPath = _pathResolver.GetRustCachePurgeLogEntriesPath();
        if (!File.Exists(rustBinaryPath))
        {
            _logger.LogWarning(
                "[EvictedLogPurge] cache_purge_log_entries binary not found at {Path} — skipping log rewrite. DB deletes will still proceed.",
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
                var args = $"\"{dsLogPath}\" \"{inputJsonPath}\" \"{outputJsonPath}\" --progress";
                var startInfo = _rustProcessHelper.CreateProcessStartInfo(rustBinaryPath, args);

                _logger.LogInformation(
                    "[EvictedLogPurge] Running {RunDescription} for datasource '{Datasource}': {Binary} {Args}",
                    options.RunDescription,
                    datasource.Name,
                    rustBinaryPath,
                    args);

                try
                {
                    using var process = Process.Start(startInfo);
                    if (process == null)
                    {
                        throw new InvalidOperationException(
                            $"Failed to start cache_purge_log_entries for datasource '{datasource.Name}'");
                    }

                    var stderrTask = process.StandardError.ReadToEndAsync(stoppingToken);
                    var progressOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                    string? line;
                    while ((line = await process.StandardOutput.ReadLineAsync(stoppingToken)) != null)
                    {
                        stoppingToken.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        try
                        {
                            var progressEvent = JsonSerializer.Deserialize<PurgeLogProgressEvent>(line, progressOptions);
                            if (progressEvent?.Event == RustProgressEventKind.Progress && progressEvent.PercentComplete.HasValue)
                            {
                                var dsSliceStart = options.ProgressStartPercent +
                                    (options.ProgressSpanPercent * dsIndex / totalDatasources);
                                var dsSliceSize = options.ProgressSpanPercent / totalDatasources;
                                var mappedPercent = dsSliceStart +
                                    (progressEvent.PercentComplete.Value / 100.0) * dsSliceSize;

                                await ReportEvictionRemovalProgressAsync(
                                    operationId,
                                    mappedPercent,
                                    "purging_log_entries",
                                    "signalr.evictionRemove.purgingLogs",
                                    context: new Dictionary<string, object?>
                                    {
                                        ["count"] = targets.Urls.Count + targets.DepotIds.Count,
                                        ["datasource"] = datasource.Name
                                    });
                            }
                        }
                        catch (JsonException)
                        {
                            // Ignore non-JSON stdout lines.
                        }
                    }

                    await process.WaitForExitAsync(stoppingToken);
                    var stderr = await stderrTask;

                    if (process.ExitCode != 0)
                    {
                        datasourcesFailed++;
                        _logger.LogWarning(
                            "[EvictedLogPurge] cache_purge_log_entries exited {Code} for datasource '{Datasource}'. stderr: {Err}",
                            process.ExitCode,
                            datasource.Name,
                            stderr);
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
                        "[EvictedLogPurge] Failed to run cache_purge_log_entries for datasource '{Datasource}' — DB deletes will still proceed",
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

            return new EvictedLogPurgeSummary(totalLinesRemoved, datasourcesProcessed, datasourcesFailed);
        }
        finally
        {
            try { File.Delete(inputJsonPath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Deletes all evicted Download records and their associated LogEntries from the database.
    /// Called when evicted data mode is set to "remove", either from the scan flow (no operationId)
    /// or from the controller with a pre-registered operationId.
    /// When operationId is null, a new operation is registered and Started is emitted internally.
    /// In both cases Progress and Complete events are always emitted.
    /// </summary>
    public async Task RemoveEvictedRecordsAsync(AppDbContext context, CancellationToken stoppingToken, Guid? operationId = null)
    {
        CancellationTokenSource? cts = null;

        // Deliberately not using TrackedRemovalOperationRunner: this service can start from the background scan path without a controller HTTP lifecycle.
        if (operationId == null)
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            operationId = _operationTracker.RegisterOperation(
                OperationType.EvictionRemoval,
                "Eviction Removal",
                cts,
                new EvictionRemovalMetadata()); // bulk removal — no specific scope/key

            await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalStarted,
                new EvictionRemovalStarted("signalr.evictionRemove.starting.bulk", operationId.Value));
        }

        // At this point operationId is guaranteed non-null; capture as non-nullable for the rest of the method.
        var opId = operationId.Value;

        try
        {
            // Step -1 (Fix 2): Rewrite nginx access.log files to drop entries for every evicted game
            // BEFORE deleting LogEntries/Downloads from the database. If we skipped this, a future
            // `ResetLogPosition` + full log re-parse would resurrect the evicted games because their
            // URLs still exist in the on-disk access.log. The rewrite is a best-effort optimization:
            // if the Rust binary fails we log a WARNING and continue — correctness of the DB delete
            // is preserved either way.
            await PurgeEvictedLogEntriesAsync(context, opId, stoppingToken);

            // Preservation-first bulk removal. The user workflow is:
            //   1. Cache Clear wipes all CachedGameDetections + CachedServiceDetections rows
            //      (but leaves Downloads intact).
            //   2. An eviction scan then flips Downloads.IsEvicted = true for every game/
            //      service that no longer has cache files (which is everything, post-clear).
            //   3. In Remove mode, this method runs. Users expect to SEE those now-evicted
            //      entities in the Evicted Items list with IsEvicted = true + CacheFilesFound = 0
            //      BEFORE we delete the Downloads rows, so the UI reflects what was purged.
            // We therefore upsert detection rows first, THEN delete Downloads. The per-item
            // Remove path (RemoveEvictedRecordsForEntityAsync) stays removal-driven — when
            // a user explicitly clicks Remove on a single entity the detection row is
            // deleted outright. The two paths together give: natural eviction preserves
            // visibility; explicit Remove makes it disappear.
            await ReportEvictionRemovalProgressAsync(
                opId,
                30,
                "preserving_evicted_entities",
                "signalr.evictionRemove.preserving");

            var evictedDownloads = await context.Downloads
                .Where(d => d.IsEvicted)
                .ToListAsync(stoppingToken);

            int gamesUpserted = 0;
            int servicesUpserted = 0;
            int logEntriesDeleted = 0;
            int downloadsDeleted = 0;

            var strategy = context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                gamesUpserted = 0;
                servicesUpserted = 0;
                logEntriesDeleted = 0;
                downloadsDeleted = 0;

                await using var transaction = await context.Database.BeginTransactionAsync(stoppingToken);
                try
                {
                    var preservationResult = await _evictedDetectionPreservationService.PreserveAsync(
                        context,
                        evictedDownloads,
                        stoppingToken);
                    gamesUpserted = preservationResult.GamesUpserted;
                    servicesUpserted = preservationResult.ServicesUpserted;

                    // Step 1: delete LogEntries for evicted downloads (FK constraint).
                    await ReportEvictionRemovalProgressAsync(
                        opId,
                        60,
                        "removing_log_entries",
                        "signalr.evictionRemove.removingLogs");

                    logEntriesDeleted = await context.LogEntries
                        .Where(le => le.DownloadId != null && le.Download != null && le.Download.IsEvicted)
                        .ExecuteDeleteAsync(stoppingToken);

                    // Step 2: delete evicted Downloads.
                    await ReportEvictionRemovalProgressAsync(
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

            if (downloadsDeleted > 0 || logEntriesDeleted > 0 || gamesUpserted > 0 || servicesUpserted > 0)
            {
                _logger.LogInformation(
                    "[EvictionScan] Remove mode: preserved {Games} games + {Services} services as evicted, deleted {Downloads} downloads, {LogEntries} log entries",
                    gamesUpserted, servicesUpserted, downloadsDeleted, logEntriesDeleted);
            }

            // Invalidate the detection cache so the frontend refetch gets fresh data
            _gameCacheDetectionService.InvalidateDetectionCache();
            _logger.LogDebug("[EvictedRemoval] Detection cache invalidated");

            await CompleteEvictionRemovalAsync(
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
            await CompleteEvictionRemovalAsync(
                opId,
                success: false,
                stageKey: "signalr.evictionRemove.cancelled",
                error: "Cancelled by user",
                cancelled: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EvictionScan] Error removing evicted records from database");
            await CompleteEvictionRemovalAsync(
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
    /// Fix 2: Rewrites nginx access.log files to drop all entries belonging to evicted games,
    /// using the `cache_purge_log_entries` Rust binary. Runs once per configured datasource.
    ///
    /// Called by <see cref="RemoveEvictedRecordsAsync"/> BEFORE the DB LogEntries/Downloads deletes
    /// so a future `ResetLogPosition` + full log re-parse cannot resurrect the evicted games.
    ///
    /// Best-effort: if the Rust binary fails we log a warning but still allow the DB deletes to
    /// proceed — the only loss is that a later full re-parse could re-create the rows.
    /// </summary>
    private async Task PurgeEvictedLogEntriesAsync(AppDbContext context, Guid operationId, CancellationToken stoppingToken)
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
        GameCacheDetectionDataService detectionDataService,
        EvictedDetectionPreservationService evictedDetectionPreservationService,
        CancellationToken ct)
    {
        var gamesToUnevict = await detectionDataService.GetGamesToUnevictAsync(context, ct);
        if (gamesToUnevict.SteamGameAppIds.Count == 0 && gamesToUnevict.EpicAppIds.Count == 0)
        {
            return 0;
        }

        var unpreserveResult = await evictedDetectionPreservationService.UnpreserveAsync(
            context,
            gamesToUnevict.SteamGameAppIds,
            gamesToUnevict.EpicAppIds,
            ct);

        if (unpreserveResult.SteamGamesUpdated > 0)
        {
            logger.LogInformation(
                "[GameDetection] Self-healed {Count} Steam games — Downloads no longer all evicted",
                unpreserveResult.SteamGamesUpdated);
        }

        if (unpreserveResult.EpicGamesUpdated > 0)
        {
            logger.LogInformation(
                "[GameDetection] Self-healed {Count} Epic games — Downloads no longer all evicted",
                unpreserveResult.EpicGamesUpdated);
        }

        return unpreserveResult.TotalUpdated;
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
    /// - DELETES the CachedGameDetection / CachedServiceDetection row for the entity outright
    ///   so the removal is durable — the next scan cannot resurrect it. A fresh row is
    ///   re-inserted only if the entity ever caches again.
    /// - Calls <see cref="UnevictCachedGameDetectionsAsync"/> / <see cref="UnevictCachedServiceDetectionsAsync"/>
    ///   at the end for defensive self-healing of unrelated rows.
    /// </summary>
    public async Task RemoveEvictedRecordsForEntityAsync(
        AppDbContext context,
        EvictionScope scope,
        string key,
        CancellationToken stoppingToken,
        Guid? operationId = null)
    {
        // Npgsql cannot translate string.Equals(..., StringComparison.OrdinalIgnoreCase);
        // service names are already stored lowercase, so lowercasing `key` once here lets
        // the EvictionScope.Service branches use plain `==` in the LINQ (SQL-translatable).
        var keyLower = key.ToLowerInvariant();

        CancellationTokenSource? cts = null;

        // Deliberately not using TrackedRemovalOperationRunner: this service can start from the background scan path without a controller HTTP lifecycle.
        if (operationId == null)
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            operationId = _operationTracker.RegisterOperation(
                OperationType.EvictionRemoval,
                $"Eviction Removal ({scope}: {key})",
                cts);

            await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalStarted,
                new EvictionRemovalStarted("signalr.evictionRemove.starting.entity", operationId.Value,
                    new Dictionary<string, object?> { ["scope"] = scope.ToString(), ["key"] = key },
                    EpicAppId: scope == EvictionScope.Epic ? key : null));
        }

        var opId = operationId.Value;

        try
        {
            // Step -1: Rewrite nginx access.log files to drop entries for this entity's evicted
            // downloads BEFORE deleting LogEntries/Downloads from the database. Best-effort —
            // failures are logged as warnings and do not block the DB delete.
            await PurgeEvictedLogEntriesForEntityAsync(context, scope, key, opId, stoppingToken);

            int logEntriesDeleted = 0;
            int downloadsDeleted = 0;

            await ReportEvictionRemovalProgressAsync(
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
                    await ReportEvictionRemovalProgressAsync(
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

            // Step 3: Defensive self-heal — if all evicted rows for this entity are now gone, clear
            // the aggregate IsEvicted flag so Dashboard stats update on the next GetCachedDetectionAsync.
            await ReportEvictionRemovalProgressAsync(
                opId,
                75,
                "updating_status",
                "signalr.evictionRemove.updatingStatus",
                downloadsRemoved: downloadsDeleted,
                logEntriesRemoved: logEntriesDeleted);

            // Step 3a: Targeted un-evict — clear IsEvicted on the specific entity we just removed
            // downloads for. This is the equivalent of Game Cache Removal's row-delete for the
            // partial-eviction case: after the user removes the evicted portion, the entity is no
            // longer considered evicted regardless of CacheFilesFound (per CachedGameDetection.IsEvicted
            // docstring: "Games with no matching downloads are NOT considered evicted"). Any remaining
            // non-evicted downloads keep their row with IsEvicted=false. The next detection scan will
            // refresh CacheFilesFound / TotalSizeBytes if they drifted.
            // Removal-driven cleanup. Two cases:
            //   • Full removal — no Downloads remain for this entity after the delete above.
            //     DELETE the detection row so the next scan's flip-to-evicted logic cannot
            //     resurrect it. This is the WSUS case that used to come back after restart.
            //   • Partial eviction — some Downloads for this entity still exist and are NOT
            //     evicted (the entity has real cache files). Leave the detection row in
            //     place and just clear IsEvicted = false so the UI shows the entity as
            //     cached again. Deleting here would wipe a legitimately-cached entity.
            bool anyRemaining = scope switch
            {
                EvictionScope.Steam => await context.Downloads
                    .AnyAsync(d => d.GameAppId == long.Parse(key) && d.EpicAppId == null, stoppingToken),
                EvictionScope.Epic => await context.Downloads
                    .AnyAsync(d => d.EpicAppId == key, stoppingToken),
                EvictionScope.Service => await context.Downloads
                    .AnyAsync(d => d.GameAppId == null
                                && d.EpicAppId == null
                                && d.Service == keyLower, stoppingToken),
                _ => false
            };

            int detectionRowsChanged;
            if (!anyRemaining)
            {
                detectionRowsChanged = scope switch
                {
                    EvictionScope.Steam => await context.CachedGameDetections
                        .Where(g => g.GameAppId == long.Parse(key) && g.EpicAppId == null)
                        .ExecuteDeleteAsync(stoppingToken),

                    EvictionScope.Epic => await context.CachedGameDetections
                        .Where(g => g.EpicAppId == key)
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
            // use case (background reconciliation). Its `CacheFilesFound > 0` filter is correct
            // for that scenario; the targeted un-evict above handles the user-triggered removal.
            if (scope == EvictionScope.Service)
            {
                await UnevictCachedServiceDetectionsAsync(context, _logger, stoppingToken);
            }
            else
            {
                await UnevictCachedGameDetectionsAsync(
                    context,
                    _logger,
                    _gameCacheDetectionDataService,
                    _evictedDetectionPreservationService,
                    stoppingToken);
            }

            // Invalidate the detection cache so the frontend refetch gets fresh data
            await ReportEvictionRemovalProgressAsync(
                opId,
                90,
                "finalizing_removal",
                "signalr.evictionRemove.finalizingRemoval",
                downloadsRemoved: downloadsDeleted,
                logEntriesRemoved: logEntriesDeleted);

            var detectionService = _serviceProvider.GetService<GameCacheDetectionService>();
            detectionService?.InvalidateDetectionCache();

            await CompleteEvictionRemovalAsync(
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
            await CompleteEvictionRemovalAsync(
                opId,
                success: false,
                stageKey: "signalr.evictionRemove.cancelled",
                error: "Cancelled by user",
                cancelled: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EvictionScan] Error removing evicted records for {Scope} '{Key}'", scope, key);
            await CompleteEvictionRemovalAsync(
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
    /// Entity-scoped variant of <see cref="PurgeEvictedLogEntriesAsync"/>. Rewrites nginx access.log
    /// files to drop entries belonging only to the specified entity's evicted downloads.
    /// Best-effort: failures are logged as warnings and do not block the DB delete.
    /// </summary>
    private async Task PurgeEvictedLogEntriesForEntityAsync(
        AppDbContext context,
        EvictionScope scope,
        string key,
        Guid operationId,
        CancellationToken stoppingToken)
    {
        // Npgsql cannot translate string.Equals(..., StringComparison.OrdinalIgnoreCase);
        // service names are stored lowercase, so lowercasing `key` here lets the
        // EvictionScope.Service branch use plain `==` in the LINQ.
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
                                 && d.Service == keyLower
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
        public RustProgressEventKind Event { get; set; } = RustProgressEventKind.Unknown;

        [System.Text.Json.Serialization.JsonPropertyName("percentComplete")]
        public double? PercentComplete { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("stageKey")]
        public string? StageKey { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public OperationStatus? Status { get; set; }
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
