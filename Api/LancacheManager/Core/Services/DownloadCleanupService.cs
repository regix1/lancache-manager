using System.Data;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.Base;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

public class DownloadCleanupService : ScopedScheduledBackgroundService
{
    private readonly CacheManagementService _cacheManagementService;
    private readonly DatasourceService _datasourceService;

    protected override string ServiceName => "DownloadCleanupService";
    protected override TimeSpan StartupDelay => TimeSpan.FromSeconds(10);
    protected override TimeSpan Interval => TimeSpan.FromSeconds(10);
    protected override bool RunOnStartup => true;

    public DownloadCleanupService(
        IServiceProvider serviceProvider,
        ILogger<DownloadCleanupService> logger,
        IConfiguration configuration,
        CacheManagementService cacheManagementService,
        DatasourceService datasourceService)
        : base(serviceProvider, logger, configuration)
    {
        _cacheManagementService = cacheManagementService;
        _datasourceService = datasourceService;
    }

    protected override async Task OnStartupAsync(CancellationToken stoppingToken)
    {
        using var scope = ServiceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await PerformInitialCleanup(context, stoppingToken);
    }

    protected override async Task ExecuteScopedWorkAsync(
        IServiceProvider scopedServices,
        CancellationToken stoppingToken)
    {
        var context = scopedServices.GetRequiredService<AppDbContext>();
        await CleanupStaleDownloads(context, stoppingToken);
    }

