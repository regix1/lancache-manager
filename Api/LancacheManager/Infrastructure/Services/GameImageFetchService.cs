using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Background service that periodically fetches and stores game banner images
/// for Steam and Epic games in the database.
/// Runs on startup (30s delay) and every 6 hours.
/// </summary>
public class GameImageFetchService : ScopedScheduledBackgroundService
{
    private readonly IStateService _stateService;
    private static readonly SemaphoreSlim _executionLock = new(1, 1);

    protected override string ServiceName => "GameImageFetch";
    protected override TimeSpan Interval => TimeSpan.FromMinutes(30);
    protected override bool RunOnStartup => true;
    protected override TimeSpan StartupDelay => TimeSpan.Zero;

    public GameImageFetchService(
        IServiceProvider serviceProvider,
        ILogger<GameImageFetchService> logger,
        IConfiguration configuration,
        IStateService stateService)
        : base(serviceProvider, logger, configuration)
    {
        _stateService = stateService;
    }

    protected override async Task OnStartupAsync(CancellationToken stoppingToken)
    {
        // Wait for setup to complete before running scheduled image fetches.
        // During setup, RustLogProcessorService triggers FetchImagesNowAsync directly
        // after log processing — we must not race with that.
        await _stateService.WaitForSetupCompletedAsync(stoppingToken);
    }

