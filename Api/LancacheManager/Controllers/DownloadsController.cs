using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using LancacheManager.Services;
using LancacheManager.Models;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DownloadsController : ControllerBase
{
    private readonly DatabaseService _dbService;

    public DownloadsController(DatabaseService dbService)
    {
        _dbService = dbService;
    }

    [HttpGet("latest")]
    public async Task<ActionResult<List<Download>>> GetLatestDownloads([FromQuery] int count = 20)
    {
        // Only get downloads from the last 24 hours
        var downloads = await _dbService.GetRecentDownloads(count);
        return Ok(downloads);
    }

    [HttpGet("active")]
    public async Task<ActionResult<List<Download>>> GetActiveDownloads()
    {
        var downloads = await _dbService.GetActiveDownloads();
        return Ok(downloads);
    }
}