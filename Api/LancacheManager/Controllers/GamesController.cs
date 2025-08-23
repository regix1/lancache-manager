using Microsoft.AspNetCore.Mvc;
using LancacheManager.Services;
using LancacheManager.Models;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class GamesController : ControllerBase
{
    private readonly DatabaseService _dbService;

    public GamesController(DatabaseService dbService)
    {
        _dbService = dbService;
    }

    [HttpGet("stats")]
    public async Task<ActionResult<List<GameStats>>> GetGameStats([FromQuery] string? service = null)
    {
        var stats = await _dbService.GetGameStats(service);
        return Ok(stats);
    }

    [HttpGet("steam")]
    public async Task<ActionResult<List<GameStats>>> GetSteamGameStats()
    {
        var stats = await _dbService.GetGameStats("steam");
        return Ok(stats);
    }

    [HttpGet("top")]
    public async Task<ActionResult<List<GameStats>>> GetTopGames([FromQuery] int count = 10)
    {
        var stats = await _dbService.GetGameStats();
        return Ok(stats.Take(count));
    }
}