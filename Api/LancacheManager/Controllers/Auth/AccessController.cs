using LancacheManager.Core.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Security;
using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AccessController : ControllerBase
{
    private readonly AccessService _accessService;
    private readonly ApiKeyService _apiKeyService;
    private readonly AccountClaimWindow _claimWindow;
    private readonly OidcChallengeStore _challenges;
    private readonly SessionService? _sessionService;
    private readonly IOptionsMonitorCache<OpenIdConnectOptions> _oidcOptions;
    private readonly IOptionsMonitorCache<OAuthOptions> _oauthOptions;

    public AccessController(
        AccessService accessService,
        ApiKeyService apiKeyService,
        AccountClaimWindow claimWindow,
        OidcChallengeStore challenges,
        IOptionsMonitorCache<OpenIdConnectOptions> oidcOptions,
        IOptionsMonitorCache<OAuthOptions> oauthOptions,
        SessionService? sessionService = null)
    {
        _accessService = accessService;
        _apiKeyService = apiKeyService;
        _claimWindow = claimWindow;
        _challenges = challenges;
        _sessionService = sessionService;
        _oidcOptions = oidcOptions;
        _oauthOptions = oauthOptions;
    }

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("setup")]
    [ProducesResponseType(typeof(AccessSetupResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AccessSetupResponse>> SetUpAsync([FromBody] AccessSetupRequest request)
    {
        if (!_apiKeyService.ValidateApiKey(request.ApiKey))
        {
            return Unauthorized(ApiResponse.Error("A valid API key is required"));
        }

        var session = HttpContext.GetUserSession();
        var recovery = request.Recovery
            && _accessService.AllowsKeyRecovery()
            && _claimWindow.IsRecoveryOpen;
        // With sign-in already off every browser holds full admin, so the main-admin check guards nothing
        // for the switch to unauthenticated access, and an account left from an earlier mode with no main
        // admin would otherwise block the wipe for good. The confirm with the count still gates the delete.
        var wipeWithSignInOff = request.Mode == AccountMode.Unauthenticated && !_accessService.IsAuthenticationEnabled();
        if (!recovery
            && !wipeWithSignInOff
            && await _accessService.RequiresMainAdminAsync()
            && !await _accessService.IsMainAdminAsync(session))
        {
            return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.Error("The main administrator must authorize this change"));
        }

        if (request.Mode is AccountMode.Password or AccountMode.ApiKeyPassword
            && !await _accessService.CanUsePasswordAsync())
        {
            return Conflict(ApiResponse.Error("Set local credentials for the main administrator before selecting password sign-in"));
        }

        // Unauthenticated access keeps no accounts, so the switch deletes them first, once the person has
        // confirmed it. Done before the mode is saved: a delete that fails leaves sign-in on, and a
        // request the save would refuse deletes nothing. A save that fails after the delete leaves sign-in
        // on with no account, the first-admin state; the same Confirm finishes the switch, since with no
        // account left it skips the confirm and the delete.
        if (request.Mode == AccountMode.Unauthenticated)
        {
            _accessService.ValidateSetupRequest(request);
            // An account an environment login allows is deleted with the rest: with sign-in off nobody can
            // sign in to it, and that login's next sign-in creates it again once sign-in is back on.
            var accountIds = await _accessService.AccountIdsAsync();
            // The confirm names the count it showed. A request with no count, or with a different count
            // than the table holds now, asks again with the current number, so the person never confirms
            // a count other than the one deleted.
            if (accountIds.Count > 0 && request.ConfirmedAccountCount != accountIds.Count)
            {
                return Conflict(new AccountRefusalResponse
                {
                    StageKey = AccountRefusalResponse.WipeConfirmRequired,
                    Error = $"Turning sign-in off deletes all {accountIds.Count} accounts. Confirm to continue",
                    Context = new Dictionary<string, object?> { ["count"] = accountIds.Count }
                });
            }
            if (accountIds.Count > 0)
            {
                await _accessService.DeleteEveryAccountAsync(session?.AccountId, session?.Id, HttpContext.RequestServices);
            }
        }

        var response = _accessService.Apply(request, session?.AccountId);
        if (!response.RequiresLoginTest
            && _accessService.IsAuthenticationEnabled()
            && _sessionService is not null
            && await _sessionService.RetireSharedAdminSessionAsync() is { } retired)
        {
            // Every browser now has to sign in under a session of its own, so no page can reach the prefill the
            // shared session started. A stop that fails is logged by the stop and stays listed for an admin.
            await PrefillDaemonServiceBase.TerminateGuestSessionsAsync(PrefillDaemonServiceBase.ResolveAllDaemons(HttpContext.RequestServices),
                retired, "Shared session retired", PrefillDaemonServiceBase.AccessEndedReasonKey, retired.ToString());
        }
        // Sign-in back on with no account: the first-admin wizard comes next, and the hour it may create the
        // account in has usually passed since the start or since the accounts were deleted.
        if (!response.RequiresLoginTest
            && _accessService.IsAuthenticationEnabled()
            && !await _accessService.RequiresMainAdminAsync())
        {
            _claimWindow.Reopen();
        }
        if (recovery && !response.RequiresOidcTest)
        {
            _claimWindow.Expire();
        }
        response.CallbackUrls = response.CallbackUrls
            .Select(path => $"{Request.Scheme}://{Request.Host}{Request.PathBase}{path}")
            .ToList();
        ClearOptions();
        return Ok(response);
    }

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [IgnoreAntiforgeryToken]
    [HttpPost("oidc/start")]
    [ProducesResponseType(typeof(OidcStartResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OidcStartResponse>> StartOidcAsync([FromBody] OidcStartRequest request)
    {
        var loginId = _accessService.GetCompatibleLoginId(request.Setup);
        if (loginId is null)
        {
            return Conflict(ApiResponse.Error("A login service must be selected"));
        }
        return await StartLoginCoreAsync(new LoginStartRequest
        {
            LoginId = loginId,
            ApiKey = request.ApiKey,
            Setup = request.Setup,
            Recovery = request.Recovery,
            Owner = request.Owner
        });
    }

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [IgnoreAntiforgeryToken]
    [HttpPost("login/start")]
    [ProducesResponseType(typeof(OidcStartResponse), StatusCodes.Status200OK)]
    public Task<ActionResult<OidcStartResponse>> StartLoginAsync([FromBody] LoginStartRequest request)
        => StartLoginCoreAsync(request);

    private async Task<ActionResult<OidcStartResponse>> StartLoginCoreAsync(LoginStartRequest request)
    {
        var settings = _accessService.GetLoginSettings(request.LoginId, request.Setup);
        if (settings is null)
        {
            return Conflict(ApiResponse.Error("Login service settings are not available"));
        }

        if (request.Setup)
        {
            if (!_apiKeyService.ValidateApiKey(request.ApiKey))
            {
                return Unauthorized(ApiResponse.Error("A valid pending login setup and API key are required"));
            }

            var recovery = request.Recovery
                && _accessService.AllowsKeyRecovery()
                && _claimWindow.IsRecoveryOpen;
            if (!recovery
                && await _accessService.RequiresMainAdminAsync()
                && !await _accessService.IsMainAdminAsync(HttpContext.GetUserSession()))
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.Error("The main administrator must authorize this change"));
            }
        }
        else
        {
            var ownerSignIn = request.Owner && settings.OwnerSubject is not null;
            // With sign-in off every browser already has full admin and no account may exist.
            if (!_accessService.IsAuthenticationEnabled()
                || _accessService.IsSetupRequired()
                || (!_accessService.UsesOidc() && !ownerSignIn))
            {
                return Conflict(ApiResponse.Error("External sign-in is not available"));
            }

            var keyRequired = _accessService.RequiresApiKeyForOidc()
                || (ownerSignIn && _accessService.RequiresApiKeyForPassword());
            if (keyRequired
                && !_apiKeyService.ValidateApiKey(request.ApiKey))
            {
                return Unauthorized(ApiResponse.Error("A valid API key is required"));
            }
        }

        var host = Request.Host.Host;
        var hostIsAddress = IPAddress.TryParse(host, out var address);
        var loopback = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (hostIsAddress && IPAddress.IsLoopback(address!));
        // Google and Microsoft accept plain HTTP callbacks only on loopback, and Apple only an HTTPS
        // domain. A custom OpenID Connect service and GitHub may run on plain HTTP on the LAN.
        if ((settings.Kind is LoginKind.Google or LoginKind.Microsoft && !Request.IsHttps && !loopback)
            || (settings.Kind == LoginKind.Apple
                && (!Request.IsHttps
                    || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                    || hostIsAddress)))
        {
            return Conflict(ApiResponse.Error(settings.Kind == LoginKind.Apple
                ? "Apple sign-in requires an HTTPS domain address"
                : "External sign-in requires an HTTPS application address"));
        }

        var scheme = AccessService.GetScheme(settings.Kind, request.Setup);
        ClearOptions(scheme);
        var token = _challenges.Create(settings.Revision, request.Setup, request.Owner, settings.Id);
        if (request.Setup
            && request.Recovery
            && _accessService.AllowsKeyRecovery()
            && _claimWindow.IsRecoveryOpen)
        {
            _claimWindow.Expire();
        }
        return Ok(new OidcStartResponse
        {
            Url = $"/api/auth/login/challenge?token={Uri.EscapeDataString(token)}"
        });
    }

    [AllowAnonymous]
    [HttpGet("oidc/challenge")]
    [HttpGet("login/challenge")]
    public IActionResult GetOidcChallenge([FromQuery] string? token)
    {
        var challenge = _challenges.Take(token);
        var settings = challenge is null
            ? null
            : _accessService.GetLoginSettings(challenge.LoginId, challenge.Setup);
        if (challenge is null || settings is null || settings.Revision != challenge.Revision)
        {
            return BadRequest(ApiResponse.Error("OIDC sign-in request expired"));
        }

        var properties = new AuthenticationProperties
        {
            RedirectUri = "/",
            // The one stored start time of this attempt; a failed callback compares it with the
            // handler's RemoteAuthenticationTimeout to tell an expired attempt from a broken one.
            IssuedUtc = DateTimeOffset.UtcNow
        };
        properties.Items["access_revision"] = challenge.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        properties.Items["access_setup"] = challenge.Setup ? "true" : "false";
        properties.Items["access_owner"] = challenge.Owner ? "true" : "false";
        // A setup that started with sign-in off is the only sign-in that may finish while sign-in is off.
        properties.Items["access_sign_in_off"] = _accessService.IsAuthenticationEnabled() ? "false" : "true";
        properties.Items["login_id"] = challenge.LoginId;
        return Challenge(properties, AccessService.GetScheme(settings.Kind, challenge.Setup));
    }

    /// <summary>
    /// Completes an Apple sign-in. Apple's callback is a cross-site form post that carries no session
    /// cookie, so its handler keeps the verified identity in the Apple cookie and redirects here; this
    /// top-level GET carries the session cookie the browser holds now, and the sign-in ends that session.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("login/apple/complete")]
    public async Task<IActionResult> CompleteAppleSignInAsync([FromServices] OidcEvents events)
    {
        var verified = await HttpContext.AuthenticateAsync(AccessService.AppleCookieScheme);
        await HttpContext.SignOutAsync(AccessService.AppleCookieScheme);
        return Redirect(await events.CompleteSignInAsync(verified.Succeeded ? verified.Properties : null, HttpContext));
    }

    [EnableRateLimiting("auth")]
    [HttpDelete("login-services/{id}")]
    public async Task<IActionResult> RemoveLoginAsync(
        [FromRoute] string id,
        [FromBody] RemoveLoginRequest request)
    {
        if (!_apiKeyService.ValidateApiKey(request.ApiKey))
        {
            return Unauthorized(ApiResponse.Error("A valid API key is required"));
        }
        if (!await _accessService.IsMainAdminAsync(HttpContext.GetUserSession()))
        {
            return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.Error("The main administrator must authorize this change"));
        }
        if (!_accessService.RemoveLogin(id))
        {
            return NotFound(ApiResponse.Error("Login service was not found"));
        }

        ClearOptions();
        return NoContent();
    }

    private void ClearOptions(string? scheme = null)
    {
        if (scheme is not null)
        {
            if (scheme is AccessService.GitHubScheme or AccessService.GitHubSetupScheme)
            {
                _oauthOptions.TryRemove(scheme);
            }
            else
            {
                _oidcOptions.TryRemove(scheme);
            }
            return;
        }

        foreach (var loginKind in Enum.GetValues<LoginKind>())
        {
            var active = AccessService.GetScheme(loginKind, setup: false);
            var setup = AccessService.GetScheme(loginKind, setup: true);
            if (loginKind == LoginKind.GitHub)
            {
                _oauthOptions.TryRemove(active);
                _oauthOptions.TryRemove(setup);
            }
            else
            {
                _oidcOptions.TryRemove(active);
                _oidcOptions.TryRemove(setup);
            }
        }
    }
}