    /// <summary>
    /// Public trigger so other services (e.g. RustLogProcessorService) can request
    /// an immediate image fetch after populating downloads.
    /// </summary>
    public async Task FetchImagesNowAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[GameImageFetch] Triggered by external service");
        using var scope = _serviceProvider.CreateScope();
        await ExecuteScopedWorkAsync(scope.ServiceProvider, ct);
    }

    protected override async Task ExecuteScopedWorkAsync(
        IServiceProvider scopedServices,
        CancellationToken stoppingToken)
    {
        // Prevent concurrent execution — FetchImagesNowAsync and scheduled runs can overlap
        if (!await _executionLock.WaitAsync(0, stoppingToken))
        {
            _logger.LogDebug("[GameImageFetch] Skipping — another fetch is already running");
            return;
        }

        try
        {
            await ExecuteImageFetchAsync(scopedServices, stoppingToken);
        }
        finally
        {
            _executionLock.Release();
        }
    }

    private async Task ExecuteImageFetchAsync(
        IServiceProvider scopedServices,
        CancellationToken stoppingToken)
    {
        var db = scopedServices.GetRequiredService<AppDbContext>();
        var httpClientFactory = scopedServices.GetRequiredService<IHttpClientFactory>();
        var client = httpClientFactory.CreateClient("SteamImages");

        // 1. STEAM: Get all unique GameAppIds that don't have a GameImage yet
        var totalDownloads = await db.Downloads.CountAsync(stoppingToken);
        if (totalDownloads == 0)
        {
            _logger.LogInformation("[GameImageFetch] Downloads table is empty — log processing hasn't completed yet, will retry next cycle");
            return;
        }

        var steamAppIds = await db.Downloads
            .Where(d => d.GameAppId != null && d.GameAppId != 0 && !string.IsNullOrEmpty(d.GameName))
            .Select(d => d.GameAppId!.Value)
            .Distinct()
            .ToListAsync(stoppingToken);

        var existingSteamIds = await db.GameImages
            .Where(g => g.Service == "steam")
            .Select(g => g.AppId)
            .ToListAsync(stoppingToken);

        var missingSteamIds = steamAppIds
            .Select(id => id.ToString())
            .Except(existingSteamIds)
            .ToList();

        foreach (var batch in missingSteamIds.Chunk(50))
        {
            foreach (var appId in batch)
            {
                if (stoppingToken.IsCancellationRequested) return;
                await FetchSteamImageAsync(db, client, appId, stoppingToken);
            }
            await db.SaveChangesAsync(stoppingToken);
            await Task.Delay(1000, stoppingToken);
        }

        // 2. EPIC: Get all EpicGameMappings with ImageUrl that don't have a GameImage yet
        var epicMappings = await db.EpicGameMappings
            .Where(m => m.ImageUrl != null)
            .ToListAsync(stoppingToken);

        var existingEpicIds = await db.GameImages
            .Where(g => g.Service == "epicgames")
            .Select(g => g.AppId)
            .ToListAsync(stoppingToken);

        var missingEpicMappings = epicMappings
            .Where(m => !existingEpicIds.Contains(m.AppId))
            .ToList();

        foreach (var batch in missingEpicMappings.Chunk(50))
        {
            foreach (var mapping in batch)
            {
                if (stoppingToken.IsCancellationRequested) return;
                await FetchEpicImageAsync(db, client, mapping, stoppingToken);
            }
            await db.SaveChangesAsync(stoppingToken);
            await Task.Delay(1000, stoppingToken);
        }

        // 3. Re-fetch stale images (older than 30 days)
        var staleImages = await db.GameImages
            .Where(g => g.FetchedAtUtc < DateTime.UtcNow.AddDays(-30))
            .ToListAsync(stoppingToken);

        foreach (var batch in staleImages.Chunk(50))
        {
            foreach (var image in batch)
            {
                if (stoppingToken.IsCancellationRequested) return;
                await RefreshImageAsync(client, image, stoppingToken);
            }
            await db.SaveChangesAsync(stoppingToken);
            await Task.Delay(1000, stoppingToken);
        }

        _logger.LogInformation(
            "[GameImageFetch] Complete: {NewSteam} new Steam, {NewEpic} new Epic, {Stale} refreshed",
            missingSteamIds.Count, missingEpicMappings.Count, staleImages.Count);
    }

    // Minimum image size — Steam returns tiny ~1-2KB placeholder images for some apps
    private const int MinImageBytes = 5000;

    private async Task FetchSteamImageAsync(
        AppDbContext db,
        HttpClient client,
        string appId,
        CancellationToken ct)
    {
        // Try fetching the image using the given appId
        var imageBytes = await TryFetchSteamImageBytesAsync(db, client, appId, ct);

        if (imageBytes != null)
        {
            db.GameImages.Add(new GameImage
            {
                AppId = appId,
                Service = "steam",
                ImageData = imageBytes.Value.bytes,
                ContentType = imageBytes.Value.contentType,
                SourceUrl = imageBytes.Value.sourceUrl,
                FetchedAtUtc = DateTime.UtcNow
            });
            return;
        }

        // Try to find a parent app ID via SteamDepotMappings
        var parentAppId = await FindParentAppIdAsync(db, appId, ct);
        if (parentAppId == null)
        {
            _logger.LogDebug("[GameImageFetch] No valid image found for Steam app {AppId} and no parent app found", appId);
            return;
        }

        _logger.LogInformation("[GameImageFetch] No image for app {AppId}, trying parent app {ParentAppId}", appId, parentAppId);

        // Try fetching using the parent's app ID
        var parentBytes = await TryFetchSteamImageBytesAsync(db, client, parentAppId, ct);
        if (parentBytes != null)
        {
            // Store under the ORIGINAL appId so frontend lookups work
            db.GameImages.Add(new GameImage
            {
                AppId = appId,
                Service = "steam",
                ImageData = parentBytes.Value.bytes,
                ContentType = parentBytes.Value.contentType,
                SourceUrl = parentBytes.Value.sourceUrl,
                FetchedAtUtc = DateTime.UtcNow
            });
            _logger.LogInformation("[GameImageFetch] Successfully fetched image for app {AppId} using parent app {ParentAppId}", appId, parentAppId);
        }
        else
        {
            _logger.LogDebug("[GameImageFetch] No valid image found for app {AppId} or parent app {ParentAppId}", appId, parentAppId);
        }
    }

    /// <summary>
    /// Attempts to fetch a Steam image for the given appId from PICS URL and CDN patterns.
    /// Returns the image bytes, content type, and source URL if successful; null otherwise.
    /// </summary>
    private async Task<(byte[] bytes, string contentType, string sourceUrl)?> TryFetchSteamImageBytesAsync(
        AppDbContext db,
        HttpClient client,
        string appId,
        CancellationToken ct)
    {
        var urls = new List<string>();

        // Tier 1: PICS-sourced URL stored in Download.GameImageUrl (contains the hash path that newer games require)
        if (long.TryParse(appId, out var appIdLong))
        {
            var picsUrl = await db.Downloads
                .Where(d => d.GameAppId != null && d.GameAppId.Value == appIdLong
                            && !string.IsNullOrEmpty(d.GameImageUrl))
                .OrderByDescending(d => d.StartTimeUtc)
                .Select(d => d.GameImageUrl)
                .FirstOrDefaultAsync(ct);

            if (!string.IsNullOrEmpty(picsUrl))
            {
                urls.Add(picsUrl);
            }
        }

        // Tier 2: CDN shortcut patterns (work for older/established games)
        urls.Add($"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg");
        urls.Add($"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/capsule_616x353.jpg");

        foreach (var url in urls)
        {
            try
            {
                var response = await client.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode) continue;

                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length < MinImageBytes)
                {
                    _logger.LogDebug("[GameImageFetch] Skipping tiny image ({Size} bytes) for {AppId} from {Url}", bytes.Length, appId, url);
                    continue;
                }

                return (bytes, response.Content.Headers.ContentType?.MediaType ?? "image/jpeg", url);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[GameImageFetch] Failed to fetch Steam image {AppId} from {Url}", appId, url);
            }
        }

        _logger.LogDebug("[GameImageFetch] No valid image found for Steam app {AppId} after trying {Count} URLs", appId, urls.Count);
        return null;
    }

    /// <summary>
    /// Tries to find a parent app ID for a DLC/sub-app by checking SteamDepotMappings.
    /// Strategy: The appId itself may appear as a DepotId mapped to a different owner AppId,
    /// or depots near the appId (appId+1) may map to a different owner.
    /// Also checks the Downloads table for depots associated with this app that map to a different owner.
    /// </summary>
    private async Task<string?> FindParentAppIdAsync(AppDbContext db, string appId, CancellationToken ct)
    {
        if (!long.TryParse(appId, out var appIdLong))
            return null;

        // Strategy 1: Check if this appId appears as a DepotId in SteamDepotMappings
        // (common pattern: DLC app 3893180 has depot 3893180 owned by parent app 3893179)
        var candidateDepotIds = new List<long> { appIdLong };
        if (appIdLong + 1 <= uint.MaxValue)
            candidateDepotIds.Add(appIdLong + 1);
        if (appIdLong - 1 > 0)
            candidateDepotIds.Add(appIdLong - 1);

        var ownerMapping = await db.SteamDepotMappings
            .Where(m => candidateDepotIds.Contains(m.DepotId) && m.IsOwner && m.AppId != appIdLong)
            .Select(m => m.AppId)
            .FirstOrDefaultAsync(ct);

        if (ownerMapping != 0)
            return ownerMapping.ToString();

        // Strategy 2: Find depots for this app from Downloads, then look up their owner in SteamDepotMappings
        var depotIds = await db.Downloads
            .Where(d => d.GameAppId != null && d.GameAppId.Value == appIdLong && d.DepotId.HasValue)
            .Select(d => d.DepotId!.Value)
            .Distinct()
            .ToListAsync(ct);

        if (depotIds.Count > 0)
        {
            var parentFromDepot = await db.SteamDepotMappings
                .Where(m => depotIds.Contains(m.DepotId) && m.IsOwner && m.AppId != appIdLong)
                .Select(m => m.AppId)
                .FirstOrDefaultAsync(ct);

            if (parentFromDepot != 0)
                return parentFromDepot.ToString();
        }

        return null;
    }

    private async Task FetchEpicImageAsync(
        AppDbContext db,
        HttpClient client,
        EpicGameMapping mapping,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(mapping.ImageUrl)) return;

        var url = EpicApiDirectClient.EnsureResizeParams(mapping.ImageUrl);
        try
        {
            var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return;

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length < MinImageBytes)
            {
                _logger.LogDebug("[GameImageFetch] Skipping tiny image ({Size} bytes) for Epic {AppId} from {Url}", bytes.Length, mapping.AppId, url);
                return;
            }

            db.GameImages.Add(new GameImage
            {
                AppId = mapping.AppId,
                Service = "epicgames",
                ImageData = bytes,
                ContentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg",
                SourceUrl = url,
                FetchedAtUtc = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[GameImageFetch] Failed to fetch Epic image {AppId} from {Url}", mapping.AppId, url);
        }
    }

    private async Task RefreshImageAsync(
        HttpClient client,
        GameImage image,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(image.SourceUrl)) return;

        try
        {
            var response = await client.GetAsync(image.SourceUrl, ct);

            // If the primary SourceUrl fails for Steam images, fall back to the Tier2 CDN shortcut URL.
            // Steam PICS-sourced URLs contain hash paths that can expire/404.
            if (!response.IsSuccessStatusCode && image.Service == "steam")
            {
                var tier2Url = $"https://cdn.akamai.steamstatic.com/steam/apps/{image.AppId}/header.jpg";
                _logger.LogDebug("[GameImageFetch] Primary URL failed ({Status}) for Steam {AppId}, trying Tier2: {Url}",
                    (int)response.StatusCode, image.AppId, tier2Url);
                response = await client.GetAsync(tier2Url, ct);

                if (response.IsSuccessStatusCode)
                {
                    // Update SourceUrl so future refreshes use the working URL
                    image.SourceUrl = tier2Url;
                }
            }

            if (!response.IsSuccessStatusCode) return;

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length < MinImageBytes)
            {
                _logger.LogDebug("[GameImageFetch] Skipping tiny image ({Size} bytes) during refresh for {AppId}", bytes.Length, image.AppId);
                return;
            }

            image.ImageData = bytes;
            image.ContentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            image.FetchedAtUtc = DateTime.UtcNow;
            image.UpdatedAtUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[GameImageFetch] Failed to refresh image {AppId}", image.AppId);
        }
    }
}
