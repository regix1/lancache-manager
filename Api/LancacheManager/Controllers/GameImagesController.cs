using Microsoft.AspNetCore.Mvc;
using LancacheManager.Data;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class GameImagesController : ControllerBase
{
    private readonly ILogger<GameImagesController> _logger;
    private readonly AppDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;

    // Cache of failed image fetches to avoid repeated 404 warnings (AppId -> timestamp)
    private static readonly ConcurrentDictionary<uint, DateTime> _failedImageCache = new();
    private static readonly TimeSpan _failedCacheDuration = TimeSpan.FromHours(24);

    public GameImagesController(ILogger<GameImagesController> logger, AppDbContext context, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _context = context;
        _httpClientFactory = httpClientFactory;
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

            // Look up the game in the database to get the actual image URL
            var download = await _context.Downloads
                .Where(d => d.GameAppId == appId && !string.IsNullOrEmpty(d.GameImageUrl))
                .OrderByDescending(d => d.StartTimeUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (download?.GameImageUrl == null)
            {
                _logger.LogTrace($"No game image URL found for app {appId} in database");
                return NotFound(new { error = $"Game image URL not found for app {appId}" });
            }

            var httpClient = _httpClientFactory.CreateClient("SteamImages");

            var response = await httpClient.GetAsync(download.GameImageUrl, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var imageBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";

                // Add cache headers
                Response.Headers["Cache-Control"] = "public, max-age=3600"; // Cache for 1 hour

                return File(imageBytes, contentType);
            }

            // Image fetch failed - cache this failure
            _failedImageCache.TryAdd(appId, DateTime.UtcNow);

            // Use Debug level for 404s (expected for non-game apps like tools/redistributables)
            // Use Warning level for other errors
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogDebug($"Steam header image not found for app {appId} ({download.GameName ?? "Unknown"}) - likely a tool/redistributable");
            }
            else
            {
                _logger.LogWarning($"Failed to fetch Steam header image for app {appId} from {download.GameImageUrl}, status: {response.StatusCode}");
            }

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
            _logger.LogDebug($"Request cancelled for Steam header image app {appId}");
            return StatusCode(499, new { error = "Request cancelled" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error proxying Steam header image for app {appId}");
            return StatusCode(500, new { error = "Failed to fetch game header image" });
        }
    }

}