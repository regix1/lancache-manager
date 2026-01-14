using System.Collections.Concurrent;
using LancacheManager.Models;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Core.Interfaces.Services;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for game image proxying
/// Handles Steam game header image retrieval with CORS workaround and caching
/// </summary>
[ApiController]
[Route("api/game-images")]
[RequireGuestSession]
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
    /// Proxy Steam game header images to avoid CORS issues
    /// </summary>
    [HttpGet("{appId}/header")]
    public async Task<IActionResult> GetGameHeaderImage(uint appId, CancellationToken cancellationToken = default)
    {
        try
        {
            // Check if this app has recently failed - avoid repeated Steam requests
            if (_failedImageCache.TryGetValue(appId, out var failedTime))
            {
                if (DateTime.UtcNow - failedTime < _failedCacheDuration)
                {
                    _logger.LogTrace($"Skipping cached failed image for app {appId}");
                    return NotFound(new GameImageErrorResponse { Error = $"Game image not available for app {appId}" });
                }
                else
                {
                    // Cache expired, remove it and try again
                    _failedImageCache.TryRemove(appId, out _);
                }
            }

            // Get current image URL from database (needed for PICS update detection)
            var download = await _context.Downloads
                .Where(d => d.GameAppId == appId && !string.IsNullOrEmpty(d.GameImageUrl))
                .OrderByDescending(d => d.StartTimeUtc)
                .FirstOrDefaultAsync(cancellationToken);

            // Use database URL if available, otherwise fallback to Steam CDN (for mock mode/screenshots)
            var imageUrl = download?.GameImageUrl
                ?? $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg";

            // FAST PATH with PICS validation: Check cache and validate URL hasn't changed
            var cachedResult = await _imageCacheService.GetCachedImageAsync(appId, imageUrl, cancellationToken);

            if (cachedResult.HasValue)
            {
                var (imageBytes, contentType) = cachedResult.Value;

                // Add cache headers with ETag for browser caching
                Response.Headers["Cache-Control"] = "public, max-age=86400"; // Cache for 24 hours
                Response.Headers["ETag"] = $"\"{appId}\"";

                return File(imageBytes, contentType);
            }

            // SLOW PATH: Not in cache or URL changed - download and cache the image
            var result = await _imageCacheService.GetOrDownloadImageAsync(appId, imageUrl, cancellationToken);

            if (result.HasValue)
            {
                var (imageBytes, contentType) = result.Value;

                // Add cache headers with ETag for browser caching
                Response.Headers["Cache-Control"] = "public, max-age=86400"; // Cache for 24 hours
                Response.Headers["ETag"] = $"\"{appId}\"";

                return File(imageBytes, contentType);
            }

            // Image fetch failed - cache this failure
            _failedImageCache.TryAdd(appId, DateTime.UtcNow);

            return NotFound(new GameImageErrorResponse { Error = $"Steam header image not available for app {appId}" });
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogWarning($"Timeout fetching Steam header image for app {appId}");
            _failedImageCache.TryAdd(appId, DateTime.UtcNow);
            return StatusCode(504, new GameImageErrorResponse { Error = "Request timeout fetching game header image" });
        }
        catch (TaskCanceledException)
        {
            return StatusCode(499, new GameImageErrorResponse { Error = "Request cancelled" });
        }
    }

}
