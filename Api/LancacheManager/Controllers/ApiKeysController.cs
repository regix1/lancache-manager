using LancacheManager.Models;
using LancacheManager.Security;
using LancacheManager.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using LancacheManager.Core.Services.SteamKit2;
using Microsoft.AspNetCore.Authorization;


namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for API key management
/// Handles API key retrieval, regeneration, and validation
/// </summary>
[ApiController]
[Route("api/api-keys")]
[Authorize(Policy = "AdminOnly")]
public class ApiKeysController : ControllerBase
{
    private readonly ApiKeyService _apiKeyService;
    private readonly SteamKit2Service _steamKit2Service;
    private readonly SteamAuthStorageService _steamAuthStorage;
    private readonly StateService _stateService;
    private readonly IConfiguration _configuration;
    private readonly AuthenticationHelper _authenticationHelper;
    private readonly ILogger<ApiKeysController> _logger;

    public ApiKeysController(
        ApiKeyService apiKeyService,
        SteamKit2Service steamKit2Service,
        SteamAuthStorageService steamAuthStorage,
        StateService stateService,
        IConfiguration configuration,
        AuthenticationHelper authenticationHelper,
        ILogger<ApiKeysController> logger)
    {
        _apiKeyService = apiKeyService;
        _steamKit2Service = steamKit2Service;
        _steamAuthStorage = steamAuthStorage;
        _stateService = stateService;
        _configuration = configuration;
        _authenticationHelper = authenticationHelper;
        _logger = logger;
    }

    /// <summary>
    /// Reports whether the request's API key is a valid admin key.
    /// </summary>
    /// <remarks>
    /// Does not echo the key itself back. Has no UI caller today; it exists as a standalone check
    /// an API key holder can run before trying the key against another endpoint.
    /// </remarks>
    [HttpGet("status")]
    [ProducesResponseType(typeof(ApiKeyStatusResponse), StatusCodes.Status200OK)]
    public ActionResult<ApiKeyStatusResponse> GetStatus()
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
    /// Regenerates the admin API key.
    /// </summary>
    /// <remarks>
    /// This logs out all Steam sessions and revokes all device registrations. When authentication
    /// is disabled, the current API key is still required so an open authorization policy cannot
    /// rotate the credential that protects database recovery.
    /// </remarks>
    [HttpPost("regenerate")]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(ApiKeyRegenerateResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> RegenerateApiKeyAsync()
    {
        if (!_configuration.GetValue<bool>("Security:EnableAuthentication", true))
        {
            var apiKeyResult = _authenticationHelper.ValidateApiKey(HttpContext);
            if (!apiKeyResult.IsAuthenticated)
            {
                return StatusCode(
                    apiKeyResult.StatusCode,
                    ApiResponse.Error(apiKeyResult.ErrorMessage ?? "API key required"));
            }
        }

        // SECURITY: Clear ALL Steam-related data when API key is regenerated
        var steamWasAuthenticated = _stateService.GetSteamAuthMode() == SteamAuthMode.Authenticated;
        var hadSteamWebApiKey = !string.IsNullOrWhiteSpace(_steamAuthStorage.GetAuthData().SteamApiKey);

        // Clear Steam auth data (with error handling to ensure API key regen completes)
        try
        {
            await _steamKit2Service.ClearAllSteamAuthAsync();
        }
        catch (Exception steamEx)
        {
            _logger.LogWarning(steamEx, "Error clearing Steam auth during API key regeneration (continuing anyway)");
        }

        var (oldKey, newKey) = _apiKeyService.RegenerateApiKey();
        _apiKeyService.DisplayApiKey(_configuration);

        _logger.LogWarning(
            "API key regenerated | Steam PICS: {SteamLogout} | Steam Web API Key: {WebApiKey}",
            steamWasAuthenticated ? "Logged out" : "Cleared",
            hadSteamWebApiKey ? "Removed" : "None");

        return Ok(new ApiKeyRegenerateResponse
        {
            Success = true,
            Message = "API key regenerated successfully.",
            Warning = "Check container logs for the new API key."
        });
    }
}
