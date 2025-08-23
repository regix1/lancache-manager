using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using LancacheManager.Data;
using LancacheManager.Models;
using LancacheManager.Hubs;

namespace LancacheManager.Services;

public class DatabaseService
{
    private readonly AppDbContext _context;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly ILogger<DatabaseService> _logger;

    public DatabaseService(AppDbContext context, IHubContext<DownloadHub> hubContext, ILogger<DatabaseService> logger)
    {
        _context = context;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task ProcessLogEntry(LogEntry entry)
    {
        // Skip small entries
        if (entry.BytesServed < 1024 * 1024) return;

        // Find or create active download
        var download = await _context.Downloads
            .Where(d => d.ClientIp == entry.ClientIp &&
                       d.Service == entry.Service &&
                       d.IsActive &&
                       d.EndTime > DateTime.UtcNow.AddMinutes(-2))
            .FirstOrDefaultAsync();

        if (download == null)
        {
            download = new Download
            {
                Service = entry.Service,
                ClientIp = entry.ClientIp,
                StartTime = entry.Timestamp,
                EndTime = entry.Timestamp,
                IsActive = true
            };
            _context.Downloads.Add(download);
        }

        // Update download
        download.EndTime = entry.Timestamp;
        if (entry.CacheStatus == "HIT")
            download.CacheHitBytes += entry.BytesServed;
        else
            download.CacheMissBytes += entry.BytesServed;

        // Update client stats
        var clientStats = await _context.ClientStats.FindAsync(entry.ClientIp);
        if (clientStats == null)
        {
            clientStats = new ClientStats { ClientIp = entry.ClientIp };
            _context.ClientStats.Add(clientStats);
        }

        if (entry.CacheStatus == "HIT")
            clientStats.TotalCacheHitBytes += entry.BytesServed;
        else
            clientStats.TotalCacheMissBytes += entry.BytesServed;
        
        clientStats.LastSeen = entry.Timestamp;

        // Update service stats
        var serviceStats = await _context.ServiceStats.FindAsync(entry.Service);
        if (serviceStats == null)
        {
            serviceStats = new ServiceStats { Service = entry.Service };
            _context.ServiceStats.Add(serviceStats);
        }

        if (entry.CacheStatus == "HIT")
            serviceStats.TotalCacheHitBytes += entry.BytesServed;
        else
            serviceStats.TotalCacheMissBytes += entry.BytesServed;
        
        serviceStats.LastActivity = entry.Timestamp;

        await _context.SaveChangesAsync();

        // Notify clients
        await _hubContext.Clients.All.SendAsync("DownloadUpdate", download);
    }

    public async Task<List<Download>> GetLatestDownloads(int count)
    {
        return await _context.Downloads
            .OrderByDescending(d => d.StartTime)
            .Take(count)
            .ToListAsync();
    }

    public async Task<List<Download>> GetActiveDownloads()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-2);
        return await _context.Downloads
            .Where(d => d.IsActive && d.EndTime > cutoff)
            .OrderByDescending(d => d.StartTime)
            .ToListAsync();
    }

    public async Task<List<ClientStats>> GetClientStats()
    {
        // First get all stats, then sort in memory (EF Core can't translate calculated properties)
        var stats = await _context.ClientStats.ToListAsync();
        
        // Sort by TotalBytes (calculated property) in memory
        return stats.OrderByDescending(c => c.TotalCacheHitBytes + c.TotalCacheMissBytes).ToList();
    }

    public async Task<List<ServiceStats>> GetServiceStats()
    {
        // First get all stats, then sort in memory (EF Core can't translate calculated properties)
        var stats = await _context.ServiceStats.ToListAsync();
        
        // Sort by TotalBytes (calculated property) in memory
        return stats.OrderByDescending(s => s.TotalCacheHitBytes + s.TotalCacheMissBytes).ToList();
    }

    public async Task ResetDatabase()
    {
        _context.Downloads.RemoveRange(_context.Downloads);
        _context.ClientStats.RemoveRange(_context.ClientStats);
        _context.ServiceStats.RemoveRange(_context.ServiceStats);
        await _context.SaveChangesAsync();
    }
}