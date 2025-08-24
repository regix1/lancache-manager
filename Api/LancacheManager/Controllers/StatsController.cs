using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LancacheManager.Services;
using LancacheManager.Data;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StatsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ILogger<StatsController> _logger;

    public StatsController(AppDbContext context, ILogger<StatsController> logger)
    {
        _context = context;
        _logger = logger;
    }

    [HttpGet("clients")]
    [ResponseCache(Duration = 10)] // Cache for 10 seconds
    public async Task<IActionResult> GetClients()
    {
        try
        {
            var stats = await _context.ClientStats
                .AsNoTracking()
                .OrderByDescending(c => c.TotalCacheHitBytes + c.TotalCacheMissBytes)
                .Take(100) // Limit results
                .ToListAsync();
                
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting client stats");
            return Ok(new List<object>());
        }
    }

    [HttpGet("services")]
    [ResponseCache(Duration = 10)] // Cache for 10 seconds
    public async Task<IActionResult> GetServices()
    {
        try
        {
            var stats = await _context.ServiceStats
                .AsNoTracking()
                .OrderByDescending(s => s.TotalCacheHitBytes + s.TotalCacheMissBytes)
                .ToListAsync();
                
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting service stats");
            return Ok(new List<object>());
        }
    }
}