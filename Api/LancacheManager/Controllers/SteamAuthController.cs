using LancacheManager.Application.Services;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for Steam authentication (SteamKit2)
/// Handles Steam login, logout, and authentication status
/// </summary>
[ApiController]
[Route("api/steam-auth")]
public class SteamAuthController : ControllerBase
{
    private readonly SteamKit2Service _steamKit2Service;
    private readonly SteamAuthRepository _steamAuthStorage;
    private readonly StateRepository _stateService;
    private readonly ILogger<SteamAuthController> _logger;

    public SteamAuthController(
        SteamKit2Service steamKit2Service,
        SteamAuthRepository steamAuthStorage,
        StateRepository stateService,
        ILogger<SteamAuthController> logger)
    {
        _steamKit2Service = steamKit2Service;
        _steamAuthStorage = steamAuthStorage;
        _stateService = stateService;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/steam-auth/status - Get Steam authentication status
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetSteamAuthStatus()
    {
        try
        {
            var authMode = _stateService.GetSteamAuthMode();
            var isConnected = _steamKit2Service.IsReady;
            var username = _stateService.GetSteamUsername();

            return Ok(new
            {
                authMode,
                isConnected,
                isAuthenticated = !string.IsNullOrEmpty(username),
                hasStoredCredentials = !string.IsNullOrEmpty(username)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Steam auth status");
            return StatusCode(500, new { error = "Failed to get Steam auth status", details = ex.Message });
        }
    }

    /// <summary>
    /// POST /api/steam-auth/login - Login to Steam
    /// Request body depends on auth mode:
    /// - Anonymous: No body required
    /// - Credentials: { "username": "...", "password": "..." }
    /// - Guard Code: { "guardCode": "..." }
    /// </summary>
    [HttpPost("login")]
    [RequireAuth]
    public async Task<IActionResult> LoginToSteam([FromBody] SteamLoginRequest? request)
    {
        try
        {
            var authMode = _stateService.GetSteamAuthMode();

            if (authMode == "authenticated")
            {
                // Authenticated login
                if (request == null || string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
                {
                    return BadRequest(new { error = "Username and password required for authenticated login" });
                }

                var result = await _steamKit2Service.AuthenticateAsync(
                    request.Username,
                    request.Password,
                    request.TwoFactorCode,
                    request.EmailCode,
                    false // allowMobileConfirmation
                );

                if (result.Success)
                {
                    _stateService.SetSteamAuthMode("authenticated");
                    _stateService.SetSteamUsername(request.Username);

                    _logger.LogInformation("Steam authentication successful for user: {Username}", request.Username);

                    return Ok(new
                    {
                        success = true,
                        message = "Authentication successful",
                        authMode = "authenticated",
                        username = request.Username
                    });
                }
                else if (result.RequiresTwoFactor)
                {
                    return Ok(new { requiresTwoFactor = true, message = "Two-factor authentication required" });
                }
                else if (result.RequiresEmailCode)
                {
                    return Ok(new { requiresEmailCode = true, message = "Email verification code required" });
                }
                else
                {
                    return BadRequest(new { error = result.Message ?? "Authentication failed" });
                }
            }
            else if (authMode == "anonymous")
            {
                // Anonymous mode - Steam connects automatically
                _logger.LogInformation("Steam running in anonymous mode");

                return Ok(new
                {
                    message = "Steam is running in anonymous mode",
                    authMode = "anonymous",
                    status = "connected"
                });
            }
            else
            {
                return BadRequest(new { error = $"Unknown Steam auth mode: {authMode}" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Steam login");
            return StatusCode(500, new { error = "Steam login failed", details = ex.Message });
        }
    }

    /// <summary>
    /// DELETE /api/steam-auth - Logout from Steam
    /// RESTful: DELETE is proper method for removing/ending sessions
    /// </summary>
    [HttpDelete]
    [RequireAuth]
    public async Task<IActionResult> LogoutFromSteam()
    {
        try
        {
            await _steamKit2Service.LogoutAsync();
            _logger.LogInformation("Steam logout completed");

            return Ok(new { message = "Logged out from Steam successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Steam logout");
            return StatusCode(500, new { error = "Steam logout failed", details = ex.Message });
        }
    }

    public class SteamLoginRequest
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? TwoFactorCode { get; set; }
        public string? EmailCode { get; set; }
    }
}
