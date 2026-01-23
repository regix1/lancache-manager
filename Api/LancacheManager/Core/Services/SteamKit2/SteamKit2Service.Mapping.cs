using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services.SteamKit2;

public partial class SteamKit2Service
{
    /// <summary>
    /// Clear game information from all downloads (used before full scans/GitHub imports)
    /// </summary>
    private async Task ClearDownloadGameDataAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            _logger.LogInformation("Clearing game information from Downloads table (GameName, GameImageUrl, GameAppId)");

            await context.Downloads
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.GameName, (string?)null)
                    .SetProperty(d => d.GameImageUrl, (string?)null)
                    .SetProperty(d => d.GameAppId, (uint?)null));

            _logger.LogInformation("Cleared game information from all downloads - ready for fresh mapping");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear download game data");
            throw;
        }
    }

    /// <summary>
    /// Manually apply depot mappings to existing downloads (called from UI)
    /// </summary>
    public async Task ManuallyApplyDepotMappings()
    {
        _logger.LogInformation("Manually applying depot mappings to downloads");

        // Wait a moment to ensure database operations have completed
        _logger.LogInformation("Waiting 2 seconds to ensure database is fully synced...");
        await Task.Delay(2000);

        // Reload depot mappings from database to ensure we have latest data
        _logger.LogInformation("Reloading depot mappings from database...");
        await LoadExistingDepotMappings();

        await UpdateDownloadsWithDepotMappings();
    }

    /// <summary>
    /// Update downloads that have depot IDs but no game information
    /// </summary>
    private async Task<(int updated, int notFound)> UpdateDownloadsWithDepotMappings()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Get downloads that have depot IDs but no game info (or missing image)
            var downloadsNeedingGameInfo = await context.Downloads
                .Where(d => d.DepotId.HasValue && (d.GameAppId == null || string.IsNullOrEmpty(d.GameImageUrl)))
                .ToListAsync();

            _logger.LogInformation($"Found {downloadsNeedingGameInfo.Count} downloads needing game info after PICS completion");

            int updated = 0;
            int notFound = 0;
            int processed = 0;
            int totalDownloads = downloadsNeedingGameInfo.Count;

            foreach (var download in downloadsNeedingGameInfo)
            {
                try
                {
                    uint? appId = download.GameAppId; // Use existing appId if available

                    // If no AppId yet, use owner ID from PICS data
                    if (!appId.HasValue && download.DepotId.HasValue)
                    {
                        // First, check in-memory owner mapping from PICS scan
                        if (_depotOwners.TryGetValue(download.DepotId.Value, out var ownerId))
                        {
                            appId = ownerId;
                            _logger.LogTrace($"Using PICS owner app {appId} for depot {download.DepotId}");
                        }
                        else
                        {
                            // Fallback to database owner lookup
                            var ownerApp = await context.SteamDepotMappings
                                .Where(m => m.DepotId == download.DepotId.Value && m.IsOwner)
                                .Select(m => m.AppId)
                                .FirstOrDefaultAsync();

                            if (ownerApp != 0)
                            {
                                appId = ownerApp;
                                _logger.LogTrace($"Using database owner app {appId} for depot {download.DepotId}");
                            }
                            else
                            {
                                // Last resort fallback: Use depot ID as app ID if app exists
                                // This handles cases where depot appears in logs but has no PICS mapping (depot ID = app ID)
                                var potentialAppId = download.DepotId.Value;
                                if (_appNames.ContainsKey(potentialAppId))
                                {
                                    appId = potentialAppId;
                                }
                                else
                                {
                                }
                            }
                        }
                    }

                    if (appId.HasValue)
                    {
                        download.GameAppId = appId.Value;

                        // First check if we have a PICS name (works for all apps including redistributables/launchers)
                        var picsName = _appNames.TryGetValue(appId.Value, out var name) ? name : null;

                        // Check for depot name (for redistributables like "Ubisoft Connect PC Client Content")
                        string? depotName = null;
                        if (download.DepotId.HasValue)
                        {
                            _depotNames.TryGetValue(download.DepotId.Value, out depotName);
                        }

                        // Get game info from Steam API
                        var gameInfo = await _steamService.GetGameInfoAsync(appId.Value);
                        if (gameInfo != null && !gameInfo.Name.StartsWith("Steam App ") && !gameInfo.Name.StartsWith("App "))
                        {
                            download.GameName = gameInfo.Name;
                            download.GameImageUrl = gameInfo.HeaderImage;
                            updated++;
                        }
                        else if (!string.IsNullOrEmpty(picsName) && !picsName.StartsWith("App "))
                        {
                            // Use PICS name if Steam Store API failed (e.g., redistributables/launchers)
                            download.GameName = picsName;
                            download.GameImageUrl = gameInfo?.HeaderImage ?? $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg";
                            updated++;
                        }
                        else if (!string.IsNullOrEmpty(depotName))
                        {
                            // Use depot name as fallback for shared redistributables
                            // e.g., "Ubisoft Connect PC Client Content", "RGL/SC Content"
                            download.GameName = depotName;
                            download.GameImageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg";
                            _logger.LogInformation("Using depot name for depot {DepotId}: {DepotName}", download.DepotId, depotName);
                            updated++;
                        }
                        else
                        {
                            download.GameName = $"Steam App {appId}";
                            download.GameImageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg";
                            updated++;
                        }
                    }
                    else
                    {
                        notFound++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to get game info for depot {download.DepotId}");
                    notFound++;
                }

                // Send progress updates EVERY download to keep connection alive
                // This prevents SignalR/HTTP connection timeouts during long-running mapping operations
                processed++;
                if (totalDownloads > 0)
                {
                    double percentComplete = (double)processed / totalDownloads * 100;
                    try
                    {
                        await _notifications.NotifyAllAsync(SignalREvents.DepotMappingProgress, new
                        {
                            status = "Applying mappings to downloads",
                            percentComplete,
                            processedMappings = processed,
                            totalMappings = totalDownloads,
                            mappingsApplied = updated,
                            isLoggedOn = IsSteamAuthenticated,
                            message = $"Applying depot mappings to downloads... {processed}/{totalDownloads}"
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to send depot mapping progress via SignalR");
                    }
                }
            }

            if (updated > 0)
            {
                await context.SaveChangesAsync();
                _logger.LogInformation($"Updated {updated} downloads with game information, {notFound} not found");
            }
            else
            {
                _logger.LogInformation($"No downloads updated, {notFound} depots without mappings");
            }

            // Send final 100% progress update
            if (totalDownloads > 0)
            {
                try
                {
                    await _notifications.NotifyAllAsync(SignalREvents.DepotMappingProgress, new
                    {
                        status = "Finalizing depot mappings",
                        percentComplete = 100.0,
                        processedMappings = totalDownloads,
                        totalMappings = totalDownloads,
                        mappingsApplied = updated,
                        isLoggedOn = IsSteamAuthenticated,
                        message = $"Depot mapping complete - {updated} downloads updated"
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send final depot mapping progress via SignalR");
                }
            }

            return (updated, notFound);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating downloads with depot mappings");
            return (0, 0);
        }
    }
}
