using LancacheManager.Data;
using LancacheManager.Hubs;
using LancacheManager.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Services;

public class DatabaseService
{
    private readonly AppDbContext _context;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly ILogger<DatabaseService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly StatsCache _statsCache;

    public DatabaseService(
        AppDbContext context,
        IHubContext<DownloadHub> hubContext,
        ILogger<DatabaseService> logger,
        IPathResolver pathResolver,
        StatsCache statsCache)
    {
        _context = context;
        _hubContext = hubContext;
        _logger = logger;
        _pathResolver = pathResolver;
        _statsCache = statsCache;
    }

    public async Task ResetDatabase()
    {
        try
        {
            _logger.LogInformation("Starting database reset using row-by-row deletion");

            // Send initial progress update
            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
            {
                isProcessing = true,
                percentComplete = 0.0,
                status = "starting",
                message = "Starting database reset...",
                timestamp = DateTime.UtcNow
            });

            // Use efficient bulk deletion to clear all tables
            // Clear LogEntries first due to foreign key relationship with Downloads
            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
            {
                isProcessing = true,
                percentComplete = 10.0,
                status = "deleting",
                message = "Clearing log entries...",
                timestamp = DateTime.UtcNow
            });
            await _context.LogEntries.ExecuteDeleteAsync();

            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
            {
                isProcessing = true,
                percentComplete = 30.0,
                status = "deleting",
                message = "Clearing downloads...",
                timestamp = DateTime.UtcNow
            });
            await _context.Downloads.ExecuteDeleteAsync();

            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
            {
                isProcessing = true,
                percentComplete = 50.0,
                status = "deleting",
                message = "Clearing client stats...",
                timestamp = DateTime.UtcNow
            });
            await _context.ClientStats.ExecuteDeleteAsync();

            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
            {
                isProcessing = true,
                percentComplete = 70.0,
                status = "deleting",
                message = "Clearing service stats...",
                timestamp = DateTime.UtcNow
            });
            await _context.ServiceStats.ExecuteDeleteAsync();
            await _context.SaveChangesAsync();

            _logger.LogDebug("Database cleared successfully");

            // Get data directory for file cleanup
            var dataDirectory = _pathResolver.GetDataDirectory();

            // Ensure data directory exists before working with files
            if (!Directory.Exists(dataDirectory))
            {
                Directory.CreateDirectory(dataDirectory);
                _logger.LogInformation($"Created data directory: {dataDirectory}");
            }

            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
            {
                isProcessing = true,
                percentComplete = 85.0,
                status = "cleanup",
                message = "Cleaning up files...",
                timestamp = DateTime.UtcNow
            });

            // Clear position file
            var positionFile = Path.Combine(dataDirectory, "position.txt");
            if (File.Exists(positionFile))
            {
                File.Delete(positionFile);
                _logger.LogDebug($"Deleted position file: {positionFile}");
            }

            // Clear performance data file
            var performanceFile = Path.Combine(dataDirectory, "performance_data.json");
            if (File.Exists(performanceFile))
            {
                File.Delete(performanceFile);
                _logger.LogDebug($"Deleted performance data file: {performanceFile}");
            }

            // Clear processing marker
            var processingMarker = Path.Combine(dataDirectory, "processing.marker");
            if (File.Exists(processingMarker))
            {
                File.Delete(processingMarker);
                _logger.LogDebug($"Deleted processing marker: {processingMarker}");
            }

            _logger.LogInformation($"Database reset completed successfully. Data directory: {dataDirectory}");

            // Send completion update
            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
            {
                isProcessing = false,
                percentComplete = 100.0,
                status = "complete",
                message = "Database reset completed successfully",
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting database");

            // Send error update
            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
            {
                isProcessing = false,
                percentComplete = 0.0,
                status = "error",
                message = $"Database reset failed: {ex.Message}",
                timestamp = DateTime.UtcNow
            });

            throw;
        }
    }

    public async Task<List<Download>> GetDownloadsWithApp0()
    {
        return await _context.Downloads
            .Where(d => d.GameAppId == 0)
            .ToListAsync();
    }

    public async Task MarkApp0DownloadsInactive()
    {
        var app0Downloads = await GetDownloadsWithApp0();
        foreach (var download in app0Downloads)
        {
            download.IsActive = false;
        }
        await _context.SaveChangesAsync();
        _statsCache.InvalidateDownloads();
    }

    public async Task<List<Download>> GetDownloadsWithBadImageUrls()
    {
        return await _context.Downloads
            .Where(d => d.GameImageUrl != null && d.GameImageUrl.Contains("cdn.akamai.steamstatic.com"))
            .ToListAsync();
    }

    public async Task<int> FixBadImageUrls()
    {
        var badImageUrls = await GetDownloadsWithBadImageUrls();

        if (badImageUrls.Any())
        {
            // Clear bad image URLs - they will be backfilled from Steam API
            foreach (var download in badImageUrls)
            {
                download.GameImageUrl = null;
            }

            await _context.SaveChangesAsync();
            _statsCache.InvalidateDownloads();
            return badImageUrls.Count;
        }

        return 0;
    }
}
