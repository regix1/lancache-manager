using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Extensions;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.Base;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

public class DownloadCleanupService : ScopedScheduledBackgroundService
{
    private readonly CacheManagementService _cacheManagementService;
    private readonly DatasourceService _datasourceService;
    private readonly IStateService _stateService;

    protected override string ServiceName => "DownloadCleanupService";
    protected override TimeSpan StartupDelay => TimeSpan.Zero;
    protected override TimeSpan Interval => TimeSpan.FromSeconds(10);
    public override bool DefaultRunOnStartup => true;

    public DownloadCleanupService(
        IServiceProvider serviceProvider,
        ILogger<DownloadCleanupService> logger,
        IConfiguration configuration,
        CacheManagementService cacheManagementService,
        DatasourceService datasourceService,
        IStateService stateService)
        : base(serviceProvider, logger, configuration)
    {
        _cacheManagementService = cacheManagementService;
        _datasourceService = datasourceService;
        _stateService = stateService;
    }

    protected override async Task OnStartupAsync(CancellationToken stoppingToken)
    {
        // Wait for setup to complete so the database is configured
        await _stateService.WaitForSetupCompletedAsync(stoppingToken);

        using var scopedDb = _serviceProvider.CreateScopedDbContext();
        await InitialCleanupAsync(scopedDb.DbContext, stoppingToken);
    }

    protected override async Task ExecuteWorkAsync(
        IServiceProvider scopedServices,
        CancellationToken stoppingToken)
    {
        var context = scopedServices.GetRequiredService<AppDbContext>();
        await CleanupStaleDownloadsAsync(context, stoppingToken);
    }

    private async Task CleanupStaleDownloadsAsync(AppDbContext context, CancellationToken stoppingToken)
    {
        // Use 15-second timeout - if no new data in 15 seconds, download is complete
        // Reduced from 30 seconds for faster completion detection
        var cutoff = DateTime.UtcNow.AddSeconds(-15);

        // 60-second timeout for downloads that were never updated (EndTimeUtc still default)
        var staleCutoff = DateTime.UtcNow.AddSeconds(-60);

        // Process in smaller batches to avoid long locks (important when Rust processor is running)
        const int batchSize = 10;
        var totalUpdated = 0;

        while (true)
        {
            var staleDownloads = await context.Downloads
                .Where(d => d.IsActive &&
                    ((d.EndTimeUtc != default(DateTime) && d.EndTimeUtc < cutoff) ||  // Normal completion check
                     (d.EndTimeUtc == default(DateTime) && d.StartTimeUtc < staleCutoff))) // Never updated check
                .Take(batchSize)
                .ToListAsync(stoppingToken);

            if (!staleDownloads.Any())
                break;

            foreach (var download in staleDownloads)
            {
                download.IsActive = false;
            }

            await context.SaveChangesAsync(stoppingToken);
            totalUpdated += staleDownloads.Count;

            // Small delay between batches to allow other operations
            if (staleDownloads.Count == batchSize)
                await Task.Delay(50, stoppingToken);
        }

        if (totalUpdated > 0)
        {
            _logger.LogInformation("Marked {Count} downloads as complete (EndTime > 15 seconds old or never updated)", totalUpdated);
        }
    }

