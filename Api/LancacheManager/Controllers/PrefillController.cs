using LancacheManager.Application.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// REST API endpoints for Steam Prefill session management
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class PrefillController : ControllerBase
{
    private readonly PrefillSessionService _sessionService;
    private readonly DeviceAuthService _deviceAuthService;
    private readonly ILogger<PrefillController> _logger;

    public PrefillController(
        PrefillSessionService sessionService,
        DeviceAuthService deviceAuthService,
        ILogger<PrefillController> logger)
    {
        _sessionService = sessionService;
        _deviceAuthService = deviceAuthService;
        _logger = logger;
    }

    /// <summary>
    /// Gets all active prefill sessions (admin only)
    /// </summary>
    [HttpGet("sessions")]
    public ActionResult<IEnumerable<PrefillSessionDto>> GetAllSessions()
    {
        // Check if user is authenticated
        if (!IsAuthenticated())
        {
            return Unauthorized();
        }

        var sessions = _sessionService.GetAllSessions()
            .Select(PrefillSessionDto.FromSession);

        return Ok(sessions);
    }

    /// <summary>
    /// Gets sessions for the current user
    /// </summary>
    [HttpGet("sessions/mine")]
    public ActionResult<IEnumerable<PrefillSessionDto>> GetMySessions()
    {
        var deviceId = GetDeviceId();
        if (string.IsNullOrEmpty(deviceId))
        {
            return Unauthorized();
        }

        var sessions = _sessionService.GetUserSessions(deviceId)
            .Select(PrefillSessionDto.FromSession);

        return Ok(sessions);
    }

    /// <summary>
    /// Gets a specific session
    /// </summary>
    [HttpGet("sessions/{sessionId}")]
    public ActionResult<PrefillSessionDto> GetSession(string sessionId)
    {
        var deviceId = GetDeviceId();
        if (string.IsNullOrEmpty(deviceId))
        {
            return Unauthorized();
        }

        var session = _sessionService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound();
        }

        // Only owner can view their session details
        if (session.UserId != deviceId)
        {
            return Forbid();
        }

        return Ok(PrefillSessionDto.FromSession(session));
    }

    /// <summary>
    /// Creates a new prefill session
    /// </summary>
    [HttpPost("sessions")]
    public async Task<ActionResult<PrefillSessionDto>> CreateSession()
    {
        var deviceId = GetDeviceId();
        if (string.IsNullOrEmpty(deviceId))
        {
            return Unauthorized();
        }

        if (!IsAuthenticated())
        {
            return Unauthorized(new { message = "Authentication required to create prefill sessions" });
        }

        try
        {
            _logger.LogInformation("Creating prefill session via REST API for device {DeviceId}", deviceId);
            var session = await _sessionService.CreateSessionAsync(deviceId);
            return CreatedAtAction(nameof(GetSession), new { sessionId = session.Id }, PrefillSessionDto.FromSession(session));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create prefill session for device {DeviceId}", deviceId);
            return StatusCode(500, new { message = "Failed to create prefill session. Ensure Docker is running." });
        }
    }

    /// <summary>
    /// Terminates a prefill session
    /// </summary>
    [HttpDelete("sessions/{sessionId}")]
    public async Task<ActionResult> TerminateSession(string sessionId)
    {
        var deviceId = GetDeviceId();
        if (string.IsNullOrEmpty(deviceId))
        {
            return Unauthorized();
        }

        var session = _sessionService.GetSession(sessionId);
        if (session == null)
        {
            return NotFound();
        }

        // Only owner can terminate their session
        if (session.UserId != deviceId)
        {
            return Forbid();
        }

        await _sessionService.TerminateSessionAsync(sessionId, "Terminated via API");
        return NoContent();
    }

    /// <summary>
    /// Gets prefill service status
    /// </summary>
    [HttpGet("status")]
    public ActionResult GetStatus()
    {
        var sessions = _sessionService.GetAllSessions().ToList();

        return Ok(new
        {
            activeSessions = sessions.Count,
            maxSessionsPerUser = 1,
            sessionTimeoutMinutes = 120
        });
    }

    private string? GetDeviceId()
    {
        return Request.Headers["X-Device-Id"].FirstOrDefault();
    }

    private bool IsAuthenticated()
    {
        var deviceId = GetDeviceId();
        return !string.IsNullOrEmpty(deviceId) && _deviceAuthService.ValidateDevice(deviceId);
    }
}
