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

    public SessionsController(
        SessionService sessionService,
        ILogger<SessionsController> logger)
    {
        _sessionService = sessionService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetAllSessions()
    {
        var sessions = await _sessionService.GetActiveSessionsAsync();

        var dtos = sessions.Select(s => new SessionDto
        {
            Id = s.Id.ToString(),
            SessionType = s.SessionType,
            IpAddress = s.IpAddress,
            UserAgent = s.UserAgent,
            CreatedAt = s.CreatedAtUtc,
            LastSeenAt = s.LastSeenAtUtc,
            ExpiresAt = s.ExpiresAtUtc,
            IsRevoked = s.IsRevoked
        }).ToList();

        return Ok(new
        {
            sessions = dtos,
            count = dtos.Count,
            adminCount = dtos.Count(s => s.SessionType == "admin"),
            guestCount = dtos.Count(s => s.SessionType == "guest")
        });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> RevokeSession(Guid id)
    {
        // Don't allow revoking own session via this endpoint
        var currentSession = HttpContext.GetUserSession();
        if (currentSession != null && currentSession.Id == id)
        {
            return BadRequest(new { error = "Cannot revoke your own session. Use /api/auth/logout instead." });
        }

        var success = await _sessionService.RevokeSessionAsync(id);
        if (!success)
        {
            return NotFound(new { error = "Session not found" });
        }

        return Ok(new { success = true, message = "Session revoked" });
    }

    [HttpDelete("guests")]
    public async Task<IActionResult> RevokeAllGuests()
    {
        var count = await _sessionService.RevokeAllGuestSessionsAsync();

        return Ok(new { success = true, revokedCount = count, message = $"Revoked {count} guest sessions" });
    }
}
