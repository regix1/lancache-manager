using Microsoft.AspNetCore.Mvc;
using LancacheManager.Services;

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
    public async Task<IActionResult> GetLatest([FromQuery] int count = 50)
    {
        var downloads = await _dbService.GetLatestDownloads(count);
        return Ok(downloads);
    }

    [HttpGet("active")]
    public async Task<IActionResult> GetActive()
    {
        var downloads = await _dbService.GetActiveDownloads();
        return Ok(downloads);
    }
}