using System.Globalization;
using System.Security.Claims;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // While authentication is off a browser runs a prefill under the shared session. Setting up GitHub or
    // another login service finishes with its first sign-in, which saves the secure mode and retires the
    // shared session; no page can reach that prefill any more, so it stops with the session.
    [Theory]
    [InlineData(LoginKind.GitHub)]
    [InlineData(LoginKind.CustomOidc)]
    public async Task FinishingALoginServiceSetup_StopsThePrefillOfTheRetiredSharedSession(LoginKind kind)
    {
        var scene = await StartAccountPrefillAsync(SessionType.User);
        await using var app = scene.App;
        var configuration = app.GetRequiredService<IConfiguration>();
        configuration["Security:EnableAuthentication"] = "false";
        var (_, shared) = (await scene.Sessions.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext()))!.Value;
        var browser = AddGuest(scene.Steam, scene.SteamGateway, shared.Id, temporary: false);

        var contexts = app.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var access = new AccessService(app.GetRequiredService<StateService>(), configuration, contexts);
        var loginId = kind == LoginKind.GitHub ? "github" : "customOidc";
        const string issuer = "https://login.example.test";
        access.Apply(
            new AccessSetupRequest
            {
                Mode = AccountMode.Oidc,
                ApiKey = "checked-by-controller",
                Login = new LoginSetupRequest
                {
                    Kind = kind,
                    ClientId = $"{kind}-client",
                    ClientSecret = "secret",
                    Authority = kind == LoginKind.CustomOidc ? issuer : null
                }
            },
            null);
        var pending = Assert.IsType<OidcSettings>(access.GetLoginSettings(loginId, setup: true));
        var signIns = new ExternalSignInService(access, contexts, scene.Sessions, app.GetRequiredService<ISignalRNotificationService>());

        var properties = new AuthenticationProperties();
        properties.Items["access_revision"] = pending.Revision.ToString(CultureInfo.InvariantCulture);
        properties.Items["access_setup"] = "true";
        properties.Items["access_owner"] = "false";
        properties.Items["access_sign_in_off"] = "true";
        properties.Items["login_id"] = loginId;
        const string subject = "first-owner";
        var callback = new DefaultHttpContext { RequestServices = app };
        callback.Request.Scheme = Uri.UriSchemeHttp;
        callback.Request.Host = new HostString("localhost");

        if (kind == LoginKind.GitHub)
        {
            var scheme = new AuthenticationScheme(AccessService.GitHubSetupScheme, AccessService.GitHubSetupScheme, typeof(OAuthHandler<OAuthOptions>));
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject)], scheme.Name));
            await new GitHubEvents(access, signIns, new OptionsCache<OAuthOptions>(), TimeProvider.System, NullLogger<GitHubEvents>.Instance)
                .TicketReceived(new TicketReceivedContext(callback, scheme, new OAuthOptions(), new AuthenticationTicket(principal, properties, scheme.Name)));
            Assert.Contains("loginTest=success", callback.Response.Headers.Location.ToString(), StringComparison.Ordinal);
        }
        else
        {
            properties.Items["oidc_issuer"] = issuer;
            properties.Items["oidc_subject"] = subject;
            var location = await new OidcEvents(access, signIns, new OptionsCache<OpenIdConnectOptions>(), TimeProvider.System, NullLogger<OidcEvents>.Instance)
                .CompleteSignInAsync(properties, callback);
            Assert.Contains("loginTest=success", location, StringComparison.Ordinal);
        }

        Assert.True(scene.Sessions.IsAuthenticationEnabled());
        Assert.False(
            scene.SteamGateway.ContainsContainer(browser.ContainerId!),
            $"the shared session's prefill container is still running after the {kind} setup sign-in retired that session, and no page can reach it");
        Assert.Empty(scene.Steam.GetUserSessions(shared.Id));
    }
}
