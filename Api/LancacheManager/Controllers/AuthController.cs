using Microsoft.AspNetCore.Mvc;
using LancacheManager.Security;
using LancacheManager.Services;
using LancacheManager.Data;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly ApiKeyService _apiKeyService;
    private readonly DeviceAuthService _deviceAuthService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;
    private readonly AppDbContext _dbContext;
    private readonly StateService _stateService;
    private readonly SteamKit2Service _steamKit2Service;
    private readonly SteamAuthStorageService _steamAuthStorage;

    public AuthController(
        ApiKeyService apiKeyService,
        DeviceAuthService deviceAuthService,
        IConfiguration configuration,
        ILogger<AuthController> logger,
        AppDbContext dbContext,
        StateService stateService,
        SteamKit2Service steamKit2Service,
        SteamAuthStorageService steamAuthStorage)
    {
        _apiKeyService = apiKeyService;
        _deviceAuthService = deviceAuthService;
        _configuration = configuration;
        _logger = logger;
        _dbContext = dbContext;
        _stateService = stateService;
        _steamKit2Service = steamKit2Service;
        _steamAuthStorage = steamAuthStorage;
    }

    /// <summary>
    /// Check if authentication is required and if current session is authenticated
    /// </summary>
    [HttpGet("check")]
    public IActionResult CheckAuth()
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
        else if (!string.IsNullOrEmpty(deviceId) && _deviceAuthService.ValidateDevice(deviceId))
        {
            isAuthenticated = true;
            authenticationType = "device";
        }

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

    /// <summary>
    /// Register a device with the API key
    /// </summary>
    [HttpPost("register")]
    public IActionResult RegisterDevice([FromBody] DeviceAuthService.RegisterDeviceRequest request)
    {
        try
        {
            // Get client IP
            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
            var userAgent = Request.Headers["User-Agent"].FirstOrDefault();
            
            _logger.LogInformation("Device registration attempt from IP: {IP}, Device: {DeviceId}", 
                ipAddress, request.DeviceId);
            
            var result = _deviceAuthService.RegisterDevice(request, ipAddress, userAgent);
            
            if (result.Success)
            {
                return Ok(result);
            }
            
            return Unauthorized(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during device registration");
            return StatusCode(500, new { error = "Registration failed", message = ex.Message });
        }
    }

    /// <summary>
    /// Validate a device ID
    /// </summary>
    [HttpPost("validate")]
    public IActionResult ValidateDevice([FromBody] ValidateRequest request)
    {
        try
        {
            var isValid = _deviceAuthService.ValidateDevice(request.DeviceId);
            
            if (isValid)
            {
                return Ok(new { valid = true, message = "Device is registered and valid" });
            }
            
            return Unauthorized(new { valid = false, message = "Device not registered or expired" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating device");
            return StatusCode(500, new { error = "Validation failed" });
        }
    }

    /// <summary>
    /// Get all registered devices (requires authentication)
    /// </summary>
    [HttpGet("devices")]
    [RequireAuth]
    public IActionResult GetDevices()
    {
        try
        {
            var devices = _deviceAuthService.GetAllDevices();
            return Ok(devices);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting devices");
            return StatusCode(500, new { error = "Failed to get devices" });
        }
    }

    /// <summary>
    /// Revoke a device registration (requires authentication)
    /// </summary>
    [HttpDelete("devices/{deviceId}")]
    [RequireAuth]
    public IActionResult RevokeDevice(string deviceId)
    {
        try
        {
            var success = _deviceAuthService.RevokeDevice(deviceId);
            
            if (success)
            {
                _logger.LogInformation("Device revoked: {DeviceId}", deviceId);
                return Ok(new { message = "Device revoked successfully" });
            }
            
            return NotFound(new { error = "Device not found" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error revoking device");
            return StatusCode(500, new { error = "Failed to revoke device" });
        }
    }

    /// <summary>
    /// Regenerate the API key (requires authentication)
    /// SECURITY: This will logout all Steam sessions and revoke all device registrations
    /// </summary>
    [HttpPost("regenerate-key")]
    [RequireAuth]
    public async Task<IActionResult> RegenerateApiKey()
    {
        try
        {
            // SECURITY: Logout from Steam first (both in-memory session AND state)
            // This ensures old encrypted tokens become unreadable AND active session is terminated
            var steamWasAuthenticated = _stateService.GetSteamAuthMode() == "authenticated";
            if (steamWasAuthenticated)
            {
                _logger.LogInformation("Logging out from Steam (in-memory + state) before API key regeneration for security");

                // Disconnect the active Steam session
                await _steamKit2Service.LogoutAsync();

                // Clear tokens from Steam auth file (using Microsoft Data Protection API)
                _steamAuthStorage.ClearSteamAuthData();
                _logger.LogInformation("Cleared Steam auth data from encrypted file (data/steam_auth/credentials.json)");
            }

            var (oldKey, newKey) = _apiKeyService.ForceRegenerateApiKey();

            // Display the new key
            _apiKeyService.DisplayApiKey();

            // Revoke all existing device registrations
            var revokedCount = _deviceAuthService.RevokeAllDevices();

            _logger.LogWarning(
                "API key regenerated. Old key prefix: {OldPrefix}, New key prefix: {NewPrefix}. {RevokedCount} device registration(s) revoked. Steam logout: {SteamLogout}",
                oldKey[..System.Math.Min(oldKey.Length, 12)],
                newKey[..System.Math.Min(newKey.Length, 12)],
                revokedCount,
                steamWasAuthenticated ? "Yes (in-memory + state)" : "No");

            return Ok(new
            {
                success = true,
                message = $"API key regenerated successfully. {revokedCount} device(s) revoked." +
                          (steamWasAuthenticated ? " Steam session terminated and logged out." : ""),
                warning = "All users must re-authenticate with the new key. Steam re-authentication required. Check container logs for the new key."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error regenerating API key");
            return StatusCode(500, new { error = "Failed to regenerate API key", message = ex.Message });
        }
    }

    /// <summary>
    /// Get the current API key (only works from localhost)
    /// </summary>
    [HttpGet("key")]
    public IActionResult GetApiKey()
    {
        // Only allow from localhost
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        if (remoteIp == null || (!remoteIp.ToString().StartsWith("127.") && !remoteIp.ToString().StartsWith("::1")))
        {
            _logger.LogWarning("API key request denied from non-localhost IP: {IP}", remoteIp);
            return Forbid("This endpoint is only accessible from localhost");
        }
        
        var key = _apiKeyService.GetOrCreateApiKey();
        return Ok(new 
        { 
            apiKey = key,
            message = "Save this key! It's required for authentication.",
            warning = "This endpoint is only accessible from localhost"
        });
    }
}

public class ValidateRequest
{
    public string DeviceId { get; set; } = string.Empty;
}
