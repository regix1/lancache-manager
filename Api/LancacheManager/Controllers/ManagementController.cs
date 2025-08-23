using Microsoft.AspNetCore.Mvc;
using LancacheManager.Services;
using LancacheManager.Models;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ManagementController : ControllerBase
{
    private readonly CacheManagementService _cacheService;
    private readonly DatabaseService _dbService;

    public ManagementController(CacheManagementService cacheService, DatabaseService dbService)
    {
        _cacheService = cacheService;
        _dbService = dbService;
    }

    [HttpGet("cache-info")]
    public ActionResult<CacheInfo> GetCacheInfo()
    {
        var info = _cacheService.GetCacheInfo();
        return Ok(info);
    }

    [HttpPost("clear-cache")]
    public async Task<ActionResult> ClearCache([FromBody] ManagementAction action)
    {
        var success = await _cacheService.ClearCache(action.Service);
        if (success)
        {
            return Ok(new { message = "Cache cleared successfully" });
        }
        return BadRequest(new { error = "Failed to clear cache" });
    }

    [HttpPost("reset-database")]
    public async Task<ActionResult> ResetDatabase()
    {
        await _dbService.ResetDatabase();
        return Ok(new { message = "Database reset successfully" });
    }
}