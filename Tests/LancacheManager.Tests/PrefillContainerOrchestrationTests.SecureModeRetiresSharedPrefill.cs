using LancacheManager.Controllers;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // While authentication is off a browser runs a prefill under the shared session. The main administrator
    // then saves password sign-in: the shared session is retired, and every browser has to sign in under a
    // session of its own, so no page can reach that prefill any more. It stops with the session.
    [Fact]
    public async Task SavingASecureMode_StopsThePrefillOfTheRetiredSharedSession()
    {
        var scene = await StartAccountPrefillAsync(SessionType.Admin);
        await using var app = scene.App;
        var configuration = app.GetRequiredService<IConfiguration>();
        configuration["Security:EnableAuthentication"] = "false";
        var (_, shared) = (await scene.Sessions.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext()))!.Value;
        var browser = AddGuest(scene.Steam, scene.SteamGateway, shared.Id, temporary: false);

        var access = new AccessService(
            app.GetRequiredService<StateService>(), configuration, app.GetRequiredService<IDbContextFactory<AppDbContext>>());
        var settings = new AccessController(
            access,
            app.GetRequiredService<ApiKeyService>(),
            new AccountClaimWindow(NullLogger<AccountClaimWindow>.Instance),
            new OidcChallengeStore(TimeProvider.System),
            new OptionsCache<OpenIdConnectOptions>(),
            new OptionsCache<OAuthOptions>(),
            scene.Sessions);
        var request = new DefaultHttpContext { RequestServices = app };
        request.Items["Session"] = scene.Session;
        settings.ControllerContext = new ControllerContext { HttpContext = request };

        var saved = await settings.SetUpAsync(new AccessSetupRequest { Mode = AccountMode.Password, ApiKey = scene.ApiKey });

        Assert.IsType<OkObjectResult>(saved.Result);
        Assert.True(scene.Sessions.IsAuthenticationEnabled());
        Assert.False(
            scene.SteamGateway.ContainsContainer(browser.ContainerId!),
            "the shared session's prefill container is still running after saving password sign-in retired that session, and no page can reach it");
        Assert.Empty(scene.Steam.GetUserSessions(shared.Id));
    }
}
