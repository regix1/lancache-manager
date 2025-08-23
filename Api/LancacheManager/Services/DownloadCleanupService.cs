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

                foreach (var download in staleDownloads)
                {
                    download.IsActive = false;
                    
                    // Update client download count
                    var client = await context.ClientStats.FindAsync(download.ClientIp);
                    if (client != null)
                    {
                        client.TotalDownloads++;
                    }
                    
                    // Update service download count
                    var service = await context.ServiceStats.FindAsync(download.Service);
                    if (service != null)
                    {
                        service.TotalDownloads++;
                    }
                }

                if (staleDownloads.Any())
                {
                    await context.SaveChangesAsync(stoppingToken);
                    _logger.LogInformation($"Marked {staleDownloads.Count} downloads as complete");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in cleanup service");
            }

            await Task.Delay(30000, stoppingToken);
        }
    }
}