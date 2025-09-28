using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using LancacheManager.Data;
using LancacheManager.Models;
using LancacheManager.Hubs;
using LancacheManager.Services;

namespace LancacheManager.Services;

public class DatabaseService
{
    private readonly AppDbContext _context;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly ILogger<DatabaseService> _logger;
    private readonly SteamService _steamService;
    private readonly SteamKit2Service _steamKit2Service;
    private readonly PicsDataService _picsDataService;
    private readonly IPathResolver _pathResolver;

    // Track ongoing sessions to group downloads properly
    private static readonly Dictionary<string, DateTime> _sessionTracker = new();
    private static readonly object _sessionLock = new object();

    public DatabaseService(
        AppDbContext context,
        IHubContext<DownloadHub> hubContext,
        ILogger<DatabaseService> logger,
        SteamService steamService,
        SteamKit2Service steamKit2Service,
        PicsDataService picsDataService,
        IPathResolver pathResolver)
    {
        _context = context;
        _hubContext = hubContext;
        _logger = logger;
        _steamService = steamService;
        _steamKit2Service = steamKit2Service;
        _picsDataService = picsDataService;
        _pathResolver = pathResolver;
    }

    public async Task ProcessLogEntryBatch(List<LogEntry> entries, bool sendRealtimeUpdates)
    {
        if (!entries.Any()) return;

        try
        {
            // Only trigger PICS crawler for real-time processing, not during bulk processing
            if (sendRealtimeUpdates && !_steamKit2Service.IsReady && _steamKit2Service.ShouldRunPicsCrawl())
            {
                _steamKit2Service.TryStartRebuild();
                _logger.LogInformation("Triggered PICS crawler for real-time log processing");
            }

            // During bulk reprocessing, be more aggressive about processing
            if (!sendRealtimeUpdates) // This indicates bulk processing
            {
                // Clear the change tracker to avoid holding on to large tracked graphs
                // and to ensure duplicate detection only considers persisted rows.
                _context.ChangeTracker.Clear();

                var firstTimestamp = entries.Min(e => e.Timestamp);
                var lastTimestamp = entries.Max(e => e.Timestamp);

                _logger.LogTrace($"Processing batch for {entries.First().ClientIp}/{entries.First().Service} from {firstTimestamp:yyyy-MM-dd HH:mm:ss}");
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

            // Group downloads by depot ID (for Steam) or by client+service+session for other services
            var firstTimestampEntry = entries.Min(e => e.Timestamp);
            var lastTimestampEntry = entries.Max(e => e.Timestamp);
            var depotId = firstEntry.DepotId;

            Download? download = null;

            if (firstEntry.Service == "steam" && depotId.HasValue)
            {
                // For Steam, group by depot ID - each depot is a separate download session
                download = await _context.Downloads
                    .Where(d => d.ClientIp == firstEntry.ClientIp &&
                            d.Service == "steam" &&
                            d.DepotId == depotId &&
                            d.IsActive)
                    .OrderByDescending(d => d.StartTime)
                    .FirstOrDefaultAsync();

                // If no active download but should create new (5+ minute gap), check for recent inactive
                if (download == null && shouldCreateNewDownload)
                {
                    var recentDownload = await _context.Downloads
                        .Where(d => d.ClientIp == firstEntry.ClientIp &&
                                d.Service == "steam" &&
                                d.DepotId == depotId &&
                                !d.IsActive)
                        .OrderByDescending(d => d.EndTime)
                        .FirstOrDefaultAsync();

                    // Don't merge if there's been more than 5 minutes since last activity
                    if (recentDownload != null && (firstTimestampEntry - recentDownload.EndTime).TotalMinutes <= 5)
                    {
                        download = recentDownload;
                        download.IsActive = true;
                    }
                }
            }
            else
            {
                // For non-Steam services, use simple session-based grouping with 5 minute timeout
                download = await _context.Downloads
                    .Where(d => d.ClientIp == firstEntry.ClientIp &&
                            d.Service == firstEntry.Service &&
                            d.IsActive)
                    .OrderByDescending(d => d.StartTime)
                    .FirstOrDefaultAsync();

                // Check if we should continue existing download or create new
                if (download != null && shouldCreateNewDownload)
                {
                    // More than 5 minutes since last activity - close old and create new
                    download.IsActive = false;
                    download = null;
                }
            }

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
                    _logger.LogTrace($"Created new download session for {firstEntry.ClientIp} - {firstEntry.Service}");
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
            }

            // Extract depot ID from log entries (prioritize the most recent one with a depot ID)
            var depotEntry = entries.LastOrDefault(e => e.DepotId.HasValue);
            if (depotEntry != null && download.DepotId == null)
            {
                download.DepotId = depotEntry.DepotId;
                _logger.LogTrace($"Set depot ID {depotEntry.DepotId} for download from {firstEntry.ClientIp}");
            }

            // For Steam downloads, try to extract game info using PICS depot mapping first, then fallback to pattern matching
            // SKIP depot mapping during bulk processing for performance - will be done in post-processing step
            if (sendRealtimeUpdates && download.Service.ToLower() == "steam" && download.GameAppId == null && download.DepotId.HasValue)
            {
                uint? appId = null;

                // First try PICS depot mapping from SteamKit2Service
                var appIds = _steamKit2Service.GetAppIdsForDepot(download.DepotId.Value);
                if (appIds.Any())
                {
                    appId = appIds.First(); // Take the first app ID if multiple exist
                    _logger.LogDebug($"Mapped depot {download.DepotId} to app {appId} using PICS data from database");
                }
                else
                {
                    // Second, try JSON file fallback
                    var jsonAppIds = await _picsDataService.GetAppIdsForDepotFromJsonAsync(download.DepotId.Value);
                    if (jsonAppIds.Any())
                    {
                        appId = jsonAppIds.First();
                        _logger.LogDebug($"Mapped depot {download.DepotId} to app {appId} using PICS data from JSON file");

                        // Import this data to database for future use (if not in bulk processing mode)
                        if (sendRealtimeUpdates) // This indicates real-time processing, not bulk
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await _picsDataService.ImportJsonDataToDatabaseAsync();
                                    _logger.LogDebug("Imported JSON data to database after finding mapping in JSON file");
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed to import JSON data to database during log processing");
                                }
                            });
                        }
                    }
                    else
                    {
                        _logger.LogDebug($"No PICS mapping found for depot {download.DepotId} in database or JSON file");
                    }
                }

                // If we found an app ID, populate the game info
                if (appId.HasValue)
                {
                    download.GameAppId = appId.Value;

                    string? resolvedName = null;

                    // Try to fetch game name immediately (but don't fail if it doesn't work)
                    try
                    {
                        var gameInfo = await _steamService.GetGameInfoAsync(appId.Value);
                        if (gameInfo != null)
                        {
                            resolvedName = gameInfo.Name;
                            download.GameName = gameInfo.Name;
                            download.GameImageUrl = gameInfo.HeaderImage;
                            _logger.LogDebug($"Identified Steam game: {gameInfo.Name} (App ID: {appId})");
                        }
                        else
                        {
                            download.GameName = $"Steam App {appId}";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to fetch game info for app {appId}");
                        download.GameName = $"Steam App {appId}";
                    }

                    await StoreDepotMappingAsync(appId.Value, download.DepotId.Value, resolvedName ?? download.GameName, "realtime");
                }
            }
            else if (!sendRealtimeUpdates && download.Service.ToLower() == "steam" && download.DepotId.HasValue)
            {
                // During bulk processing, just log that we're deferring mapping
                _logger.LogTrace($"Deferring depot mapping for depot {download.DepotId} (bulk processing mode)");
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

            // Save the download first to get its ID
            await _context.SaveChangesAsync();

            // Now save individual log entries with the download ID
            // NO DUPLICATE CHECKING - save ALL entries
            var logEntriesToSave = new List<LogEntryRecord>();

            foreach (var entry in entries)
            {
                var logRecord = new LogEntryRecord
                {
                    Timestamp = entry.Timestamp,
                    ClientIp = entry.ClientIp,
                    Service = entry.Service,
                    Method = "GET", // Default to GET since logs don't include method
                    Url = entry.Url,
                    StatusCode = entry.StatusCode,
                    BytesServed = entry.BytesServed,
                    CacheStatus = entry.CacheStatus,
                    DepotId = entry.DepotId,
                    DownloadId = download.Id, // Associate with the download session
                    CreatedAt = DateTime.UtcNow
                };
                logEntriesToSave.Add(logRecord);
            }

            // Bulk insert log entries for better performance
            if (logEntriesToSave.Any())
            {
                await _context.LogEntries.AddRangeAsync(logEntriesToSave);
                await _context.SaveChangesAsync();
                _logger.LogTrace($"Saved {logEntriesToSave.Count} log entries to database");
            }

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
            _logger.LogInformation("Starting database reset using row-by-row deletion");

            // Use efficient bulk deletion to clear all tables
            // Clear LogEntries first due to foreign key relationship with Downloads
            await _context.LogEntries.ExecuteDeleteAsync();
            await _context.Downloads.ExecuteDeleteAsync();
            await _context.ClientStats.ExecuteDeleteAsync();
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

            // Clear session tracker
            lock (_sessionLock)
            {
                _sessionTracker.Clear();
            }

            _logger.LogInformation($"Database reset completed successfully. Data directory: {dataDirectory}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting database");
            throw;
        }
    }

    /// <summary>
    /// Post-process depot mappings for downloads that were created during bulk processing
    /// This should be called after bulk log processing is complete
    /// </summary>
    public async Task<int> PostProcessDepotMappings()
    {
        _logger.LogInformation("Starting post-processing of depot mappings...");
        int mappingsProcessed = 0;

        try
        {
            // Find all Steam downloads that have depot IDs but no game app ID
            var unmappedDownloads = await _context.Downloads
                .Where(d => d.Service.ToLower() == "steam"
                       && d.DepotId.HasValue
                       && !d.GameAppId.HasValue)
                .ToListAsync();

            _logger.LogInformation($"Found {unmappedDownloads.Count} downloads needing depot mapping");

            // Send initial progress update
            await _hubContext.Clients.All.SendAsync("DepotMappingProgress", new
            {
                isProcessing = true,
                totalMappings = unmappedDownloads.Count,
                processedMappings = 0,
                percentComplete = 0.0,
                status = "starting",
                message = $"Starting depot mapping for {unmappedDownloads.Count} downloads...",
                timestamp = DateTime.UtcNow
            });

            for (int i = 0; i < unmappedDownloads.Count; i++)
            {
                var download = unmappedDownloads[i];
                try
                {
                    uint? appId = null;

                    // First try PICS depot mapping from SteamKit2Service
                    var appIds = _steamKit2Service.GetAppIdsForDepot(download.DepotId.Value);
                    if (appIds.Any())
                    {
                        appId = appIds.First(); // Take the first app ID if multiple exist
                        _logger.LogTrace($"Mapped depot {download.DepotId} to app {appId} using PICS data from database");
                    }
                    else
                    {
                        // Second, try JSON file fallback
                        var jsonAppIds = await _picsDataService.GetAppIdsForDepotFromJsonAsync(download.DepotId.Value);
                        if (jsonAppIds.Any())
                        {
                            appId = jsonAppIds.First();
                            _logger.LogTrace($"Mapped depot {download.DepotId} to app {appId} using PICS data from JSON file");
                        }
                        else
                        {
                            _logger.LogTrace($"No PICS mapping found for depot {download.DepotId} in database or JSON file");
                        }
                    }

                    // If we found an app ID, populate the game info
                    if (appId.HasValue)
                    {
                        download.GameAppId = appId.Value;

                        string resolvedName = $"Steam App {appId}";

                        // Try to fetch game name
                        try
                        {
                            var gameInfo = await _steamService.GetGameInfoAsync(appId.Value);
                            if (gameInfo != null)
                            {
                                resolvedName = gameInfo.Name;
                                download.GameName = gameInfo.Name;
                                download.GameImageUrl = gameInfo.HeaderImage;
                                _logger.LogTrace($"Identified Steam game: {gameInfo.Name} (App ID: {appId})");
                            }
                            else
                            {
                                download.GameName = resolvedName;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, $"Failed to fetch game info for app {appId}");
                            download.GameName = resolvedName;
                        }

                        await StoreDepotMappingAsync(appId.Value, download.DepotId.Value, resolvedName, "post_process");

                        mappingsProcessed++;
                    }

                    // Send progress update every 10 items or on last item
                    if ((i + 1) % 10 == 0 || i == unmappedDownloads.Count - 1)
                    {
                        var percentComplete = unmappedDownloads.Count > 0 ? (double)(i + 1) / unmappedDownloads.Count * 100 : 100;
                        await _hubContext.Clients.All.SendAsync("DepotMappingProgress", new
                        {
                            isProcessing = true,
                            totalMappings = unmappedDownloads.Count,
                            processedMappings = i + 1,
                            mappingsApplied = mappingsProcessed,
                            percentComplete = percentComplete,
                            status = "processing",
                            message = $"Processing depot mappings... {i + 1}/{unmappedDownloads.Count} ({mappingsProcessed} mapped)",
                            timestamp = DateTime.UtcNow
                        });

                        // Add small delay to prevent overwhelming external services
                        await Task.Delay(100);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error processing depot mapping for download {download.Id} with depot {download.DepotId}");
                }
            }

            // Save all changes
            await _context.SaveChangesAsync();

            // Send completion update
            await _hubContext.Clients.All.SendAsync("DepotMappingProgress", new
            {
                isProcessing = false,
                totalMappings = unmappedDownloads.Count,
                processedMappings = unmappedDownloads.Count,
                mappingsApplied = mappingsProcessed,
                percentComplete = 100.0,
                status = "complete",
                message = $"Depot mapping completed. Successfully mapped {mappingsProcessed} downloads.",
                timestamp = DateTime.UtcNow
            });

            _logger.LogInformation($"Post-processing completed. Successfully mapped {mappingsProcessed} downloads.");
            return mappingsProcessed;
        }
        catch (Exception ex)
        {
            // Send error update
            await _hubContext.Clients.All.SendAsync("DepotMappingProgress", new
            {
                isProcessing = false,
                status = "error",
                message = $"Depot mapping failed: {ex.Message}",
                timestamp = DateTime.UtcNow
            });

            _logger.LogError(ex, "Error during depot mapping post-processing");
            throw;
        }
    }

    private async Task StoreDepotMappingAsync(uint appId, uint depotId, string? appName, string source)
    {
        try
        {
            var existing = await _context.SteamDepotMappings
                .FirstOrDefaultAsync(m => m.DepotId == depotId && m.AppId == appId);

            if (existing != null)
            {
                bool changed = false;

                if (!string.IsNullOrWhiteSpace(appName) && string.IsNullOrWhiteSpace(existing.AppName))
                {
                    existing.AppName = appName;
                    changed = true;
                }

                if (changed)
                {
                    existing.Source = source;
                    existing.DiscoveredAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                }
                return;
            }

            var mapping = new SteamDepotMapping
            {
                DepotId = depotId,
                AppId = appId,
                AppName = appName,
                Source = source,
                DiscoveredAt = DateTime.UtcNow
            };

            _context.SteamDepotMappings.Add(mapping);
            await _context.SaveChangesAsync();
            _logger.LogDebug($"Stored depot mapping {depotId} -> {appId} ({appName ?? "unknown"}) via {source}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Failed to store depot mapping {depotId} -> {appId}");
        }
    }
}
