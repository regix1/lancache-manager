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

    public async Task ProcessLogEntryBatch(List<LogEntry> entries, bool sendRealtimeUpdates)
    {
        if (!entries.Any()) return;

        // All entries in batch are for same client/service
        var firstEntry = entries.First();
        var sessionKey = $"{firstEntry.ClientIp}_{firstEntry.Service}";
        
        bool shouldCreateNewDownload = false;
        
        lock (_sessionLock)
        {
            if (_sessionTracker.TryGetValue(sessionKey, out var lastActivity))
            {
                // If more than 5 minutes since last activity, it's a new download session
                if (firstEntry.Timestamp - lastActivity > TimeSpan.FromMinutes(5))
                {
                    shouldCreateNewDownload = true;
                }
            }
            else
            {
                shouldCreateNewDownload = true;
            }
            _sessionTracker[sessionKey] = entries.Max(e => e.Timestamp);
        }

        // Find existing active download for this session
        var download = await _context.Downloads
            .Where(d => d.ClientIp == firstEntry.ClientIp &&
                       d.Service == firstEntry.Service &&
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
                Service = firstEntry.Service,
                ClientIp = firstEntry.ClientIp,
                StartTime = entries.Min(e => e.Timestamp),
                EndTime = entries.Max(e => e.Timestamp),
                IsActive = true
            };
            _context.Downloads.Add(download);
            _logger.LogDebug($"Created new download session for {firstEntry.ClientIp} - {firstEntry.Service}");
        }
        else
        {
            // Update existing download session
            download.EndTime = entries.Max(e => e.Timestamp);
        }

        // Aggregate bytes from all entries
        var totalHitBytes = entries.Where(e => e.CacheStatus == "HIT").Sum(e => e.BytesServed);
        var totalMissBytes = entries.Where(e => e.CacheStatus == "MISS").Sum(e => e.BytesServed);

        download.CacheHitBytes += totalHitBytes;
        download.CacheMissBytes += totalMissBytes;

        // Update client stats
        var clientStats = await _context.ClientStats.FindAsync(firstEntry.ClientIp);
        if (clientStats == null)
        {
            clientStats = new ClientStats { ClientIp = firstEntry.ClientIp };
            _context.ClientStats.Add(clientStats);
        }

        clientStats.TotalCacheHitBytes += totalHitBytes;
        clientStats.TotalCacheMissBytes += totalMissBytes;
        clientStats.LastSeen = entries.Max(e => e.Timestamp);

        // Update service stats  
        var serviceStats = await _context.ServiceStats.FindAsync(firstEntry.Service);
        if (serviceStats == null)
        {
            serviceStats = new ServiceStats { Service = firstEntry.Service };
            _context.ServiceStats.Add(serviceStats);
        }

        serviceStats.TotalCacheHitBytes += totalHitBytes;
        serviceStats.TotalCacheMissBytes += totalMissBytes;
        serviceStats.LastActivity = entries.Max(e => e.Timestamp);

        await _context.SaveChangesAsync();

        // Only send SignalR updates if requested (not during preload)
        if (sendRealtimeUpdates)
        {
            await _hubContext.Clients.All.SendAsync("DownloadUpdate", download);
        }
    }

    public async Task ProcessLogEntry(LogEntry entry)
    {
        // Convert single entry to batch
        await ProcessLogEntryBatch(new List<LogEntry> { entry }, true);
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