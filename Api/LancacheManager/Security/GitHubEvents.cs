using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Options;

namespace LancacheManager.Security;

public sealed class GitHubEvents : OAuthEvents
{
    private const string Issuer = "https://github.com";
    private readonly AccessService _accessService;
    private readonly ExternalSignInService _signInService;
    private readonly IOptionsMonitorCache<OAuthOptions> _oauthOptions;
    private readonly ILogger<GitHubEvents> _logger;

    public GitHubEvents(
        AccessService accessService,
        ExternalSignInService signInService,
        IOptionsMonitorCache<OAuthOptions> oauthOptions,
        ILogger<GitHubEvents> logger)
    {
        _accessService = accessService;
        _signInService = signInService;
        _oauthOptions = oauthOptions;
        _logger = logger;
    }

    public override async Task CreatingTicket(OAuthCreatingTicketContext context)
    {
        if (context.Properties is not { } properties
            || !TryReadTransaction(properties, out var loginId, out var revision, out var setup, out _)
            || !string.Equals(loginId, "github", StringComparison.Ordinal)
            || _accessService.GetLoginSettings(loginId, setup) is not { Kind: LoginKind.GitHub } settings
            || settings.Revision != revision
            || !AccessService.TryReadScheme(context.Scheme.Name, out var schemeKind, out var schemeSetup)
            || schemeKind != LoginKind.GitHub
            || schemeSetup != setup
            || string.IsNullOrWhiteSpace(context.AccessToken))
        {
            context.Fail("GitHub transaction state is missing");
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("LancacheManager/1.10.7");
        using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
        if (!response.IsSuccessStatusCode)
        {
            context.Fail("GitHub did not return a valid user");
            return;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted);
        JsonDocument user;
        try
        {
            user = await JsonDocument.ParseAsync(stream, cancellationToken: context.HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
            context.Fail("GitHub returned an invalid user response");
            return;
        }
        using (user)
        {
            if (user.RootElement.ValueKind != JsonValueKind.Object
                || !user.RootElement.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.Number
                || !id.TryGetInt64(out var userId)
                || userId <= 0)
            {
                context.Fail("GitHub returned an invalid user response");
                return;
            }

            var subject = userId.ToString(CultureInfo.InvariantCulture);
            context.Identity?.AddClaim(new Claim(ClaimTypes.NameIdentifier, subject));
            if (user.RootElement.TryGetProperty("login", out var login)
                && login.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(login.GetString()))
            {
                context.Identity?.AddClaim(new Claim(ClaimTypes.Name, login.GetString()!));
            }
        }
    }

    public override async Task TicketReceived(TicketReceivedContext context)
    {
        var setup = false;
        var ownerOnly = false;
        if (context.Properties is not { } properties
            || !TryReadTransaction(properties, out var loginId, out var revision, out setup, out ownerOnly)
            || context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier) is not { } subject
            || !_accessService.IdentityAllowed(loginId, revision, setup, ownerOnly, Issuer, subject))
        {
            RedirectFailure(context, "identity", setup || ownerOnly);
            return;
        }

        using var setupLease = setup
            ? await _signInService.EnterSetupAsync(context.HttpContext.RequestAborted)
            : null;
        var settings = _accessService.GetLoginSettings(loginId, setup);
        if (settings is null || settings.Revision != revision)
        {
            RedirectFailure(context, "expired", setup || ownerOnly);
            return;
        }

        var deferOwner = setup && _accessService.CanDeferOidcAccount(revision);
        var signIn = deferOwner
            ? null
            : await _signInService.TryCreateSessionAsync(
                settings,
                setup || IsOwner(settings, subject),
                Issuer,
                subject,
                context.HttpContext);

        if (signIn is null && !deferOwner)
        {
            RedirectFailure(context, "unavailable", setup || ownerOnly);
            return;
        }

        if (setup)
        {
            bool promoted;
            try
            {
                promoted = await _signInService.CompleteSetupAsync(
                    signIn,
                    () => _accessService.PromotePendingLogin(loginId, revision, Issuer, subject, signIn?.AccountId));
            }
            catch (ServiceUnavailableException ex)
            {
                _logger.LogWarning(ex, "GitHub settings could not be saved after verification");
                RedirectFailure(context, "unavailable", returnToSetup: true);
                return;
            }
            if (!promoted)
            {
                RedirectFailure(context, "expired", returnToSetup: true);
                return;
            }
            _oauthOptions.TryRemove(AccessService.GitHubScheme);
            _oauthOptions.TryRemove(AccessService.GitHubSetupScheme);
        }

        if (signIn is not null)
        {
            _signInService.SetSessionCookie(context.HttpContext, signIn);
        }

        context.HandleResponse();
        context.Response.Redirect(setup
            ? $"/?accessSetup=1&loginTest=success&loginId={Uri.EscapeDataString(loginId)}"
            : ownerOnly ? "/?accessSetup=1" : "/");
    }

    public override Task RemoteFailure(RemoteFailureContext context)
    {
        _logger.LogWarning(context.Failure, "GitHub sign-in failed");
        context.HandleResponse();
        var returnToSetup = context.Properties is { } properties
            && TryReadTransaction(properties, out _, out _, out var setup, out var ownerOnly)
            && (setup || ownerOnly);
        var code = FailureCode(context.Failure);
        context.Response.Redirect(returnToSetup
            ? $"/?accessSetup=1&oidcError={code}"
            : $"/?oidcError={code}");
        return Task.CompletedTask;
    }

    private static bool IsOwner(OidcSettings settings, string subject)
        => string.Equals(settings.OwnerIssuer, Issuer, StringComparison.Ordinal)
            && string.Equals(settings.OwnerSubject, subject, StringComparison.Ordinal);

    private static bool TryReadTransaction(
        AuthenticationProperties properties,
        out string loginId,
        out long revision,
        out bool setup,
        out bool ownerOnly)
    {
        loginId = properties.Items.TryGetValue("login_id", out var id) ? id ?? string.Empty : string.Empty;
        revision = 0;
        setup = properties.Items.TryGetValue("access_setup", out var setupValue)
            && string.Equals(setupValue, "true", StringComparison.Ordinal);
        ownerOnly = properties.Items.TryGetValue("access_owner", out var ownerValue)
            && string.Equals(ownerValue, "true", StringComparison.Ordinal);
        return properties.Items.TryGetValue("access_revision", out var revisionValue)
            && long.TryParse(revisionValue, NumberStyles.None, CultureInfo.InvariantCulture, out revision);
    }

    private static void RedirectFailure(TicketReceivedContext context, string code, bool returnToSetup)
    {
        context.HandleResponse();
        context.Response.Redirect(returnToSetup
            ? $"/?accessSetup=1&oidcError={code}"
            : $"/?oidcError={code}");
    }

    private static string FailureCode(Exception? failure)
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException or IOException)
            {
                return "connection";
            }
        }

        var message = failure?.Message ?? string.Empty;
        if (message.StartsWith("GitHub did not return", StringComparison.Ordinal)
            || message.StartsWith("GitHub returned", StringComparison.Ordinal))
        {
            return "identity";
        }
        if (message.Contains("transaction state", StringComparison.Ordinal)
            || message.Contains("correlation", StringComparison.OrdinalIgnoreCase)
            || message.Contains("state", StringComparison.OrdinalIgnoreCase))
        {
            return "expired";
        }
        return "authentication";
    }
}
