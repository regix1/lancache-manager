using System.Collections.Concurrent;
using LancacheManager.Data;
using LancacheManager.Infrastructure.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
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
                    return NotFound(new { error = $"Game image not available for app {appId}" });
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

            if (download?.GameImageUrl == null)
            {
                _logger.LogTrace($"No game image URL found for app {appId} in database");
                return NotFound(new { error = $"Game image URL not found for app {appId}" });
            }

            // FAST PATH with PICS validation: Check cache and validate URL hasn't changed
            var cachedResult = await _imageCacheService.GetCachedImageAsync(appId, download.GameImageUrl, cancellationToken);

            if (cachedResult.HasValue)
            {
                var (imageBytes, contentType) = cachedResult.Value;

                // Add cache headers with ETag for browser caching
                Response.Headers["Cache-Control"] = "public, max-age=86400"; // Cache for 24 hours
                Response.Headers["ETag"] = $"\"{appId}\"";

                return File(imageBytes, contentType);
            }

            // SLOW PATH: Not in cache or URL changed - download and cache the image
            var result = await _imageCacheService.GetOrDownloadImageAsync(appId, download.GameImageUrl, cancellationToken);

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

            return NotFound(new { error = $"Steam header image not available for app {appId}" });
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogWarning($"Timeout fetching Steam header image for app {appId}");
            _failedImageCache.TryAdd(appId, DateTime.UtcNow);
            return StatusCode(504, new { error = "Request timeout fetching game header image" });
        }
        catch (TaskCanceledException)
        {
            return StatusCode(499, new { error = "Request cancelled" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error proxying Steam header image for app {appId}");
            return StatusCode(500, new { error = "Failed to fetch game header image" });
        }
    }

}