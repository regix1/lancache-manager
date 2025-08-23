using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using LancacheManager.Models;
using LancacheManager.Data;
using LancacheManager.Hubs;

namespace LancacheManager.Services;

public class DatabaseService
{
    private readonly AppDbContext _context;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly SteamService _steamService;
    private readonly ILogger<DatabaseService> _logger;

    public DatabaseService(
        AppDbContext context, 
        IHubContext<DownloadHub> hubContext,
        SteamService steamService,
        ILogger<DatabaseService> logger)
    {
        _context = context;
        _hubContext = hubContext;
        _steamService = steamService;
        _logger = logger;
    }

    public async Task ProcessLogEntry(LogEntry entry)
    {
        // Only process substantial downloads (skip small requests under 1MB)
        if (entry.BytesServed < 1024 * 1024)
            return;

        // Get game name for this depot
        string appName = "Unknown";
        if (!string.IsNullOrEmpty(entry.DepotId))
        {
            appName = await _steamService.GetAppNameAsync(entry.DepotId, entry.Service);
            _logger.LogDebug($"Processing {entry.Service} depot {entry.DepotId}: {appName}");
        }

        // Find active download for this specific depot/app
        var download = await _context.Downloads
            .Where(d => d.ClientIp == entry.ClientIp && 
                       d.Service == entry.Service &&
                       d.Depot == (entry.DepotId ?? "unknown") &&
                       d.IsActive &&
                       d.EndTime > DateTime.UtcNow.AddMinutes(-2))
            .FirstOrDefaultAsync();

        bool isNewDownload = false;
        if (download == null)
        {
            download = new Download
            {
                Service = entry.Service,
                ClientIp = entry.ClientIp,
                StartTime = entry.Timestamp,
                EndTime = entry.Timestamp,
                Depot = entry.DepotId ?? "unknown",
                App = appName,
                IsActive = true,
                Status = "Downloading"
            };
            _context.Downloads.Add(download);
            isNewDownload = true;
            
            _logger.LogInformation($"New download started: {appName} from {entry.ClientIp}");
        }

        // Update download stats
        download.EndTime = entry.Timestamp;
        if (entry.CacheStatus.Contains("HIT"))
        {
            download.CacheHitBytes += entry.BytesServed;
        }
        else
        {
            download.CacheMissBytes += entry.BytesServed;
        }
        
        // Update the app name if it was previously unknown
        if (download.App == "Unknown" || download.App.StartsWith("Steam App"))
        {
            download.App = appName;
        }
        
        // If download gets too large (>10GB), complete it and start fresh
        if (download.TotalBytes > 10L * 1024 * 1024 * 1024)
        {
            download.IsActive = false;
            download.Status = "Completed";
            _logger.LogInformation($"Download completed (size limit): {download.App} - {FormatBytes(download.TotalBytes)}");
        }

        // Auto-complete stale downloads
        var inactiveDownloads = await _context.Downloads
            .Where(d => d.IsActive && 
                       d.EndTime < DateTime.UtcNow.AddMinutes(-2))
            .ToListAsync();
        
        foreach (var inactiveDownload in inactiveDownloads)
        {
            inactiveDownload.IsActive = false;
            inactiveDownload.Status = "Completed";
            _logger.LogInformation($"Download completed (timeout): {inactiveDownload.App} - {FormatBytes(inactiveDownload.TotalBytes)}");
        }

        // Update client stats
        var clientStats = await _context.ClientStats.FindAsync(entry.ClientIp);
        if (clientStats == null)
        {
            clientStats = new ClientStats { ClientIp = entry.ClientIp };
            _context.ClientStats.Add(clientStats);
        }

        if (entry.CacheStatus.Contains("HIT"))
        {
            clientStats.TotalCacheHitBytes += entry.BytesServed;
        }
        else
        {
            clientStats.TotalCacheMissBytes += entry.BytesServed;
        }
        clientStats.LastSeen = entry.Timestamp;
        if (isNewDownload) clientStats.TotalDownloads++;

        // Update service stats
        var serviceStats = await _context.ServiceStats.FindAsync(entry.Service);
        if (serviceStats == null)
        {
            serviceStats = new ServiceStats { Service = entry.Service };
            _context.ServiceStats.Add(serviceStats);
        }

        if (entry.CacheStatus.Contains("HIT"))
        {
            serviceStats.TotalCacheHitBytes += entry.BytesServed;
        }
        else
        {
            serviceStats.TotalCacheMissBytes += entry.BytesServed;
        }
        serviceStats.LastActivity = entry.Timestamp;
        if (isNewDownload) serviceStats.TotalDownloads++;

        await _context.SaveChangesAsync();

        // Notify clients via SignalR
        await _hubContext.Clients.All.SendAsync("DownloadUpdate", download);
    }

