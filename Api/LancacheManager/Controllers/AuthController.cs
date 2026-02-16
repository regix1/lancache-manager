using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly SessionService _sessionService;
    private readonly ILogger<AuthController> _logger;
    private readonly AppDbContext _dbContext;
    private readonly StateService _stateService;
    private readonly ISignalRNotificationService _signalR;

    public AuthController(
        SessionService sessionService,
        ILogger<AuthController> logger,
        AppDbContext dbContext,
        StateService stateService,
        ISignalRNotificationService signalR)
    {
        _sessionService = sessionService;
        _logger = logger;
        _dbContext = dbContext;
        _stateService = stateService;
        _signalR = signalR;
    }

    [HttpGet("status")]
    public IActionResult CheckAuthStatus()
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

        var session = HttpContext.GetUserSession();

        bool hasData = false;
        bool hasBeenInitialized = false;
        bool hasDataLoaded = false;

        try { hasData = _dbContext.Downloads.Any(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to check if database has data"); }

        try { hasBeenInitialized = _stateService.GetSetupCompleted(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to check if setup has been completed"); }

        try { hasDataLoaded = _stateService.HasDataLoaded(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to check if data has been loaded"); }

        // Determine prefill access
        var prefillEnabled = false;
        DateTime? prefillExpiresAt = null;

        if (session != null)
        {
            if (session.SessionType == "admin")
            {
                prefillEnabled = true;
            }
            else if (session.SessionType == "guest")
            {
                prefillEnabled = session.PrefillExpiresAtUtc != null && session.PrefillExpiresAtUtc > DateTime.UtcNow;
                prefillExpiresAt = session.PrefillExpiresAtUtc > DateTime.UtcNow
                    ? DateTime.SpecifyKind(session.PrefillExpiresAtUtc.Value, DateTimeKind.Utc) : null;
            }
        }

        return Ok(new AuthStatusResponse
        {
            IsAuthenticated = session != null,
            SessionType = session?.SessionType,
            SessionId = session?.Id.ToString(),
            ExpiresAt = session != null ? DateTime.SpecifyKind(session.ExpiresAtUtc, DateTimeKind.Utc) : (DateTime?)null,
            HasData = hasData,
            HasBeenInitialized = hasBeenInitialized,
            HasDataLoaded = hasDataLoaded,
            GuestAccessEnabled = _sessionService.IsGuestAccessEnabled(),
            GuestDurationHours = _sessionService.GetGuestDurationHours(),
            PrefillEnabled = prefillEnabled,
            PrefillExpiresAt = prefillExpiresAt
        });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return BadRequest(new { error = "API key is required" });
        }

        // If this browser has an existing guest session, revoke it before upgrading
        var existingToken = SessionService.GetSessionTokenFromCookie(HttpContext);
        if (!string.IsNullOrEmpty(existingToken))
        {
            var existingSession = await _sessionService.ValidateSessionAsync(existingToken);
            if (existingSession is { SessionType: "guest" })
            {
                await _sessionService.RevokeSessionAsync(existingSession.Id);
                _logger.LogInformation("Revoked guest session {SessionId} during upgrade to admin", existingSession.Id);
            }
        }

        var result = await _sessionService.CreateAdminSessionAsync(request.ApiKey, HttpContext);
        if (result == null)
        {
            _logger.LogWarning("Failed login attempt from {IP}", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { error = "Invalid API key" });
        }

        var (rawToken, session) = result.Value;
        _sessionService.SetSessionCookie(HttpContext, rawToken, session.ExpiresAtUtc);

        // Broadcast session created
        await _signalR.NotifyAllAsync(SignalREvents.UserSessionCreated, new
        {
            sessionId = session.Id.ToString(),
            sessionType = session.SessionType
        });

        return Ok(new LoginResponse
        {
            Success = true,
            SessionType = session.SessionType,
            ExpiresAt = DateTime.SpecifyKind(session.ExpiresAtUtc, DateTimeKind.Utc)
        });
    }

    [HttpPost("guest")]
    public async Task<IActionResult> StartGuest()
    {
        if (!_sessionService.IsGuestAccessEnabled())
        {
            return StatusCode(403, new { error = "Guest access is disabled" });
        }

        var result = await _sessionService.CreateGuestSessionAsync(HttpContext);
        if (result == null)
        {
            return StatusCode(500, new { error = "Failed to create guest session" });
        }

        var (rawToken, session) = result.Value;
        _sessionService.SetSessionCookie(HttpContext, rawToken, session.ExpiresAtUtc);

        // Auto-grant prefill access if enabled by default
        if (_sessionService.IsGuestPrefillEnabled())
        {
            await _sessionService.GrantPrefillAccessAsync(session.Id, _sessionService.GetGuestPrefillDurationHours());
        }

        // Broadcast session created
        await _signalR.NotifyAllAsync(SignalREvents.UserSessionCreated, new
        {
            sessionId = session.Id.ToString(),
            sessionType = session.SessionType
        });

        return Ok(new LoginResponse
        {
            Success = true,
            SessionType = session.SessionType,
            ExpiresAt = DateTime.SpecifyKind(session.ExpiresAtUtc, DateTimeKind.Utc)
        });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var rawToken = SessionService.GetSessionTokenFromCookie(HttpContext);
        if (!string.IsNullOrEmpty(rawToken))
        {
            var session = await _sessionService.ValidateSessionAsync(rawToken);
            if (session != null)
            {
                await _sessionService.RevokeSessionAsync(session.Id);

                // Broadcast session revoked
                await _signalR.NotifyAllAsync(SignalREvents.UserSessionRevoked, new
                {
                    sessionId = session.Id.ToString(),
                    sessionType = session.SessionType
                });
            }
        }

        _sessionService.ClearSessionCookie(HttpContext);

        return Ok(new { success = true, message = "Logged out successfully" });
    }

    [HttpGet("guest/status")]
    public IActionResult GetGuestStatus()
    {
        return Ok(new
        {
            isLocked = _sessionService.IsGuestModeLocked(),
            durationHours = _sessionService.GetGuestDurationHours()
        });
    }

    // --- Guest Configuration Endpoints ---

    [HttpGet("guest/config")]
    public IActionResult GetGuestConfig()
    {
        return Ok(new GuestConfigResponse
        {
            DurationHours = _sessionService.GetGuestDurationHours(),
            IsLocked = _sessionService.IsGuestModeLocked()
        });
    }

    [HttpPost("guest/config/duration")]
    public async Task<IActionResult> SetGuestDuration([FromBody] GuestDurationRequest request)
    {
        if (request.DurationHours < 1 || request.DurationHours > 720)
        {
            return BadRequest(new { error = "Duration must be between 1 and 720 hours" });
        }

        _sessionService.SetGuestDurationHours(request.DurationHours);

        _logger.LogInformation("Default guest duration updated to {Hours}h (existing sessions unchanged)", request.DurationHours);

        await _signalR.NotifyAllAsync(SignalREvents.GuestDurationUpdated, new
        {
            durationHours = request.DurationHours
        });

        return Ok(new { success = true, durationHours = request.DurationHours, message = "Guest duration updated" });
    }

    [HttpPost("guest/config/lock")]
    public async Task<IActionResult> SetGuestLock([FromBody] GuestLockRequest request)
    {
        _sessionService.SetGuestModeLocked(request.IsLocked);

        await _signalR.NotifyAllAsync(SignalREvents.GuestModeLockChanged, new
        {
            isLocked = request.IsLocked
        });

        return Ok(new { success = true, isLocked = request.IsLocked, message = request.IsLocked ? "Guest mode locked" : "Guest mode unlocked" });
    }

    // --- Guest Prefill Endpoints ---

    [HttpGet("guest/prefill/config")]
    public IActionResult GetGuestPrefillConfig()
    {
        return Ok(new
        {
            enabledByDefault = _sessionService.IsGuestPrefillEnabled(),
            durationHours = _sessionService.GetGuestPrefillDurationHours(),
            maxThreadCount = _stateService.GetDefaultGuestMaxThreadCount()
        });
    }

    [HttpPost("guest/prefill/config")]
    public async Task<IActionResult> SetGuestPrefillConfig([FromBody] GuestPrefillConfigRequest request)
    {
        if (request.DurationHours != 1 && request.DurationHours != 2)
        {
            return BadRequest(new { error = "Duration must be 1 or 2 hours" });
        }

        _sessionService.SetGuestPrefillEnabled(request.EnabledByDefault);
        _sessionService.SetGuestPrefillDurationHours(request.DurationHours);
        _stateService.SetDefaultGuestMaxThreadCount(request.MaxThreadCount);

        _logger.LogInformation("Default guest prefill config updated: enabled={Enabled}, duration={Hours}h, maxThreads={MaxThreads} (existing sessions unchanged)",
            request.EnabledByDefault, request.DurationHours, request.MaxThreadCount);

        await _signalR.NotifyAllAsync(SignalREvents.GuestPrefillConfigChanged, new
        {
            enabledByDefault = request.EnabledByDefault,
            durationHours = request.DurationHours,
            maxThreadCount = _stateService.GetDefaultGuestMaxThreadCount()
        });

        return Ok(new {
            success = true,
            enabledByDefault = _sessionService.IsGuestPrefillEnabled(),
            durationHours = _sessionService.GetGuestPrefillDurationHours(),
            maxThreadCount = _stateService.GetDefaultGuestMaxThreadCount()
        });
    }

    [HttpPost("guest/prefill/toggle/{sessionId:guid}")]
    public async Task<IActionResult> ToggleGuestPrefill(Guid sessionId, [FromBody] GuestPrefillToggleRequest request)
    {
        if (request.Enabled)
        {
            await _sessionService.GrantPrefillAccessAsync(sessionId, _sessionService.GetGuestPrefillDurationHours());
        }
        else
        {
            await _sessionService.RevokePrefillAccessAsync(sessionId);
        }

        var updatedSession = await _dbContext.UserSessions.FindAsync(sessionId);
        var prefillExpiresAt = updatedSession?.PrefillExpiresAtUtc != null
            ? DateTime.SpecifyKind(updatedSession.PrefillExpiresAtUtc!.Value, DateTimeKind.Utc)
            : (DateTime?)null;

        await _signalR.NotifyAllAsync(SignalREvents.GuestPrefillPermissionChanged, new
        {
            sessionId = sessionId.ToString(),
            enabled = request.Enabled,
            prefillExpiresAt
        });

        return Ok(new { success = true, sessionId = sessionId.ToString(), enabled = request.Enabled, prefillExpiresAt });
    }
}

// Request models
public class GuestDurationRequest
{
    public int DurationHours { get; set; }
}

public class GuestLockRequest
{
    public bool IsLocked { get; set; }
}

public class GuestPrefillConfigRequest
{
    public bool EnabledByDefault { get; set; }
    public int DurationHours { get; set; } = 2;
    public int? MaxThreadCount { get; set; }
}

public class GuestPrefillToggleRequest
{
    public bool Enabled { get; set; }
}
