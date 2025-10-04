using Microsoft.EntityFrameworkCore;
using LancacheManager.Data;

namespace LancacheManager.Services;

public class DownloadCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DownloadCleanupService> _logger;
    private bool _initialCleanupDone = false;

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
            _initialCleanupDone = true;
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

                // Use 1-minute timeout - if no new data in 1 minute, download is complete
                var cutoff = DateTime.UtcNow.AddMinutes(-1);

                // Process in smaller batches to avoid long locks (important when Rust processor is running)
                const int batchSize = 10;
                var totalUpdated = 0;

                while (true)
                {
                    var staleDownloads = await context.Downloads
                        .Where(d => d.IsActive && d.EndTime < cutoff)
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
                    _logger.LogInformation($"Marked {totalUpdated} downloads as complete (EndTime > 1 minute old)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in cleanup service");
            }

            // Run every 30 seconds
            await Task.Delay(30000, stoppingToken);
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

            // Fix bad image URLs (cdn.akamai.steamstatic.com) - try fallback URLs
            _logger.LogInformation("Checking for bad image URLs...");
            var badImageUrls = await context.Downloads
                .Where(d => d.GameImageUrl != null && d.GameImageUrl.Contains("cdn.akamai.steamstatic.com"))
                .ToListAsync(stoppingToken);

            _logger.LogInformation($"Found {badImageUrls.Count} downloads with bad image URLs");

            if (badImageUrls.Any())
            {
                var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("User-Agent", "LancacheManager/1.0");
                httpClient.Timeout = TimeSpan.FromSeconds(5);

                var updated = 0;
                foreach (var download in badImageUrls)
                {
                    if (!download.GameAppId.HasValue) continue;

                    var appId = download.GameAppId.Value;
                    var fallbackUrls = new[]
                    {
                        $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg",
                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg",
                        $"https://steamcdn-a.akamaihd.net/steam/apps/{appId}/header.jpg"
                    };

                    foreach (var fallbackUrl in fallbackUrls)
                    {
                        try
                        {
                            var response = await httpClient.GetAsync(fallbackUrl, HttpCompletionOption.ResponseHeadersRead, stoppingToken);
                            if (response.IsSuccessStatusCode)
                            {
                                download.GameImageUrl = fallbackUrl;
                                updated++;
                                _logger.LogDebug($"Updated image URL for app {appId} to {fallbackUrl}");
                                break;
                            }
                        }
                        catch
                        {
                            // Try next URL
                        }
                    }
                }

                if (updated > 0)
                {
                    await context.SaveChangesAsync(stoppingToken);
                    _logger.LogInformation($"Updated {updated} image URLs to working fallback CDNs");
                    needsCacheInvalidation = true;
                }
                else
                {
                    _logger.LogInformation("No image URLs were updated (all fallbacks failed or URLs already correct)");
                }

                httpClient.Dispose();
            }
            else
            {
                _logger.LogInformation("No bad image URLs found to fix");
            }

            // Invalidate cache if we made any changes
            if (needsCacheInvalidation)
            {
                using var scope = _serviceProvider.CreateScope();
                var statsCache = scope.ServiceProvider.GetRequiredService<StatsCache>();
                statsCache.InvalidateDownloads();
                _logger.LogInformation("Invalidated downloads cache after cleanup");
            }

            _logger.LogInformation("Initial database cleanup complete");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during initial cleanup");
        }
    }
}