using Microsoft.AspNetCore.Mvc;
using LancacheManager.Data;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class GameImagesController : ControllerBase
{
    private readonly ILogger<GameImagesController> _logger;
    private readonly AppDbContext _context;

    public GameImagesController(ILogger<GameImagesController> logger, AppDbContext context)
    {
        _logger = logger;
        _context = context;
    }

    /// <summary>
    /// Proxy Steam game header images to avoid CORS issues
    /// </summary>
    [HttpGet("{appId}/header")]
    public async Task<IActionResult> GetGameHeaderImage(uint appId)
    {
        try
        {
            // Look up the game in the database to get the actual image URL
            var download = await _context.Downloads
                .Where(d => d.GameAppId == appId && !string.IsNullOrEmpty(d.GameImageUrl))
                .OrderByDescending(d => d.StartTime)
                .FirstOrDefaultAsync();

            if (download?.GameImageUrl == null)
            {
                _logger.LogWarning($"No game image URL found for app {appId} in database");
                return NotFound(new { error = $"Game image URL not found for app {appId}" });
            }

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "LancacheManager/1.0");

            var response = await httpClient.GetAsync(download.GameImageUrl);

            if (response.IsSuccessStatusCode)
            {
                var imageBytes = await response.Content.ReadAsByteArrayAsync();
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";

                // Add cache headers
                Response.Headers["Cache-Control"] = "public, max-age=3600"; // Cache for 1 hour

                return File(imageBytes, contentType);
            }
            else
            {
                _logger.LogWarning($"Failed to fetch Steam header image for app {appId} from {download.GameImageUrl}, status: {response.StatusCode}");
                return NotFound(new { error = $"Steam header image not found for app {appId}" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error proxying Steam header image for app {appId}");
            return StatusCode(500, new { error = "Failed to fetch game header image" });
        }
    }

}