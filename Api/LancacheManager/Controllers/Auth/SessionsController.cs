using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.StatusCheck;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Filters;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/sessions")]
[Authorize]
public class SessionsController : ControllerBase
{
    private readonly SessionService _sessionService;
    private readonly ISignalRNotificationService _signalR;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StateService _stateService;

    public SessionsController(
        SessionService sessionService,
        ISignalRNotificationService signalR,
        IServiceScopeFactory scopeFactory,
        StateService stateService)
    {
        _sessionService = sessionService;
        _signalR = signalR;
        _scopeFactory = scopeFactory;
        _stateService = stateService;
    }

    /// <summary>
    /// Lists sessions the caller may see. The owner's sessions are withheld from every other
    /// account holder.
    /// </summary>
    /// <remarks>
    /// Active sessions (paginated), plus the full revoked/expired history unpaginated since it is
    /// only ever shown as a flat audit list.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpGet]
    [ProducesResponseType(typeof(SessionListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SessionListResponse>> GetAllAsync([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var currentSession = HttpContext.GetUserSession();
        var currentSessionId = currentSession?.Id;
        var now = DateTime.UtcNow;

        // Active sessions (paginated). The caller is passed through so the owner's sessions are
        // withheld from everyone else the same way the account list withholds that row.
        var (activeSessions, activeCount) = await _sessionService.GetActiveSessionsPagedAsync(
            page, pageSize, currentSession);
        var totalPages = (int)Math.Ceiling((double)activeCount / pageSize);

        // History sessions (revoked/expired) - unpaginated
        var historySessions = await _sessionService.GetSessionHistoryAsync(currentSession);

        var accountIds = activeSessions
            .Concat(historySessions)
            .Select(session => session.AccountId)
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var usernames = new Dictionary<Guid, string>();
        if (accountIds.Count > 0)
        {
            using var scope = _scopeFactory.CreateScope();
            var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var context = await contexts.CreateDbContextAsync();
            usernames = await context.UserAccounts
                .AsNoTracking()
                .Where(account => accountIds.Contains(account.Id))
                .Select(account => new { account.Id, account.Username })
                .ToDictionaryAsync(account => account.Id, account => account.Username);
        }

        var sharedSessionInUse = !_sessionService.IsAuthenticationEnabled();
        var activeRecords = activeSessions
            .Select(session => ToRecord(
                session,
                currentSessionId,
                now,
                session.AccountId is { } accountId && usernames.TryGetValue(accountId, out var username)
                    ? username
                    : null,
                sharedSessionInUse && _sessionService.IsSharedAdminSession(session.Id)))
            .ToList();
        var historyRecords = historySessions
            .Select(session => ToRecord(
                session,
                currentSessionId,
                now,
                session.AccountId is { } accountId && usernames.TryGetValue(accountId, out var username)
                    ? username
                    : null,
                sharedSessionInUse && _sessionService.IsSharedAdminSession(session.Id)))
            .ToList();

        return Ok(new SessionListResponse
        {
            Sessions = activeRecords,
            Count = activeRecords.Count,
            AdminCount = activeRecords.Count(s => s.SessionType == SessionType.Admin),
            UserCount = activeRecords.Count(s => s.SessionType == SessionType.User),
            GuestCount = activeRecords.Count(s => s.SessionType == SessionType.Guest),
            Pagination = new SessionListPage
            {
                Page = page,
                PageSize = pageSize,
                TotalCount = activeCount,
                TotalPages = totalPages
            },
            HistorySessions = historyRecords
        });
    }

    private static SessionRecord ToRecord(UserSession s, Guid? currentSessionId, DateTime now, string? username, bool shared)
    {
        var isAccountHolder = s.SessionType.IsAccountHolder();
        var steamPrefillEnabled = isAccountHolder || (s.SteamPrefillExpiresAtUtc != null && s.SteamPrefillExpiresAtUtc > now);
        var epicPrefillEnabled = isAccountHolder || (s.EpicPrefillExpiresAtUtc != null && s.EpicPrefillExpiresAtUtc > now);
        var battlenetPrefillEnabled = isAccountHolder || (s.BattleNetPrefillExpiresAtUtc != null && s.BattleNetPrefillExpiresAtUtc > now);
        var riotPrefillEnabled = isAccountHolder || (s.RiotPrefillExpiresAtUtc != null && s.RiotPrefillExpiresAtUtc > now);
        var xboxPrefillEnabled = isAccountHolder || (s.XboxPrefillExpiresAtUtc != null && s.XboxPrefillExpiresAtUtc > now);

        return new SessionRecord
        {
            Id = s.Id,
            Username = username,
            AccountDeleted = s.AccountId.HasValue && username == null,
            SessionType = s.SessionType,
            IpAddress = s.IpAddress,
            UserAgent = s.UserAgent,
            CreatedAt = DateTime.SpecifyKind(s.CreatedAtUtc, DateTimeKind.Utc),
            LastSeenAt = DateTime.SpecifyKind(s.LastSeenAtUtc, DateTimeKind.Utc),
            ExpiresAt = DateTime.SpecifyKind(s.ExpiresAtUtc, DateTimeKind.Utc),
            IsRevoked = s.IsRevoked,
            IsCurrentSession = s.Id == currentSessionId,
            IsSharedSession = shared,
            IsExpired = !s.IsRevoked && s.ExpiresAtUtc <= now,
            RevokedAt = s.RevokedAtUtc.HasValue ? DateTime.SpecifyKind(s.RevokedAtUtc.Value, DateTimeKind.Utc) : (DateTime?)null,
            SignedOut = s.SignedOut,
            AccountId = s.AccountId,
            PrefillEnabled = steamPrefillEnabled || epicPrefillEnabled || battlenetPrefillEnabled || riotPrefillEnabled || xboxPrefillEnabled,
            SteamPrefillEnabled = steamPrefillEnabled,
            SteamPrefillExpiresAt = !isAccountHolder && s.SteamPrefillExpiresAtUtc > now
                ? DateTime.SpecifyKind(s.SteamPrefillExpiresAtUtc!.Value, DateTimeKind.Utc) : null,
            EpicPrefillEnabled = epicPrefillEnabled,
            EpicPrefillExpiresAt = !isAccountHolder && s.EpicPrefillExpiresAtUtc > now
                ? DateTime.SpecifyKind(s.EpicPrefillExpiresAtUtc!.Value, DateTimeKind.Utc) : null,
            BattlenetPrefillEnabled = battlenetPrefillEnabled,
            BattlenetPrefillExpiresAt = !isAccountHolder && s.BattleNetPrefillExpiresAtUtc > now
                ? DateTime.SpecifyKind(s.BattleNetPrefillExpiresAtUtc!.Value, DateTimeKind.Utc) : null,
            RiotPrefillEnabled = riotPrefillEnabled,
            RiotPrefillExpiresAt = !isAccountHolder && s.RiotPrefillExpiresAtUtc > now
                ? DateTime.SpecifyKind(s.RiotPrefillExpiresAtUtc!.Value, DateTimeKind.Utc) : null,
            XboxPrefillEnabled = xboxPrefillEnabled,
            XboxPrefillExpiresAt = !isAccountHolder && s.XboxPrefillExpiresAtUtc > now
                ? DateTime.SpecifyKind(s.XboxPrefillExpiresAtUtc!.Value, DateTimeKind.Utc) : null,
            PublicIpAddress = s.PublicIpAddress,
            CountryCode = s.CountryCode,
            CountryName = s.CountryName,
            RegionName = s.RegionName,
            City = s.City,
            Timezone = s.Timezone,
            IspName = s.IspName,
            ScreenResolution = s.ScreenResolution,
            BrowserLanguage = s.BrowserLanguage
        };
    }

    /// <summary>
    /// Revokes a session. A session belonging to the owner answers as one that does not exist, and a
    /// user-role account is refused any session its account does not hold.
    /// </summary>
    /// <remarks>
    /// It can no longer authenticate, but its row (and history) is kept, unlike
    /// <see cref="DeleteAsync"/> which erases it outright.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpPatch("{id:guid}/revoke")]
    [HandlerChecksCaller]
    [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MessageResponse>> RevokeAsync(Guid id)
    {
        var currentSession = HttpContext.GetUserSession();
        if (!await _sessionService.CallerMaySeeSessionAsync(currentSession, id))
            return NotFound(ApiResponse.NotFound("Session"));
        if (!await _sessionService.CallerMayChangeSessionAsync(currentSession, id))
            return Forbid();
        var target = await _sessionService.GetSessionByIdAsync(id);
        if (target == null)
            return NotFound(ApiResponse.NotFound("Session"));
        // Every browser runs on the shared session while sign-in is off, and the next request makes it
        // again, so ending it changes nothing anyone sees except that every browser drops its live connections.
        if (!_sessionService.IsAuthenticationEnabled() && _sessionService.IsSharedAdminSession(id))
            return Conflict(ApiResponse.Error("The shared session is used by every browser while sign-in is off and cannot be revoked or deleted"));

        using var scope = _scopeFactory.CreateScope();
        var guest = target.SessionType == SessionType.Guest;
        if (guest)
            PrefillDaemonServiceBase.GuestGate.EnterStop(id);
        var cleanup = guest
            ? PrefillDaemonServiceBase.TerminateGuestSessionsAsync(PrefillDaemonServiceBase.ResolveAllDaemons(scope.ServiceProvider),
                id, "Guest access revoked", PrefillDaemonServiceBase.AccessEndedReasonKey, currentSession?.Id.ToString())
            : Task.FromResult(new GuestPrefillStopResult(0, 0));
        GuestPrefillStopResult stopped;
        try
        {
            if (!await _sessionService.RevokeSessionAsync(id))
                return NotFound(ApiResponse.NotFound("Session"));
            // Any other session's prefill stops after the revoke, so a start still finishing for it finds the
            // session revoked and ends itself.
            if (!guest)
                cleanup = PrefillDaemonServiceBase.TerminateGuestSessionsAsync(PrefillDaemonServiceBase.ResolveAllDaemons(scope.ServiceProvider),
                    id, "Session access revoked", PrefillDaemonServiceBase.AccessEndedReasonKey, currentSession?.Id.ToString());
            if (!target.IsRevoked)
                await _signalR.NotifyAllAsync(SignalREvents.UserSessionRevoked, new
                {
                    sessionId = id.ToString(),
                    sessionType = target.SessionType.ToString().ToLowerInvariant()
                });
        }
        finally
        {
            try { stopped = await cleanup; }
            finally { if (guest) PrefillDaemonServiceBase.GuestGate.ExitStop(id); }
        }
        if (!stopped.Success)
        {
            var error = ApiResponse.Error("Session access was revoked, but prefill cleanup is incomplete. Retry this action.",
                $"{stopped.FailedSessions} sessions and {stopped.PendingStarts} starts still require cleanup.");
            error.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return StatusCode(StatusCodes.Status503ServiceUnavailable, error);
        }
        return Ok(MessageResponse.Ok("Session revoked"));
    }

    /// <summary>
    /// Permanently deletes a session's row and history. A session belonging to the owner answers
    /// as one that does not exist, and a user-role account is refused any session its account does not hold.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="RevokeAsync"/> which only blocks it from authenticating again.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpDelete("{id:guid}")]
    [HandlerChecksCaller]
    [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MessageResponse>> DeleteAsync(Guid id)
    {
        var currentSession = HttpContext.GetUserSession();
        if (!await _sessionService.CallerMaySeeSessionAsync(currentSession, id))
            return NotFound(ApiResponse.NotFound("Session"));
        if (!await _sessionService.CallerMayChangeSessionAsync(currentSession, id))
            return Forbid();
        var target = await _sessionService.GetSessionByIdAsync(id);
        if (target == null)
            return NotFound(ApiResponse.NotFound("Session"));
        // Refused for the same reason as a revoke of it.
        if (!_sessionService.IsAuthenticationEnabled() && _sessionService.IsSharedAdminSession(id))
            return Conflict(ApiResponse.Error("The shared session is used by every browser while sign-in is off and cannot be revoked or deleted"));
        var guest = target.SessionType == SessionType.Guest;
        using var scope = _scopeFactory.CreateScope();
        if (guest)
            PrefillDaemonServiceBase.GuestGate.EnterStop(id);
        var cleanup = guest
            ? PrefillDaemonServiceBase.TerminateGuestSessionsAsync(PrefillDaemonServiceBase.ResolveAllDaemons(scope.ServiceProvider),
                id, "Guest session deleted", PrefillDaemonServiceBase.AccessEndedReasonKey, currentSession?.Id.ToString())
            : Task.FromResult(new GuestPrefillStopResult(0, 0));
        try
        {
            if (!await _sessionService.RevokeSessionAsync(id))
                return NotFound(ApiResponse.NotFound("Session"));
            // After the revoke, so a start still finishing for the session finds it revoked and ends itself.
            if (!guest)
                cleanup = PrefillDaemonServiceBase.TerminateGuestSessionsAsync(PrefillDaemonServiceBase.ResolveAllDaemons(scope.ServiceProvider),
                    id, "Session deleted", PrefillDaemonServiceBase.AccessEndedReasonKey, currentSession?.Id.ToString());
            if (!target.IsRevoked)
                await _signalR.NotifyAllAsync(SignalREvents.UserSessionRevoked, new
                {
                    sessionId = id.ToString(),
                    sessionType = target.SessionType.ToString().ToLowerInvariant()
                });
            var stopped = await cleanup;
            if (!stopped.Success)
            {
                var error = ApiResponse.Error("Session access was revoked, but deletion is waiting for prefill cleanup. Retry this action.",
                    $"{stopped.FailedSessions} sessions and {stopped.PendingStarts} starts still require cleanup.");
                error.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return StatusCode(StatusCodes.Status503ServiceUnavailable, error);
            }
            if (!await _sessionService.DeleteSessionAsync(id))
                return NotFound(ApiResponse.NotFound("Session"));
            await _signalR.NotifyAllAsync(SignalREvents.UserSessionDeleted, new
            {
                sessionId = id.ToString(),
                sessionType = target.SessionType.ToString().ToLowerInvariant()
            });
            return Ok(MessageResponse.Ok("Session permanently deleted"));
        }
        finally
        {
            try { await cleanup; }
            finally { if (guest) PrefillDaemonServiceBase.GuestGate.ExitStop(id); }
        }
    }

    /// <summary>
    /// Updates the caller's own dashboard refresh rate preference.
    /// </summary>
    /// <remarks>
    /// The owning session may always change its own rate; an admin may change anyone's, and a user-role
    /// account only the sessions its own account holds.
    /// A caller that is not an admin is blocked while the session is locked: by its own lock, else for a
    /// guest by the lock for every guest. A user-role account's unset lock leaves its rate free.
    /// </remarks>
    [HttpPatch("{id:guid}/refresh-rate")]
    [ProducesResponseType(typeof(StateUpdateResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<StateUpdateResponse>> UpdateRefreshRateAsync(Guid id, [FromBody] RefreshRateRequest request)
    {
        var callerSession = HttpContext.GetUserSession();
        var isAccountHolder = callerSession?.SessionType.IsAccountHolder() == true;

        // Only the owning session or an account holder may update refresh rate
        if (!isAccountHolder && callerSession?.Id != id)
            return Forbid();

        if (callerSession?.Id != id && !await _sessionService.CallerMaySeeSessionAsync(callerSession, id))
            return NotFound(ApiResponse.NotFound("Session"));

        if (callerSession?.Id != id && !await _sessionService.CallerMayChangeSessionAsync(callerSession, id))
            return Forbid();

        using var scope = _scopeFactory.CreateScope();
        var prefsService = scope.ServiceProvider.GetRequiredService<UserPreferencesService>();

        // A guest or a user-role account cannot change a rate while it is locked for that session. Such a
        // caller reaches only its own session or its own account's, so its type is the session's type.
        if (callerSession!.SessionType != SessionType.Admin
            && prefsService.IsRefreshRateLocked(
                id, callerSession.SessionType == SessionType.Guest && _stateService.GetGuestRefreshRateLocked()))
            throw new ForbiddenException("Refresh rate changes are locked by the administrator");

        var refreshRateJson = JsonSerializer.SerializeToElement(request.RefreshRate ?? "");
        var result = await prefsService.UpdatePreferenceAsync(id, PreferenceKey.RefreshRate, refreshRateJson);
        if (result == null)
        {
            return NotFound(ApiResponse.Error("Session not found or update failed"));
        }

        // The stored rate, not the request text: the store reads the text case-insensitively and keeps
        // null for an empty or unknown one, and the session's window applies exactly what is stored.
        await _signalR.NotifyAllAsync(SignalREvents.GuestRefreshRateUpdated, new
        {
            sessionId = id.ToString(),
            refreshRate = result.RefreshRate
        });

        return Ok(new StateUpdateResponse { Success = true });
    }

    /// <summary>
    /// Deletes every guest session's stored preferences, resetting them to defaults.
    /// </summary>
    /// <remarks>
    /// Admin sessions are untouched. A user-role account is refused: it changes no session but its own.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpPost("bulk/reset-to-defaults")]
    [ProducesResponseType(typeof(SessionResetResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SessionResetResponse>> ResetToDefaultsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var prefsService = scope.ServiceProvider.GetRequiredService<UserPreferencesService>();

        // Get all guest session IDs and delete their preferences
        var guestSessions = await _sessionService.GetActiveSessionsAsync();
        var guestSessionIds = guestSessions
            .Where(s => s.SessionType == SessionType.Guest)
            .Select(s => s.Id)
            .ToList();

        var affectedCount = 0;
        foreach (var sessionId in guestSessionIds)
        {
            if (await prefsService.DeletePreferencesAsync(sessionId))
            {
                affectedCount++;
            }
        }

        if (affectedCount > 0)
        {
            await _signalR.NotifyAllAsync(SignalREvents.UserPreferencesReset, new
            {
                affectedCount,
                sessionType = "guest"
            });
        }

        return Ok(new SessionResetResponse { Success = true, AffectedCount = affectedCount });
    }

    /// <summary>
    /// Revokes every active guest session.
    /// </summary>
    /// <remarks>
    /// Frees up their slots and forces a fresh session on next visit. Admin sessions are
    /// untouched. A user-role account is refused: it changes no session but its own.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpDelete("bulk/clear-guests")]
    [ProducesResponseType(typeof(SessionClearGuestsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SessionClearGuestsResponse>> ClearGuestsAsync()
    {
        var pendingOwners = PrefillDaemonServiceBase.GuestGate.GetPendingOwners();
        var active = await _sessionService.GetActiveSessionsAsync();
        var targets = active.Where(session => session.SessionType == SessionType.Guest).ToDictionary(session => session.Id);
        foreach (var id in pendingOwners)
        {
            var session = await _sessionService.GetSessionByIdAsync(id);
            if (session?.SessionType == SessionType.Guest)
                targets.TryAdd(id, session);
        }
        using var scope = _scopeFactory.CreateScope();
        var daemons = PrefillDaemonServiceBase.ResolveAllDaemons(scope.ServiceProvider).ToArray();
        foreach (var id in targets.Keys)
            PrefillDaemonServiceBase.GuestGate.EnterStop(id);
        var cleanups = targets.Keys.Select(id => PrefillDaemonServiceBase.TerminateGuestSessionsAsync(
            daemons, id, "Guest sessions cleared", PrefillDaemonServiceBase.AccessEndedReasonKey, HttpContext.GetUserSession()?.Id.ToString())).ToArray();
        var ended = new List<string>();
        var failures = new List<Exception>();
        GuestPrefillStopResult[] stopped;
        try
        {
            foreach (var target in targets.Values)
            {
                try
                {
                    if (await _sessionService.RevokeSessionAsync(target.Id) && !target.IsRevoked)
                        ended.Add(target.Id.ToString());
                }
                catch (Exception ex) { failures.Add(ex); }
            }
            // A guest can sign in while this runs; naming the ended sessions keeps that window signed in.
            if (ended.Count != 0)
                await _signalR.NotifyAllAsync(SignalREvents.UserSessionsCleared, new { clearedCount = ended.Count, sessionType = "guest", sessionIds = ended });
        }
        finally
        {
            try { stopped = await Task.WhenAll(cleanups); }
            finally
            {
                foreach (var id in targets.Keys)
                    PrefillDaemonServiceBase.GuestGate.ExitStop(id);
            }
        }
        if (failures.Count != 0)
            throw new AggregateException("Some guest sessions could not be revoked.", failures);
        if (stopped.Any(result => !result.Success))
        {
            var error = ApiResponse.Error("Guest access was revoked, but prefill cleanup is incomplete. Retry this action.",
                $"{ended.Count} sessions revoked; {stopped.Sum(result => result.FailedSessions)} sessions and {stopped.Sum(result => result.PendingStarts)} starts still require cleanup.");
            error.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return StatusCode(StatusCodes.Status503ServiceUnavailable, error);
        }
        return Ok(new SessionClearGuestsResponse { Success = true, ClearedCount = ended.Count });
    }

    /// <summary>
    /// Accepts browser-reported client metadata for the caller's own session.
    /// </summary>
    /// <remarks>
    /// Navigator-derived locale/screen fields, plus a public IP resolved server-side from the
    /// connection's remote address, falling back to PublicIpLookupService when the caller is on
    /// the LAN. Performs a cached GeoIP lookup on the public IP and writes the resolved
    /// country/city/ISP back onto the session.
    ///
    /// Any session type (admin or guest) may write its own client info.
    /// </remarks>
    [HttpPost("me/client-info")]
    [ProducesResponseType(typeof(SessionClientInfoResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SessionClientInfoResponse>> UpdateClientInfoAsync(
        [FromBody] ClientInfoRequest request,
        [FromServices] GeoIpService geoIpService,
        [FromServices] PublicIpLookupService publicIpLookupService,
        CancellationToken ct = default)
    {
        var session = HttpContext.GetUserSession();
        if (session == null)
        {
            return Unauthorized();
        }

        // The address this request arrived on. When the caller is remote it IS their public IP, and
        // it is better evidence than anything the server can look up about itself. When they are on
        // the LAN it is a private address, which tells us nothing about the public side.
        string? publicIp = null;
        if (HttpContext.Connection.RemoteIpAddress is { } remote
            && PublicAddressSafety.IsPublic(remote))
        {
            publicIp = (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString();
        }

        // Caller is on the LAN, so fall back to the server's own public IP. In a typical lancache
        // deployment the server shares the LAN with the client, so the two match.
        if (publicIp == null)
        {
            publicIp = await publicIpLookupService.ResolveAsync(ct);
        }

        GeoIpLookup? geo = null;
        if (publicIp != null)
        {
            geo = await geoIpService.LookupAsync(publicIp, ct);
        }

        // Browser-reported timezone wins over GeoIP timezone when both are
        // present - the browser value is authoritative for the user's device.
        var timezone = !string.IsNullOrWhiteSpace(request.Timezone)
            ? request.Timezone.Trim()
            : geo?.Timezone;

        await _sessionService.UpdateClientInfoAsync(
            sessionId: session.Id,
            publicIpAddress: publicIp,
            countryCode: geo?.CountryCode,
            countryName: geo?.CountryName,
            regionName: geo?.RegionName,
            city: geo?.City,
            timezone: Truncate(timezone, 64),
            ispName: geo?.IspName,
            screenResolution: Truncate(request.ScreenResolution, 32),
            browserLanguage: Truncate(request.Language, 16));

        return Ok(new SessionClientInfoResponse
        {
            Success = true,
            PublicIp = publicIp,
            CountryCode = geo?.CountryCode,
            Country = geo?.CountryName,
            Region = geo?.RegionName,
            City = geo?.City,
            Timezone = timezone,
            Isp = geo?.IspName
        });
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
