using LancacheManager.Models;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// Admin endpoints for managing prefill sessions and prefill user bans.
/// Requires authentication.
/// </summary>
[ApiController]
[Route("api/prefill-admin")]
[Authorize]
public class PrefillAdminController : ControllerBase
{
    private readonly PrefillSessionService _sessionService;
    private readonly SteamDaemonService _steamDaemonService;
    private readonly EpicPrefillDaemonService _epicDaemonService;
    private readonly BattleNetDaemonService _battleNetDaemonService;
    private readonly RiotDaemonService _riotDaemonService;
    private readonly XboxPrefillDaemonService _xboxDaemonService;
    private readonly PrefillCacheService _cacheService;
    private readonly ISignalRNotificationService _notifications;
    private readonly ILogger<PrefillAdminController> _logger;

    public PrefillAdminController(
        PrefillSessionService sessionService,
        SteamDaemonService steamDaemonService,
        EpicPrefillDaemonService epicDaemonService,
        BattleNetDaemonService battleNetDaemonService,
        RiotDaemonService riotDaemonService,
        XboxPrefillDaemonService xboxDaemonService,
        PrefillCacheService cacheService,
        ISignalRNotificationService notifications,
        ILogger<PrefillAdminController> logger)
    {
        _sessionService = sessionService;
        _steamDaemonService = steamDaemonService;
        _epicDaemonService = epicDaemonService;
        _battleNetDaemonService = battleNetDaemonService;
        _riotDaemonService = riotDaemonService;
        _xboxDaemonService = xboxDaemonService;
        _cacheService = cacheService;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>
    /// Builds a BannedPrefillUserDto from a BannedPrefillUser entity (always sets IsActive = true for new bans).
    /// </summary>
    private static BannedPrefillUserDto ToBanDto(BannedPrefillUser ban) => new()
    {
        Id = ban.Id,
        Username = ban.Username,
        BanReason = ban.BanReason,
        BannedBySessionId = TryParseGuid(ban.BannedBySessionId),
        BannedAtUtc = ban.BannedAtUtc,
        BannedBy = ban.BannedBy,
        ExpiresAtUtc = ban.ExpiresAtUtc,
        IsLifted = ban.IsLifted,
        IsActive = true
    };

    /// <summary>
    /// Parses a session id string to Guid, returning null for null/empty/invalid values.
    /// Used at the entity/DTO boundary for fields that legacy-store UserSession.Id as string.
    /// </summary>
    private static Guid? TryParseGuid(string? value)
        => Guid.TryParse(value, out var guid) ? guid : null;

    #region Session Management

    /// <summary>
    /// Gets all prefill sessions (paginated), less the ones an account the caller may not see started.
    /// </summary>
    [Authorize(Policy = "AccountHolder")]
    [HttpGet("sessions")]
    [ProducesResponseType(typeof(PrefillSessionsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PrefillSessionsResponse>> GetSessionsAsync(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        [FromQuery] string? platform = null)
    {
        var caller = HttpContext.GetUserSession();
        var (sessions, totalCount) = await _sessionService.GetSessionsAsync(caller, page, pageSize, status, platform);

        // Also get in-memory sessions for live data from all services
        var steamSessions = _steamDaemonService.GetAllSessions(includeTerminating: true);
        var epicSessions = _epicDaemonService.GetAllSessions(includeTerminating: true);
        var battleNetSessions = _battleNetDaemonService.GetAllSessions(includeTerminating: true);
        var riotSessions = _riotDaemonService.GetAllSessions(includeTerminating: true);
        var xboxSessions = _xboxDaemonService.GetAllSessions(includeTerminating: true);
        var liveSessions = steamSessions.Concat(epicSessions).Concat(battleNetSessions).Concat(riotSessions).Concat(xboxSessions).ToList();

        // Enrich DB sessions with live data.
        // DaemonSession.Id is a 16-char daemon-local id (string); PrefillSession.SessionId is a Guid.
        // Compare via their canonical string forms so matching remains consistent.
        var enrichedSessions = sessions.Select(s =>
        {
            var liveSession = liveSessions.FirstOrDefault(ls => ls.Id == s.SessionId);
            return PrefillSessionDto.FromEntity(s, liveSession);
        }).ToList();

        var (lastCacheIp, lastCacheIpSource) = GetLastPrefillCacheRouting();

        return Ok(new PrefillSessionsResponse
        {
            Sessions = enrichedSessions,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            LastPrefillCacheIp = lastCacheIp,
            LastPrefillCacheIpSource = lastCacheIpSource
        });
    }

    /// <summary>
    /// Most recent LANCACHE_IP injection across the five daemon services. All services resolve
    /// through the same shared locator, so any recorded value is representative; a service that
    /// found an IP wins over one that only recorded a source with no IP.
    /// </summary>
    private (string? Ip, string? Source) GetLastPrefillCacheRouting()
    {
        var services = GetDaemons();

        var withIp = services.FirstOrDefault(s => s.LastInjectedLancacheIp != null);
        if (withIp != null)
        {
            return (withIp.LastInjectedLancacheIp, withIp.LastLancacheIpSource);
        }

        var attempted = services.FirstOrDefault(s => s.LastLancacheIpSource != null);
        return (null, attempted?.LastLancacheIpSource);
    }

    /// <summary>
    /// Gets all currently active (in-memory) sessions, less the ones an account the caller may not see
    /// started.
    /// </summary>
    /// <remarks>
    /// <see cref="DaemonSessionDto.UserId"/> is the auth session that created the container, the
    /// same id the session list withholds, so this list is filtered on the same rule.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpGet("sessions/active")]
    [ProducesResponseType(typeof(List<DaemonSessionDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<DaemonSessionDto>>> GetActiveSessionsAsync()
    {
        var caller = HttpContext.GetUserSession();
        var sessions = GetDaemons().SelectMany(daemon => daemon.GetAllSessions(includeTerminating: true))
            .ToList();

        var visible = new List<DaemonSessionDto>();
        foreach (var session in sessions)
        {
            if (await _sessionService.CallerMaySeeSessionAsync(caller, session.Id))
            {
                visible.Add(DaemonSessionDto.FromSession(session));
            }
        }

        return Ok(visible);
    }

    /// <summary>
    /// Gets prefill history for a specific session. A session an account the caller may not see started
    /// answers as one that does not exist.
    /// </summary>
    [Authorize(Policy = "AccountHolder")]
    [HttpGet("sessions/{sessionId}/history")]
    [ProducesResponseType(typeof(List<PrefillHistoryEntryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<PrefillHistoryEntryDto>>> GetSessionHistoryAsync(string sessionId)
    {
        if (!await _sessionService.CallerMaySeeSessionAsync(HttpContext.GetUserSession(), sessionId))
        {
            return NotFound(ApiResponse.NotFound("Session"));
        }

        var history = await _sessionService.GetHistoryAsync(sessionId);

        return Ok(history.Select(h => new PrefillHistoryEntryDto
        {
            Id = h.Id,
            SessionId = h.SessionId.ToString(),
            AppId = h.AppId,
            AppName = h.AppName,
            StartedAtUtc = h.StartedAtUtc,
            CompletedAtUtc = h.CompletedAtUtc,
            BytesDownloaded = h.BytesDownloaded,
            TotalBytes = h.TotalBytes,
            Status = h.Status.ToString(),
            ErrorMessage = h.ErrorMessage
        }).ToList());
    }

    /// <summary>
    /// Terminates a specific session.
    /// </summary>
    [Authorize(Policy = "AccountHolder")]
    [HttpPost("sessions/{sessionId}/terminate")]
    [ProducesResponseType(typeof(MessageOnlyResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MessageOnlyResponse>> TerminateAsync(
        string sessionId,
        [FromBody] TerminateSessionRequest? request = null)
    {
        if (!await _sessionService.CallerMaySeeSessionAsync(HttpContext.GetUserSession(), sessionId))
            return NotFound(ApiResponse.NotFound("Session"));
        var adminSessionId = HttpContext.GetRequiredSessionId();
        var reason = request?.Reason ?? "Terminated by admin";
        var target = GetDaemons().SelectMany(daemon => daemon.GetAllSessions(includeTerminating: true))
            .FirstOrDefault(session => session.Id == sessionId);
        var history = target == null ? await _sessionService.GetSessionAsync(sessionId) : null;
        if ((target != null && !PrefillSessionService.IsTerminatableByAdmin(target))
            || (history != null && !PrefillSessionService.IsTerminatableByAdmin(history)))
            return BadRequest(ApiResponse.Error("This session belongs to a persistent container. Stop it from Management > Schedules instead."));
        var owner = target?.IsTemporary == true ? target.UserId : history?.CreatedBySessionId;
        var guest = owner.HasValue ? await _sessionService.GetGuestSessionAsync(owner.Value) : null;
        if (guest != null)
            PrefillDaemonServiceBase.GuestGate.EnterStop(guest.Id);
        try
        {
            var stopped = guest != null
                ? await PrefillDaemonServiceBase.TerminateGuestSessionsAsync(GetDaemons(), guest.Id, reason, adminSessionId.ToString())
                : await StopSessionAsync(sessionId, reason, request?.Force ?? false, adminSessionId.ToString());
            if (!stopped.Success)
            {
                var error = ApiResponse.Error("Prefill cleanup is incomplete. Retry this action.",
                    $"{stopped.FailedSessions} sessions and {stopped.PendingStarts} starts still require cleanup.");
                error.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return StatusCode(StatusCodes.Status503ServiceUnavailable, error);
            }
            return Ok(new MessageOnlyResponse { Message = "Session terminated" });
        }
        finally { if (guest != null) PrefillDaemonServiceBase.GuestGate.ExitStop(guest.Id); }
    }

    private PrefillDaemonServiceBase[] GetDaemons() =>
        [_steamDaemonService, _epicDaemonService, _battleNetDaemonService, _riotDaemonService, _xboxDaemonService];

    private async Task<GuestPrefillStopResult> StopSessionAsync(string sessionId, string reason, bool force, string terminatedBy)
    {
        var stops = GetDaemons().Select(daemon => daemon.TerminateSessionAsync(sessionId, reason, force, terminatedBy)).ToArray();
        try { await Task.WhenAll(stops); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session {SessionId} cleanup is incomplete; unfinished stages remain available for retry", sessionId);
        }
        return new GuestPrefillStopResult(stops.Count(stop => !stop.IsCompletedSuccessfully), 0);
    }

    /// <summary>
    /// The sessions of one daemon service this caller may both see and tear down.
    /// </summary>
    /// <remarks>
    /// Persistent containers are excluded: they have their own stop path
    /// (PersistentPrefillController.StopAsync) and must survive a guest "end all sessions" sweep.
    /// A session the caller may not see is excluded on the session list's rule.
    /// </remarks>
    private async Task<List<DaemonSession>> TerminatableByCallerAsync(
        PrefillDaemonServiceBase service,
        UserSession? caller)
    {
        var terminatable = new List<DaemonSession>();
        foreach (var session in service.GetAllSessions(includeTerminating: true).Where(PrefillSessionService.IsTerminatableByAdmin))
        {
            if (await _sessionService.CallerMaySeeSessionAsync(caller, session.Id))
            {
                terminatable.Add(session);
            }
        }

        return terminatable;
    }

    /// <summary>
    /// Terminates all active sessions, less the ones an account the caller may not see started.
    /// </summary>
    /// <remarks>
    /// The count in the reply is of the sessions this caller could see, so a sweep by a second
    /// administrator cannot report that a withheld session was there.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpPost("sessions/terminate-all")]
    [ProducesResponseType(typeof(TerminatedSessionsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<TerminatedSessionsResponse>> TerminateAllAsync([FromBody] TerminateSessionRequest? request = null)
    {
        var caller = HttpContext.GetUserSession();
        var adminSessionId = HttpContext.GetRequiredSessionId();
        var adminSessionIdString = adminSessionId.ToString();
        var reason = request?.Reason ?? "All sessions terminated by admin";
        var force = request?.Force ?? true;

        var steamSessions = await TerminatableByCallerAsync(_steamDaemonService, caller);
        var epicSessions = await TerminatableByCallerAsync(_epicDaemonService, caller);
        var battleNetSessions = await TerminatableByCallerAsync(_battleNetDaemonService, caller);
        var riotSessions = await TerminatableByCallerAsync(_riotDaemonService, caller);
        var xboxSessions = await TerminatableByCallerAsync(_xboxDaemonService, caller);
        var count = steamSessions.Count
            + epicSessions.Count
            + battleNetSessions.Count
            + riotSessions.Count
            + xboxSessions.Count;

        _logger.LogWarning("Admin session {AdminId} terminating all {Count} non-persistent sessions: {Reason}",
            adminSessionId, count, reason);

        var stops = steamSessions.Select(session => _steamDaemonService.TerminateSessionAsync(session.Id, reason, force, adminSessionIdString))
            .Concat(epicSessions.Select(session => _epicDaemonService.TerminateSessionAsync(session.Id, reason, force, adminSessionIdString)))
            .Concat(battleNetSessions.Select(session => _battleNetDaemonService.TerminateSessionAsync(session.Id, reason, force, adminSessionIdString)))
            .Concat(riotSessions.Select(session => _riotDaemonService.TerminateSessionAsync(session.Id, reason, force, adminSessionIdString)))
            .Concat(xboxSessions.Select(session => _xboxDaemonService.TerminateSessionAsync(session.Id, reason, force, adminSessionIdString)))
            .ToArray();
        try { await Task.WhenAll(stops); }
        catch (Exception)
        {
            var failed = stops.Count(stop => !stop.IsCompletedSuccessfully);
            var error = ApiResponse.Error("Some prefill sessions could not be terminated. Retry this action.",
                $"{count - failed} sessions terminated; {failed} sessions still require cleanup.");
            error.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return StatusCode(StatusCodes.Status503ServiceUnavailable, error);
        }

        return Ok(new TerminatedSessionsResponse { Count = count });
    }

    #endregion

    #region Ban Management

    /// <summary>
    /// Gets all active bans, less the acting session id on any ban an account the caller may not see
    /// placed.
    /// </summary>
    /// <remarks>
    /// A ban is a shared moderation record, so every row is answered. What is withheld is
    /// <see cref="BannedPrefillUserDto.BannedBySessionId"/>, the same id the session list withholds.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpGet("bans")]
    [ProducesResponseType(typeof(List<BannedPrefillUserDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<BannedPrefillUserDto>>> GetBansAsync([FromQuery] bool includeLifted = false)
    {
        var bans = includeLifted
            ? await _sessionService.GetAllBansAsync()
            : await _sessionService.GetActiveBansAsync();

        var hiddenSessionIds = await _sessionService.HiddenSessionIdsAsync(HttpContext.GetUserSession());

        return Ok(bans.Select(b => new BannedPrefillUserDto
        {
            Id = b.Id,
            Username = b.Username,
            BanReason = b.BanReason,
            BannedBySessionId = TryParseGuid(b.BannedBySessionId) is { } bannedBySessionId
                && !hiddenSessionIds.Contains(bannedBySessionId)
                    ? bannedBySessionId
                    : null,
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
    /// Bans a prefill user by session ID.
    /// </summary>
    /// <remarks>
    /// Looks up the username from the session. A session an account the caller may not see started
    /// answers as one that does not exist, so the ban cannot be used to confirm it is there.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpPost("bans/by-session/{sessionId}")]
    [ProducesResponseType(typeof(BannedPrefillUserDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<BannedPrefillUserDto>> BanBySessionAsync(
        string sessionId,
        [FromBody] BanRequest request)
    {
        if (!await _sessionService.CallerMaySeeSessionAsync(HttpContext.GetUserSession(), sessionId))
            return NotFound(ApiResponse.NotFound("Session"));
        var adminSessionId = HttpContext.GetRequiredSessionId().ToString();
        var target = GetDaemons().SelectMany(daemon => daemon.GetAllSessions(includeTerminating: true))
            .FirstOrDefault(session => session.Id == sessionId);
        var history = target == null ? await _sessionService.GetSessionAsync(sessionId) : null;
        if ((target != null && !PrefillSessionService.IsTerminatableByAdmin(target))
            || (history != null && !PrefillSessionService.IsTerminatableByAdmin(history)))
            return BadRequest(ApiResponse.Error("This session belongs to a persistent container. Stop it from Management > Schedules instead."));
        var owner = target?.IsTemporary == true ? target.UserId : history?.CreatedBySessionId;
        var guest = owner.HasValue ? await _sessionService.GetGuestSessionAsync(owner.Value) : null;
        if (guest != null)
            PrefillDaemonServiceBase.GuestGate.EnterStop(guest.Id);
        var cleanup = guest != null
            ? PrefillDaemonServiceBase.TerminateGuestSessionsAsync(GetDaemons(), guest.Id, "Banned by admin", adminSessionId)
            : Task.FromResult(new GuestPrefillStopResult(0, 0));
        try
        {
            var ban = await _sessionService.BanUserBySessionAsync(sessionId, request.Reason, adminSessionId, request.ExpiresAt);
            if (ban == null)
                return BadRequest(ApiResponse.Error("Could not ban user - session not found or has no identity to ban."));
            var stopped = guest != null
                ? await cleanup
                : await StopSessionAsync(sessionId, "Banned by admin", force: true, adminSessionId);
            if (!stopped.Success)
            {
                var error = ApiResponse.Error("The ban was saved, but prefill cleanup is incomplete. Retry this action.",
                    $"{stopped.FailedSessions} sessions and {stopped.PendingStarts} starts still require cleanup.");
                error.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return StatusCode(StatusCodes.Status503ServiceUnavailable, error);
            }
            return Ok(ToBanDto(ban));
        }
        finally
        {
            try { await cleanup; }
            finally { if (guest != null) PrefillDaemonServiceBase.GuestGate.ExitStop(guest.Id); }
        }
    }

    /// <summary>
    /// Bans a prefill user by username.
    /// </summary>
    [Authorize(Policy = "AccountHolder")]
    [HttpPost("bans")]
    [ProducesResponseType(typeof(BannedPrefillUserDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<BannedPrefillUserDto>> BanByUsernameAsync([FromBody] BanByUsernameRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return BadRequest(ApiResponse.Required("Username"));
        }

        var adminSessionId = HttpContext.GetRequiredSessionId();
        var adminSessionIdString = adminSessionId.ToString();

        var ban = await _sessionService.BanUserAsync(
            request.Username,
            request.Reason,
            request.SessionId?.ToString(),
            adminSessionIdString,
            request.ExpiresAt);

        _logger.LogWarning("Admin session {AdminId} banned prefill user {Username}. Reason: {Reason}",
            adminSessionId, ban.Username, request.Reason);

        return Ok(ToBanDto(ban));
    }

    /// <summary>
    /// Lifts a ban.
    /// </summary>
    [Authorize(Policy = "AccountHolder")]
    [HttpPost("bans/{banId}/lift")]
    [ProducesResponseType(typeof(MessageOnlyResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MessageOnlyResponse>> LiftBanAsync(long banId)
    {
        var adminSessionId = HttpContext.GetRequiredSessionId();
        var adminSessionIdString = adminSessionId.ToString();

        var ban = await _sessionService.LiftBanAsync(banId, adminSessionIdString);

        if (ban == null)
        {
            return NotFound(ApiResponse.Error("Ban not found or already lifted"));
        }

        _logger.LogInformation("Admin session {AdminId} lifted ban {BanId}", adminSessionId, banId);

        return Ok(new MessageOnlyResponse { Message = "Ban lifted" });
    }

    #endregion

    #region Prefill Cache

    /// <summary>
    /// Gets all cached apps with their cache timestamps.
    /// </summary>
    [Authorize(Policy = "AnyPrefillAccess")]
    [HttpGet("cache")]
    [ProducesResponseType(typeof(List<CachedAppDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<CachedAppDto>>> GetCachedAppsAsync([FromQuery] PrefillPlatform? service)
    {
        if (!service.HasValue || !Enum.IsDefined(service.Value))
        {
            return BadRequest(ApiResponse.Error("A valid prefill service is required"));
        }
        var apps = await _cacheService.GetCachedAppsAsync(service.Value, HttpContext.RequestAborted);

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
    /// Clears the entire prefill cache.
    /// </summary>
    [Authorize(Policy = "AccountHolder")]
    [HttpDelete("cache")]
    [ProducesResponseType(typeof(MessageOnlyResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MessageOnlyResponse>> ClearAllCacheAsync([FromQuery] PrefillPlatform? service)
    {
        if (!service.HasValue || !Enum.IsDefined(service.Value))
        {
            return BadRequest(ApiResponse.Error("A valid prefill service is required"));
        }
        var removed = await _cacheService.ClearAllCacheAsync(service.Value);
        _logger.LogInformation("Entire prefill cache cleared by session {SessionId}", HttpContext.GetRequiredSessionId());
        if (removed.RemovedApps + removed.RemovedDepots > 0)
        {
            await _notifications.NotifyAllAsync(SignalREvents.PrefillCacheChanged);
        }
        return Ok(new MessageOnlyResponse { Message = "Prefill cache cleared" });
    }

    /// <summary>
    /// Clears the prefill cache for a single app so its next prefill downloads it again.
    /// </summary>
    [Authorize(Policy = "AccountHolder")]
    [HttpDelete("cache/{appId}")]
    [ProducesResponseType(typeof(PrefillCacheRemovalResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PrefillCacheRemovalResponse>> ClearAppCacheAsync(string appId, [FromQuery] PrefillPlatform? service)
    {
        if (!service.HasValue || !Enum.IsDefined(service.Value))
        {
            return BadRequest(ApiResponse.Error("A valid prefill service is required"));
        }
        var (removedApps, removedDepots) = await _cacheService.ClearAppCacheAsync(service.Value, appId);
        _logger.LogInformation("Prefill cache cleared for app {AppId} ({Count} depots) by session {SessionId}", appId, removedDepots, HttpContext.GetRequiredSessionId());
        if (removedApps + removedDepots > 0)
        {
            await _notifications.NotifyAllAsync(SignalREvents.PrefillCacheChanged);
        }
        return Ok(new PrefillCacheRemovalResponse
        {
            Message = $"Prefill cache cleared for app {appId}",
            RemovedDepots = removedDepots,
            RemovedApps = removedApps
        });
    }

    #endregion
}


