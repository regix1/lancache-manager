using LancacheManager.Middleware;
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
[Authorize(Policy = "AccountHolder")]
public class ApiKeysController : ControllerBase
{
    private readonly ApiKeyService _apiKeyService;
    private readonly SteamKit2Service _steamKit2Service;
    private readonly SteamAuthStorageService _steamAuthStorage;
    private readonly StateService _stateService;
    private readonly SessionService _sessionService;
    private readonly IdentityAuditService _identityAuditService;
    private readonly IConfiguration _configuration;
    private readonly AuthenticationHelper _authenticationHelper;
    private readonly ILogger<ApiKeysController> _logger;

    public ApiKeysController(
        ApiKeyService apiKeyService,
        SteamKit2Service steamKit2Service,
        SteamAuthStorageService steamAuthStorage,
        StateService stateService,
        SessionService sessionService,
        IdentityAuditService identityAuditService,
        IConfiguration configuration,
        AuthenticationHelper authenticationHelper,
        ILogger<ApiKeysController> logger)
    {
        _apiKeyService = apiKeyService;
        _steamKit2Service = steamKit2Service;
        _steamAuthStorage = steamAuthStorage;
        _stateService = stateService;
        _sessionService = sessionService;
        _identityAuditService = identityAuditService;
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
    ///
    /// It also ends every signed-in session, the caller's own included, because each of them was
    /// opened against the key that has just stopped being valid. The new key travels in the response
    /// so the caller can read it on the way out. [48][49]
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

        // Built before anything is revoked. The caller signs in with the key, so the session making
        // this request is one of the ones about to end, and a rotation that only confirms itself
        // leaves that admin outside the screen that was going to show them the key they now need.
        // [49]
        var response = new ApiKeyRegenerateResponse
        {
            Success = true,
            Message = "API key regenerated successfully.",
            ApiKey = newKey,
            Warning = "Every signed-in session was ended. Sign in again with this key."
        };

        // Read while the rows still exist. An X-Api-Key caller and a caller running with
        // authentication disabled both run as a shared session that belongs to no account, so the
        // account half of the actor is null for them. [29c]
        var session = HttpContext.GetUserSession();
        await _identityAuditService.RecordAsync(
            IdentityAuditEvent.ApiKeyRotated,
            session?.AccountId,
            session?.Id,
            targetAccountId: null);

        // Guests included: every one of these sessions was opened against the key that has just
        // stopped being valid. Only the session rows go, so every account can sign in again with the
        // new key. [48]
        var endedSessions = await _sessionService.ClearAllSessionsAsync();

        _logger.LogWarning(
            "API key regenerated | Sessions ended: {EndedSessions} | Steam PICS: {SteamLogout} | Steam Web API Key: {WebApiKey}",
            endedSessions,
            steamWasAuthenticated ? "Logged out" : "Cleared",
            hadSteamWebApiKey ? "Removed" : "None");

        return Ok(response);
    }
}
