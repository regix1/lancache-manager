using LancacheManager.Models;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SpeedsController : ControllerBase
{
    private readonly RustSpeedTrackerService _speedTrackerService;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IStateService _stateRepository;
    private readonly ILogger<SpeedsController> _logger;

    public SpeedsController(
        RustSpeedTrackerService speedTrackerService,
        IDbContextFactory<AppDbContext> contextFactory,
        IStateService stateRepository,
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
        var evictedMode = _stateRepository.GetEvictedDataMode();

        var filteredClients = snapshot.ClientSpeeds
            .Where(c => !excludedClientIps.Contains(c.ClientIp))
            .ToList();

        var filteredGames = snapshot.GameSpeeds
            .Where(g => string.IsNullOrWhiteSpace(g.ClientIp) || !excludedClientIps.Contains(g.ClientIp))
            .ToList();

        // Apply eviction filter (hide/remove modes exclude evicted entries from speed data)
        if (evictedMode == EvictedDataModes.Hide || evictedMode == EvictedDataModes.Remove)
        {
            filteredGames = filteredGames.Where(g => !g.IsEvicted).ToList();
        }
        else if (evictedMode == EvictedDataModes.ShowClean)
        {
            foreach (var g in filteredGames) g.IsEvicted = false;
        }

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
    public async Task<ActionResult<SpeedHistorySnapshot>> GetSpeedHistoryAsync([FromQuery] int minutes = 60)
    {
        // Clamp to reasonable values
        minutes = Math.Clamp(minutes, 5, 1440); // 5 minutes to 24 hours

        var periodEnd = DateTime.UtcNow;
        var periodStart = periodEnd.AddMinutes(-minutes);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var excludedClientIps = _stateRepository.GetExcludedClientIps();
        var evictedMode = _stateRepository.GetEvictedDataMode();

        // Query downloads within the time period
        var query = context.Downloads
            .Where(d => d.EndTimeUtc >= periodStart && d.StartTimeUtc <= periodEnd)
            .Where(d => excludedClientIps.Count == 0 || !excludedClientIps.Contains(d.ClientIp));

        // Apply eviction filter (hide/remove modes exclude evicted downloads)
        query = ApplyEvictedFilter(query, evictedMode);

        var downloads = await query.ToListAsync();

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

    private static IQueryable<Download> ApplyEvictedFilter(IQueryable<Download> query, string evictedMode)
    {
        if (evictedMode == EvictedDataModes.Hide || evictedMode == EvictedDataModes.Remove)
        {
            return query.Where(d => !d.IsEvicted);
        }
        return query;
    }
}