    public async Task<List<GameStats>> GetGameStats(string? service = null)
    {
        var query = _context.Downloads.AsQueryable();
        
        if (!string.IsNullOrEmpty(service))
        {
            query = query.Where(d => d.Service == service);
        }

        var gameGroups = await query
            .GroupBy(d => new { d.Service, d.Depot, d.App })
            .Select(g => new 
            {
                g.Key.Service,
                g.Key.Depot,
                g.Key.App,
                TotalCacheHitBytes = g.Sum(d => d.CacheHitBytes),
                TotalCacheMissBytes = g.Sum(d => d.CacheMissBytes),
                DownloadCount = g.Count(),
                LastDownloaded = g.Max(d => d.EndTime),
                Clients = g.Select(d => d.ClientIp).Distinct().ToList()
            })
            .ToListAsync();

        var gameStats = new List<GameStats>();
        
        foreach (var group in gameGroups)
        {
            // Try to get updated name if it's still unknown
            var gameName = group.App;
            if ((gameName == "Unknown" || gameName.StartsWith("Steam App")) && !string.IsNullOrEmpty(group.Depot))
            {
                gameName = await _steamService.GetAppNameAsync(group.Depot, group.Service);
            }
            
            gameStats.Add(new GameStats
            {
                GameId = group.Depot,
                GameName = gameName,
                Service = group.Service,
                TotalCacheHitBytes = group.TotalCacheHitBytes,
                TotalCacheMissBytes = group.TotalCacheMissBytes,
                DownloadCount = group.DownloadCount,
                LastDownloaded = group.LastDownloaded,
                Clients = group.Clients
            });
        }

        return gameStats.OrderByDescending(g => g.TotalBytes).ToList();
    }

    public async Task<List<Download>> GetLatestDownloads(int count = 20)
    {
        var downloads = await _context.Downloads
            .OrderByDescending(d => d.StartTime)
            .Take(count)
            .ToListAsync();
        
        // Update any unknown game names
        foreach (var download in downloads.Where(d => d.App == "Unknown" || d.App.StartsWith("Steam App")))
        {
            if (!string.IsNullOrEmpty(download.Depot))
            {
                download.App = await _steamService.GetAppNameAsync(download.Depot, download.Service);
            }
        }
        
        return downloads;
    }

    public async Task<List<Download>> GetRecentDownloads(int count = 20)
    {
        var cutoffTime = DateTime.UtcNow.AddHours(-24);
        
        var downloads = await _context.Downloads
            .Where(d => d.StartTime > cutoffTime)
            .OrderByDescending(d => d.StartTime)
            .Take(count)
            .ToListAsync();
        
        // Update any unknown game names
        foreach (var download in downloads.Where(d => d.App == "Unknown" || d.App.StartsWith("Steam App")))
        {
            if (!string.IsNullOrEmpty(download.Depot))
            {
                download.App = await _steamService.GetAppNameAsync(download.Depot, download.Service);
            }
        }
        
        return downloads;
    }

    public async Task<List<Download>> GetActiveDownloads()
    {
        var activeTime = DateTime.UtcNow.AddMinutes(-2);
        
        return await _context.Downloads
            .Where(d => d.IsActive && d.EndTime > activeTime)
            .OrderByDescending(d => d.StartTime)
            .ToListAsync();
    }

    public async Task<List<ClientStats>> GetClientStats()
    {
        return await _context.ClientStats
            .OrderByDescending(c => c.TotalBytes)
            .ToListAsync();
    }

    public async Task<List<ServiceStats>> GetServiceStats()
    {
        return await _context.ServiceStats
            .OrderByDescending(s => s.TotalBytes)
            .ToListAsync();
    }

    public async Task ResetDatabase()
    {
        _context.Downloads.RemoveRange(_context.Downloads);
        _context.ClientStats.RemoveRange(_context.ClientStats);
        _context.ServiceStats.RemoveRange(_context.ServiceStats);
        _context.SteamApps.RemoveRange(_context.SteamApps);
        await _context.SaveChangesAsync();
    }
    
    private string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size = size / 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }
}