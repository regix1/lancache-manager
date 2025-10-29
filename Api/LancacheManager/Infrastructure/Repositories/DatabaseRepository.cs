using LancacheManager.Data;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Repositories.Interfaces;
using LancacheManager.Infrastructure.Services.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Infrastructure.Repositories;

public class DatabaseRepository : IDatabaseRepository
{
    private readonly AppDbContext _context;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly ILogger<DatabaseRepository> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly StatsCache _statsCache;

    public DatabaseRepository(
        AppDbContext context,
        IHubContext<DownloadHub> hubContext,
        ILogger<DatabaseRepository> logger,
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
            _logger.LogInformation("Starting database reset with batched deletion");

            // Send initial progress update
            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
            {
                isProcessing = true,
                percentComplete = 0.0,
                status = "starting",
                message = "Starting database reset...",
                timestamp = DateTime.UtcNow
            });

            // Count total rows for progress calculation
            var logEntriesCount = await _context.LogEntries.CountAsync();
            var downloadsCount = await _context.Downloads.CountAsync();
            var clientStatsCount = await _context.ClientStats.CountAsync();
            var serviceStatsCount = await _context.ServiceStats.CountAsync();
            var totalRows = logEntriesCount + downloadsCount + clientStatsCount + serviceStatsCount;

            _logger.LogInformation($"Deleting {totalRows:N0} total rows: LogEntries={logEntriesCount:N0}, Downloads={downloadsCount:N0}, ClientStats={clientStatsCount:N0}, ServiceStats={serviceStatsCount:N0}");

            int deletedRows = 0;

            // Delete LogEntries in batches (foreign key constraint requires this first)
            deletedRows = await DeleteInBatches(
                () => _context.LogEntries.Take(5000),
                totalRows,
                deletedRows,
                "log entries",
                5.0,
                25.0);

            // Delete Downloads in batches
            deletedRows = await DeleteInBatches(
                () => _context.Downloads.Take(5000),
                totalRows,
                deletedRows,
                "downloads",
                25.0,
                60.0);

            // Delete ClientStats in batches
            deletedRows = await DeleteInBatches(
                () => _context.ClientStats.Take(5000),
                totalRows,
                deletedRows,
                "client stats",
                60.0,
                75.0);

            // Delete ServiceStats in batches
            deletedRows = await DeleteInBatches(
                () => _context.ServiceStats.Take(5000),
                totalRows,
                deletedRows,
                "service stats",
                75.0,
                85.0);


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
            }

            // Clear performance data file
            var performanceFile = Path.Combine(dataDirectory, "performance_data.json");
            if (File.Exists(performanceFile))
            {
                File.Delete(performanceFile);
            }

            // Clear processing marker
            var processingMarker = Path.Combine(dataDirectory, "processing.marker");
            if (File.Exists(processingMarker))
            {
                File.Delete(processingMarker);
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

    private async Task<int> DeleteInBatches<T>(
        Func<IQueryable<T>> queryFactory,
        int totalRows,
        int currentDeletedRows,
        string tableName,
        double startPercent,
        double endPercent) where T : class
    {
        int deletedRows = currentDeletedRows;
        int batchCount = 0;

        while (true)
        {
            // Get a batch of rows to delete
            var batch = await queryFactory().ToListAsync();

            if (!batch.Any())
            {
                break; // No more rows to delete
            }

            // Delete the batch
            _context.RemoveRange(batch);
            await _context.SaveChangesAsync();

            deletedRows += batch.Count;
            batchCount++;

            // Calculate progress within this table's range
            var tableProgress = totalRows > 0 ? (double)deletedRows / totalRows : 0;
            var percentComplete = startPercent + (tableProgress * (endPercent - startPercent));

            // Send progress update
            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
            {
                isProcessing = true,
                percentComplete = Math.Min(percentComplete, endPercent),
                status = "deleting",
                message = $"Clearing {tableName}... ({deletedRows:N0} / {totalRows:N0} rows)",
                timestamp = DateTime.UtcNow
            });

            _logger.LogInformation($"Deleted batch {batchCount} of {tableName}: {batch.Count} rows (total: {deletedRows:N0} / {totalRows:N0})");
        }

        return deletedRows;
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

    public async Task<int> ClearDepotMappings()
    {
        try
        {
            _logger.LogInformation("Clearing all depot mappings from database and downloads table");

            var count = await _context.SteamDepotMappings.CountAsync();

            // Clear depot mappings table
            await _context.SteamDepotMappings.ExecuteDeleteAsync();

            // Also clear game info from downloads table (set to null, keep download records)
            await _context.Downloads
                .Where(d => d.GameAppId != null || d.GameName != null || d.GameImageUrl != null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(d => d.GameAppId, (uint?)null)
                    .SetProperty(d => d.GameName, (string?)null)
                    .SetProperty(d => d.GameImageUrl, (string?)null));

            await _context.SaveChangesAsync();

            // Invalidate stats cache since download records changed
            _statsCache.InvalidateDownloads();

            _logger.LogInformation("Cleared {Count} depot mappings from database and cleared game info from downloads table", count);
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing depot mappings");
            throw;
        }
    }
}
