using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // An administrator or user with a temporary prefill session running clicks Log Out. The session is
    // revoked, so nothing can reach that prefill session afterwards: it has to stop with the sign-out,
    // as a guest's does. The setup is in PrefillContainerOrchestrationTests.AccountSessionEndStopsPrefill.cs.
    [Theory]
    [InlineData(SessionType.Admin)]
    [InlineData(SessionType.User)]
    public async Task SignOutOfAnAccountSession_StopsThePrefillThatSessionStarted(SessionType role)
    {
        var scene = await StartAccountPrefillAsync(role);
        await using var app = scene.App;
        var controller = WindowHoldingTheCookie(scene, sessionChecked: false);

        var answer = await controller.LogoutAsync();

        Assert.IsType<OkObjectResult>(answer.Result);
        Assert.True(
            (await scene.Sessions.GetSessionByIdAsync(scene.Session.Id))!.IsRevoked,
            $"the {role} session that signed out is still live");
        Assert.False(
            scene.SteamGateway.ContainsContainer(scene.Prefill.ContainerId!),
            $"the {role} session's temporary prefill container is still running after that session signed out");
        Assert.Empty(scene.Steam.GetUserSessions(scene.Session.Id));
    }
}
