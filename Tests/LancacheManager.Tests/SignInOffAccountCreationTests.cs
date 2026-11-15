using LancacheManager.Controllers;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

/// <summary>
/// With sign-in off no account exists: every browser runs on the one shared session, and an account
/// made then could never be signed in to. So Create account on the Users page is refused with a reason
/// the page can name. An installation that already holds such an account, left by an older version, can
/// still be cleared from the shared session: the confirmed switch to unauthenticated mode deletes it,
/// though no account there is the main administrator.
/// </summary>
// The shared session is process-wide (SessionService.cs:45).
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class SignInOffAccountCreationTests
{
    [Fact]
    public async Task WithSignInOff_CreateAccountIsRefusedAndTheConfirmedSwitchDeletesALeftoverAccountAsync()
    {
        await using var installation = await SignInModeScene.CreateAsync(
            new Dictionary<string, string?>(),
            access =>
            {
                access.Mode = AccountMode.Unauthenticated;
                access.SetupVersion = AccessSettings.RequiredSetupVersion;
            });
        await installation.SeedAsync(SignInModeScene.Account(Guid.NewGuid(), "member", SessionType.User, mainAdmin: false));
        Assert.False(installation.Sessions.IsAuthenticationEnabled(), "the saved mode did not turn sign-in off");
        var shared = (await installation.Sessions.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext()))!.Value.Session;

        var wrong = new List<string>();

        // Create account on the Users page, from the shared session.
        var created = await installation.Controller<AccountsController>(shared).CreateAccountAsync(new CreateAccountRequest
        {
            Username = "newcomer",
            Password = "Newcomer-Passphrase-2026"
        });
        var refusal = created.Result as ObjectResult;
        await using (var read = await installation.Database.Factory.CreateDbContextAsync())
        {
            if (await read.UserAccounts.AnyAsync(account => account.Username == "newcomer"))
                wrong.Add($"Create account with sign-in off was answered {refusal?.StatusCode} and stored the account");
        }
        if (refusal is null or { StatusCode: < 400 })
            wrong.Add($"Create account with sign-in off was not refused (answered {refusal?.StatusCode})");
        else if (refusal.Value?.GetType().GetProperty("StageKey")?.GetValue(refusal.Value) is not string { Length: > 0 })
            wrong.Add($"the refusal of Create account carries no stage key the page can name ({refusal.Value?.GetType().Name})");

        // The confirmed switch to unauthenticated mode, from the shared session.
        var switched = await installation.Controller<AccessController>(shared).SetUpAsync(
            UnauthenticatedModeWipesAccountsTests.ConfirmingAccountWipe(new AccessSetupRequest
            {
                Mode = AccountMode.Unauthenticated,
                AcknowledgeUnauthenticated = true,
                ApiKey = installation.ApiKey.GetApiKey()
            }, accountCount: 1));
        if (switched.Result is not OkObjectResult)
            wrong.Add($"the confirmed switch to unauthenticated mode from the shared session was answered {(switched.Result as ObjectResult)?.StatusCode}");
        await using (var read = await installation.Database.Factory.CreateDbContextAsync())
        {
            var left = await read.UserAccounts.Select(account => account.Username).ToListAsync();
            if (left.Count > 0)
                wrong.Add($"{left.Count} accounts remain with sign-in off ({string.Join(", ", left)})");
        }

        Assert.True(wrong.Count == 0, "with sign-in off: " + string.Join("; ", wrong));
    }
}
