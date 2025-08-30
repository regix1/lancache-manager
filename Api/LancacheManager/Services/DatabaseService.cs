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
    private readonly SteamService _steamService;
    
    // Track ongoing sessions to group downloads properly
    private static readonly Dictionary<string, DateTime> _sessionTracker = new();
    private static readonly object _sessionLock = new object();
    private const string POSITION_FILE = "/data/logposition.txt";

    public DatabaseService(
        AppDbContext context, 
        IHubContext<DownloadHub> hubContext, 
        ILogger<DatabaseService> logger,
        SteamService steamService)
    {
        _context = context;
        _hubContext = hubContext;
        _logger = logger;
        _steamService = steamService;
    }

    public async Task ProcessLogEntryBatch(List<LogEntry> entries, bool sendRealtimeUpdates)
    {
        if (!entries.Any()) return;

        try
        {
            // During bulk reprocessing, be more aggressive about processing
            if (!sendRealtimeUpdates) // This indicates bulk processing
            {
                // Clear the change tracker to avoid caching issues
                _context.ChangeTracker.Clear();
                
                var firstTimestamp = entries.Min(e => e.Timestamp);
                var lastTimestamp = entries.Max(e => e.Timestamp);
                
                // Don't skip ANY entries during bulk processing
                _logger.LogDebug($"Processing batch for {entries.First().ClientIp}/{entries.First().Service} from {firstTimestamp:yyyy-MM-dd HH:mm:ss}");
            }

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

            // When reprocessing, check for existing downloads in the same time window
            var firstTimestampEntry = entries.Min(e => e.Timestamp);
            var lastTimestampEntry = entries.Max(e => e.Timestamp);

            var download = await _context.Downloads
                .Where(d => d.ClientIp == firstEntry.ClientIp &&
                        d.Service == firstEntry.Service &&
                        d.StartTime <= lastTimestampEntry &&
                        d.EndTime >= firstTimestampEntry)
                .OrderByDescending(d => d.StartTime)
                .FirstOrDefaultAsync();

            bool isNewDownload = false;
            
            if (download == null)
            {
                // Check for active download if no overlap found
                download = await _context.Downloads
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
                    isNewDownload = true;
                    _logger.LogDebug($"Created new download session for {firstEntry.ClientIp} - {firstEntry.Service}");
                }
                else
                {
                    // Update existing download session
                    download.EndTime = entries.Max(e => e.Timestamp);
                }
            }
            else
            {
                // Update existing download session times if needed
                if (firstTimestampEntry < download.StartTime)
                {
                    download.StartTime = firstTimestampEntry;
                }
                if (lastTimestampEntry > download.EndTime)
                {
                    download.EndTime = lastTimestampEntry;
                }
            }

            // Aggregate bytes from all entries
            var totalHitBytes = entries.Where(e => e.CacheStatus == "HIT").Sum(e => e.BytesServed);
            var totalMissBytes = entries.Where(e => e.CacheStatus == "MISS").Sum(e => e.BytesServed);

            download.CacheHitBytes += totalHitBytes;
            download.CacheMissBytes += totalMissBytes;

            // Store the last URL for game identification
            if (!string.IsNullOrEmpty(entries.LastOrDefault()?.Url))
            {
                download.LastUrl = entries.Last().Url;
                
                // For Steam downloads, try to extract game info
                if (download.Service.ToLower() == "steam" && download.GameAppId == null)
                {
                    var appId = _steamService.ExtractAppIdFromUrl(download.LastUrl);
                    if (appId.HasValue)
                    {
                        download.GameAppId = appId;
                        
                        // Try to fetch game name immediately (but don't fail if it doesn't work)
                        try
                        {
                            var gameInfo = await _steamService.GetGameInfoAsync(appId.Value);
                            if (gameInfo != null)
                            {
                                download.GameName = gameInfo.Name;
                                download.GameImageUrl = gameInfo.HeaderImage;
                                _logger.LogDebug($"Identified Steam game: {gameInfo.Name} (App ID: {appId})");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, $"Failed to fetch game info for app {appId}");
                            // Don't fail the whole process, just log and continue
                            download.GameName = $"Steam App {appId}";
                        }
                    }
                }
            }

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
            
            // Increment download count if this is a new download
            if (isNewDownload)
            {
                clientStats.TotalDownloads++;
            }

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
            
            // Increment download count if this is a new download
            if (isNewDownload)
            {
                serviceStats.TotalDownloads++;
            }

            await _context.SaveChangesAsync();

            // Only send SignalR updates if requested (not during bulk processing)
            if (sendRealtimeUpdates)
            {
                await _hubContext.Clients.All.SendAsync("DownloadUpdate", download);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing log entry batch");
            // Don't throw - continue processing other batches
        }
    }

    public async Task<List<Download>> GetLatestDownloads(int count)
    {
        try
        {
            return await _context.Downloads
                .OrderByDescending(d => d.StartTime)
                .Take(count)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting latest downloads");
            return new List<Download>();
        }
    }

    public async Task<List<Download>> GetActiveDownloads()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-5);
            return await _context.Downloads
                .Where(d => d.IsActive && d.EndTime > cutoff)
                .OrderByDescending(d => d.StartTime)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting active downloads");
            return new List<Download>();
        }
    }

    public async Task<List<ClientStats>> GetClientStats()
    {
        try
        {
            var stats = await _context.ClientStats.ToListAsync();
            return stats.OrderByDescending(c => c.TotalBytes).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting client stats");
            return new List<ClientStats>();
        }
    }

    public async Task<List<ServiceStats>> GetServiceStats()
    {
        try
        {
            var stats = await _context.ServiceStats.ToListAsync();
            return stats.OrderByDescending(s => s.TotalBytes).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting service stats");
            return new List<ServiceStats>();
        }
    }

    public async Task ResetDatabase()
    {
        try
        {
            _context.Downloads.RemoveRange(_context.Downloads);
            _context.ClientStats.RemoveRange(_context.ClientStats);
            _context.ServiceStats.RemoveRange(_context.ServiceStats);
            await _context.SaveChangesAsync();
            
            // Clear position file
            if (File.Exists(POSITION_FILE))
            {
                File.Delete(POSITION_FILE);
            }
            
            // Clear session tracker
            lock (_sessionLock)
            {
                _sessionTracker.Clear();
            }
            
            _logger.LogInformation("Database reset completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting database");
            throw;
        }
    }
}