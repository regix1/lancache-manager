using LancacheManager.Application.Services;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for Steam Web API key management
/// Handles Steam Web API key storage, testing, and status checking
/// Note: Renamed from SteamWebApiController for proper resource-based naming
/// </summary>
[ApiController]
[Route("api/steam-api-keys")]
public class SteamApiKeysController : ControllerBase
{
    private readonly SteamWebApiService _steamWebApiService;
    private readonly ILogger<SteamApiKeysController> _logger;

    public SteamApiKeysController(
        SteamWebApiService steamWebApiService,
        ILogger<SteamApiKeysController> logger)
    {
        _steamWebApiService = steamWebApiService;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/steam-api-keys/status - Get Steam Web API status (V2/V1 availability)
    /// RESTful: Status endpoint for the API keys resource
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus([FromQuery] bool forceRefresh = false)
    {
        try
        {
            var status = await _steamWebApiService.GetApiStatusAsync(forceRefresh);

            return Ok(new
            {
                version = status.Version.ToString(),
                isV2Available = status.IsV2Available,
                isV1Available = status.IsV1Available,
                hasApiKey = status.HasApiKey,
                isFullyOperational = status.IsFullyOperational,
                message = status.Message,
                lastChecked = status.LastChecked
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Steam Web API status");
            return StatusCode(500, new { error = "Failed to get Steam Web API status", details = ex.Message });
        }
    }

    /// <summary>
    /// POST /api/steam-api-keys/test - Test a Steam Web API key without saving
    /// RESTful: POST is acceptable for testing/validation operations
    /// Request body: { "apiKey": "..." }
    /// </summary>
    [HttpPost("test")]
    [RequireAuth]
    public async Task<IActionResult> TestApiKey([FromBody] TestApiKeyRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.ApiKey))
            {
                return BadRequest(new { error = "API key is required" });
            }

            var isValid = await _steamWebApiService.TestApiKeyAsync(request.ApiKey);

            if (isValid)
            {
                return Ok(new
                {
                    valid = true,
                    message = "Steam Web API key is valid and working"
                });
            }
            else
            {
                return Ok(new
                {
                    valid = false,
                    message = "Steam Web API key is invalid or Steam Web API V1 is unavailable"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing Steam Web API key");
            return StatusCode(500, new { error = "Failed to test API key", details = ex.Message });
        }
    }

    /// <summary>
    /// POST /api/steam-api-keys - Save Steam Web API key (encrypted storage)
    /// RESTful: POST is proper method for creating/saving resources
    /// Request body: { "apiKey": "..." }
    /// </summary>
    [HttpPost]
    [RequireAuth]
    public async Task<IActionResult> SaveApiKey([FromBody] SaveApiKeyRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.ApiKey))
            {
                return BadRequest(new { error = "API key is required" });
            }

            // Test the key first
            var isValid = await _steamWebApiService.TestApiKeyAsync(request.ApiKey);

            if (!isValid)
            {
                return BadRequest(new
                {
                    error = "Invalid API key",
                    message = "The provided API key is invalid or Steam Web API V1 is unavailable. Please verify your key at https://steamcommunity.com/dev/apikey"
                });
            }

            // Save the key
            _steamWebApiService.SaveApiKey(request.ApiKey);

            _logger.LogInformation("Steam Web API key saved successfully");

            return Created("/api/steam-api-keys/status", new
            {
                message = "Steam Web API key saved successfully",
                encrypted = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving Steam Web API key");
            return StatusCode(500, new { error = "Failed to save API key", details = ex.Message });
        }
    }

    /// <summary>
    /// DELETE /api/steam-api-keys/current - Remove the configured Steam Web API key
    /// RESTful: DELETE is proper method for removing resources
    /// </summary>
    [HttpDelete("current")]
    [RequireAuth]
    public IActionResult RemoveApiKey()
    {
        try
        {
            _steamWebApiService.RemoveApiKey();

            _logger.LogInformation("Steam Web API key removed successfully");

            return Ok(new
            {
                message = "Steam Web API key removed successfully"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing Steam Web API key");
            return StatusCode(500, new { error = "Failed to remove API key", details = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/steam-api-keys/app-list - Get app list from Steam Web API (proxy/testing)
    /// RESTful: This is a proxy endpoint for testing purposes
    /// </summary>
    [HttpGet("app-list")]
    [RequireAuth]
    public async Task<IActionResult> GetAppList([FromQuery] int? limit = 100)
    {
        try
        {
            var apps = await _steamWebApiService.GetAppListAsync();

            if (apps == null)
            {
                return BadRequest(new
                {
                    error = "Failed to get app list",
                    message = "Steam Web API is not operational. Check status endpoint for details."
                });
            }

            var limitedApps = apps.Take(limit ?? 100).ToList();

            return Ok(new
            {
                total = apps.Count,
                returned = limitedApps.Count,
                apps = limitedApps
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting app list from Steam Web API");
            return StatusCode(500, new { error = "Failed to get app list", details = ex.Message });
        }
    }

    public class TestApiKeyRequest
    {
        public string ApiKey { get; set; } = string.Empty;
    }

    public class SaveApiKeyRequest
    {
        public string ApiKey { get; set; } = string.Empty;
    }
}
