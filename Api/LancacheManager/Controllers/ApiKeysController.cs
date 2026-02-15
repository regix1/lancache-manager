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
    private readonly SteamKit2Service _steamKit2Service;
    private readonly SteamAuthStorageService _steamAuthStorage;
    private readonly StateService _stateService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ApiKeysController> _logger;
    private readonly ISignalRNotificationService _notifications;

    public ApiKeysController(
        ApiKeyService apiKeyService,
        SteamKit2Service steamKit2Service,
        SteamAuthStorageService steamAuthStorage,
        StateService stateService,
        IConfiguration configuration,
        ILogger<ApiKeysController> logger,
        ISignalRNotificationService notifications)
    {
        _apiKeyService = apiKeyService;
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

        _logger.LogWarning(
            "API key regenerated | Steam PICS: {SteamLogout} | Steam Web API Key: {WebApiKey}",
            steamWasAuthenticated ? "Logged out" : "Cleared",
            hadSteamWebApiKey ? "Removed" : "None");

        return Ok(new
        {
            success = true,
            message = "API key regenerated successfully.",
            warning = "Check container logs for the new API key."
        });
    }
}
