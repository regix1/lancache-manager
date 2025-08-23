using Microsoft.AspNetCore.Mvc;
using LancacheManager.Services;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StatsController : ControllerBase
{
    private readonly DatabaseService _dbService;

    public StatsController(DatabaseService dbService)
    {
        _dbService = dbService;
    }

    [HttpGet("clients")]
    public async Task<IActionResult> GetClients()
    {
        var stats = await _dbService.GetClientStats();
        return Ok(stats);
    }

    [HttpGet("services")]
    public async Task<IActionResult> GetServices()
    {
        var stats = await _dbService.GetServiceStats();
        return Ok(stats);
    }
}