    private async Task InitialCleanupAsync(AppDbContext context, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Running initial database cleanup...");

        try
        {
            // Fix App 0 entries - mark them as inactive so they don't show up
            _logger.LogInformation("Checking for App 0 downloads...");
            var app0Downloads = await context.Downloads
                .Where(d => d.GameAppId == 0)
                .ToListAsync(stoppingToken);

            _logger.LogInformation("Found {Count} App 0 downloads", app0Downloads.Count);

            if (app0Downloads.Any())
            {
                foreach (var download in app0Downloads)
                {
                    download.IsActive = false;
                }
                await context.SaveChangesAsync(stoppingToken);
                _logger.LogInformation("Marked {Count} 'App 0' downloads as inactive", app0Downloads.Count);
            }
            else
            {
                _logger.LogInformation("No App 0 downloads found to fix");
            }

            // Mark stale downloads as complete on startup (downloads older than 15 seconds)
            // Also catch downloads that were never updated (EndTimeUtc still default)
            _logger.LogInformation("Checking for stale active downloads...");
            var cutoff = DateTime.UtcNow.AddSeconds(-15);
            var staleCutoff = DateTime.UtcNow.AddSeconds(-60);
            var staleDownloads = await context.Downloads
                .Where(d => d.IsActive &&
                    ((d.EndTimeUtc != default(DateTime) && d.EndTimeUtc < cutoff) ||  // Normal completion check
                     (d.EndTimeUtc == default(DateTime) && d.StartTimeUtc < staleCutoff))) // Never updated check
                .ToListAsync(stoppingToken);

            _logger.LogInformation("Found {Count} stale active downloads", staleDownloads.Count);

            if (staleDownloads.Any())
            {
                foreach (var download in staleDownloads)
                {
                    download.IsActive = false;
                }
                await context.SaveChangesAsync(stoppingToken);
                _logger.LogInformation("Marked {Count} stale downloads as complete", staleDownloads.Count);
            }

            // Note: Image URL backfilling is now handled automatically by PICS during incremental scans
            // No manual cleanup needed - PICS fills in missing GameImageUrl fields when processing downloads

            // Fix service name aliases that don't match lancache nginx cache identifiers
            // The log processor previously normalized "epicgames" → "epic", but nginx cache keys use "epicgames"
            await NormalizeServicesAsync(context);

            // Normalize datasource mappings - fix inconsistent case and missing datasources
            await NormalizeDatasourceMappingsAsync(context, stoppingToken);

            // Repair rows whose game identity columns hold "" instead of an absent value
            await NormalizeEmptyGameIdentitiesAsync(context, stoppingToken);

            // Clean up orphaned services (services in DB but not in log files)
            await CleanupOrphanedServicesAsync(context, stoppingToken);

            _logger.LogInformation("Initial database cleanup complete");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during initial cleanup");
        }
    }

    /// <summary>
    /// Renames service name aliases in LogEntries and Downloads to match lancache nginx cache identifiers.
    /// The Rust log processor previously aliased "epicgames" → "epic", but nginx uses "epicgames" in its
    /// proxy_cache_key ($cacheidentifier$uri), causing cache file lookups to fail with wrong MD5 hashes.
    /// </summary>
    private async Task NormalizeServicesAsync(AppDbContext context)
    {
        // Map of old (incorrect) names → correct nginx cache identifier names
        var serviceRenames = new Dictionary<string, string>
        {
            { "epic", "epicgames" }
        };

        foreach (var (oldName, newName) in serviceRenames)
        {
            var logEntriesUpdated = await context.Database.ExecuteSqlRawAsync(
                "UPDATE \"LogEntries\" SET \"Service\" = {0} WHERE \"Service\" = {1}",
                newName, oldName);

            var downloadsUpdated = await context.Database.ExecuteSqlRawAsync(
                "UPDATE \"Downloads\" SET \"Service\" = {0} WHERE \"Service\" = {1}",
                newName, oldName);

            if (logEntriesUpdated > 0 || downloadsUpdated > 0)
            {
                _logger.LogInformation("Renamed service '{OldName}' → '{NewName}': {LogEntries} log entries, {Downloads} downloads",
                    oldName, newName, logEntriesUpdated, downloadsUpdated);
            }
        }
    }

