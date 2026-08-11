using LancacheManager.Models;
using LancacheManager.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for Steam Web API key management
/// Handles Steam Web API key storage, testing, and status checking
/// Note: Renamed from SteamWebApiController for proper resource-based naming
/// </summary>
[ApiController]
[Route("api/steam-api-keys")]
[Authorize(Policy = "AccountHolder")]
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
    /// Gets the Steam Web API status.
    /// </summary>
    /// <remarks>
    /// Reports V2 and V1 availability. This is the status endpoint for the API keys resource.
    /// </remarks>
    [HttpGet("status")]
    [ProducesResponseType(typeof(SteamApiStatusResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SteamApiStatusResponse>> GetStatusAsync([FromQuery] bool forceRefresh = false)
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
    /// Tests a Steam Web API key without saving it.
    /// </summary>
    /// <remarks>
    /// POST is used here for testing/validation, not resource creation. Request body:
    /// { "apiKey": "..." }.
    ///
    /// Validation is handled automatically by FluentValidation (see TestApiKeyRequestValidator).
    ///
    /// This endpoint appears redundant with the validation in POST /api/steam-api-keys, but both
    /// are intentionally kept for UX purposes: /test allows users to test a key without saving it
    /// (try before commit), and POST (save) validates as a safety check before persisting. Both
    /// endpoints are actively used by the frontend setup wizard and settings modal.
    /// </remarks>
    [HttpPost("test")]
    [ProducesResponseType(typeof(ApiKeyTestResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiKeyTestResponse>> TestKeyAsync([FromBody] TestApiKeyRequest request)
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
    /// Saves a Steam Web API key with encrypted storage.
    /// </summary>
    /// <remarks>
    /// POST is the proper method here for creating/saving resources. Request body:
    /// { "apiKey": "..." }.
    ///
    /// Validation is handled automatically by FluentValidation (see SaveApiKeyRequestValidator).
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(ApiKeySaveResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<ApiKeySaveResponse>> SaveKeyAsync([FromBody] SaveApiKeyRequest request)
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
    /// Removes the configured Steam Web API key.
    /// </summary>
    /// <remarks>
    /// DELETE is the proper method here for removing resources.
    /// </remarks>
    [HttpDelete("current")]
    [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
    public ActionResult<MessageResponse> RemoveKey()
    {
        _steamWebApiService.RemoveApiKey();

        _logger.LogInformation("Steam Web API key removed successfully");

        return Ok(MessageResponse.Ok("Steam Web API key removed successfully"));
    }
}
