using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LancacheManager.Services;
using LancacheManager.Data;
using LancacheManager.Models;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DownloadsController : ControllerBase
{
    private readonly DatabaseService _dbService;
    private readonly AppDbContext _context;
    private readonly StatsService _statsService;
    private readonly ILogger<DownloadsController> _logger;

    public DownloadsController(DatabaseService dbService, AppDbContext context, StatsService statsService, ILogger<DownloadsController> logger)
    {
        _dbService = dbService;
        _context = context;
        _statsService = statsService;
        _logger = logger;
    }

    [HttpGet("latest")]
    [ResponseCache(Duration = 5)] // Cache for 5 seconds
    public async Task<IActionResult> GetLatest([FromQuery] int count = 9999, [FromQuery] long? startTime = null, [FromQuery] long? endTime = null)
    {
        const int maxRetries = 3;
        for (int retry = 0; retry < maxRetries; retry++)
        {
            try
            {
                List<Download> downloads;

                // If no time filtering, use cached service method
                if (!startTime.HasValue && !endTime.HasValue)
                {
                    downloads = await _statsService.GetLatestDownloadsAsync(count);
                }
                else
                {
                    // With filtering, query database directly
                    // Database stores dates in UTC, so filter with UTC
                    var startDate = startTime.HasValue
                        ? DateTimeOffset.FromUnixTimeSeconds(startTime.Value).UtcDateTime
                        : DateTime.MinValue;
                    var endDate = endTime.HasValue
                        ? DateTimeOffset.FromUnixTimeSeconds(endTime.Value).UtcDateTime
                        : DateTime.UtcNow;

                    downloads = await _context.Downloads
                        .AsNoTracking()
                        .Where(d => d.StartTimeUtc >= startDate && d.StartTimeUtc <= endDate)
                        .OrderByDescending(d => d.StartTimeUtc)
                        .Take(count)
                        .ToListAsync();
                }

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
            // Use cached service method (2-second cache)
            var downloads = await _statsService.GetActiveDownloadsAsync();
            return Ok(downloads);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting active downloads");
            return Ok(new List<object>());
        }
    }
}