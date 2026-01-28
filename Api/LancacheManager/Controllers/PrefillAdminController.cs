using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// Admin endpoints for managing prefill sessions and Steam user bans.
/// Requires authentication.
/// </summary>
[ApiController]
[Route("api/prefill-admin")]
[RequireAuth]
public class PrefillAdminController : ControllerBase
{
    private readonly PrefillSessionService _sessionService;
    private readonly SteamPrefillDaemonService _daemonService;
    private readonly PrefillCacheService _cacheService;
    private readonly ILogger<PrefillAdminController> _logger;
    private readonly ISignalRNotificationService _notifications;

    public PrefillAdminController(
        PrefillSessionService sessionService,
        SteamPrefillDaemonService daemonService,
        PrefillCacheService cacheService,
        ILogger<PrefillAdminController> logger,
        ISignalRNotificationService notifications)
    {
        _sessionService = sessionService;
        _daemonService = daemonService;
        _cacheService = cacheService;
        _logger = logger;
        _notifications = notifications;
    }

    private string? GetDeviceId() =>
        HttpContext.Items.TryGetValue("DeviceId", out var deviceId) ? deviceId as string : null;

    #region Session Management

    /// <summary>
    /// Gets all prefill sessions (paginated).
    /// </summary>
    [HttpGet("sessions")]
    public async Task<ActionResult<PrefillSessionsResponse>> GetSessions(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null)
    {
        var (sessions, totalCount) = await _sessionService.GetSessionsAsync(page, pageSize, status);

        // Also get in-memory sessions for live data
        var liveSessions = _daemonService.GetAllSessions();

        // Enrich DB sessions with live data
        var enrichedSessions = sessions.Select(s =>
        {
            var liveSession = liveSessions.FirstOrDefault(ls => ls.Id == s.SessionId);
            return new PrefillSessionDto
            {
                Id = s.Id,
                SessionId = s.SessionId,
                DeviceId = s.DeviceId,
                ContainerId = s.ContainerId,
                ContainerName = s.ContainerName,
                SteamUsername = liveSession?.SteamUsername ?? s.SteamUsername,
                Status = liveSession?.Status.ToString() ?? s.Status,
                IsAuthenticated = liveSession?.AuthState == DaemonAuthState.Authenticated || s.IsAuthenticated,
                IsPrefilling = liveSession?.IsPrefilling ?? s.IsPrefilling,
                CreatedAtUtc = s.CreatedAtUtc,
                EndedAtUtc = s.EndedAtUtc,
                ExpiresAtUtc = s.ExpiresAtUtc,
                TerminationReason = s.TerminationReason,
                TerminatedBy = s.TerminatedBy,
                IsLive = liveSession != null
            };
        }).ToList();

        return Ok(new PrefillSessionsResponse
        {
            Sessions = enrichedSessions,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        });
    }

    /// <summary>
    /// Gets all currently active (in-memory) sessions.
    /// </summary>
    [HttpGet("sessions/active")]
    public ActionResult<List<DaemonSessionDto>> GetActiveSessions()
    {
        var sessions = _daemonService.GetAllSessions()
            .Select(DaemonSessionDto.FromSession)
            .ToList();
        return Ok(sessions);
    }

    /// <summary>
    /// Gets prefill history for a specific session.
    /// </summary>
    [HttpGet("sessions/{sessionId}/history")]
    public async Task<ActionResult<List<PrefillHistoryEntryDto>>> GetSessionHistory(string sessionId)
    {
        var history = await _sessionService.GetPrefillHistoryAsync(sessionId);

        return Ok(history.Select(h => new PrefillHistoryEntryDto
        {
            Id = h.Id,
            SessionId = h.SessionId,
            AppId = h.AppId,
            AppName = h.AppName,
            StartedAtUtc = h.StartedAtUtc,
            CompletedAtUtc = h.CompletedAtUtc,
            BytesDownloaded = h.BytesDownloaded,
            TotalBytes = h.TotalBytes,
            Status = h.Status,
            ErrorMessage = h.ErrorMessage
        }).ToList());
    }

    /// <summary>
    /// Terminates a specific session.
    /// </summary>
    [HttpPost("sessions/{sessionId}/terminate")]
    public async Task<ActionResult> TerminateSession(
        string sessionId,
        [FromBody] TerminateSessionRequest? request = null)
    {
        var adminDeviceId = GetDeviceId();
        var reason = request?.Reason ?? "Terminated by admin";
        var force = request?.Force ?? false;

        _logger.LogWarning("Admin {AdminId} terminating session {SessionId}: {Reason}",
            adminDeviceId, sessionId, reason);

        await _daemonService.TerminateSessionAsync(sessionId, reason, force, adminDeviceId);

        return Ok(ApiResponse.Message("Session terminated"));
    }

