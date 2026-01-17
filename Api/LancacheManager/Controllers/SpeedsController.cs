using LancacheManager.Models;
using LancacheManager.Core.Interfaces.Repositories;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
[RequireGuestSession]
public class SpeedsController : ControllerBase
{
    private readonly RustSpeedTrackerService _speedTrackerService;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IStateRepository _stateRepository;
    private readonly ILogger<SpeedsController> _logger;

    public SpeedsController(
        RustSpeedTrackerService speedTrackerService,
        IDbContextFactory<AppDbContext> contextFactory,
        IStateRepository stateRepository,
        ILogger<SpeedsController> logger)
    {
        _speedTrackerService = speedTrackerService;
        _contextFactory = contextFactory;
        _stateRepository = stateRepository;
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

        var excludedClientIps = _stateRepository.GetExcludedClientIps();
        if (excludedClientIps.Count == 0)
        {
            return Ok(snapshot);
        }

        var filteredClients = snapshot.ClientSpeeds
            .Where(c => !excludedClientIps.Contains(c.ClientIp))
            .ToList();

        var filteredGames = snapshot.GameSpeeds
            .Where(g => string.IsNullOrWhiteSpace(g.ClientIp) || !excludedClientIps.Contains(g.ClientIp))
            .ToList();

        var totalBytesPerSecond = filteredClients.Sum(c => c.BytesPerSecond);
        var entriesInWindow = filteredGames.Sum(g => g.RequestCount);

        var filteredSnapshot = new DownloadSpeedSnapshot
        {
            TimestampUtc = snapshot.TimestampUtc,
            WindowSeconds = snapshot.WindowSeconds,
            TotalBytesPerSecond = totalBytesPerSecond,
            EntriesInWindow = entriesInWindow,
            GameSpeeds = filteredGames,
            ClientSpeeds = filteredClients
        };

        return Ok(filteredSnapshot);
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
        var excludedClientIps = _stateRepository.GetExcludedClientIps();

        // Query downloads within the time period
        var downloads = await context.Downloads
            .Where(d => d.EndTimeUtc >= periodStart && d.StartTimeUtc <= periodEnd)
            .Where(d => excludedClientIps.Count == 0 || !excludedClientIps.Contains(d.ClientIp))
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
