using System.Security.Cryptography;
using LancacheManager.Models;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for game image serving.
/// Serves images from the local DB/cache only — no CDN fallback.
/// Images are pre-fetched by the background image download service.
/// </summary>
[ApiController]
[Route("api/game-images")]
[AllowAnonymous]
public class GameImagesController : ControllerBase
{
    private readonly ILogger<GameImagesController> _logger;
    private readonly IImageCacheService _imageCacheService;
    private readonly EpicMappingService? _epicMappingService;
    private readonly GameImageFetchService _gameImageFetchService;

    // Bumped on cache clear so the frontend can build cache-busted image URLs
    private static long _cacheGeneration = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public GameImagesController(
        ILogger<GameImagesController> logger,
        IImageCacheService imageCacheService,
        GameImageFetchService gameImageFetchService,
        EpicMappingService? epicMappingService = null)
    {
        _logger = logger;
        _imageCacheService = imageCacheService;
        _gameImageFetchService = gameImageFetchService;
        _epicMappingService = epicMappingService;
    }

    /// <summary>
    /// Returns the cached Steam game header image.
    /// Returns 404 if no image is stored in the DB for this app.
    /// </summary>
    [HttpGet("{appId}/header")]
    public async Task<IActionResult> GetGameHeaderImageAsync(
        int appId,
        CancellationToken cancellationToken = default)
    {
        var (imageData, contentType) = await _imageCacheService.GetCachedImageAsync(
            appId.ToString(), "steam", cancellationToken) ?? default;

        if (imageData == null)
        {
            return NotFound(new GameImageErrorResponse { Error = $"Game image not available for app {appId}" });
        }

        return ReturnImageWithCaching(imageData, contentType ?? "image/jpeg", appId.ToString());
    }

    /// <summary>
    /// Returns the cached Epic game header image.
    /// Returns 404 if no image is stored in the DB for this Epic app.
    /// </summary>
    [HttpGet("epic/{epicAppId}/header")]
    public async Task<IActionResult> GetEpicGameHeaderImageAsync(
        string epicAppId,
        CancellationToken cancellationToken = default)
    {
        var (imageData, contentType) = await _imageCacheService.GetCachedImageAsync(
            epicAppId, "epicgames", cancellationToken) ?? default;

        if (imageData == null)
        {
            return NotFound(new GameImageErrorResponse { Error = $"Game image not available for Epic app {epicAppId}" });
        }

        return ReturnImageWithCaching(imageData, contentType ?? "image/jpeg", $"epic-{epicAppId}");
    }

    /// <summary>
    /// Returns the current cache generation so the frontend can build cache-busted image URLs on page load.
    /// </summary>
    [HttpGet("cache-version")]
    public IActionResult GetCacheVersion() => Ok(new { version = _cacheGeneration });

    /// <summary>
    /// Clears the game image cache and optionally triggers an Epic image URL refresh.
    /// </summary>
    [HttpDelete("cache")]
    public async Task<IActionResult> ClearImageCacheAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("=== ClearImageCache START ===");

        await _imageCacheService.ClearCacheAsync();
        _logger.LogInformation("Image cache cleared");

        var epicUrlsRefreshed = 0;
        if (_epicMappingService != null)
        {
            if (_epicMappingService.IsAuthenticated)
            {
                _logger.LogInformation("Epic is authenticated - triggering immediate catalog refresh for image URLs");
                try
                {
                    epicUrlsRefreshed = await _epicMappingService.RefreshImageUrlsAsync(cancellationToken);
                    _logger.LogInformation("Epic image URL refresh complete: {Count} URLs updated", epicUrlsRefreshed);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Epic image URL refresh failed - existing URLs preserved");
                }
            }
            else
            {
                _logger.LogInformation("Epic not authenticated - skipping image URL refresh (existing URLs preserved)");
            }
        }

        _cacheGeneration = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        _logger.LogInformation("Triggering immediate image re-fetch after cache clear");
        try
        {
            await _gameImageFetchService.FetchImagesNowAsync(cancellationToken);
            _logger.LogInformation("Image re-fetch completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Image re-fetch after cache clear failed");
        }

        _logger.LogInformation("=== ClearImageCache END === Epic URLs refreshed: {Epic}", epicUrlsRefreshed);

        return Ok(new
        {
            message = "Image cache cleared and re-fetch triggered",
            epicImageUrlsRefreshed = epicUrlsRefreshed,
            cacheGeneration = _cacheGeneration
        });
    }

    /// <summary>
    /// Returns an image response with proper cache headers and ETag-based conditional request support.
    /// Uses no-cache so the browser always revalidates, but gets efficient 304 responses when the image hasn't changed.
    /// </summary>
    private IActionResult ReturnImageWithCaching(byte[] imageBytes, string contentType, string etagPrefix)
    {
        var hash = Convert.ToHexString(SHA256.HashData(imageBytes)).ToLowerInvariant();
        var etag = $"\"{etagPrefix}-{hash}\"";

        Response.Headers["Cache-Control"] = "public, max-age=3600, must-revalidate";
        Response.Headers["ETag"] = etag;

        var ifNoneMatch = Request.Headers["If-None-Match"].ToString();
        if (!string.IsNullOrEmpty(ifNoneMatch) && (ifNoneMatch.Contains(etag) || ifNoneMatch.Trim() == "*"))
        {
            return StatusCode(304);
        }

        return File(imageBytes, contentType);
    }
}
