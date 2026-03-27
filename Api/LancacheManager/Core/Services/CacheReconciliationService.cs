using System.Security.Cryptography;
using System.Text;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

/// <summary>
/// Background service that periodically reconciles Download records with actual cache files on disk.
/// Downloads whose cache files have been evicted by nginx are flagged as IsEvicted = true.
/// Downloads whose cache files reappear (re-cached) are un-flagged back to IsEvicted = false.
/// In "remove" mode, evicted records are deleted from the database entirely.
/// </summary>
public class CacheReconciliationService : ScopedScheduledBackgroundService
{
    private readonly DatasourceService _datasourceService;
    private readonly StateService _stateService;
    private readonly ISignalRNotificationService _notifications;
    private readonly IUnifiedOperationTracker _operationTracker;
    private bool _isRunning;

    protected override string ServiceName => "CacheReconciliationService";
    protected override TimeSpan Interval => TimeSpan.FromHours(6);
    protected override bool RunOnStartup => false;

    public bool IsRunning => _isRunning;

    /// <summary>
    /// Start reconciliation as a fire-and-forget background task.
    /// Returns the operationId immediately, or null if already running.
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
                await ReconcileCacheFilesAsync(context, operationId, cts.Token);
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
        IUnifiedOperationTracker operationTracker)
        : base(serviceProvider, logger, configuration)
    {
        _datasourceService = datasourceService;
        _stateService = stateService;
        _notifications = notifications;
        _operationTracker = operationTracker;
    }

    protected override async Task ExecuteScopedWorkAsync(
        IServiceProvider scopedServices,
        CancellationToken stoppingToken)
    {
        var context = scopedServices.GetRequiredService<AppDbContext>();

        var cts = new CancellationTokenSource();
        // Register a linked source so the scheduled cancellation token also cancels
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, cts.Token);
        var operationId = _operationTracker.RegisterOperation(
            OperationType.EvictionScan,
            "Eviction Scan",
            cts);

        await ReconcileCacheFilesAsync(context, operationId, linked.Token);
    }

    private async Task ReconcileCacheFilesAsync(AppDbContext context, string operationId, CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("[EvictionScan] Starting eviction scan");

            await _notifications.NotifyAllAsync(SignalREvents.EvictionScanStarted, new EvictionScanStarted(
                Message: "Starting eviction scan...",
                OperationId: operationId));

            // Step 1: Build a HashSet of all files currently on disk across all datasource cache directories
            var filesOnDisk = BuildDiskFileSet();

            if (filesOnDisk.Count == 0)
            {
                _logger.LogWarning("[EvictionScan] No cache files found on disk across any datasource - skipping reconciliation to prevent false eviction flags");
                _operationTracker.CompleteOperation(operationId, success: true);
                await _notifications.NotifyAllAsync(SignalREvents.EvictionScanComplete, new EvictionScanComplete(
                    Success: true,
                    OperationId: operationId,
                    Message: "No cache files found on disk — scan skipped to prevent false eviction flags.",
                    Processed: 0,
                    Evicted: 0,
                    UnEvicted: 0));
                return;
            }

            _logger.LogInformation("[EvictionScan] Found {FileCount} cache files on disk across {DatasourceCount} datasource(s)",
                filesOnDisk.Count, _datasourceService.DatasourceCount);

            // Auto-detect nginx cache directory levels from actual file paths on disk
            var (level1Size, level2Size) = DetectCacheLevels(filesOnDisk);

            // Auto-detect nginx slice range from cache file headers
            // When slice module is active (e.g., slice 1m), cache key = "$cacheidentifier$uri$slice_range"
            // When slice is 0 (disabled), cache key = "$cacheidentifier$uri"
            var firstSliceSuffix = DetectFirstSliceSuffix(filesOnDisk);

            // Estimate total for progress: count inactive downloads
            var totalEstimate = await context.Downloads.CountAsync(d => !d.IsActive, stoppingToken);

            // Step 2: Get all inactive (completed) Downloads that have associated LogEntries
            // Process in batches to avoid loading the entire table into memory
            const int batchSize = 100;
            var totalEvicted = 0;
            var totalUnEvicted = 0;
            var totalProcessed = 0;
            var lastProcessedId = 0L;

            while (!stoppingToken.IsCancellationRequested)
            {
                var downloads = await context.Downloads
                    .Where(d => !d.IsActive && d.Id > lastProcessedId)
                    .OrderBy(d => d.Id)
                    .Take(batchSize)
                    .ToListAsync(stoppingToken);

                if (downloads.Count == 0)
                    break;

                lastProcessedId = downloads[^1].Id;

                // Get LogEntries for these downloads in one query
                var downloadIds = downloads.Select(d => d.Id).ToList();
                var logEntriesByDownload = await context.LogEntries
                    .Where(le => le.DownloadId != null && downloadIds.Contains(le.DownloadId.Value))
                    .GroupBy(le => le.DownloadId!.Value)
                    .Select(g => new
                    {
                        DownloadId = g.Key,
                        Entries = g.Select(le => new { le.Service, le.Url, le.Datasource }).ToList()
                    })
                    .ToListAsync(stoppingToken);

                var logEntryLookup = logEntriesByDownload.ToDictionary(x => x.DownloadId, x => x.Entries);

                foreach (Download download in downloads)
                {
                    if (!logEntryLookup.TryGetValue(download.Id, out var entries) || entries.Count == 0)
                    {
                        // No log entries - cannot determine cache status, skip
                        continue;
                    }

                    // Check if any cache file for this download still exists on disk
                    var hasCacheFile = false;
                    foreach (var entry in entries)
                    {
                        if (string.IsNullOrEmpty(entry.Url) || string.IsNullOrEmpty(entry.Service))
                            continue;

                        // Resolve the cache directory for this entry's datasource
                        var datasource = _datasourceService.GetDatasource(entry.Datasource);
                        if (datasource == null)
                        {
                            datasource = _datasourceService.GetDefaultDatasource();
                        }
                        if (datasource == null)
                            continue;

                        // Try with detected slice suffix first (covers most files)
                        var cacheFilePath = ComputeCacheFilePath(datasource.CachePath, entry.Service, entry.Url, level1Size, level2Size, firstSliceSuffix);
                        if (filesOnDisk.Contains(cacheFilePath))
                        {
                            hasCacheFile = true;
                            break;
                        }

                        // Fallback: try the opposite variant (sliced vs non-sliced)
                        // This handles mixed configurations or detection edge cases
                        var fallbackSuffix = string.IsNullOrEmpty(firstSliceSuffix) ? "bytes=0-1048575" : "";
                        var fallbackPath = ComputeCacheFilePath(datasource.CachePath, entry.Service, entry.Url, level1Size, level2Size, fallbackSuffix);
                        if (filesOnDisk.Contains(fallbackPath))
                        {
                            hasCacheFile = true;
                            break;
                        }
                    }

                    if (!hasCacheFile && !download.IsEvicted)
                    {
                        download.IsEvicted = true;
                        totalEvicted++;
                    }
                    else if (hasCacheFile && download.IsEvicted)
                    {
                        download.IsEvicted = false;
                        totalUnEvicted++;
                    }
                }

                totalProcessed += downloads.Count;

                // Save changes for this batch
                await context.SaveChangesAsync(stoppingToken);

                // Emit progress after each batch
                var percent = totalEstimate > 0
                    ? Math.Min(100.0, totalProcessed / (double)totalEstimate * 100.0)
                    : 0.0;
                _operationTracker.UpdateProgress(operationId, percent,
                    $"Processed {totalProcessed} of ~{totalEstimate} downloads...");
                await _notifications.NotifyAllAsync(SignalREvents.EvictionScanProgress, new EvictionScanProgress(
                    OperationId: operationId,
                    Status: "running",
                    Message: $"Processed {totalProcessed} of ~{totalEstimate} downloads...",
                    PercentComplete: percent,
                    Processed: totalProcessed,
                    TotalEstimate: totalEstimate,
                    Evicted: totalEvicted,
                    UnEvicted: totalUnEvicted));

                // Small delay between batches to reduce pressure
                if (downloads.Count == batchSize)
                    await Task.Delay(50, stoppingToken);
            }

            _logger.LogInformation(
                "[EvictionScan] Scan complete: processed {Total} downloads, {Evicted} newly evicted, {UnEvicted} un-evicted (re-cached)",
                totalProcessed, totalEvicted, totalUnEvicted);

            // Step 3: Handle evicted data mode
            var evictedDataMode = _stateService.GetEvictedDataMode();
            if (evictedDataMode == EvictedDataModes.Remove)
            {
                await RemoveEvictedRecordsAsync(context, stoppingToken);
            }

            _operationTracker.CompleteOperation(operationId, success: true);
            await _notifications.NotifyAllAsync(SignalREvents.EvictionScanComplete, new EvictionScanComplete(
                Success: true,
                OperationId: operationId,
                Message: $"Scan complete: {totalProcessed} processed, {totalEvicted} newly evicted, {totalUnEvicted} un-evicted.",
                Processed: totalProcessed,
                Evicted: totalEvicted,
                UnEvicted: totalUnEvicted));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[EvictionScan] Operation {OperationId} was cancelled", operationId);
            _operationTracker.CompleteOperation(operationId, success: false, error: "Cancelled by user");
            await _notifications.NotifyAllAsync(SignalREvents.EvictionScanComplete, new EvictionScanComplete(
                Success: false,
                OperationId: operationId,
                Message: "Eviction scan was cancelled.",
                Processed: 0,
                Evicted: 0,
                UnEvicted: 0,
                Error: "Cancelled by user"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EvictionScan] Error during eviction scan");
            _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);
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

    /// <summary>
    /// Builds a HashSet of all file paths across all datasource cache directories.
    /// </summary>
    private HashSet<string> BuildDiskFileSet()
    {
        var filesOnDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var datasources = _datasourceService.GetDatasources();

        foreach (ResolvedDatasource datasource in datasources)
        {
            if (!Directory.Exists(datasource.CachePath))
            {
                _logger.LogDebug("[EvictionScan] Cache directory does not exist for datasource '{Name}': {Path}",
                    datasource.Name, datasource.CachePath);
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(datasource.CachePath, "*", SearchOption.AllDirectories))
                {
                    filesOnDisk.Add(file);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EvictionScan] Error enumerating cache files for datasource '{Name}': {Path}",
                    datasource.Name, datasource.CachePath);
            }
        }

        return filesOnDisk;
    }

    /// <summary>
    /// Auto-detects nginx cache directory levels by examining actual file paths on disk.
    /// Samples files and determines the directory component sizes (e.g., 1:2, 2:2).
    /// </summary>
    private (int level1Size, int level2Size) DetectCacheLevels(HashSet<string> filesOnDisk)
    {
        var datasources = _datasourceService.GetDatasources();

        foreach (var filePath in filesOnDisk.Take(20))
        {
            foreach (ResolvedDatasource datasource in datasources)
            {
                var cachePath = datasource.CachePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!filePath.StartsWith(cachePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var relative = filePath[cachePath.Length..];
                var parts = relative.Split(Path.DirectorySeparatorChar);

                // Expect exactly: level1dir/level2dir/hashfilename
                // MD5 hash is 32 hex characters
                if (parts.Length != 3 || parts[2].Length != 32)
                    continue;

                // Validate directory components are hex strings
                if (!IsHexString(parts[0]) || !IsHexString(parts[1]))
                    continue;

                var l1 = parts[0].Length;
                var l2 = parts[1].Length;
                _logger.LogInformation("[EvictionScan] Auto-detected cache levels: {L1}:{L2} from file structure (sample: {Path})",
                    l1, l2, filePath);
                return (l1, l2);
            }
        }

        _logger.LogWarning("[EvictionScan] Could not auto-detect cache levels from disk, using default 2:2");
        return (2, 2);
    }

    /// <summary>
    /// Auto-detects the nginx slice range by reading the KEY header from a sample cache file.
    /// nginx cache files contain a "KEY: " line with the actual cache key used.
    /// When the slice module is active, the key ends with e.g. "bytes=0-1048575".
    /// When slice is 0 (disabled), there is no bytes suffix.
    /// Returns the first-slice suffix (e.g., "bytes=0-1048575") or empty string if no slicing.
    /// </summary>
    private string DetectFirstSliceSuffix(HashSet<string> filesOnDisk)
    {
        foreach (var filePath in filesOnDisk.Take(20))
        {
            try
            {
                var buffer = new byte[4096];
                int bytesRead;
                using (var stream = File.OpenRead(filePath))
                {
                    bytesRead = stream.Read(buffer, 0, buffer.Length);
                }

                // Use Latin1 to preserve all byte values (binary header + text KEY line)
                var content = Encoding.Latin1.GetString(buffer, 0, bytesRead);

                var keyIndex = content.IndexOf("\nKEY: ", StringComparison.Ordinal);
                if (keyIndex < 0)
                    continue;

                var keyStart = keyIndex + 6; // length of "\nKEY: "
                var keyEnd = content.IndexOf('\n', keyStart);
                if (keyEnd < 0)
                    continue;

                var cacheKey = content[keyStart..keyEnd].Trim();

                // Check if the key contains a bytes= range suffix (slice module active)
                var bytesIdx = cacheKey.LastIndexOf("bytes=", StringComparison.Ordinal);
                if (bytesIdx >= 0)
                {
                    var rangeStr = cacheKey[(bytesIdx + 6)..]; // after "bytes="
                    var dashIndex = rangeStr.IndexOf('-');
                    if (dashIndex >= 0
                        && long.TryParse(rangeStr[..dashIndex], out var startByte)
                        && long.TryParse(rangeStr[(dashIndex + 1)..], out var endByte))
                    {
                        // Compute slice size from any slice, then derive the first-slice suffix
                        var sliceSize = endByte - startByte + 1;
                        var firstSliceSuffix = $"bytes=0-{sliceSize - 1}";
                        _logger.LogInformation(
                            "[EvictionScan] Auto-detected nginx slice range: {Suffix} (slice size: {Size} bytes)",
                            firstSliceSuffix, sliceSize);
                        return firstSliceSuffix;
                    }
                }

                // KEY found but no bytes= suffix → slice module is disabled (slice 0)
                _logger.LogInformation("[EvictionScan] No slice range detected in cache file header — nginx slice is disabled (slice 0)");
                return string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[EvictionScan] Could not read cache file header: {Path}", filePath);
            }
        }

        _logger.LogWarning("[EvictionScan] Could not detect slice range from any cache file, defaulting to no slice suffix");
        return string.Empty;
    }

    private static bool IsHexString(string value)
    {
        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c))
                return false;
        }
        return value.Length > 0;
    }

    /// <summary>
    /// Computes the expected nginx cache file path for a given service + URL combination.
    /// nginx proxy_cache_key = "$cacheidentifier$uri$slice_range", stored at cachePath/L1/L2/MD5HASH
    /// When slice module is active, sliceSuffix is e.g. "bytes=0-1048575" (first 1 MiB slice).
    /// When slice is disabled (0), sliceSuffix is empty.
    /// </summary>
    private static string ComputeCacheFilePath(string cachePath, string service, string url, int level1Size, int level2Size, string sliceSuffix)
    {
        var cacheKey = service + url + sliceSuffix;
        #pragma warning disable CA5351 // MD5 is required here to match nginx's cache file naming convention
        var hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(cacheKey));
        #pragma warning restore CA5351
        var hash = Convert.ToHexStringLower(hashBytes);

        var level1 = hash[^level1Size..];
        var level2 = hash[^(level1Size + level2Size)..^level1Size];

        return Path.Combine(cachePath, level1, level2, hash);
    }

    /// <summary>
    /// Deletes all evicted Download records and their associated LogEntries from the database.
    /// Called when evicted data mode is set to "remove".
    /// </summary>
    private async Task RemoveEvictedRecordsAsync(AppDbContext context, CancellationToken stoppingToken)
    {
        try
        {
            // Delete LogEntries for evicted downloads first (foreign key constraint)
            var logEntriesDeleted = await context.LogEntries
                .Where(le => le.DownloadId != null && le.Download != null && le.Download.IsEvicted)
                .ExecuteDeleteAsync(stoppingToken);

            // Delete evicted Downloads
            var downloadsDeleted = await context.Downloads
                .Where(d => d.IsEvicted)
                .ExecuteDeleteAsync(stoppingToken);

            if (downloadsDeleted > 0 || logEntriesDeleted > 0)
            {
                _logger.LogInformation(
                    "[EvictionScan] Remove mode: deleted {Downloads} evicted downloads and {LogEntries} associated log entries",
                    downloadsDeleted, logEntriesDeleted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EvictionScan] Error removing evicted records from database");
        }
    }
}
