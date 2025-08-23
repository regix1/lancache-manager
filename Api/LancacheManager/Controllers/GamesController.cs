using Microsoft.AspNetCore.Mvc;
using LancacheManager.Services;
using LancacheManager.Models;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class GamesController : ControllerBase
{
    private readonly DatabaseService _dbService;
    private readonly SteamService _steamService;
    private readonly ILogger<GamesController> _logger;

    public GamesController(
        DatabaseService dbService,
        SteamService steamService,
        ILogger<GamesController> logger)
    {
        _dbService = dbService;
        _steamService = steamService;
        _logger = logger;
    }

    /// <summary>
    /// Get game statistics with optional filtering
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<GameStats>>> GetGames(
        [FromQuery] string? service = null,
        [FromQuery] int? top = null,
        [FromQuery] string? sortBy = "totalBytes")
    {
        try
        {
            var stats = await _dbService.GetGameStats(service, sortBy);
            
            if (top.HasValue)
            {
                stats = stats.Take(top.Value).ToList();
            }
            
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting game stats");
            return StatusCode(500, new { error = "Failed to retrieve game statistics" });
        }
    }

    /// <summary>
    /// Get statistics for a specific game
    /// </summary>
    [HttpGet("{gameId}")]
    public async Task<ActionResult<GameStats>> GetGame(string gameId)
    {
        try
        {
            var game = await _dbService.GetGameById(gameId);
            if (game == null)
            {
                return NotFound(new { error = "Game not found" });
            }
            return Ok(game);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting game {GameId}", gameId);
            return StatusCode(500, new { error = "Failed to retrieve game" });
        }
    }

    /// <summary>
    /// Get top downloaded games
    /// </summary>
    [HttpGet("top")]
    public async Task<ActionResult<List<GameStats>>> GetTopGames(
        [FromQuery] int count = 10,
        [FromQuery] string? service = null,
        [FromQuery] int days = 30)
    {
        try
        {
            var stats = await _dbService.GetTopGames(count, service, days);
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting top games");
            return StatusCode(500, new { error = "Failed to retrieve top games" });
        }
    }

    /// <summary>
    /// Get trending games (most downloaded recently)
    /// </summary>
    [HttpGet("trending")]
    public async Task<ActionResult<List<GameStats>>> GetTrendingGames(
        [FromQuery] int count = 10,
        [FromQuery] int hours = 24)
    {
        try
        {
            var stats = await _dbService.GetTrendingGames(count, hours);
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting trending games");
            return StatusCode(500, new { error = "Failed to retrieve trending games" });
        }
    }

    /// <summary>
    /// Get Steam-specific game statistics
    /// </summary>
    [HttpGet("steam")]
    public async Task<ActionResult<List<GameStats>>> GetSteamGames([FromQuery] int? top = null)
    {
        try
        {
            var stats = await _dbService.GetGameStats("steam", "totalBytes");
            
            if (top.HasValue)
            {
                stats = stats.Take(top.Value).ToList();
            }
            
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Steam games");
            return StatusCode(500, new { error = "Failed to retrieve Steam games" });
        }
    }

    /// <summary>
    /// Resolve multiple app names (primarily for Steam)
    /// </summary>
    [HttpPost("resolve-names")]
    public async Task<ActionResult<Dictionary<string, string>>> ResolveAppNames([FromBody] List<string> appIds)
    {
        try
        {
            if (appIds == null || !appIds.Any())
            {
                return BadRequest(new { error = "No app IDs provided" });
            }

            var results = await _steamService.GetMultipleAppNamesAsync(appIds);
            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resolving app names");
            return StatusCode(500, new { error = "Failed to resolve app names" });
        }
    }

    /// <summary>
    /// Get game download history
    /// </summary>
    [HttpGet("{gameId}/history")]
    public async Task<ActionResult<List<Download>>> GetGameHistory(
        string gameId,
        [FromQuery] int days = 30,
        [FromQuery] int count = 100)
    {
        try
        {
            var history = await _dbService.GetGameDownloadHistory(gameId, days, count);
            return Ok(history);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting game history for {GameId}", gameId);
            return StatusCode(500, new { error = "Failed to retrieve game history" });
        }
    }

    /// <summary>
    /// Get clients who downloaded a specific game
    /// </summary>
    [HttpGet("{gameId}/clients")]
    public async Task<ActionResult<List<string>>> GetGameClients(string gameId)
    {
        try
        {
            var clients = await _dbService.GetClientsForGame(gameId);
            return Ok(clients);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting clients for game {GameId}", gameId);
            return StatusCode(500, new { error = "Failed to retrieve game clients" });
        }
    }

    /// <summary>
    /// Search games by name
    /// </summary>
    [HttpGet("search")]
    public async Task<ActionResult<List<GameStats>>> SearchGames(
        [FromQuery] string query,
        [FromQuery] int limit = 20)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return BadRequest(new { error = "Search query is required" });
            }

            var results = await _dbService.SearchGames(query, limit);
            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching games with query {Query}", query);
            return StatusCode(500, new { error = "Failed to search games" });
        }
    }
}