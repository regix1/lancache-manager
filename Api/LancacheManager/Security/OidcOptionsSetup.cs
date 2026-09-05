using LancacheManager.Models;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LancacheManager.Security;

public sealed class OidcOptionsSetup : IConfigureNamedOptions<OpenIdConnectOptions>
{
    private readonly AccessService _accessService;

    public OidcOptionsSetup(AccessService accessService)
    {
        _accessService = accessService;
    }

    public void Configure(OpenIdConnectOptions options)
        => Configure(Options.DefaultName, options);

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (!AccessService.TryReadScheme(name, out var kind, out var setup)
            || kind == LoginKind.GitHub)
        {
            return;
        }

        var loginId = kind switch
        {
            LoginKind.CustomOidc => "customOidc",
            LoginKind.Google => "google",
            LoginKind.Microsoft => "microsoft",
            LoginKind.Apple => "apple",
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
        var settings = _accessService.GetLoginSettings(loginId, setup);
        // Authentication middleware resolves every request-handling scheme before routing, even
        // before OIDC has been configured. Syntactically valid inert values keep ordinary requests
        // available during setup; AccessController refuses every challenge until real settings exist.
        options.Authority = settings?.Authority ?? "https://invalid.invalid";
        options.ClientId = settings?.ClientId ?? "not-configured";
        options.ClientSecret = settings?.ClientSecret;
        options.ResponseType = OpenIdConnectResponseType.Code;
        // Apple does not advertise PKCE in its discovery document. It uses a confidential client,
        // nonce, state, and a short-lived server-generated client assertion instead.
        options.UsePkce = kind != LoginKind.Apple;
        options.RequireHttpsMetadata = true;
        options.ProtocolValidator.RequireStateValidation = true;
        options.MapInboundClaims = false;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.SignInScheme = kind == LoginKind.Apple
            ? AccessService.AppleCookieScheme
            : AccessService.OidcCookieScheme;
        options.CallbackPath = AccessService.GetCallbackPath(kind, setup);
        options.EventsType = typeof(OidcEvents);
        options.ResponseMode = kind == LoginKind.Apple
            ? OpenIdConnectResponseMode.FormPost
            : OpenIdConnectResponseMode.Query;
        options.Scope.Clear();
        options.Scope.Add("openid");
        if (kind != LoginKind.Apple)
        {
            options.Scope.Add("profile");
        }
    }
}
