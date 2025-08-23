using Microsoft.EntityFrameworkCore;
using LancacheManager.Data;

namespace LancacheManager.Services;

public class DownloadCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DownloadCleanupService> _logger;

    public DownloadCleanupService(
        IServiceProvider serviceProvider,
        ILogger<DownloadCleanupService> logger)
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
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // Mark downloads as complete if no activity for 2 minutes
                var staleDownloads = await dbContext.Downloads
                    .Where(d => d.IsActive && 
                               d.EndTime < DateTime.UtcNow.AddMinutes(-2))
                    .ToListAsync(stoppingToken);

                if (staleDownloads.Any())
                {
                    foreach (var download in staleDownloads)
                    {
                        download.IsActive = false;
                        download.Status = "Completed";
                        _logger.LogInformation(
                            "Marked download as complete: {Service} for {Client}, Total: {Total:F2} GB", 
                            download.Service, 
                            download.ClientIp, 
                            download.TotalBytes / (1024.0 * 1024.0 * 1024.0));
                    }

                    await dbContext.SaveChangesAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during download cleanup");
            }

            // Run every 30 seconds
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}