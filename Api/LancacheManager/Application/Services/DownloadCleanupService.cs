using LancacheManager.Data;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Application.Services;

public class DownloadCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DownloadCleanupService> _logger;
    private readonly CacheManagementService _cacheManagementService;

    public DownloadCleanupService(
        IServiceProvider serviceProvider,
        ILogger<DownloadCleanupService> logger,
        CacheManagementService cacheManagementService)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _cacheManagementService = cacheManagementService;
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

            // Get unique services from Downloads table
            var dbServices = await context.Downloads
                .Select(d => d.Service.ToLower())
                .Distinct()
                .ToListAsync(stoppingToken);

            _logger.LogInformation("Found {Count} services in database: {Services}",
                dbServices.Count, string.Join(", ", dbServices.Take(10)));

            // Find orphaned services (in DB but not in logs)
            var orphanedServices = dbServices
                .Where(s => !logServiceNames.Contains(s.ToLowerInvariant()))
                .ToList();

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
}
