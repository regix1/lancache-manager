using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Security;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Core.Services.Xbox;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;


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
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ApiKeyService _apiKeyService;
    private readonly SteamKit2Service _steamKit2Service;
    private readonly XboxCatalogMappingService _xboxCatalogMappingService;
    private readonly EpicMappingService _epicMappingService;
    private readonly SteamAuthStorageService _steamAuthStorage;
    private readonly XboxAuthStorageService _xboxAuthStorage;
    private readonly EpicAuthStorageService _epicAuthStorage;
    private readonly StateService _stateService;
    private readonly SessionService _sessionService;
    private readonly IdentityAuditService _identityAuditService;
    private readonly IConfiguration _configuration;
    private readonly AuthenticationHelper _authenticationHelper;
    private readonly ILogger<ApiKeysController> _logger;
    private readonly AccessService? _accessService;

    public ApiKeysController(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ApiKeyService apiKeyService,
        SteamKit2Service steamKit2Service,
        XboxCatalogMappingService xboxCatalogMappingService,
        EpicMappingService epicMappingService,
        SteamAuthStorageService steamAuthStorage,
        XboxAuthStorageService xboxAuthStorage,
        EpicAuthStorageService epicAuthStorage,
        StateService stateService,
        SessionService sessionService,
        IdentityAuditService identityAuditService,
        IConfiguration configuration,
        AuthenticationHelper authenticationHelper,
        ILogger<ApiKeysController> logger,
        AccessService? accessService = null)
    {
        _dbContextFactory = dbContextFactory;
        _apiKeyService = apiKeyService;
        _steamKit2Service = steamKit2Service;
        _xboxCatalogMappingService = xboxCatalogMappingService;
        _epicMappingService = epicMappingService;
        _steamAuthStorage = steamAuthStorage;
        _xboxAuthStorage = xboxAuthStorage;
        _epicAuthStorage = epicAuthStorage;
        _stateService = stateService;
        _sessionService = sessionService;
        _identityAuditService = identityAuditService;
        _configuration = configuration;
        _authenticationHelper = authenticationHelper;
        _logger = logger;
        _accessService = accessService;
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
                HasApiKey = false
            });
        }

        var isValid = _apiKeyService.ValidateApiKey(apiKey);

        return Ok(new ApiKeyStatusResponse
        {
            HasApiKey = isValid
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
    /// so the caller can read it on the way out.
    ///
    /// With authentication on, the account that owns the installation is the only one that may ask
    /// for it. Rotating signs every other person out and hands the new credential to whoever asked,
    /// so an ordinary administrator holding it could lock the rest of them out. The check reads the
    /// caller's stored account row, because the claim on the cookie says "admin" for every
    /// administrator and a check that read it would admit all of them.
    ///
    /// With authentication on it is also how the owner takes the installation back: every other
    /// account is deleted along with the sessions, and the owning account keeps its username and
    /// password so it signs back in with those and the new key.
    ///
    /// Steam, Xbox, and Epic sign-ins are cleared with the key, including the stored Steam Web API
    /// key. The owner signs those platforms back in after they sign back into the app.
    /// </remarks>
    [HttpPost("regenerate")]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(ApiKeyRegenerateResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> RegenerateApiKeyAsync()
    {
        var accessConfigured = _accessService is not null && !_accessService.IsSetupRequired();
        var authenticationEnabled = accessConfigured
            || _configuration.GetValue<bool>("Security:EnableAuthentication", true);
        if (!authenticationEnabled)
        {
            var apiKeyResult = _authenticationHelper.ValidateApiKey(HttpContext);
            if (!apiKeyResult.IsAuthenticated)
            {
                return StatusCode(
                    apiKeyResult.StatusCode,
                    ApiResponse.Error(apiKeyResult.ErrorMessage ?? "API key required"));
            }
        }
        else
        {
            // A guest never reaches here, and neither does a caller holding only the key: with
            // authentication on the header stands in for a session on the API reference alone
            // (SessionAuthenticationHandler.cs:65-73). What is left is an account holder, and only
            // one account row answers true.
            var callerSession = HttpContext.GetUserSession();
            var ownsInstallation = false;
            if (callerSession?.AccountId is { } callerAccountId)
            {
                await using var accounts = await _dbContextFactory.CreateDbContextAsync();
                ownsInstallation = await MainAdminVisibility.OwnsInstallationAsync(accounts, callerAccountId);
            }

            if (!ownsInstallation)
            {
                return StatusCode(
                    StatusCodes.Status403Forbidden,
                    ApiResponse.Error("Only the account that owns this installation can rotate the API key."));
            }
        }

        var steamWasAuthenticated = _stateService.GetSteamAuthMode() == SteamAuthMode.Authenticated;
        var hadSteamWebApiKey = !string.IsNullOrWhiteSpace(_steamAuthStorage.GetAuthData().SteamApiKey);
        var hadXbox = _xboxAuthStorage.HasSavedCredentials();
        var hadEpic = _epicAuthStorage.HasSavedCredentials();

        // Each platform clear is best-effort so a failure on one cannot leave the new key unissued.
        // Logout drops the in-memory session; the storage wipe is what removes the files if logout
        // does not run (tests leave the mapping services unbuilt, same as Steam).
        try
        {
            await _steamKit2Service.ClearAllSteamAuthAsync();
        }
        catch (Exception steamEx)
        {
            _logger.LogWarning(steamEx, "Error clearing Steam auth during API key regeneration (continuing anyway)");
        }

        try
        {
            await _xboxCatalogMappingService.LogoutAsync();
        }
        catch (Exception xboxEx)
        {
            _logger.LogWarning(xboxEx, "Error clearing Xbox auth during API key regeneration (continuing anyway)");
        }

        try
        {
            await _epicMappingService.LogoutAsync();
        }
        catch (Exception epicEx)
        {
            _logger.LogWarning(epicEx, "Error clearing Epic auth during API key regeneration (continuing anyway)");
        }

        try
        {
            _xboxAuthStorage.ClearAuthData();
        }
        catch (Exception xboxStorageEx)
        {
            _logger.LogWarning(xboxStorageEx, "Error clearing Xbox credentials during API key regeneration (continuing anyway)");
        }

        try
        {
            _epicAuthStorage.ClearAuthData();
        }
        catch (Exception epicStorageEx)
        {
            _logger.LogWarning(epicStorageEx, "Error clearing Epic credentials during API key regeneration (continuing anyway)");
        }

        var (oldKey, newKey) = _apiKeyService.RegenerateApiKey();
        _apiKeyService.DisplayApiKey(revealKey: true);

        // Built before anything is revoked. The caller signs in with the key, so the session making
        // this request is one of the ones about to end, and a rotation that only confirms itself
        // leaves that admin outside the screen that was going to show them the key they now need.
        var response = new ApiKeyRegenerateResponse
        {
            Success = true,
            Message = "API key regenerated successfully.",
            ApiKey = newKey,
            Warning = "Every signed-in session was ended. Sign in again with this key."
        };

        // Read while the rows still exist. An X-Api-Key caller and a caller running with
        // authentication disabled both run as a shared session that belongs to no account, so the
        // account half of the actor is null for them.
        var session = HttpContext.GetUserSession();
        await _identityAuditService.RecordAsync(
            IdentityAuditEvent.ApiKeyRotated,
            session?.AccountId,
            session?.Id,
            targetAccountId: null);

        // Guests included: every one of these sessions was opened against the key that has just
        // stopped being valid. Only the session rows go, so every account can sign in again with the
        // new key.
        var endedSessions = await _sessionService.ClearAllSessionsAsync();

        // The sessions are already gone, so these rows take nothing live with them. Only the branch
        // above that identified the caller as the owning account does this: on the other branch the
        // caller has presented nothing but the key and is nobody in particular, and deleting other
        // people's accounts is a wider power than rotating a credential.
        var removedAccounts = 0;
        if (authenticationEnabled)
        {
            await using var accounts = await _dbContextFactory.CreateDbContextAsync();
            var otherAccounts = await accounts.UserAccounts.Where(a => !a.IsMainAdmin).ToListAsync();
            _accessService?.ForgetAccounts(otherAccounts.Select(account => account.Id));
            accounts.UserAccounts.RemoveRange(otherAccounts);
            await accounts.SaveChangesAsync();

            foreach (var removed in otherAccounts)
            {
                await _identityAuditService.RecordAsync(
                    IdentityAuditEvent.AccountDeleted,
                    session?.AccountId,
                    session?.Id,
                    removed.Id);
            }

            removedAccounts = otherAccounts.Count;
        }

        _logger.LogWarning(
            "API key regenerated | Sessions ended: {EndedSessions} | Accounts removed: {RemovedAccounts} | Steam PICS: {SteamLogout} | Steam Web API Key: {WebApiKey} | Xbox: {Xbox} | Epic: {Epic}",
            endedSessions,
            removedAccounts,
            steamWasAuthenticated ? "Logged out" : "Cleared",
            hadSteamWebApiKey ? "Removed" : "None",
            hadXbox ? "Cleared" : "None",
            hadEpic ? "Cleared" : "None");

        return Ok(response);
    }
}
