using Microsoft.EntityFrameworkCore;
using LancacheManager.Data;
using LancacheManager.Models;

namespace LancacheManager.Services;

public class GameImageCacheService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly HttpClient _httpClient;
    private readonly ILogger<GameImageCacheService> _logger;

    public GameImageCacheService(
        IServiceProvider serviceProvider,
        IHttpClientFactory httpClientFactory,
        ILogger<GameImageCacheService> logger)
    {
        _serviceProvider = serviceProvider;
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        _logger = logger;
    }

    public async Task<GameImage?> GetImageAsync(uint appId, string imageType = "header")
    {
        using var scope = _serviceProvider.CreateScope();
        using var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Try to get from cache first
        var cachedImage = await context.GameImages
            .FirstOrDefaultAsync(g => g.AppId == appId && g.ImageType == imageType);

        if (cachedImage != null)
        {
            // Update access stats
            cachedImage.LastAccessed = DateTime.UtcNow;
            cachedImage.AccessCount++;
            await context.SaveChangesAsync();
            return cachedImage;
        }

        // Not in cache, try to download from Steam
        var imageUrl = GetSteamImageUrl(appId, imageType);
        if (string.IsNullOrEmpty(imageUrl))
        {
            return null;
        }

        try
        {
            _logger.LogInformation($"Downloading image for app {appId}, type {imageType}");

            var response = await _httpClient.GetAsync(imageUrl);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning($"Failed to download image for app {appId}: {response.StatusCode}");
                return null;
            }

            var imageData = await response.Content.ReadAsByteArrayAsync();
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";

            // Create new cache entry
            var gameImage = new GameImage
            {
                AppId = appId,
                GameName = $"Steam App {appId}", // Will be updated by caller if known
                ImageType = imageType,
                ImageData = imageData,
                ContentType = contentType,
                CachedAt = DateTime.UtcNow,
                LastAccessed = DateTime.UtcNow,
                AccessCount = 1
            };

            context.GameImages.Add(gameImage);
            await context.SaveChangesAsync();

            return gameImage;
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning($"Timeout downloading image for app {appId}");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error downloading image for app {appId}");
            return null;
        }
    }

    public async Task<GameImage?> GetOrCreateImageAsync(uint appId, string gameName, string imageType = "header")
    {
        using var scope = _serviceProvider.CreateScope();
        using var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Try to get from cache first
        var cachedImage = await context.GameImages
            .FirstOrDefaultAsync(g => g.AppId == appId && g.ImageType == imageType);

        if (cachedImage != null)
        {
            // Update access stats and game name if needed
            cachedImage.LastAccessed = DateTime.UtcNow;
            cachedImage.AccessCount++;
            if (!string.IsNullOrEmpty(gameName) && cachedImage.GameName != gameName)
            {
                cachedImage.GameName = gameName;
            }
            await context.SaveChangesAsync();
            return cachedImage;
        }

        // Not in cache, try to download from Steam
        var imageUrl = GetSteamImageUrl(appId, imageType);
        if (string.IsNullOrEmpty(imageUrl))
        {
            return null;
        }

        try
        {
            _logger.LogInformation($"Downloading image for {gameName} (app {appId}), type {imageType}");

            var response = await _httpClient.GetAsync(imageUrl);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning($"Failed to download image for {gameName} (app {appId}): {response.StatusCode}");

                // Create a placeholder entry to avoid repeated failed attempts
                var placeholder = new GameImage
                {
                    AppId = appId,
                    GameName = gameName,
                    ImageType = imageType,
                    ImageData = Array.Empty<byte>(), // Empty data indicates failed download
                    ContentType = "application/octet-stream",
                    CachedAt = DateTime.UtcNow,
                    LastAccessed = DateTime.UtcNow,
                    AccessCount = 1
                };

                context.GameImages.Add(placeholder);
                await context.SaveChangesAsync();

                return null;
            }

            var imageData = await response.Content.ReadAsByteArrayAsync();
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";

            // Create new cache entry
            var gameImage = new GameImage
            {
                AppId = appId,
                GameName = gameName,
                ImageType = imageType,
                ImageData = imageData,
                ContentType = contentType,
                CachedAt = DateTime.UtcNow,
                LastAccessed = DateTime.UtcNow,
                AccessCount = 1
            };

            context.GameImages.Add(gameImage);
            await context.SaveChangesAsync();

            return gameImage;
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning($"Timeout downloading image for {gameName} (app {appId})");
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning($"Network error downloading image for {gameName} (app {appId}): {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error downloading image for {gameName} (app {appId})");
            return null;
        }
    }

    public async Task CleanupOldImagesAsync(int daysOld = 30)
    {
        using var scope = _serviceProvider.CreateScope();
        using var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cutoffDate = DateTime.UtcNow.AddDays(-daysOld);

        var oldImages = await context.GameImages
            .Where(g => g.LastAccessed < cutoffDate)
            .ToListAsync();

        if (oldImages.Any())
        {
            _logger.LogInformation($"Removing {oldImages.Count} old cached images");
            context.GameImages.RemoveRange(oldImages);
            await context.SaveChangesAsync();
        }
    }

    private string GetSteamImageUrl(uint appId, string imageType)
    {
        return imageType.ToLower() switch
        {
            "header" => $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg",
            "capsule" => $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/capsule_231x87.jpg",
            "hero" => $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/hero_capsule.jpg",
            "library" => $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg",
            _ => $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg"
        };
    }
}