    /// <summary>
    /// Terminates all active sessions.
    /// </summary>
    [HttpPost("sessions/terminate-all")]
    public async Task<ActionResult> TerminateAllSessions([FromBody] TerminateSessionRequest? request = null)
    {
        var adminDeviceId = GetDeviceId();
        var reason = request?.Reason ?? "All sessions terminated by admin";
        var force = request?.Force ?? true;

        var sessions = _daemonService.GetAllSessions().ToList();
        var count = sessions.Count;

        _logger.LogWarning("Admin {AdminId} terminating all {Count} sessions: {Reason}",
            adminDeviceId, count, reason);

        foreach (var session in sessions)
        {
            await _daemonService.TerminateSessionAsync(session.Id, reason, force, adminDeviceId);
        }

        return Ok(new { message = $"Terminated {count} sessions" });
    }

    #endregion

    #region Ban Management

    /// <summary>
    /// Gets all active bans.
    /// </summary>
    [HttpGet("bans")]
    public async Task<ActionResult<List<BannedSteamUserDto>>> GetBans([FromQuery] bool includeLifted = false)
    {
        var bans = includeLifted
            ? await _sessionService.GetAllBansAsync()
            : await _sessionService.GetActiveBansAsync();

        return Ok(bans.Select(b => new BannedSteamUserDto
        {
            Id = b.Id,
            Username = b.Username,
            BanReason = b.BanReason,
            BannedDeviceId = b.BannedDeviceId,
            BannedAtUtc = b.BannedAtUtc,
            BannedBy = b.BannedBy,
            ExpiresAtUtc = b.ExpiresAtUtc,
            IsLifted = b.IsLifted,
            LiftedAtUtc = b.LiftedAtUtc,
            LiftedBy = b.LiftedBy,
            IsActive = !b.IsLifted && (b.ExpiresAtUtc == null || b.ExpiresAtUtc > DateTime.UtcNow)
        }).ToList());
    }

    /// <summary>
    /// Bans a Steam user by session ID.
    /// Looks up the username from the session.
    /// </summary>
    [HttpPost("bans/by-session/{sessionId}")]
    public async Task<ActionResult<BannedSteamUserDto>> BanBySession(
        string sessionId,
        [FromBody] BanRequest request)
    {
        var adminDeviceId = GetDeviceId();

        var ban = await _sessionService.BanUserBySessionAsync(
            sessionId,
            request.Reason,
            adminDeviceId,
            request.ExpiresAt);

        if (ban == null)
        {
            return BadRequest(ApiResponse.Error("Could not ban user - session has no username. User may not have logged in yet."));
        }

        // Also terminate the session
        await _daemonService.TerminateSessionAsync(sessionId, "Banned by admin", true, adminDeviceId);

        // Notify the banned device via SignalR so their UI updates immediately
        if (!string.IsNullOrEmpty(ban.BannedDeviceId))
        {
            await _notifications.NotifyAllAsync(SignalREvents.SteamUserBanned, new
            {
                deviceId = ban.BannedDeviceId,
                username = ban.Username,
                reason = ban.BanReason,
                expiresAt = ban.ExpiresAtUtc?.ToString("o")
            });
        }

        _logger.LogWarning("Admin {AdminId} banned Steam user {Username} from session {SessionId}. Reason: {Reason}",
            adminDeviceId, ban.Username, sessionId, request.Reason);

        return Ok(new BannedSteamUserDto
        {
            Id = ban.Id,
            Username = ban.Username,
            BanReason = ban.BanReason,
            BannedDeviceId = ban.BannedDeviceId,
            BannedAtUtc = ban.BannedAtUtc,
            BannedBy = ban.BannedBy,
            ExpiresAtUtc = ban.ExpiresAtUtc,
            IsLifted = ban.IsLifted,
            IsActive = true
        });
    }

    /// <summary>
    /// Bans a Steam user by username.
    /// </summary>
    [HttpPost("bans")]
    public async Task<ActionResult<BannedSteamUserDto>> BanByUsername([FromBody] BanByUsernameRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return BadRequest(ApiResponse.Required("Username"));
        }

        var adminDeviceId = GetDeviceId();

        var ban = await _sessionService.BanUserAsync(
            request.Username,
            request.Reason,
            request.DeviceId,
            adminDeviceId,
            request.ExpiresAt);

