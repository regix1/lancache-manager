using System.Security.Cryptography;
using System.Text;
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
    private bool _isRunning;

    protected override string ServiceName => "CacheReconciliationService";
    protected override TimeSpan Interval => TimeSpan.FromHours(6);
    protected override bool RunOnStartup => false;

    public bool IsRunning => _isRunning;

    /// <summary>
    /// Run reconciliation manually (called from API endpoint).
    /// Returns false if already running.
    /// </summary>
    public async Task<(bool started, int processed, int evicted, int unEvicted)> RunManualAsync(CancellationToken ct)
    {
        if (_isRunning) return (false, 0, 0, 0);
        _isRunning = true;
        try
        {
            using var scope = _serviceProvider.CreateScope();
            await ExecuteScopedWorkAsync(scope.ServiceProvider, ct);
            return (true, _lastProcessed, _lastEvicted, _lastUnEvicted);
        }
        finally
        {
            _isRunning = false;
        }
    }

    private int _lastProcessed, _lastEvicted, _lastUnEvicted;

    public CacheReconciliationService(
        IServiceProvider serviceProvider,
        ILogger<CacheReconciliationService> logger,
        IConfiguration configuration,
        DatasourceService datasourceService,
        StateService stateService)
        : base(serviceProvider, logger, configuration)
    {
        _datasourceService = datasourceService;
        _stateService = stateService;
    }

    protected override async Task ExecuteScopedWorkAsync(
        IServiceProvider scopedServices,
        CancellationToken stoppingToken)
    {
        var context = scopedServices.GetRequiredService<AppDbContext>();
        await ReconcileCacheFilesAsync(context, stoppingToken);
    }

    private async Task ReconcileCacheFilesAsync(AppDbContext context, CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("[CacheReconciliation] Starting cache reconciliation scan");

            // Step 1: Build a HashSet of all files currently on disk across all datasource cache directories
            var filesOnDisk = BuildDiskFileSet();

            if (filesOnDisk.Count == 0)
            {
                _logger.LogWarning("[CacheReconciliation] No cache files found on disk across any datasource - skipping reconciliation to prevent false eviction flags");
                return;
            }

            _logger.LogInformation("[CacheReconciliation] Found {FileCount} cache files on disk across {DatasourceCount} datasource(s)",
                filesOnDisk.Count, _datasourceService.DatasourceCount);

            // Auto-detect nginx cache directory levels from actual file paths on disk
            var (level1Size, level2Size) = DetectCacheLevels(filesOnDisk);

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

                        var cacheFilePath = ComputeCacheFilePath(datasource.CachePath, entry.Service, entry.Url, level1Size, level2Size);
                        if (filesOnDisk.Contains(cacheFilePath))
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

                // Small delay between batches to reduce pressure
                if (downloads.Count == batchSize)
                    await Task.Delay(50, stoppingToken);
            }

            _lastProcessed = totalProcessed;
            _lastEvicted = totalEvicted;
            _lastUnEvicted = totalUnEvicted;

            _logger.LogInformation(
                "[CacheReconciliation] Reconciliation complete: processed {Total} downloads, {Evicted} newly evicted, {UnEvicted} un-evicted (re-cached)",
                totalProcessed, totalEvicted, totalUnEvicted);

            // Step 3: Handle evicted data mode
            var evictedDataMode = _stateService.GetEvictedDataMode();
            if (evictedDataMode == EvictedDataModes.Remove)
            {
                await RemoveEvictedRecordsAsync(context, stoppingToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[CacheReconciliation] Error during cache reconciliation");
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
                _logger.LogDebug("[CacheReconciliation] Cache directory does not exist for datasource '{Name}': {Path}",
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
                _logger.LogWarning(ex, "[CacheReconciliation] Error enumerating cache files for datasource '{Name}': {Path}",
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
                _logger.LogInformation("[CacheReconciliation] Auto-detected cache levels: {L1}:{L2} from file structure (sample: {Path})",
                    l1, l2, filePath);
                return (l1, l2);
            }
        }

        _logger.LogWarning("[CacheReconciliation] Could not auto-detect cache levels from disk, using default 2:2");
        return (2, 2);
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
    /// nginx proxy_cache_key = "$cacheidentifier$uri", stored at cachePath/L1/L2/MD5HASH
    /// where L1 and L2 directory sizes are determined by the nginx levels configuration.
    /// </summary>
    private static string ComputeCacheFilePath(string cachePath, string service, string url, int level1Size, int level2Size)
    {
        var cacheKey = service + url;
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
                    "[CacheReconciliation] Remove mode: deleted {Downloads} evicted downloads and {LogEntries} associated log entries",
                    downloadsDeleted, logEntriesDeleted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CacheReconciliation] Error removing evicted records from database");
        }
    }
}
