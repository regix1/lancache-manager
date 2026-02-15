using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for session management.
/// Auth stripped — all endpoints return empty/success results.
/// </summary>
[ApiController]
[Route("api/sessions")]
public class SessionsController : ControllerBase
{
    private readonly ILogger<SessionsController> _logger;

    public SessionsController(ILogger<SessionsController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// GET /api/sessions - Returns empty session list.
    /// </summary>
    [HttpGet]
    public IActionResult GetAllSessions([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        return Ok(new PaginatedSessionsResponse
        {
            Sessions = new List<SessionDto>(),
            Pagination = new PaginationInfo
            {
                Page = page,
                PageSize = pageSize,
                TotalCount = 0,
                TotalPages = 0,
                HasNextPage = false,
                HasPreviousPage = false
            },
            Count = 0,
            AuthenticatedCount = 0,
            GuestCount = 0
        });
    }

    /// <summary>
    /// GET /api/sessions/{id} - Returns not found.
    /// </summary>
    [HttpGet("{id}")]
    public IActionResult GetSession(string id)
    {
        return NotFound(new ErrorResponse { Error = "Session not found" });
    }

    /// <summary>
    /// POST /api/sessions - Returns success.
    /// </summary>
    [HttpPost]
    [EnableRateLimiting("auth")]
    public IActionResult CreateSession([FromQuery] string? type, [FromBody] CreateSessionRequest request)
    {
        return Created($"/api/sessions/{request.DeviceId}", new SessionCreateResponse
        {
            Success = true,
            DeviceId = request.DeviceId,
            ExpiresAt = DateTime.UtcNow.AddHours(24),
            Message = "Session created successfully"
        });
    }

    /// <summary>
    /// PATCH /api/sessions/current/last-seen - Heartbeat no-op.
    /// </summary>
    [HttpPatch("current/last-seen")]
    public IActionResult UpdateCurrentLastSeen()
    {
        return Ok(new SessionHeartbeatResponse { Success = true, Type = "authenticated" });
    }

    /// <summary>
    /// DELETE /api/sessions/current - Logout no-op.
    /// </summary>
    [HttpDelete("current")]
    public IActionResult DeleteCurrentSession()
    {
        return Ok(new SessionDeleteResponse { Success = true, Message = "Session cleared successfully" });
    }

    /// <summary>
    /// DELETE /api/sessions/{id} - Delete session no-op.
    /// </summary>
    [HttpDelete("{id}")]
    public IActionResult DeleteSession(string id, [FromQuery] string action = "delete")
    {
        return Ok(new SessionDeleteResponse { Success = true, Message = "Session deleted successfully" });
    }

    /// <summary>
    /// PATCH /api/sessions/{id}/refresh-rate - No-op.
    /// </summary>
    [HttpPatch("{id}/refresh-rate")]
    public IActionResult SetSessionRefreshRate(string id, [FromBody] SetSessionRefreshRateRequest request)
    {
        return Ok(new SetSessionRefreshRateResponse
        {
            Success = true,
            Message = "Refresh rate updated",
            RefreshRate = request.RefreshRate ?? "STANDARD"
        });
    }

    /// <summary>
    /// POST /api/sessions/bulk/reset-to-defaults - No-op.
    /// </summary>
    [HttpPost("bulk/reset-to-defaults")]
    public IActionResult BulkResetToDefaults()
    {
        return Ok(new { affectedCount = 0, message = "Reset 0 guest sessions to defaults" });
    }

    /// <summary>
    /// DELETE /api/sessions/bulk/clear-guests - No-op.
    /// </summary>
    [HttpDelete("bulk/clear-guests")]
    public IActionResult BulkClearGuests()
    {
        return Ok(new { clearedCount = 0, message = "Cleared 0 guest sessions" });
    }
}
