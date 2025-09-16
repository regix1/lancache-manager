using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LancacheManager.Data;
using LancacheManager.Services;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class GameImagesController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly GameImageCacheService _imageCacheService;
    private readonly ILogger<GameImagesController> _logger;

    public GameImagesController(
        AppDbContext context,
        GameImageCacheService imageCacheService,
        ILogger<GameImagesController> logger)
    {
        _context = context;
        _imageCacheService = imageCacheService;
        _logger = logger;
    }

    [HttpGet("{appId}/{imageType?}")]
    [ResponseCache(Duration = 86400)] // Cache for 24 hours
    public async Task<IActionResult> GetImage(uint appId, string imageType = "header")
    {
        try
        {
            _logger.LogInformation($"Getting image for app {appId}, type {imageType}");

            // First check if we have it in cache
            var cachedImage = await _context.GameImages
                .AsNoTracking()
                .FirstOrDefaultAsync(g => g.AppId == appId && g.ImageType == imageType);

            _logger.LogInformation($"Cache check result: {(cachedImage != null ? "found" : "not found")}");

            if (cachedImage != null && cachedImage.ImageData.Length > 0)
            {
                // Update access stats asynchronously (fire and forget)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var image = await _context.GameImages
                            .FirstOrDefaultAsync(g => g.Id == cachedImage.Id);
                        if (image != null)
                        {
                            image.LastAccessed = DateTime.UtcNow;
                            image.AccessCount++;
                            await _context.SaveChangesAsync();
                        }
                    }
                    catch { }
                });

                return File(cachedImage.ImageData, cachedImage.ContentType);
            }

            // Not in cache or placeholder, try to download
            _logger.LogInformation($"Attempting to download image for app {appId}");
            var gameImage = await _imageCacheService.GetImageAsync(appId, imageType);

            _logger.LogInformation($"Download result: {(gameImage != null ? $"success, {gameImage.ImageData.Length} bytes" : "failed")}");

            if (gameImage != null && gameImage.ImageData.Length > 0)
            {
                return File(gameImage.ImageData, gameImage.ContentType);
            }

            // Return 404 if we couldn't get the image
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error getting image for app {appId}: {ex.Message}");
            return StatusCode(500, new {
                error = "Failed to get image",
                message = ex.Message,
                type = ex.GetType().Name,
                stackTrace = ex.StackTrace
            });
        }
    }

    [HttpPost("{appId}/cache")]
    public async Task<IActionResult> CacheImage(uint appId, [FromQuery] string gameName = "", [FromQuery] string imageType = "header")
    {
        try
        {
            var gameImage = await _imageCacheService.GetOrCreateImageAsync(appId, gameName, imageType);

            if (gameImage != null)
            {
                return Ok(new { success = true, cached = true });
            }

            return Ok(new { success = false, cached = false });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error caching image for app {appId}");
            return StatusCode(500, new { success = false, error = ex.Message });
        }
    }

    [HttpGet("test")]
    public IActionResult Test()
    {
        return Ok(new { status = "GameImagesController is working", timestamp = DateTime.UtcNow });
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetCacheStats()
    {
        try
        {
            var stats = await _context.GameImages
                .GroupBy(g => g.ImageType)
                .Select(g => new
                {
                    ImageType = g.Key,
                    Count = g.Count(),
                    TotalSize = g.Sum(x => x.ImageData.Length),
                    AverageAccessCount = g.Average(x => x.AccessCount)
                })
                .ToListAsync();

            var totalImages = await _context.GameImages.CountAsync();
            var totalSize = await _context.GameImages.SumAsync(g => (long)g.ImageData.Length);

            return Ok(new
            {
                TotalImages = totalImages,
                TotalSizeBytes = totalSize,
                TotalSizeMB = totalSize / (1024.0 * 1024.0),
                ByType = stats
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache stats");
            return StatusCode(500);
        }
    }

    [HttpDelete("cleanup")]
    public async Task<IActionResult> CleanupOldImages([FromQuery] int daysOld = 30)
    {
        try
        {
            await _imageCacheService.CleanupOldImagesAsync(daysOld);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up old images");
            return StatusCode(500, new { success = false, error = ex.Message });
        }
    }
}