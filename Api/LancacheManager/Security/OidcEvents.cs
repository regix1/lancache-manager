using System.Globalization;
using System.Security.Claims;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

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
            context.Fail(new SignInFailureException("state", "OIDC transaction state is missing"));
            return Task.CompletedTask;
        }

        var issuer = context.SecurityToken.Issuer;
        var subject = Subject(settings, context.Principal, issuer);
        if (string.IsNullOrWhiteSpace(issuer)
            || string.IsNullOrWhiteSpace(subject)
            || !_accessService.IdentityAllowed(loginId, revision, setup, ownerOnly, issuer, subject))
        {
            context.Fail(new SignInFailureException("identity", "OIDC identity is not allowed"));
            return Task.CompletedTask;
        }

        properties.Items["login_id"] = loginId;
        properties.Items["oidc_issuer"] = issuer;
        properties.Items["oidc_subject"] = subject;
        return Task.CompletedTask;
    }

    public override async Task TicketReceived(TicketReceivedContext context)
    {
        // Apple posts its callback cross-site, and that post carries no SameSite=Lax session cookie.
        // Left unhandled, the handler keeps the verified identity in the Apple cookie and redirects, and
        // the browser follows with a top-level GET that carries the session cookie it holds then. The
        // challenge's start time is cleared so the Apple cookie's five minutes start at this post.
        if (AccessService.TryReadScheme(context.Scheme.Name, out var kind, out _) && kind == LoginKind.Apple)
        {
            context.Properties!.IssuedUtc = null;
            context.ReturnUri = "/api/auth/login/apple/complete";
            return;
        }

        var location = await CompleteSignInAsync(context.Properties, context.HttpContext);
        context.HandleResponse();
        context.Response.Redirect(location);
    }

    /// <summary>
    /// Signs in the identity the provider verified and ends the session the request's own cookie names.
    /// Returns where the browser goes next.
    /// </summary>
    public async Task<string> CompleteSignInAsync(AuthenticationProperties? properties, HttpContext httpContext)
    {
        var setup = false;
        var ownerOnly = false;
        if (properties is null
            || !TryReadTransaction(properties, out var loginId, out var revision, out setup, out ownerOnly)
            || !properties.Items.TryGetValue("oidc_issuer", out var issuer)
            || !properties.Items.TryGetValue("oidc_subject", out var subject)
            || string.IsNullOrWhiteSpace(issuer)
            || string.IsNullOrWhiteSpace(subject))
        {
            return FailureLocation("state", setup || ownerOnly);
        }

        using var setupLease = setup
            ? await _signInService.EnterSetupAsync(httpContext.RequestAborted)
            : null;
        var settings = _accessService.GetLoginSettings(loginId, setup);
        if (settings is null || settings.Revision != revision)
        {
            return FailureLocation("expired", setup || ownerOnly);
        }

        var deferOwner = setup && _accessService.CanDeferOidcAccount(loginId, revision);
        // Only a setup that started with sign-in off may finish while sign-in is off: it is the one that
        // turns sign-in on. A deferred owner saves no account, so its setup is refused here.
        var setupStartedWithSignInOff = setup
            && properties.Items.TryGetValue("access_sign_in_off", out var signInOff)
            && string.Equals(signInOff, "true", StringComparison.Ordinal);
        if (deferOwner && !setupStartedWithSignInOff && !_accessService.IsAuthenticationEnabled())
        {
            return FailureLocation("unavailable", returnToSetup: true);
        }

        // Read before the new session exists, so a failed read leaves no session without a cookie. A
        // deferred owner has no database to read and replaces no session.
        UserSession? held;
        ExternalSignInResult? signIn;
        try
        {
            held = deferOwner ? null : await _signInService.ReadHeldSessionAsync(httpContext);
            signIn = deferOwner
                ? null
                : await _signInService.TryCreateSessionAsync(
                    settings,
                    setup || IsOwner(settings, issuer, subject),
                    issuer,
                    subject,
                    httpContext,
                    setupStartedWithSignInOff);
        }
        catch (Exception ex) when (ex is not ValidationException)
        {
            // This runs after the authentication handler's own error handling, so a database that cannot be
            // reached would otherwise end on the raw error page. Nothing in the stack tells an unreachable
            // server from another provider fault, as the session handler notes.
            _logger.LogWarning(ex, "External sign-in could not reach the database");
            return FailureLocation("unavailable", setup || ownerOnly);
        }

        if (signIn is null && !deferOwner)
        {
            return FailureLocation("unavailable", setup || ownerOnly);
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
                return FailureLocation("unavailable", returnToSetup: true);
            }
            if (!promoted)
            {
                return FailureLocation("expired", returnToSetup: true);
            }
            await _signInService.RetireSharedSessionAsync(httpContext);

            _oidcOptions.TryRemove(AccessService.GetScheme(settings.Kind, setup: false));
            _oidcOptions.TryRemove(AccessService.GetScheme(settings.Kind, setup: true));
        }

        if (signIn is not null)
        {
            await _signInService.EndGuestSessionAsync(held, httpContext, signIn);
            _signInService.SetSessionCookie(httpContext, signIn);
            await _signInService.EndReplacedSessionAsync(held, httpContext);
        }

        return setup
            ? $"/?accessSetup=1&loginTest=success&loginId={Uri.EscapeDataString(loginId)}"
            : ownerOnly ? "/?accessSetup=1" : "/";
    }

    public override Task RemoteFailure(RemoteFailureContext context)
    {
        _logger.LogWarning(context.Failure, "OIDC sign-in failed");
        context.HandleResponse();
        var returnToSetup = context.Properties is { } properties
            && TryReadTransaction(properties, out _, out _, out var setup, out var ownerOnly)
            && (setup || ownerOnly);
        var code = FailureCode(
            context.Failure,
            context.Properties,
            context.Options.RemoteAuthenticationTimeout,
            _timeProvider.GetUtcNow());
        context.Response.Redirect(FailureLocation(code, returnToSetup));
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

    internal static void RedirectFailure(TicketReceivedContext context, string code, bool returnToSetup)
    {
        context.HandleResponse();
        context.Response.Redirect(FailureLocation(code, returnToSetup));
    }

    internal static string FailureLocation(string code, bool returnToSetup)
        => returnToSetup
            ? $"/?accessSetup=1&oidcError={code}"
            : $"/?oidcError={code}";

    internal static string FailureCode(
        Exception? failure,
        AuthenticationProperties? properties,
        TimeSpan remoteTimeout,
        DateTimeOffset now)
    {
        var heldAuthenticationFailure = false;
        for (var current = failure; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException or IOException)
            {
                return "connection";
            }
            heldAuthenticationFailure |= current is AuthenticationFailureException;
        }

        if (failure is SignInFailureException typed)
        {
            return typed.Code;
        }
        if (failure is OpenIdConnectProtocolInvalidStateException or OpenIdConnectProtocolInvalidNonceException)
        {
            return "state";
        }

        // The correlation and nonce cookies expire RemoteAuthenticationTimeout after the challenge, so a
        // failure after that time is an expired attempt, and a failure before it is a configuration or
        // provider problem.
        if (heldAuthenticationFailure
            && properties?.IssuedUtc is { } issued
            && now - issued >= remoteTimeout)
        {
            return "expired";
        }
        return "authentication";
    }
}
