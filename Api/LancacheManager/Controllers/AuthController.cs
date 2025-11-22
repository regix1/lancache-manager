using LancacheManager.Application.Services;
using LancacheManager.Data;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// Simplified RESTful controller for authentication status checking
/// Note: Device registration, session management, and API key operations have been moved to:
/// - POST /api/devices (device registration)
/// - GET/POST/PATCH/DELETE /api/sessions (session management)
/// - GET/POST /api/api-keys (API key management)
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly ApiKeyService _apiKeyService;
    private readonly DeviceAuthService _deviceAuthService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;
    private readonly AppDbContext _dbContext;
    private readonly StateRepository _stateService;

    public AuthController(
        ApiKeyService apiKeyService,
        DeviceAuthService deviceAuthService,
        IConfiguration configuration,
        ILogger<AuthController> logger,
        AppDbContext dbContext,
        StateRepository stateService)
    {
        _apiKeyService = apiKeyService;
        _deviceAuthService = deviceAuthService;
        _configuration = configuration;
        _logger = logger;
        _dbContext = dbContext;
        _stateService = stateService;
    }

    /// <summary>
    /// GET /api/auth/status - Check if authentication is required and if current session is authenticated
    /// RESTful: This is now a status/query endpoint, properly named
    /// </summary>
    [HttpGet("status")]
    public IActionResult CheckAuthStatus()
    {
        // Check if authentication is enabled
        var authEnabled = _configuration.GetValue<bool>("Security:EnableAuthentication", true);

        // Check if already authenticated (do this first, before slow DB checks)
        var apiKey = Request.Headers["X-Api-Key"].FirstOrDefault();
        var deviceId = Request.Headers["X-Device-Id"].FirstOrDefault();

        bool isAuthenticated = false;
        string? authenticationType = null;

        if (!string.IsNullOrEmpty(apiKey) && _apiKeyService.ValidateApiKey(apiKey))
        {
            isAuthenticated = true;
            authenticationType = "api-key";
        }
        // Device-only authentication removed - devices must use API keys for ongoing auth
        // Device validation is only used during initial registration, not for session status

        // Check for guest mode eligibility (these might be slow, so do them last)
        bool hasData = false;
        bool hasEverBeenSetup = false;
        bool hasBeenInitialized = false;
        bool hasDataLoaded = false;

        try
        {
            hasData = _dbContext.Downloads.Any();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check if database has data");
        }

        try
        {
            hasEverBeenSetup = _deviceAuthService.HasAnyDeviceEverBeenRegistered();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check if any device has been registered");
        }

        try
        {
            hasBeenInitialized = _stateService.GetSetupCompleted();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check if setup has been completed");
        }

        try
        {
            hasDataLoaded = _stateService.HasDataLoaded();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check if data has been loaded");
        }

        // If authentication is disabled, always return authenticated
        if (!authEnabled)
        {
            return Ok(new
            {
                requiresAuth = false,
                isAuthenticated = true,
                authenticationType = "disabled",
                deviceId = (string?)null,
                hasData,
                hasEverBeenSetup,
                hasBeenInitialized,
                hasDataLoaded
            });
        }

        return Ok(new
        {
            requiresAuth = true,
            isAuthenticated,
            authenticationType,
            deviceId = isAuthenticated && authenticationType == "device" ? deviceId : null,
            hasData,
            hasEverBeenSetup,
            hasBeenInitialized,
            hasDataLoaded
        });
    }
}
