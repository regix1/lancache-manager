using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Services.Xbox;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/system/schedules/scheduledPrefill")]
// Admin-only: scheduled prefill config is shared mutable state and the auth-status endpoint
// exposes stored account login state. Bare [Authorize] satisfies the DefaultPolicy
// (RequireAuthenticatedUser), which a SessionType=guest session also satisfies. The AdminOnly
// policy (RequireClaim "SessionType"=="admin") blocks guests, matching every other admin-only
// shared-state controller (CacheController, PrefillAdminController, etc.).
[Authorize(Policy = "AdminOnly")]
public class ScheduledPrefillConfigController : ControllerBase
{
    private readonly IStateService _stateService;
    private readonly ISignalRNotificationService _notifications;
    private readonly IServiceScheduleRegistry _registry;
    private readonly IScheduledPrefillSteamAuthStorageService _scheduledPrefillSteamAuthStorage;
    private readonly IScheduledPrefillEpicAuthStorageService _scheduledPrefillEpicAuthStorage;
    private readonly IScheduledPrefillXboxAuthStorageService _scheduledPrefillXboxAuthStorage;
    private readonly ScheduledPrefillEpicAuthService _scheduledPrefillEpicAuthService;
    private readonly ScheduledPrefillXboxAuthService _scheduledPrefillXboxAuthService;
    private readonly SteamKit2Service _steamKit2Service;
    private readonly ILogger<ScheduledPrefillConfigController> _logger;

    public ScheduledPrefillConfigController(
        IStateService stateService,
        ISignalRNotificationService notifications,
        IServiceScheduleRegistry registry,
        IScheduledPrefillSteamAuthStorageService scheduledPrefillSteamAuthStorage,
        IScheduledPrefillEpicAuthStorageService scheduledPrefillEpicAuthStorage,
        IScheduledPrefillXboxAuthStorageService scheduledPrefillXboxAuthStorage,
        ScheduledPrefillEpicAuthService scheduledPrefillEpicAuthService,
        ScheduledPrefillXboxAuthService scheduledPrefillXboxAuthService,
        SteamKit2Service steamKit2Service,
        ILogger<ScheduledPrefillConfigController> logger)
    {
        _stateService = stateService;
        _notifications = notifications;
        _registry = registry;
        _scheduledPrefillSteamAuthStorage = scheduledPrefillSteamAuthStorage;
        _scheduledPrefillEpicAuthStorage = scheduledPrefillEpicAuthStorage;
        _scheduledPrefillXboxAuthStorage = scheduledPrefillXboxAuthStorage;
        _scheduledPrefillEpicAuthService = scheduledPrefillEpicAuthService;
        _scheduledPrefillXboxAuthService = scheduledPrefillXboxAuthService;
        _steamKit2Service = steamKit2Service;
        _logger = logger;
    }

    /// <summary>
    /// Returns the current scheduled prefill configuration.
    /// </summary>
    [HttpGet("config")]
    public ActionResult<ScheduledPrefillConfigDto> GetConfig()
    {
        return Ok(_stateService.GetScheduledPrefillConfig());
    }

    /// <summary>
    /// Saves the scheduled prefill configuration.
    /// </summary>
    [HttpPut("config")]
    public async Task<ActionResult> SetConfigAsync([FromBody] ScheduledPrefillConfigDto config)
    {
        try
        {
            _stateService.SetScheduledPrefillConfig(config);
        }
        catch (ScheduledPrefillConfigValidationException ex)
        {
            return BadRequest(ex.Message);
        }

        await _notifications.NotifyAllAsync(SignalREvents.SchedulesUpdated, _registry.GetAll());
        return NoContent();
    }

    /// <summary>
    /// Returns per-account-service login status for scheduled prefill (Steam/Epic/Xbox).
    /// </summary>
    [HttpGet("auth-status")]
    public ActionResult<ScheduledPrefillAuthStatusDto[]> GetAuthStatus()
    {
        SteamAuthData steam = _scheduledPrefillSteamAuthStorage.GetAuthData();
        bool steamAuthenticated = !string.IsNullOrEmpty(steam.RefreshToken) && !string.IsNullOrEmpty(steam.Username);

        EpicAuthData epic = _scheduledPrefillEpicAuthStorage.GetAuthData();
        bool epicAuthenticated = !string.IsNullOrEmpty(epic.RefreshToken);

        XboxAuthData xbox = _scheduledPrefillXboxAuthStorage.GetAuthData();
        bool xboxAuthenticated = !string.IsNullOrEmpty(xbox.RefreshToken);

        var statuses = new[]
        {
            new ScheduledPrefillAuthStatusDto
            {
                ServiceId = "steam",
                IsAuthenticated = steamAuthenticated,
                DisplayName = steamAuthenticated ? steam.Username : null,
                // Steam stores only a refresh token + login timestamp; no real credential expiry exists.
                ExpiresAtUtc = null,
                LoginState = steamAuthenticated ? "ready" : "loginRequired"
            },
            new ScheduledPrefillAuthStatusDto
            {
                ServiceId = "epic",
                IsAuthenticated = epicAuthenticated,
                DisplayName = epicAuthenticated ? epic.DisplayName : null,
                // Epic stores only a refresh token + login timestamp; no real credential expiry exists.
                ExpiresAtUtc = null,
                LoginState = epicAuthenticated ? "ready" : "loginRequired"
            },
            new ScheduledPrefillAuthStatusDto
            {
                ServiceId = "xbox",
                IsAuthenticated = xboxAuthenticated,
                DisplayName = xboxAuthenticated ? xbox.DisplayName : null,
                // Reuse the documented ~90-day MSA inactivity window for scheduled prefill Xbox credentials.
                ExpiresAtUtc = xboxAuthenticated && xbox.LastAuthenticated.HasValue
                    ? new DateTimeOffset(DateTime.SpecifyKind(xbox.LastAuthenticated.Value, DateTimeKind.Utc))
                        .Add(XboxCatalogMappingService.XboxLoginValidity)
                    : null,
                LoginState = xboxAuthenticated ? "ready" : "loginRequired"
            }
        };

        return Ok(statuses);
    }

