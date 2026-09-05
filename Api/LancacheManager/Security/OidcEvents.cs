using System.Globalization;
using System.Security.Claims;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace LancacheManager.Security;

public sealed class OidcEvents : OpenIdConnectEvents
{
    private readonly AccessService _accessService;
    private readonly ExternalSignInService _signInService;
    private readonly IOptionsMonitorCache<OpenIdConnectOptions> _oidcOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OidcEvents> _logger;

    public OidcEvents(
        AccessService accessService,
        ExternalSignInService signInService,
        IOptionsMonitorCache<OpenIdConnectOptions> oidcOptions,
        TimeProvider timeProvider,
        ILogger<OidcEvents> logger)
    {
        _accessService = accessService;
        _signInService = signInService;
        _oidcOptions = oidcOptions;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public override Task AuthorizationCodeReceived(AuthorizationCodeReceivedContext context)
    {
        if (context.Properties is { } properties
            && TryReadTransaction(properties, out var loginId, out var revision, out var setup, out _)
            && _accessService.GetLoginSettings(loginId, setup) is { Kind: LoginKind.Apple } settings
            && settings.Revision == revision
            && context.TokenEndpointRequest is { } tokenRequest)
        {
            tokenRequest.ClientSecret = AppleClientSecret.Create(
                settings,
                _timeProvider.GetUtcNow());
        }
        return Task.CompletedTask;
    }

    public override Task TokenValidated(TokenValidatedContext context)
    {
        if (context.Properties is not { } properties
            || !TryReadTransaction(properties, out var loginId, out var revision, out var setup, out var ownerOnly)
            || _accessService.GetLoginSettings(loginId, setup) is not { } settings
            || settings.Revision != revision
            || !AccessService.TryReadScheme(context.Scheme.Name, out var schemeKind, out var schemeSetup)
            || schemeKind != settings.Kind
            || schemeSetup != setup)
        {
            context.Fail("OIDC transaction state is missing");
            return Task.CompletedTask;
        }

        var issuer = context.SecurityToken.Issuer;
        var subject = Subject(settings, context.Principal, issuer);
        if (string.IsNullOrWhiteSpace(issuer)
            || string.IsNullOrWhiteSpace(subject)
            || !_accessService.IdentityAllowed(loginId, revision, setup, ownerOnly, issuer, subject))
        {
            context.Fail("OIDC identity is not allowed");
            return Task.CompletedTask;
        }

        properties.Items["login_id"] = loginId;
        properties.Items["oidc_issuer"] = issuer;
        properties.Items["oidc_subject"] = subject;
        return Task.CompletedTask;
    }

    public override async Task TicketReceived(TicketReceivedContext context)
    {
        var setup = false;
        var ownerOnly = false;
        if (context.Properties is not { } properties
            || !TryReadTransaction(properties, out var loginId, out var revision, out setup, out ownerOnly)
            || !properties.Items.TryGetValue("oidc_issuer", out var issuer)
            || !properties.Items.TryGetValue("oidc_subject", out var subject)
            || string.IsNullOrWhiteSpace(issuer)
            || string.IsNullOrWhiteSpace(subject))
        {
            RedirectFailure(context, "state", setup || ownerOnly);
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
                setup || IsOwner(settings, issuer, subject),
                issuer,
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
                    () => _accessService.PromotePendingLogin(loginId, revision, issuer, subject, signIn?.AccountId));
            }
            catch (ServiceUnavailableException ex)
            {
                _logger.LogWarning(ex, "External sign-in settings could not be saved after verification");
                RedirectFailure(context, "unavailable", returnToSetup: true);
                return;
            }
            if (!promoted)
            {
                RedirectFailure(context, "expired", returnToSetup: true);
                return;
            }

            _oidcOptions.TryRemove(AccessService.GetScheme(settings.Kind, setup: false));
            _oidcOptions.TryRemove(AccessService.GetScheme(settings.Kind, setup: true));
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
        _logger.LogWarning(context.Failure, "OIDC sign-in failed");
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

    private static bool IsOwner(OidcSettings settings, string issuer, string subject)
        => string.Equals(settings.OwnerIssuer, issuer, StringComparison.Ordinal)
            && string.Equals(settings.OwnerSubject, subject, StringComparison.Ordinal);

    private static string? Subject(OidcSettings settings, ClaimsPrincipal? principal, string issuer)
    {
        if (settings.Kind != LoginKind.Microsoft)
        {
            return principal?.FindFirstValue("sub");
        }

        var tenant = principal?.FindFirstValue("tid");
        var account = principal?.FindFirstValue("oid");
        var expectedTenant = string.Equals(settings.Tenant, "consumers", StringComparison.OrdinalIgnoreCase)
            ? Guid.Parse("9188040d-6c67-4c5b-b112-36a304b66dad")
            : Guid.TryParseExact(settings.Tenant, "D", out var configuredTenant)
                ? configuredTenant
                : Guid.Empty;
        return Guid.TryParseExact(tenant, "D", out var tenantId)
            && Guid.TryParseExact(account, "D", out var accountId)
            && tenantId == expectedTenant
            && string.Equals(
                issuer,
                $"https://login.microsoftonline.com/{tenantId:D}/v2.0",
                StringComparison.Ordinal)
            ? $"{tenantId:D}:{accountId:D}"
            : null;
    }

    private static bool TryReadTransaction(
        AuthenticationProperties properties,
        out string loginId,
        out long revision,
        out bool setup,
        out bool ownerOnly)
    {
        loginId = properties.Items.TryGetValue("login_id", out var id)
            && !string.IsNullOrWhiteSpace(id)
            ? id
            : "customOidc";
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
        if (message.Contains("identity is not allowed", StringComparison.Ordinal)
            || message.Contains("invalid user", StringComparison.Ordinal))
        {
            return "identity";
        }
        if (message.Contains("transaction state", StringComparison.Ordinal)
            || message.Contains("correlation", StringComparison.OrdinalIgnoreCase)
            || message.Contains("nonce", StringComparison.OrdinalIgnoreCase)
            || message.Contains("state", StringComparison.OrdinalIgnoreCase))
        {
            return "expired";
        }
        return "authentication";
    }
}
