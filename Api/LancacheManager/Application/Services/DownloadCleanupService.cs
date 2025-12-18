using LancacheManager.Data;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Application.Services;

public class DownloadCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DownloadCleanupService> _logger;
    private readonly CacheManagementService _cacheManagementService;
    private readonly DatasourceService _datasourceService;

    public DownloadCleanupService(
        IServiceProvider serviceProvider,
        ILogger<DownloadCleanupService> logger,
        CacheManagementService cacheManagementService,
        DatasourceService datasourceService)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _cacheManagementService = cacheManagementService;
        _datasourceService = datasourceService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for app to start and database to be ready
        await Task.Delay(10000, stoppingToken);

        _logger.LogInformation("DownloadCleanupService started");

        // Run initial cleanup immediately on first start
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PerformInitialCleanup(context, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run initial cleanup");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // Use 15-second timeout - if no new data in 15 seconds, download is complete
                // Reduced from 30 seconds for faster completion detection
                var cutoff = DateTime.UtcNow.AddSeconds(-15);

                // Process in smaller batches to avoid long locks (important when Rust processor is running)
                const int batchSize = 10;
                var totalUpdated = 0;

                while (true)
                {
                    var staleDownloads = await context.Downloads
                        .Where(d => d.IsActive && d.EndTimeUtc < cutoff)
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
                    _logger.LogInformation($"Marked {totalUpdated} downloads as complete (EndTime > 15 seconds old)");

                    // Invalidate the active downloads cache so UI sees updated data immediately
                    var statsCache = scope.ServiceProvider.GetRequiredService<StatsCache>();
                    statsCache.InvalidateDownloads();
                    _logger.LogInformation("Invalidated active downloads cache");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in cleanup service");
            }

            // Run every 10 seconds (reduced from 30 for faster completion detection)
            await Task.Delay(10000, stoppingToken);
        }
    }

    private async Task PerformInitialCleanup(AppDbContext context, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Running initial database cleanup...");

        var needsCacheInvalidation = false;

        try
        {
            // Fix App 0 entries - mark them as inactive so they don't show up
            _logger.LogInformation("Checking for App 0 downloads...");
            var app0Downloads = await context.Downloads
                .Where(d => d.GameAppId == 0)
                .ToListAsync(stoppingToken);

            _logger.LogInformation($"Found {app0Downloads.Count} App 0 downloads");

            if (app0Downloads.Any())
            {
                foreach (var download in app0Downloads)
                {
                    download.IsActive = false;
                }
                await context.SaveChangesAsync(stoppingToken);
                _logger.LogInformation($"Marked {app0Downloads.Count} 'App 0' downloads as inactive");
                needsCacheInvalidation = true;
            }
            else
            {
                _logger.LogInformation("No App 0 downloads found to fix");
            }

            // Mark stale downloads as complete on startup (downloads older than 15 seconds)
            _logger.LogInformation("Checking for stale active downloads...");
            var cutoff = DateTime.UtcNow.AddSeconds(-15);
            var staleDownloads = await context.Downloads
                .Where(d => d.IsActive && d.EndTimeUtc < cutoff)
                .ToListAsync(stoppingToken);

            _logger.LogInformation($"Found {staleDownloads.Count} stale active downloads");

            if (staleDownloads.Any())
            {
                foreach (var download in staleDownloads)
                {
                    download.IsActive = false;
                }
                await context.SaveChangesAsync(stoppingToken);
                _logger.LogInformation($"Marked {staleDownloads.Count} stale downloads as complete");
                needsCacheInvalidation = true;
            }

            // Note: Image URL backfilling is now handled automatically by PICS during incremental scans
            // No manual cleanup needed - PICS fills in missing GameImageUrl fields when processing downloads

            // Normalize datasource mappings - fix inconsistent case and missing datasources
            var datasourcesNormalized = await NormalizeDatasourceMappingsAsync(context, stoppingToken);
            if (datasourcesNormalized > 0)
            {
                needsCacheInvalidation = true;
            }

            // Clean up orphaned services (services in DB but not in log files)
            var orphanedServicesRemoved = await CleanupOrphanedServicesAsync(context, stoppingToken);
            if (orphanedServicesRemoved > 0)
            {
                needsCacheInvalidation = true;
            }

            // Invalidate cache if we made any changes
            if (needsCacheInvalidation)
            {
                using var scope = _serviceProvider.CreateScope();
                var statsCache = scope.ServiceProvider.GetRequiredService<StatsCache>();
                statsCache.InvalidateDownloads();
                _logger.LogInformation("Invalidated downloads cache after cleanup - missing data will be backfilled during next PICS crawl");
            }

            _logger.LogInformation("Initial database cleanup complete");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during initial cleanup");
        }
    }

    /// <summary>
    /// Removes database records for services that no longer exist in log files.
    /// This cleans up orphaned data left behind when log entries were removed but database wasn't updated.
    /// </summary>
    private async Task<int> CleanupOrphanedServicesAsync(AppDbContext context, CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Checking for orphaned services in database...");

            // Get services that exist in log files (from Rust log_manager)
            var logServices = await _cacheManagementService.GetServiceLogCounts(forceRefresh: false, stoppingToken);
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

            // Get unique services from Downloads table
            var dbServices = await context.Downloads
                .Select(d => d.Service.ToLower())
                .Distinct()
                .ToListAsync(stoppingToken);

            _logger.LogInformation("Found {Count} services in database: {Services}",
                dbServices.Count, string.Join(", ", dbServices.Take(10)));

            // SAFETY CHECK: Don't delete if most services would be removed
            // This protects against edge cases where log scanning returns partial results
            var orphanedServices = dbServices
                .Where(s => !logServiceNames.Contains(s.ToLowerInvariant()))
                .ToList();

            if (dbServices.Count > 0 && orphanedServices.Count >= dbServices.Count)
            {
                _logger.LogWarning("All {Count} database services would be marked as orphaned - this looks like a log scanning issue. Skipping cleanup.", dbServices.Count);
                return 0;
            }

            if (!orphanedServices.Any())
            {
                _logger.LogInformation("No orphaned services found");
                return 0;
            }

            _logger.LogInformation("Found {Count} orphaned services to clean up: {Services}",
                orphanedServices.Count, string.Join(", ", orphanedServices));

            var totalDeleted = 0;

            foreach (var service in orphanedServices)
            {
                var serviceLower = service.ToLowerInvariant();

                // Delete LogEntries first (foreign key constraint)
                var logEntriesDeleted = await context.LogEntries
                    .Where(le => le.Service.ToLower() == serviceLower)
                    .ExecuteDeleteAsync(stoppingToken);

                // Delete Downloads
                var downloadsDeleted = await context.Downloads
                    .Where(d => d.Service.ToLower() == serviceLower)
                    .ExecuteDeleteAsync(stoppingToken);

                // Delete ServiceStats
                var serviceStatsDeleted = await context.ServiceStats
                    .Where(s => s.Service.ToLower() == serviceLower)
                    .ExecuteDeleteAsync(stoppingToken);

                var serviceTotal = logEntriesDeleted + downloadsDeleted + serviceStatsDeleted;
                totalDeleted += serviceTotal;

                _logger.LogInformation("Cleaned up orphaned service '{Service}': {Downloads} downloads, {LogEntries} log entries, {ServiceStats} service stats",
                    service, downloadsDeleted, logEntriesDeleted, serviceStatsDeleted);
            }

            _logger.LogInformation("Orphaned service cleanup complete: removed {Total} total records from {Count} services",
                totalDeleted, orphanedServices.Count);

            return orphanedServices.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up orphaned services");
            return 0;
        }
    }

    /// <summary>
    /// Normalizes datasource mappings for downloads.
    /// Fixes issues like:
    /// - Null or empty datasource values
    /// - Inconsistent casing (e.g., "default" vs "Default")
    /// - Datasource names that don't match any configured datasource
    /// All invalid entries are remapped to the default datasource.
    /// </summary>
    private async Task<int> NormalizeDatasourceMappingsAsync(AppDbContext context, CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Checking for datasource mapping inconsistencies...");

            // Get configured datasources
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

            // Get all unique datasource values currently in the database
            var currentDatasources = await context.Downloads
                .Select(d => d.Datasource)
                .Distinct()
                .ToListAsync(stoppingToken);

            _logger.LogInformation("Current datasource values in database: {Values}",
                string.Join(", ", currentDatasources.Select(d => d ?? "(null)")));

            var totalUpdated = 0;

            // Process each unique datasource value
            foreach (var datasourceValue in currentDatasources)
            {
                // Check if this datasource value needs normalization
                bool needsNormalization = false;
                string reason = "";

                if (string.IsNullOrEmpty(datasourceValue))
                {
                    needsNormalization = true;
                    reason = "null or empty";
                }
                else if (!validNames.Contains(datasourceValue))
                {
                    needsNormalization = true;
                    reason = $"not a valid datasource name";
                }
                else if (datasourceValue != validNames.First(n => n.Equals(datasourceValue, StringComparison.OrdinalIgnoreCase)))
                {
                    // Case mismatch - normalize to the exact configured name
                    needsNormalization = true;
                    reason = "case mismatch";
                }

                if (needsNormalization)
                {
                    // Determine the correct normalized name
                    string normalizedName;
                    if (!string.IsNullOrEmpty(datasourceValue) && validNames.Contains(datasourceValue))
                    {
                        // It's a valid name but wrong case - use the exact configured name
                        normalizedName = validNames.First(n => n.Equals(datasourceValue, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        // Invalid or missing - use default
                        normalizedName = defaultName;
                    }

                    _logger.LogInformation("Normalizing datasource '{Old}' -> '{New}' (reason: {Reason})",
                        datasourceValue ?? "(null)", normalizedName, reason);

                    // Update all downloads with this datasource value
                    int updated;
                    if (string.IsNullOrEmpty(datasourceValue))
                    {
                        updated = await context.Downloads
                            .Where(d => d.Datasource == null || d.Datasource == "")
                            .ExecuteUpdateAsync(
                                s => s.SetProperty(d => d.Datasource, normalizedName),
                                stoppingToken);
                    }
                    else
                    {
                        updated = await context.Downloads
                            .Where(d => d.Datasource == datasourceValue)
                            .ExecuteUpdateAsync(
                                s => s.SetProperty(d => d.Datasource, normalizedName),
                                stoppingToken);
                    }

                    totalUpdated += updated;
                    _logger.LogInformation("Updated {Count} downloads from '{Old}' to '{New}'",
                        updated, datasourceValue ?? "(null)", normalizedName);
                }
            }

            if (totalUpdated > 0)
            {
                _logger.LogInformation("Datasource normalization complete: updated {Total} downloads", totalUpdated);
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
}
