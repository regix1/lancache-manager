using Microsoft.AspNetCore.Mvc;
using LancacheManager.Security;
using LancacheManager.Services;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly ApiKeyService _apiKeyService;
    private readonly DeviceAuthService _deviceAuthService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        ApiKeyService apiKeyService,
        DeviceAuthService deviceAuthService,
        IConfiguration configuration,
        ILogger<AuthController> logger)
    {
        _apiKeyService = apiKeyService;
        _deviceAuthService = deviceAuthService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Check if authentication is required and if current session is authenticated
    /// </summary>
    [HttpGet("check")]
    public IActionResult CheckAuth()
    {
        // Check if authentication is enabled
        var authEnabled = _configuration.GetValue<bool>("Security:EnableAuthentication", true);

        // If authentication is disabled, always return authenticated
        if (!authEnabled)
        {
            return Ok(new
            {
                requiresAuth = false,
                isAuthenticated = true,
                authenticationType = "disabled",
                deviceId = (string?)null
            });
        }

        // Check if already authenticated
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

        return Ok(new
        {
            requiresAuth = true,
            isAuthenticated,
            authenticationType,
            deviceId = isAuthenticated && authenticationType == "device" ? deviceId : null
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
    /// </summary>
    [HttpPost("regenerate-key")]
    [RequireAuth]
    public IActionResult RegenerateApiKey()
    {
        try
        {
            var (oldKey, newKey) = _apiKeyService.ForceRegenerateApiKey();

            // Display the new key
            _apiKeyService.DisplayApiKey();

            // Revoke all existing device registrations
            var revokedCount = _deviceAuthService.RevokeAllDevices();

            _logger.LogWarning(
                "API key regenerated. Old key prefix: {OldPrefix}, New key prefix: {NewPrefix}. {RevokedCount} device registration(s) revoked.",
                oldKey[..System.Math.Min(oldKey.Length, 12)],
                newKey[..System.Math.Min(newKey.Length, 12)],
                revokedCount);

            return Ok(new 
            { 
                success = true,
                message = $"API key regenerated successfully. {revokedCount} device(s) revoked.",
                warning = "All users must re-authenticate with the new key. Check container logs for the new key."
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
