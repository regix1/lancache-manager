using LancacheManager.Data;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Application.Services;

public class DownloadCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DownloadCleanupService> _logger;

    public DownloadCleanupService(IServiceProvider serviceProvider, ILogger<DownloadCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
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
}
