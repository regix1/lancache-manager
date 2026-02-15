using LancacheManager.Models;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// Simplified RESTful controller for authentication status checking.
/// Auth has been stripped — all endpoints are open. This controller remains
/// for the setup wizard (hasBeenInitialized, hasDataLoaded, etc.).
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly ApiKeyService _apiKeyService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;
    private readonly AppDbContext _dbContext;
    private readonly StateService _stateService;

    public AuthController(
        ApiKeyService apiKeyService,
        IConfiguration configuration,
        ILogger<AuthController> logger,
        AppDbContext dbContext,
        StateService stateService)
    {
        _apiKeyService = apiKeyService;
        _configuration = configuration;
        _logger = logger;
        _dbContext = dbContext;
        _stateService = stateService;
    }

    /// <summary>
    /// GET /api/auth/status - Always returns authenticated. Setup wizard fields preserved.
    /// </summary>
    [HttpGet("status")]
    public IActionResult CheckAuthStatus()
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

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
            RequiresAuth = false,
            IsAuthenticated = true,
            AuthenticationType = "disabled",
            DeviceId = null,
            HasData = hasData,
            HasEverBeenSetup = true,
            HasBeenInitialized = hasBeenInitialized,
            HasDataLoaded = hasDataLoaded,
            IsBanned = false
        });
    }

    /// <summary>
    /// POST /api/auth/clear-session - No-op, returns success.
    /// </summary>
    [HttpPost("clear-session")]
    public IActionResult ClearSession()
    {
        return Ok(new SessionClearResponse
        {
            Success = true,
            Message = "Session cookies cleared successfully"
        });
    }

    /// <summary>
    /// GET /api/auth/guest/config - Returns empty guest config.
    /// </summary>
    [HttpGet("guest/config")]
    public IActionResult GetGuestConfig()
    {
        return Ok(new GuestConfigResponse
        {
            DurationHours = 6,
            IsLocked = false,
            Message = "Guest configuration retrieved successfully"
        });
    }

    /// <summary>
    /// GET /api/auth/guest/status - Returns guest mode not locked.
    /// </summary>
    [HttpGet("guest/status")]
    public IActionResult GetGuestStatus()
    {
        return Ok(new
        {
            isLocked = false,
            durationHours = 6
        });
    }

    /// <summary>
    /// GET /api/auth/guest/prefill/config - Returns default prefill config.
    /// </summary>
    [HttpGet("guest/prefill/config")]
    public IActionResult GetGuestPrefillConfig()
    {
        return Ok(new
        {
            enabledByDefault = true,
            durationHours = 2,
            message = "Guest prefill configuration retrieved successfully"
        });
    }

    /// <summary>
    /// POST /api/auth/guest/config/duration - No-op, returns success.
    /// </summary>
    [HttpPost("guest/config/duration")]
    public IActionResult SetGuestSessionDuration([FromBody] SetGuestDurationRequest request)
    {
        return Ok(new GuestDurationResponse
        {
            Success = true,
            DurationHours = request.DurationHours,
            Message = $"Guest session duration updated to {request.DurationHours} hours"
        });
    }

    /// <summary>
    /// POST /api/auth/guest/config/lock - No-op, returns success.
    /// </summary>
    [HttpPost("guest/config/lock")]
    public IActionResult SetGuestModeLock([FromBody] SetGuestLockRequest request)
    {
        return Ok(new GuestLockResponse
        {
            Success = true,
            IsLocked = request.IsLocked,
            Message = request.IsLocked
                ? "Guest mode has been locked. New guests cannot log in."
                : "Guest mode has been unlocked. Guests can now log in."
        });
    }

    /// <summary>
    /// POST /api/auth/guest/prefill/config - No-op, returns success.
    /// </summary>
    [HttpPost("guest/prefill/config")]
    public IActionResult SetGuestPrefillConfig([FromBody] SetGuestPrefillConfigRequest request)
    {
        return Ok(new
        {
            success = true,
            enabledByDefault = request.EnabledByDefault,
            durationHours = request.DurationHours,
            message = "Guest prefill configuration updated successfully"
        });
    }

    /// <summary>
    /// POST /api/auth/guest/prefill/toggle/{deviceId} - No-op, returns success.
    /// </summary>
    [HttpPost("guest/prefill/toggle/{deviceId}")]
    public IActionResult ToggleGuestPrefill(string deviceId, [FromBody] ToggleGuestPrefillRequest request)
    {
        return Ok(new
        {
            success = true,
            prefillEnabled = request.Enabled,
            message = request.Enabled ? "Prefill access granted" : "Prefill access revoked"
        });
    }
}
