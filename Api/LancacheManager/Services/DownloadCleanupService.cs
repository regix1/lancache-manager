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
        // Disabled time-based cleanup - Rust processor handles session management
        // The Rust processor marks sessions inactive when a new session starts (session gap > 5 minutes)
        // This prevents downloads from disappearing when they're still active but waiting for next chunk

        _logger.LogInformation("DownloadCleanupService is disabled - session management handled by Rust processor");

        await Task.Delay(Timeout.Infinite, stoppingToken);

        // Old logic (disabled):
        // - Used 10-minute timeout to mark downloads as complete
        // - This caused downloads to disappear even when still active
        // - Conflicted with 5-minute session gap in Rust processor
    }
}