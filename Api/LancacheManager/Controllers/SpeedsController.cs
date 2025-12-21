using LancacheManager.Application.DTOs;
using LancacheManager.Application.Services;
using LancacheManager.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SpeedsController : ControllerBase
{
    private readonly RustSpeedTrackerService _speedTrackerService;
    private readonly NetworkBandwidthService _networkBandwidthService;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<SpeedsController> _logger;

    public SpeedsController(
        RustSpeedTrackerService speedTrackerService,
        NetworkBandwidthService networkBandwidthService,
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<SpeedsController> logger)
    {
        _speedTrackerService = speedTrackerService;
        _networkBandwidthService = networkBandwidthService;
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Get combined speed data: network interface bandwidth + per-game breakdown
    /// </summary>
    [HttpGet("combined")]
    [ProducesResponseType(typeof(CombinedSpeedSnapshot), StatusCodes.Status200OK)]
    public ActionResult<CombinedSpeedSnapshot> GetCombinedSpeeds()
    {
        var combined = new CombinedSpeedSnapshot
        {
            NetworkBandwidth = _networkBandwidthService.GetCurrentSnapshot(),
            GameSpeeds = _speedTrackerService.GetCurrentSnapshot()
        };
        return Ok(combined);
    }

    /// <summary>
    /// Get current network interface bandwidth (upload/download speeds)
    /// </summary>
    [HttpGet("network")]
    [ProducesResponseType(typeof(NetworkBandwidthSnapshot), StatusCodes.Status200OK)]
    public ActionResult<NetworkBandwidthSnapshot> GetNetworkBandwidth()
    {
        var snapshot = _networkBandwidthService.GetCurrentSnapshot();
        return Ok(snapshot);
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
    /// Get current per-game download speeds
    /// </summary>
    [HttpGet("games")]
    [ProducesResponseType(typeof(List<GameSpeedInfo>), StatusCodes.Status200OK)]
    public ActionResult<List<GameSpeedInfo>> GetGameSpeeds()
    {
        var snapshot = _speedTrackerService.GetCurrentSnapshot();
        return Ok(snapshot.GameSpeeds);
    }

    /// <summary>
    /// Get current per-client download speeds
    /// </summary>
    [HttpGet("clients")]
    [ProducesResponseType(typeof(List<ClientSpeedInfo>), StatusCodes.Status200OK)]
    public ActionResult<List<ClientSpeedInfo>> GetClientSpeeds()
    {
        var snapshot = _speedTrackerService.GetCurrentSnapshot();
        return Ok(snapshot.ClientSpeeds);
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

        // Calculate per-game/service statistics (filter out 0-byte entries)
        // For Steam: group by GameAppId/GameName
        // For other services (wsus, riot, epic, etc.): group by Service
        var gameGroups = downloads
            .Where(d => d.TotalBytes > 0) // Only include downloads with actual data
            .GroupBy(d => new
            {
                // For Steam games with AppId, group by game; otherwise group by service
                GroupKey = d.GameAppId.HasValue ? $"game:{d.GameAppId}" : $"service:{d.Service}",
                d.GameAppId,
                d.GameName,
                d.Service
            })
            .Select(g =>
            {
                var totalBytes = g.Sum(d => d.TotalBytes);
                var cacheHitBytes = g.Sum(d => d.CacheHitBytes);
                var cacheMissBytes = g.Sum(d => d.CacheMissBytes);
                var totalDurationSeconds = g.Sum(d => (d.EndTimeUtc - d.StartTimeUtc).TotalSeconds);
                var uniqueClients = g.Select(d => d.ClientIp).Distinct().Count();

                // Use game name if available, otherwise capitalize service name
                var displayName = g.Key.GameName
                    ?? (g.Key.Service != null
                        ? char.ToUpper(g.Key.Service[0]) + g.Key.Service.Substring(1)
                        : "Unknown");

                return new GameSpeedHistoryInfo
                {
                    GameAppId = (int?)g.Key.GameAppId,
                    GameName = displayName,
                    GameImageUrl = g.Key.GameAppId.HasValue
                        ? $"https://cdn.cloudflare.steamstatic.com/steam/apps/{g.Key.GameAppId}/header.jpg"
                        : null,
                    Service = g.Key.Service ?? "unknown",
                    TotalBytes = totalBytes,
                    CacheHitBytes = cacheHitBytes,
                    CacheMissBytes = cacheMissBytes,
                    AverageBytesPerSecond = totalDurationSeconds > 0 ? totalBytes / totalDurationSeconds : 0,
                    SessionCount = g.Count(),
                    FirstSeenUtc = g.Min(d => d.StartTimeUtc),
                    LastSeenUtc = g.Max(d => d.EndTimeUtc),
                    TotalDurationSeconds = totalDurationSeconds,
                    UniqueClients = uniqueClients
                };
            })
            .OrderByDescending(g => g.TotalBytes)
            .ToList();

        // Calculate per-client statistics (filter out 0-byte entries)
        var downloadsWithData = downloads.Where(d => d.TotalBytes > 0).ToList();

        var clientGroups = downloadsWithData
            .GroupBy(d => d.ClientIp)
            .Select(g =>
            {
                var totalBytes = g.Sum(d => d.TotalBytes);
                var cacheHitBytes = g.Sum(d => d.CacheHitBytes);
                var cacheMissBytes = g.Sum(d => d.CacheMissBytes);
                var totalDurationSeconds = g.Sum(d => (d.EndTimeUtc - d.StartTimeUtc).TotalSeconds);

                // Count unique games (by GameAppId) + unique services (for non-game downloads)
                var uniqueGames = g.Where(d => d.GameAppId.HasValue).Select(d => d.GameAppId).Distinct().Count();
                var uniqueServices = g.Where(d => !d.GameAppId.HasValue).Select(d => d.Service).Distinct().Count();
                var totalDownloaded = uniqueGames + uniqueServices;

                return new ClientSpeedHistoryInfo
                {
                    ClientIp = g.Key ?? "unknown",
                    TotalBytes = totalBytes,
                    CacheHitBytes = cacheHitBytes,
                    CacheMissBytes = cacheMissBytes,
                    AverageBytesPerSecond = totalDurationSeconds > 0 ? totalBytes / totalDurationSeconds : 0,
                    GamesDownloaded = totalDownloaded,
                    SessionCount = g.Count(),
                    FirstSeenUtc = g.Min(d => d.StartTimeUtc),
                    LastSeenUtc = g.Max(d => d.EndTimeUtc)
                };
            })
            .OrderByDescending(c => c.TotalBytes)
            .ToList();

        var totalBytes = downloadsWithData.Sum(d => d.TotalBytes);
        var totalDuration = (periodEnd - periodStart).TotalSeconds;

        return Ok(new SpeedHistorySnapshot
        {
            PeriodStartUtc = periodStart,
            PeriodEndUtc = periodEnd,
            PeriodMinutes = minutes,
            TotalBytes = totalBytes,
            AverageBytesPerSecond = totalDuration > 0 ? totalBytes / totalDuration : 0,
            GameSpeeds = gameGroups,
            ClientSpeeds = clientGroups,
            TotalSessions = downloadsWithData.Count
        });
    }
}
