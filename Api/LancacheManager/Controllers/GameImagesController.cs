using System.Security.Cryptography;
using LancacheManager.Models;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for game image serving.
/// Serves images from the local DB/cache only - no CDN fallback.
/// Images are pre-fetched by the background image download service.
/// </summary>
[ApiController]
[Route("api/game-images")]
public class GameImagesController : ControllerBase
{
    private readonly ILogger<GameImagesController> _logger;
    private readonly IImageCacheService _imageCacheService;
    private readonly EpicMappingService? _epicMappingService;
    private readonly GameImageFetchService _gameImageFetchService;
    private readonly AppDbContext _context;

    // Bumped on cache clear or new image fetch so the frontend can build cache-busted image URLs
    private static long _cacheGeneration = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Gets the current image cache generation number.</summary>
    public static long CacheGeneration => Interlocked.Read(ref _cacheGeneration);

    /// <summary>Increments the cache generation to a new timestamp, invalidating cached image URLs.</summary>
    public static void IncrementCacheGeneration() =>
        Interlocked.Exchange(ref _cacheGeneration, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    public GameImagesController(
        ILogger<GameImagesController> logger,
        IImageCacheService imageCacheService,
        GameImageFetchService gameImageFetchService,
        AppDbContext context,
        EpicMappingService? epicMappingService = null)
    {
        _logger = logger;
        _imageCacheService = imageCacheService;
        _gameImageFetchService = gameImageFetchService;
        _context = context;
        _epicMappingService = epicMappingService;
    }

    /// <summary>
    /// Returns the cached Steam game header image.
    /// Returns 404 if no image is stored in the DB for this app.
    /// </summary>
    [HttpGet("{appId}/header")]
    [AllowAnonymous]
    public async Task<IActionResult> GetHeaderImageAsync(
        int appId,
        CancellationToken cancellationToken = default)
    {
        var (imageData, contentType) = await _imageCacheService.GetImageAsync(
            appId.ToString(), "steam", cancellationToken) ?? default;

        if (imageData == null)
        {
            return NotFound(new GameImageErrorResponse { Error = $"Game image not available for app {appId}" });
        }

        return ImageResponse(imageData, contentType ?? "image/jpeg", appId.ToString());
    }

    /// <summary>
    /// Returns the cached Epic game header image.
    /// Returns 404 if no image is stored in the DB for this Epic app.
    /// </summary>
    [HttpGet("epic/{epicAppId}/header")]
    [AllowAnonymous]
    public async Task<IActionResult> GetEpicHeaderImageAsync(
        string epicAppId,
        CancellationToken cancellationToken = default)
    {
        var (imageData, contentType) = await _imageCacheService.GetImageAsync(
            epicAppId, "epicgames", cancellationToken) ?? default;

        if (imageData == null)
        {
            return NotFound(new GameImageErrorResponse { Error = $"Game image not available for Epic app {epicAppId}" });
        }

        return ImageResponse(imageData, contentType ?? "image/jpeg", $"epic-{epicAppId}");
    }

    /// <summary>
    /// Returns the cached banner image for a name-keyed service (Blizzard/Riot) whose games are
    /// identified only by GameName. The slug is the normalized GameName produced by
    /// NameKeyedBannerSource.Slug, and the service is the canonical "blizzard"/"riot" key.
    /// Returns 404 if no image is stored for this (slug, service).
    /// </summary>
    [HttpGet("name/{service}/{slug}/header")]
    [AllowAnonymous]
    public async Task<IActionResult> GetNameKeyedHeaderImageAsync(
        string service,
        string slug,
        CancellationToken cancellationToken = default)
    {
        var canonicalService = NameKeyedBannerSource.NormalizeService(service);
        if (canonicalService == null)
        {
            return NotFound(new GameImageErrorResponse { Error = $"Unsupported name-keyed service '{service}'" });
        }

        var (imageData, contentType) = await _imageCacheService.GetImageAsync(
            slug, canonicalService, cancellationToken) ?? default;

        if (imageData != null)
        {
            return ImageResponse(imageData, contentType ?? "image/jpeg", $"{canonicalService}-{slug}");
        }

        // Instant path: curated embedded banners (Blizzard/Riot) live in the assembly, so serve them
        // directly even before any GameImage row has been fetched/stored. This makes a curated banner
        // appear the moment its game card renders - no fetch, detection scan, or 30-min wait.
        if (NameKeyedBannerSource.TryGetEmbeddedBytesForSlug(canonicalService, slug, out var embeddedBytes, out var embeddedContentType))
        {
            return ImageResponse(embeddedBytes, embeddedContentType, $"{canonicalService}-{slug}");
        }

        return NotFound(new GameImageErrorResponse { Error = $"Game image not available for {canonicalService} '{slug}'" });
    }

    /// <summary>
    /// Returns the current cache generation so the frontend can build cache-busted image URLs on page load.
    /// </summary>
    [HttpGet("cache-version")]
    [AllowAnonymous]
    public IActionResult GetCacheVersion() => Ok(new { version = CacheGeneration });

    /// <summary>
    /// Returns the list of app IDs that have cached game images.
    /// The frontend uses this to skip rendering image components for apps without images.
    /// </summary>
    [HttpGet("available")]
    [AllowAnonymous]
    public async Task<IActionResult> GetAvailableImageIdsAsync(CancellationToken cancellationToken = default)
    {
        var ids = await _context.GameImages
            .AsNoTracking()
            .Select(gi => gi.AppId)
            .ToListAsync(cancellationToken);

        // Also advertise curated embedded name-keyed banners (Blizzard/Riot). Their JPEG bytes live
        // in the assembly and are served on-demand by GetNameKeyedHeaderImageAsync, so they are
        // "available" the instant a curated game's card renders - no fetched GameImage row required.
        var available = new HashSet<string>(ids, StringComparer.Ordinal);
        available.UnionWith(NameKeyedBannerSource.EmbeddedBannerSlugs());

        return Ok(available);
    }

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
                    epicUrlsRefreshed = await _epicMappingService.RefreshImagesAsync(cancellationToken);
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

        IncrementCacheGeneration();
        var cacheGeneration = CacheGeneration;

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
            cacheGeneration
        });
    }

    /// <summary>
    /// Returns an image response with proper cache headers and ETag-based conditional request support.
    /// Uses no-cache so the browser always revalidates, but gets efficient 304 responses when the image hasn't changed.
    /// </summary>
    private IActionResult ImageResponse(byte[] imageBytes, string contentType, string etagPrefix)
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
