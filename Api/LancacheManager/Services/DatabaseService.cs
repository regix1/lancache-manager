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

    public DatabaseService(
        AppDbContext context, 
        IHubContext<DownloadHub> hubContext,
        SteamService steamService)
    {
        _context = context;
        _hubContext = hubContext;
        _steamService = steamService;
    }

    public async Task ProcessLogEntry(LogEntry entry)
    {
        // Find or create active download session
        var download = await _context.Downloads
            .Where(d => d.ClientIp == entry.ClientIp && 
                       d.Service == entry.Service &&
                       d.IsActive &&
                       d.EndTime > DateTime.UtcNow.AddMinutes(-5))
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
                App = await _steamService.GetAppNameAsync(entry.DepotId ?? "", entry.Service),
                IsActive = true
            };
            _context.Downloads.Add(download);
            isNewDownload = true;
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

    public async Task<List<Download>> GetLatestDownloads(int count = 20)
    {
        return await _context.Downloads
            .OrderByDescending(d => d.StartTime)
            .Take(count)
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
        await _context.SaveChangesAsync();
    }
}