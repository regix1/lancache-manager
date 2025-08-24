using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LancacheManager.Services;
using LancacheManager.Data;
using LancacheManager.Models;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class GameInfoController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly SteamService _steamService;
    private readonly ILogger<GameInfoController> _logger;

    public GameInfoController(
        AppDbContext context,
        SteamService steamService,
        ILogger<GameInfoController> logger)
    {
        _context = context;
        _steamService = steamService;
        _logger = logger;
    }

    /// <summary>
    /// Get detailed game information for a specific download
    /// </summary>
    [HttpGet("download/{downloadId}")]
    public async Task<IActionResult> GetDownloadGameInfo(int downloadId)
    {
        try
        {
            var download = await _context.Downloads
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == downloadId);

            if (download == null)
            {
                return NotFound(new { error = "Download not found" });
            }

            // For non-Steam services, return basic info
            if (download.Service.ToLower() != "steam")
            {
                return Ok(new GameDownloadDetails
                {
                    DownloadId = download.Id,
                    Service = download.Service,
                    GameName = $"{download.Service} Content",
                    TotalBytes = download.TotalBytes,
                    CacheHitBytes = download.CacheHitBytes,
                    CacheMissBytes = download.CacheMissBytes,
                    CacheHitPercent = download.CacheHitPercent,
                    StartTime = download.StartTime,
                    EndTime = download.EndTime,
                    ClientIp = download.ClientIp,
                    IsActive = download.IsActive
                });
            }

            // Try to get game info from download record first
            if (download.GameAppId.HasValue && !string.IsNullOrEmpty(download.GameName))
            {
                var cachedInfo = await _steamService.GetGameInfoAsync(download.GameAppId.Value);
                
                return Ok(new GameDownloadDetails
                {
                    DownloadId = download.Id,
                    Service = download.Service,
                    AppId = download.GameAppId,
                    GameName = cachedInfo?.Name ?? download.GameName,
                    GameType = cachedInfo?.Type,
                    HeaderImage = cachedInfo?.HeaderImage ?? download.GameImageUrl,
                    Description = cachedInfo?.Description,
                    TotalBytes = download.TotalBytes,
                    CacheHitBytes = download.CacheHitBytes,
                    CacheMissBytes = download.CacheMissBytes,
                    CacheHitPercent = download.CacheHitPercent,
                    StartTime = download.StartTime,
                    EndTime = download.EndTime,
                    ClientIp = download.ClientIp,
                    IsActive = download.IsActive
                });
            }

            // Try to extract app ID from URL if we have one
            if (!string.IsNullOrEmpty(download.LastUrl))
            {
                var appId = _steamService.ExtractAppIdFromUrl(download.LastUrl);
                if (appId.HasValue)
                {
                    var gameInfo = await _steamService.GetGameInfoAsync(appId.Value);
                    
                    // Update the download record with game info
                    download.GameAppId = appId;
                    download.GameName = gameInfo?.Name;
                    download.GameImageUrl = gameInfo?.HeaderImage;
                    _context.Downloads.Update(download);
                    await _context.SaveChangesAsync();

                    return Ok(new GameDownloadDetails
                    {
                        DownloadId = download.Id,
                        Service = download.Service,
                        AppId = appId,
                        GameName = gameInfo?.Name ?? "Unknown Steam Game",
                        GameType = gameInfo?.Type,
                        HeaderImage = gameInfo?.HeaderImage,
                        Description = gameInfo?.Description,
                        TotalBytes = download.TotalBytes,
                        CacheHitBytes = download.CacheHitBytes,
                        CacheMissBytes = download.CacheMissBytes,
                        CacheHitPercent = download.CacheHitPercent,
                        StartTime = download.StartTime,
                        EndTime = download.EndTime,
                        ClientIp = download.ClientIp,
                        IsActive = download.IsActive
                    });
                }
            }

            // Return basic info if we can't determine the game
            return Ok(new GameDownloadDetails
            {
                DownloadId = download.Id,
                Service = download.Service,
                GameName = "Unknown Steam Game",
                TotalBytes = download.TotalBytes,
                CacheHitBytes = download.CacheHitBytes,
                CacheMissBytes = download.CacheMissBytes,
                CacheHitPercent = download.CacheHitPercent,
                StartTime = download.StartTime,
                EndTime = download.EndTime,
                ClientIp = download.ClientIp,
                IsActive = download.IsActive
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error getting game info for download {downloadId}");
            return StatusCode(500, new { error = "Failed to get game information" });
        }
    }
}