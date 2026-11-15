using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // While authentication is off every browser shares one session, and the prefill page of another browser
    // runs under it. Log Out in one browser signs nobody out, so it leaves that session as it is: the next
    // request of every browser gets the same session back, and the other browser's page still lists its
    // prefill. Revoke and delete on the Users page are refused for it, since the next request would only make
    // it again, so the same holds after either.
    [Theory]
    [InlineData("Log Out")]
    [InlineData("revoke on the Users page")]
    [InlineData("delete on the Users page")]
    public async Task EndingTheSharedSessionWithAuthenticationOff_LeavesNoPrefillOutOfReach(string end)
    {
        var scene = await StartAccountPrefillAsync(SessionType.Admin);
        await using var app = scene.App;
        app.GetRequiredService<IConfiguration>()["Security:EnableAuthentication"] = "false";
        var (sharedToken, shared) = (await scene.Sessions.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext()))!.Value;
        var otherBrowser = AddGuest(scene.Steam, scene.SteamGateway, shared.Id, temporary: false);

        var answer = end switch
        {
            "Log Out" => (await WindowHoldingTheCookie(scene with { Token = sharedToken, Session = shared }, sessionChecked: true)
                .LogoutAsync()).Result,
            "revoke on the Users page" => (await UsersPage(scene).RevokeAsync(shared.Id)).Result,
            _ => (await UsersPage(scene).DeleteAsync(shared.Id)).Result
        };

        if (end == "Log Out")
            Assert.IsType<OkObjectResult>(answer);
        else
            Assert.IsType<ConflictObjectResult>(answer);
        var next = (await scene.Sessions.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext()))!.Value.Session;
        Assert.True(
            next.Id == shared.Id,
            $"{end} with authentication off ended the shared session, so the next request of every browser got a new session and every browser lost its live connections");
        Assert.True(
            scene.Steam.GetUserSessions(next.Id).Any(session => session.Id == otherBrowser.Id),
            $"after {end} with authentication off, the other browser's prefill page no longer lists its prefill, which keeps running");
        Assert.True(
            scene.SteamGateway.ContainsContainer(otherBrowser.ContainerId!),
            $"{end} with authentication off stopped the other browser's prefill");
    }
}
