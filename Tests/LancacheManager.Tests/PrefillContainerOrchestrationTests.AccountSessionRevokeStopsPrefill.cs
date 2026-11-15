using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // The Users page as an administrator signed in with the API key, which hides no account.
    private static SessionsController UsersPage(AccountWithPrefill scene)
    {
        var controller = new SessionsController(
            scene.Sessions,
            scene.App.GetRequiredService<ISignalRNotificationService>(),
            scene.App.GetRequiredService<IServiceScopeFactory>(),
            scene.App.GetRequiredService<StateService>());
        var http = new DefaultHttpContext { RequestServices = scene.App };
        http.Items["Session"] = new UserSession { Id = Guid.NewGuid(), SessionType = SessionType.Admin };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    // Every way an account session ends while its page runs a prefill session of its own. The prefill page
    // of an account holder starts one that is not temporary (only a guest's is), so the stop has to find it
    // by its owner.
    [Theory]
    [InlineData("password sign-in in another window")]
    [InlineData("sign-out")]
    [InlineData("revoke on the Users page")]
    [InlineData("delete on the Users page")]
    [InlineData("account change")]
    public async Task EndingAnAccountSession_StopsItsOwnPrefillSession(string end)
    {
        var scene = await StartAccountPrefillAsync(SessionType.User);
        await using var app = scene.App;
        var own = AddGuest(scene.Steam, scene.SteamGateway, scene.Session.Id, temporary: false);

        switch (end)
        {
            case "password sign-in in another window":
                var signedIn = await WindowHoldingTheCookie(scene, sessionChecked: true)
                    .LoginAsync(new LoginRequest { Username = "colleague", Password = ColleaguePassword, ApiKey = scene.ApiKey });
                Assert.IsType<OkObjectResult>(signedIn.Result);
                break;
            case "sign-out":
                Assert.IsType<OkObjectResult>((await WindowHoldingTheCookie(scene, sessionChecked: false).LogoutAsync()).Result);
                break;
            case "revoke on the Users page":
                Assert.IsType<OkObjectResult>((await UsersPage(scene).RevokeAsync(scene.Session.Id)).Result);
                break;
            case "delete on the Users page":
                Assert.IsType<OkObjectResult>((await UsersPage(scene).DeleteAsync(scene.Session.Id)).Result);
                break;
            default:
                Assert.Equal(1, await scene.Sessions.RevokeAccountSessionsAsync(scene.Session.AccountId!.Value, scene.App));
                break;
        }

        Assert.False(
            scene.SteamGateway.ContainsContainer(own.ContainerId!),
            $"the account session's own prefill container is still running after its {end}, and no page can reach it");
        Assert.False(
            scene.SteamGateway.ContainsContainer(scene.Prefill.ContainerId!),
            $"the account session's temporary prefill container is still running after its {end}");
        Assert.Empty(scene.Steam.GetAllSessions(includeTerminating: true));
    }
}
