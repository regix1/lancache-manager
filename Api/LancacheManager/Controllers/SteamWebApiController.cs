using LancacheManager.Application.Services;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SteamWebApiController : ControllerBase
{
    private readonly SteamWebApiService _steamWebApiService;
    private readonly ILogger<SteamWebApiController> _logger;

    public SteamWebApiController(
        SteamWebApiService steamWebApiService,
        ILogger<SteamWebApiController> logger)
    {
        _steamWebApiService = steamWebApiService;
        _logger = logger;
    }

    /// <summary>
    /// Get current Steam Web API status (V2/V1 availability)
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
    /// Test a Steam Web API key without saving it
    /// </summary>
    [HttpPost("test-key")]
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
    /// Save Steam Web API key (encrypted storage)
    /// </summary>
    [HttpPost("save-key")]
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

            return Ok(new
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
    /// Remove the configured Steam Web API key
    /// </summary>
    [HttpPost("remove-key")]
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
    /// Get app list from Steam Web API (for testing/debugging)
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
