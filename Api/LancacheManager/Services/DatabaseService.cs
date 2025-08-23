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
    
    // Track ongoing sessions to group downloads properly
    private static readonly Dictionary<string, DateTime> _sessionTracker = new();
    private static readonly object _sessionLock = new object();

    public DatabaseService(AppDbContext context, IHubContext<DownloadHub> hubContext, ILogger<DatabaseService> logger)
    {
        _context = context;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task ProcessLogEntry(LogEntry entry)
    {
        // Skip small entries (less than 1MB)
        if (entry.BytesServed < 1024 * 1024) return;

        // Create session key
        var sessionKey = $"{entry.ClientIp}_{entry.Service}";
        bool shouldCreateNewDownload = false;
        
        lock (_sessionLock)
        {
            if (_sessionTracker.TryGetValue(sessionKey, out var lastActivity))
            {
                // If more than 5 minutes since last activity, it's a new download session
                if (entry.Timestamp - lastActivity > TimeSpan.FromMinutes(5))
                {
                    shouldCreateNewDownload = true;
                }
            }
            else
            {
                shouldCreateNewDownload = true;
            }
            _sessionTracker[sessionKey] = entry.Timestamp;
        }

        // Find existing active download for this session
        var download = await _context.Downloads
            .Where(d => d.ClientIp == entry.ClientIp &&
                       d.Service == entry.Service &&
                       d.IsActive)
            .OrderByDescending(d => d.StartTime)
            .FirstOrDefaultAsync();

        if (download == null || shouldCreateNewDownload)
        {
            // Mark old download as complete if exists
            if (download != null)
            {
                download.IsActive = false;
            }

            // Create new download session
            download = new Download
            {
                Service = entry.Service,
                ClientIp = entry.ClientIp,
                StartTime = entry.Timestamp,
                EndTime = entry.Timestamp,
                IsActive = true
            };
            _context.Downloads.Add(download);
            _logger.LogDebug($"Created new download session for {entry.ClientIp} - {entry.Service}");
        }
        else
        {
            // Update existing download session
            download.EndTime = entry.Timestamp;
        }

        // Update bytes
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

        // Notify clients via SignalR
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
        var cutoff = DateTime.UtcNow.AddMinutes(-5);
        return await _context.Downloads
            .Where(d => d.IsActive && d.EndTime > cutoff)
            .OrderByDescending(d => d.StartTime)
            .ToListAsync();
    }

    public async Task<List<ClientStats>> GetClientStats()
    {
        var stats = await _context.ClientStats.ToListAsync();
        return stats.OrderByDescending(c => c.TotalCacheHitBytes + c.TotalCacheMissBytes).ToList();
    }

    public async Task<List<ServiceStats>> GetServiceStats()
    {
        var stats = await _context.ServiceStats.ToListAsync();
        return stats.OrderByDescending(s => s.TotalCacheHitBytes + s.TotalCacheMissBytes).ToList();
    }

    public async Task ResetDatabase()
    {
        _context.Downloads.RemoveRange(_context.Downloads);
        _context.ClientStats.RemoveRange(_context.ClientStats);
        _context.ServiceStats.RemoveRange(_context.ServiceStats);
        await _context.SaveChangesAsync();
        
        // Clear position file
        var positionFile = "/data/logposition.txt";
        if (File.Exists(positionFile))
        {
            File.Delete(positionFile);
        }
        
        // Clear session tracker
        lock (_sessionLock)
        {
            _sessionTracker.Clear();
        }
        
        _logger.LogInformation("Database reset completed");
    }
}