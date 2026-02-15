using LancacheManager.Models;
using LancacheManager.Core.Services;
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
        var status = await _steamWebApiService.GetApiStatusAsync(forceRefresh);

        return Ok(new SteamApiStatusResponse
        {
            Version = status.Version.ToString(),
            IsV2Available = status.IsV2Available,
            IsV1Available = status.IsV1Available,
            HasApiKey = status.HasApiKey,
            IsFullyOperational = status.IsFullyOperational,
            Message = status.Message,
            LastChecked = status.LastChecked
        });
    }

    /// <summary>
    /// POST /api/steam-api-keys/test - Test a Steam Web API key without saving
    /// RESTful: POST is acceptable for testing/validation operations
    /// Request body: { "apiKey": "..." }
    /// </summary>
    /// <remarks>
    /// Validation is handled automatically by FluentValidation (see TestApiKeyRequestValidator).
    ///
    /// NOTE: This endpoint appears redundant with the validation in POST /api/steam-api-keys,
    /// but both are intentionally kept for UX purposes:
    /// - /test allows users to test a key without saving it (try before commit)
    /// - POST (save) validates as a safety check before persisting
    /// Both endpoints are actively used by the frontend setup wizard and settings modal.
    /// </remarks>
    [HttpPost("test")]
    public async Task<IActionResult> TestApiKey([FromBody] TestApiKeyRequest request)
    {
        // Validation is handled automatically by FluentValidation
        var isValid = await _steamWebApiService.TestApiKeyAsync(request.ApiKey);

        if (isValid)
        {
            return Ok(new ApiKeyTestResponse
            {
                Valid = true,
                Message = "Steam Web API key is valid and working"
            });
        }
        else
        {
            return Ok(new ApiKeyTestResponse
            {
                Valid = false,
                Message = "Steam Web API key is invalid or Steam Web API V1 is unavailable"
            });
        }
    }

    /// <summary>
    /// POST /api/steam-api-keys - Save Steam Web API key (encrypted storage)
    /// RESTful: POST is proper method for creating/saving resources
    /// Request body: { "apiKey": "..." }
    /// </summary>
    /// <remarks>
    /// Validation is handled automatically by FluentValidation (see SaveApiKeyRequestValidator)
    /// </remarks>
    [HttpPost]
    public async Task<IActionResult> SaveApiKey([FromBody] SaveApiKeyRequest request)
    {
        // Validation is handled automatically by FluentValidation
        // Test the key first
        var isValid = await _steamWebApiService.TestApiKeyAsync(request.ApiKey);

        if (!isValid)
        {
            return BadRequest(new ErrorResponse
            {
                Error = "Invalid API key",
                Message = "The provided API key is invalid or Steam Web API V1 is unavailable. Please verify your key at https://steamcommunity.com/dev/apikey"
            });
        }

        // Save the key
        _steamWebApiService.SaveApiKey(request.ApiKey);

        _logger.LogInformation("Steam Web API key saved successfully");

        return Created("/api/steam-api-keys/status", new ApiKeySaveResponse
        {
            Message = "Steam Web API key saved successfully",
            Encrypted = true
        });
    }

    /// <summary>
    /// DELETE /api/steam-api-keys/current - Remove the configured Steam Web API key
    /// RESTful: DELETE is proper method for removing resources
    /// </summary>
    [HttpDelete("current")]
    public IActionResult RemoveApiKey()
    {
        _steamWebApiService.RemoveApiKey();

        _logger.LogInformation("Steam Web API key removed successfully");

        return Ok(new ApiKeyRemoveResponse
        {
            Message = "Steam Web API key removed successfully"
        });
    }
}
