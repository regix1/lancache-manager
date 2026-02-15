using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly SessionService _sessionService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;
    private readonly AppDbContext _dbContext;
    private readonly StateService _stateService;

    public AuthController(
        SessionService sessionService,
        IConfiguration configuration,
        ILogger<AuthController> logger,
        AppDbContext dbContext,
        StateService stateService)
    {
        _sessionService = sessionService;
        _configuration = configuration;
        _logger = logger;
        _dbContext = dbContext;
        _stateService = stateService;
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

        return Ok(new AuthStatusResponse
        {
            IsAuthenticated = session != null,
            SessionType = session?.SessionType,
            ExpiresAt = session?.ExpiresAtUtc,
            HasData = hasData,
            HasBeenInitialized = hasBeenInitialized,
            HasDataLoaded = hasDataLoaded,
            GuestAccessEnabled = _sessionService.IsGuestAccessEnabled(),
            GuestDurationHours = _sessionService.GetGuestDurationHours()
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

        return Ok(new LoginResponse
        {
            Success = true,
            SessionType = session.SessionType,
            ExpiresAt = session.ExpiresAtUtc
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

        return Ok(new LoginResponse
        {
            Success = true,
            SessionType = session.SessionType,
            ExpiresAt = session.ExpiresAtUtc
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
            isLocked = !_sessionService.IsGuestAccessEnabled(),
            durationHours = _sessionService.GetGuestDurationHours()
        });
    }
}
