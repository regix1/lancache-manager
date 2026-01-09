using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SpeedsController : ControllerBase
{
    private readonly RustSpeedTrackerService _speedTrackerService;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<SpeedsController> _logger;

    public SpeedsController(
        RustSpeedTrackerService speedTrackerService,
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<SpeedsController> logger)
    {
        _speedTrackerService = speedTrackerService;
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Get current download speeds for all active games and clients
    /// </summary>
    [HttpGet("current")]
    [ProducesResponseType(typeof(DownloadSpeedSnapshot), StatusCodes.Status200OK)]
    public ActionResult<DownloadSpeedSnapshot> GetCurrentSpeeds()
    {
        var snapshot = _speedTrackerService.GetCurrentSnapshot();
        return Ok(snapshot);
    }

    /// <summary>
    /// Get historical download speeds for a time period
    /// </summary>
    /// <param name="minutes">Number of minutes to look back (default: 60)</param>
    [HttpGet("history")]
    [ProducesResponseType(typeof(SpeedHistorySnapshot), StatusCodes.Status200OK)]
    public async Task<ActionResult<SpeedHistorySnapshot>> GetSpeedHistory([FromQuery] int minutes = 60)
    {
        // Clamp to reasonable values
        minutes = Math.Clamp(minutes, 5, 1440); // 5 minutes to 24 hours

        var periodEnd = DateTime.UtcNow;
        var periodStart = periodEnd.AddMinutes(-minutes);

        await using var context = await _contextFactory.CreateDbContextAsync();

        // Query downloads within the time period
        var downloads = await context.Downloads
            .Where(d => d.EndTimeUtc >= periodStart && d.StartTimeUtc <= periodEnd)
            .ToListAsync();

        if (downloads.Count == 0)
        {
            return Ok(new SpeedHistorySnapshot
            {
                PeriodStartUtc = periodStart,
                PeriodEndUtc = periodEnd,
                PeriodMinutes = minutes
            });
        }

        // Calculate total bytes for the period (filter out 0-byte entries)
        var downloadsWithData = downloads.Where(d => d.TotalBytes > 0).ToList();
        var totalBytes = downloadsWithData.Sum(d => d.TotalBytes);
        var totalDuration = (periodEnd - periodStart).TotalSeconds;

        return Ok(new SpeedHistorySnapshot
        {
            PeriodStartUtc = periodStart,
            PeriodEndUtc = periodEnd,
            PeriodMinutes = minutes,
            TotalBytes = totalBytes,
            AverageBytesPerSecond = totalDuration > 0 ? totalBytes / totalDuration : 0,
            TotalSessions = downloadsWithData.Count
        });
    }
}