    /// <summary>
    /// Services whose cache log entries are written under a DIFFERENT Service name than their
    /// Downloads. Xbox downloads are tagged <c>Service='xbox'</c> but their cache (and therefore
    /// their LogEntries) live under the raw tag the traffic arrived with, because cache files key on
    /// (service, url): <c>'wsus'</c> for Delivery Optimization clients and <c>'xboxlive'</c> for
    /// prefill-daemon pulls. Either one proves the cache is in use, and a box that prefills carries
    /// almost all of its Xbox traffic under <c>'xboxlive'</c> with only an aging <c>'wsus'</c> tail,
    /// so keying on one alias loses every Xbox download the moment the other rotates out of the logs.
    /// Such a service never appears in the log-file service names under its own name, so it must NOT
    /// be treated as orphaned - doing so would delete every Xbox download (a real named service).
    /// Maps <c>Downloads.Service</c> -&gt; the LogEntries/cache Service aliases that prove it is in use.
    /// </summary>
    private static readonly Dictionary<string, string[]> _cacheSplitServiceAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["xbox"] = ["wsus", "xboxlive"]
        };

    /// <summary>
    /// Reports database services absent from the current log scan without deleting retained
    /// downloads or log observations. Returns 0 when no candidate exists or the diagnostic pass
    /// fails, because this remains one best-effort step in the periodic cleanup cycle.
    /// </summary>
    private async Task<int> CleanupOrphanedServicesAsync(AppDbContext context, CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Checking for orphaned services in database...");

            // Get services that exist in log files (from Rust log_manager)
            var logServices = await _cacheManagementService.GetServiceLogCountsAsync(forceRefresh: false, stoppingToken);
            var logServiceNames = logServices.Keys.Select(s => s.ToLowerInvariant()).ToHashSet();

            _logger.LogInformation("Found {Count} services in log files: {Services}",
                logServiceNames.Count, string.Join(", ", logServiceNames.Take(10)));

            // SAFETY CHECK: If no services found in logs, don't delete anything
            // This prevents accidentally wiping all data if log scanning fails
            if (logServiceNames.Count == 0)
            {
                _logger.LogWarning("No services found in log files - skipping orphaned service cleanup to prevent accidental data loss");
                return 0;
            }

            return await CleanupOrphanedServicesCoreAsync(context, logServiceNames, _logger, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up orphaned services");
            return 0;
        }
    }

    /// <summary>
    /// Detects services absent from the current log scan without changing retained observation
    /// history. Returns the number of diagnostic orphan candidates.
    /// </summary>
    internal static async Task<int> CleanupOrphanedServicesCoreAsync(
        AppDbContext context,
        IReadOnlySet<string> logServiceNames,
        ILogger logger,
        CancellationToken stoppingToken)
    {
        // Downloads retain retry URLs and attribution after log files rotate or partial physical
        // work completes, so a missing current log name is diagnostic evidence only.
        var dbServices = await context.Downloads
            .Select(d => d.Service.ToLower())
            .Distinct()
            .ToListAsync(stoppingToken);

        logger.LogInformation("Found {Count} services in database: {Services}",
            dbServices.Count, string.Join(", ", dbServices.Take(10)));

        var orphanedServices = ComputeOrphanedServices(dbServices, logServiceNames);

        if (orphanedServices.Count == 0)
        {
            logger.LogInformation("No orphaned services found");
            return 0;
        }

        logger.LogWarning("Found {Count} services absent from current logs; retained their observation history: {Services}",
            orphanedServices.Count, string.Join(", ", orphanedServices));
        return orphanedServices.Count;
    }

    /// <summary>
    /// Returns the DB services that are absent from the log-file service names and therefore orphaned.
    /// A service is considered present (not orphaned) when its own name OR any of its cache-split
    /// aliases (see <see cref="_cacheSplitServiceAliases"/>) appears in the log services, so
    /// cache-split identities such as Xbox (cache stored under 'wsus' or 'xboxlive') are never
    /// wrongly flagged and deleted.
    /// </summary>
    internal static List<string> ComputeOrphanedServices(
        IEnumerable<string> dbServices,
        IReadOnlySet<string> logServiceNames)
    {
        return dbServices
            .Where(service => !IsServicePresentInLogs(service, logServiceNames))
            .ToList();
    }

    private static bool IsServicePresentInLogs(string service, IReadOnlySet<string> logServiceNames)
    {
        var serviceLower = service.ToLowerInvariant();

        if (logServiceNames.Contains(serviceLower))
        {
            return true;
        }

        // Cache-split service: present if any Service its cache lives under is present in the logs.
        return _cacheSplitServiceAliases.TryGetValue(serviceLower, out var cacheAliases)
            && cacheAliases.Any(alias => logServiceNames.Contains(alias.ToLowerInvariant()));
    }

    /// <summary>
    /// Normalizes datasource mappings for downloads and log history.
    /// Fixes issues like:
    /// - Null or empty datasource values
    /// - Inconsistent casing (e.g., "default" vs "Default")
    /// Unknown names are retained because disabled and retired datasource history stays readable.
    /// Returns 0 both when nothing needed normalizing AND when the pass fails (logged as an
    /// error) - this is one best-effort pass among the periodic cleanup cycle's several
    /// independent steps, so a failure here does not abort the others.
    /// Null/empty download values use the default. Configured names in both tables use the
    /// configured casing so ingestion and cleanup share one exact datasource identity.
    /// </summary>
    private async Task<int> NormalizeDatasourceMappingsAsync(AppDbContext context, CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Checking for datasource mapping inconsistencies...");

            var datasources = _datasourceService.GetDatasources();
            var defaultDatasource = _datasourceService.GetDefaultDatasource();

            if (defaultDatasource == null)
            {
                _logger.LogWarning("No default datasource configured - skipping datasource normalization");
                return 0;
            }

            var defaultName = defaultDatasource.Name;
            var validNames = datasources.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            _logger.LogInformation("Valid datasources: {Datasources}, Default: {Default}",
                string.Join(", ", validNames), defaultName);

            var totalUpdated = await NormalizeDatasourceMappingsCoreAsync(
                context,
                validNames,
                defaultName,
                _logger,
                stoppingToken);

            if (totalUpdated > 0)
            {
                _logger.LogInformation("Datasource normalization complete: updated {Total} rows", totalUpdated);
            }
            else
            {
                _logger.LogInformation("No datasource normalization needed - all mappings are valid");
            }

            return totalUpdated;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error normalizing datasource mappings");
            return 0;
        }
    }

    internal static async Task<int> NormalizeDatasourceMappingsCoreAsync(
        AppDbContext context,
        IReadOnlyCollection<string> validNames,
        string defaultName,
        ILogger logger,
        CancellationToken stoppingToken)
    {
        var execution = context.Database.CreateExecutionStrategy();
        return await execution.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(stoppingToken);

            await context.Database.ExecuteSqlRawAsync(
                "LOCK TABLE \"Downloads\" IN SHARE ROW EXCLUSIVE MODE",
                stoppingToken);
            await context.Database.ExecuteSqlRawAsync(
                "LOCK TABLE \"LogEntries\" IN SHARE ROW EXCLUSIVE MODE",
                stoppingToken);

            var downloadNames = await context.Downloads
                .Select(download => download.Datasource)
                .Distinct()
                .ToListAsync(stoppingToken);
            var logNames = await context.LogEntries
                .Select(entry => entry.Datasource)
                .Distinct()
                .ToListAsync(stoppingToken);
            var updated = 0;

            foreach (var currentName in downloadNames)
            {
                var normalizedName = NormalizeDatasourceName(currentName, validNames, defaultName);
                if (string.Equals(currentName, normalizedName, StringComparison.Ordinal))
                {
                    continue;
                }

                var downloadCount = string.IsNullOrEmpty(currentName)
                    ? await context.Downloads
                        .Where(download => download.Datasource == null || download.Datasource == "")
                        .ExecuteUpdateAsync(
                            setters => setters.SetProperty(download => download.Datasource, normalizedName),
                            stoppingToken)
                    : await context.Downloads
                        .Where(download => download.Datasource == currentName)
                        .ExecuteUpdateAsync(
                            setters => setters.SetProperty(download => download.Datasource, normalizedName),
                            stoppingToken);

                updated += downloadCount;
                logger.LogInformation(
                    "Normalized {Count} downloads to datasource {Datasource}",
                    downloadCount,
                    normalizedName);
            }

            foreach (var currentName in logNames)
            {
                if (string.IsNullOrEmpty(currentName))
                {
                    continue;
                }

                var normalizedName = validNames.FirstOrDefault(
                    name => name.Equals(currentName, StringComparison.OrdinalIgnoreCase));
                if (normalizedName == null || string.Equals(currentName, normalizedName, StringComparison.Ordinal))
                {
                    continue;
                }

                var logCount = await context.LogEntries
                    .Where(entry => entry.Datasource == currentName)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(entry => entry.Datasource, normalizedName),
                        stoppingToken);

                updated += logCount;
                logger.LogInformation(
                    "Normalized {Count} log entries to datasource {Datasource}",
                    logCount,
                    normalizedName);
            }

            await transaction.CommitAsync(stoppingToken);
            return updated;
        });
    }

    internal static string NormalizeDatasourceName(
        string? datasourceName,
        IReadOnlyCollection<string> validNames,
        string defaultName)
    {
        if (string.IsNullOrEmpty(datasourceName))
        {
            return defaultName;
        }

        return validNames.FirstOrDefault(
            name => name.Equals(datasourceName, StringComparison.OrdinalIgnoreCase)) ?? datasourceName;
    }

    /// <summary>
    /// Repairs rows whose game identity columns hold the empty string instead of an absent value.
    /// An empty <c>EpicAppId</c> or <c>GameName</c> is neither a real value nor an absent one, and
    /// the two halves of the codebase read it differently: <c>GamesOnDiskCalculator</c>'s
    /// <c>GetDownloadGameKey</c> buckets such a row as <c>steam:0</c>, while the detection,
    /// eviction and metrics queries test the same columns against null and bucket it as Epic or as
    /// a named game. Distinct games then collapse into one grouping key and the row can drop out
    /// of the per-game figures entirely.
    /// The writers that produced these values are guarded, so only rows already on disk are left.
    /// Returns 0 both when nothing needed repairing AND when the pass fails (logged as an
    /// error) - this is one best-effort pass among the initial cleanup's several independent
    /// steps, so a failure here does not abort the others.
    /// </summary>
    private async Task<int> NormalizeEmptyGameIdentitiesAsync(AppDbContext context, CancellationToken stoppingToken)
    {
        try
        {
            return await NormalizeEmptyGameIdentitiesCoreAsync(context, _logger, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error repairing empty game identity values");
            return 0;
        }
    }

    /// <summary>
    /// Repairs the empty identity values in place and returns the number of rows changed.
    /// Every step is set-based on purpose: the Downloads table has no age-based retention and
    /// grows without bound, so materializing candidates to assign a field is not affordable on a
    /// box this size. On a clean database the whole pass is five statements that match no rows.
    /// Extracted as an internal seam so the repair can be unit tested directly. Exceptions
    /// propagate to the caller.
    /// </summary>
    internal static async Task<int> NormalizeEmptyGameIdentitiesCoreAsync(
        AppDbContext context,
        ILogger logger,
        CancellationToken stoppingToken)
    {
        // A download with an empty Epic id is already offered for re-resolution (EpicMappingService
        // tests IsNullOrEmpty), but every layer that means "not an Epic download" tests the column
        // against null, so the same row counts as Epic there at the same time. Null is the only
        // value both halves read the same way.
        var downloadEpicIdsCleared = await context.Downloads
            .Where(d => d.EpicAppId == "")
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.EpicAppId, (string?)null), stoppingToken);

        // An empty GameName does not recover on its own: the Xbox re-resolution query selects
        // candidates on GameName == null, so a row holding "" is never offered again and keeps no
        // name forever. It also keys as steam:0 rather than on its own service, which is the
        // collapse GetDownloadGameKey's "" test exists to avoid.
        var downloadNamesCleared = await context.Downloads
            .Where(d => d.GameName == "")
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.GameName, (string?)null), stoppingToken);

        // Same reasoning on the detection side. Moving a row from (GameAppId, "") to
        // (GameAppId, null) cannot violate IX_CachedGameDetection_GameAppId_EpicAppId: the index
        // is nulls-distinct, so every repaired row lands on a key no other row can equal.
        var detectionEpicIdsCleared = await context.CachedGameDetections
            .Where(g => g.EpicAppId == "")
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.EpicAppId, (string?)null), stoppingToken);

        // CachedGameDetections.GameName is NOT NULL, so there is no absent value to normalize to.
        // A row with no name, no Steam app id and no Epic id names nothing that can be displayed or
        // matched, yet it still contributes app id 0 to the Steam un-evict arm, so any download of
        // app 0 flips its badge. Removing it lets a full scan re-insert the game with its name;
        // keeping it only preserves a row nothing can address. Runs after the Epic id step above so
        // a row that held both empty values is caught in the same pass. Rows that still carry a
        // real identity are left alone - they key correctly and the scan refills the name.
        var namelessDetectionsRemoved = await context.CachedGameDetections
            .Where(g => g.GameName == "" && g.EpicAppId == null && g.GameAppId == 0)
            .ExecuteDeleteAsync(stoppingToken);

        // An Epic CDN pattern with no app id is excluded from resolution, but it is not inert:
        // IX_EpicCdnPatterns_ChunkBaseUrl is unique and MergeCdnPatternsAsync keys the patterns it
        // already has by ChunkBaseUrl, taking the update branch (which never writes AppId) when one
        // is present. The empty row therefore blocks its own chunk URL from ever being recorded
        // with a real app id. Removing it lets the next merge insert the correct pattern.
        var emptyCdnPatternsRemoved = await context.EpicCdnPatterns
            .Where(p => p.AppId == "")
            .ExecuteDeleteAsync(stoppingToken);

        var total = downloadEpicIdsCleared
            + downloadNamesCleared
            + detectionEpicIdsCleared
            + namelessDetectionsRemoved
            + emptyCdnPatternsRemoved;

        if (total > 0)
        {
            logger.LogInformation(
                "Repaired empty game identity values: {DownloadEpicIds} download Epic ids, {DownloadNames} download names, {DetectionEpicIds} detection Epic ids cleared; {NamelessDetections} nameless detections and {CdnPatterns} Epic CDN patterns removed",
                downloadEpicIdsCleared, downloadNamesCleared, detectionEpicIdsCleared,
                namelessDetectionsRemoved, emptyCdnPatternsRemoved);
        }

        return total;
    }
}
