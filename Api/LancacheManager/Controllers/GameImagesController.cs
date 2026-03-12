using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LancacheManager.Models;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for game image proxying
/// Handles Steam game header image retrieval with CORS workaround and caching
/// </summary>
[ApiController]
[Route("api/game-images")]
public class GameImagesController : ControllerBase
{
    private readonly ILogger<GameImagesController> _logger;
    private readonly AppDbContext _context;
    private readonly IImageCacheService _imageCacheService;

    // Cache of failed image fetches to avoid repeated 404 warnings (AppId -> timestamp)
    private static readonly ConcurrentDictionary<uint, DateTime> _failedImageCache = new();
    private static readonly TimeSpan _failedCacheDuration = TimeSpan.FromHours(24);

    public GameImagesController(
        ILogger<GameImagesController> logger,
        AppDbContext context,
        IImageCacheService imageCacheService)
    {
        _logger = logger;
        _context = context;
        _imageCacheService = imageCacheService;
    }

    /// <summary>
    /// Proxy Steam game images to avoid CORS issues.
    /// Supports different image types via query parameter:
    /// - header (default): 460x215 - standard Steam header
    /// - capsule: 616x353 - higher resolution, better for mobile
    ///
    /// Fallback chain:
    /// 1. Header: DB GameImageUrl -> Steam CDN header -> 404
    /// 2. Capsule: Steam CDN capsule -> DB GameImageUrl fallback -> 404
    /// </summary>
    [HttpGet("{appId}/header")]
    public async Task<IActionResult> GetGameHeaderImage(
        uint appId,
        [FromQuery] string? type = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var useCapsule = string.Equals(type, "capsule", StringComparison.OrdinalIgnoreCase);
            var cacheKey = useCapsule ? appId * 10 + 1 : appId;
            var etagSuffix = useCapsule ? "-capsule" : "";

            // Check if this app has recently failed all fallbacks
            if (_failedImageCache.TryGetValue(cacheKey, out var failedTime))
            {
                if (DateTime.UtcNow - failedTime < _failedCacheDuration)
                {
                    _logger.LogTrace("Skipping cached failed image for app {AppId}", appId);
                    return NotFound(new GameImageErrorResponse { Error = $"Game image not available for app {appId}" });
                }
                _failedImageCache.TryRemove(cacheKey, out _);
            }

            // Get stored image URL from database (populated from Steam API/PICS)
            var dbImageUrl = await GetDatabaseImageUrlAsync(appId, cancellationToken);

            // Build list of URLs to try in order
            var urlsToTry = new List<string>();

            if (useCapsule)
            {
                // Capsule: try CDN capsule first, then fall back to DB header URL
                urlsToTry.Add($"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/capsule_616x353.jpg");
                if (!string.IsNullOrEmpty(dbImageUrl))
                {
                    urlsToTry.Add(dbImageUrl);
                }
            }
            else
            {
                // Header: try DB URL first (may have custom/updated URL), then CDN fallback
                if (!string.IsNullOrEmpty(dbImageUrl))
                {
                    urlsToTry.Add(dbImageUrl);
                }
                var cdnHeaderUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg";
                if (dbImageUrl != cdnHeaderUrl)
                {
                    urlsToTry.Add(cdnHeaderUrl);
                }
            }

            // Try each URL in order until one succeeds
            foreach (var imageUrl in urlsToTry)
            {
                var result = await TryGetImageAsync(cacheKey, imageUrl, cancellationToken);
                if (result.HasValue)
                {
                    var (imageBytes, contentType) = result.Value;
                    Response.Headers["Cache-Control"] = "public, max-age=86400";
                    Response.Headers["ETag"] = $"\"{appId}{etagSuffix}\"";
                    return File(imageBytes, contentType);
                }
            }

            // All URLs failed - cache the failure and return 404
            _failedImageCache.TryAdd(cacheKey, DateTime.UtcNow);
            var imageType = useCapsule ? "capsule" : "header";
            return NotFound(new GameImageErrorResponse { Error = $"Steam {imageType} image not available for app {appId}" });
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogWarning("Timeout fetching Steam image for app {AppId}", appId);
            _failedImageCache.TryAdd(appId, DateTime.UtcNow);
            return StatusCode(504, new GameImageErrorResponse { Error = "Request timeout fetching game header image" });
        }
        catch (TaskCanceledException)
        {
            return StatusCode(499, new GameImageErrorResponse { Error = "Request cancelled" });
        }
    }

    /// <summary>
    /// Proxy Epic game images to avoid CORS issues.
    /// Looks up the image URL from EpicGameMappings table.
    /// Falls back to 404 if no image URL is stored.
    /// </summary>
    [HttpGet("epic/{epicAppId}/header")]
    public async Task<IActionResult> GetEpicGameHeaderImage(
        string epicAppId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Generate a deterministic uint cache key from the string Epic app ID.
            // OR with 0x80000000u to set the high bit, ensuring no collision with Steam uint IDs (which are always small).
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes("epic_" + epicAppId));
            var cacheKey = BitConverter.ToUInt32(hashBytes, 0) | 0x80000000u;

            // Check if this app has recently failed all fallbacks
            if (_failedImageCache.TryGetValue(cacheKey, out var failedTime))
            {
                if (DateTime.UtcNow - failedTime < _failedCacheDuration)
                {
                    _logger.LogTrace("Skipping cached failed image for Epic app {EpicAppId}", epicAppId);
                    return NotFound(new GameImageErrorResponse { Error = $"Game image not available for Epic app {epicAppId}" });
                }
                _failedImageCache.TryRemove(cacheKey, out _);
            }

            // Look up the Epic game mapping to get the image URL
            var mapping = await _context.EpicGameMappings
                .FirstOrDefaultAsync(m => m.AppId == epicAppId, cancellationToken);

            if (mapping == null || string.IsNullOrEmpty(mapping.ImageUrl))
            {
                return NotFound(new GameImageErrorResponse { Error = $"No image available for Epic app {epicAppId}" });
            }

            var imageUrl = EpicApiDirectClient.EnsureResizeParams(mapping.ImageUrl);

            var result = await TryGetImageAsync(cacheKey, imageUrl, cancellationToken);
            if (result.HasValue)
            {
                var (imageBytes, contentType) = result.Value;
                Response.Headers["Cache-Control"] = "public, max-age=3600";
                Response.Headers["ETag"] = $"\"epic-{epicAppId}-{imageUrl.GetHashCode():x}\"";
                return File(imageBytes, contentType);
            }

            // Image download failed - cache the failure and return 404
            _failedImageCache.TryAdd(cacheKey, DateTime.UtcNow);
            return NotFound(new GameImageErrorResponse { Error = $"Epic header image not available for app {epicAppId}" });
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogWarning("Timeout fetching Epic image for app {EpicAppId}", epicAppId);
            var timeoutCacheKey = BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes("epic_" + epicAppId)), 0) | 0x80000000u;
            _failedImageCache.TryAdd(timeoutCacheKey, DateTime.UtcNow);
            return StatusCode(504, new GameImageErrorResponse { Error = "Request timeout fetching Epic game header image" });
        }
        catch (TaskCanceledException)
        {
            return StatusCode(499, new GameImageErrorResponse { Error = "Request cancelled" });
        }
    }

    /// <summary>
    /// Clears the game image disk cache, failure markers, and in-memory failed-fetch cache.
    /// Forces all images to be re-downloaded from source on next request.
    /// </summary>
    [HttpDelete("cache")]
    public async Task<IActionResult> ClearImageCache()
    {
        _logger.LogInformation("Clearing game image cache (disk + in-memory)");

        // Clear in-memory failed-fetch cache
        var failedCount = _failedImageCache.Count;
        _failedImageCache.Clear();

        // Clear disk cache (images, metadata, failure markers)
        await _imageCacheService.ClearCacheAsync();

        _logger.LogInformation("Image cache cleared: {FailedCacheEntries} in-memory entries removed", failedCount);

        return Ok(new { message = "Image cache cleared", failedCacheEntriesCleared = failedCount });
    }

    /// <summary>
    /// Get the stored image URL from the database for a game
    /// </summary>
    private async Task<string?> GetDatabaseImageUrlAsync(uint appId, CancellationToken cancellationToken)
    {
        var download = await _context.Downloads
            .Where(d => d.GameAppId == appId && !string.IsNullOrEmpty(d.GameImageUrl))
            .OrderByDescending(d => d.StartTimeUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return download?.GameImageUrl;
    }

    /// <summary>
    /// Try to get an image from cache or download it
    /// </summary>
    private async Task<(byte[] ImageBytes, string ContentType)?> TryGetImageAsync(
        uint cacheKey,
        string imageUrl,
        CancellationToken cancellationToken)
    {
        // FAST PATH: Check cache first
        var cachedResult = await _imageCacheService.GetCachedImageAsync(cacheKey, imageUrl, cancellationToken);
        if (cachedResult.HasValue)
        {
            return cachedResult.Value;
        }

        // SLOW PATH: Download and cache the image
        return await _imageCacheService.GetOrDownloadImageAsync(cacheKey, imageUrl, cancellationToken);
    }

}
