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
    protected override string ServiceName => "GameImageFetch";
    protected override TimeSpan Interval => TimeSpan.FromMinutes(30);
    protected override bool RunOnStartup => true;
    protected override TimeSpan StartupDelay => TimeSpan.FromMinutes(3);

    public GameImageFetchService(
        IServiceProvider serviceProvider,
        ILogger<GameImageFetchService> logger,
        IConfiguration configuration)
        : base(serviceProvider, logger, configuration) { }

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
            .Where(d => d.GameAppId != null && d.GameAppId != 0)
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
        // Build URL list: prefer PICS-sourced URL from Downloads table (has hashed path),
        // then fall back to CDN shortcut patterns (older games still use these)
        var urls = new List<string>();

        // Tier 1: PICS-sourced URL stored in Download.GameImageUrl (contains the hash path that newer games require)
        var picsUrl = await db.Downloads
            .Where(d => d.GameAppId != null && d.GameAppId.Value.ToString() == appId
                        && !string.IsNullOrEmpty(d.GameImageUrl))
            .OrderByDescending(d => d.StartTimeUtc)
            .Select(d => d.GameImageUrl)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrEmpty(picsUrl))
        {
            urls.Add(picsUrl);
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

                db.GameImages.Add(new GameImage
                {
                    AppId = appId,
                    Service = "steam",
                    ImageData = bytes,
                    ContentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg",
                    SourceUrl = url,
                    FetchedAtUtc = DateTime.UtcNow
                });
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[GameImageFetch] Failed to fetch Steam image {AppId} from {Url}", appId, url);
            }
        }

        _logger.LogDebug("[GameImageFetch] No valid image found for Steam app {AppId} after trying {Count} URLs", appId, urls.Count);
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
            if (bytes.Length == 0) return;

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
            if (!response.IsSuccessStatusCode) return;

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0) return;

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
