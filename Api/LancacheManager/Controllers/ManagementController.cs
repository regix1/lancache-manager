using Microsoft.AspNetCore.Mvc;
using LancacheManager.Services;

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
    public IActionResult GetCacheInfo()
    {
        var info = _cacheService.GetCacheInfo();
        return Ok(info);
    }

    [HttpPost("clear-cache")]
    public async Task<IActionResult> ClearCache([FromBody] ClearCacheRequest request)
    {
        await _cacheService.ClearCache(request?.Service);
        return Ok(new { message = "Cache cleared" });
    }

    [HttpPost("reset-database")]
    public async Task<IActionResult> ResetDatabase()
    {
        await _dbService.ResetDatabase();
        return Ok(new { message = "Database reset" });
    }
}

public class ClearCacheRequest
{
    public string? Service { get; set; }
}