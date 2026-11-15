using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;

namespace LancacheManager.Tests;

/// <summary>
/// The shared session is the one every browser runs on while sign-in is off. When the configuration
/// alone turns sign-in on, with a restart and no save in the app, a cookie for that session must stop
/// signing its browser in as an administrator from that start on, even while access setup is still
/// unfinished and an account already exists.
/// </summary>
// The shared session is process-wide (SessionService.cs:45).
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class SignInTurnedOnRetiresSharedCookieTests
{
    [Fact]
    public async Task AStartWithSignInTurnedOnByConfiguration_NoLongerSignsTheSharedCookieInAsAnAdministratorAsync()
    {
        // An installation from before the sign-in modes, sign-in off in its configuration, access setup
        // not yet finished.
        await using var installation = await SignInModeScene.CreateAsync(
            new Dictionary<string, string?> { ["Security:EnableAuthentication"] = "false" },
            access => access.SetupVersion = 0);
        Assert.False(installation.Sessions.IsAuthenticationEnabled(), "the configuration did not turn sign-in off");
        var (sharedToken, shared) = (await installation.Sessions.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext()))!.Value;
        await installation.SeedAsync(SignInModeScene.Account(Guid.NewGuid(), "owner", SessionType.Admin, mainAdmin: true));

        // The container restarts with sign-in on in its configuration; the browser still holds the cookie.
        await using var restarted = installation.Restart(new Dictionary<string, string?> { ["Security:EnableAuthentication"] = "true" });
        Assert.True(restarted.Sessions.IsAuthenticationEnabled(), "the configuration did not turn sign-in on");
        Assert.True(restarted.Access.IsSetupRequired(), "access setup reads as finished, so the test proves nothing");
        await restarted.StartAsync();
        var (result, request) = await restarted.AuthenticateAsync($"LancacheManager.Session={sharedToken}");

        var signedInAs = request.GetUserSession();
        Assert.False(
            result.Succeeded && result.Principal!.IsInRole("admin"),
            $"after sign-in was turned on, the shared session's cookie still signs its browser in as an administrator (session {signedInAs?.Id} of type {signedInAs?.SessionType}, the shared session was {shared.Id})");
    }
}
