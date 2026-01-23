using System.Collections.Concurrent;
using LancacheManager.Models;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Core.Interfaces;
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
    /// </summary>
    [HttpGet("{appId}/header")]
    public async Task<IActionResult> GetGameHeaderImage(
        uint appId, 
        [FromQuery] string? type = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Determine if we want the higher-res capsule image
            var useCapsule = string.Equals(type, "capsule", StringComparison.OrdinalIgnoreCase);
            var cacheKey = useCapsule ? appId * 10 + 1 : appId;
            var etagSuffix = useCapsule ? "-capsule" : "";

            // Check if this app has recently failed - avoid repeated Steam requests
            if (_failedImageCache.TryGetValue(cacheKey, out var failedTime))
            {
                if (DateTime.UtcNow - failedTime < _failedCacheDuration)
                {
                    _logger.LogTrace($"Skipping cached failed image for app {appId}");
                    return NotFound(new GameImageErrorResponse { Error = $"Game image not available for app {appId}" });
                }
                else
                {
                    _failedImageCache.TryRemove(cacheKey, out _);
                }
            }

            // Determine image URL based on type
            string imageUrl;
            if (useCapsule)
            {
                imageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/capsule_616x353.jpg";
            }
            else
            {
                // Get current image URL from database (needed for PICS update detection)
                var download = await _context.Downloads
                    .Where(d => d.GameAppId == appId && !string.IsNullOrEmpty(d.GameImageUrl))
                    .OrderByDescending(d => d.StartTimeUtc)
                    .FirstOrDefaultAsync(cancellationToken);

                imageUrl = download?.GameImageUrl
                    ?? $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg";
            }

            // FAST PATH: Check cache first
            var cachedResult = await _imageCacheService.GetCachedImageAsync(cacheKey, imageUrl, cancellationToken);

            if (cachedResult.HasValue)
            {
                var (imageBytes, contentType) = cachedResult.Value;
                Response.Headers["Cache-Control"] = "public, max-age=86400";
                Response.Headers["ETag"] = $"\"{appId}{etagSuffix}\"";
                return File(imageBytes, contentType);
            }

            // SLOW PATH: Download and cache the image
            var result = await _imageCacheService.GetOrDownloadImageAsync(cacheKey, imageUrl, cancellationToken);

            if (result.HasValue)
            {
                var (imageBytes, contentType) = result.Value;
                Response.Headers["Cache-Control"] = "public, max-age=86400";
                Response.Headers["ETag"] = $"\"{appId}{etagSuffix}\"";
                return File(imageBytes, contentType);
            }

            // If capsule failed, fall back to header
            if (useCapsule)
            {
                _logger.LogDebug($"Capsule image not available for app {appId}, falling back to header");
                return await GetGameHeaderImage(appId, null, cancellationToken);
            }

            _failedImageCache.TryAdd(cacheKey, DateTime.UtcNow);
            return NotFound(new GameImageErrorResponse { Error = $"Steam header image not available for app {appId}" });
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogWarning($"Timeout fetching Steam image for app {appId}");
            _failedImageCache.TryAdd(appId, DateTime.UtcNow);
            return StatusCode(504, new GameImageErrorResponse { Error = "Request timeout fetching game header image" });
        }
        catch (TaskCanceledException)
        {
            return StatusCode(499, new GameImageErrorResponse { Error = "Request cancelled" });
        }
    }

}
