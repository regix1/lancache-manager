using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LancacheManager.Models;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.EpicMapping;
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
    private readonly EpicMappingService? _epicMappingService;

    // Cache of failed image fetches to avoid repeated 404 warnings (AppId -> timestamp)
    private static readonly ConcurrentDictionary<uint, DateTime> _failedImageCache = new();
    private static readonly TimeSpan _failedCacheDuration = TimeSpan.FromHours(24);

    // Bumped on cache clear so the frontend can build cache-busted image URLs
    private static long _cacheGeneration = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public GameImagesController(
        ILogger<GameImagesController> logger,
        AppDbContext context,
        IImageCacheService imageCacheService,
        EpicMappingService? epicMappingService = null)
    {
        _logger = logger;
        _context = context;
        _imageCacheService = imageCacheService;
        _epicMappingService = epicMappingService;
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
    public async Task<IActionResult> GetGameHeaderImageAsync(
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
                    return ReturnImageWithCaching(imageBytes, contentType, $"{appId}{etagSuffix}");
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
    public async Task<IActionResult> GetEpicGameHeaderImageAsync(
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

            _logger.LogDebug("Epic image request for {EpicAppId}: mapping={Found}, imageUrl={Url}",
                epicAppId, mapping != null ? "found" : "NOT FOUND",
                mapping?.ImageUrl ?? "null");

            if (mapping == null || string.IsNullOrEmpty(mapping.ImageUrl))
            {
                return NotFound(new GameImageErrorResponse { Error = $"No image available for Epic app {epicAppId}" });
            }

            var imageUrl = EpicApiDirectClient.EnsureResizeParams(mapping.ImageUrl);

            _logger.LogDebug("Epic image URL for {EpicAppId} after EnsureResizeParams: {Url}", epicAppId, imageUrl);

            var result = await TryGetImageAsync(cacheKey, imageUrl, cancellationToken);
            if (result.HasValue)
            {
                var (imageBytes, contentType) = result.Value;
                return ReturnImageWithCaching(imageBytes, contentType, $"epic-{epicAppId}");
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
    /// Returns the current cache generation so the frontend can build cache-busted image URLs on page load.
    /// </summary>
    [HttpGet("cache-version")]
    public IActionResult GetCacheVersion() => Ok(new { version = _cacheGeneration });

    /// <summary>
    /// Clears the game image disk cache, failure markers, and in-memory failed-fetch cache,
    /// then immediately triggers an Epic image URL refresh so landscape URLs are repopulated.
    /// </summary>
    [HttpDelete("cache")]
    public async Task<IActionResult> ClearImageCacheAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("=== ClearImageCache START ===");

        // Step 1: Clear in-memory failed-fetch cache
        var failedCount = _failedImageCache.Count;
        _failedImageCache.Clear();
        _logger.LogInformation("Cleared {Count} in-memory failed-fetch entries", failedCount);

        // Step 2: Clear disk cache (images, metadata, failure markers)
        await _imageCacheService.ClearCacheAsync();
        _logger.LogInformation("Disk cache cleared (images, metadata, failure markers)");

        // Step 3: Trigger immediate Epic image URL refresh (instead of nulling URLs!)
        // This re-fetches correct landscape URLs from Epic's catalog API
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
        else
        {
            _logger.LogDebug("EpicMappingService not available - skipping Epic image URL refresh");
        }

        // Bump so frontend image URLs change on next load
        _cacheGeneration = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        _logger.LogInformation("=== ClearImageCache END === Failed entries cleared: {Failed}, Epic URLs refreshed: {Epic}",
            failedCount, epicUrlsRefreshed);

        return Ok(new
        {
            message = "Image cache cleared",
            failedCacheEntriesCleared = failedCount,
            epicImageUrlsRefreshed = epicUrlsRefreshed,
            cacheGeneration = _cacheGeneration
        });
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

    /// <summary>
    /// Returns an image response with proper cache headers and ETag-based conditional request support.
    /// Uses no-cache so the browser always revalidates, but gets efficient 304 responses when the image hasn't changed.
    /// </summary>
    private IActionResult ReturnImageWithCaching(byte[] imageBytes, string contentType, string etagPrefix)
    {
        var hash = Convert.ToHexString(SHA256.HashData(imageBytes)).ToLowerInvariant();
        var etag = $"\"{etagPrefix}-{hash}\"";

        Response.Headers["Cache-Control"] = "public, no-cache";
        Response.Headers["ETag"] = etag;

        var ifNoneMatch = Request.Headers["If-None-Match"].ToString();
        if (!string.IsNullOrEmpty(ifNoneMatch) && (ifNoneMatch.Contains(etag) || ifNoneMatch.Trim() == "*"))
        {
            return StatusCode(304);
        }

        return File(imageBytes, contentType);
    }

}
