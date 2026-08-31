using LancacheManager.Models;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Utilities;
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
    private readonly CacheScanGate _cacheScanGate;

    public SpeedsController(
        RustSpeedTrackerService speedTrackerService,
        IDbContextFactory<AppDbContext> contextFactory,
        IStateService stateRepository,
        CacheScanGate cacheScanGate)
    {
        _speedTrackerService = speedTrackerService;
        _contextFactory = contextFactory;
        _stateRepository = stateRepository;
        _cacheScanGate = cacheScanGate;
    }

    /// <summary>
    /// Gets current download speeds for all active games and clients.
    /// </summary>
    /// <remarks>
    /// Hidden-client and evicted-data filtering happen inside the tracker's shared client-visible
    /// snapshot builder, which the SignalR broadcast also uses, so both transports always expose
    /// identical visibility semantics. Prefill traffic is not filtered and appears like any other
    /// client.
    /// </remarks>
    [HttpGet("current")]
    [ProducesResponseType(typeof(DownloadSpeedSnapshot), StatusCodes.Status200OK)]
    public ActionResult<DownloadSpeedSnapshot> GetCurrentSpeeds()
    {
        return Ok(_speedTrackerService.GetCurrentSnapshot());
    }

    /// <summary>
    /// Says whether a cache scan would be refused right now, and why.
    /// </summary>
    /// <remarks>
    /// Answers from the same gate the scan routes use, so a control that reads this can never
    /// offer an action the server is about to refuse. Deliberately NOT derived from the
    /// client-visible speeds: hiding a client keeps it off the dashboard, it does not stop its
    /// bytes reaching the cache. Blocked is also true while the tracker has not reported yet,
    /// which is not the same as nothing downloading, and the reason says which it is.
    /// </remarks>
    [HttpGet("scan-blocked")]
    [ProducesResponseType(typeof(CacheScanBlockedResponse), StatusCodes.Status200OK)]
    public ActionResult<CacheScanBlockedResponse> GetCacheScanBlocked()
    {
        var reason = _cacheScanGate.CheckDownloadInProgress();
        return Ok(new CacheScanBlockedResponse { Blocked = reason != null, Reason = reason });
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
        var hiddenClientIps = _stateRepository.GetHiddenClientIps();
        var evictedMode = _stateRepository.GetEvictedDataMode();

        // Query downloads within the time period
        var query = context.Downloads
            .Where(d => d.EndTimeUtc >= periodStart && d.StartTimeUtc <= periodEnd)
            .Where(d => hiddenClientIps.Count == 0 || !hiddenClientIps.Contains(d.ClientIp));

        // Apply eviction filter (hide/remove modes exclude evicted downloads)
        query = query.ApplyEvictedFilter(evictedMode);

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
}
