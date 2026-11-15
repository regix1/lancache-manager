using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LancacheManager.Tests;

/// <summary>
/// Switching to unauthenticated mode deletes every account with its sign-in links. A member's GitHub
/// sign-in that was started while sign-in was on can come back from GitHub before or after that switch,
/// and in either order the stored access state ends with no sign-in link that names an account which
/// does not exist.
/// </summary>
// Ending every session touches the process-wide shared session (SessionService.cs:45).
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class RefusedExternalSignInLinkTests
{
    private const string GitHubIssuer = "https://github.com";
    private const string MemberSubject = "member-subject";
    private const string CallbackAfterSwitch = "the callback lands after the switch";
    private const string CallbackBeforeSwitch = "the callback lands before the switch";

    [Theory]
    [InlineData(CallbackAfterSwitch)]
    [InlineData(CallbackBeforeSwitch)]
    public async Task ASignInAroundTheSwitchToUnauthenticatedMode_LeavesNoLinkToAMissingAccountAsync(string order)
    {
        var owner = SignInModeScene.Account(Guid.NewGuid(), "owner", SessionType.Admin, mainAdmin: true);
        await using var installation = await SignInModeScene.CreateAsync(new Dictionary<string, string?>(), access =>
        {
            access.Mode = AccountMode.Oidc;
            access.SetupVersion = AccessSettings.RequiredSetupVersion;
            access.Logins.Add(new OidcSettings
            {
                Id = "github",
                Kind = LoginKind.GitHub,
                ClientId = "github-client",
                ClientSecret = "secret",
                DisplayName = "GitHub"
            });
        });
        await installation.SeedAsync(owner);
        var (_, ownerSession) = await installation.Sessions.CreateAccountSessionAsync(new DefaultHttpContext(), owner);

        // A member starts a GitHub sign-in and is on GitHub's consent page.
        var started = Assert.IsType<OidcSettings>(installation.Access.GetLoginSettings("github", setup: false));
        var signIns = new ExternalSignInService(
            installation.Access,
            installation.Database.Factory,
            installation.Sessions,
            installation.App.GetRequiredService<ISignalRNotificationService>());

        var steps = order == CallbackAfterSwitch
            ? new Func<Task>[] { SwitchAsync, CallbackAsync }
            : [CallbackAsync, SwitchAsync];
        foreach (var step in steps)
            await step();

        await using var read = await installation.Database.Factory.CreateDbContextAsync();
        var accounts = await read.UserAccounts.Select(account => account.Id).ToListAsync();
        var dangling = installation.State.GetState().Access.Logins
            .SelectMany(login => login.AccountIds
                .Where(link => !accounts.Contains(link.Value))
                .Select(link => $"{login.Id}: {link.Key} -> {link.Value}"))
            .ToList();
        Assert.True(
            dangling.Count == 0,
            $"when {order}, the stored access state keeps {dangling.Count} sign-in link(s) to an account that does not exist ({string.Join(", ", dangling)}), "
            + "though the switch deletes every account with its sign-in links");

        // The owner confirms the switch to unauthenticated mode, which deletes every account.
        async Task SwitchAsync()
        {
            int count;
            await using (var context = await installation.Database.Factory.CreateDbContextAsync())
                count = await context.UserAccounts.CountAsync();
            var switched = await installation.Controller<AccessController>(ownerSession).SetUpAsync(
                UnauthenticatedModeWipesAccountsTests.ConfirmingAccountWipe(new AccessSetupRequest
                {
                    Mode = AccountMode.Unauthenticated,
                    AcknowledgeUnauthenticated = true,
                    ApiKey = installation.ApiKey.GetApiKey()
                }, accountCount: count));
            Assert.True(switched.Result is OkObjectResult, $"the confirmed switch was answered {(switched.Result as ObjectResult)?.StatusCode}");
            Assert.False(installation.Sessions.IsAuthenticationEnabled(), "the switch did not turn sign-in off");
        }

        // The member approves on GitHub. The callback re-reads the login and compares only its
        // revision (OidcEvents.cs:118-121, GitHubEvents.cs:113-121), so it reaches the save.
        async Task CallbackAsync()
        {
            var current = installation.Access.GetLoginSettings("github", setup: false);
            Assert.True(
                current is not null && current.Revision == started.Revision,
                "the callback's own revision check already refuses this sign-in, so the test proves nothing");
            try
            {
                await signIns.TryCreateSessionAsync(
                    current!,
                    owner: false,
                    GitHubIssuer,
                    MemberSubject,
                    new DefaultHttpContext { RequestServices = installation.App });
            }
            catch (ValidationException)
            {
                // The callbacks turn this refusal into the sign-in-expired redirect (OidcEvents.cs:141).
            }
        }
    }
}
