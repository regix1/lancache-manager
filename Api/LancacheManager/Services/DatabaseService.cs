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

    // Game Statistics Methods
    public async Task<List<GameStats>> GetGameStats(string? service = null, string? sortBy = "totalBytes")
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

        // Apply sorting
        return sortBy?.ToLower() switch
        {
            "downloadcount" => gameStats.OrderByDescending(g => g.DownloadCount).ToList(),
            "lastdownloaded" => gameStats.OrderByDescending(g => g.LastDownloaded).ToList(),
            "cachehitpercent" => gameStats.OrderByDescending(g => g.CacheHitPercent).ToList(),
            _ => gameStats.OrderByDescending(g => g.TotalBytes).ToList()
        };
    }

    public async Task<GameStats?> GetGameById(string gameId)
    {
        var games = await GetGameStats();
        return games.FirstOrDefault(g => g.GameId == gameId);
    }

    public async Task<List<GameStats>> GetTopGames(int count, string? service, int days)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var query = _context.Downloads
            .Where(d => d.StartTime > cutoff);
        
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
                DownloadCount = g.Count()
            })
            .OrderByDescending(g => g.TotalCacheHitBytes + g.TotalCacheMissBytes)
            .Take(count)
            .ToListAsync();
        
        var gameStats = new List<GameStats>();
        foreach (var group in gameGroups)
        {
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
                DownloadCount = group.DownloadCount
            });
        }
        
        return gameStats;
    }

    public async Task<List<GameStats>> GetTrendingGames(int count, int hours)
    {
        var cutoff = DateTime.UtcNow.AddHours(-hours);
        var query = _context.Downloads
            .Where(d => d.StartTime > cutoff);
        
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
                LastDownloaded = g.Max(d => d.EndTime)
            })
            .OrderByDescending(g => g.DownloadCount)
            .ThenByDescending(g => g.TotalCacheHitBytes + g.TotalCacheMissBytes)
            .Take(count)
            .ToListAsync();
        
        var gameStats = new List<GameStats>();
        foreach (var group in gameGroups)
        {
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
                LastDownloaded = group.LastDownloaded
            });
        }
        
        return gameStats;
    }

    public async Task<List<GameStats>> SearchGames(string query, int limit)
    {
        var games = await GetGameStats();
        return games
            .Where(g => g.GameName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();
    }

    public async Task<List<Download>> GetGameDownloadHistory(string gameId, int days, int count)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        return await _context.Downloads
            .Where(d => d.Depot == gameId && d.StartTime > cutoff)
            .OrderByDescending(d => d.StartTime)
            .Take(count)
            .ToListAsync();
    }

    public async Task<List<string>> GetClientsForGame(string gameId)
    {
        return await _context.Downloads
            .Where(d => d.Depot == gameId)
            .Select(d => d.ClientIp)
            .Distinct()
            .ToListAsync();
    }

    public async Task<List<GameStats>> GetTopGamesForClient(string clientIp, int count)
    {
        var downloads = await _context.Downloads
            .Where(d => d.ClientIp == clientIp)
            .GroupBy(d => new { d.Service, d.Depot, d.App })
            .Select(g => new
            {
                g.Key.Service,
                g.Key.Depot,
                g.Key.App,
                TotalCacheHitBytes = g.Sum(d => d.CacheHitBytes),
                TotalCacheMissBytes = g.Sum(d => d.CacheMissBytes),
                DownloadCount = g.Count()
            })
            .OrderByDescending(g => g.TotalCacheHitBytes + g.TotalCacheMissBytes)
            .Take(count)
            .ToListAsync();
        
        var gameStats = new List<GameStats>();
        foreach (var group in downloads)
        {
            gameStats.Add(new GameStats
            {
                GameId = group.Depot,
                GameName = group.App,
                Service = group.Service,
                TotalCacheHitBytes = group.TotalCacheHitBytes,
                TotalCacheMissBytes = group.TotalCacheMissBytes,
                DownloadCount = group.DownloadCount
            });
        }
        
        return gameStats;
    }

    // Download Methods
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

    public async Task<List<Download>> GetDownloadsByClient(string clientIp, int count)
    {
        return await _context.Downloads
            .Where(d => d.ClientIp == clientIp)
            .OrderByDescending(d => d.StartTime)
            .Take(count)
            .ToListAsync();
    }

    public async Task<List<Download>> GetDownloadsByService(string service, int count)
    {
        return await _context.Downloads
            .Where(d => d.Service == service)
            .OrderByDescending(d => d.StartTime)
            .Take(count)
            .ToListAsync();
    }

    // Client Statistics Methods
    public async Task<List<ClientStats>> GetClientStats(string? sortBy = "totalBytes")
    {
        var stats = await _context.ClientStats
            .ToListAsync();
        
        return sortBy?.ToLower() switch
        {
            "totaldownloads" => stats.OrderByDescending(c => c.TotalDownloads).ToList(),
            "lastseen" => stats.OrderByDescending(c => c.LastSeen).ToList(),
            "cachehitpercent" => stats.OrderByDescending(c => c.CacheHitPercent).ToList(),
            _ => stats.OrderByDescending(c => c.TotalBytes).ToList()
        };
    }

    public async Task<ClientStats?> GetClientById(string clientIp)
    {
        return await _context.ClientStats.FindAsync(clientIp);
    }

    // Service Statistics Methods
    public async Task<List<ServiceStats>> GetServiceStats(string? sortBy = "totalBytes")
    {
        var stats = await _context.ServiceStats
            .ToListAsync();
        
        return sortBy?.ToLower() switch
        {
            "totaldownloads" => stats.OrderByDescending(s => s.TotalDownloads).ToList(),
            "lastactivity" => stats.OrderByDescending(s => s.LastActivity).ToList(),
            "cachehitpercent" => stats.OrderByDescending(s => s.CacheHitPercent).ToList(),
            _ => stats.OrderByDescending(s => s.TotalBytes).ToList()
        };
    }

    public async Task<ServiceStats?> GetServiceById(string service)
    {
        return await _context.ServiceStats.FindAsync(service);
    }

    // Analytics Methods
    public async Task<object> GetRecentStats(int hours)
    {
        var cutoff = DateTime.UtcNow.AddHours(-hours);
        var downloads = await _context.Downloads
            .Where(d => d.StartTime > cutoff)
            .ToListAsync();
        
        var topServices = downloads
            .GroupBy(d => d.Service)
            .Select(g => new { Service = g.Key, Count = g.Count() })
            .OrderByDescending(s => s.Count)
            .Take(5)
            .ToList();
        
        var topClients = downloads
            .GroupBy(d => d.ClientIp)
            .Select(g => new { Client = g.Key, Count = g.Count() })
            .OrderByDescending(c => c.Count)
            .Take(5)
            .ToList();
        
        var totalBytes = downloads.Sum(d => d.TotalBytes);
        var cacheHitBytes = downloads.Sum(d => d.CacheHitBytes);
        
        return new
        {
            DownloadCount = downloads.Count,
            TotalBytes = totalBytes,
            CacheHitRate = totalBytes > 0 ? (cacheHitBytes * 100.0) / totalBytes : 0,
            TopServices = topServices,
            TopClients = topClients
        };
    }

    public async Task<object> GetCacheHitRateTrends(int days, string interval)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var downloads = await _context.Downloads
            .Where(d => d.StartTime > cutoff)
            .OrderBy(d => d.StartTime)
            .ToListAsync();
        
        // Group by interval
        var grouped = interval.ToLower() switch
        {
            "day" => downloads.GroupBy(d => d.StartTime.Date),
            "hour" => downloads.GroupBy(d => new DateTime(d.StartTime.Year, d.StartTime.Month, d.StartTime.Day, d.StartTime.Hour, 0, 0)),
            _ => downloads.GroupBy(d => d.StartTime.Date)
        };
        
        var trends = grouped.Select(g => new
        {
            Timestamp = g.Key,
            HitRate = g.Sum(d => d.TotalBytes) > 0 
                ? (g.Sum(d => d.CacheHitBytes) * 100.0) / g.Sum(d => d.TotalBytes) 
                : 0,
            TotalBytes = g.Sum(d => d.TotalBytes)
        }).ToList();
        
        return trends;
    }

    public async Task<object> GetBandwidthStats(int hours, string interval)
    {
        var cutoff = DateTime.UtcNow.AddHours(-hours);
        var downloads = await _context.Downloads
            .Where(d => d.StartTime > cutoff)
            .OrderBy(d => d.StartTime)
            .ToListAsync();
        
        // Group by interval
        var grouped = interval.ToLower() switch
        {
            "minute" => downloads.GroupBy(d => new DateTime(d.StartTime.Year, d.StartTime.Month, d.StartTime.Day, d.StartTime.Hour, d.StartTime.Minute, 0)),
            "hour" => downloads.GroupBy(d => new DateTime(d.StartTime.Year, d.StartTime.Month, d.StartTime.Day, d.StartTime.Hour, 0, 0)),
            _ => downloads.GroupBy(d => new DateTime(d.StartTime.Year, d.StartTime.Month, d.StartTime.Day, d.StartTime.Hour, 0, 0))
        };
        
        var stats = grouped.Select(g => new
        {
            Timestamp = g.Key,
            BytesPerSecond = g.Sum(d => d.TotalBytes) / 3600.0, // Simplified calculation
            TotalBytes = g.Sum(d => d.TotalBytes),
            DownloadCount = g.Count()
        }).ToList();
        
        return stats;
    }

    public async Task<object> GetSavingsStats(int days)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var downloads = await _context.Downloads
            .Where(d => d.StartTime > cutoff)
            .ToListAsync();
        
        var totalCacheHits = downloads.Sum(d => d.CacheHitBytes);
        var totalCacheMisses = downloads.Sum(d => d.CacheMissBytes);
        var totalBytes = totalCacheHits + totalCacheMisses;
        
        // Estimate bandwidth savings (cache hits = data not downloaded from internet)
        var bandwidthSaved = totalCacheHits;
        
        // Estimate time saved (assuming 10 MB/s internet speed)
        var timeSavedSeconds = bandwidthSaved / (10 * 1024 * 1024);
        
        return new
        {
            BandwidthSaved = bandwidthSaved,
            TimeSavedSeconds = timeSavedSeconds,
            TimeSavedFormatted = TimeSpan.FromSeconds(timeSavedSeconds).ToString(@"hh\:mm\:ss"),
            CacheHitRate = totalBytes > 0 ? (totalCacheHits * 100.0) / totalBytes : 0,
            TotalServed = totalBytes,
            Period = $"Last {days} days"
        };
    }

    // Maintenance Methods
    public async Task ResetDatabase()
    {
        _context.Downloads.RemoveRange(_context.Downloads);
        _context.ClientStats.RemoveRange(_context.ClientStats);
        _context.ServiceStats.RemoveRange(_context.ServiceStats);
        _context.SteamApps.RemoveRange(_context.SteamApps);
        await _context.SaveChangesAsync();
    }

    // Utility Methods
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