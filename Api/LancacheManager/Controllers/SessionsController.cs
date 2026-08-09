using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.StatusCheck;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    /// Lists sessions for the admin management screen.
    /// </summary>
    /// <remarks>
    /// Active sessions (paginated), plus the full revoked/expired history unpaginated since it is
    /// only ever shown as a flat audit list.
    /// </remarks>
    [Authorize(Policy = "AdminOnly")]
    [HttpGet]
    [ProducesResponseType(typeof(SessionListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SessionListResponse>> GetAllAsync([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var currentSessionId = HttpContext.GetUserSession()?.Id;
        var now = DateTime.UtcNow;

        // Active sessions (paginated)
        var (activeSessions, activeCount) = await _sessionService.GetActiveSessionsPagedAsync(page, pageSize);
        var activeDtos = activeSessions.Select(s => ToDto(s, currentSessionId, now)).ToList();
        var totalPages = (int)Math.Ceiling((double)activeCount / pageSize);

        // History sessions (revoked/expired) - unpaginated
        var historySessions = await _sessionService.GetSessionHistoryAsync();
        var historyDtos = historySessions.Select(s => ToDto(s, currentSessionId, now)).ToList();

        return Ok(new SessionListResponse
        {
            Sessions = activeDtos,
            Count = activeDtos.Count,
            AdminCount = activeDtos.Count(s => s.SessionType == SessionType.Admin),
            GuestCount = activeDtos.Count(s => s.SessionType == SessionType.Guest),
            Pagination = new SessionListPage
            {
                Page = page,
                PageSize = pageSize,
                TotalCount = activeCount,
                TotalPages = totalPages
            },
            HistorySessions = historyDtos
        });
    }

    private static SessionDto ToDto(UserSession s, Guid? currentSessionId, DateTime now)
    {
        var isAdmin = s.SessionType == SessionType.Admin;
        var steamPrefillEnabled = isAdmin || (s.SteamPrefillExpiresAtUtc != null && s.SteamPrefillExpiresAtUtc > now);
        var epicPrefillEnabled = isAdmin || (s.EpicPrefillExpiresAtUtc != null && s.EpicPrefillExpiresAtUtc > now);
        var battlenetPrefillEnabled = isAdmin || (s.BattleNetPrefillExpiresAtUtc != null && s.BattleNetPrefillExpiresAtUtc > now);
        var riotPrefillEnabled = isAdmin || (s.RiotPrefillExpiresAtUtc != null && s.RiotPrefillExpiresAtUtc > now);
        var xboxPrefillEnabled = isAdmin || (s.XboxPrefillExpiresAtUtc != null && s.XboxPrefillExpiresAtUtc > now);

        return new SessionDto
        {
            Id = s.Id,
            SessionType = s.SessionType,
            IpAddress = s.IpAddress,
            UserAgent = s.UserAgent,
            CreatedAt = DateTime.SpecifyKind(s.CreatedAtUtc, DateTimeKind.Utc),
            LastSeenAt = DateTime.SpecifyKind(s.LastSeenAtUtc, DateTimeKind.Utc),
            ExpiresAt = DateTime.SpecifyKind(s.ExpiresAtUtc, DateTimeKind.Utc),
            IsRevoked = s.IsRevoked,
            IsCurrentSession = s.Id == currentSessionId,
            IsExpired = !s.IsRevoked && s.ExpiresAtUtc <= now,
            RevokedAt = s.RevokedAtUtc.HasValue ? DateTime.SpecifyKind(s.RevokedAtUtc.Value, DateTimeKind.Utc) : (DateTime?)null,
            PrefillEnabled = steamPrefillEnabled || epicPrefillEnabled || battlenetPrefillEnabled || riotPrefillEnabled || xboxPrefillEnabled,
            SteamPrefillEnabled = steamPrefillEnabled,
            SteamPrefillExpiresAt = !isAdmin && s.SteamPrefillExpiresAtUtc > now
                ? DateTime.SpecifyKind(s.SteamPrefillExpiresAtUtc!.Value, DateTimeKind.Utc) : null,
            EpicPrefillEnabled = epicPrefillEnabled,
            EpicPrefillExpiresAt = !isAdmin && s.EpicPrefillExpiresAtUtc > now
                ? DateTime.SpecifyKind(s.EpicPrefillExpiresAtUtc!.Value, DateTimeKind.Utc) : null,
            BattlenetPrefillEnabled = battlenetPrefillEnabled,
            BattlenetPrefillExpiresAt = !isAdmin && s.BattleNetPrefillExpiresAtUtc > now
                ? DateTime.SpecifyKind(s.BattleNetPrefillExpiresAtUtc!.Value, DateTimeKind.Utc) : null,
            RiotPrefillEnabled = riotPrefillEnabled,
            RiotPrefillExpiresAt = !isAdmin && s.RiotPrefillExpiresAtUtc > now
                ? DateTime.SpecifyKind(s.RiotPrefillExpiresAtUtc!.Value, DateTimeKind.Utc) : null,
            XboxPrefillEnabled = xboxPrefillEnabled,
            XboxPrefillExpiresAt = !isAdmin && s.XboxPrefillExpiresAtUtc > now
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
    /// Revokes a session.
    /// </summary>
    /// <remarks>
    /// It can no longer authenticate, but its row (and history) is kept, unlike
    /// <see cref="DeleteAsync"/> which erases it outright.
    /// </remarks>
    [Authorize(Policy = "AdminOnly")]
    [HttpPatch("{id:guid}/revoke")]
    [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MessageResponse>> RevokeAsync(Guid id)
    {
        var currentSession = HttpContext.GetUserSession();

        var success = await _sessionService.RevokeSessionAsync(id);
        if (!success)
        {
            return NotFound(ApiResponse.NotFound("Session"));
        }

        // Broadcast session revoked
        await _signalR.NotifyAllAsync(SignalREvents.UserSessionRevoked, new
        {
            sessionId = id.ToString(),
            sessionType = currentSession != null && currentSession.Id == id ? currentSession.SessionType.ToString().ToLowerInvariant() : "unknown"
        });

        return Ok(MessageResponse.Ok("Session revoked"));
    }

    /// <summary>
    /// Permanently deletes a session's row and history.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="RevokeAsync"/> which only blocks it from authenticating again.
    /// </remarks>
    [Authorize(Policy = "AdminOnly")]
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MessageResponse>> DeleteAsync(Guid id)
    {
        var currentSession = HttpContext.GetUserSession();

        var success = await _sessionService.DeleteSessionAsync(id);
        if (!success)
        {
            return NotFound(ApiResponse.NotFound("Session"));
        }

        // Broadcast session deleted (permanently removed)
        await _signalR.NotifyAllAsync(SignalREvents.UserSessionDeleted, new
        {
            sessionId = id.ToString(),
            sessionType = currentSession != null && currentSession.Id == id ? currentSession.SessionType.ToString().ToLowerInvariant() : "unknown"
        });

        return Ok(MessageResponse.Ok("Session permanently deleted"));
    }

    /// <summary>
    /// Updates the caller's own dashboard refresh rate preference.
    /// </summary>
    /// <remarks>
    /// The owning session may always change its own rate; an admin may change anyone's. Guests
    /// are blocked while the admin has locked the global guest refresh rate.
    /// </remarks>
    [HttpPatch("{id:guid}/refresh-rate")]
    [ProducesResponseType(typeof(StateUpdateResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<StateUpdateResponse>> UpdateRefreshRateAsync(Guid id, [FromBody] RefreshRateRequest request)
    {
        var callerSession = HttpContext.GetUserSession();
        var isAdmin = callerSession?.SessionType == SessionType.Admin;

        // Only the owning session or an admin may update refresh rate
        if (!isAdmin && callerSession?.Id != id)
            return Forbid();

        // Guests cannot change their refresh rate when the global lock is active
        if (!isAdmin && _stateService.GetGuestRefreshRateLocked())
            throw new ForbiddenException("Refresh rate changes are locked by the administrator");

        using var scope = _scopeFactory.CreateScope();
        var prefsService = scope.ServiceProvider.GetRequiredService<UserPreferencesService>();

        var refreshRateJson = JsonSerializer.SerializeToElement(request.RefreshRate ?? "");
        var result = await prefsService.UpdatePreferenceAsync(id, PreferenceKey.RefreshRate, refreshRateJson);
        if (result == null)
        {
            return NotFound(ApiResponse.Error("Session not found or update failed"));
        }

        await _signalR.NotifyAllAsync(SignalREvents.GuestRefreshRateUpdated, new
        {
            sessionId = id.ToString(),
            refreshRate = request.RefreshRate
        });

        return Ok(new StateUpdateResponse { Success = true });
    }

    /// <summary>
    /// Deletes every guest session's stored preferences, resetting them to defaults.
    /// </summary>
    /// <remarks>
    /// Admin sessions are untouched.
    /// </remarks>
    [Authorize(Policy = "AdminOnly")]
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
    /// untouched.
    /// </remarks>
    [Authorize(Policy = "AdminOnly")]
    [HttpDelete("bulk/clear-guests")]
    [ProducesResponseType(typeof(SessionClearGuestsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SessionClearGuestsResponse>> ClearGuestsAsync()
    {
        var count = await _sessionService.RevokeAllGuestSessionsAsync();

        await _signalR.NotifyAllAsync(SignalREvents.UserSessionsCleared, new
        {
            clearedCount = count,
            sessionType = "guest"
        });

        return Ok(new SessionClearGuestsResponse { Success = true, ClearedCount = count });
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

public class RefreshRateRequest
{
    public string? RefreshRate { get; set; }
}

public class ClientInfoRequest
{
    public string? Timezone { get; set; }
    public string? Language { get; set; }
    public string? ScreenResolution { get; set; }
}
