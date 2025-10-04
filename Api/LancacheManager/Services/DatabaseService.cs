using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using LancacheManager.Data;
using LancacheManager.Models;
using LancacheManager.Hubs;
using LancacheManager.Services;
using LancacheManager.Utilities;

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
    private readonly StatsCache _statsCache;

    public DatabaseService(
        AppDbContext context,
        IHubContext<DownloadHub> hubContext,
        ILogger<DatabaseService> logger,
        SteamService steamService,
        SteamKit2Service steamKit2Service,
        PicsDataService picsDataService,
        IPathResolver pathResolver,
        StatsCache statsCache)
    {
        _context = context;
        _hubContext = hubContext;
        _logger = logger;
        _steamService = steamService;
        _steamKit2Service = steamKit2Service;
        _picsDataService = picsDataService;
        _pathResolver = pathResolver;
        _statsCache = statsCache;
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
            // Only check IsActive flag - cleanup service handles marking old downloads as complete
            return await _context.Downloads
                .Where(d => d.IsActive)
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
            // Find all Steam downloads that have depot IDs but no game app ID or missing image
            var unmappedDownloads = await _context.Downloads
                .Where(d => d.Service.ToLower() == "steam"
                       && d.DepotId.HasValue
                       && (!d.GameAppId.HasValue || string.IsNullOrEmpty(d.GameImageUrl)))
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
                    uint? appId = download.GameAppId; // Use existing appId if available

                    // If no AppId yet, try to find it
                    if (!appId.HasValue)
                    {
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
                    }

                    // If we have an app ID, populate the game info
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

            // Invalidate cache so newly mapped downloads appear immediately
            _statsCache.InvalidateDownloads();
            _logger.LogDebug("Invalidated downloads cache after depot mapping");

            // Trigger UI refresh to show newly mapped downloads
            await _hubContext.Clients.All.SendAsync("DownloadsRefresh", new
            {
                message = "Depot mappings updated",
                timestamp = DateTime.UtcNow
            });

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

    public async Task<long> GetLogEntryCountAsync()
    {
        try
        {
            return await _context.LogEntries.LongCountAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to count log entries");
            return 0;
        }
    }
}
