using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Services.Xbox;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    private readonly ISteamAuthStorageService _steamAuthStorage;
    private readonly EpicAuthStorageService _epicAuthStorage;
    private readonly XboxAuthStorageService _xboxAuthStorage;

    public ScheduledPrefillConfigController(
        IStateService stateService,
        ISignalRNotificationService notifications,
        IServiceScheduleRegistry registry,
        ISteamAuthStorageService steamAuthStorage,
        EpicAuthStorageService epicAuthStorage,
        XboxAuthStorageService xboxAuthStorage)
    {
        _stateService = stateService;
        _notifications = notifications;
        _registry = registry;
        _steamAuthStorage = steamAuthStorage;
        _epicAuthStorage = epicAuthStorage;
        _xboxAuthStorage = xboxAuthStorage;
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
        SteamAuthData steam = _steamAuthStorage.GetAuthData();
        bool steamAuthenticated = !string.IsNullOrEmpty(steam.RefreshToken) && !string.IsNullOrEmpty(steam.Username);

        EpicAuthData epic = _epicAuthStorage.GetAuthData();
        bool epicAuthenticated = !string.IsNullOrEmpty(epic.RefreshToken);

        XboxAuthData xbox = _xboxAuthStorage.GetAuthData();
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
                LoginState = "unsupported"
            },
            new ScheduledPrefillAuthStatusDto
            {
                ServiceId = "xbox",
                IsAuthenticated = xboxAuthenticated,
                DisplayName = xboxAuthenticated ? xbox.DisplayName : null,
                // Reuse the SAME expiry the Integrations card surfaces: last-auth time + the
                // documented ~90-day MSA inactivity window (refresh tokens carry no returned expiry).
                ExpiresAtUtc = xboxAuthenticated && xbox.LastAuthenticated.HasValue
                    ? new DateTimeOffset(DateTime.SpecifyKind(xbox.LastAuthenticated.Value, DateTimeKind.Utc))
                        .Add(XboxCatalogMappingService.XboxLoginValidity)
                    : null,
                LoginState = "unsupported"
            }
        };

        return Ok(statuses);
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
