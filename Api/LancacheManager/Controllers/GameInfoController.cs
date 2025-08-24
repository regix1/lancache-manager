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
                    Genres = cachedInfo?.Genres ?? new(),
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
                        Genres = gameInfo?.Genres ?? new(),
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

    /// <summary>
    /// Get downloads grouped by game
    /// </summary>
    [HttpGet("downloads-by-game")]
    public async Task<IActionResult> GetDownloadsByGame([FromQuery] string service = "steam")
    {
        try
        {
            var downloads = await _context.Downloads
                .AsNoTracking()
                .Where(d => d.Service.ToLower() == service.ToLower())
                .Where(d => d.GameAppId.HasValue)
                .GroupBy(d => new { d.GameAppId, d.GameName })
                .Select(g => new
                {
                    AppId = g.Key.GameAppId,
                    GameName = g.Key.GameName ?? "Unknown",
                    TotalDownloads = g.Count(),
                    TotalBytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                    CacheHitBytes = g.Sum(d => d.CacheHitBytes),
                    CacheMissBytes = g.Sum(d => d.CacheMissBytes),
                    LastDownload = g.Max(d => d.EndTime),
                    UniqueClients = g.Select(d => d.ClientIp).Distinct().Count()
                })
                .OrderByDescending(g => g.TotalBytes)
                .Take(50)
                .ToListAsync();

            // Enrich with Steam data
            var appIds = downloads
                .Where(d => d.AppId.HasValue)
                .Select(d => d.AppId!.Value)
                .Distinct();

            var gameInfos = await _steamService.GetMultipleGameInfoAsync(appIds);

            var enrichedData = downloads.Select(d => new
            {
                d.AppId,
                GameName = d.AppId.HasValue && gameInfos.TryGetValue(d.AppId.Value, out var info) 
                    ? info.Name 
                    : d.GameName,
                HeaderImage = d.AppId.HasValue && gameInfos.TryGetValue(d.AppId.Value, out var info2)
                    ? info2.HeaderImage
                    : null,
                d.TotalDownloads,
                d.TotalBytes,
                d.CacheHitBytes,
                d.CacheMissBytes,
                CacheHitPercent = d.TotalBytes > 0 ? (d.CacheHitBytes * 100.0) / d.TotalBytes : 0,
                d.LastDownload,
                d.UniqueClients
            });

            return Ok(enrichedData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting downloads by game");
            return StatusCode(500, new { error = "Failed to get downloads by game" });
        }
    }

    /// <summary>
    /// Identify and update game info for recent downloads
    /// </summary>
    [HttpPost("identify-games")]
    public async Task<IActionResult> IdentifyGames([FromQuery] int hours = 24)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-hours);
            
            var unidentifiedDownloads = await _context.Downloads
                .Where(d => d.Service.ToLower() == "steam")
                .Where(d => d.GameAppId == null)
                .Where(d => d.StartTime > cutoff)
                .Where(d => !string.IsNullOrEmpty(d.LastUrl))
                .Take(100)
                .ToListAsync();

            int identified = 0;
            int failed = 0;

            foreach (var download in unidentifiedDownloads)
            {
                if (string.IsNullOrEmpty(download.LastUrl))
                    continue;

                var appId = _steamService.ExtractAppIdFromUrl(download.LastUrl);
                if (appId.HasValue)
                {
                    var gameInfo = await _steamService.GetGameInfoAsync(appId.Value);
                    if (gameInfo != null)
                    {
                        download.GameAppId = appId;
                        download.GameName = gameInfo.Name;
                        download.GameImageUrl = gameInfo.HeaderImage;
                        identified++;
                    }
                    else
                    {
                        failed++;
                    }
                }
            }

            if (identified > 0)
            {
                await _context.SaveChangesAsync();
            }

            return Ok(new
            {
                processed = unidentifiedDownloads.Count,
                identified,
                failed,
                message = $"Identified {identified} games from {unidentifiedDownloads.Count} downloads"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error identifying games");
            return StatusCode(500, new { error = "Failed to identify games" });
        }
    }
}