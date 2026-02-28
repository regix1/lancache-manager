using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/sessions")]
public class SessionsController : ControllerBase
{
    private readonly SessionService _sessionService;
    private readonly ILogger<SessionsController> _logger;
    private readonly ISignalRNotificationService _signalR;
    private readonly IServiceScopeFactory _scopeFactory;

    public SessionsController(
        SessionService sessionService,
        ILogger<SessionsController> logger,
        ISignalRNotificationService signalR,
        IServiceScopeFactory scopeFactory)
    {
        _sessionService = sessionService;
        _logger = logger;
        _signalR = signalR;
        _scopeFactory = scopeFactory;
    }

    [HttpGet]
    public async Task<IActionResult> GetAllSessions([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var (sessions, totalCount) = await _sessionService.GetAllSessionsPagedAsync(page, pageSize);
        var currentSessionId = HttpContext.GetUserSession()?.Id;
        var now = DateTime.UtcNow;

        var dtos = sessions.Select(s => MapSessionToDto(s, currentSessionId, now)).ToList();

        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

        return Ok(new
        {
            sessions = dtos,
            count = dtos.Count,
            adminCount = dtos.Count(s => s.SessionType == "admin"),
            guestCount = dtos.Count(s => s.SessionType == "guest"),
            pagination = new
            {
                page,
                pageSize,
                totalCount,
                totalPages
            }
        });
    }

    private static SessionDto MapSessionToDto(UserSession s, Guid? currentSessionId, DateTime now)
    {
        var isAdmin = s.SessionType == "admin";
        var steamPrefillEnabled = isAdmin || (s.SteamPrefillExpiresAtUtc != null && s.SteamPrefillExpiresAtUtc > now);
        var epicPrefillEnabled = isAdmin || (s.EpicPrefillExpiresAtUtc != null && s.EpicPrefillExpiresAtUtc > now);

        return new SessionDto
        {
            Id = s.Id.ToString(),
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

    [HttpPatch("{id:guid}/revoke")]
    public async Task<IActionResult> RevokeSession(Guid id)
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
            sessionType = currentSession != null && currentSession.Id == id ? currentSession.SessionType : "unknown"
        });

        return Ok(new { success = true, message = "Session revoked" });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteSession(Guid id)
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
            sessionType = currentSession != null && currentSession.Id == id ? currentSession.SessionType : "unknown"
        });

        return Ok(new { success = true, message = "Session permanently deleted" });
    }

    [HttpDelete("guests")]
    public async Task<IActionResult> RevokeAllGuests()
    {
        var count = await _sessionService.RevokeAllGuestSessionsAsync();

        // Broadcast sessions cleared
        await _signalR.NotifyAllAsync(SignalREvents.UserSessionsCleared, new
        {
            clearedCount = count,
            sessionType = "guest"
        });

        return Ok(new { success = true, revokedCount = count, message = $"Revoked {count} guest sessions" });
    }

    [HttpPatch("{id:guid}/refresh-rate")]
    public async Task<IActionResult> UpdateRefreshRate(Guid id, [FromBody] RefreshRateRequest request)
    {
        using var scope = _scopeFactory.CreateScope();
        var prefsService = scope.ServiceProvider.GetRequiredService<UserPreferencesService>();

        var result = prefsService.UpdatePreferenceAndGet(id, "refreshRate", request.RefreshRate ?? "");
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

    [HttpPost("bulk/reset-to-defaults")]
    public async Task<IActionResult> BulkResetToDefaults()
    {
        using var scope = _scopeFactory.CreateScope();
        var prefsService = scope.ServiceProvider.GetRequiredService<UserPreferencesService>();

        // Get all guest session IDs and delete their preferences
        var guestSessions = await _sessionService.GetActiveSessionsAsync();
        var guestSessionIds = guestSessions
            .Where(s => s.SessionType == "guest")
            .Select(s => s.Id)
            .ToList();

        var affectedCount = 0;
        foreach (var sessionId in guestSessionIds)
        {
            if (prefsService.DeletePreferences(sessionId))
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

    [HttpDelete("bulk/clear-guests")]
    public async Task<IActionResult> BulkClearGuests()
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
