using Microsoft.AspNetCore.Mvc;
using LancacheManager.Services;
using LancacheManager.Models;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DownloadsController : ControllerBase
{
    private readonly DatabaseService _dbService;
    private readonly ILogger<DownloadsController> _logger;

    public DownloadsController(
        DatabaseService dbService,
        ILogger<DownloadsController> logger)
    {
        _dbService = dbService;
        _logger = logger;
    }

    /// <summary>
    /// Get latest downloads
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<Download>>> GetDownloads([FromQuery] int count = 20)
    {
        try
        {
            var downloads = await _dbService.GetLatestDownloads(count);
            return Ok(downloads);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting downloads");
            return StatusCode(500, new { error = "Failed to retrieve downloads" });
        }
    }

    /// <summary>
    /// Get recent downloads (last 24 hours)
    /// </summary>
    [HttpGet("recent")]
    public async Task<ActionResult<List<Download>>> GetRecentDownloads([FromQuery] int count = 20)
    {
        try
        {
            var downloads = await _dbService.GetRecentDownloads(count);
            return Ok(downloads);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting recent downloads");
            return StatusCode(500, new { error = "Failed to retrieve recent downloads" });
        }
    }

    /// <summary>
    /// Get active downloads
    /// </summary>
    [HttpGet("active")]
    public async Task<ActionResult<List<Download>>> GetActiveDownloads()
    {
        try
        {
            var downloads = await _dbService.GetActiveDownloads();
            return Ok(downloads);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting active downloads");
            return StatusCode(500, new { error = "Failed to retrieve active downloads" });
        }
    }

    /// <summary>
    /// Get downloads by client
    /// </summary>
    [HttpGet("client/{clientIp}")]
    public async Task<ActionResult<List<Download>>> GetDownloadsByClient(
        string clientIp,
        [FromQuery] int count = 20)
    {
        try
        {
            var downloads = await _dbService.GetDownloadsByClient(clientIp, count);
            return Ok(downloads);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting downloads for client {ClientIp}", clientIp);
            return StatusCode(500, new { error = "Failed to retrieve client downloads" });
        }
    }

    /// <summary>
    /// Get downloads by service
    /// </summary>
    [HttpGet("service/{service}")]
    public async Task<ActionResult<List<Download>>> GetDownloadsByService(
        string service,
        [FromQuery] int count = 20)
    {
        try
        {
            var downloads = await _dbService.GetDownloadsByService(service, count);
            return Ok(downloads);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting downloads for service {Service}", service);
            return StatusCode(500, new { error = "Failed to retrieve service downloads" });
        }
    }
}