        // Notify the banned device via SignalR so their UI updates immediately
        if (!string.IsNullOrEmpty(ban.BannedDeviceId))
        {
            await _notifications.NotifyAllAsync(SignalREvents.SteamUserBanned, new
            {
                deviceId = ban.BannedDeviceId,
                username = ban.Username,
                reason = ban.BanReason,
                expiresAt = ban.ExpiresAtUtc?.ToString("o")
            });
        }

        _logger.LogWarning("Admin {AdminId} banned Steam user {Username}. Reason: {Reason}",
            adminDeviceId, ban.Username, request.Reason);

        return Ok(new BannedSteamUserDto
        {
            Id = ban.Id,
            Username = ban.Username,
            BanReason = ban.BanReason,
            BannedDeviceId = ban.BannedDeviceId,
            BannedAtUtc = ban.BannedAtUtc,
            BannedBy = ban.BannedBy,
            ExpiresAtUtc = ban.ExpiresAtUtc,
            IsLifted = ban.IsLifted,
            IsActive = true
        });
    }

    /// <summary>
    /// Lifts a ban.
    /// </summary>
    [HttpPost("bans/{banId}/lift")]
    public async Task<ActionResult> LiftBan(int banId)
    {
        var adminDeviceId = GetDeviceId();

        var ban = await _sessionService.LiftBanAsync(banId, adminDeviceId);

        if (ban == null)
        {
            return NotFound(ApiResponse.Error("Ban not found or already lifted"));
        }

        // Notify the unbanned device via SignalR so their UI updates immediately
        if (!string.IsNullOrEmpty(ban.BannedDeviceId))
        {
            await _notifications.NotifyAllAsync(SignalREvents.SteamUserUnbanned, new
            {
                deviceId = ban.BannedDeviceId,
                username = ban.Username
            });
        }

        _logger.LogInformation("Admin {AdminId} lifted ban {BanId}", adminDeviceId, banId);

        return Ok(ApiResponse.Message("Ban lifted"));
    }

    #endregion

    #region Prefill Cache

    /// <summary>
    /// Gets all cached apps with their cache timestamps.
    /// </summary>
    [HttpGet("cache")]
    public async Task<ActionResult<List<CachedAppDto>>> GetCachedApps()
    {
        var apps = await _cacheService.GetCachedAppsAsync();

        return Ok(apps.Select(a => new CachedAppDto
        {
            AppId = a.AppId,
            AppName = a.AppName,
            DepotCount = a.DepotCount,
            TotalBytes = a.TotalBytes,
            CachedAtUtc = a.CachedAtUtc,
            CachedBy = a.CachedBy
        }).ToList());
    }

    /// <summary>
    /// Checks if specific apps are cached. Returns which ones are cached.
    /// </summary>
    [HttpPost("cache/check")]
    public async Task<ActionResult<CacheCheckResponse>> CheckAppsCached([FromBody] List<uint> appIds)
    {
        if (appIds == null || appIds.Count == 0)
        {
            return BadRequest(ApiResponse.Error("No app IDs provided"));
        }

        var cachedApps = await _cacheService.GetCachedAppsAsync();
        var cachedAppIds = cachedApps.Select(a => a.AppId).ToHashSet();

        var result = new CacheCheckResponse
        {
            CachedAppIds = appIds.Where(id => cachedAppIds.Contains(id)).ToList(),
            UncachedAppIds = appIds.Where(id => !cachedAppIds.Contains(id)).ToList(),
            CacheInfo = cachedApps
                .Where(a => appIds.Contains(a.AppId))
                .Select(a => new CachedAppDto
                {
                    AppId = a.AppId,
                    AppName = a.AppName,
                    DepotCount = a.DepotCount,
                    TotalBytes = a.TotalBytes,
                    CachedAtUtc = a.CachedAtUtc,
                    CachedBy = a.CachedBy
                })
                .ToList()
        };

        return Ok(result);
    }

    /// <summary>
    /// Clears cache for a specific app (for force re-download).
    /// </summary>
    [HttpDelete("cache/{appId}")]
    public async Task<ActionResult> ClearAppCache(uint appId)
    {
        await _cacheService.ClearAppCacheAsync(appId);
        _logger.LogInformation("Cache cleared for app {AppId} by {DeviceId}", appId, GetDeviceId());
        return Ok(ApiResponse.Message($"Cache cleared for app {appId}"));
    }

    /// <summary>
    /// Clears the entire prefill cache.
    /// </summary>
    [HttpDelete("cache")]
    public async Task<ActionResult> ClearAllCache()
    {
        await _cacheService.ClearAllCacheAsync();
        _logger.LogInformation("Entire prefill cache cleared by {DeviceId}", GetDeviceId());
        return Ok(ApiResponse.Message("Prefill cache cleared"));
    }

    #endregion
}


