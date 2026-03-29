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
    private bool _isRunning;
    private bool _currentScanIsSilent = true;

    protected override string ServiceName => "CacheReconciliationService";
    protected override TimeSpan Interval => TimeSpan.FromHours(6);
    protected override bool RunOnStartup => true;

    public bool IsRunning => _isRunning;
    public bool CurrentScanIsSilent => _currentScanIsSilent;

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
        RustProcessHelper rustProcessHelper)
        : base(serviceProvider, logger, configuration)
    {
        _datasourceService = datasourceService;
        _stateService = stateService;
        _notifications = notifications;
        _operationTracker = operationTracker;
        _rustProcessHelper = rustProcessHelper;
    }

    protected override async Task OnStartupAsync(CancellationToken stoppingToken)
    {
        var silent = !_stateService.GetEvictionScanNotifications();

        _isRunning = true;
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

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
        }
    }

    protected override async Task ExecuteScopedWorkAsync(
        IServiceProvider scopedServices,
        CancellationToken stoppingToken)
    {
        var silent = !_stateService.GetEvictionScanNotifications();
        var context = scopedServices.GetRequiredService<AppDbContext>();

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
                    Message: "Starting eviction scan...",
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
                            Message: progress.Message,
                            PercentComplete: progress.PercentComplete,
                            Processed: progress.Processed,
                            TotalEstimate: progress.TotalEstimate,
                            Evicted: progress.Evicted,
                            UnEvicted: progress.UnEvicted));
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

                // Handle evicted data "remove" mode
                var evictedDataMode = _stateService.GetEvictedDataMode();
                if (evictedDataMode == EvictedDataModes.Remove)
                {
                    await RemoveEvictedRecordsAsync(context, stoppingToken);
                }

                _operationTracker.CompleteOperation(operationId, success: true);
                if (!silent)
                {
                    await _notifications.NotifyAllAsync(SignalREvents.EvictionScanComplete, new EvictionScanComplete(
                        Success: true,
                        OperationId: operationId,
                        Message: $"Scan complete: {scanResult.Processed} processed, {scanResult.Evicted} newly evicted, {scanResult.UnEvicted} un-evicted.",
                        Processed: scanResult.Processed,
                        Evicted: scanResult.Evicted,
                        UnEvicted: scanResult.UnEvicted));
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
                        Message: "Eviction scan failed with an error.",
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
                    Message: "Eviction scan was cancelled.",
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
                    Message: "Eviction scan failed with an error.",
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
                new EvictionRemovalStarted("Removing evicted records from database...", operationId));
        }

        try
        {
            // Step 1: Delete LogEntries for evicted downloads first (foreign key constraint)
            _operationTracker.UpdateProgress(operationId, 0, "Removing associated log entries...");
            await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalProgress,
                new EvictionRemovalProgress(operationId, "removing_log_entries", "Removing associated log entries...", 0, 0, 0));

            var logEntriesDeleted = await context.LogEntries
                .Where(le => le.DownloadId != null && le.Download != null && le.Download.IsEvicted)
                .ExecuteDeleteAsync(stoppingToken);

            // Step 2: Delete evicted Downloads
            _operationTracker.UpdateProgress(operationId, 50, "Removing evicted download records...");
            await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalProgress,
                new EvictionRemovalProgress(operationId, "removing_downloads", "Removing evicted download records...", 50, 0, logEntriesDeleted));

            var downloadsDeleted = await context.Downloads
                .Where(d => d.IsEvicted)
                .ExecuteDeleteAsync(stoppingToken);

            if (downloadsDeleted > 0 || logEntriesDeleted > 0)
            {
                _logger.LogInformation(
                    "[EvictionScan] Remove mode: deleted {Downloads} evicted downloads and {LogEntries} associated log entries",
                    downloadsDeleted, logEntriesDeleted);
            }

            _operationTracker.UpdateProgress(operationId, 100, "Eviction removal complete.");
            _operationTracker.CompleteOperation(operationId, success: true);
            await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalComplete,
                new EvictionRemovalComplete(true, operationId, "Eviction removal complete.", downloadsDeleted, logEntriesDeleted));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EvictionScan] Error removing evicted records from database");
            _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);
            await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalComplete,
                new EvictionRemovalComplete(false, operationId, "Eviction removal failed.", 0, 0, ex.Message));
        }
        finally
        {
            cts?.Dispose();
        }
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
