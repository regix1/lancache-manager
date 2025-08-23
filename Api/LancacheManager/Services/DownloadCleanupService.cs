using Microsoft.EntityFrameworkCore;
using LancacheManager.Data;

namespace LancacheManager.Services;

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
        // Wait a bit for the app to start
        await Task.Delay(5000, stoppingToken);
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var cutoff = DateTime.UtcNow.AddMinutes(-2);
                var staleDownloads = await context.Downloads
                    .Where(d => d.IsActive && d.EndTime < cutoff)
                    .ToListAsync(stoppingToken);

                if (staleDownloads.Any())
                {
                    foreach (var download in staleDownloads)
                    {
                        download.IsActive = false;
                    }

                    await context.SaveChangesAsync(stoppingToken);
                    _logger.LogInformation($"Marked {staleDownloads.Count} downloads as complete");
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
}