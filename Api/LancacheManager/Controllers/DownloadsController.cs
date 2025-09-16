using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LancacheManager.Services;
using LancacheManager.Data;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DownloadsController : ControllerBase
{
    private readonly DatabaseService _dbService;
    private readonly AppDbContext _context;
    private readonly ILogger<DownloadsController> _logger;

    public DownloadsController(DatabaseService dbService, AppDbContext context, ILogger<DownloadsController> logger)
    {
        _dbService = dbService;
        _context = context;
        _logger = logger;
    }

    [HttpGet("latest")]
    [ResponseCache(Duration = 5)] // Cache for 5 seconds
    public async Task<IActionResult> GetLatest([FromQuery] int count = 50)
    {
        const int maxRetries = 3;
        for (int retry = 0; retry < maxRetries; retry++)
        {
            try
            {
                // Use AsNoTracking for read-only query
                var downloads = await _context.Downloads
                    .AsNoTracking()
                    .OrderByDescending(d => d.StartTime)
                    .Take(count)
                    .ToListAsync();

                return Ok(downloads);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 5) // SQLITE_BUSY
            {
                if (retry < maxRetries - 1)
                {
                    _logger.LogWarning($"Database busy, retrying... (attempt {retry + 1}/{maxRetries})");
                    await Task.Delay(100 * (retry + 1)); // Exponential backoff
                    continue;
                }
                _logger.LogError(ex, "Database locked after retries");
                return Ok(new List<object>());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting latest downloads");
                return Ok(new List<object>());
            }
        }
        return Ok(new List<object>());
    }

    [HttpGet("active")]
    [ResponseCache(Duration = 2)] // Cache for 2 seconds
    public async Task<IActionResult> GetActive()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-5);
            
            // Use AsNoTracking and optimize query
            var downloads = await _context.Downloads
                .AsNoTracking()
                .Where(d => d.IsActive && d.EndTime > cutoff)
                .OrderByDescending(d => d.StartTime)
                .Take(100) // Limit results
                .ToListAsync();
                
            return Ok(downloads);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting active downloads");
            return Ok(new List<object>());
        }
    }
}