    private async Task CleanupStaleDownloads(AppDbContext context, CancellationToken stoppingToken)
    {
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
            Logger.LogInformation("Marked {Count} downloads as complete (EndTime > 15 seconds old)", totalUpdated);
        }
    }

    private async Task PerformInitialCleanup(AppDbContext context, CancellationToken stoppingToken)
    {
        Logger.LogInformation("Running initial database cleanup...");

        try
        {
            // Fix App 0 entries - mark them as inactive so they don't show up
            Logger.LogInformation("Checking for App 0 downloads...");
            var app0Downloads = await context.Downloads
                .Where(d => d.GameAppId == 0)
                .ToListAsync(stoppingToken);

            Logger.LogInformation("Found {Count} App 0 downloads", app0Downloads.Count);

            if (app0Downloads.Any())
            {
                foreach (var download in app0Downloads)
                {
                    download.IsActive = false;
                }
                await context.SaveChangesAsync(stoppingToken);
                Logger.LogInformation("Marked {Count} 'App 0' downloads as inactive", app0Downloads.Count);
            }
            else
            {
                Logger.LogInformation("No App 0 downloads found to fix");
            }

            // Mark stale downloads as complete on startup (downloads older than 15 seconds)
            Logger.LogInformation("Checking for stale active downloads...");
            var cutoff = DateTime.UtcNow.AddSeconds(-15);
            var staleDownloads = await context.Downloads
                .Where(d => d.IsActive && d.EndTimeUtc < cutoff)
                .ToListAsync(stoppingToken);

            Logger.LogInformation("Found {Count} stale active downloads", staleDownloads.Count);

            if (staleDownloads.Any())
            {
                foreach (var download in staleDownloads)
                {
                    download.IsActive = false;
                }
                await context.SaveChangesAsync(stoppingToken);
                Logger.LogInformation("Marked {Count} stale downloads as complete", staleDownloads.Count);
            }

            // Note: Image URL backfilling is now handled automatically by PICS during incremental scans
            // No manual cleanup needed - PICS fills in missing GameImageUrl fields when processing downloads

            // Normalize datasource mappings - fix inconsistent case and missing datasources
            await NormalizeDatasourceMappingsAsync(context, stoppingToken);

            // Clean up orphaned services (services in DB but not in log files)
            await CleanupOrphanedServicesAsync(context, stoppingToken);

            Logger.LogInformation("Initial database cleanup complete");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error during initial cleanup");
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
            Logger.LogInformation("Checking for orphaned services in database...");

            // Get services that exist in log files (from Rust log_manager)
            var logServices = await _cacheManagementService.GetServiceLogCounts(forceRefresh: false, stoppingToken);
            var logServiceNames = logServices.Keys.Select(s => s.ToLowerInvariant()).ToHashSet();

            Logger.LogInformation("Found {Count} services in log files: {Services}",
                logServiceNames.Count, string.Join(", ", logServiceNames.Take(10)));

            // SAFETY CHECK: If no services found in logs, don't delete anything
            // This prevents accidentally wiping all data if log scanning fails
            if (logServiceNames.Count == 0)
            {
                Logger.LogWarning("No services found in log files - skipping orphaned service cleanup to prevent accidental data loss");
                return 0;
            }

            // Use a transaction to ensure consistency between querying and deleting
            // This prevents race conditions where data changes between queries
            using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, stoppingToken);
            try
            {
                // Get unique services from Downloads table (within transaction)
                var dbServices = await context.Downloads
                    .Select(d => d.Service.ToLower())
                    .Distinct()
                    .ToListAsync(stoppingToken);

                Logger.LogInformation("Found {Count} services in database: {Services}",
                    dbServices.Count, string.Join(", ", dbServices.Take(10)));

                // SAFETY CHECK: Don't delete if most services would be removed
                // This protects against edge cases where log scanning returns partial results
                var orphanedServices = dbServices
                    .Where(s => !logServiceNames.Contains(s.ToLowerInvariant()))
                    .ToList();

                if (dbServices.Count > 0 && orphanedServices.Count >= dbServices.Count)
                {
                    Logger.LogWarning("All {Count} database services would be marked as orphaned - this looks like a log scanning issue. Skipping cleanup.", dbServices.Count);
                    await transaction.RollbackAsync(stoppingToken);
                    return 0;
                }

                if (!orphanedServices.Any())
                {
                    Logger.LogInformation("No orphaned services found");
                    await transaction.CommitAsync(stoppingToken);
                    return 0;
                }

                Logger.LogInformation("Found {Count} orphaned services to clean up: {Services}",
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

                    Logger.LogInformation("Cleaned up orphaned service '{Service}': {Downloads} downloads, {LogEntries} log entries, {ServiceStats} service stats",
                        service, downloadsDeleted, logEntriesDeleted, serviceStatsDeleted);
                }

                await transaction.CommitAsync(stoppingToken);

                Logger.LogInformation("Orphaned service cleanup complete: removed {Total} total records from {Count} services",
                    totalDeleted, orphanedServices.Count);

                return orphanedServices.Count;
            }
            catch
            {
                await transaction.RollbackAsync(stoppingToken);
                throw;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error cleaning up orphaned services");
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
            Logger.LogInformation("Checking for datasource mapping inconsistencies...");

            // Get configured datasources
            var datasources = _datasourceService.GetDatasources();
            var defaultDatasource = _datasourceService.GetDefaultDatasource();

            if (defaultDatasource == null)
            {
                Logger.LogWarning("No default datasource configured - skipping datasource normalization");
                return 0;
            }

            var defaultName = defaultDatasource.Name;
            var validNames = datasources.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            Logger.LogInformation("Valid datasources: {Datasources}, Default: {Default}",
                string.Join(", ", validNames), defaultName);

            // Get all unique datasource values currently in the database
            var currentDatasources = await context.Downloads
                .Select(d => d.Datasource)
                .Distinct()
                .ToListAsync(stoppingToken);

            Logger.LogInformation("Current datasource values in database: {Values}",
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

                    Logger.LogInformation("Normalizing datasource '{Old}' -> '{New}' (reason: {Reason})",
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
                    Logger.LogInformation("Updated {Count} downloads from '{Old}' to '{New}'",
                        updated, datasourceValue ?? "(null)", normalizedName);
                }
            }

            if (totalUpdated > 0)
            {
                Logger.LogInformation("Datasource normalization complete: updated {Total} downloads", totalUpdated);
            }
            else
            {
                Logger.LogInformation("No datasource normalization needed - all mappings are valid");
            }

            return totalUpdated;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error normalizing datasource mappings");
            return 0;
        }
    }
}
