using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
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
    private readonly ILogger<SessionsController> _logger;
    private readonly ISignalRNotificationService _signalR;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StateService _stateService;

    public SessionsController(
        SessionService sessionService,
        ILogger<SessionsController> logger,
        ISignalRNotificationService signalR,
        IServiceScopeFactory scopeFactory,
        StateService stateService)
    {
        _sessionService = sessionService;
        _logger = logger;
        _signalR = signalR;
        _scopeFactory = scopeFactory;
        _stateService = stateService;
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpGet]
    public async Task<IActionResult> GetAllSessionsAsync([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var currentSessionId = HttpContext.GetUserSession()?.Id;
        var now = DateTime.UtcNow;

        // Active sessions (paginated)
        var (activeSessions, activeCount) = await _sessionService.GetActiveSessionsPagedAsync(page, pageSize);
        var activeDtos = activeSessions.Select(s => MapSessionToDto(s, currentSessionId, now)).ToList();
        var totalPages = (int)Math.Ceiling((double)activeCount / pageSize);

        // History sessions (revoked/expired) - unpaginated
        var historySessions = await _sessionService.GetHistorySessionsAsync();
        var historyDtos = historySessions.Select(s => MapSessionToDto(s, currentSessionId, now)).ToList();

        return Ok(new
        {
            sessions = activeDtos,
            count = activeDtos.Count,
            adminCount = activeDtos.Count(s => s.SessionType == SessionType.Admin),
            guestCount = activeDtos.Count(s => s.SessionType == SessionType.Guest),
            pagination = new
            {
                page,
                pageSize,
                totalCount = activeCount,
                totalPages
            },
            historySessions = historyDtos
        });
    }

    private static SessionDto MapSessionToDto(UserSession s, Guid? currentSessionId, DateTime now)
    {
        var isAdmin = s.SessionType == SessionType.Admin;
        var steamPrefillEnabled = isAdmin || (s.SteamPrefillExpiresAtUtc != null && s.SteamPrefillExpiresAtUtc > now);
        var epicPrefillEnabled = isAdmin || (s.EpicPrefillExpiresAtUtc != null && s.EpicPrefillExpiresAtUtc > now);

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
            PrefillEnabled = steamPrefillEnabled || epicPrefillEnabled,
            SteamPrefillEnabled = steamPrefillEnabled,
            SteamPrefillExpiresAt = !isAdmin && s.SteamPrefillExpiresAtUtc > now
                ? DateTime.SpecifyKind(s.SteamPrefillExpiresAtUtc!.Value, DateTimeKind.Utc) : null,
            EpicPrefillEnabled = epicPrefillEnabled,
            EpicPrefillExpiresAt = !isAdmin && s.EpicPrefillExpiresAtUtc > now
                ? DateTime.SpecifyKind(s.EpicPrefillExpiresAtUtc!.Value, DateTimeKind.Utc) : null
        };
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPatch("{id:guid}/revoke")]
    public async Task<IActionResult> RevokeSessionAsync(Guid id)
    {
        var currentSession = HttpContext.GetUserSession();

        var success = await _sessionService.RevokeSessionAsync(id);
        if (!success)
        {
            return NotFound(new { error = "Session not found" });
        }

        // Broadcast session revoked
        await _signalR.NotifyAllAsync(SignalREvents.UserSessionRevoked, new
        {
            sessionId = id.ToString(),
            sessionType = currentSession != null && currentSession.Id == id ? currentSession.SessionType.ToString().ToLowerInvariant() : "unknown"
        });

        return Ok(new { success = true, message = "Session revoked" });
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteSessionAsync(Guid id)
    {
        var currentSession = HttpContext.GetUserSession();

        var success = await _sessionService.DeleteSessionAsync(id);
        if (!success)
        {
            return NotFound(new { error = "Session not found" });
        }

        // Broadcast session deleted (permanently removed)
        await _signalR.NotifyAllAsync(SignalREvents.UserSessionDeleted, new
        {
            sessionId = id.ToString(),
            sessionType = currentSession != null && currentSession.Id == id ? currentSession.SessionType.ToString().ToLowerInvariant() : "unknown"
        });

        return Ok(new { success = true, message = "Session permanently deleted" });
    }

    [HttpPatch("{id:guid}/refresh-rate")]
    public async Task<IActionResult> UpdateRefreshRateAsync(Guid id, [FromBody] RefreshRateRequest request)
    {
        var callerSession = HttpContext.GetUserSession();
        var isAdmin = callerSession?.SessionType == SessionType.Admin;

        // Only the owning session or an admin may update refresh rate
        if (!isAdmin && callerSession?.Id != id)
            return Forbid();

        // Guests cannot change their refresh rate when the global lock is active
        if (!isAdmin && _stateService.GetGuestRefreshRateLocked())
            return StatusCode(403, new { error = "Refresh rate changes are locked by the administrator" });

        using var scope = _scopeFactory.CreateScope();
        var prefsService = scope.ServiceProvider.GetRequiredService<UserPreferencesService>();

        var refreshRateJson = JsonSerializer.SerializeToElement(request.RefreshRate ?? "");
        var result = await prefsService.UpdatePreferenceAndGetAsync(id, PreferenceKey.RefreshRate, refreshRateJson);
        if (result == null)
        {
            return NotFound(new { error = "Session not found or update failed" });
        }

        await _signalR.NotifyAllAsync(SignalREvents.GuestRefreshRateUpdated, new
        {
            sessionId = id.ToString(),
            refreshRate = request.RefreshRate
        });

        return Ok(new { success = true });
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPost("bulk/reset-to-defaults")]
    public async Task<IActionResult> BulkResetToDefaultsAsync()
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

        return Ok(new { success = true, affectedCount });
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpDelete("bulk/clear-guests")]
    public async Task<IActionResult> BulkClearGuestsAsync()
    {
        var count = await _sessionService.RevokeAllGuestSessionsAsync();

        await _signalR.NotifyAllAsync(SignalREvents.UserSessionsCleared, new
        {
            clearedCount = count,
            sessionType = "guest"
        });

        return Ok(new { success = true, clearedCount = count });
    }
}

public class RefreshRateRequest
{
    public string? RefreshRate { get; set; }
}