    /// <summary>
    /// Authenticates with Steam for scheduled prefill only and stores credentials in the isolated store.
    /// Does not update the main SteamKit2 / depot-mapping credential store.
    /// </summary>
    [HttpPost("steam/login")]
    [EnableRateLimiting("steam-auth")]
    public async Task<IActionResult> SteamLoginAsync([FromBody] SteamLoginRequest? request)
    {
        if (request == null
            || string.IsNullOrEmpty(request.Username)
            || string.IsNullOrEmpty(request.Password))
        {
            return BadRequest(new ErrorResponse { Error = "Username and password are required" });
        }

        var result = await _steamKit2Service.AcquireRefreshTokenAsync(
            request.Username,
            request.Password,
            request.TwoFactorCode,
            request.EmailCode,
            request.AllowMobileConfirmation);

        if (result.Success)
        {
            string username = result.AccountName ?? request.Username;

            _scheduledPrefillSteamAuthStorage.UpdateAuthData(data =>
            {
                data.Mode = SteamAuthMode.Authenticated.ToWireString();
                data.Username = username;
                data.RefreshToken = result.RefreshToken;
                data.LastAuthenticated = DateTime.UtcNow;
            });

            _logger.LogInformation(
                "Scheduled prefill Steam authentication saved for user: {Username}",
                username);

            return Ok(SteamLoginResponseMapper.CreateSuccessResponse(username));
        }

        return SteamLoginResponseMapper.MapChallengeOrFailure(result)!;
    }

    /// <summary>
    /// Starts Epic OAuth login for scheduled prefill only (returns authorization URL).
    /// </summary>
    [HttpPost("epic/login")]
    public ActionResult StartEpicLogin()
    {
        try
        {
            var authorizationUrl = _scheduledPrefillEpicAuthService.GetAuthorizationUrl();
            return Ok(new { authorizationUrl });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate Epic authorization URL for scheduled prefill");
            return StatusCode(500, ApiResponse.Error(ex.Message));
        }
    }

    /// <summary>
    /// Completes Epic OAuth login for scheduled prefill and stores credentials in the isolated store.
    /// </summary>
    [HttpPost("epic/complete")]
    public async Task<ActionResult> CompleteEpicLoginAsync([FromBody] EpicAuthCompleteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.AuthorizationCode))
        {
            return BadRequest(ApiResponse.Error("Authorization code is required"));
        }

        try
        {
            await _scheduledPrefillEpicAuthService.CompleteAuthAsync(request.AuthorizationCode.Trim());
            EpicAuthData auth = _scheduledPrefillEpicAuthStorage.GetAuthData();
            return Ok(new
            {
                isAuthenticated = !string.IsNullOrEmpty(auth.RefreshToken),
                displayName = auth.DisplayName
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Scheduled prefill Epic auth code exchange failed");
            return BadRequest(ApiResponse.Error(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to complete scheduled prefill Epic login");
            return StatusCode(500, ApiResponse.Error(ex.Message));
        }
    }

    /// <summary>
    /// Starts Xbox MSA device-code login for scheduled prefill only.
    /// </summary>
    [HttpPost("xbox/login")]
    public async Task<ActionResult> StartXboxLoginAsync(CancellationToken ct = default)
    {
        try
        {
            var challenge = await _scheduledPrefillXboxAuthService.StartLoginAsync(ct);
            return Ok(new
            {
                userCode = challenge.UserCode,
                verificationUri = challenge.VerificationUri,
                expiresIn = challenge.ExpiresIn,
                interval = challenge.Interval,
                operationId = challenge.OperationId
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start scheduled prefill Xbox login");
            return StatusCode(500, ApiResponse.Error("Failed to start Xbox login: " + ex.Message));
        }
    }

    /// <summary>
    /// Cancels a pending scheduled prefill Xbox device-code login poll.
    /// </summary>
    [HttpPost("xbox/cancel")]
    public ActionResult CancelXboxLogin()
    {
        _scheduledPrefillXboxAuthService.CancelLogin();
        return Ok(ApiResponse.Message("Xbox login cancelled"));
    }
}

/// <summary>
/// Per-account-service authentication status for scheduled prefill.
/// </summary>
public sealed class ScheduledPrefillAuthStatusDto
{
    /// <summary>Service identifier: "steam", "epic", or "xbox".</summary>
    public required string ServiceId { get; init; }

    /// <summary>True when the manager holds stored credentials for this service.</summary>
    public required bool IsAuthenticated { get; init; }

    /// <summary>Stored account display name, when available.</summary>
    public string? DisplayName { get; init; }

    /// <summary>When the stored credentials were last authenticated / expire, when available.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; init; }

    /// <summary>"ready", "loginRequired", or "unsupported".</summary>
    public required string LoginState { get; init; }
}
