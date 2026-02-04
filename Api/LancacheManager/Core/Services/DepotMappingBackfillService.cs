using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.Base;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

/// <summary>
/// Background service that periodically checks for downloads with missing game names
/// and attempts to resolve them using available depot mappings.
///
/// This fixes the race condition where fast downloads complete before depot mappings
/// are available in the database.
/// </summary>
public class DepotMappingBackfillService : ScopedScheduledBackgroundService
{
    private readonly SteamKit2Service _steamKit2Service;
    private readonly SteamService _steamService;
    private readonly ISignalRNotificationService _notifications;
    private DateTime _lastBackfillTime = DateTime.MinValue;
    private int _consecutiveEmptyRuns = 0;

    protected override string ServiceName => "DepotMappingBackfillService";
    protected override TimeSpan StartupDelay => TimeSpan.FromSeconds(30); // Wait for other services to initialize
    protected override TimeSpan Interval => TimeSpan.FromSeconds(30); // Check every 30 seconds
    protected override bool RunOnStartup => true;

    public DepotMappingBackfillService(
        IServiceProvider serviceProvider,
        ILogger<DepotMappingBackfillService> logger,
        IConfiguration configuration,
        SteamKit2Service steamKit2Service,
        SteamService steamService,
        ISignalRNotificationService notifications)
        : base(serviceProvider, logger, configuration)
    {
        _steamKit2Service = steamKit2Service;
        _steamService = steamService;
        _notifications = notifications;
    }

    protected override async Task OnStartupAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("DepotMappingBackfillService starting - will periodically resolve missing game names");

        // Run initial backfill on startup
        await RunBackfillAsync(stoppingToken);
    }

    protected override async Task ExecuteScopedWorkAsync(
        IServiceProvider scopedServices,
        CancellationToken stoppingToken)
    {
        // Skip if we've had multiple empty runs in a row (adaptive throttling)
        // After 5 empty runs, slow down to every 5 minutes instead of 30 seconds
        if (_consecutiveEmptyRuns >= 5)
        {
            var timeSinceLastCheck = DateTime.UtcNow - _lastBackfillTime;
            if (timeSinceLastCheck < TimeSpan.FromMinutes(5))
            {
                return;
            }
        }

        await RunBackfillAsync(stoppingToken);
    }

    private async Task RunBackfillAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = ServiceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Find downloads that have depot IDs but no game name
            // Limit to recent downloads (last 24 hours) to avoid processing old data repeatedly
            var cutoffTime = DateTime.UtcNow.AddHours(-24);

            var downloadsNeedingMapping = await context.Downloads
                .Where(d => d.DepotId.HasValue
                    && d.GameAppId == null
                    && d.Service.ToLower() == "steam"
                    && d.StartTimeUtc > cutoffTime)
                .OrderByDescending(d => d.StartTimeUtc)
                .Take(50) // Process in batches to avoid overwhelming the system
                .ToListAsync(stoppingToken);

            if (downloadsNeedingMapping.Count == 0)
            {
                _consecutiveEmptyRuns++;
                _lastBackfillTime = DateTime.UtcNow;
                return;
            }

            // Reset adaptive throttling since we found work to do
            _consecutiveEmptyRuns = 0;

            Logger.LogInformation("Found {Count} downloads needing game name resolution", downloadsNeedingMapping.Count);

            // Get unique depot IDs that need resolution
            var depotIds = downloadsNeedingMapping
                .Where(d => d.DepotId.HasValue)
                .Select(d => d.DepotId!.Value)
                .Distinct()
                .ToList();

            // Batch load depot mappings from database
            var depotMappings = await context.SteamDepotMappings
                .Where(m => depotIds.Contains(m.DepotId) && m.IsOwner)
                .ToDictionaryAsync(m => m.DepotId, m => new { m.AppId, m.AppName }, stoppingToken);

            Logger.LogDebug("Found {Count} depot mappings available for {DepotCount} depot IDs",
                depotMappings.Count, depotIds.Count);

            int updated = 0;
            int stillMissing = 0;

            foreach (var download in downloadsNeedingMapping)
            {
                if (!download.DepotId.HasValue)
                    continue;

                var depotId = download.DepotId.Value;

                // Try to find mapping in database
                if (depotMappings.TryGetValue(depotId, out var mapping))
                {
                    download.GameAppId = mapping.AppId;

                    // Try to get game info from Steam API for the name and image
                    var gameInfo = await _steamService.GetGameInfoAsync(mapping.AppId);

                    if (gameInfo != null && !string.IsNullOrEmpty(gameInfo.Name)
                        && !gameInfo.Name.StartsWith("Steam App ")
                        && !gameInfo.Name.StartsWith("App "))
                    {
                        download.GameName = gameInfo.Name;
                        download.GameImageUrl = gameInfo.HeaderImage;
                    }
                    else if (!string.IsNullOrEmpty(mapping.AppName) && !mapping.AppName.StartsWith("App "))
                    {
                        // Fallback to mapping name
                        download.GameName = mapping.AppName;
                        download.GameImageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{mapping.AppId}/header.jpg";
                    }
                    else
                    {
                        // Last resort
                        download.GameName = $"Steam App {mapping.AppId}";
                        download.GameImageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{mapping.AppId}/header.jpg";
                    }

                    updated++;
                    Logger.LogDebug("Resolved depot {DepotId} -> {GameName} ({AppId})",
                        depotId, download.GameName, download.GameAppId);
                }
                else
                {
                    stillMissing++;
                }
            }

            if (updated > 0)
            {
                await context.SaveChangesAsync(stoppingToken);
                Logger.LogInformation("Backfill complete: resolved {Updated} downloads, {Missing} still missing mappings",
                    updated, stillMissing);

                // Notify frontend to refresh downloads display
                await _notifications.NotifyAllAsync(SignalREvents.DownloadsRefresh, new
                {
                    reason = "backfill",
                    updated,
                    timestamp = DateTime.UtcNow
                });
            }
            else if (stillMissing > 0)
            {
                Logger.LogDebug("Backfill: {Missing} downloads still waiting for depot mappings", stillMissing);
            }

            _lastBackfillTime = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during depot mapping backfill - will retry on next interval");
        }
    }
}
