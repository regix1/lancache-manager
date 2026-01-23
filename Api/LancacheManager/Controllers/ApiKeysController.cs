using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Core.Interfaces;
using LancacheManager.Security;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Hubs;
using Microsoft.AspNetCore.Mvc;
using LancacheManager.Core.Services.SteamKit2;


namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for API key management
/// Handles API key retrieval, regeneration, and validation
/// </summary>
[ApiController]
[Route("api/api-keys")]
public class ApiKeysController : ControllerBase
{
    private readonly ApiKeyService _apiKeyService;
    private readonly DeviceAuthService _deviceAuthService;
    private readonly GuestSessionService _guestSessionService;
    private readonly SteamKit2Service _steamKit2Service;
    private readonly SteamAuthStorageService _steamAuthStorage;
    private readonly StateService _stateService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ApiKeysController> _logger;
    private readonly ISignalRNotificationService _notifications;

    public ApiKeysController(
        ApiKeyService apiKeyService,
        DeviceAuthService deviceAuthService,
        GuestSessionService guestSessionService,
        SteamKit2Service steamKit2Service,
        SteamAuthStorageService steamAuthStorage,
        StateService stateService,
        IConfiguration configuration,
        ILogger<ApiKeysController> logger,
        ISignalRNotificationService notifications)
    {
        _apiKeyService = apiKeyService;
        _deviceAuthService = deviceAuthService;
        _guestSessionService = guestSessionService;
        _steamKit2Service = steamKit2Service;
        _steamAuthStorage = steamAuthStorage;
        _stateService = stateService;
        _configuration = configuration;
        _logger = logger;
        _notifications = notifications;
    }

    /// <summary>
    /// GET /api/api-keys/status - Check API key type
    /// </summary>
    [HttpGet("status")]
    [RequireAuth]
    public IActionResult GetApiKeyStatus()
    {
        var apiKey = Request.Headers["X-Api-Key"].FirstOrDefault();

        if (string.IsNullOrEmpty(apiKey))
        {
            return Ok(new ApiKeyStatusResponse
            {
                HasApiKey = false,
                KeyType = "none",
                HasPrimaryKey = false
            });
        }

        var isValid = _apiKeyService.ValidateApiKey(apiKey);

        return Ok(new ApiKeyStatusResponse
        {
            HasApiKey = isValid,
            KeyType = isValid ? "admin" : "none",
            HasPrimaryKey = isValid
        });
    }

    /// <summary>
    /// POST /api/api-keys/regenerate - Regenerate the API key
    /// RESTful: POST is acceptable for operations that create new resources/states
    /// SECURITY: This logs out all Steam sessions and revokes all device registrations
    /// </summary>
    [HttpPost("regenerate")]
    [RequireAuth]
    public async Task<IActionResult> RegenerateApiKey()
    {
        // SECURITY: Clear ALL Steam-related data when API key is regenerated
        var steamWasAuthenticated = _stateService.GetSteamAuthMode() == "authenticated";
        var hadSteamWebApiKey = !string.IsNullOrWhiteSpace(_steamAuthStorage.GetSteamAuthData().SteamApiKey);

        // Clear Steam auth data (with error handling to ensure API key regen completes)
        try
        {
            await _steamKit2Service.ClearAllSteamAuthAsync();
        }
        catch (Exception steamEx)
        {
            _logger.LogWarning(steamEx, "Error clearing Steam auth during API key regeneration (continuing anyway)");
        }

        var (oldKey, newKey) = _apiKeyService.ForceRegenerateApiKey();
        _apiKeyService.DisplayApiKey(_configuration);

        // Revoke all existing device registrations
        var revokedDeviceCount = _deviceAuthService.RevokeAllDevices();

        // Revoke all guest sessions
        var guestSessions = _guestSessionService.GetAllSessions();
        var revokedGuestCount = 0;
        foreach (var session in guestSessions.Where(s => !s.IsRevoked))
        {
            if (!string.IsNullOrEmpty(session.SessionId) &&
                _guestSessionService.RevokeSession(session.SessionId, "Admin (API Key Regeneration)"))
            {
                revokedGuestCount++;
            }
        }

        // Broadcast to ALL users for instant logout
        await _notifications.NotifyAllAsync(SignalREvents.UserSessionsCleared);
        _logger.LogInformation("Broadcasted UserSessionsCleared event");

        _logger.LogWarning(
            "API key regenerated. Revoked: {DeviceCount} device(s), {GuestCount} guest(s) | Steam PICS: {SteamLogout} | Steam Web API Key: {WebApiKey}",
            revokedDeviceCount,
            revokedGuestCount,
            steamWasAuthenticated ? "Logged out" : "Cleared",
            hadSteamWebApiKey ? "Removed" : "None");

        var steamStatus = new List<string>();
        if (steamWasAuthenticated) steamStatus.Add("Steam session terminated");
        if (hadSteamWebApiKey) steamStatus.Add("Steam Web API key removed");
        var steamMessage = steamStatus.Count > 0 ? " " + string.Join(", ", steamStatus) + "." : "";

        return Ok(new
        {
            success = true,
            message = $"API key regenerated successfully. {revokedDeviceCount} device(s) and {revokedGuestCount} guest session(s) revoked.{steamMessage}",
            warning = "All users must re-authenticate with the new key. Check container logs for the new API key."
        });
    }
}
