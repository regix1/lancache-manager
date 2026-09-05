using LancacheManager.Models;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Options;

namespace LancacheManager.Security;

public sealed class GitHubOptionsSetup : IConfigureNamedOptions<OAuthOptions>
{
    private readonly AccessService _accessService;

    public GitHubOptionsSetup(AccessService accessService)
    {
        _accessService = accessService;
    }

    public void Configure(OAuthOptions options)
        => Configure(Options.DefaultName, options);

    public void Configure(string? name, OAuthOptions options)
    {
        if (!AccessService.TryReadScheme(name, out var kind, out var setup)
            || kind != LoginKind.GitHub)
        {
            return;
        }

        var settings = _accessService.GetLoginSettings("github", setup);
        options.ClientId = settings?.ClientId ?? "not-configured";
        options.ClientSecret = settings?.ClientSecret ?? "not-configured";
        options.CallbackPath = AccessService.GetCallbackPath(LoginKind.GitHub, setup);
        options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
        options.TokenEndpoint = "https://github.com/login/oauth/access_token";
        options.UserInformationEndpoint = "https://api.github.com/user";
        options.UsePkce = true;
        options.SaveTokens = false;
        options.SignInScheme = AccessService.OidcCookieScheme;
        options.EventsType = typeof(GitHubEvents);
        options.Scope.Clear();
    }
}
