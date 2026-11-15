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
/// With sign-in off no account exists. A GitHub or OpenID Connect sign-in that someone started while
/// sign-in was on can come back from the provider after the owner switched to unauthenticated mode, and
/// its callback must then create no account and no session, for a stored login and for a login the
/// environment sets alike. Both callbacks (OidcEvents, GitHubEvents) save through
/// <see cref="ExternalSignInService.TryCreateSessionAsync"/>, so the callback's last step is run there.
/// </summary>
// Ending every session touches the process-wide shared session (SessionService.cs:45).
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class ExternalSignInAfterSignInOffTests
{
    private const string GitHubIssuer = "https://github.com";
    private const string OwnerSubject = "owner-subject";
    private const string StoredLogin = "a stored login, a member's sign-in";
    private const string EnvironmentLogin = "a login whose environment sets the owner, the owner's sign-in";

    [Theory]
    [InlineData(StoredLogin)]
    [InlineData(EnvironmentLogin)]
    public async Task ASignInThatReturnsAfterTheSwitchToUnauthenticatedMode_CreatesNoAccountAsync(string login)
    {
        var fromEnvironment = login == EnvironmentLogin;
        var owner = SignInModeScene.Account(Guid.NewGuid(), "owner", SessionType.Admin, mainAdmin: true);
        await using var installation = await SignInModeScene.CreateAsync(
            fromEnvironment
                ? new Dictionary<string, string?> { ["Security:GitHub:OwnerSubject"] = OwnerSubject }
                : new Dictionary<string, string?>(),
            access =>
            {
                access.Mode = AccountMode.Oidc;
                access.SetupVersion = AccessSettings.RequiredSetupVersion;
                var github = new OidcSettings
                {
                    Id = "github",
                    Kind = LoginKind.GitHub,
                    ClientId = "github-client",
                    ClientSecret = "secret",
                    DisplayName = "GitHub"
                };
                if (fromEnvironment)
                {
                    github.OwnerIssuer = GitHubIssuer;
                    github.OwnerSubject = OwnerSubject;
                    github.OwnerAccountId = owner.Id;
                    github.AccountIds = new Dictionary<string, Guid>(StringComparer.Ordinal) { ["github-" + OwnerSubject] = owner.Id };
                }
                access.Logins.Add(github);
            });
        await installation.SeedAsync(owner);
        var (_, ownerSession) = await installation.Sessions.CreateAccountSessionAsync(new DefaultHttpContext(), owner);

        // Someone starts a GitHub sign-in and is on GitHub's consent page.
        var started = Assert.IsType<OidcSettings>(installation.Access.GetLoginSettings("github", setup: false));

        // The owner confirms the switch to unauthenticated mode, which deletes every account.
        var switched = await installation.Controller<AccessController>(ownerSession).SetUpAsync(
            UnauthenticatedModeWipesAccountsTests.ConfirmingAccountWipe(new AccessSetupRequest
            {
                Mode = AccountMode.Unauthenticated,
                AcknowledgeUnauthenticated = true,
                ApiKey = installation.ApiKey.GetApiKey()
            }, accountCount: 1));
        Assert.True(switched.Result is OkObjectResult, $"the confirmed switch was answered {(switched.Result as ObjectResult)?.StatusCode}");
        Assert.False(installation.Sessions.IsAuthenticationEnabled(), "the switch did not turn sign-in off");

        // The person approves on GitHub. The callback re-reads the login and compares only its revision
        // (OidcEvents.cs:118-121, GitHubEvents.cs:113-121), so it reaches the save.
        var current = installation.Access.GetLoginSettings("github", setup: false);
        Assert.True(
            current is not null && current.Revision == started.Revision,
            "the callback's own revision check already refuses this sign-in, so the test proves nothing");
        var signIns = new ExternalSignInService(
            installation.Access,
            installation.Database.Factory,
            installation.Sessions,
            installation.App.GetRequiredService<ISignalRNotificationService>());
        ExternalSignInResult? signIn = null;
        Exception? thrown = null;
        try
        {
            signIn = await signIns.TryCreateSessionAsync(
                current!,
                owner: fromEnvironment,
                GitHubIssuer,
                fromEnvironment ? OwnerSubject : "member-subject",
                new DefaultHttpContext { RequestServices = installation.App });
        }
        catch (ValidationException)
        {
            // The callbacks turn this refusal into the sign-in-expired redirect (OidcEvents.cs:141).
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        var wrong = new List<string>();
        if (thrown is not null)
            wrong.Add($"the callback's save threw {thrown.GetType().Name} ({thrown.Message})");
        if (signIn is not null)
            wrong.Add("the callback signed the browser in to an account");
        await using (var read = await installation.Database.Factory.CreateDbContextAsync())
        {
            var left = await read.UserAccounts.Select(account => new { account.Username, account.IsMainAdmin }).ToListAsync();
            if (left.Count > 0)
                wrong.Add($"{left.Count} account(s) exist with sign-in off ({string.Join(", ", left.Select(account => account.IsMainAdmin ? account.Username + " (main admin)" : account.Username))})");
        }
        Assert.True(wrong.Count == 0, $"with {login} returning after sign-in was turned off: " + string.Join("; ", wrong));
    }
}
