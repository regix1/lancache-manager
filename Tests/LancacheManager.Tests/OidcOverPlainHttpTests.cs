using LancacheManager.Controllers;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A LAN install often runs on plain HTTP, and its own sign-in service, such as a Pocket ID or
/// Authentik on the same network, often does too. Single sign-on works there: the custom OpenID
/// Connect service and GitHub can be saved with an <c>http://</c> address, the sign-in starts from the
/// app's <c>http://</c> LAN address, discovery is read over HTTP, and the correlation and nonce cookies
/// the sign-in sets are ones a browser keeps on an HTTP page (not <c>Secure</c>, not
/// <c>SameSite=None</c>). Apple, Google and Microsoft stay HTTPS only and are not part of this.
/// </summary>
public sealed class OidcOverPlainHttpTests : IDisposable
{
    private const string LanAuthority = "http://192.0.2.30:1411";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lcm-oidc-http-{Guid.NewGuid():N}");

    [Fact]
    public async Task SingleSignOnFromAPlainHttpLanAddress_SavesStartsAndSetsCookiesTheBrowserKeepsAsync()
    {
        Directory.CreateDirectory(_root);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(_root, "api-key.txt"),
                ["Runtime:DatabaseSetupPending"] = "true"
            })
            .Build();
        var access = new AccessService(StateTestMethods.CreateStateService(_root), configuration, dbContextFactory: null!);
        var wrong = new List<string>();

        // The owner saves the LAN sign-in service by its http:// address.
        AccessSetupRequest Saving(string authority) => new()
        {
            Mode = AccountMode.Oidc,
            ApiKey = "checked-by-controller",
            Oidc = new OidcSetupRequest { Authority = authority, ClientId = "lancache", ClientSecret = "secret" }
        };
        var savedOverHttp = true;
        try
        {
            access.Apply(Saving(LanAuthority), null);
        }
        catch (ValidationException refused)
        {
            savedOverHttp = false;
            wrong.Add($"saving the custom sign-in service at {LanAuthority} was refused: {refused.Message}");
            access.Apply(Saving("https://login.example.test"), null);
        }

        // The sign-in starts from the app's http:// LAN address.
        var apiKey = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!);
        await using var app = new ServiceCollection()
            .AddOptions()
            .AddSingleton(access)
            .AddSingleton(apiKey)
            .AddSingleton(new AccountClaimWindow(NullLogger<AccountClaimWindow>.Instance))
            .AddSingleton(new OidcChallengeStore(TimeProvider.System))
            .BuildServiceProvider();
        var controller = ActivatorUtilities.CreateInstance<AccessController>(app);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = app } };
        controller.Request.Scheme = Uri.UriSchemeHttp;
        controller.Request.Host = new HostString("192.0.2.20", 8080);
        var started = await controller.StartOidcAsync(new OidcStartRequest { Setup = true, ApiKey = apiKey.GetApiKey() });
        if (started.Result is not OkObjectResult)
            wrong.Add($"starting the sign-in from http://192.0.2.20:8080 was answered {(started.Result as ObjectResult)?.StatusCode} {(started.Result as ObjectResult)?.Value}");

        // What the sign-in's handlers do on that HTTP request.
        var page = new DefaultHttpContext();
        page.Request.Scheme = Uri.UriSchemeHttp;
        page.Request.Host = new HostString("192.0.2.20", 8080);
        var oidc = new OpenIdConnectOptions();
        new OidcOptionsSetup(access).Configure(AccessService.OidcSetupScheme, oidc);
        if (savedOverHttp && oidc.RequireHttpsMetadata)
            wrong.Add($"the discovery document at {LanAuthority} must come over HTTPS (RequireHttpsMetadata is on)");
        var gitHub = new OAuthOptions();
        new GitHubOptionsSetup(access).Configure(AccessService.GitHubSetupScheme, gitHub);
        foreach (var (name, cookie) in new[]
        {
            ("custom OpenID Connect correlation", oidc.CorrelationCookie.Build(page)),
            ("custom OpenID Connect nonce", oidc.NonceCookie.Build(page)),
            ("GitHub correlation", gitHub.CorrelationCookie.Build(page))
        })
        {
            if (cookie.Secure || cookie.SameSite == SameSiteMode.None)
                wrong.Add($"the {name} cookie is set Secure={cookie.Secure}, SameSite={cookie.SameSite} on an HTTP page, so the browser drops it and the callback fails its check");
        }

        Assert.True(wrong.Count == 0, "single sign-on over plain HTTP: " + string.Join("; ", wrong));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
