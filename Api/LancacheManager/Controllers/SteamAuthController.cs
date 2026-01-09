using System.ComponentModel.DataAnnotations;
using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using LancacheManager.Core.Services.SteamKit2;


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
        var authMode = _stateService.GetSteamAuthMode();
        var isConnected = _steamKit2Service.IsReady;
        var username = _stateService.GetSteamUsername();

        return Ok(new SteamAuthStatusResponse
        {
            Mode = authMode ?? string.Empty,
            Username = username ?? string.Empty,
            IsAuthenticated = !string.IsNullOrEmpty(username),
            // Legacy fields for backward compatibility
            AuthMode = authMode ?? string.Empty,
            IsConnected = isConnected,
            HasStoredCredentials = !string.IsNullOrEmpty(username)
        });
    }

    /// <summary>
    /// POST /api/steam-auth/login - Login to Steam
    /// Request body depends on auth mode:
    /// - Anonymous: No body required
    /// - Credentials: { "username": "...", "password": "..." }
    /// - Guard Code: { "guardCode": "..." }
    /// Note: This endpoint does NOT require LANCache Manager authentication
    /// since users need to be able to log in to Steam before authenticating to the app
    /// </summary>
    [HttpPost("login")]
    [EnableRateLimiting("steam-auth")]
    public async Task<IActionResult> LoginToSteam([FromBody] SteamLoginRequest? request)
    {
        // If user provides credentials, they want to authenticate (regardless of current mode)
        if (request != null && !string.IsNullOrEmpty(request.Username) && !string.IsNullOrEmpty(request.Password))
        {
            // User wants to switch to authenticated mode
            var result = await _steamKit2Service.AuthenticateAsync(
                request.Username,
                request.Password,
                request.TwoFactorCode,
                request.EmailCode,
                request.AllowMobileConfirmation
            );

            if (result.Success)
            {
                _stateService.SetSteamAuthMode("authenticated");
                _stateService.SetSteamUsername(request.Username);

                _logger.LogInformation("Steam authentication successful for user: {Username}", request.Username);

                // Auto-start PICS rebuild if requested
                if (request.AutoStartPicsRebuild)
                {
                    _logger.LogInformation("Auto-starting PICS depot mapping rebuild after login");
                    _steamKit2Service.TryStartRebuild();
                }

                return Ok(new SteamLoginResponse
                {
                    Success = true,
                    Message = "Authentication successful",
                    AuthMode = "authenticated",
                    Username = request.Username
                });
            }
            else if (result.RequiresTwoFactor)
            {
                return Ok(new SteamLoginResponse { RequiresTwoFactor = true, Message = "Two-factor authentication required" });
            }
            else if (result.RequiresEmailCode)
            {
                return Ok(new SteamLoginResponse { RequiresEmailCode = true, Message = "Email verification code required" });
            }
            else if (result.SessionExpired)
            {
                // Session expired waiting for mobile confirmation - suggest using 2FA code instead
                return Ok(new SteamLoginResponse
                {
                    SessionExpired = true,
                    RequiresTwoFactor = true,
                    Message = result.Message ?? "Session expired. Please enter your 2FA code instead."
                });
            }
            else
            {
                return BadRequest(new ErrorResponse { Error = result.Message ?? "Authentication failed" });
            }
        }

        // No credentials provided - just return current status
        var authMode = _stateService.GetSteamAuthMode();

        if (authMode == "anonymous")
        {
            return Ok(new SteamLoginResponse
            {
                Message = "Steam is running in anonymous mode. Provide username and password to authenticate.",
                AuthMode = "anonymous",
                Status = "connected"
            });
        }
        else if (authMode == "authenticated")
        {
            var username = _stateService.GetSteamUsername();
            return Ok(new SteamLoginResponse
            {
                Message = $"Already authenticated as {username}",
                AuthMode = "authenticated",
                Username = username,
                Status = "connected"
            });
        }
        else
        {
            return BadRequest(new ErrorResponse { Error = $"Unknown Steam auth mode: {authMode}" });
        }
    }

    /// <summary>
    /// PUT /api/steam-auth/mode - Set Steam authentication mode
    /// Request body: { "mode": "anonymous" | "authenticated" }
    /// Used during setup to explicitly save the user's auth mode choice
    /// </summary>
    [HttpPut("mode")]
    public IActionResult SetSteamAuthMode([FromBody] SetModeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Mode))
        {
            return BadRequest(new ErrorResponse { Error = "Mode is required" });
        }

        var mode = request.Mode.ToLowerInvariant();
        if (mode != "anonymous" && mode != "authenticated")
        {
            return BadRequest(new ErrorResponse { Error = "Mode must be 'anonymous' or 'authenticated'" });
        }

        _stateService.SetSteamAuthMode(mode);
        _logger.LogInformation("Steam auth mode set to: {Mode}", mode);

        return Ok(new SteamModeResponse
        {
            Success = true,
            Message = $"Steam authentication mode set to {mode}",
            Mode = mode
        });
    }

    /// <summary>
    /// DELETE /api/steam-auth - Logout from Steam
    /// RESTful: DELETE is proper method for removing/ending sessions
    /// Note: This endpoint does NOT require LANCache Manager authentication
    /// </summary>
    [HttpDelete]
    public async Task<IActionResult> LogoutFromSteam()
    {
        await _steamKit2Service.LogoutAsync();
        _logger.LogInformation("Steam logout completed");

        return Ok(MessageResponse.Ok("Logged out from Steam successfully"));
    }

    public class SteamLoginRequest
    {
        [StringLength(64, ErrorMessage = "Username cannot exceed 64 characters")]
        public string? Username { get; set; }

        [StringLength(256, ErrorMessage = "Password cannot exceed 256 characters")]
        public string? Password { get; set; }

        [StringLength(10, ErrorMessage = "TwoFactorCode cannot exceed 10 characters")]
        [RegularExpression(@"^[A-Z0-9]*$", ErrorMessage = "TwoFactorCode contains invalid characters")]
        public string? TwoFactorCode { get; set; }

        [StringLength(10, ErrorMessage = "EmailCode cannot exceed 10 characters")]
        [RegularExpression(@"^[A-Z0-9]*$", ErrorMessage = "EmailCode contains invalid characters")]
        public string? EmailCode { get; set; }

        public bool AllowMobileConfirmation { get; set; } = true;
        public bool AutoStartPicsRebuild { get; set; } = false;
    }

    public class SetModeRequest
    {
        public string? Mode { get; set; }
    }
}
